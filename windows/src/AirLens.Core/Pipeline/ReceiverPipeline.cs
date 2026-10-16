using System.Diagnostics;
using System.Runtime;
using AirLens.Core.Buffering;
using AirLens.Core.Decoding;
using AirLens.Core.Protocol;
using AirLens.Core.Transport;
using AirLens.Core.VirtualCamera;
using FFmpeg.AutoGen;

namespace AirLens.Core.Pipeline;

public sealed record ReceiverOptions
{
    /// <summary>Tried in order on every (re)connect; USB first is the low-latency default.</summary>
    public IReadOnlyList<StreamEndpoint> Endpoints { get; init; } = [StreamEndpoint.Usb()];
    public DecoderPreference Decoder { get; init; } = DecoderPreference.Auto;
    public bool EnableVirtualCamera { get; init; } = true;
    public string? FfmpegDirectory { get; init; }
}

public enum ReceiverState
{
    Connecting,
    Connected,
    Streaming,
    Reconnecting,
    Stopped,
}

public readonly record struct ReceiverStats(
    ReceiverState State,
    string Status,
    StreamEndpoint? Endpoint,
    StreamInfo? Stream,
    string Decoder,
    string? Gpu,
    double DecodedFps,
    double ReceivedMbps,
    double RttMs,
    double JitterMs,
    double HoldMs,
    double DecodeMs,
    double EndToEndMs,
    long QueueDrops,
    long Superseded,
    long DecodeErrors,
    long KeyframeRequests,
    int PoolOutstanding,
    int LiveLeases,
    long LeakedLeases,
    long VirtualCameraFrames,
    bool VirtualCameraConsumer,
    /// <summary>The phone's lenses and quality presets; null until a phone that sends them connects.</summary>
    DeviceState? Device);

/// <summary>
/// Owns the receive side end to end and all of its threads:
/// <code>
/// AirLens-Rx      StreamClient: socket → PacketBuffer (pooled) → CompressedFrameQueue
/// AirLens-Decode  queue → FfmpegHwDecoder (NVDEC) → PlayoutController → GpuFrameMailbox
/// AirLens-VCam    mailbox → GPU→CPU NV12 readback → shared memory (only while a camera client exists)
/// (renderer)      mailbox → GPU copy → swap chain, on the UI's own render thread
/// </code>
/// Each thread owns its objects; the hand-offs are the bounded queue and the one-slot mailbox,
/// so no thread can make another accumulate latency or memory.
///
/// GC: steady-state streaming allocates nothing, and the pipeline switches the process to
/// <see cref="GCLatencyMode.SustainedLowLatency"/> while it runs so a background gen-2 never
/// blocks the decoder.
/// </summary>
public sealed unsafe class ReceiverPipeline : IDisposable, IStreamClientListener, IDecodedFrameSink
{
    private readonly ReceiverOptions _options;
    private readonly Action<string> _log;
    private readonly PacketBufferPool _pool = new();
    private readonly CompressedFrameQueue _queue = new(QueueBudget.Usb);
    private readonly PlayoutController _playout = new(PlayoutMode.Realtime, 1000.0 / 30);
    private readonly StreamClient _client;
    private readonly Thread _decodeThread;
    private readonly Thread? _vcamThread;
    private readonly GCLatencyMode _previousGcMode;
    private volatile bool _stopping;

    private IReadOnlyList<GpuAdapterInfo> _adapters = [];
    private FfmpegHwDecoder? _decoder;
    private GpuAdapterInfo? _decoderAdapter;
    private object? _pendingInfo; // boxed StreamInfo, handed from Rx to the decoder thread
    private uint _decoderEpoch = uint.MaxValue;
    private long _currentArrivalTicks;
    private long _currentPtsUs;
    private AVFrame* _held;
    private FrameMeta _heldMeta;
    private long _heldShowAt;

    private QueueBudget _steadyBudget = QueueBudget.Usb;
    private volatile bool _awaitingFirstFrame = true;
    private volatile ReceiverState _state = ReceiverState.Connecting;
    private volatile string _status = "starting";
    private StreamInfo? _info;
    private double _rttMs, _decodeMs, _endToEndMs, _fps, _mbps;
    private long _keyframeRequests, _lastReportTicks, _framesAtReport, _bytesAtReport;
    private SharedMemoryFrameWriter? _vcam;
    private volatile bool _vcamConsumer;
    private volatile DeviceState? _device;
    private volatile VirtualCameraLook _look = VirtualCameraLook.Default;

    /// <summary>How the virtual camera frames the picture for apps: mirror, rotation, zoom, fill, hidden.</summary>
    public VirtualCameraLook VirtualCameraLook
    {
        get => _look;
        set => _look = value ?? VirtualCameraLook.Default;
    }

    public ReceiverPipeline(ReceiverOptions options, Action<string>? log = null)
    {
        _options = options;
        _log = log ?? (_ => { });
        _client = new StreamClient(options.Endpoints, _pool, this);
        _decodeThread = new Thread(DecodeLoop) { Name = "AirLens-Decode", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        if (options.EnableVirtualCamera)
            _vcamThread = new Thread(VirtualCameraLoop) { Name = "AirLens-VCam", IsBackground = true, Priority = ThreadPriority.BelowNormal };
        _previousGcMode = GCSettings.LatencyMode;
    }

    public GpuFrameMailbox Mailbox { get; } = new();

    /// <summary>Raised on the decoder thread when a (new) D3D11 device carries the frames.</summary>
    public event Action<Vortice.Direct3D11.ID3D11Device?>? DecoderDeviceChanged;

    public IReadOnlyList<GpuAdapterInfo> Adapters => _adapters;

    public void Start()
    {
        _log(FfmpegLoader.Load(_options.FfmpegDirectory, _log));
        _adapters = GpuSelector.Enumerate();
        foreach (var a in _adapters) _log($"GPU: {a} H.264={a.SupportsH264} HEVC={a.SupportsHevc}{(a.IsNvidia ? " [NVIDIA]" : "")}");
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        _lastReportTicks = Stopwatch.GetTimestamp();
        _decodeThread.Start();
        _vcamThread?.Start();
        _client.Start();
    }

    // ---- AirLens-Rx --------------------------------------------------------------------------

    void IStreamClientListener.OnConnected(StreamEndpoint endpoint)
    {
        bool usb = endpoint.Kind == EndpointKind.Usb;
        _steadyBudget = usb ? QueueBudget.Usb : QueueBudget.Wifi;
        EnterStartup();
        _playout.Mode = usb ? PlayoutMode.Realtime : PlayoutMode.Smoothed;
        _state = ReceiverState.Connected;
        _status = $"connected via {endpoint}";
        _log(_status);
        _client.SendControl(ClientPackets.BuildControl(virtualCamera: _vcamActive));
    }

    void IStreamClientListener.OnDisconnected(StreamEndpoint endpoint, string reason)
    {
        _device = null;
        _state = ReceiverState.Reconnecting;
        _status = $"disconnected ({reason}); reconnecting";
        _log(_status);
    }

    void IStreamClientListener.OnStreamInfo(StreamInfo info)
    {
        if (_info is not { } previous || previous.Epoch != info.Epoch)
        {
            _queue.Reset();
            EnterStartup();
        }
        _info = info;
        Volatile.Write(ref _pendingInfo, info);
        _log($"stream: {info}");
    }

    /// <summary>Generous queue budget until the first frame decodes; see <see cref="QueueBudget.Startup"/>.</summary>
    private void EnterStartup()
    {
        _awaitingFirstFrame = true;
        _queue.Budget = QueueBudget.Startup;
    }

    void IStreamClientListener.OnDeviceState(DeviceState state)
    {
        var previous = _device;
        _device = state;
        if (previous == null || previous.LensKey != state.LensKey || previous.Quality != state.Quality)
            _log($"phone: {state.Device}, lens {state.ActiveLens?.Label ?? state.LensKey}, quality {state.ActivePreset?.Title ?? state.Quality}");
    }

    /// <summary>New addresses to try, e.g. after discovery saw the phone's Wi-Fi address change.</summary>
    public void SetEndpoints(IReadOnlyList<StreamEndpoint> endpoints) => _client.SetEndpoints(endpoints);

    /// <summary>Asks the phone to switch lens. Lens changes are live; the next DEVICE_STATE confirms it.</summary>
    public void SetLens(string key) => _client.SendControl(ClientPackets.BuildControl(lens: key));

    /// <summary>Asks the phone for a quality preset. The phone restarts its encoder (a new epoch, ~1 s).</summary>
    public void SetQuality(string presetId) => _client.SendControl(ClientPackets.BuildControl(quality: presetId));

    /// <summary>Manual focus 0 (far) .. 1 (near), or null for autofocus. Live on the phone.</summary>
    public void SetFocus(double? value) => _client.SendControl(ClientPackets.BuildControl(focus: value, focusAuto: value == null));

    /// <summary>Turns autofocus off, holding focus where autofocus has it now.</summary>
    public void LockFocus() => _client.SendControl(ClientPackets.BuildControl(focusLock: true));

    /// <summary>
    /// Tells the phone whether the virtual camera is on, so it can skip drawing its own preview.
    /// Repeated on every (re)connect.
    /// </summary>
    public void SetVirtualCameraActive(bool on)
    {
        _vcamActive = on;
        _client.SendControl(ClientPackets.BuildControl(virtualCamera: on));
    }

    private volatile bool _vcamActive;

    void IStreamClientListener.OnFrame(PacketBuffer frame)
    {
        if (_queue.Enqueue(frame) == EnqueueResult.DroppedNeedKeyframe) RequestKeyframe();
    }

    void IStreamClientListener.OnRoundTrip(double rttMs) => _rttMs = _rttMs == 0 ? rttMs : _rttMs * 0.8 + rttMs * 0.2;

    void IStreamClientListener.OnStatus(string message)
    {
        _status = message;
        _log(message);
    }

    private void RequestKeyframe()
    {
        if (_client.RequestKeyframe()) Interlocked.Increment(ref _keyframeRequests);
    }

    // ---- AirLens-Decode ----------------------------------------------------------------------

    private void DecodeLoop()
    {
        try
        {
            while (!_stopping)
            {
                double waitMs = _held != null ? Math.Max(0, Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), _heldShowAt).TotalMilliseconds) : 50;
                if (_queue.TryDequeue(TimeSpan.FromMilliseconds(waitMs), out var packet) && packet != null)
                {
                    try
                    {
                        DecodeOne(packet);
                    }
                    finally
                    {
                        packet.Return();
                    }
                }
                ReleaseHeldIfDue();
                ReportIfDue();
            }
        }
        catch (Exception e)
        {
            // Last line of defence: log and leave the thread; Dispose still cleans up.
            _log($"decoder thread crashed: {e}");
            _status = $"decoder crashed: {e.Message}";
        }
    }

    private void DecodeOne(PacketBuffer packet)
    {
        if (packet.Epoch != _decoderEpoch || _decoder == null)
        {
            if (!ApplyStreamInfo(packet.Epoch)) return;
        }
        _currentArrivalTicks = packet.ArrivalTicks;
        _currentPtsUs = packet.PtsUs;
        long t0 = Stopwatch.GetTimestamp();
        var result = _decoder!.Decode(packet);
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (!packet.IsConfig) _decodeMs = _decodeMs == 0 ? ms : _decodeMs * 0.9 + ms * 0.1;
        if (result == DecodeResult.NeedKeyframe)
        {
            // Queued deltas depend on the broken reference chain; a queued keyframe repairs it.
            _queue.DropUntilKeyframe();
            RequestKeyframe();
        }
    }

    private bool ApplyStreamInfo(uint epoch)
    {
        if (Volatile.Read(ref _pendingInfo) is not StreamInfo info || info.Epoch != epoch) return false;
        var adapter = GpuSelector.Choose(_adapters, info.Codec);
        if (_decoder == null || !Equals(adapter, _decoderAdapter))
        {
            ReleaseHeld();
            _decoder?.Dispose();
            _decoder = new FfmpegHwDecoder(this, _options.Decoder, adapter, _log);
            _decoderAdapter = adapter;
        }
        try
        {
            var deviceBefore = _decoder.D3D11Device;
            _decoder.Open(info);
            if (!ReferenceEquals(deviceBefore, _decoder.D3D11Device) || deviceBefore == null)
                DecoderDeviceChanged?.Invoke(_decoder.D3D11Device);
        }
        catch (Exception e)
        {
            _status = $"decoder open failed: {e.Message}";
            _log(_status);
            return false;
        }
        _playout.Reset();
        _playout.FrameIntervalMs = 1000.0 / Math.Max(1, info.Fps);
        _decoderEpoch = info.Epoch;
        _vcam?.AnnounceFormat(info.Width, info.Height, info.Fps);
        return true;
    }

    void IDecodedFrameSink.OnDecodedFrame(AVFrame* frame, in FrameMeta meta)
    {
        _state = ReceiverState.Streaming;
        if (_awaitingFirstFrame)
        {
            _awaitingFirstFrame = false;
            _queue.Budget = _steadyBudget; // decoder is warm: back to the strict latency cap
        }
        long now = Stopwatch.GetTimestamp();
        long showAt = _playout.ScheduleTicks(_currentPtsUs, _currentArrivalTicks);
        _endToEndMs = Stopwatch.GetElapsedTime(_currentArrivalTicks, now).TotalMilliseconds;
        if (showAt <= now)
        {
            ReleaseHeld();
            Mailbox.Publish(frame, meta);
            return;
        }
        // Smoothed (Wi-Fi) playout: hold this frame until its slot. A newer frame replaces an
        // older held one, so holding can never build a queue.
        ReleaseHeld();
        _held = ffmpeg.av_frame_clone(frame);
        _heldMeta = meta;
        _heldShowAt = showAt;
    }

    private void ReleaseHeldIfDue()
    {
        if (_held == null || Stopwatch.GetTimestamp() < _heldShowAt) return;
        Mailbox.Publish(_held, _heldMeta);
        ReleaseHeld();
    }

    private void ReleaseHeld()
    {
        if (_held == null) return;
        var held = _held;
        ffmpeg.av_frame_free(&held);
        _held = null;
    }

    private void ReportIfDue()
    {
        long now = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(_lastReportTicks, now);
        if (elapsed < TimeSpan.FromSeconds(1)) return;
        long frames = _decoder?.FramesDecoded ?? 0;
        long bytes = _client.BytesReceived;
        _fps = (frames - _framesAtReport) / elapsed.TotalSeconds;
        _mbps = (bytes - _bytesAtReport) * 8 / elapsed.TotalSeconds / 1e6;
        _framesAtReport = frames;
        _bytesAtReport = bytes;
        _lastReportTicks = now;
        long dropped = _queue.DroppedFrames + (_decoder?.DecodeErrors ?? 0);
        _client.SendReceiverReport((uint)frames, (uint)dropped, (ushort)Math.Min(ushort.MaxValue, _queue.BacklogMs));
    }

    // ---- AirLens-VCam ------------------------------------------------------------------------

    private void VirtualCameraLoop()
    {
        using var writer = new SharedMemoryFrameWriter();
        _vcam = writer;
        AVFrame* nv12 = ffmpeg.av_frame_alloc();
        long lastVersion = 0;
        string? lastError = null;
        VirtualCameraGpuStage? gpu = null;
        nint gpuTexture = 0;
        bool gpuFailed = false;
        try
        {
            while (!_stopping)
            {
                if (!writer.IsOpen && !writer.TryOpen())
                {
                    if (writer.LastError != lastError) _log($"virtual camera feed: {lastError = writer.LastError}");
                    Thread.Sleep(1000);
                    continue;
                }
                if (lastError != null) _log("virtual camera feed: connected to the camera's shared memory");
                lastError = null;
                if (_info is { } info) writer.AnnounceFormat(info.Width, info.Height, info.Fps);
                _vcamConsumer = writer.ConsumerActive;
                if (!_vcamConsumer)
                {
                    Thread.Sleep(250);
                    continue;
                }
                if (!Mailbox.WaitForNewFrame(lastVersion, 100) || !Mailbox.TryAcquire(ref lastVersion, out var lease) || lease == null)
                    continue;
                var look = _look;
                var (reqW, reqH) = writer.RequestedSize;
                int dstW = reqW > 0 ? reqW : DefaultCameraWidth, dstH = reqH > 0 ? reqH : DefaultCameraHeight;
                if (look.Hidden)
                {
                    lease.Dispose();
                    writer.WriteBlack(dstW, dstH);
                    continue;
                }
                AVFrame* source = null;
                bool submitted = false;
                using (lease)
                {
                    if (lease.IsD3D11)
                    {
                        nint texture = lease.Texture;
                        if (texture != gpuTexture)
                        {
                            // A new surface pool, maybe on a new device: rebuild the GPU stage for it.
                            gpuTexture = texture;
                            nint device = DeviceOf(texture);
                            if (gpu == null || gpu.DevicePointer != device)
                            {
                                gpu?.Dispose();
                                gpu = null;
                                gpuFailed = false;
                                try
                                {
                                    gpu = new VirtualCameraGpuStage(device);
                                }
                                catch (Exception e)
                                {
                                    gpuFailed = true;
                                    _log($"virtual camera: no GPU scaling ({e.Message}); copying full frames instead");
                                }
                            }
                        }
                        if (gpu != null && !gpuFailed)
                        {
                            try
                            {
                                var stream = _info;
                                gpu.Submit(texture, lease.ArraySlice, lease.Meta.Width, lease.Meta.Height, dstW, dstH,
                                    (stream?.RotationDegrees ?? 0) + look.RotationDegrees,
                                    (stream?.MirrorRequired ?? false) ^ look.Mirror,
                                    look, stream?.FullRange ?? false);
                                submitted = true;
                            }
                            catch (Exception e)
                            {
                                gpuFailed = true;
                                _log($"virtual camera: GPU scaling failed ({e.Message}); copying full frames instead");
                            }
                        }
                    }
                    if (!submitted) source = Download(lease.Frame, nv12);
                }
                if (submitted) gpu!.Publish(writer);
                else if (source != null) writer.Write(source);
            }
        }
        catch (Exception e)
        {
            _log($"virtual camera thread stopped: {e.Message}");
        }
        finally
        {
            _vcam = null;
            gpu?.Dispose();
            ffmpeg.av_frame_free(&nv12);
        }
    }

    /// <summary>Size the camera app gets when it has not picked one (the source advertises 1080p).</summary>
    private const int DefaultCameraWidth = 1920;
    private const int DefaultCameraHeight = 1080;

    private static nint DeviceOf(nint texture)
    {
        using var t = new Vortice.Direct3D11.ID3D11Texture2D(texture);
        t.AddRef(); // balanced by the using; the decoder keeps its own reference
        using var device = t.Device;
        return device.NativePointer;
    }

    /// <summary>
    /// GPU → CPU NV12 into a reused frame (one staging copy, done by FFmpeg under the device lock).
    /// The lease is released by the caller right after this returns, so the decoder's surface pool
    /// is pinned for the copy only.
    /// </summary>
    private static AVFrame* Download(AVFrame* src, AVFrame* nv12)
    {
        if (src->hw_frames_ctx == null)
            return src->format == (int)AVPixelFormat.AV_PIX_FMT_NV12 ? src : null; // software YUV420P: not wired in v1
        if (nv12->width != src->width || nv12->height != src->height || nv12->buf[0] == null)
        {
            ffmpeg.av_frame_unref(nv12);
            nv12->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
            nv12->width = src->width;
            nv12->height = src->height;
            if (ffmpeg.av_frame_get_buffer(nv12, 0) < 0) return null;
        }
        return ffmpeg.av_hwframe_transfer_data(nv12, src, 0) < 0 ? null : nv12;
    }

    // ---- stats & lifetime --------------------------------------------------------------------

    public ReceiverStats GetStats() => new(
        _state,
        _status,
        _client.ConnectedEndpoint,
        _info,
        _decoder?.Description ?? "not open",
        _decoderAdapter?.Description,
        _fps,
        _mbps,
        _rttMs,
        _playout.JitterMs,
        _playout.TargetHoldMs,
        _decodeMs,
        _endToEndMs,
        _queue.DroppedFrames,
        Mailbox.Superseded,
        _decoder?.DecodeErrors ?? 0,
        Interlocked.Read(ref _keyframeRequests),
        _pool.Outstanding,
        Mailbox.LiveLeases,
        Mailbox.LeakedLeases,
        _vcam?.FramesWritten ?? 0,
        _vcamConsumer,
        _device);

    public void Dispose()
    {
        if (_stopping) return;
        _stopping = true;
        _client.Dispose();                // no more frames arrive
        _queue.Close();                   // wakes the decoder thread, returns queued buffers
        // Start may have thrown (no FFmpeg, no GPU) before the threads ran; Join would throw too.
        if (_decodeThread.ThreadState != System.Threading.ThreadState.Unstarted) _decodeThread.Join(TimeSpan.FromSeconds(3));
        if (_vcamThread is { ThreadState: not System.Threading.ThreadState.Unstarted }) _vcamThread.Join(TimeSpan.FromSeconds(3));
        ReleaseHeld();
        _decoder?.Dispose();
        _decoder = null;
        Mailbox.Dispose();
        _state = ReceiverState.Stopped;
        GCSettings.LatencyMode = _previousGcMode;
    }
}

using System.Diagnostics;
using AirLens.Core.Buffering;
using AirLens.Core.Protocol;
using FFmpeg.AutoGen;
using D3D11Device = Vortice.Direct3D11.ID3D11Device;

namespace AirLens.Core.Decoding;

public enum DecoderPreference
{
    /// <summary>D3D11VA on the preferred adapter, then CUDA, then software.</summary>
    Auto,
    D3D11VA,
    Cuda,
    Software,
}

public enum DecodeResult
{
    Ok,

    /// <summary>The bitstream is broken or a reference is missing: request an IDR.</summary>
    NeedKeyframe,
}

/// <summary>Receives each decoded frame on the decoder thread. The frame is only valid during the call.</summary>
public unsafe interface IDecodedFrameSink
{
    void OnDecodedFrame(AVFrame* frame, in FrameMeta meta);
}

public interface IVideoDecoder : IDisposable
{
    DecoderBackend Backend { get; }
    string Description { get; }
    long FramesDecoded { get; }
    long DecodeErrors { get; }

    /// <summary>Opens for <paramref name="info"/>; a same-codec, same-size reopen is just a flush.</summary>
    void Open(StreamInfo info);

    /// <summary>Decodes one access unit; frames go to the sink. Does not take ownership of the packet.</summary>
    DecodeResult Decode(PacketBuffer packet);

    void Flush();
}

/// <summary>
/// FFmpeg's native h264/hevc decoders with a hardware accelerator, not the <c>*_cuvid</c> wrappers:
/// hwaccel decoders return a frame as soon as it is decoded (CUVID keeps its own surface queue) and
/// can output D3D11 textures directly.
///
/// <b>Backends.</b> D3D11VA on the adapter chosen by <see cref="GpuSelector"/> is the default: on an
/// NVIDIA GPU this is the NVDEC engine, and the frames are <c>ID3D11Texture2D</c>s the renderer can
/// copy GPU-to-GPU (zero CPU). CUDA (<c>AV_HWDEVICE_TYPE_CUDA</c>, also NVDEC) is available for
/// pipelines that want CUDA memory. Software is the last resort.
///
/// <b>Latency settings.</b> <c>AV_CODEC_FLAG_LOW_DELAY</c>; one thread with hwaccel (frame
/// threading adds a frame of delay per thread); slice threading only for software; corrupt frames
/// are not output (the caller requests an IDR instead).
///
/// <b>Memory.</b> One AVPacket and one AVFrame for the decoder's lifetime, unref'd after every use.
/// Packets are non-refcounted views over the pinned pooled buffer: <c>avcodec_send_packet</c> copies
/// into its own padded buffer, so the pin ends when the call returns and FFmpeg never holds a
/// pointer into managed memory. The <c>get_format</c> delegate is a field so the GC cannot collect
/// it while native code holds the pointer. Native objects are freed in <see cref="Dispose"/> and,
/// as a safety net, the finalizer.
/// </summary>
public sealed unsafe class FfmpegHwDecoder : IVideoDecoder
{
    private const int ExtraHwFrames = 4; // mailbox slot + one lease per consumer + one in flight

    private readonly IDecodedFrameSink _sink;
    private readonly DecoderPreference _preference;
    private readonly GpuAdapterInfo? _adapter;
    private readonly Action<string> _log;
    private readonly AVCodecContext_get_format _getFormat;

    private AVCodecContext* _ctx;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private AVBufferRef* _hwDevice;
    private AVPixelFormat _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    private AVHWDeviceType _hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
    private D3D11Device? _d3d;
    private StreamInfo? _openFor;
    private byte[] _config = new byte[256];
    private int _configLength;
    private byte[] _joined = [];
    private uint _epoch;
    private int _consecutiveErrors;
    private bool _disposed;

    public FfmpegHwDecoder(IDecodedFrameSink sink, DecoderPreference preference, GpuAdapterInfo? adapter, Action<string>? log = null)
    {
        _sink = sink;
        _preference = preference;
        _adapter = adapter;
        _log = log ?? (_ => { });
        _getFormat = GetFormat;
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        if (_packet == null || _frame == null) throw new OutOfMemoryException("av_packet_alloc/av_frame_alloc");
    }

    public DecoderBackend Backend { get; private set; } = DecoderBackend.None;
    public string Description { get; private set; } = "closed";
    public long FramesDecoded { get; private set; }
    public long DecodeErrors { get; private set; }

    /// <summary>The device the D3D11 frames live on. The renderer must create its swap chain on it.</summary>
    public D3D11Device? D3D11Device => _d3d;

    public void Open(StreamInfo info)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _epoch = info.Epoch;
        if (_ctx != null && _openFor is { } open && open.Codec == info.Codec && open.Width == info.Width && open.Height == info.Height)
        {
            Flush();
            _openFor = info;
            return;
        }
        CloseCodec();

        var codecId = info.Codec == VideoCodec.Hevc ? AVCodecID.AV_CODEC_ID_HEVC : AVCodecID.AV_CODEC_ID_H264;
        var codec = ffmpeg.avcodec_find_decoder(codecId);
        if (codec == null) throw new InvalidOperationException($"FFmpeg build lacks a {codecId} decoder");

        if (_hwDevice == null) CreateHardwareDevice(info.Codec);

        _ctx = ffmpeg.avcodec_alloc_context3(codec);
        if (_ctx == null) throw new OutOfMemoryException("avcodec_alloc_context3");
        _ctx->flags |= ffmpeg.AV_CODEC_FLAG_LOW_DELAY;
        _ctx->flags &= ~ffmpeg.AV_CODEC_FLAG_OUTPUT_CORRUPT;
        _ctx->pkt_timebase = new AVRational { num = 1, den = 1_000_000 };
        if (_hwDevice != null)
        {
            _ctx->hw_device_ctx = ffmpeg.av_buffer_ref(_hwDevice);
            _ctx->get_format = _getFormat;
            _ctx->extra_hw_frames = ExtraHwFrames;
            _ctx->thread_count = 1;
        }
        else
        {
            _ctx->thread_type = ffmpeg.FF_THREAD_SLICE;
            _ctx->thread_count = 0; // auto
        }

        int ret = ffmpeg.avcodec_open2(_ctx, codec, null);
        if (ret < 0)
        {
            CloseCodec();
            throw new InvalidOperationException($"avcodec_open2: {FfmpegLoader.ErrorText(ret)}");
        }
        _openFor = info;
        Backend = _hwType switch
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => DecoderBackend.D3D11VA,
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => DecoderBackend.Cuda,
            _ => DecoderBackend.Software,
        };
        Description = $"{Backend} {(info.Codec == VideoCodec.Hevc ? "HEVC" : "H.264")} {info.Width}x{info.Height}" +
                      (_adapter != null && Backend != DecoderBackend.Software ? $" on {_adapter.Description}" : "");
        _log($"decoder open: {Description}");
    }

    private void CreateHardwareDevice(VideoCodec codec)
    {
        var order = _preference switch
        {
            DecoderPreference.D3D11VA => new[] { AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA },
            DecoderPreference.Cuda => [AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA],
            DecoderPreference.Software => [],
            _ => [AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA],
        };
        foreach (var type in order)
        {
            try
            {
                if (type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
                {
                    if (_adapter == null || !_adapter.Supports(codec)) continue;
                    CreateD3D11Device(_adapter);
                    _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_D3D11;
                }
                else
                {
                    AVBufferRef* device = null;
                    // CUDA device 0 is the first NVIDIA GPU; on hybrid laptops the iGPU is never a CUDA device.
                    int ret = ffmpeg.av_hwdevice_ctx_create(&device, type, "0", null, 0);
                    if (ret < 0) throw new InvalidOperationException(FfmpegLoader.ErrorText(ret));
                    _hwDevice = device;
                    _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_CUDA;
                }
                _hwType = type;
                return;
            }
            catch (Exception e)
            {
                _log($"{type} unavailable: {e.Message}");
                ReleaseHardwareDevice();
            }
        }
        _log("no hardware decoder available; using software decoding");
    }

    /// <summary>
    /// Hands FFmpeg our own device instead of letting it create one, so we choose the adapter and
    /// the renderer can share it. FFmpeg releases the references it holds on uninit; the AddRef
    /// below is the one it will release.
    /// </summary>
    private void CreateD3D11Device(GpuAdapterInfo adapter)
    {
        _d3d = GpuSelector.CreateDevice(adapter);
        var device = ffmpeg.av_hwdevice_ctx_alloc(AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA);
        if (device == null) throw new OutOfMemoryException("av_hwdevice_ctx_alloc");
        var hw = (AVD3D11VADeviceContext*)((AVHWDeviceContext*)device->data)->hwctx;
        _d3d.AddRef();
        hw->device = (FFmpeg.AutoGen.ID3D11Device*)_d3d.NativePointer;
        int ret = ffmpeg.av_hwdevice_ctx_init(device);
        if (ret < 0)
        {
            ffmpeg.av_buffer_unref(&device);
            throw new InvalidOperationException($"av_hwdevice_ctx_init: {FfmpegLoader.ErrorText(ret)}");
        }
        _hwDevice = device;
    }

    private AVPixelFormat GetFormat(AVCodecContext* ctx, AVPixelFormat* formats)
    {
        for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            if (*p == _hwPixelFormat) return *p;
        }
        // The hwaccel refused this stream (e.g. an unsupported profile): FFmpeg asks again without
        // the hardware format. Take the first software format.
        for (var p = formats; *p != AVPixelFormat.AV_PIX_FMT_NONE; p++)
        {
            var desc = ffmpeg.av_pix_fmt_desc_get(*p);
            if (desc != null && (desc->flags & ffmpeg.AV_PIX_FMT_FLAG_HWACCEL) == 0)
            {
                Backend = DecoderBackend.Software;
                _log($"hardware decoding refused this stream; falling back to {ffmpeg.av_get_pix_fmt_name(*p)}");
                return *p;
            }
        }
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public DecodeResult Decode(PacketBuffer packet)
    {
        if (_ctx == null) return DecodeResult.NeedKeyframe;
        if (packet.IsConfig)
        {
            // FFmpeg's h264/hevc decoders reject a packet that holds only parameter sets
            // ("no frame!", AVERROR_INVALIDDATA). Keep them and prepend them to the next keyframe.
            Grow(ref _config, packet.Length);
            packet.Payload.CopyTo(_config);
            _configLength = packet.Length;
            return DecodeResult.Ok;
        }
        if (packet.IsKeyframe && _configLength > 0)
        {
            int total = _configLength + packet.Length;
            Grow(ref _joined, total);
            _config.AsSpan(0, _configLength).CopyTo(_joined);
            packet.Payload.CopyTo(_joined.AsSpan(_configLength));
            _configLength = 0; // the decoder keeps SPS/PPS from here on
            return Send(_joined, total, packet.PtsUs, keyframe: true);
        }
        return Send(packet.Data, packet.Length, packet.PtsUs, packet.IsKeyframe);
    }

    /// <summary>Grows to the next power of two, keeping the array for the stream's lifetime.</summary>
    private static void Grow(ref byte[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new byte[(int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)length)];
    }

    private DecodeResult Send(byte[] buffer, int length, long ptsUs, bool keyframe)
    {
        int ret;
        fixed (byte* data = buffer)
        {
            _packet->data = data;
            _packet->size = length;
            _packet->pts = ptsUs;
            _packet->dts = ptsUs;
            _packet->flags = keyframe ? ffmpeg.AV_PKT_FLAG_KEY : 0;
            ret = ffmpeg.avcodec_send_packet(_ctx, _packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
                // Output is full; drain, then the packet fits. Never happens in steady low-delay operation.
                Drain();
                ret = ffmpeg.avcodec_send_packet(_ctx, _packet);
            }
            // Never let FFmpeg see a pointer into managed memory after the pin ends.
            _packet->data = null;
            _packet->size = 0;
        }
        if (ret < 0 && ret != ffmpeg.AVERROR_EOF) return OnError("send_packet", ret);
        return Drain();
    }

    private DecodeResult Drain()
    {
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_frame(_ctx, _frame);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF) return DecodeResult.Ok;
            if (ret < 0) return OnError("receive_frame", ret);

            FramesDecoded++;
            _consecutiveErrors = 0;
            var backend = _frame->format switch
            {
                (int)AVPixelFormat.AV_PIX_FMT_D3D11 => DecoderBackend.D3D11VA,
                (int)AVPixelFormat.AV_PIX_FMT_CUDA => DecoderBackend.Cuda,
                _ => DecoderBackend.Software,
            };
            var meta = new FrameMeta(_frame->pts, _frame->width, _frame->height, backend, _epoch, Stopwatch.GetTimestamp());
            try
            {
                _sink.OnDecodedFrame(_frame, meta);
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private DecodeResult OnError(string where, int ret)
    {
        DecodeErrors++;
        if (++_consecutiveErrors >= 3)
        {
            // A wedged decoder (rare, driver-specific) recovers with a flush plus a fresh IDR.
            ffmpeg.avcodec_flush_buffers(_ctx);
            _consecutiveErrors = 0;
        }
        _log($"{where}: {FfmpegLoader.ErrorText(ret)}");
        return DecodeResult.NeedKeyframe;
    }

    public void Flush()
    {
        if (_ctx != null) ffmpeg.avcodec_flush_buffers(_ctx);
        _consecutiveErrors = 0;
        _configLength = 0; // a new epoch always brings its own parameter sets
    }

    private void CloseCodec()
    {
        if (_ctx != null)
        {
            var ctx = _ctx;
            ffmpeg.avcodec_free_context(&ctx); // also unrefs ctx->hw_device_ctx
            _ctx = null;
        }
        _openFor = null;
        _configLength = 0;
        Description = "closed";
    }

    private void ReleaseHardwareDevice()
    {
        if (_hwDevice != null)
        {
            var device = _hwDevice;
            ffmpeg.av_buffer_unref(&device);
            _hwDevice = null;
        }
        _d3d?.Dispose();
        _d3d = null;
        _hwType = AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;
        _hwPixelFormat = AVPixelFormat.AV_PIX_FMT_NONE;
    }

    public void Dispose()
    {
        DisposeNative();
        GC.SuppressFinalize(this);
    }

    ~FfmpegHwDecoder() => DisposeNative();

    private void DisposeNative()
    {
        if (_disposed) return;
        _disposed = true;
        CloseCodec();
        ReleaseHardwareDevice();
        if (_packet != null)
        {
            var packet = _packet;
            ffmpeg.av_packet_free(&packet);
            _packet = null;
        }
        if (_frame != null)
        {
            var frame = _frame;
            ffmpeg.av_frame_free(&frame);
            _frame = null;
        }
    }
}

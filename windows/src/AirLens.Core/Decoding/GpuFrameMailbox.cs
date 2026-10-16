using System.Diagnostics;
using FFmpeg.AutoGen;

namespace AirLens.Core.Decoding;

public enum DecoderBackend
{
    None,
    D3D11VA,
    Cuda,
    Software,
}

/// <summary>Per-frame facts that consumers need without touching the AVFrame.</summary>
public readonly record struct FrameMeta(long PtsUs, int Width, int Height, DecoderBackend Backend, uint Epoch, long DecodedTicks);

/// <summary>
/// The hand-off from the decoder to its consumers (preview renderer, virtual camera): a single slot
/// that always holds the newest decoded frame. "Latest frame wins" is what keeps presentation from
/// ever queueing latency: a frame nobody took before the next one arrived is simply superseded
/// (counted in <see cref="Superseded"/>; it was decoded, so no reference chain is harmed).
///
/// Memory/GPU-pool discipline: the slot owns one AVFrame reference; every <see cref="TryAcquire"/>
/// hands out another (<see cref="FrameLease"/>). With D3D11VA the references pin surfaces of the
/// decoder's fixed texture pool, so consumers must dispose leases right after their GPU copy.
/// The decoder is opened with <c>extra_hw_frames</c> covering the slot plus one lease per consumer.
/// </summary>
public sealed unsafe class GpuFrameMailbox : IDisposable
{
    private readonly object _gate = new();
    private AVFrame* _latest;
    private FrameMeta _latestMeta;
    private long _version;
    private bool _latestTaken = true;
    private int _liveLeases;
    private long _superseded;
    private bool _disposed;

    public long Version
    {
        get { lock (_gate) return _version; }
    }

    public long Superseded => Interlocked.Read(ref _superseded);
    public int LiveLeases => Volatile.Read(ref _liveLeases);
    public long LeakedLeases => FrameLease.Leaked;

    /// <summary>Called on the decoder thread. Takes a new reference; the caller keeps its own.</summary>
    public void Publish(AVFrame* frame, in FrameMeta meta)
    {
        var clone = ffmpeg.av_frame_clone(frame);
        if (clone == null) return; // out of memory: skip one frame rather than crash
        AVFrame* old;
        lock (_gate)
        {
            if (_disposed)
            {
                ffmpeg.av_frame_free(&clone);
                return;
            }
            old = _latest;
            if (old != null && !_latestTaken) _superseded++;
            _latest = clone;
            _latestMeta = meta;
            _latestTaken = false;
            _version++;
            Monitor.PulseAll(_gate);
        }
        if (old != null) ffmpeg.av_frame_free(&old);
    }

    /// <summary>
    /// Returns a lease on the newest frame if it is newer than <paramref name="lastSeenVersion"/>.
    /// Dispose the lease as soon as the frame has been copied.
    /// </summary>
    public bool TryAcquire(ref long lastSeenVersion, out FrameLease? lease)
    {
        lock (_gate)
        {
            if (_disposed || _latest == null || _version == lastSeenVersion)
            {
                lease = null;
                return false;
            }
            var reference = ffmpeg.av_frame_clone(_latest);
            if (reference == null)
            {
                lease = null;
                return false;
            }
            lastSeenVersion = _version;
            _latestTaken = true;
            _liveLeases++;
            lease = new FrameLease(this, reference, _latestMeta);
            return true;
        }
    }

    /// <summary>Blocks until a frame newer than <paramref name="lastSeenVersion"/> exists, or the timeout.</summary>
    public bool WaitForNewFrame(long lastSeenVersion, int timeoutMs)
    {
        lock (_gate)
        {
            if (_version != lastSeenVersion) return true;
            Monitor.Wait(_gate, timeoutMs);
            return _version != lastSeenVersion;
        }
    }

    internal void Release()
    {
        Interlocked.Decrement(ref _liveLeases);
    }

    public void Dispose()
    {
        AVFrame* latest;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            latest = _latest;
            _latest = null;
            Monitor.PulseAll(_gate);
        }
        if (latest != null) ffmpeg.av_frame_free(&latest);
    }
}

/// <summary>
/// One reference to a decoded frame. For D3D11VA frames, <see cref="Texture"/> is an
/// <c>ID3D11Texture2D*</c> texture array and <see cref="ArraySlice"/> the index of this frame in it
/// (libavutil/hwcontext_d3d11va.h). The finalizer is a safety net only; it is counted in
/// <see cref="Leaked"/> so a missing <c>Dispose</c> shows up in diagnostics instead of starving
/// the decoder's surface pool silently.
/// </summary>
public sealed unsafe class FrameLease : IDisposable
{
    private static long _leaked;
    private readonly GpuFrameMailbox _owner;
    private nint _frame; // AVFrame*, swapped atomically so Dispose and the finalizer cannot double-free

    internal FrameLease(GpuFrameMailbox owner, AVFrame* frame, FrameMeta meta)
    {
        _owner = owner;
        _frame = (nint)frame;
        Meta = meta;
    }

    public static long Leaked => Interlocked.Read(ref _leaked);
    public FrameMeta Meta { get; }
    public AVFrame* Frame => (AVFrame*)Volatile.Read(ref _frame);
    public bool IsD3D11 => Frame != null && Frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11;
    public bool IsCuda => Frame != null && Frame->format == (int)AVPixelFormat.AV_PIX_FMT_CUDA;
    public nint Texture => IsD3D11 ? (nint)Frame->data[0] : 0;
    public int ArraySlice => IsD3D11 ? (int)(nint)Frame->data[1] : 0;
    public TimeSpan Age => Stopwatch.GetElapsedTime(Meta.DecodedTicks);

    public void Dispose()
    {
        ReleaseFrame();
        GC.SuppressFinalize(this);
    }

    ~FrameLease()
    {
        Interlocked.Increment(ref _leaked);
        ReleaseFrame();
    }

    private void ReleaseFrame()
    {
        var frame = (AVFrame*)Interlocked.Exchange(ref _frame, 0);
        if (frame == null) return;
        ffmpeg.av_frame_free(&frame);
        _owner.Release();
    }
}

using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using FFmpeg.AutoGen;
using static AirLens.Core.VirtualCamera.SharedFrameLayout;

namespace AirLens.Core.VirtualCamera;

/// <summary>
/// Producer side of the virtual-camera shared memory (see AirLensSharedFrame.h for the protocol).
/// Opens the section the media source created; never creates it, because a standard user cannot
/// create Global\ objects. The view is mapped once per consumer session and unmapped as soon as the
/// consumer's heartbeat stops, so an idle virtual camera costs no memory and no copies.
/// </summary>
public sealed unsafe class SharedMemoryFrameWriter : IDisposable
{
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(2);
    private MemoryMappedFile? _mapping;
    private MemoryMappedViewAccessor? _view;
    private EventWaitHandle? _event;
    private byte* _base;
    private long _sequence;

    public bool IsOpen => _base != null;

    /// <summary>Why the last <see cref="TryOpen"/> failed (null when open or never tried).</summary>
    public string? LastError { get; private set; }
    public long FramesWritten { get; private set; }

    /// <summary>Opens the mapping if a consumer has created it. Cheap; call about once a second.</summary>
    public bool TryOpen()
    {
        if (IsOpen) return true;
        try
        {
            _mapping = MemoryMappedFile.OpenExisting(MappingName, MemoryMappedFileRights.ReadWrite);
            _view = _mapping.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.ReadWrite);
            // The frame-ready event only wakes the consumer early; it polls with a one-frame timeout
            // anyway, so a missing or inaccessible event must not stop frames from flowing.
            try
            {
                EventWaitHandle.TryOpenExisting(EventName, out _event);
            }
            catch (UnauthorizedAccessException)
            {
                _event = null;
            }
            byte* p = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
            _base = p + _view.PointerOffset;
            if (*(uint*)(_base + OffMagic) != Magic || *(uint*)(_base + OffVersion) != SharedFrameLayout.Version)
            {
                LastError = "shared memory has an unexpected layout (component version mismatch?)";
                Close();
                return false;
            }
            _sequence = Volatile.Read(ref *(long*)(_base + OffWriteSequence));
            LastError = null;
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or UnauthorizedAccessException or IOException)
        {
            LastError = e is FileNotFoundException ? "no camera client yet" : $"{e.GetType().Name}: {e.Message}";
            Close();
            return false;
        }
    }

    /// <summary>True while a camera client has refreshed its heartbeat recently.</summary>
    public bool ConsumerActive
    {
        get
        {
            if (!IsOpen) return false;
            long heartbeat = Volatile.Read(ref *(long*)(_base + OffConsumerHeartbeat));
            if (heartbeat <= 0) return false;
            var age = DateTime.UtcNow - DateTime.FromFileTimeUtc(heartbeat);
            return age < HeartbeatTimeout;
        }
    }

    public void AnnounceFormat(int width, int height, int fps)
    {
        if (!IsOpen) return;
        *(uint*)(_base + OffProducerWidth) = (uint)width;
        *(uint*)(_base + OffProducerHeight) = (uint)height;
        *(uint*)(_base + OffProducerFpsNum) = (uint)fps;
        *(uint*)(_base + OffProducerFpsDen) = 1;
    }

    /// <summary>The size the camera app selected (its media type), or (0, 0) if it has not said.</summary>
    public (int Width, int Height) RequestedSize => IsOpen
        ? ((int)*(uint*)(_base + OffRequestedWidth), (int)*(uint*)(_base + OffRequestedHeight))
        : (0, 0);

    /// <summary>Copies one NV12 system-memory frame into the next slot and publishes it.</summary>
    public bool Write(AVFrame* nv12)
    {
        if (!IsOpen || nv12->format != (int)AVPixelFormat.AV_PIX_FMT_NV12) return false;
        return WriteNv12(nv12->data[0], nv12->linesize[0], nv12->data[1], nv12->linesize[1], nv12->width, nv12->height);
    }

    /// <summary>Publishes a black frame (the "Hide video" switch).</summary>
    public bool WriteBlack(int w, int h)
    {
        if (!IsOpen || w <= 0 || h <= 0 || w > MaxWidth || h > MaxHeight) return false;
        long n = BeginSlot(out byte* header, out byte* data);
        new Span<byte>(data, w * h).Fill(16);
        new Span<byte>(data + (long)w * h, w * h / 2).Fill(128);
        EndSlot(n, header, w, h, w, w);
        return true;
    }

    /// <summary>Copies NV12 planes (any row pitch) into the next slot and publishes them.</summary>
    public bool WriteNv12(byte* y, int pitchY, byte* uvPlane, int pitchUV, int w, int h)
    {
        if (!IsOpen || w <= 0 || h <= 0 || w > MaxWidth || h > MaxHeight) return false;
        long n = BeginSlot(out byte* header, out byte* data);
        for (int row = 0; row < h; row++) Buffer.MemoryCopy(y + (long)row * pitchY, data + (long)row * w, w, w);
        byte* uv = data + (long)w * h;
        for (int row = 0; row < h / 2; row++) Buffer.MemoryCopy(uvPlane + (long)row * pitchUV, uv + (long)row * w, w, w);
        EndSlot(n, header, w, h, w, w);
        return true;
    }

    private long BeginSlot(out byte* header, out byte* data)
    {
        long n = _sequence + 1;
        int slot = (int)(n % SlotCount);
        header = _base + OffSlots + slot * SlotHeaderSize;
        data = _base + SlotDataOffset(slot);
        Volatile.Write(ref *(long*)(header + SlotOffSequence), 0); // busy
        return n;
    }

    private void EndSlot(long n, byte* header, int w, int h, int strideY, int strideUV)
    {
        *(long*)(header + SlotOffTimestamp) = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
        *(uint*)(header + SlotOffWidth) = (uint)w;
        *(uint*)(header + SlotOffHeight) = (uint)h;
        *(uint*)(header + SlotOffStrideY) = (uint)strideY;
        *(uint*)(header + SlotOffStrideUV) = (uint)strideUV;
        *(uint*)(header + SlotOffFormat) = FormatNv12;
        *(uint*)(header + SlotOffFlags) = 0;
        Volatile.Write(ref *(long*)(header + SlotOffSequence), n);
        Volatile.Write(ref *(long*)(_base + OffWriteSequence), n);
        _sequence = n;
        _event?.Set();
        FramesWritten++;
    }

    public void Close()
    {
        if (_base != null)
        {
            _view?.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }
        _view?.Dispose();
        _view = null;
        _mapping?.Dispose();
        _mapping = null;
        _event?.Dispose();
        _event = null;
    }

    public void Dispose() => Close();
}

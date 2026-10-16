using System.Diagnostics;
using System.Numerics;
using AirLens.Core.Protocol;

namespace AirLens.Core.Buffering;

/// <summary>
/// One compressed access unit read straight from the socket. The array only grows (power of two)
/// and is reused for the life of the process, so a multi-hour stream allocates nothing per frame and
/// never churns the Large Object Heap (4K IDRs exceed the 85 KB LOH threshold).
/// Ownership is single: whoever holds a buffer passes it on or calls <see cref="Return"/>.
/// </summary>
public sealed class PacketBuffer
{
    private readonly PacketBufferPool _pool;
    internal bool InPool;

    internal PacketBuffer(PacketBufferPool pool, int capacity)
    {
        _pool = pool;
        Data = new byte[capacity];
    }

    public byte[] Data { get; private set; }
    public int Length { get; internal set; }
    public FrameFlags Flags { get; set; }
    public long PtsUs { get; set; }
    public uint Sequence { get; set; }
    public uint Epoch { get; set; }

    /// <summary><see cref="Stopwatch.GetTimestamp"/> when the last byte arrived.</summary>
    public long ArrivalTicks { get; set; }

    public bool IsKeyframe => (Flags & FrameFlags.Keyframe) != 0;
    public bool IsConfig => (Flags & FrameFlags.CodecConfig) != 0;
    public Span<byte> Payload => Data.AsSpan(0, Length);

    internal void Prepare(int length)
    {
        if (Data.Length < length)
            Data = new byte[(int)Math.Min(ProtocolConstants.MaxPayload, BitOperations.RoundUpToPowerOf2((uint)length))];
        Length = length;
        Flags = FrameFlags.None;
        PtsUs = 0;
        Sequence = 0;
        Epoch = 0;
        ArrivalTicks = 0;
    }

    public void Return() => _pool.Return(this);
}

/// <summary>
/// Bounded free-list of <see cref="PacketBuffer"/>s. <see cref="Outstanding"/> is shown in the
/// diagnostics overlay: a number that only climbs is a leak.
/// </summary>
public sealed class PacketBufferPool(int maxRetained = 64, int initialCapacity = 256 * 1024)
{
    private readonly Stack<PacketBuffer> _free = new();
    private int _outstanding;

    public int Outstanding => Volatile.Read(ref _outstanding);

    public int Retained
    {
        get { lock (_free) return _free.Count; }
    }

    public PacketBuffer Rent(int length)
    {
        PacketBuffer? buffer;
        lock (_free) _free.TryPop(out buffer);
        buffer ??= new PacketBuffer(this, Math.Max(initialCapacity, length));
        buffer.InPool = false;
        buffer.Prepare(length);
        Interlocked.Increment(ref _outstanding);
        return buffer;
    }

    internal void Return(PacketBuffer buffer)
    {
        lock (_free)
        {
            if (buffer.InPool)
            {
                Debug.Fail("PacketBuffer returned twice");
                return;
            }
            buffer.InPool = true;
            Interlocked.Decrement(ref _outstanding);
            if (_free.Count < maxRetained) _free.Push(buffer);
        }
    }
}

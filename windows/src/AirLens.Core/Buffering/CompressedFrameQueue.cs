using System.Diagnostics;

namespace AirLens.Core.Buffering;

/// <summary>Backlog limits for <see cref="CompressedFrameQueue"/>.</summary>
public readonly record struct QueueBudget(int MaxFrames, int MaxDelayMs)
{
    /// <summary>USB: transport jitter is tiny, so any backlog is a stall worth skipping.</summary>
    public static QueueBudget Usb => new(3, 50);

    /// <summary>Wi-Fi: tolerate short bursts (an IDR, a retransmit) before skipping.</summary>
    public static QueueBudget Wifi => new(6, 150);

    /// <summary>
    /// Until the first frame of a session or epoch decodes. Opening a 4K hardware decoder takes
    /// ~200 ms; a strict budget would discard the very keyframe the decoder is waiting for.
    /// </summary>
    public static QueueBudget Startup => new(60, 2000);
}

public enum EnqueueResult
{
    Accepted,
    Dropped,

    /// <summary>The backlog was discarded; ask the phone for a keyframe.</summary>
    DroppedNeedKeyframe,
}

/// <summary>
/// Bounded hand-off from the socket thread to the decoder thread, mirroring the phone's SendQueue:
/// <list type="number">
/// <item>Codec config is never dropped.</item>
/// <item>After any drop, nothing but a keyframe is accepted (a hole in a P-frame chain corrupts
///   every later picture until the next IDR anyway).</item>
/// <item>A keyframe obsoletes every queued picture before it: a free way to drain a backlog.</item>
/// <item>If the oldest queued frame has waited longer than <see cref="QueueBudget.MaxDelayMs"/>,
///   or more than <see cref="QueueBudget.MaxFrames"/> are waiting, everything is dropped and a
///   keyframe is requested. Latency is capped; memory is capped.</item>
/// </list>
/// </summary>
public sealed class CompressedFrameQueue(QueueBudget budget)
{
    private readonly object _gate = new();
    private readonly LinkedList<PacketBuffer> _frames = new();
    private bool _waitingForKeyframe = true;
    private bool _closed;
    private long _dropped;

    public QueueBudget Budget { get; set; } = budget;
    public long DroppedFrames => Interlocked.Read(ref _dropped);

    public int Count
    {
        get { lock (_gate) return _frames.Count; }
    }

    /// <summary>How long the oldest queued frame has been waiting.</summary>
    public double BacklogMs
    {
        get
        {
            lock (_gate)
            {
                var first = _frames.First?.Value;
                return first == null ? 0 : Stopwatch.GetElapsedTime(first.ArrivalTicks).TotalMilliseconds;
            }
        }
    }

    /// <summary>Takes ownership of <paramref name="packet"/>.</summary>
    public EnqueueResult Enqueue(PacketBuffer packet)
    {
        lock (_gate)
        {
            if (_closed)
            {
                packet.Return();
                return EnqueueResult.Dropped;
            }
            if (packet.IsConfig)
            {
                Add(packet);
                return EnqueueResult.Accepted;
            }
            if (_waitingForKeyframe)
            {
                if (!packet.IsKeyframe)
                {
                    // Keep asking while waiting: the first request may have been lost or rate-limited,
                    // and without an IDR nothing will ever decode (callers rate-limit the requests).
                    Drop(packet);
                    return EnqueueResult.DroppedNeedKeyframe;
                }
                _waitingForKeyframe = false;
            }
            else if (packet.IsKeyframe)
            {
                DropQueuedPictures();
            }

            var oldest = _frames.First?.Value;
            bool tooOld = oldest != null && Stopwatch.GetElapsedTime(oldest.ArrivalTicks).TotalMilliseconds > Budget.MaxDelayMs;
            if (_frames.Count >= Budget.MaxFrames || tooOld)
            {
                DropQueuedPictures();
                if (!packet.IsKeyframe)
                {
                    Drop(packet);
                    _waitingForKeyframe = true;
                    Monitor.PulseAll(_gate);
                    return EnqueueResult.DroppedNeedKeyframe;
                }
            }
            Add(packet);
            return EnqueueResult.Accepted;
        }
    }

    /// <summary>Waits up to <paramref name="timeout"/>; returns false on timeout or once closed.</summary>
    public bool TryDequeue(TimeSpan timeout, out PacketBuffer? packet)
    {
        lock (_gate)
        {
            long deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
            while (_frames.Count == 0 && !_closed)
            {
                long remaining = deadline - Stopwatch.GetTimestamp();
                if (remaining <= 0)
                {
                    packet = null;
                    return false;
                }
                Monitor.Wait(_gate, TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency));
            }
            if (_closed || _frames.First == null)
            {
                packet = null;
                return false;
            }
            packet = _frames.First.Value;
            _frames.RemoveFirst();
            return true;
        }
    }

    /// <summary>
    /// After a decode error: drops queued pictures up to the first queued keyframe, which (with
    /// everything after it) is kept because it repairs the reference chain. With no keyframe
    /// queued, waits for one.
    /// </summary>
    public void DropUntilKeyframe()
    {
        lock (_gate)
        {
            var node = _frames.First;
            while (node != null && !node.Value.IsKeyframe)
            {
                var next = node.Next;
                if (!node.Value.IsConfig)
                {
                    Drop(node.Value);
                    _frames.Remove(node);
                }
                node = next;
            }
            if (node == null) _waitingForKeyframe = true;
        }
    }

    /// <summary>Drops everything, config included (new epoch), and waits for a keyframe.</summary>
    public void Reset()
    {
        lock (_gate)
        {
            while (_frames.First != null)
            {
                Drop(_frames.First.Value);
                _frames.RemoveFirst();
            }
            _waitingForKeyframe = true;
        }
    }

    /// <summary>Returns every queued buffer to the pool and wakes waiters. Idempotent.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            while (_frames.First != null)
            {
                _frames.First.Value.Return();
                _frames.RemoveFirst();
            }
            Monitor.PulseAll(_gate);
        }
    }

    private void Add(PacketBuffer packet)
    {
        _frames.AddLast(packet);
        Monitor.Pulse(_gate);
    }

    private void DropQueuedPictures()
    {
        var node = _frames.First;
        while (node != null)
        {
            var next = node.Next;
            if (!node.Value.IsConfig)
            {
                Drop(node.Value);
                _frames.Remove(node);
            }
            node = next;
        }
    }

    private void Drop(PacketBuffer packet)
    {
        if (!packet.IsConfig) Interlocked.Increment(ref _dropped);
        packet.Return();
    }
}

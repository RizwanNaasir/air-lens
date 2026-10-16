using System.Diagnostics;
using AirLens.Core.Buffering;
using AirLens.Core.Protocol;
using Xunit;

namespace AirLens.Core.Tests;

public class CompressedFrameQueueTests
{
    private readonly PacketBufferPool _pool = new();
    private readonly CompressedFrameQueue _queue = new(new QueueBudget(MaxFrames: 4, MaxDelayMs: 10_000));

    private PacketBuffer Frame(FrameFlags flags, long pts = 0, long arrival = 0)
    {
        var p = _pool.Rent(100);
        p.Flags = flags;
        p.PtsUs = pts;
        p.ArrivalTicks = arrival == 0 ? Stopwatch.GetTimestamp() : arrival;
        return p;
    }

    private List<PacketBuffer> Drain()
    {
        var list = new List<PacketBuffer>();
        while (_queue.TryDequeue(TimeSpan.Zero, out var p) && p != null) list.Add(p);
        return list;
    }

    [Fact]
    public void WaitsForKeyframeButAlwaysAcceptsConfig()
    {
        // While waiting for the first keyframe, every dropped delta asks for one (callers rate-limit).
        Assert.Equal(EnqueueResult.DroppedNeedKeyframe, _queue.Enqueue(Frame(FrameFlags.None)));
        Assert.Equal(EnqueueResult.Accepted, _queue.Enqueue(Frame(FrameFlags.CodecConfig)));
        Assert.Equal(EnqueueResult.Accepted, _queue.Enqueue(Frame(FrameFlags.Keyframe)));
        Assert.Equal(EnqueueResult.Accepted, _queue.Enqueue(Frame(FrameFlags.None)));
        Assert.Equal(3, _queue.Count);
        Drain().ForEach(p => p.Return());
    }

    [Fact]
    public void FrameBudgetOverflowDropsBacklogAndAsksForKeyframe()
    {
        _queue.Enqueue(Frame(FrameFlags.Keyframe, 1));
        for (int i = 2; i <= 4; i++) _queue.Enqueue(Frame(FrameFlags.None, i));
        Assert.Equal(EnqueueResult.DroppedNeedKeyframe, _queue.Enqueue(Frame(FrameFlags.None, 5)));
        Assert.Equal(0, _queue.Count);
        Assert.Equal(EnqueueResult.DroppedNeedKeyframe, _queue.Enqueue(Frame(FrameFlags.None, 6)));
        Assert.Equal(EnqueueResult.Accepted, _queue.Enqueue(Frame(FrameFlags.Keyframe, 7)));
        var out1 = Drain();
        Assert.Equal(new long[] { 7 }, out1.Select(p => p.PtsUs));
        out1.ForEach(p => p.Return());
    }

    [Fact]
    public void StaleBacklogTriggersDropByAge()
    {
        var queue = new CompressedFrameQueue(new QueueBudget(MaxFrames: 100, MaxDelayMs: 50));
        long old = Stopwatch.GetTimestamp() - Stopwatch.Frequency; // arrived a second ago
        queue.Enqueue(Frame(FrameFlags.Keyframe, 1, old));
        Assert.Equal(EnqueueResult.DroppedNeedKeyframe, queue.Enqueue(Frame(FrameFlags.None, 2)));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void KeyframeSupersedesQueuedDeltasButKeepsConfig()
    {
        _queue.Enqueue(Frame(FrameFlags.Keyframe, 1));
        _queue.Enqueue(Frame(FrameFlags.None, 2));
        _queue.Enqueue(Frame(FrameFlags.CodecConfig, 3));
        _queue.Enqueue(Frame(FrameFlags.Keyframe, 4));
        var out1 = Drain();
        Assert.Equal(new long[] { 3, 4 }, out1.Select(p => p.PtsUs));
        out1.ForEach(p => p.Return());
    }

    [Fact]
    public void DecodeErrorKeepsAQueuedKeyframeAndWhatFollowsIt()
    {
        _queue.Enqueue(Frame(FrameFlags.Keyframe, 1));
        Assert.True(_queue.TryDequeue(TimeSpan.Zero, out var first));
        first!.Return();
        var queue = new CompressedFrameQueue(new QueueBudget(MaxFrames: 10, MaxDelayMs: 10_000));
        queue.Enqueue(Frame(FrameFlags.Keyframe, 1));
        queue.Enqueue(Frame(FrameFlags.None, 2));
        queue.Enqueue(Frame(FrameFlags.CodecConfig, 3));
        queue.Enqueue(Frame(FrameFlags.None, 4));
        queue.TryDequeue(TimeSpan.Zero, out var decoded); // the keyframe went to the decoder and failed
        decoded!.Return();
        queue.DropUntilKeyframe();                   // no keyframe queued: drop deltas, keep config, wait
        Assert.Equal(EnqueueResult.DroppedNeedKeyframe, queue.Enqueue(Frame(FrameFlags.None, 5)));
        Assert.Equal(EnqueueResult.Accepted, queue.Enqueue(Frame(FrameFlags.Keyframe, 6)));
        queue.Enqueue(Frame(FrameFlags.None, 7));
        queue.DropUntilKeyframe();                   // keyframe 6 is queued: keep it and frame 7
        var rest = new List<PacketBuffer>();
        while (queue.TryDequeue(TimeSpan.Zero, out var p) && p != null) rest.Add(p);
        Assert.Equal(new long[] { 3, 6, 7 }, rest.Select(p => p.PtsUs));
        rest.ForEach(p => p.Return());
    }

    [Fact]
    public void CloseReturnsEverythingToThePool()
    {
        _queue.Enqueue(Frame(FrameFlags.Keyframe));
        _queue.Enqueue(Frame(FrameFlags.None));
        _queue.Close();
        Assert.Equal(EnqueueResult.Dropped, _queue.Enqueue(Frame(FrameFlags.Keyframe)));
        Assert.False(_queue.TryDequeue(TimeSpan.FromMilliseconds(10), out _));
        Assert.Equal(0, _pool.Outstanding);
    }

    [Fact]
    public void TryDequeueWaitsForAProducer()
    {
        var producer = Task.Run(async () =>
        {
            await Task.Delay(50);
            _queue.Enqueue(Frame(FrameFlags.Keyframe, 42));
        });
        Assert.True(_queue.TryDequeue(TimeSpan.FromSeconds(2), out var p));
        Assert.Equal(42, p!.PtsUs);
        p.Return();
        producer.Wait();
    }

    [Fact]
    public void PoolReusesBuffersAndGrowsToHighWaterMark()
    {
        var pool = new PacketBufferPool(initialCapacity: 1024);
        var a = pool.Rent(10);
        var array = a.Data;
        a.Return();
        var b = pool.Rent(500_000);
        Assert.Same(a, b);
        Assert.True(b.Data.Length >= 500_000);
        Assert.NotSame(array, b.Data);
        b.Return();
        var c = pool.Rent(100);
        Assert.Same(b.Data, c.Data); // grown array kept: no LOH churn on the next IDR
        c.Return();
        Assert.Equal(0, pool.Outstanding);
    }
}

public class PlayoutControllerTests
{
    private static readonly long Freq = Stopwatch.Frequency;
    private static long Ms(double ms) => (long)(ms / 1000.0 * Freq);

    [Fact]
    public void RealtimeShowsEveryFrameOnArrival()
    {
        var p = new PlayoutController(PlayoutMode.Realtime, 33.3);
        long t = Ms(10_000);
        for (int i = 0; i < 100; i++)
        {
            long arrival = t + Ms(i * 33.3 + (i % 3) * 7);
            Assert.Equal(arrival, p.ScheduleTicks(i * 33_333, arrival));
        }
    }

    [Fact]
    public void SmoothedWithoutJitterAddsNoDelay()
    {
        var p = new PlayoutController(PlayoutMode.Smoothed, 33.3);
        long t = Ms(10_000);
        for (int i = 0; i < 60; i++)
        {
            long arrival = t + Ms(i * 33.3);
            long show = p.ScheduleTicks(i * 33_300, arrival);
            Assert.True(show - arrival <= Ms(0.1), $"frame {i} delayed by {(show - arrival) * 1000.0 / Freq} ms");
        }
        Assert.True(p.TargetHoldMs < 0.5);
    }

    [Fact]
    public void SmoothedHoldIsBoundedAndLateFramesAreNotDelayed()
    {
        var p = new PlayoutController(PlayoutMode.Smoothed, 33.3);
        long t = Ms(10_000);
        var rnd = new Random(7);
        long maxDelay = 0;
        for (int i = 0; i < 300; i++)
        {
            double jitter = rnd.NextDouble() * 20; // 0-20 ms network jitter
            long arrival = t + Ms(i * 33.3 + jitter);
            long show = p.ScheduleTicks(i * 33_300, arrival);
            Assert.True(show >= arrival);
            maxDelay = Math.Max(maxDelay, show - arrival);
        }
        Assert.True(p.TargetHoldMs > 1, "jitter should produce a hold");
        Assert.True(maxDelay <= Ms(1.5 * 33.3 + 0.5), "hold never exceeds 1.5 frame intervals");
    }

    [Fact]
    public void ClockDriftIsTrackedByTheWindowedMinimum()
    {
        var p = new PlayoutController(PlayoutMode.Smoothed, 33.3);
        long t = Ms(10_000);
        // Phone clock runs 1000 ppm slow: transit grows ~33 us per frame. After the 2 s window
        // rolls over, the baseline follows, so the hold stays small instead of growing forever.
        for (int i = 0; i < 600; i++)
        {
            long arrival = t + Ms(i * 33.3);
            p.ScheduleTicks((long)(i * 33_300 * 0.999), arrival);
        }
        Assert.True(p.TargetHoldMs < 5, $"hold {p.TargetHoldMs} ms after drift");
    }
}

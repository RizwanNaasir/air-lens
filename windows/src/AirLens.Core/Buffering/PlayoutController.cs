using System.Diagnostics;

namespace AirLens.Core.Buffering;

public enum PlayoutMode
{
    /// <summary>Show every frame the moment it is decoded. USB: jitter is negligible.</summary>
    Realtime,

    /// <summary>Hold frames briefly to even out Wi-Fi jitter, never longer than ~1.5 frame intervals.</summary>
    Smoothed,
}

/// <summary>
/// Decides when a decoded frame becomes visible. No clock synchronisation is needed:
/// <code>
/// transit  = arrival(local µs) − pts(phone µs)       // unknown offset + network delay
/// baseline = min(transit) over the last 2 s           // offset + fastest path; tracks clock drift
/// jitter   = transit − baseline                       // ≥ 0
/// hold     = clamp(2 · EWMA(jitter), 0, maxHold)
/// showAt   = pts + baseline + hold                    // in local time
/// </code>
/// A late frame (jitter ≥ hold) is shown immediately. In <see cref="PlayoutMode.Realtime"/> the
/// hold is always 0, which is what keeps USB under the 30 ms budget.
/// The 2 s minimum uses 8 rotating 250 ms buckets: O(1) per frame, no allocation.
/// Not thread-safe; owned by the decoder thread.
/// </summary>
public sealed class PlayoutController
{
    private const int Buckets = 8;
    private const long BucketUs = 250_000;
    private readonly long[] _bucketMin = new long[Buckets];
    private long _bucketStartUs = long.MinValue;
    private int _bucket;
    private double _jitterEwmaUs;
    private bool _primed;

    public PlayoutController(PlayoutMode mode, double frameIntervalMs)
    {
        Mode = mode;
        FrameIntervalMs = frameIntervalMs;
        Reset();
    }

    public PlayoutMode Mode { get; set; }
    public double FrameIntervalMs { get; set; }
    public double JitterMs => _jitterEwmaUs / 1000.0;
    public double TargetHoldMs => HoldUs() / 1000.0;

    public void Reset()
    {
        Array.Fill(_bucketMin, long.MaxValue);
        _bucketStartUs = long.MinValue;
        _bucket = 0;
        _jitterEwmaUs = 0;
        _primed = false;
    }

    /// <summary>Returns the <see cref="Stopwatch"/> timestamp at which the frame should be shown.</summary>
    public long ScheduleTicks(long ptsUs, long arrivalTicks)
    {
        long arrivalUs = TicksToUs(arrivalTicks);
        long transit = arrivalUs - ptsUs;
        Rotate(arrivalUs);
        if (transit < _bucketMin[_bucket]) _bucketMin[_bucket] = transit;

        long baseline = long.MaxValue;
        foreach (long m in _bucketMin) if (m < baseline) baseline = m;

        long jitter = transit - baseline;
        _jitterEwmaUs = _primed ? _jitterEwmaUs + (jitter - _jitterEwmaUs) / 16.0 : jitter;
        _primed = true;

        if (Mode == PlayoutMode.Realtime) return arrivalTicks;
        long showAtUs = ptsUs + baseline + HoldUs();
        return Math.Max(arrivalTicks, UsToTicks(showAtUs));
    }

    private long HoldUs()
    {
        if (Mode == PlayoutMode.Realtime) return 0;
        double max = 1.5 * FrameIntervalMs * 1000.0;
        return (long)Math.Clamp(2 * _jitterEwmaUs, 0, max);
    }

    private void Rotate(long nowUs)
    {
        if (_bucketStartUs == long.MinValue)
        {
            _bucketStartUs = nowUs;
            return;
        }
        if (nowUs - _bucketStartUs >= Buckets * BucketUs)
        {
            // A gap longer than the whole window (stream paused): every bucket is stale.
            Array.Fill(_bucketMin, long.MaxValue);
            _bucketStartUs = nowUs;
            return;
        }
        while (nowUs - _bucketStartUs >= BucketUs)
        {
            _bucket = (_bucket + 1) % Buckets;
            _bucketMin[_bucket] = long.MaxValue;
            _bucketStartUs += BucketUs;
        }
    }

    public static long TicksToUs(long ticks) => (long)(ticks * (1_000_000.0 / Stopwatch.Frequency));
    public static long UsToTicks(long us) => (long)(us * (Stopwatch.Frequency / 1_000_000.0));
}

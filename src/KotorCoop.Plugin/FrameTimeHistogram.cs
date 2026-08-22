namespace KotorCoop.Plugin;

/// <summary>One flush period's worth of frame-time readings, ready to log.</summary>
internal readonly record struct FrameTimeSnapshot(
    long SampleCount, double AverageMs, double MaxMs, long OverThresholdCount);

/// <summary>
/// A cheap, allocation-free accumulator for per-frame wall-clock deltas — the pure half of the
/// `frame_stats` instrument (docs/research/lag-audit.md), separated from the wall-clock read
/// and the log line the same way every other stats/rule helper in this codebase splits engine
/// I/O from arithmetic, so the arithmetic is unit-testable without <c>FrameHook</c> or a live
/// game.
///
/// Deliberately NOT an EWMA of the delta feeding into itself across flushes: each flush period
/// reports the TRUE average and max of exactly the samples it saw, then resets — a smoothed
/// running average would blur a burst of hitches into an innocuous-looking mean over a long
/// session, which is precisely the failure mode this exists to catch.
/// </summary>
internal struct FrameTimeHistogram
{
    private long _sampleCount;
    private double _sumMs;
    private double _maxMs;
    private long _overThresholdCount;

    /// <summary>Records one frame's delta. <paramref name="deltaMs"/> must be the gap since the
    /// PREVIOUS frame — the caller skips the very first call after a (re)start, since there is
    /// nothing to measure a delta against yet.</summary>
    public void Add(double deltaMs, double hitchThresholdMs)
    {
        if (deltaMs < 0)
        {
            // A clock that ever reports backwards is not a frame time; refuse rather than
            // corrupt the average with a negative sample.
            return;
        }

        _sampleCount++;
        _sumMs += deltaMs;

        if (deltaMs > _maxMs)
        {
            _maxMs = deltaMs;
        }

        if (deltaMs > hitchThresholdMs)
        {
            _overThresholdCount++;
        }
    }

    public readonly bool HasSamples => _sampleCount > 0;

    /// <summary>The accumulated period as a snapshot, ready to log — 0-filled averages/max when
    /// nothing was recorded rather than a NaN or a divide-by-zero.</summary>
    public readonly FrameTimeSnapshot Snapshot() => new(
        _sampleCount,
        _sampleCount == 0 ? 0 : _sumMs / _sampleCount,
        _maxMs,
        _overThresholdCount);

    /// <summary>Starts a fresh accumulation period. Called right after logging a snapshot, so
    /// each flush reports only what happened SINCE the last one.</summary>
    public void Reset()
    {
        _sampleCount = 0;
        _sumMs = 0;
        _maxMs = 0;
        _overThresholdCount = 0;
    }
}

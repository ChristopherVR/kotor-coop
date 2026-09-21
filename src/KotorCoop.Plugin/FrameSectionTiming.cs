namespace KotorCoop.Plugin;

/// <summary>
/// Allocation-free accumulator for one opt-in <c>frame_stats</c> controller group. The
/// timestamp is taken by the caller only while that diagnostic is enabled; the accumulator is
/// deliberately just counters so an ordinary frame never creates an object or a collection.
/// </summary>
internal struct FrameSectionTiming
{
    private long _sampleCount;
    private long _totalTicks;
    private long _maxTicks;

    public readonly bool HasSamples => _sampleCount != 0;

    public void Add(long elapsedTicks)
    {
        if (elapsedTicks < 0)
        {
            return;
        }

        _sampleCount++;
        _totalTicks += elapsedTicks;
        if (elapsedTicks > _maxTicks)
        {
            _maxTicks = elapsedTicks;
        }
    }

    public readonly FrameSectionSnapshot Snapshot(long stopwatchFrequency)
    {
        if (_sampleCount == 0 || stopwatchFrequency <= 0)
        {
            return default;
        }

        double scale = 1000.0 / stopwatchFrequency;
        return new FrameSectionSnapshot(
            _sampleCount,
            _totalTicks * scale / _sampleCount,
            _maxTicks * scale);
    }

    public void Reset()
    {
        _sampleCount = 0;
        _totalTicks = 0;
        _maxTicks = 0;
    }
}

internal readonly record struct FrameSectionSnapshot(
    long SampleCount, double AverageMs, double MaxMs);

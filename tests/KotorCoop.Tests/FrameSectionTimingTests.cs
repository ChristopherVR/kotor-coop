using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public class FrameSectionTimingTests
{
    [Fact]
    public void SnapshotConvertsTotalAndMaximumTicksToMilliseconds()
    {
        FrameSectionTiming timing = default;
        timing.Add(10);
        timing.Add(30);

        FrameSectionSnapshot snapshot = timing.Snapshot(stopwatchFrequency: 1000);

        Assert.Equal(2, snapshot.SampleCount);
        Assert.Equal(20, snapshot.AverageMs, precision: 3);
        Assert.Equal(30, snapshot.MaxMs, precision: 3);
    }

    [Fact]
    public void NegativeSamplesAreIgnored()
    {
        FrameSectionTiming timing = default;
        timing.Add(-1);

        Assert.False(timing.HasSamples);
    }

    [Fact]
    public void ResetStartsACleanPeriod()
    {
        FrameSectionTiming timing = default;
        timing.Add(50);
        timing.Reset();

        Assert.False(timing.HasSamples);
        timing.Add(10);
        Assert.Equal(10, timing.Snapshot(stopwatchFrequency: 1000).AverageMs, precision: 3);
    }
}

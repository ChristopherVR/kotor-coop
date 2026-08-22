using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure half of the `frame_stats` instrument (docs/research/lag-audit.md): an
/// allocation-free accumulator fed one wall-clock delta per rendered frame, flushed to the log
/// every ~10 s so a live round can measure frame-time hitching instead of guessing at it.
/// </summary>
public class FrameTimeHistogramTests
{
    [Fact]
    public void NoSamplesReportsZeroedSnapshot()
    {
        FrameTimeHistogram histogram = default;

        Assert.False(histogram.HasSamples);
        Assert.Equal(new FrameTimeSnapshot(0, 0, 0, 0), histogram.Snapshot());
    }

    [Fact]
    public void ASingleSampleIsItsOwnAverageAndMax()
    {
        FrameTimeHistogram histogram = default;
        histogram.Add(16.7, hitchThresholdMs: 33.0);

        FrameTimeSnapshot snapshot = histogram.Snapshot();
        Assert.Equal(1, snapshot.SampleCount);
        Assert.Equal(16.7, snapshot.AverageMs, precision: 3);
        Assert.Equal(16.7, snapshot.MaxMs, precision: 3);
        Assert.Equal(0, snapshot.OverThresholdCount);
    }

    [Fact]
    public void TheAverageIsTheTrueMeanOfEverySampleThisPeriod()
    {
        FrameTimeHistogram histogram = default;
        histogram.Add(10, 33);
        histogram.Add(20, 33);
        histogram.Add(30, 33);

        FrameTimeSnapshot snapshot = histogram.Snapshot();
        Assert.Equal(3, snapshot.SampleCount);
        Assert.Equal(20.0, snapshot.AverageMs, precision: 3);
        Assert.Equal(30.0, snapshot.MaxMs, precision: 3);
    }

    [Fact]
    public void SamplesOverTheThresholdAreCountedAsHitches()
    {
        FrameTimeHistogram histogram = default;
        histogram.Add(16, 33); // fine
        histogram.Add(50, 33); // hitch
        histogram.Add(34, 33); // hitch (strictly greater than)
        histogram.Add(33, 33); // exactly the threshold - not a hitch

        Assert.Equal(2, histogram.Snapshot().OverThresholdCount);
    }

    [Fact]
    public void OneHugeStallDoesNotHideInASmoothedAverage()
    {
        // The whole reason this is NOT an EWMA: a single multi-second stall averaged across a
        // long session would read as noise. Within one flush period it must dominate the mean
        // and the max both.
        FrameTimeHistogram histogram = default;
        for (int i = 0; i < 100; i++)
        {
            histogram.Add(16, 33);
        }

        histogram.Add(3000, 33);

        FrameTimeSnapshot snapshot = histogram.Snapshot();
        Assert.Equal(3000, snapshot.MaxMs, precision: 1);
        Assert.True(snapshot.AverageMs > 40, "a single 3s stall must move the period average, not vanish into it");
        Assert.Equal(1, snapshot.OverThresholdCount);
    }

    [Fact]
    public void NegativeDeltasAreRefusedRatherThanCorruptingTheAverage()
    {
        // A clock that ever reports backwards (a timer wraparound, a bad read) is not a real
        // frame time.
        FrameTimeHistogram histogram = default;
        histogram.Add(16, 33);
        histogram.Add(-5, 33);

        Assert.Equal(1, histogram.Snapshot().SampleCount);
    }

    [Fact]
    public void ResetStartsACleanPeriod()
    {
        FrameTimeHistogram histogram = default;
        histogram.Add(500, 33);
        histogram.Reset();

        Assert.False(histogram.HasSamples);
        histogram.Add(16, 33);
        Assert.Equal(16.0, histogram.Snapshot().AverageMs, precision: 3);
    }
}

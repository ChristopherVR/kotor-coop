using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The ring buffer behind `lagstats`' send/recv interval readout (docs/research/lag-audit.md):
/// shows the REAL cadence a session is achieving, not the configured tick rate. Pure and
/// clock-free — every case here drives it with hand-picked timestamps.
/// </summary>
public class IntervalTrackerTests
{
    [Fact]
    public void TheFirstEventReportsNoIntervalYet()
    {
        IntervalTracker tracker = new();
        tracker.Record(1000);

        Assert.Equal(0, tracker.Count);
        Assert.Empty(tracker.Snapshot());
    }

    [Fact]
    public void TwoEventsProduceOneInterval()
    {
        IntervalTracker tracker = new();
        tracker.Record(1000);
        tracker.Record(1033);

        Assert.Equal(1, tracker.Count);
        Assert.Equal([33L], tracker.Snapshot());
    }

    [Fact]
    public void SnapshotOrdersOldestFirst()
    {
        IntervalTracker tracker = new(capacity: 8);
        tracker.Record(0);
        tracker.Record(10);
        tracker.Record(30);
        tracker.Record(60);

        Assert.Equal([10L, 20L, 30L], tracker.Snapshot());
    }

    [Fact]
    public void TheBufferWrapsRatherThanGrowingUnbounded()
    {
        IntervalTracker tracker = new(capacity: 3);
        long now = 0;
        for (int i = 0; i < 10; i++)
        {
            tracker.Record(now);
            now += 10;
        }

        // 9 real intervals recorded (10 events), capped at capacity 3 — and it must be the
        // LAST 3, not the first 3, or a live round would be reading stale history.
        Assert.Equal(3, tracker.Count);
        Assert.Equal([10L, 10L, 10L], tracker.Snapshot());
    }

    [Fact]
    public void AClockGoingBackwardsIsClampedToZeroNotNegative()
    {
        IntervalTracker tracker = new();
        tracker.Record(1000);
        tracker.Record(900); // some clock source went backwards

        Assert.Equal([0L], tracker.Snapshot());
    }

    [Fact]
    public void AverageMsMatchesTheRecordedGaps()
    {
        IntervalTracker tracker = new();
        tracker.Record(0);
        tracker.Record(10);
        tracker.Record(30);

        Assert.Equal(15.0, tracker.AverageMs, precision: 3);
    }

    [Fact]
    public void AverageMsIsZeroWithNoIntervalsYet()
    {
        IntervalTracker tracker = new();
        Assert.Equal(0.0, tracker.AverageMs);
    }
}

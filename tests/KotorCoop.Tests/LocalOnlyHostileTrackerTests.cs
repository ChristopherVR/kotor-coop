using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LocalOnlyHostileTrackerTests
{
    private static readonly HashSet<uint> Host = [0x10, 0x11];

    [Fact]
    public void AHostileTheHostNeverHadIsReportedOnlyAfterTheGrace()
    {
        // Endar Spire 2026-10-08: end_reinforce3/4 (0x16A, 0x16E) existed only on the joiner.
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x10, 0x16A];

        Assert.Empty(tracker.Update(local, Host, 0));
        Assert.Empty(tracker.Update(local, Host, LocalOnlyHostileTracker.GraceMs - 1));
        Assert.Equal([0x16Au], tracker.Update(local, Host, LocalOnlyHostileTracker.GraceMs));
    }

    [Fact]
    public void OneLookThatIncludesTheIdRestartsTheClock()
    {
        // The host's view arrives in pages; a partial cycle can miss an id it does have.
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x20];
        tracker.Update(local, Host, 0);
        tracker.Update(local, new HashSet<uint>(Host) { 0x20 }, 10_000);
        tracker.Update(local, Host, 15_000);

        Assert.Empty(tracker.Update(local, Host, 30_000));
        Assert.Equal([0x20u], tracker.Update(local, Host, 35_000));
    }

    [Fact]
    public void WithNoHostViewNothingIsJudgedAndTheClockRestarts()
    {
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x30];
        tracker.Update(local, Host, 0);
        Assert.Empty(tracker.Update(local, new HashSet<uint>(), 25_000));
        Assert.Empty(tracker.Update(local, Host, 30_000));
        Assert.Equal([0x30u], tracker.Update(local, Host, 50_000));
    }

    [Fact]
    public void AHostileThatLeftThisWorldIsForgotten()
    {
        LocalOnlyHostileTracker tracker = new();
        tracker.Update([0x40], Host, 0);
        tracker.Update([], Host, 10_000);
        Assert.Empty(tracker.Update([0x40], Host, 25_000));
    }

    [Fact]
    public void HostilesTheHostHasAreNeverReported()
    {
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x10, 0x11];
        tracker.Update(local, Host, 0);
        Assert.Empty(tracker.Update(local, Host, 60_000));
    }
}

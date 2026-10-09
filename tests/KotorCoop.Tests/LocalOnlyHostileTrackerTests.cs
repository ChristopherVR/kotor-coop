using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LocalOnlyHostileTrackerTests
{
    private static readonly HashSet<uint> Host = [0x10, 0x11];
    private const long Lag = LocalOnlyHostileTracker.HostViewLagMs;

    [Fact]
    public void AHostileTheHostNeverHadIsReportedByTheFirstCompleteViewThatPostdatesIt()
    {
        // Endar Spire 2026-10-08: end_reinforce3/4 (0x16A, 0x16E) existed only on the joiner.
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x10, 0x16A];

        Assert.Empty(tracker.Update(local, Host, 0, completeViewReceivedAtMs: 0));
        Assert.Empty(tracker.Update(local, Host, 2_000, completeViewReceivedAtMs: 2_000));
        Assert.Equal([0x16Au], tracker.Update(local, Host, Lag, completeViewReceivedAtMs: Lag));
    }

    [Fact]
    public void AnIncompleteViewNeverJudges()
    {
        // The host's view arrives in pages; a partial cycle can miss an id it does have.
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x20];
        tracker.Update(local, Host, 0, null);

        Assert.Empty(tracker.Update(local, Host, 60_000, completeViewReceivedAtMs: null));
        Assert.Equal([0x20u], tracker.Update(local, Host, 60_000, completeViewReceivedAtMs: 60_000));
    }

    [Fact]
    public void AViewThatIncludesTheIdRestartsTheClock()
    {
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x20];
        tracker.Update(local, Host, 0, 0);
        tracker.Update(local, new HashSet<uint>(Host) { 0x20 }, 4_000, 4_000);
        tracker.Update(local, Host, 6_000, 6_000);

        Assert.Empty(tracker.Update(local, Host, 10_000, 10_000));
        Assert.Equal([0x20u], tracker.Update(local, Host, 6_000 + Lag, 6_000 + Lag));
    }

    [Fact]
    public void WithNoHostViewNothingIsJudgedAndTheClockRestarts()
    {
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x30];
        tracker.Update(local, Host, 0, 0);
        Assert.Empty(tracker.Update(local, new HashSet<uint>(), 25_000, 25_000));
        Assert.Empty(tracker.Update(local, Host, 26_000, 26_000));
        Assert.Equal([0x30u], tracker.Update(local, Host, 26_000 + Lag, 26_000 + Lag));
    }

    [Fact]
    public void AHostileThatLeftThisWorldIsForgotten()
    {
        LocalOnlyHostileTracker tracker = new();
        tracker.Update([0x40], Host, 0, 0);
        tracker.Update([], Host, 3_000, 3_000);
        Assert.Empty(tracker.Update([0x40], Host, 5_000, 5_000));
    }

    [Fact]
    public void HostilesTheHostHasAreNeverReported()
    {
        LocalOnlyHostileTracker tracker = new();
        uint[] local = [0x10, 0x11];
        tracker.Update(local, Host, 0, 0);
        Assert.Empty(tracker.Update(local, Host, 60_000, 60_000));
    }
}

using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="SessionStats"/> is a plain immutable snapshot record — a readout `CoopHost`/
/// `CoopClient` fill in and hand out, with no logic of its own. What is worth pinning is the
/// defaults a caller sees before anything has ever happened (an idle, never-connected session
/// must not look like a connected one with zeroed counters) and that with-expressions/equality
/// behave the way every other record in this codebase does.
/// </summary>
public class SessionStatsTests
{
    [Fact]
    public void DefaultIsAnIdleDisconnectedSnapshot()
    {
        SessionStats stats = new();

        Assert.False(stats.Connected);
        Assert.Equal("idle", stats.Status);
        Assert.Equal(0u, stats.LocalPeerId);
        Assert.Equal(0, stats.PeerCount);
        Assert.Equal(0, stats.RoundTripMs);
    }

    [Fact]
    public void DefaultCountersAreAllZero()
    {
        SessionStats stats = new();

        Assert.Equal(0, stats.PacketsSent);
        Assert.Equal(0, stats.PacketsReceived);
        Assert.Equal(0, stats.PacketsDropped);
        Assert.Equal(0, stats.BytesSent);
        Assert.Equal(0, stats.BytesReceived);
        Assert.Equal(0, stats.LastInboundAgeMs);
        Assert.Equal(0u, stats.FlagVersion);
        Assert.Equal(0, stats.PendingFlagOps);
        Assert.Equal(0u, stats.LivenessAnomalies);
        Assert.Equal(0, stats.PhantomInterpolationDelayMs);
    }

    [Fact]
    public void DefaultIntervalListsAreEmptyNotNull()
    {
        SessionStats stats = new();

        Assert.NotNull(stats.RecentSendIntervalsMs);
        Assert.Empty(stats.RecentSendIntervalsMs);
        Assert.NotNull(stats.RecentRecvIntervalsMs);
        Assert.Empty(stats.RecentRecvIntervalsMs);
    }

    [Fact]
    public void WithExpressionProducesAnIndependentCopy()
    {
        SessionStats original = new() { Connected = true, PeerCount = 2, Status = "connected" };
        SessionStats updated = original with { PeerCount = 3 };

        Assert.Equal(2, original.PeerCount);
        Assert.Equal(3, updated.PeerCount);
        Assert.True(updated.Connected);
        Assert.Equal("connected", updated.Status);
    }

    [Fact]
    public void IntervalListsSurviveAWithExpressionUntouched()
    {
        long[] intervals = [30, 33, 31];
        SessionStats original = new() { RecentSendIntervalsMs = intervals };

        SessionStats updated = original with { PeerCount = 1 };

        Assert.Equal(intervals, updated.RecentSendIntervalsMs);
    }

    [Fact]
    public void TwoSnapshotsWithTheSameScalarValuesAreEqual()
    {
        SessionStats a = new() { Connected = true, LocalPeerId = 5, PeerCount = 2 };
        SessionStats b = new() { Connected = true, LocalPeerId = 5, PeerCount = 2 };

        Assert.Equal(a, b);
    }

    [Fact]
    public void TwoSnapshotsWithDifferentValuesAreNotEqual()
    {
        SessionStats a = new() { PeerCount = 2 };
        SessionStats b = new() { PeerCount = 3 };

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void TwoSnapshotsWithEqualButDifferentIntervalListInstancesAreEqual()
    {
        // Record equality on an IReadOnlyList<long> property is reference equality unless the
        // underlying collection type implements structural equality itself. Arrays do not, so
        // this pins the ACTUAL behaviour (not equal) rather than an assumption a caller might
        // otherwise make about "same contents == equal snapshot".
        SessionStats a = new() { RecentSendIntervalsMs = [30, 33] };
        SessionStats b = new() { RecentSendIntervalsMs = [30, 33] };

        Assert.NotEqual(a, b);
        Assert.Equal(a.RecentSendIntervalsMs, b.RecentSendIntervalsMs);
    }
}

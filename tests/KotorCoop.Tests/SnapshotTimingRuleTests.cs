using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure half of the burst fix: <see cref="SnapshotTimingRule"/> decides what time a
/// received snapshot is treated as having HAPPENED at, given only when it landed and how fast
/// the sender claims to be sending. See its own remarks for why arrival times are the wrong
/// answer, and docs/research/lag-audit.md for the measured symptom.
/// </summary>
public class SnapshotTimingRuleTests
{
    private const int Period = 33;

    private static PlayerState At(float x) => new(new Vector3f(x, 0f, 0f), 0f, "end_m01aa", 100, 0);

    [Fact]
    public void FirstSampleIsStampedWhenItArrived()
    {
        // Nothing to regularise against yet — the arrival IS the origin of the timeline.
        Assert.Equal(1000, SnapshotTimingRule.Assign(SnapshotTimingState.Fresh, 1000, Period));
    }

    [Theory]
    // Everything from a coalesced 0 ms gap to a badly late 49 ms one is ONE send period: the
    // sample exists, so exactly one period passed at the sender, whatever the link did with it.
    [InlineData(0)]
    [InlineData(17)]
    [InlineData(33)]
    [InlineData(45)]
    [InlineData(49)]
    public void JitterWithinOnePeriodIsFlattenedToExactlyOnePeriod(long gapMs)
    {
        SnapshotTimingState state = SnapshotTimingState.Fresh.Accept(1000, 1000);

        Assert.Equal(1033, SnapshotTimingRule.Assign(state, 1000 + gapMs, Period));
    }

    [Fact]
    public void AGapOfTwoPeriodsIsCreditedAsTwoPeriods()
    {
        // What a suppressed duplicate leaves behind: the timing state was NOT advanced for it,
        // so the next real sample's gap spans both periods and must be credited both — this is
        // the whole reason the double-speed dash disappears.
        SnapshotTimingState state = SnapshotTimingState.Fresh.Accept(1000, 1000);

        Assert.Equal(1066, SnapshotTimingRule.Assign(state, 1068, Period));
    }

    [Fact]
    public void AGapPastTheResyncThresholdBelievesTheArrival()
    {
        // A load screen, a stalled sender, a link that came back. Pretending the missing time
        // was a tidy number of periods would file the sample far from where it belongs.
        SnapshotTimingState state = SnapshotTimingState.Fresh.Accept(1000, 1000);
        long arrival = 1000 + SnapshotTimingRule.ResyncGapMs + 1;

        Assert.Equal(arrival, SnapshotTimingRule.Assign(state, arrival, Period));
    }

    [Fact]
    public void ATimelineThatHasDriftedTooFarIsRePeggedToTheArrival()
    {
        // The quantiser rounds, and a sender on a genuinely different rate rounds the same way
        // every time. Left alone that is unbounded latency; the drift bound converts it into
        // one re-peg and a fresh start.
        SnapshotTimingState state =
            SnapshotTimingState.Fresh.Accept(1000, 1000 + SnapshotTimingRule.MaxDriftMs + 100);

        Assert.Equal(1033, SnapshotTimingRule.Assign(state, 1033, Period));
    }

    [Fact]
    public void ANonPositivePeriodDisablesRegularisation()
    {
        SnapshotTimingState state = SnapshotTimingState.Fresh.Accept(1000, 1000);

        Assert.Equal(1010, SnapshotTimingRule.Assign(state, 1010, 0));
    }

    [Fact]
    public void ABackwardsArrivalNeverProducesANonAdvancingStamp()
    {
        SnapshotTimingState state = SnapshotTimingState.Fresh.Accept(1000, 1000);

        Assert.True(SnapshotTimingRule.Assign(state, 990, Period) > 1000);
    }

    [Fact]
    public void AnIdenticalStateIsNotAnObservation()
    {
        Assert.False(SnapshotTimingRule.ShouldAccept(At(5f), At(5f)));
    }

    [Fact]
    public void AChangedStateIsAnObservation()
    {
        Assert.True(SnapshotTimingRule.ShouldAccept(At(5f), At(5.01f)));
    }

    [Fact]
    public void AStateDifferingOnlyInANonPositionalFieldIsStillAnObservation()
    {
        // Health, flags and the one-shot animation sequence all matter downstream, and a peer
        // who drew a weapon without moving must not be filtered out as "nothing new".
        PlayerState still = At(5f);

        Assert.True(SnapshotTimingRule.ShouldAccept(still, still with { Health = 90 }));
        Assert.True(SnapshotTimingRule.ShouldAccept(still, still with { OneShotSequence = 3 }));
    }

    [Fact]
    public void ARunOfJitteredArrivalsProducesAPerfectlyEvenTimeline()
    {
        // The end-to-end property this rule exists for. Arrivals alternate 17/49 ms — the
        // beat between two free-running 30 Hz timers, plus Windows' own timer granularity —
        // and the timeline they produce must still be exactly one period apart every time,
        // because that is what makes the interpolated velocity the peer's real velocity.
        SnapshotTimingState state = SnapshotTimingState.Fresh.Accept(1000, 1000);
        long arrival = 1000;
        List<long> assigned = [];

        for (int i = 0; i < 12; i++)
        {
            arrival += (i % 2 == 0) ? 17 : 49;
            long stamp = SnapshotTimingRule.Assign(state, arrival, Period);
            assigned.Add(stamp);
            state = state.Accept(arrival, stamp);
        }

        for (int i = 1; i < assigned.Count; i++)
        {
            Assert.Equal(Period, assigned[i] - assigned[i - 1]);
        }
    }
}

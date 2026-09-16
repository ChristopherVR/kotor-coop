using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="NetworkSim"/> is the fault injector every channel's send/receive path consults
/// (loss, latency/jitter, blackout). Its loss and jitter decisions read a <see cref="Random"/>
/// — production always gets the thread-safe <see cref="Random.Shared"/>, unchanged from before
/// this test file's constructor overload existed, but every test here that needs a boundary
/// exactly right (not just "probably") injects <see cref="FixedRandom"/> instead, so a 50%-loss
/// coin flip does not depend on which side of 0.5 the real RNG happens to land on this run.
///
/// Blackout deadlines are the one thing this class times against the REAL
/// <see cref="SessionClock"/> (<c>StartBlackout</c> takes a duration, not a deadline), so the
/// blackout tests use short real sleeps with generous margins rather than an injected clock —
/// every other decision here takes its own explicit <c>nowMs</c> and needs no such margin.
/// </summary>
public class NetworkSimTests
{
    /// <summary>A <see cref="Random"/> that always answers with the same fixed values, so a
    /// loss/jitter decision that depends on "the next random draw" is pinned exactly rather
    /// than merely bounded.</summary>
    private sealed class FixedRandom : Random
    {
        private readonly double _nextDouble;
        private readonly int _next;

        public FixedRandom(double nextDouble, int next = 0)
        {
            _nextDouble = nextDouble;
            _next = next;
        }

        public override double NextDouble() => _nextDouble;

        public override int Next(int maxValue) => _next;
    }

    // ---- Off by default, free when off -----------------------------------------------------

    [Fact]
    public void ANewSimIsUnarmedAndDropsNothing()
    {
        NetworkSim sim = new();

        Assert.False(sim.Armed);
        Assert.False(sim.DropOutbound(0));
        Assert.False(sim.DropInbound(0));
        Assert.Equal(0, sim.DelayMs());
        Assert.Equal("netsim off (clean link)", sim.Describe());
    }

    [Fact]
    public void SettingZeroLossLeavesTheSimIdle()
    {
        NetworkSim sim = new();
        sim.SetLoss(0);

        Assert.False(sim.Armed);
    }

    [Fact]
    public void SettingZeroLatencyAndJitterLeavesTheSimIdle()
    {
        NetworkSim sim = new();
        sim.SetLatency(0, 0);

        Assert.False(sim.Armed);
    }

    // ---- Loss --------------------------------------------------------------------------

    [Fact]
    public void SettingLossArmsTheSim()
    {
        NetworkSim sim = new();
        sim.SetLoss(50);

        Assert.True(sim.Armed);
    }

    [Fact]
    public void ADrawJustUnderTheLossThresholdDrops()
    {
        // loss=50: a draw of 0.49 -> 49 < 50 -> dropped.
        NetworkSim sim = new(new FixedRandom(0.49));
        sim.SetLoss(50);

        Assert.True(sim.DropOutbound(0));
    }

    [Fact]
    public void ADrawExactlyAtTheLossThresholdDoesNotDrop()
    {
        // loss=50: a draw of 0.50 -> 50 < 50 is false -> not dropped (strictly less-than).
        NetworkSim sim = new(new FixedRandom(0.50));
        sim.SetLoss(50);

        Assert.False(sim.DropOutbound(0));
    }

    [Fact]
    public void HundredPercentLossAlwaysDropsRegardlessOfTheDraw()
    {
        // NextDouble() is documented [0, 1), so even the largest possible draw (just under 1)
        // times 100 is still just under 100 - always < 100.
        NetworkSim sim = new(new FixedRandom(0.999999));
        sim.SetLoss(100);

        Assert.True(sim.DropOutbound(0));
        Assert.True(sim.DropInbound(0));
    }

    [Fact]
    public void LossPercentIsClampedToOneHundred()
    {
        NetworkSim sim = new();
        sim.SetLoss(150);

        Assert.Contains("loss 100%", sim.Describe());
    }

    [Fact]
    public void LossPercentIsClampedToZero()
    {
        NetworkSim sim = new();
        sim.SetLoss(-10);

        // Clamped to 0, which is indistinguishable from never having called SetLoss.
        Assert.False(sim.Armed);
        Assert.Equal("netsim off (clean link)", sim.Describe());
    }

    [Fact]
    public void DroppedCountersTrackEachDirectionSeparately()
    {
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.SetLoss(100);

        sim.DropOutbound(0);
        sim.DropOutbound(0);
        sim.DropInbound(0);

        Assert.Equal(2, sim.DroppedOutbound);
        Assert.Equal(1, sim.DroppedInbound);
    }

    // ---- Latency / jitter ----------------------------------------------------------------

    [Fact]
    public void FlatLatencyWithNoJitterIsExact()
    {
        NetworkSim sim = new();
        sim.SetLatency(100, 0);

        Assert.Equal(100, sim.DelayMs());
        Assert.Equal(100, sim.DelayMs());
    }

    [Fact]
    public void JitterAddsExactlyWhatTheDrawProduces()
    {
        // jitter draws Next(jitterMs + 1); a fixed draw of 20 must add exactly 20, every call.
        NetworkSim sim = new(new FixedRandom(nextDouble: 0, next: 20));
        sim.SetLatency(100, jitterMs: 50);

        Assert.Equal(120, sim.DelayMs());
    }

    [Fact]
    public void JitterStaysWithinLatencyToLatencyPlusJitterOverManyDraws()
    {
        // A real (unseeded-in-spirit but still bounded) draw sequence: the property under test
        // is the RANGE, which holds regardless of which values the RNG actually produces, so
        // this does not need a fixed random to be non-flaky.
        NetworkSim sim = new();
        sim.SetLatency(100, jitterMs: 50);

        for (int i = 0; i < 200; i++)
        {
            int delay = sim.DelayMs();
            Assert.InRange(delay, 100, 150);
        }
    }

    [Fact]
    public void ZeroDelayIsNotCountedAsDelayed()
    {
        NetworkSim sim = new();

        Assert.Equal(0, sim.DelayMs());
        Assert.Equal(0, sim.DelayedPackets);
    }

    [Fact]
    public void EveryNonZeroDelayIncrementsTheCounter()
    {
        NetworkSim sim = new();
        sim.SetLatency(50);

        sim.DelayMs();
        sim.DelayMs();

        Assert.Equal(2, sim.DelayedPackets);
    }

    [Fact]
    public void LatencyAndJitterAreClampedToSixtySeconds()
    {
        NetworkSim sim = new();
        sim.SetLatency(70_000, 70_000);

        Assert.Contains("latency 60000ms +/-60000ms", sim.Describe());
    }

    [Fact]
    public void NegativeLatencyAndJitterClampToZeroAndLeaveTheSimIdle()
    {
        NetworkSim sim = new();
        sim.SetLatency(-5, -5);

        Assert.False(sim.Armed);
    }

    // ---- Direction -----------------------------------------------------------------------

    [Fact]
    public void OutboundOnlyLossNeverDropsInbound()
    {
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.StartBlackout(0, NetSimDirection.Outbound); // sets Direction without an active window
        sim.SetLoss(100);

        Assert.True(sim.DropOutbound(1_000));
        Assert.False(sim.DropInbound(1_000));
    }

    [Fact]
    public void InboundOnlyLossNeverDropsOutbound()
    {
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.StartBlackout(0, NetSimDirection.Inbound);
        sim.SetLoss(100);

        Assert.True(sim.DropInbound(1_000));
        Assert.False(sim.DropOutbound(1_000));
    }

    [Fact]
    public void ADirectionalBlackoutOnlyTouchesItsOwnHalf()
    {
        NetworkSim sim = new();
        sim.StartBlackout(5_000, NetSimDirection.Outbound);

        long now = SessionClock.NowMs;
        Assert.True(sim.DropOutbound(now));
        Assert.False(sim.DropInbound(now));

        sim.StartBlackout(5_000, NetSimDirection.Inbound);
        Assert.False(sim.DropOutbound(now));
        Assert.True(sim.DropInbound(now));
    }

    [Fact]
    public void DefaultDirectionIsBothWays()
    {
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.SetLoss(100);

        Assert.True(sim.DropOutbound(0));
        Assert.True(sim.DropInbound(0));
    }

    // ---- Blackout (timed against the real SessionClock) ----------------------------------

    [Fact]
    public void BlackoutDropsBothDirectionsUntilItExpires()
    {
        NetworkSim sim = new();
        long start = SessionClock.NowMs;
        sim.StartBlackout(200);

        Assert.True(sim.DropOutbound(start));
        Assert.True(sim.DropInbound(start));

        Thread.Sleep(300); // well past the 200ms window, generous margin against scheduling jitter
        long later = SessionClock.NowMs;

        Assert.False(sim.DropOutbound(later));
        Assert.False(sim.DropInbound(later));
    }

    [Fact]
    public void EndBlackoutEndsEarlyButLeavesLossAlone()
    {
        NetworkSim sim = new(new FixedRandom(0.0)); // always "unlucky": drops whenever loss > 0
        sim.SetLoss(50);
        sim.StartBlackout(10_000);

        long now = SessionClock.NowMs;
        Assert.True(sim.DropOutbound(now)); // the blackout, not the loss, is what drops here

        sim.EndBlackout();

        // Still dropped - now because of the loss setting, which EndBlackout does not touch.
        Assert.True(sim.DropOutbound(now));
        Assert.True(sim.Armed);
    }

    [Fact]
    public void ArmedStaysTrueUntilAPacketIsJudgedAfterEverythingLapses()
    {
        // ⚠ Documented laziness: Armed does not clear itself the instant a blackout's deadline
        // passes - only the next Drop* call that actually looks at the (now-idle) rules flips
        // it, since there is deliberately no timer here.
        NetworkSim sim = new();
        sim.StartBlackout(50);

        Assert.True(sim.Armed);

        Thread.Sleep(150);
        Assert.True(sim.Armed); // still true: nothing has judged a packet since it lapsed

        sim.DropOutbound(SessionClock.NowMs); // judges it, finds everything idle, clears the gate
        Assert.False(sim.Armed);
    }

    // ---- Clear -------------------------------------------------------------------------

    [Fact]
    public void ClearReturnsToAPerfectLinkImmediately()
    {
        NetworkSim sim = new();
        sim.SetLoss(50);
        sim.SetLatency(100, 20);
        sim.StartBlackout(10_000);

        sim.Clear();

        Assert.False(sim.Armed);
        Assert.Equal(0, sim.DelayMs());
        Assert.False(sim.DropOutbound(SessionClock.NowMs));
        Assert.Equal("netsim off (clean link)", sim.Describe());
    }

    [Fact]
    public void ClearResetsRulesButNotTheCumulativeCounters()
    {
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.SetLoss(100);
        sim.DropOutbound(0);

        sim.Clear();

        // The counters are a running total for the whole session ("dropped X out / Y in" in
        // Describe()) - Clear only resets what governs FUTURE decisions.
        Assert.Equal(1, sim.DroppedOutbound);
    }

    // ---- Describe --------------------------------------------------------------------------

    [Fact]
    public void DescribeReportsLossAndDroppedTotals()
    {
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.SetLoss(50);
        sim.DropOutbound(0);

        string description = sim.Describe();

        Assert.Contains("loss 50%", description);
        Assert.Contains("both ways", description);
        Assert.Contains("dropped 1 out / 0 in", description);
    }

    [Fact]
    public void DescribeReportsLatencyAndJitter()
    {
        NetworkSim sim = new();
        sim.SetLatency(80, 20);

        Assert.Contains("latency 80ms +/-20ms", sim.Describe());
    }

    [Fact]
    public void DescribeReportsAnActiveBlackoutWithItsDirection()
    {
        NetworkSim sim = new();
        sim.StartBlackout(10_000, NetSimDirection.Inbound);

        string description = sim.Describe();

        Assert.Matches(@"blackout \d+\.\d+s left \(inbound\)", description);
    }

    [Fact]
    public void DescribeShowsOutboundDirectionLabel()
    {
        NetworkSim sim = new();
        sim.StartBlackout(10_000, NetSimDirection.Outbound);

        Assert.Contains("(outbound)", sim.Describe());
    }

    // ---- Deterministic loss pattern ---------------------------------------------------------
    //
    // Why this exists at all: a percentage is a coin flip with an unbounded tail, and a test
    // that runs on every push cannot have one. See NetworkSim.SetLossPattern.

    [Fact]
    public void ALossPatternDropsExactlyItsShareOfEveryWindow()
    {
        NetworkSim sim = new();
        sim.SetLossPattern(3, 10);

        int dropped = 0;
        for (int i = 0; i < 100; i++)
        {
            if (sim.DropOutbound(0))
            {
                dropped++;
            }
        }

        // Exactly, not approximately: three in every ten, a hundred times.
        Assert.Equal(30, dropped);
    }

    [Fact]
    public void ALossPatternNeverDropsMoreThanItsWindowShareInARow()
    {
        NetworkSim sim = new();
        sim.SetLossPattern(3, 10);

        int run = 0;
        int worstRun = 0;
        for (int i = 0; i < 1000; i++)
        {
            run = sim.DropOutbound(0) ? run + 1 : 0;
            worstRun = Math.Max(worstRun, run);
        }

        // THE WHOLE POINT. A 30% coin flip will, given enough draws, eat ten in a row and with
        // them every retransmission of one op inside a test's patience. A pattern cannot.
        Assert.Equal(3, worstRun);
    }

    [Fact]
    public void EachDirectionCountsItsOwnWindow()
    {
        NetworkSim sim = new();
        sim.SetLossPattern(1, 2);

        // Interleaving the two directions must not let one direction's counter advance the
        // other's — each half of the link eats every second datagram OF ITS OWN.
        Assert.True(sim.DropOutbound(0));
        Assert.True(sim.DropInbound(0));
        Assert.False(sim.DropOutbound(0));
        Assert.False(sim.DropInbound(0));
        Assert.True(sim.DropOutbound(0));
        Assert.True(sim.DropInbound(0));
    }

    [Fact]
    public void APatternWinsOverAPercentageSoTheTwoCannotStack()
    {
        // A FixedRandom that would drop everything if the percentage were still consulted.
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.SetLoss(90);
        sim.SetLossPattern(1, 4);

        int dropped = 0;
        for (int i = 0; i < 40; i++)
        {
            if (sim.DropOutbound(0))
            {
                dropped++;
            }
        }

        Assert.Equal(10, dropped);
        Assert.Contains("1-in-4 fixed pattern", sim.Describe());
    }

    [Fact]
    public void ClearingThePatternGoesBackToACleanLink()
    {
        NetworkSim sim = new();
        sim.SetLossPattern(1, 2);
        Assert.True(sim.Armed);

        sim.SetLossPattern(0, 2);

        Assert.False(sim.DropOutbound(0));
        Assert.Equal("netsim off (clean link)", sim.Describe());
    }

    [Fact]
    public void ABlackoutStillBeatsAPattern()
    {
        NetworkSim sim = new();
        sim.SetLossPattern(1, 10);
        sim.StartBlackout(10_000);

        // Ten in a row, which the pattern on its own could never produce.
        for (int i = 0; i < 10; i++)
        {
            Assert.True(sim.DropOutbound(SessionClock.NowMs));
        }
    }

    // ---- A blackout eats what is already in flight -------------------------------------------

    [Fact]
    public void BlackedOutIsTrueOnlyWhileTheWindowIsOpen()
    {
        NetworkSim sim = new();

        Assert.False(sim.BlackedOut(SessionClock.NowMs, outbound: true));

        sim.StartBlackout(10_000);
        Assert.True(sim.BlackedOut(SessionClock.NowMs, outbound: true));
        Assert.True(sim.BlackedOut(SessionClock.NowMs, outbound: false));

        sim.EndBlackout();
        Assert.False(sim.BlackedOut(SessionClock.NowMs, outbound: true));
    }

    [Fact]
    public void BlackedOutFollowsTheDirectionAndIgnoresPlainLoss()
    {
        // A FixedRandom that would lose every packet if this consulted the loss rule at all —
        // it must not: its only question is "is the cable out right now", asked about a
        // datagram whose loss draw already happened. See NetworkSim.BlackedOut.
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.SetLoss(100);

        Assert.False(sim.BlackedOut(SessionClock.NowMs, outbound: true));

        sim.StartBlackout(10_000, NetSimDirection.Outbound);

        Assert.True(sim.BlackedOut(SessionClock.NowMs, outbound: true));
        Assert.False(sim.BlackedOut(SessionClock.NowMs, outbound: false));

        // ...and it counts nothing: the drop it represents was already counted when the
        // datagram was first judged.
        Assert.Equal(0, sim.DroppedOutbound);
        Assert.Equal(0, sim.DroppedInbound);
    }

    // ---- Duplication -------------------------------------------------------------------------

    [Fact]
    public void DuplicationIsOffUntilAskedFor()
    {
        NetworkSim sim = new(new FixedRandom(0.0));

        Assert.False(sim.DuplicateOutbound());
        Assert.Equal(0, sim.DuplicatedPackets);
    }

    [Fact]
    public void DuplicationFiresBelowItsThresholdAndIsCounted()
    {
        NetworkSim sim = new(new FixedRandom(0.1));
        sim.SetDuplication(20);

        Assert.True(sim.Armed);
        Assert.True(sim.DuplicateOutbound());
        Assert.True(sim.DuplicateOutbound());
        Assert.Equal(2, sim.DuplicatedPackets);
        Assert.Contains("duplicate 20%", sim.Describe());
    }

    [Fact]
    public void DuplicationDoesNotFireAtOrAboveItsThreshold()
    {
        NetworkSim sim = new(new FixedRandom(0.2));
        sim.SetDuplication(20);

        Assert.False(sim.DuplicateOutbound());
        Assert.Equal(0, sim.DuplicatedPackets);
    }

    [Fact]
    public void DuplicationFollowsTheConfiguredDirection()
    {
        NetworkSim sim = new(new FixedRandom(0.0));
        sim.SetDuplication(50);
        sim.StartBlackout(0, NetSimDirection.Inbound);

        // Duplication is an outbound-only effect (UdpChannel.Send is its only caller), so a
        // rule scoped to the inbound half must leave it alone rather than duplicating anyway.
        Assert.False(sim.DuplicateOutbound());
    }
}

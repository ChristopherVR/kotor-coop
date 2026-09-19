using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="StuntLegFollowRule"/> — the pure decision behind replaying a leader's scripted
/// leg into a <c>STUNT_*</c> module on the follower's own engine (docs/research/cinematics.md,
/// 2026-09-18 section). The pump integration (the replay request, the movie that rides it, the
/// hold inside our own leg, the echo de-dup, the chain) lives in <c>TravelPumpTests</c>'s
/// stunt-leg section.
/// </summary>
public sealed class StuntLegFollowRuleTests
{
    private static StuntLegFollowPath Decide(
        bool enabled = true, bool leaderInitiated = true, bool alreadyInThisLeg = false,
        bool inDialogue = false, bool inCombat = false, bool midTransition = false, long ageMs = 0) =>
        StuntLegFollowRule.Decide(enabled, leaderInitiated, alreadyInThisLeg, inDialogue, inCombat, midTransition, ageMs);

    [Fact]
    public void AFreshEligibleLegOnAQuietEngineIsReplayed()
    {
        Assert.Equal(StuntLegFollowPath.Replay, Decide());
    }

    /// <summary>The opt-out restores the pre-2026-09-18 refusal exactly.</summary>
    [Fact]
    public void TheFlagOffRefuses()
    {
        Assert.Equal(StuntLegFollowPath.Refuse, Decide(enabled: false));
    }

    /// <summary>The one case the refusal is still right for: the leader's published module
    /// reads STUNT_* but nobody's own engine announced STARTING the leg.</summary>
    [Fact]
    public void ALegNobodyAnnouncedStartingIsRefused()
    {
        Assert.Equal(StuntLegFollowPath.Refuse, Decide(leaderInitiated: false));
    }

    /// <summary>Refusal outranks every other answer — a disabled feature never waits or
    /// drops, it simply behaves as before.</summary>
    [Fact]
    public void RefusalOutranksBusyAndStale()
    {
        Assert.Equal(StuntLegFollowPath.Refuse, Decide(enabled: false, inCombat: true, ageMs: 999_999));
    }

    /// <summary>Our own click, our own replay, a later hop of a chain we are inside, or the
    /// echo of our own warp coming back off a peer: all "already in it", all dropped.</summary>
    [Fact]
    public void ALegThisEngineIsAlreadyInsideIsDropped()
    {
        Assert.Equal(StuntLegFollowPath.Drop, Decide(alreadyInThisLeg: true));
    }

    [Fact]
    public void AStaleLegIsDropped()
    {
        Assert.Equal(StuntLegFollowPath.Drop, Decide(ageMs: StuntLegFollowRule.ExpiryMs + 1));
        Assert.Equal(StuntLegFollowPath.Replay, Decide(ageMs: StuntLegFollowRule.ExpiryMs));
    }

    /// <summary>Dropping outranks waiting: a leg we are already inside is spent even while
    /// busy, never kept to fire later.</summary>
    [Fact]
    public void DroppingOutranksWaiting()
    {
        Assert.Equal(StuntLegFollowPath.Drop, Decide(alreadyInThisLeg: true, inCombat: true));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ABusyEngineWaits(bool inDialogue, bool inCombat, bool midTransition)
    {
        Assert.Equal(
            StuntLegFollowPath.Wait,
            Decide(inDialogue: inDialogue, inCombat: inCombat, midTransition: midTransition));
    }

    // ---- holding follows inside our own leg --------------------------------------------

    [Fact]
    public void FollowsAreHeldInsideAnActiveLegWhileLocalIsAStuntModule()
    {
        Assert.True(StuntLegFollowRule.ShouldHoldFollowsInOwnLeg(legActive: true, localInStuntModule: true, hopAgeMs: 0));
    }

    /// <summary>The replay is still loading (local module is the source, or the fallback
    /// name): the ordinary guards cover that window; the hold is only for INSIDE the scene.</summary>
    [Fact]
    public void NoHoldOutsideAStuntModule()
    {
        Assert.False(StuntLegFollowRule.ShouldHoldFollowsInOwnLeg(legActive: true, localInStuntModule: false, hopAgeMs: 0));
    }

    /// <summary>A stunt module reached with no leg of our own (an agent attached mid-scene
    /// before the observer has run) does not hold on its own; the observer starts a leg for it
    /// on the same tick, which is the pump's job, not this rule's.</summary>
    [Fact]
    public void NoHoldWithoutAnActiveLeg()
    {
        Assert.False(StuntLegFollowRule.ShouldHoldFollowsInOwnLeg(legActive: false, localInStuntModule: true, hopAgeMs: 0));
    }

    /// <summary>The safety valve: a hop that never exits here releases the follows after the
    /// window, so a scene keyed on an unsynced global cannot strand this player forever.</summary>
    [Fact]
    public void AHopThatOverrunsItsWindowReleasesTheHold()
    {
        Assert.True(StuntLegFollowRule.ShouldHoldFollowsInOwnLeg(true, true, StuntLegFollowRule.HopWindowMs));
        Assert.False(StuntLegFollowRule.ShouldHoldFollowsInOwnLeg(true, true, StuntLegFollowRule.HopWindowMs + 1));
    }

    /// <summary>The numbers the doc quotes: a minute for a follower to free up, five per hop.</summary>
    [Fact]
    public void TheWindowsAreWhatTheDocSays()
    {
        Assert.Equal(60_000, StuntLegFollowRule.ExpiryMs);
        Assert.Equal(300_000, StuntLegFollowRule.HopWindowMs);
    }
}

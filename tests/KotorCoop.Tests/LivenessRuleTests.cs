using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The reconcile decision table for creature-liveness reconciliation
/// (docs/research/creature-liveness.md): given what the enemy owner's engine reports for one
/// creature and what this instance's own engine has for the same id, what should happen.
/// </summary>
public class LivenessRuleTests
{
    [Theory]
    [InlineData(0x007470C8, 300, 300, false, false, true)]
    [InlineData(0x007470C8, 0x8000012Cu, 300, false, false, true)]
    [InlineData(0x00748AD0, 300, 300, false, false, false)] // reused waypoint id
    [InlineData(0x007470C8, 301, 300, false, false, false)]
    [InlineData(0x007470C8, 300, 300, true, false, false)]
    [InlineData(0x007470C8, 300, 300, false, true, false)]
    public void ReusedObjectIdCannotReconcileASeparateBody(uint vtable, uint actual, uint expected,
        bool player, bool phantom, bool allowed) =>
        Assert.Equal(allowed, LivenessRule.CanReconcileBody(vtable, actual, expected, player, phantom));

    [Fact]
    public void ReusedMaterialisedIdCannotDestroyOrDressAReplacementCreature()
    {
        Assert.False(LivenessRule.IsCurrentMaterialisedBody((nint)0x10000, (nint)0x20000, 0x007470C8, 300, 300));
        Assert.True(LivenessRule.IsCurrentMaterialisedBody((nint)0x10000, (nint)0x10000, 0x007470C8, 300, 300));
        Assert.False(LivenessRule.IsCurrentMaterialisedBody((nint)0x10000, (nint)0x10000, 0x00748AD0, 300, 300));
    }

    [Fact]
    public void TemporaryNullLookupRetainsBirthGraceButPositiveReplacementDoesNot()
    {
        Assert.False(LivenessRule.IsMaterialisedReplacement((nint)0x10000, 0, 0, 0, 300));
        Assert.False(LivenessRule.IsMaterialisedReplacement((nint)0x10000, (nint)0x10000, 0x007470C8, 300, 300));
        Assert.True(LivenessRule.IsMaterialisedReplacement((nint)0x10000, (nint)0x20000, 0x007470C8, 300, 300));
        Assert.True(LivenessRule.IsMaterialisedReplacement((nint)0x10000, (nint)0x10000, 0x00748AD0, 300, 300));
    }

    [Fact]
    public void ClearedReplacementMappingCannotFallBackToOwnerIdAndDestroyNewCreature()
    {
        MaterialisedOwnerRejections rejected = new();
        const uint owner = 300;
        Assert.Equal(301u, rejected.Resolve(owner, 301));
        Assert.True(LivenessRule.IsMaterialisedReplacement((nint)0x10000, (nint)0x20000, 0x007470C8, 301, 301));
        rejected.Reject(owner); // registry clears its mapping after that positive replacement
        uint mapped = rejected.Resolve(owner, 0);
        uint effective = mapped != 0 ? mapped : owner; // actual consumer fallback selection
        Assert.Equal(MaterialisedOwnerRejections.InvalidObject, effective);
        Assert.False(LivenessRule.CanReconcileBody(0x007470C8, owner, effective, false, false));
        Assert.Equal(LivenessAction.Nothing, LivenessRule.Decide(LivenessHostState.Gone,
            localResolved: false, localDead: false, localPlot: false));
        Assert.Equal(0u, rejected.Resolve(302, 0));
        rejected.AdmitFreshBody(owner);
        Assert.Equal(303u, rejected.Resolve(owner, 303));
    }

    [Fact]
    public void RejectedOwnerGuardSurvivesRepeatedReadsUntilWorldInvalidation()
    {
        MaterialisedOwnerRejections rejected = new();
        rejected.Reject(65535);
        Assert.Equal(MaterialisedOwnerRejections.InvalidObject, rejected.Resolve(65535, 0));
        Assert.Equal(MaterialisedOwnerRejections.InvalidObject, rejected.Resolve(65535, 0));
        rejected.Clear();
        Assert.Equal(0u, rejected.Resolve(65535, 0));
    }
    [Fact]
    public void BothSidesAliveIsNothing()
    {
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(LivenessHostState.Alive, localResolved: true, localDead: false, localPlot: false));
    }

    [Fact]
    public void BothSidesDeadIsNothing()
    {
        // The owner already killed it and this instance already killed its own mirrored
        // copy through the ordinary census path — nothing left for this channel to do.
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(LivenessHostState.Dead, localResolved: true, localDead: true, localPlot: false));
    }

    [Fact]
    public void OwnerDeadLocalAliveIsAKill()
    {
        // The exact case the user reported: a corpse on the owner and a live body here.
        Assert.Equal(
            LivenessAction.Kill,
            LivenessRule.Decide(LivenessHostState.Dead, localResolved: true, localDead: false, localPlot: false));
    }

    [Fact]
    public void OwnerDeadLocalUnresolvedIsNothing()
    {
        // Not resolvable here at all reads the same as "already gone" for this purpose —
        // there is no body left to kill.
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(LivenessHostState.Dead, localResolved: false, localDead: false, localPlot: false));
    }

    [Fact]
    public void OwnerDeadLocalAlivePlotIsLeftAlone()
    {
        // Un-killable by the module's own rule — CombatController never touches a plot
        // creature's health either, and this channel must not be the back door that does.
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(LivenessHostState.Dead, localResolved: true, localDead: false, localPlot: true));
    }

    [Fact]
    public void OwnerGoneLocalStillStandingIsADestroy()
    {
        Assert.Equal(
            LivenessAction.Destroy,
            LivenessRule.Decide(LivenessHostState.Gone, localResolved: true, localDead: false, localPlot: false));
    }

    [Fact]
    public void OwnerGoneLocalStillStandingDeadIsAlsoADestroy()
    {
        // A corpse the owner has fully destroyed (not merely killed) must not linger as a
        // corpse here either — DestroyObject removes the body, it does not leave one.
        Assert.Equal(
            LivenessAction.Destroy,
            LivenessRule.Decide(LivenessHostState.Gone, localResolved: true, localDead: true, localPlot: false));
    }

    [Fact]
    public void OwnerGoneLocalAlreadyUnresolvedIsNothing()
    {
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(LivenessHostState.Gone, localResolved: false, localDead: false, localPlot: false));
    }

    [Fact]
    public void OwnerGonePlotStillDestroys()
    {
        // Un-killable is not the same claim as un-destroyable: the module itself removed the
        // actor (a script's own DestroyObject), which is the module's business either way.
        Assert.Equal(
            LivenessAction.Destroy,
            LivenessRule.Decide(LivenessHostState.Gone, localResolved: true, localDead: false, localPlot: true));
    }

    [Fact]
    public void OwnerAliveLocalDeadIsAnAnomaly()
    {
        Assert.Equal(
            LivenessAction.Anomaly,
            LivenessRule.Decide(LivenessHostState.Alive, localResolved: true, localDead: true, localPlot: false));
    }

    [Fact]
    public void OwnerAliveLocalUnresolvedIsAlsoAnAnomaly()
    {
        // "Gone" and "dead" both read as "not alive" here — the owner disagrees either way.
        Assert.Equal(
            LivenessAction.Anomaly,
            LivenessRule.Decide(LivenessHostState.Alive, localResolved: false, localDead: false, localPlot: false));
    }

    // DestroyObjectById is not synchronous (docs/research/creature-liveness.md's 2026-08-24
    // disassembly round: the engine schedules a delayed removal rather than unlinking the
    // object array entry immediately), so a body asked to be destroyed can keep resolving here
    // for a pass or several afterwards. destroyAlreadyRequested is how the controller tells
    // Decide "I already asked once" so it is not asked again every throttled pass while the
    // engine's own removal is still pending — see LivenessReconcileController's own
    // _destroyRequested latch.

    [Fact]
    public void OwnerGoneLocalStillStandingWithNoPriorRequestIsStillADestroy()
    {
        Assert.Equal(
            LivenessAction.Destroy,
            LivenessRule.Decide(
                LivenessHostState.Gone, localResolved: true, localDead: false, localPlot: false,
                destroyAlreadyRequested: false));
    }

    [Fact]
    public void OwnerGoneLocalStillStandingButAlreadyRequestedIsNothing()
    {
        // The pending-removal window: do not pile up a second delayed destroy on top of one
        // already accepted by the engine.
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(
                LivenessHostState.Gone, localResolved: true, localDead: false, localPlot: false,
                destroyAlreadyRequested: true));
    }

    [Fact]
    public void OwnerGoneLocalDeadButAlreadyRequestedIsAlsoNothing()
    {
        // Same latch applies to the corpse-still-standing Gone case, not just the live one.
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(
                LivenessHostState.Gone, localResolved: true, localDead: true, localPlot: false,
                destroyAlreadyRequested: true));
    }

    [Fact]
    public void OwnerGonePlotAlreadyRequestedIsAlsoNothing()
    {
        // The latch is keyed on "did we already ask", independent of the plot exemption that
        // governs whether Destroy applied in the first place.
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(
                LivenessHostState.Gone, localResolved: true, localDead: false, localPlot: true,
                destroyAlreadyRequested: true));
    }

    [Fact]
    public void OwnerGoneLocalAlreadyUnresolvedIsNothingRegardlessOfThePriorRequestFlag()
    {
        // Already gone here too — the latch is irrelevant once there is nothing left to
        // destroy, and a stale "already requested" flag must not matter once the body has
        // genuinely disappeared (this is also what lets the flag be forgotten safely: once
        // localResolved goes false, this branch is reached instead of the latch check).
        Assert.Equal(
            LivenessAction.Nothing,
            LivenessRule.Decide(
                LivenessHostState.Gone, localResolved: false, localDead: false, localPlot: false,
                destroyAlreadyRequested: true));
    }
}

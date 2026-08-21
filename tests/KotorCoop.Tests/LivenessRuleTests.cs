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
}

using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ThreatRetargetRule.Decide"/> is the pure decision at the heart of
/// THREAT_RETARGET (docs/research/peer-damage-ledger.md §9) — every branch is a static
/// function of its arguments, so each is pinned here directly rather than through
/// <see cref="PeerDamageController"/>'s own engine-dependent call site.
/// </summary>
public sealed class ThreatRetargetRuleTests
{
    private static readonly ThreatRetargetOptions Options = new(
        MarginRatio: 1.5, MinThreatToRetarget: 15.0, CooldownSeconds: 4.0);

    [Theory]
    [InlineData(0u)]
    [InlineData(0x7F000000u)] // Odyssey.ObjectInvalid
    public void NoLiveCurrentTargetMeansNotInCombat(uint currentTargetId)
    {
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId, topAttackerId: 0x100, topAttackerThreat: 100,
            currentTargetThreat: 0, secondsSinceLastRetarget: 999, Options);

        Assert.Equal(ThreatRetargetDecision.NotInCombat, decision);
    }

    [Fact]
    public void NoTrackedAttackerAtAllRefuses()
    {
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x50, topAttackerId: 0, topAttackerThreat: 0,
            currentTargetThreat: 0, secondsSinceLastRetarget: 999, Options);

        Assert.Equal(ThreatRetargetDecision.NoTopAttacker, decision);
    }

    [Fact]
    public void TopAttackerAlreadyBeingTheCurrentTargetRefuses()
    {
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x100, topAttackerId: 0x100, topAttackerThreat: 500,
            currentTargetThreat: 0, secondsSinceLastRetarget: 999, Options);

        Assert.Equal(ThreatRetargetDecision.AlreadyTarget, decision);
    }

    [Fact]
    public void BelowTheAbsoluteFloorRefusesEvenWithNoCompetingThreat()
    {
        // The current target's own threat is untracked (0, the common case — see
        // ThreatTable's own remarks), so a ratio check alone would pass on almost any hit;
        // the absolute floor is what actually stops a single glancing blow.
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x50, topAttackerId: 0x100, topAttackerThreat: 14.9,
            currentTargetThreat: 0, secondsSinceLastRetarget: 999, Options);

        Assert.Equal(ThreatRetargetDecision.BelowMinimum, decision);
    }

    [Fact]
    public void BelowTheMarginOverAKnownCurrentTargetRefuses()
    {
        // Current target itself has tracked threat (e.g. it is also a watched attacker id) of
        // 20; 1.5x that is 30, and the top attacker's 29 does not clear it even though it is
        // comfortably above the absolute floor.
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x50, topAttackerId: 0x100, topAttackerThreat: 29,
            currentTargetThreat: 20, secondsSinceLastRetarget: 999, Options);

        Assert.Equal(ThreatRetargetDecision.BelowMargin, decision);
    }

    [Fact]
    public void ExactlyAtTheMarginIsEnough()
    {
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x50, topAttackerId: 0x100, topAttackerThreat: 30,
            currentTargetThreat: 20, secondsSinceLastRetarget: 999, Options);

        Assert.Equal(ThreatRetargetDecision.Retarget, decision);
    }

    [Fact]
    public void OnCooldownRefusesEvenWhenEveryOtherTestPasses()
    {
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x50, topAttackerId: 0x100, topAttackerThreat: 100,
            currentTargetThreat: 0, secondsSinceLastRetarget: 3.9, Options);

        Assert.Equal(ThreatRetargetDecision.OnCooldown, decision);
    }

    [Fact]
    public void ExactlyAtTheCooldownBoundaryIsAllowed()
    {
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x50, topAttackerId: 0x100, topAttackerThreat: 100,
            currentTargetThreat: 0, secondsSinceLastRetarget: 4.0, Options);

        Assert.Equal(ThreatRetargetDecision.Retarget, decision);
    }

    [Fact]
    public void AFreshCreatureWithNoRetargetHistoryIsNeverHeldBackByCooldown()
    {
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x50, topAttackerId: 0x100, topAttackerThreat: 100,
            currentTargetThreat: 0, secondsSinceLastRetarget: double.PositiveInfinity, Options);

        Assert.Equal(ThreatRetargetDecision.Retarget, decision);
    }

    [Fact]
    public void EveryTestPassingRetargets()
    {
        ThreatRetargetDecision decision = ThreatRetargetRule.Decide(
            currentTargetId: 0x50, topAttackerId: 0x100, topAttackerThreat: 200,
            currentTargetThreat: 0, secondsSinceLastRetarget: 999, Options);

        Assert.Equal(ThreatRetargetDecision.Retarget, decision);
    }
}

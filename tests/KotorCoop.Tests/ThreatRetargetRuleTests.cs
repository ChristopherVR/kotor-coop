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

    // The shipped tunables come from ThreatRetargetOptions.Default, and ONLY from there:
    // the type is a struct, so `new()` is the zero-initialiser and never runs the primary
    // constructor's defaults. That is what shipped in the first live round (2026-09-03 02:55,
    // "threat: 0x24 retargeted ... (threat 6 vs 0)" straight through a 15-point floor that
    // was really 0). Pin Default's values, show the trap, and keep the controller off `new()`.
    [Fact]
    public void TheShippedDefaultsAreTheDocumentedOnes()
    {
        Assert.Equal(Options, ThreatRetargetOptions.Default);
    }

    [Fact]
    public void AParameterlessNewIsAllZerosWhichIsWhyDefaultExists()
    {
        ThreatRetargetOptions zeros = new();
        Assert.Equal(0.0, zeros.MinThreatToRetarget);
        Assert.Equal(0.0, zeros.MarginRatio);
        Assert.Equal(0.0, zeros.CooldownSeconds);
        Assert.Equal(
            ThreatRetargetDecision.Retarget,
            ThreatRetargetRule.Decide(currentTargetId: 0x7FFFFFFF, topAttackerId: 0x13F, topAttackerThreat: 6, currentTargetThreat: 0, secondsSinceLastRetarget: double.PositiveInfinity, zeros));
        Assert.Equal(
            ThreatRetargetDecision.BelowMinimum,
            ThreatRetargetRule.Decide(currentTargetId: 0x7FFFFFFF, topAttackerId: 0x13F, topAttackerThreat: 6, currentTargetThreat: 0, secondsSinceLastRetarget: double.PositiveInfinity, ThreatRetargetOptions.Default));
    }

    [Fact]
    public void TheControllerTakesItsOptionsFromDefaultNotFromNew()
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KotorCoop.slnx")))
        {
            root = root.Parent;
        }

        Assert.True(root is not null, $"no KotorCoop.slnx above {AppContext.BaseDirectory}");
        string source = File.ReadAllText(Path.Combine(root!.FullName, "src", "KotorCoop.Plugin", "PeerDamageController.cs"));
        Assert.Contains("ThreatRetargetOptions ThreatOptions = ThreatRetargetOptions.Default;", source);
        Assert.DoesNotContain("ThreatRetargetOptions ThreatOptions = new()", source);
    }

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

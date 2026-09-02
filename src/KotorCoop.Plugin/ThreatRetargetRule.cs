namespace KotorCoop.Plugin;

/// <summary>Tunables for <see cref="ThreatRetargetRule.Decide"/>. Rule-defined, not measured
/// off the engine — see docs/research/peer-damage-ledger.md §9 for the reasoning behind each
/// default.</summary>
/// <param name="MarginRatio">The top attacker's threat must be at least this many times the
/// current target's own threat before a retarget is even considered — stops a creature
/// flip-flopping between two attackers dealing near-identical damage.</param>
/// <param name="MinThreatToRetarget">An absolute floor beneath the ratio: with the current
/// target usually untracked (see <see cref="ThreatTable"/>'s own remarks on what it does not
/// see), a ratio over zero is trivially satisfied by any damage at all, so this is what
/// actually stops a single glancing hit from redirecting a creature.</param>
/// <param name="CooldownSeconds">Minimum time between two retargets of the SAME creature —
/// never more than once per cooldown, regardless of how the threat table moves in between.</param>
internal readonly record struct ThreatRetargetOptions(
    double MarginRatio = 1.5,
    double MinThreatToRetarget = 15.0,
    double CooldownSeconds = 4.0);

/// <summary>Every outcome <see cref="ThreatRetargetRule.Decide"/> can reach, named so a caller
/// (and a test) can tell WHY nothing happened rather than only THAT nothing happened.</summary>
internal enum ThreatRetargetDecision
{
    /// <summary>The creature has no live combat-round target at all — not currently fighting
    /// anyone, so there is nothing to steer it away from. Never force an idle/non-hostile
    /// creature into a fight through this rule; that is a different feature's job.</summary>
    NotInCombat,

    /// <summary>No attacker has any tracked threat against this creature.</summary>
    NoTopAttacker,

    /// <summary>The top attacker already IS the current target.</summary>
    AlreadyTarget,

    /// <summary>The top attacker's threat has not cleared the absolute floor.</summary>
    BelowMinimum,

    /// <summary>The top attacker's threat has not cleared the margin over the current
    /// target's own threat.</summary>
    BelowMargin,

    /// <summary>This creature was retargeted too recently.</summary>
    OnCooldown,

    /// <summary>Every test passed — issue the retarget.</summary>
    Retarget,
}

/// <summary>
/// The pure decision at the heart of THREAT_RETARGET: given a creature's current attack
/// target and what <see cref="ThreatTable"/> says about who has actually been hurting it
/// lately, should it be ordered onto someone else? No engine calls, no state of its own — a
/// static function over its arguments, so every branch is directly testable.
/// </summary>
internal static class ThreatRetargetRule
{
    /// <summary>Same sentinel as <see cref="Odyssey.ObjectInvalid"/>, duplicated rather than
    /// referenced: this file is dependency-free by design (see
    /// tests/KotorCoop.Tests/KotorCoop.Tests.csproj's own remarks — anything touching
    /// <c>Odyssey</c> cannot compile in the 64-bit test host), the same tradeoff
    /// <c>PartyRosterBodyReuseRule.ObjectInvalid</c> already makes for the identical reason.</summary>
    private const uint ObjectInvalid = 0x7F000000;

    public static ThreatRetargetDecision Decide(
        uint currentTargetId,
        uint topAttackerId,
        double topAttackerThreat,
        double currentTargetThreat,
        double secondsSinceLastRetarget,
        ThreatRetargetOptions options)
    {
        if (currentTargetId is 0 or ObjectInvalid)
        {
            return ThreatRetargetDecision.NotInCombat;
        }

        if (topAttackerId == 0)
        {
            return ThreatRetargetDecision.NoTopAttacker;
        }

        if (topAttackerId == currentTargetId)
        {
            return ThreatRetargetDecision.AlreadyTarget;
        }

        if (topAttackerThreat < options.MinThreatToRetarget)
        {
            return ThreatRetargetDecision.BelowMinimum;
        }

        if (topAttackerThreat < currentTargetThreat * options.MarginRatio)
        {
            return ThreatRetargetDecision.BelowMargin;
        }

        if (secondsSinceLastRetarget < options.CooldownSeconds)
        {
            return ThreatRetargetDecision.OnCooldown;
        }

        return ThreatRetargetDecision.Retarget;
    }
}

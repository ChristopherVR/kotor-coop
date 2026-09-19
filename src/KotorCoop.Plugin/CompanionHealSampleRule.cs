namespace KotorCoop.Plugin;

/// <summary>
/// How a CLIENT notices that its own engine HEALED a companion <c>companion_local_combat</c> has
/// released (docs/research/companion-state-sync.md §13.6, 2026-09-18). Damage taken is counted
/// at the engine's damage choke point (<see cref="PhantomHitHook"/>'s companion watch); a heal
/// goes through a different effect path with no hook, so it is inferred from the body's hit
/// points instead — safe here, and only here, because a RELEASED companion is the one body the
/// host's mirror write never touches (<c>PartyPresenceController</c> stops writing a released
/// companion's health entirely), so a rise in its hit points between two samples has exactly
/// one author: this instance's own engine, i.e. a medpac, a Force heal, or a regeneration
/// property.
///
/// The sample is a pure function of (previous, current) so the arithmetic can be pinned by
/// tests without a game. One rule per hazard:
/// <list type="bullet">
/// <item>A different (or no) watched body → baseline only. A reclaim then re-release restarts
/// the sample; the running TOTAL lives in the caller per roster slot and is never reset here
/// (the same reclaim lesson <see cref="PhantomHitHook"/>'s companion total learned).</item>
/// <item>A hit landed in the same window (the damage watch's serial moved) → baseline only.
/// Hit points that went down and up inside one window cannot be split apart, and crediting
/// the hit's size as a heal would be the exact "overkill reads as a heal" mistake.</item>
/// <item>The body was at or below zero last sample → baseline only. Coming back from a temp
/// death is the engine's own recovery, not something a peer cast.</item>
/// <item>A rise above <see cref="MaxPlausibleHeal"/> → refused (counted, for the log). No
/// single heal in this game reaches four digits; a level-up on the client's copy or a misread
/// field must not become a thousand points on the host's body.</item>
/// </list>
/// </summary>
public static class CompanionHealSampleRule
{
    /// <summary>The largest single-window rise believed to be a heal — the engine's own
    /// <c>EffectHeal</c> ceiling the host applies with (OdysseyEffects.MaxHealMagnitude).</summary>
    public const int MaxPlausibleHeal = 999;

    public readonly record struct Sample(uint ObjectId, int HitPoints, uint DamageSerial);

    public enum Verdict
    {
        /// <summary>Nothing to credit; the sample is now the baseline.</summary>
        Baseline,

        /// <summary>A rise no heal could produce — refused, baseline taken.</summary>
        Refused,

        /// <summary>A heal landed; <c>Heal</c> carries how much.</summary>
        Healed,
    }

    /// <summary>
    /// Compares this pass's reading against the last one. Always returns the new baseline;
    /// <paramref name="heal"/> is nonzero only for <see cref="Verdict.Healed"/>.
    /// </summary>
    public static Verdict Observe(in Sample previous, in Sample current, out int heal)
    {
        heal = 0;

        if (current.ObjectId == 0 || previous.ObjectId != current.ObjectId)
        {
            return Verdict.Baseline;
        }

        if (current.DamageSerial != previous.DamageSerial)
        {
            return Verdict.Baseline;
        }

        if (previous.HitPoints <= 0)
        {
            return Verdict.Baseline;
        }

        int delta = current.HitPoints - previous.HitPoints;
        if (delta <= 0)
        {
            return Verdict.Baseline;
        }

        if (delta > MaxPlausibleHeal)
        {
            return Verdict.Refused;
        }

        heal = delta;
        return Verdict.Healed;
    }
}

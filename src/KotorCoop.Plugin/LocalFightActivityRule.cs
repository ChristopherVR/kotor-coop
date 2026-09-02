namespace KotorCoop.Plugin;

/// <summary>What <c>MaintainLocalFightSlot</c> should do about ONE physical slot's inactivity
/// clock this pass — see docs/research/combat-participation.md §13 ("<c>local_combat_timeout_s</c>
/// measures inactivity, not fight length") for the churn this replaces: measured live
/// (2026-09-02, tar_m10aa), a genuinely ongoing fight — the host was applying the client's
/// forwarded hits on the excluded creature the whole time — got released every
/// <c>local_combat_timeout_s</c> regardless, simply because that many seconds had passed since
/// <see cref="EnemyPresenceController.StartLocalFight"/> armed the slot, handing the creature
/// back to the census for a tick before re-arming and re-engaging it roughly 0.8s later.</summary>
internal enum LocalFightActivityAction
{
    /// <summary>Evidence this pass (a rise in damage dealt to this creature, or the creature's
    /// own combat round currently targeting the local player) — the caller should bump its
    /// activity clock to now. Wins over the clock unconditionally: a pass that finds either
    /// kind of evidence refreshes regardless of how close the timeout already was.</summary>
    Refresh,

    /// <summary>No evidence this pass, but still inside the inactivity window — leave the
    /// clock alone.</summary>
    Continue,

    /// <summary>No evidence this pass, and the inactivity window has elapsed since the last
    /// confirmed activity — the caller should release the slot.</summary>
    TimedOut,
}

/// <summary>
/// The pure decision half of local_combat's inactivity timeout (coop.ini
/// <c>local_combat_timeout_s</c>, docs/research/combat-participation.md §13, docs/CONFIG.md).
/// Answers, once per physical slot per pass: is there fresh evidence this fight is still real,
/// and if not, has the slot gone long enough without any that it should be released?
///
/// Two pieces of evidence, either one enough on its own (see
/// <see cref="EnemyPresenceController.MaintainLocalFightSlot"/> for where each is actually
/// read): a rise in <see cref="DamageDealtTable"/>'s cumulative total for this slot's creature
/// (the local player landed a real hit on it), or the creature's own live combat round
/// (<c>creature+0x9C8</c> -&gt; round, <c>round+0x9CC</c>, via
/// <see cref="Odyssey.GetCurrentAttackTargetId"/>) currently naming the local player as its
/// target — docs/research/combat-participation.md §1.2. This is the same "TargetingUs" signal
/// <see cref="LocalFightCandidateRule"/> already ranks highest for SELECTING a candidate; here
/// it answers the different question of whether an ALREADY-armed fight is still live, read
/// directly off the now-local creature's own state rather than the pre-fight phantom-attacker
/// report <see cref="LocalFightCandidateRule"/> has to rely on before a fight exists to inspect.
/// </summary>
internal static class LocalFightActivityRule
{
    /// <param name="damageDealtRose"><see cref="DamageDealtTable"/>'s cumulative total for this
    /// slot's creature is higher than the last pass saw. The caller does that comparison itself
    /// (the same "cumulative, never a delta" discipline as every other wire total in this file
    /// — see <see cref="EnemyPresenceController"/>'s own <c>UpdatePhantomAttackerTracking</c>)
    /// and hands in only the verdict.</param>
    /// <param name="targetingUs">The creature's own combat round currently names the local
    /// player as its target.</param>
    /// <param name="lastActivityAtSeconds">When this slot last had either kind of evidence — or,
    /// with none yet this fight, when <c>StartLocalFight</c> armed it.</param>
    /// <param name="nowSeconds">This pass's clock-relative timestamp.</param>
    /// <param name="timeoutSeconds">coop.ini <c>local_combat_timeout_s</c>.</param>
    public static LocalFightActivityAction Decide(
        bool damageDealtRose,
        bool targetingUs,
        double lastActivityAtSeconds,
        double nowSeconds,
        double timeoutSeconds)
    {
        if (damageDealtRose || targetingUs)
        {
            return LocalFightActivityAction.Refresh;
        }

        // Strictly greater, matching the release check this replaces: a slot polled at exactly
        // the boundary keeps its fight for one more pass rather than losing it a frame early.
        return nowSeconds - lastActivityAtSeconds > timeoutSeconds
            ? LocalFightActivityAction.TimedOut
            : LocalFightActivityAction.Continue;
    }
}

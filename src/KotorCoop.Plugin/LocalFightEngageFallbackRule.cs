namespace KotorCoop.Plugin;

/// <summary>What <c>MaintainLocalFightSlot</c> should do about local_combat's own script-fire
/// fallback for ONE physical slot this pass — see docs/research/combat-participation.md §12
/// ("Idle hostiles never engage") for why <see cref="EnemyPresenceController.StartLocalFight"/>'s
/// own <see cref="Odyssey.FireCreatureScriptSlot0x260"/> call alone does nothing for a creature
/// with no combat round already running: live evidence in that section shows `k_def_combend01`
/// acts on an EXISTING round/target rather than creating one, so an idle hostile — hostile in the
/// census, real script, nobody has hit it or perceived it yet — sits frozen after the fire with
/// nothing to act on.</summary>
internal enum LocalFightEngageFallbackAction
{
    /// <summary>Nothing to do: the flag is off, the fallback already fired once for this fight,
    /// the creature already has a real attack-queue target (the script worked, or its own AI beat
    /// this to it), or the grace window since the fight was armed has not elapsed yet.</summary>
    None,

    /// <summary>Drive the creature's attack on the local player directly, once, through the same
    /// engine call (<see cref="Odyssey.AttackTarget"/>, the native <c>ExecuteCommandAttack</c>)
    /// the <c>companion_local_combat</c> release path and the <c>attack</c> console verb already
    /// use — the exact primitive that, driven by hand, un-froze the tar_m10aa Vulkars this fix is
    /// built from.</summary>
    EngageDirectly,
}

/// <summary>
/// The pure decision half of local_combat's engage fallback (coop.ini
/// <c>local_combat_engage_fallback</c>, default ON — docs/research/combat-participation.md §12,
/// docs/CONFIG.md). Answers, once per physical slot per pass: has enough time passed since this
/// slot's fight was armed, with no attack-queue target ever appearing on the creature's own
/// (i.e. neither the script fire nor the creature's own AI found one), that the fallback should
/// drive the attack directly instead of leaving the creature frozen for the rest of its
/// <c>local_combat_timeout_s</c> window.
///
/// One-shot per fight, not a retry loop (unlike <see cref="CompanionLocalFightRule.ShouldDriveAttack"/>'s
/// own periodic re-attempt for a released companion): a creature that still shows no target
/// after one direct drive is not going to gain one from an identical second call, and the direct
/// drive itself queues a real attack action that the very next pass reads back as a non-empty
/// head — without the one-shot guard that would look like fresh grounds to fire again every
/// single frame.
/// </summary>
internal static class LocalFightEngageFallbackRule
{
    /// <param name="enabled">coop.ini <c>local_combat_engage_fallback</c>.</param>
    /// <param name="alreadyIssued">Whether this slot's currently-armed fight already had the
    /// fallback fire once.</param>
    /// <param name="headActionType"><see cref="Odyssey.GetHeadActionType"/> on the creature — the
    /// same signal <see cref="CompanionLocalFightRule.ShouldDriveAttack"/> already uses for the
    /// identical "does this creature currently have nothing queued" question. -1 means empty.</param>
    /// <param name="armedAtSeconds">When this slot's fight was armed
    /// (<c>StartLocalFight</c>'s own clock-relative timestamp, the same one the timeout safety
    /// net already uses).</param>
    /// <param name="nowSeconds">This pass's clock-relative timestamp.</param>
    /// <param name="graceSeconds">How long to let the script fire — and any independent action by
    /// the creature's own AI — work before concluding neither found a target.</param>
    public static LocalFightEngageFallbackAction Decide(
        bool enabled,
        bool alreadyIssued,
        int headActionType,
        double armedAtSeconds,
        double nowSeconds,
        double graceSeconds)
    {
        if (!enabled || alreadyIssued || headActionType != -1)
        {
            return LocalFightEngageFallbackAction.None;
        }

        return nowSeconds - armedAtSeconds >= graceSeconds
            ? LocalFightEngageFallbackAction.EngageDirectly
            : LocalFightEngageFallbackAction.None;
    }
}

namespace KotorCoop.Plugin;

/// <summary>
/// The pure text half of the local_combat deferred-hostiles HUD line (item 14,
/// docs/roadmap-seamless.md Tier 2) — what <c>local_combat_deferred_notice</c> shows, if
/// anything, for a given deferred count. Separated from <see cref="EnemyPresenceController"/> the
/// same way <see cref="ConnectionIndicatorRule"/> is split from
/// <see cref="ConnectionIndicatorController"/>, so the wording (and its singular/plural edge) is
/// unit-testable without a live block.
/// </summary>
internal static class LocalCombatDeferredNoticeRule
{
    /// <summary>
    /// Null (show nothing) when nothing is deferred; otherwise "N more enem{y|ies} engaged -
    /// mirrored". "engaged" rather than "waiting": a deferred hostile is not idle — it is still
    /// being mirrored (position, health, animation) exactly like every other census enemy, only
    /// its own AI has not been handed the fight yet.
    /// </summary>
    public static string? Text(int deferredCount)
    {
        if (deferredCount <= 0)
        {
            return null;
        }

        return deferredCount == 1
            ? "1 more enemy engaged - mirrored"
            : $"{deferredCount} more enemies engaged - mirrored";
    }
}

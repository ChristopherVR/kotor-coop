namespace KotorCoop.Session.Pumps;

/// <summary>
/// One tick's worth of the values CombatPump.PumpEnemies' "local combat diag (agent)" lines
/// report: how many enemies the owner published, how many survived the exclusion filter, and
/// which ids were excluded. Two snapshots that compare equal describe a tick that told the log
/// nothing new.
/// </summary>
internal readonly record struct LocalCombatDiagSnapshot(int EnemyCount, int FilteredCount, string ExcludedKey);

/// <summary>
/// The pure decision behind rate-limiting CombatPump's "local combat diag (agent)" TEMPORARY
/// DIAGNOSTIC lines (docs/research/combat-participation.md §11): PumpEnemies runs on the
/// ~150ms combat cadence, and both lines fired unconditionally every tick local_combat's
/// exclusion filter had anything excluded — several times a SECOND during a fight, all
/// reporting the same numbers between hits. Kept as the evidence path for the exclusion
/// mechanism (do not delete; that is what found the immortal-Sith bug LocalCombatExclusionRule
/// closes) — just no longer spammed: log once when the values genuinely change, or at most once
/// every <see cref="MinIntervalMs"/> when they don't, so a live question mid-fight still finds
/// something recent.
/// </summary>
internal static class LocalCombatDiagThrottle
{
    /// <summary>Ceiling on how stale the diagnostic is allowed to look while nothing about the
    /// exclusion is actually changing.</summary>
    public const long MinIntervalMs = 10_000;

    /// <summary>The excluded-id set as a comparison key, sorted before joining — the owner's
    /// array order is not guaranteed stable tick to tick, and a reorder of the same ids is not
    /// a real change worth logging again.</summary>
    public static string ExcludedKey(IReadOnlyList<uint> excludedIds)
    {
        uint[] sorted = [.. excludedIds];
        Array.Sort(sorted);
        return string.Join(",", sorted);
    }

    /// <summary>True when <paramref name="current"/> differs from <paramref name="last"/> (or
    /// there is no <paramref name="last"/> yet — the first tick always logs), or when
    /// <see cref="MinIntervalMs"/> has elapsed since <paramref name="lastLoggedMs"/> regardless
    /// of whether anything changed.</summary>
    public static bool ShouldLog(
        LocalCombatDiagSnapshot current, LocalCombatDiagSnapshot? last, long nowMs, long lastLoggedMs) =>
        last is null
            || !current.Equals(last.Value)
            || nowMs - lastLoggedMs >= MinIntervalMs;
}

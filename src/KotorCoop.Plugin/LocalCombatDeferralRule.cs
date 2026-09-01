namespace KotorCoop.Plugin;

/// <summary>
/// Item 14 (docs/roadmap-seamless.md Tier 2): local_combat's own exclusion holds at most
/// <c>SharedBlock.LocalCombatSlotCapacity</c> creatures at a time (N-slot local_combat,
/// docs/research/combat-participation.md §11; originally exactly one, before that widening) — a
/// hostile beyond that count simply never gets a turn until a slot frees, and until item 14
/// nothing recorded that this happened at all. This is the pure half of surfacing it: given
/// every id <c>SelectImmediateCandidate</c> found eligible this scan (already past
/// <see cref="LocalFightCandidateRule.Evaluate"/>'s hard gates — hostile, GIT-identical,
/// scripted, in range) and the SET of ids currently HOLDING a slot, decides the DEFERRED set —
/// everyone eligible except a current holder — and whether that set has genuinely changed since
/// the last time it was computed, so the caller can log/notify on the EDGE instead of every scan.
/// Separated from <see cref="EnemyPresenceController"/> the same way every other <c>*Rule</c>
/// class in this file's neighbourhood is, so the set arithmetic is unit-testable without a live
/// engine underneath it.
/// </summary>
internal static class LocalCombatDeferralRule
{
    /// <summary>
    /// Writes the deferred set into <paramref name="deferredOwnerIds"/>: every id among the
    /// first <paramref name="eligibleCount"/> entries of <paramref name="eligibleOwnerIds"/>
    /// except one already among the first <paramref name="holderCount"/> entries of
    /// <paramref name="holderOwnerIds"/> — N-slot local_combat's holder SET, not a single id
    /// (holderCount 0 excludes nothing, so every eligible id is deferred). Returns the count
    /// written. Bounded by <paramref name="deferredOwnerIds"/>'s own length, which in practice
    /// is <c>SharedBlock.EnemyCapacity</c> on both sides — the same cap the census itself
    /// carries, so this can never overflow the caller's buffer.
    /// </summary>
    public static int ComputeDeferred(
        uint[] eligibleOwnerIds, int eligibleCount,
        uint[] holderOwnerIds, int holderCount,
        uint[] deferredOwnerIds)
    {
        int n = 0;

        for (int i = 0; i < eligibleCount && n < deferredOwnerIds.Length; i++)
        {
            uint id = eligibleOwnerIds[i];
            if (id != 0 && !Contains(holderOwnerIds, holderCount, id))
            {
                deferredOwnerIds[n++] = id;
            }
        }

        return n;
    }

    /// <summary>
    /// Whether the deferred set genuinely changed since the last time it was computed — same
    /// COUNT and every id present in both, order-independent. The census re-sorts every publish
    /// (creatures mid-combat-round first, then by distance to the nearest player), so enumeration
    /// order is not stable frame to frame even when the exact same creatures are eligible; a naive
    /// index-by-index compare would flap — and log — on every reorder even though nothing about
    /// the set actually moved.
    /// </summary>
    public static bool HasChanged(
        uint[] previousOwnerIds, int previousCount, uint[] currentOwnerIds, int currentCount)
    {
        if (previousCount != currentCount)
        {
            return true;
        }

        for (int i = 0; i < currentCount; i++)
        {
            if (!Contains(previousOwnerIds, previousCount, currentOwnerIds[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Contains(uint[] ids, int count, uint id)
    {
        for (int i = 0; i < count; i++)
        {
            if (ids[i] == id)
            {
                return true;
            }
        }

        return false;
    }
}

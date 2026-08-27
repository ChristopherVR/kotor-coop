namespace KotorCoop.Plugin;

/// <summary>
/// Item 14 (docs/roadmap-seamless.md Tier 2): local_combat's own exclusion slot
/// (<c>EnemyPresenceController._locallyFightingId</c>) holds exactly one creature at a time, by
/// design (see its own doc comment) — a second, third, ... eligible hostile simply never gets a
/// turn until the first fight ends, and until now nothing recorded that this happened at all.
/// This is the pure half of surfacing it: given every id <c>SelectImmediateCandidate</c> found
/// eligible this scan (already past <see cref="LocalFightCandidateRule.Evaluate"/>'s hard gates —
/// hostile, GIT-identical, scripted, in range) and whichever id currently HOLDS the one slot (0
/// if none), decides the DEFERRED set — everyone eligible except the holder — and whether that
/// set has genuinely changed since the last time it was computed, so the caller can log/notify on
/// the EDGE instead of every scan. Separated from <see cref="EnemyPresenceController"/> the same
/// way every other <c>*Rule</c> class in this file's neighbourhood is, so the set arithmetic is
/// unit-testable without a live engine underneath it.
/// </summary>
internal static class LocalCombatDeferralRule
{
    /// <summary>
    /// Writes the deferred set into <paramref name="deferredOwnerIds"/>: every id among the
    /// first <paramref name="eligibleCount"/> entries of <paramref name="eligibleOwnerIds"/>
    /// except <paramref name="holderOwnerId"/> (0 = nobody currently holds the slot, so nothing
    /// is excluded — every eligible id is deferred). Returns the count written. Bounded by
    /// <paramref name="deferredOwnerIds"/>'s own length, which in practice is
    /// <c>SharedBlock.EnemyCapacity</c> on both sides — the same cap the census itself carries,
    /// so this can never overflow the caller's buffer.
    /// </summary>
    public static int ComputeDeferred(
        uint[] eligibleOwnerIds, int eligibleCount, uint holderOwnerId, uint[] deferredOwnerIds)
    {
        int n = 0;

        for (int i = 0; i < eligibleCount && n < deferredOwnerIds.Length; i++)
        {
            uint id = eligibleOwnerIds[i];
            if (id != 0 && id != holderOwnerId)
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

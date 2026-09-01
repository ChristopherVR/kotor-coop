namespace KotorCoop.Protocol;

/// <summary>
/// Merges several senders' independently-reported per-attacker breakdowns for ONE target into
/// a single table, summing each attacker id's contribution across senders — the same "totals
/// from different senders ADD" rule <c>ICoopSession.GetIncomingPhantomDamage</c>'s scalar total
/// already applies (two peers hitting the same phantom target in their own, independently
/// simulated games both genuinely happened), extended to the per-attacker dimension.
///
/// Each sender's own list is already a monotonic cumulative total by the time it reaches this:
/// the packet's scalar <see cref="PhantomDamagePacket.CumulativeDamage"/> field gates a
/// reordered/duplicate datagram before either side of a report is ever stored (see
/// CoopHost/CoopClient's <c>AcceptIncomingPhantomDamage</c>/<c>HandlePhantomDamage</c>), so
/// summing the latest known value per sender here is safe and never regresses.
///
/// Session-layer only (uses LINQ) — NOT compiled into the NativeAOT plugin. The plugin's own
/// per-attacker reconciliation is <see cref="PhantomAttackerAppliedLedger"/>, in the sibling
/// file, which stays LINQ-free on purpose.
/// </summary>
public static class PhantomAttackerMerge
{
    /// <summary>
    /// Sums <paramref name="reports"/> (one list per sender) by attacker id and returns the
    /// top <paramref name="capacity"/> entries by total — the same least-total eviction rule
    /// every fixed-capacity table in this channel uses, applied once at the merge boundary
    /// rather than tracked incrementally, since this runs at pump rate (not inside a hot
    /// engine hook) and a handful of senders' worth of entries is cheap to re-sum each call.
    /// </summary>
    public static IReadOnlyList<PhantomAttackerEntry> Merge(
        IEnumerable<IReadOnlyList<PhantomAttackerEntry>> reports, int capacity)
    {
        Dictionary<uint, ulong> totals = [];

        foreach (IReadOnlyList<PhantomAttackerEntry> report in reports)
        {
            foreach (PhantomAttackerEntry entry in report)
            {
                if (entry.AttackerId == 0)
                {
                    continue;
                }

                totals.TryGetValue(entry.AttackerId, out ulong existing);
                totals[entry.AttackerId] = existing + entry.CumulativeDamage;
            }
        }

        if (totals.Count == 0)
        {
            return [];
        }

        return totals
            .OrderByDescending(kv => kv.Value)
            .Take(capacity)
            .Select(kv => new PhantomAttackerEntry(kv.Key, (uint)Math.Min(kv.Value, uint.MaxValue)))
            .ToList();
    }
}

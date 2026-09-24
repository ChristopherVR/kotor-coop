using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// Selects which of a module's candidate enemies <see cref="KotorGameBridge.ReadEnemies"/>
/// actually publishes, on the rare pump where the live population exceeds
/// <see cref="KotorGameBridge.BlockEnemyCapacity"/> — the "no cap really" design's safety net
/// for whatever that generous, content-survey-backed bound still cannot hold (a debug
/// <c>spawncreature</c> spree, not real shipped content; see
/// docs/research/creature-liveness.md). Pure and side-effect-free, the same shape
/// <c>KotorCoop.Session.LivenessBlockPage.Select</c> already uses for its own overflow, so the
/// rotation arithmetic can be unit-tested without a game process.
///
/// Fighting candidates always win a slot and are never rotated away — dropping a creature that
/// is mid-combat-round is worse than a bystander missing one publish, the same priority
/// <see cref="KotorGameBridge.ReadEnemies"/> already gave them before this pass. When even the
/// fighting set alone overflows capacity, the same policy LivenessBlockPage's "actionable" tier
/// uses applies: truncating them is the only option left, and the channel's own tick-rate
/// resend means whatever is dropped this pass is retried on the very next one.
///
/// Whatever capacity remains is filled by ROTATING through the bystander pool (already sorted
/// nearest-to-a-player-first by the caller) instead of always covering the same nearest prefix:
/// the previous design's failure mode was a farther bystander that could sit past the cap
/// forever, described by nothing this instance ever sent. Rotating means every bystander gets a
/// turn within a bounded number of pumps — this method is called once per
/// <c>KotorCoop.Session.Pumps.CombatPump.PumpEnemies</c> tick (the combat cadence, several times
/// a second), so "bounded" here means a fraction of a second, not minutes.
/// </summary>
public static class EnemyCensusPage
{
    public static (List<EnemyState> Page, int NextCursor) Select(
        IReadOnlyList<EnemyState> fighting, IReadOnlyList<EnemyState> bystanders, int capacity, int cursor)
    {
        if (capacity <= 0)
        {
            return ([], 0);
        }

        if (fighting.Count >= capacity)
        {
            List<EnemyState> truncated = new(capacity);
            for (int i = 0; i < capacity; i++)
            {
                truncated.Add(fighting[i]);
            }

            return (truncated, cursor);
        }

        List<EnemyState> page = new(capacity);
        page.AddRange(fighting);

        int remaining = capacity - fighting.Count;
        if (bystanders.Count == 0 || remaining <= 0)
        {
            return (page, 0);
        }

        // Every bystander fits without rotating: take them all, and reset the cursor so a
        // later overflow starts from the front rather than an arbitrary leftover offset.
        if (bystanders.Count <= remaining)
        {
            page.AddRange(bystanders);
            return (page, 0);
        }

        int start = ((cursor % bystanders.Count) + bystanders.Count) % bystanders.Count;
        for (int i = 0; i < remaining; i++)
        {
            page.Add(bystanders[(start + i) % bystanders.Count]);
        }

        int nextCursor = (start + remaining) % bystanders.Count;
        return (page, nextCursor);
    }
}

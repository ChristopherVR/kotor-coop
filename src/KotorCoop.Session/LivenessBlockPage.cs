using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Selects what a mirroring peer's agent hands the plugin each pump, when the accumulated
/// creature-liveness set is larger than the plugin's shared-block capacity (unlike the wire,
/// which pages a whole cycle across several datagrams, the plugin hand-off is a single fixed
/// array the agent overwrites every pump — see docs/research/creature-liveness.md for why the
/// two capacities do not have to be reconciled the same way).
///
/// Dead/gone entries are the ones that need ACTING on — a client's own copy might still be
/// standing — so they always go first. When there are more of them than fit, the page rotates
/// through them across pumps (the same cursor the alive tail uses), so every verdict reaches
/// the plugin within a few pumps instead of the overflow never arriving. Alive entries exist only to catch the rarer "host alive, local
/// dead" anomaly, so when there is no room for all of them the remaining capacity rotates
/// through them across pumps instead of always covering the same prefix and starving the
/// rest forever.
/// </summary>
public static class LivenessBlockPage
{
    public static (List<CreatureLivenessEntry> Page, int NextCursor) Select(
        IReadOnlyList<CreatureLivenessEntry> entries, int capacity, int cursor)
    {
        if (capacity <= 0)
        {
            return ([], 0);
        }

        List<CreatureLivenessEntry> actionable = [];
        List<CreatureLivenessEntry> alive = [];
        foreach (CreatureLivenessEntry entry in entries)
        {
            (entry.State == CreatureLivenessState.Alive ? alive : actionable).Add(entry);
        }

        if (actionable.Count > capacity)
        {
            // More verdicts than slots: rotate through them too. Truncating always kept the
            // same prefix, and the owner appends its retained "gone" verdicts after the scan's
            // corpses, so in a module full of bodies a destroy could never reach the plugin.
            int first = ((cursor % actionable.Count) + actionable.Count) % actionable.Count;
            List<CreatureLivenessEntry> window = new(capacity);
            for (int i = 0; i < capacity; i++)
            {
                window.Add(actionable[(first + i) % actionable.Count]);
            }

            return (window, (first + capacity) % actionable.Count);
        }

        if (actionable.Count == capacity)
        {
            return (actionable, 0);
        }

        List<CreatureLivenessEntry> page = new(capacity);
        page.AddRange(actionable);

        int remaining = capacity - actionable.Count;
        if (alive.Count == 0 || remaining <= 0)
        {
            return (page, 0);
        }

        int start = ((cursor % alive.Count) + alive.Count) % alive.Count;
        int taken = Math.Min(remaining, alive.Count);
        for (int i = 0; i < taken; i++)
        {
            page.Add(alive[(start + i) % alive.Count]);
        }

        int nextCursor = taken >= alive.Count ? 0 : (start + taken) % alive.Count;
        return (page, nextCursor);
    }
}

namespace KotorCoop.Protocol;

/// <summary>
/// A short-lived, consume-on-match budget of "an item row is about to appear (or just
/// appeared) in the local PC's inventory through a channel that ALREADY replicates it" —
/// ground-item pickup, container/corpse loot, a peer's dialogue or script grant applied
/// through the give-loot cheat channel, and a human's own <c>giveloot</c> console command.
///
/// <see cref="ScriptItemGrantRule"/> exists because none of those channels are visible to a
/// bare inventory sweep: <c>IGameBridge.ReadPossessedItems</c> only ever sees "a new row of
/// resref X appeared", never WHY. Every already-synced path records one unit of budget here
/// the moment it knows a new row is about to land (see the call sites in
/// <c>KotorCoop.Session.Pumps</c>); the script-grant diff then consumes one unit per
/// already-accounted-for row instead of re-reporting it as freshly created by a script.
///
/// Consume-on-match, not "any recent activity suppresses everything": recording is keyed by
/// RESREF because that is the only identity a possessed-items sweep carries (see
/// <see cref="DialogueItemDiff"/>'s own remarks on why no object id survives the sweep), so a
/// genuine script grant of resref X arriving in the same window as an unrelated pickup of
/// resref Y is still reported — only a same-resref match is ambiguous enough to suppress.
///
/// Bounded by TIME, not by count: an entry older than the window is worthless (whatever
/// caused it either already showed up in a sample or never will) and is pruned lazily rather
/// than swept on a timer, so an idle session costs nothing.
/// </summary>
public sealed class RecentlySyncedItems(long windowMs = 5000)
{
    private readonly record struct Entry(string Resref, long ExpiresAtMs);

    private readonly List<Entry> _entries = [];

    /// <summary>How many entries are currently live, for tests only — production code has no
    /// reason to ask.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// Records <paramref name="count"/> units of budget for <paramref name="resref"/>, each
    /// good until <paramref name="nowMs"/> + the configured window. A no-op for an empty
    /// resref or a non-positive count — there is nothing meaningful to suppress.
    /// </summary>
    public void Record(string resref, int count, long nowMs)
    {
        if (resref.Length == 0 || count <= 0)
        {
            return;
        }

        long expiresAtMs = nowMs + windowMs;
        for (int i = 0; i < count; i++)
        {
            _entries.Add(new Entry(resref, expiresAtMs));
        }
    }

    /// <summary>
    /// Consumes ONE unit of budget for <paramref name="resref"/> if one is still live,
    /// pruning everything expired first. Returns true (and removes exactly one entry) when a
    /// match was found — the caller's new row is accounted for and must not be re-broadcast;
    /// false means this row is unexplained by any already-synced channel and is a genuine
    /// candidate for a script grant.
    /// </summary>
    public bool TryConsume(string resref, long nowMs)
    {
        Prune(nowMs);

        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Resref, resref, StringComparison.OrdinalIgnoreCase))
            {
                _entries.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>Discards every unit of budget — a module change makes any pending entry
    /// meaningless (the item ids and even the sweep it was about to explain are gone).</summary>
    public void Clear() => _entries.Clear();

    private void Prune(long nowMs) => _entries.RemoveAll(e => e.ExpiresAtMs <= nowMs);
}

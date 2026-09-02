namespace KotorCoop.Memory;

/// <summary>
/// An item sweep taken while the world is between lives is PROVISIONAL: it is served until the
/// world settles, and the first lookup in the settled world rebuilds the caches before answering.
/// Pure state behind <c>KotorGameBridge.ServiceItemTagCache</c>, kept separate so the rule can
/// be pinned without an engine.
///
/// Live 2026-09-02 (docs/research/death-handling.md §6.7, shared-inventory-v2.md §17): a client's
/// rejoin after a wipe resolved its new player 3.5 s before the plugin acked the world on screen,
/// and a lookup in that window ran the one-time inline sweep of the new world. That sweep found
/// the carried character's clothes but NOT its short sword — the heap was still being built —
/// and because a sweep is trusted for a full rebuild interval, the script-grant baseline taken
/// once the world had settled was read through it. The next background sweep, fifteen seconds
/// later, found the sword, and the diff published the client's own weapon as a script grant:
/// the host pooled a new short sword and both pools carried one more than anyone owned.
///
/// The sweep during the load is not wrong to run — callers between lives would otherwise see an
/// empty inventory, which is worse — it is wrong to KEEP. Without a plugin the world is never
/// between lives and nothing here ever fires.
/// </summary>
internal sealed class ItemSweepProvenanceRule
{
    /// <summary>True while the caches describe a sweep taken between lives.</summary>
    public bool Provisional { get; private set; }

    /// <summary>A sweep has just been applied to the caches, taken while the world was (or
    /// was not) between lives.</summary>
    public void Swept(bool betweenLives) => Provisional = betweenLives;

    /// <summary>Whether a lookup about to be served from the caches must rebuild them first:
    /// the held sweep is provisional and the world has now settled. Stays false while still
    /// between lives — rebuilding then would only produce another provisional sweep.</summary>
    public bool MustRebuildNow(bool betweenLives) => Provisional && !betweenLives;

    /// <summary>The caches were dropped (world replaced, or never built); nothing is held.</summary>
    public void Reset() => Provisional = false;
}

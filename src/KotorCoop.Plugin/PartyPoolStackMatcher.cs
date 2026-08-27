// PartyPoolStackMatcher.cs - the pure precedence rule RemovePartyPoolStack (Odyssey.PartyPool.cs)
// uses to decide whether a live pool item matches the resref it was asked to remove. Extracted
// here, free of unsafe pointers/SharedBlockView, so this one decision has a place to be tested
// directly rather than trusted to eyes on the pointer-walking code around it. See
// docs/research/party-pool-sync.md §13/§14.
namespace KotorCoop.Plugin;

/// <summary>
/// Live-verified 2026-08-28 (docs/research/party-pool-sync.md §13.2): a stack
/// <c>AddItemToPartyPool</c> had just inserted was not found by <c>RemovePartyPoolStack</c>'s own
/// Tag-based walk of the identical resref, moments later — even though a THIRD, independent
/// reader (the <c>item</c> console verb, an unrelated code path) confirmed the object was live and
/// correctly tagged in the same breath. Whatever field made the SEARCH side miss it, Tag alone is
/// not a trustworthy match key for a stack that may have been inserted only moments earlier.
///
/// The fix: prefer the resref <c>ItemTemplateSpawnHook</c> itself captured for that item's object
/// id at the exact moment <c>CreateItemOnObject</c> built it (the same VM command
/// <c>AddItemToPartyPool</c>'s own <c>GiveItemToInventory</c> call routes through) — a point-in-
/// time capture immune to whatever transient state the item's live Tag field happens to be in
/// right now. Tag is consulted only when the spawn ring has nothing for this id at all (evicted,
/// a module change cleared it, or the item predates this plugin session).
/// </summary>
public static class PartyPoolStackMatcher
{
    /// <summary>
    /// True when a live pool item should be treated as a match for <paramref name="targetResref"/>.
    /// Both resref parameters are compared case-insensitively — matching the caller's own
    /// case-insensitive convention for everything else in this codebase (<c>TagEqualsIgnoreCase</c>).
    /// </summary>
    /// <param name="spawnRingResref">
    /// What <c>ItemTemplateSpawnHook</c> captured for this item's object id, or <c>null</c> when
    /// the ring remembers nothing for this id. When non-null, this is the AUTHORITATIVE answer —
    /// <paramref name="tagMatches"/> is not even consulted.
    /// </param>
    /// <param name="targetResref">The resref <c>RemovePartyPoolStack</c> was asked to remove.</param>
    /// <param name="tagMatches">
    /// The result of the item's own Tag-based comparison (<c>TagEqualsIgnoreCase</c>) — used only
    /// as a fallback, when the spawn ring has no entry for this item's id.
    /// </param>
    public static bool Matches(string? spawnRingResref, string targetResref, bool tagMatches)
    {
        return spawnRingResref is not null
            ? string.Equals(spawnRingResref, targetResref, System.StringComparison.OrdinalIgnoreCase)
            : tagMatches;
    }
}

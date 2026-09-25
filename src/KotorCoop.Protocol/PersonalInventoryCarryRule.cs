namespace KotorCoop.Protocol;

/// <summary>One stack a joiner's own engine held when it was captured for the character
/// sidecar — the personal-inventory carry docs/research/personal-inventory.md §6 designs.
/// Reuses <see cref="PartyPoolStackKey"/> verbatim (same resref+identity shape the party-pool
/// channel already keys on) rather than inventing a parallel key type, since the eventual
/// restore is meant to be replayed through that exact channel's own primitives.</summary>
public readonly record struct PersonalInventoryCarryEntry(PartyPoolStackKey Key, int Count);

/// <summary>
/// Pure rules for the personal-inventory carry (docs/research/personal-inventory.md §6): what
/// is worth capturing into the sidecar, and what still needs to be RE-APPLIED to reach parity
/// once a carried set meets whatever the newly-adopted world's pool already holds. No
/// session/bridge/file I/O here — see that document's §6.4 for exactly what is (and, honestly,
/// is not) wired up to a live engine this pass; this class is the fully-tested pure core the
/// wiring is meant to call.
/// </summary>
public static class PersonalInventoryCarryRule
{
    /// <summary>
    /// A quest item never needs to be carried through the sidecar at all — it already travels
    /// WITH the world itself (the party-pool channel keeps mirroring it, and any world a joiner
    /// adopts already reflects the host's own quest-item state). Carrying a quest item through
    /// the sidecar as well would risk it being "restored" into a world that has since moved on
    /// past whatever quantity was captured. Call this on a fresh engine scan before writing the
    /// sidecar.
    /// </summary>
    public static IReadOnlyList<PersonalInventoryCarryEntry> FilterCarriable(
        IEnumerable<PersonalInventoryCarryEntry> scanned)
    {
        List<PersonalInventoryCarryEntry> keep = [];
        foreach (PersonalInventoryCarryEntry entry in scanned)
        {
            if (!QuestItemCatalog.IsQuestItemTag(entry.Key.Resref))
            {
                keep.Add(entry);
            }
        }

        return keep;
    }

    /// <summary>
    /// What still needs to be added to <paramref name="alreadyPresent"/> (the freshly-adopted
    /// world's own pool, as this engine's own bridge scan reports it) to reach parity with
    /// <paramref name="carried"/> (the sidecar's own record). Only the EXCESS beyond what is
    /// already there is ever returned — idempotent under a repeated call (e.g. a retry after a
    /// partially-landed restore, or the same sidecar consulted again before the dedup marker in
    /// <see cref="ShouldSkipRestore"/> has been written), and correct even if the adopted world's
    /// pool happens to already hold some or all of a carried stack (a rejoin onto the SAME host
    /// world the sidecar was captured from, for instance).
    /// </summary>
    public static IReadOnlyList<PersonalInventoryCarryEntry> ComputeRestoreOps(
        IReadOnlyList<PersonalInventoryCarryEntry> carried,
        IReadOnlyDictionary<PartyPoolStackKey, int> alreadyPresent)
    {
        List<PersonalInventoryCarryEntry> ops = [];
        foreach (PersonalInventoryCarryEntry entry in carried)
        {
            int have = alreadyPresent.GetValueOrDefault(entry.Key);
            int need = entry.Count - have;
            if (need > 0)
            {
                ops.Add(entry with { Count = need });
            }
        }

        return ops;
    }

    /// <summary>
    /// The dedup guard docs/research/personal-inventory.md §6.3 step 3 describes: a restore
    /// already queued for a given staged world's own load generation must never be queued a
    /// second time for that SAME generation (the "never duplicate on rejoin" requirement) — but
    /// a genuinely NEW generation (a different world, or the same world reloaded/re-staged) is
    /// always eligible again. 0 is "unknown/never restored" and never matches a real generation,
    /// mirroring the house "an unknown value never blocks" convention this codebase's other
    /// compatibility checks already follow.
    /// </summary>
    public static bool ShouldSkipRestore(uint lastRestoredWorldEpoch, uint currentWorldEpoch) =>
        currentWorldEpoch != 0 && lastRestoredWorldEpoch == currentWorldEpoch;
}

namespace KotorCoop.Session;

/// <summary>
/// Pure per-slot max-merge for companion experience (docs/research/companion-state-sync.md,
/// roadmap item 11) — pulled out of <see cref="Pumps.PartyPump"/> so the rule itself can be
/// tested without a bridge or a session, the same split
/// <see cref="KotorCoop.Protocol.PartyRoster.AddsAgainst"/> already gets for the roster
/// channel.
///
/// The rule mirrors <see cref="KotorCoop.Protocol.PacketType.CompanionXpSnapshot"/>'s own doc
/// comment: both engines already grant kill XP to every active party member independently, so
/// combining two readings must never go backwards and must never invent a value neither side
/// actually reported. Concretely: a slot absent from BOTH inputs stays absent; a slot present
/// in only one is adopted exactly as reported; a slot present in both takes the higher value.
/// </summary>
public static class CompanionExperienceMerge
{
    /// <summary>
    /// Merges one more (<paramref name="candidateMask"/>, <paramref name="candidateXp"/>)
    /// reading into the accumulator in place. Used both for the host's per-peer-report loop
    /// (each report merged in turn, starting from the host's own reading) and a client's own
    /// host-vs-local merge (the host's snapshot merged into the client's local reading).
    /// </summary>
    public static void MergeInto(
        ref ushort mergedMask, uint[] merged, ushort candidateMask, IReadOnlyList<uint> candidateXp)
    {
        for (int slot = 0; slot < merged.Length && slot < candidateXp.Count; slot++)
        {
            if ((candidateMask & (1 << slot)) == 0)
            {
                continue;
            }

            bool alreadyPresent = (mergedMask & (1 << slot)) != 0;
            merged[slot] = alreadyPresent ? Math.Max(merged[slot], candidateXp[slot]) : candidateXp[slot];
            mergedMask |= (ushort)(1 << slot);
        }
    }
}

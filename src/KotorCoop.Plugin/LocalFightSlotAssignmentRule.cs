namespace KotorCoop.Plugin;

/// <summary>
/// The pure top-K assignment half of N-slot <c>local_combat</c>
/// (docs/research/combat-participation.md §11): given every candidate
/// <c>EnemyPresenceController.SelectImmediateCandidate</c> found eligible this scan (already
/// past <see cref="LocalFightCandidateRule.Evaluate"/>'s hard gates) and which physical
/// local-combat slots are currently free, decides which candidate — if any — each free slot
/// should arm THIS frame. Separated from <see cref="EnemyPresenceController"/> the same way
/// every other <c>*Rule</c> class in this file's neighbourhood is, so the ranking arithmetic is
/// unit-testable without a live engine underneath it.
///
/// Ranking is exactly <see cref="LocalFightCandidateRule.Beats"/>'s own ordering, applied slot
/// by slot in ascending physical-slot order: slot 0 always gets the single best remaining
/// candidate, slot 1 the next best, and so on. With exactly one free slot this reproduces the
/// pre-N-slot single-winner selection bit for bit — the reason <c>local_combat_slots=1</c>
/// reproduces today's exact behaviour.
///
/// A candidate can never win two slots in the same call: once assigned to a slot it is removed
/// from contention for every slot considered after it.
/// </summary>
internal static class LocalFightSlotAssignmentRule
{
    /// <summary>
    /// Fills <paramref name="assignedCandidateIndex"/>[s], for every s &lt;
    /// <paramref name="slotCount"/>, with the index into the parallel
    /// <paramref name="candidatePriority"/>/<paramref name="candidateDistance"/> arrays chosen
    /// for physical slot s, or -1 when slot s is already occupied
    /// (<paramref name="slotOccupied"/>[s] is true) or nothing eligible remains to offer it.
    ///
    /// <paramref name="candidateUsedScratch"/> is caller-owned scratch space, at least
    /// <paramref name="candidateCount"/> long — reset here rather than allocated, so a caller
    /// running this every frame pays no allocation.
    /// </summary>
    public static void Assign(
        LocalFightPriority[] candidatePriority,
        float[] candidateDistance,
        int candidateCount,
        bool[] slotOccupied,
        int slotCount,
        bool[] candidateUsedScratch,
        int[] assignedCandidateIndex)
    {
        for (int c = 0; c < candidateCount; c++)
        {
            candidateUsedScratch[c] = false;
        }

        for (int slot = 0; slot < slotCount; slot++)
        {
            assignedCandidateIndex[slot] = -1;

            if (slotOccupied[slot])
            {
                continue;
            }

            int bestIndex = -1;
            for (int c = 0; c < candidateCount; c++)
            {
                if (candidateUsedScratch[c])
                {
                    continue;
                }

                if (bestIndex == -1 || LocalFightCandidateRule.Beats(
                        candidatePriority[c], candidateDistance[c],
                        candidatePriority[bestIndex], candidateDistance[bestIndex]))
                {
                    bestIndex = c;
                }
            }

            if (bestIndex == -1)
            {
                continue;
            }

            candidateUsedScratch[bestIndex] = true;
            assignedCandidateIndex[slot] = bestIndex;
        }
    }
}

namespace KotorCoop.Memory;

/// <summary>What a <see cref="LiveObjectGate"/> pass did, for the caller's log.</summary>
/// <param name="Kept">How many candidates the engine's own object array confirmed.</param>
/// <param name="Dropped">How many were ghosts — bytes that look like an object but that the
/// array does not hold.</param>
/// <param name="Trusted">False when the gate refused to act on its own verdict because it
/// confirmed NOTHING; the candidates are passed through untouched in that case.</param>
public readonly record struct LiveObjectVerdict(int Kept, int Dropped, bool Trusted);

/// <summary>
/// Separates real objects from ghosts, by asking the engine's own id → object array whether
/// each scanned address is the object it claims to be.
///
/// ⚠ WHY THIS EXISTS. The agent finds creatures by scanning committed memory for the
/// <c>CSWSCreature</c> vtable word. That finds every live creature — and also every DEAD one
/// whose heap block has not been reused yet, because <c>operator delete</c> does not clear
/// the vtable word or the object id that follows it. Such a ghost reads a plausible tag,
/// position and hit points, so nothing downstream can tell it apart; measured live on
/// 2026-08-15, a host's scan held the same object id at two different addresses, and the
/// enemy census it fed published an id (<c>0x35</c>) that the host's OWN
/// <c>ResolveServerObject</c> could not resolve. A peer that forwards damage keyed by that id
/// is owed for ever, silently.
///
/// The array is the authority because it is the same lookup the debt is spent through
/// (<c>CServerExoApp::GetGameObject</c>), so "confirmed here" means exactly "an id a peer can
/// key work by".
///
/// ⚠ AND THE GATE DISTRUSTS ITSELF FIRST. If it confirms not one candidate out of many, the
/// likely fault is the gate — a wrong offset, a torn read, an array being rebuilt — not a
/// world that has suddenly become entirely imaginary. It passes everything through and says
/// so, because the failure mode of a wrong gate is a session with no enemies at all, which is
/// far worse than the ghosts it was built to remove.
/// </summary>
/// <param name="resolve">Maps an object id to the address the engine holds for it, or 0 when
/// the array does not hold the id at all.</param>
public sealed class LiveObjectGate(Func<uint, nint> resolve)
{
    /// <summary>
    /// Keeps the candidates whose id resolves to their own address.
    ///
    /// The address must match, not merely the id: a duplicate id in the scan (one live
    /// object, one ghost of the same creature from a previous module load) is exactly the
    /// case where "the id resolves" is true of both and only one of them is the object the
    /// engine will hand a peer's damage to.
    /// </summary>
    public List<T> Confirm<T>(IReadOnlyList<T> candidates, Func<T, (uint Id, nint Address)> key, out LiveObjectVerdict verdict)
    {
        List<T> kept = new(candidates.Count);

        foreach (T candidate in candidates)
        {
            (uint id, nint address) = key(candidate);

            if (id != 0 && address != 0 && resolve(id) == address)
            {
                kept.Add(candidate);
            }
        }

        if (kept.Count == 0 && candidates.Count > 0)
        {
            verdict = new LiveObjectVerdict(candidates.Count, 0, Trusted: false);
            return [.. candidates];
        }

        verdict = new LiveObjectVerdict(kept.Count, candidates.Count - kept.Count, Trusted: true);
        return kept;
    }
}

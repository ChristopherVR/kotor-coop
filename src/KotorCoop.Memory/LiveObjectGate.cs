namespace KotorCoop.Memory;

/// <summary>Which of the four things the liveness gate did on its last pass. Every one of
/// them is REPORTED; there is no state in which the gate says nothing, because "no line in
/// the log" was ambiguous between three of these and that cost a live round.</summary>
public enum CensusGateState
{
    /// <summary>No pass has run yet.</summary>
    NeverRun,

    /// <summary>Nothing was scanned — no world, or the scan came back empty.</summary>
    NothingToCheck,

    /// <summary>⚠ The server object array could not be resolved, so NOTHING WAS CHECKED and
    /// the scan was published as-is. Expected at the main menu and mid-transition; in a
    /// loaded world it means the pointer chain is wrong.</summary>
    NoObjectArray,

    /// <summary>The array answered and its verdict was acted on.</summary>
    Confirmed,

    /// <summary>The array confirmed nothing at all, so the gate distrusted ITSELF and passed
    /// everything through — a broken check is likelier than an imaginary world.</summary>
    Distrusted,
}

/// <summary>
/// What the liveness gate did last, in a shape both a log line and the <c>census</c> console
/// verb can render. Held as state rather than only logged so the question "is the gate
/// running, and what is it seeing?" can be ASKED on a live rig instead of waited for.
/// </summary>
public readonly record struct CensusGateReport(
    CensusGateState State,
    nint ObjectArray,
    int Scanned,
    int Confirmed,
    int Dropped,
    IReadOnlyList<uint> DroppedIds)
{
    /// <summary>
    /// One line, always safe to print, that names the state and its numbers.
    ///
    /// ⚠ KEPT UNDER <see cref="MaxLineLength"/>. The console answer channel is 96 bytes
    /// (<c>SharedBlock.ConsoleReplyLength</c>) and truncates without a mark, which on
    /// 2026-08-15 cut the tail off every diagnostic answer exactly where it became
    /// interesting — the sentence explaining the numbers survived and the numbers did not.
    /// A diagnostic that does not fit the channel it is read through is not a diagnostic, so
    /// the prose lives in this file's comments and the line carries facts.
    /// </summary>
    public string Describe() => State switch
    {
        CensusGateState.NeverRun => "gate: no pass yet",
        CensusGateState.NothingToCheck => "gate: nothing scanned (no world)",
        CensusGateState.NoObjectArray =>
            $"gate: NOT CHECKING - no object array; all {Scanned} pass unchecked",
        CensusGateState.Distrusted =>
            $"gate: array 0x{ObjectArray:X} confirmed 0 of {Scanned} - distrusting it, publishing all",
        _ => $"gate: array 0x{ObjectArray:X} confirmed {Confirmed} of {Scanned}"
            + (Dropped > 0
                ? $", dropped {Dropped} ({string.Join(" ", DroppedIds.Take(3).Select(id => $"0x{id:X}"))}"
                    + (Dropped > Math.Min(3, DroppedIds.Count) ? " ..)" : ")")
                : " (no ghosts)"),
    };

    /// <summary>The console answer channel is 96 bytes and silently truncates; every line a
    /// diagnostic emits must fit inside it with room for the caller's own prefix.</summary>
    public const int MaxLineLength = 88;
}

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

        return Decide(candidates, kept, out verdict);
    }

    /// <summary>
    /// Keeps the candidates whose id the array holds at all, for callers that have an id and
    /// no address to match it against — the published census, whose entries are wire records
    /// rather than memory.
    ///
    /// Same self-distrust rule, deliberately shared rather than re-implemented: the two gates
    /// guard the same invariant at different points on the path, and a rule that exists twice
    /// is a rule that will differ once.
    /// </summary>
    public List<T> ConfirmIds<T>(IReadOnlyList<T> candidates, Func<T, uint> key, out LiveObjectVerdict verdict)
    {
        List<T> kept = new(candidates.Count);

        foreach (T candidate in candidates)
        {
            uint id = key(candidate);
            if (id != 0 && resolve(id) != 0)
            {
                kept.Add(candidate);
            }
        }

        return Decide(candidates, kept, out verdict);
    }

    private static List<T> Decide<T>(IReadOnlyList<T> candidates, List<T> kept, out LiveObjectVerdict verdict)
    {
        if (kept.Count == 0 && candidates.Count > 0)
        {
            verdict = new LiveObjectVerdict(candidates.Count, 0, Trusted: false);
            return [.. candidates];
        }

        verdict = new LiveObjectVerdict(kept.Count, candidates.Count - kept.Count, Trusted: true);
        return kept;
    }
}

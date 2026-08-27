namespace KotorCoop.Protocol;

/// <summary>What <see cref="SoloSegmentMachine.Step"/> just decided, for the caller to log.
/// <see cref="SoloSegmentMachine.Active"/> is the level a per-frame caller should actually
/// drive off; this is only the EDGE, so a caller can tell "just started"/"just ended" from
/// "no change" without diffing the level itself.</summary>
public enum SoloSegmentEvent
{
    None,
    Began,
    Ended,
}

/// <summary>One pass's inputs to <see cref="SoloSegmentMachine.Step"/>, sampled fresh every
/// call — nothing here is remembered between calls except inside the machine itself.</summary>
/// <param name="Module">The local player's current module resref.</param>
/// <param name="CompanionCount">How many companions are in the active party RIGHT NOW (0..2) —
/// i.e. the roster's own <c>CSWPartyTable+0x00</c> count, not counting the PC.</param>
/// <param name="RemovedCompanionsAlive">Only meaningful on the pass a drop is actually
/// observed (<c>CompanionCount</c> having just gone from &gt;=1 to 0): whether the companions
/// this pass no longer has were alive (not downed/dead) the last time they were seen. Ignored
/// otherwise. A caller with nothing to report yet (no prior sighting) should pass true — see
/// the "not qualifying" rule below, which pairs this with a genuine drop edge so a vacuous
/// true here alone can never start a segment.</param>
/// <param name="PartySelectionOpen">Whether the party-select GUI (<c>CGuiInGame+0x78</c>,
/// <c>CSWGuiPartySelection</c>) is open right now — see docs/research/galaxy-map-and-party-
/// select.md §2. A roster drop while this is open is the player benching companions by hand,
/// not a story-forced strip, and must never start a segment.</param>
public readonly record struct SoloSegmentInputs(
    string Module,
    int CompanionCount,
    bool RemovedCompanionsAlive,
    bool PartySelectionOpen);

/// <summary>
/// Detects a story-forced solo segment: the local roster drops from a companion (or two) to
/// none, in one of <see cref="SoloSegmentCatalogue"/>'s modules, via the engine's own script
/// path — not because the player opened the party-select screen, not because nobody had been
/// recruited yet, and not because the companions who left were downed or dead. Ends when the
/// roster refills (a companion count of 1 or more again) or the module changes. See
/// docs/research/star-forge-finale.md §3.3/§3.5 and the product decision recorded there: this
/// is the OWNER's fight, the other human SPECTATES.
///
/// A DROP EDGE, not a level test on the count alone — <see cref="SoloSegmentInputs.CompanionCount"/>
/// at 0 is also the ordinary state of the game before the very first companion is ever
/// recruited (the Endar Spire), so only a transition FROM &gt;=1 DOWN TO 0 counts, and the very
/// first observation this machine ever sees never fires one (there is no "before" yet to have
/// dropped from — see <see cref="Invalidate"/>'s own doc comment).
///
/// Distinct from — and does NOT reuse — <c>PartyFreezeRule</c>: that rule answers "is
/// <c>CSWPartyTable+0xF0</c> non -1 right now" (a script has taken ONE specific companion under
/// direct control), which is a different engine fact that happens to correlate with some forced-
/// solo beats but is neither necessary nor sufficient here — the Bastila-chamber door strips the
/// WHOLE roster through nine plain <c>RemovePartyMember</c> calls, never touching +0xF0 at all
/// (docs/research/star-forge-finale.md §3.3).
/// </summary>
public sealed class SoloSegmentMachine
{
    private int _lastCompanionCount = -1;
    private string _activeModule = string.Empty;

    /// <summary>True while a segment is active. This is the LEVEL a per-frame caller should
    /// drive off (publish the wire flag, hide peers, etc.) — <see cref="Step"/>'s return value
    /// is only the edge.</summary>
    public bool Active { get; private set; }

    /// <summary>Runs one pass. Call this every frame/tick regardless of whether anything looks
    /// like it changed — the drop-edge detection needs to see every companion-count sample to
    /// tell a real drop from having simply missed a call.</summary>
    public SoloSegmentEvent Step(SoloSegmentCatalogue catalogue, SoloSegmentInputs inputs)
    {
        int before = _lastCompanionCount;
        int after = inputs.CompanionCount;
        _lastCompanionCount = after;

        if (Active)
        {
            bool moduleChanged = !string.Equals(_activeModule, inputs.Module, StringComparison.OrdinalIgnoreCase);
            bool rosterRefilled = after >= 1;

            if (!moduleChanged && !rosterRefilled)
            {
                return SoloSegmentEvent.None;
            }

            Active = false;
            _activeModule = string.Empty;
            return SoloSegmentEvent.Ended;
        }

        // The drop edge: strictly ">= 1 down to 0". `before == -1` (nothing observed yet, i.e.
        // this is the very first Step call, or the machine was just Invalidate()d) never
        // satisfies this — the baseline pass alone must never look like a drop.
        bool dropEdge = before >= 1 && after == 0;

        if (!dropEdge
            || !catalogue.Contains(inputs.Module)
            || !inputs.RemovedCompanionsAlive
            || inputs.PartySelectionOpen)
        {
            return SoloSegmentEvent.None;
        }

        Active = true;
        _activeModule = inputs.Module;
        return SoloSegmentEvent.Began;
    }

    /// <summary>
    /// Module change or session teardown: forget everything, including the companion-count
    /// baseline. The NEXT <see cref="Step"/> call only establishes a fresh baseline — it can
    /// never itself read as a drop, however different the new module's starting count is from
    /// whatever the old module last had.
    /// </summary>
    public void Invalidate()
    {
        _lastCompanionCount = -1;
        Active = false;
        _activeModule = string.Empty;
    }
}

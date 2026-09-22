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

    /// <summary>
    /// Whether an emptied roster has actually been OBSERVED (a real <see cref="Step"/> pass
    /// with <c>CompanionCount == 0</c>) since the segment became active — see
    /// <see cref="NoteRemovePartyMemberCall"/>'s own remarks for why this exists: the burst
    /// trigger can set <see cref="Active"/> true a frame or more BEFORE the roster-count read
    /// actually reflects the wipe (the hook fires at the exact VM call; the poll that feeds
    /// <see cref="Step"/> catches up whenever the next frame's read happens to land). Without
    /// this guard, that very next <see cref="Step"/> call would see <c>CompanionCount &gt;= 1</c>
    /// (still stale) and read it as "the roster refilled", ending the segment one frame after it
    /// began. Gating <c>rosterRefilled</c> on having seen a genuine empty roster at least once
    /// since <see cref="Active"/> went true closes that race without weakening the real end
    /// condition: a real refill (Malak dies, the roster restores) still ends the segment exactly
    /// as before, once the emptied state has actually been observed.
    /// </summary>
    private bool _sawEmptyRosterSinceActive;

    /// <summary>True while the active segment was begun by <see cref="NoteControlledNpc"/>. Such a
    /// segment ends ONLY when the controlled slot returns to -1 or the module changes: the roster
    /// count is meaningless during a rescuer segment (it is 0 from the strip, and the rescuer's
    /// own party can refill it), so <see cref="Step"/> must not end it on a "refill".</summary>
    private bool _controlledTrigger;

    private bool _lastControlled;

    /// <summary>True while the segment was begun by the Taris duel's verified
    /// <c>SetMinOneHP(GetFirstPC(), TRUE)</c> edge. Unlike a roster-drop segment, this trigger
    /// is useful when the player entered the ring with zero or one companion: the script's
    /// removal loop then produces fewer than two hook calls. It ends when the matching duel-end
    /// script clears the PC pin or the module changes.</summary>
    private bool _duelPinTrigger;

    private bool _duelPinStillSet;
    private bool _lastDuelPin;

    /// <summary>Recent <c>Environment.TickCount</c>-style timestamps of
    /// <see cref="NoteRemovePartyMemberCall"/> calls, oldest first, capped at
    /// <see cref="MaxRecentCalls"/> (a real wipe fires exactly nine — see
    /// docs/research/star-forge-finale.md §9.2 step 4 — so nine is enough to see every call of
    /// one wipe without growing unbounded across a long session).</summary>
    private readonly int[] _recentCallTicks = new int[MaxRecentCalls];

    private int _recentCallCount;

    private const int MaxRecentCalls = 9;

    /// <summary>The burst trigger's own debounce window: at least two calls inside this many
    /// milliseconds of each other. ~1s per the task brief — generous next to how fast the real
    /// nine-call wipe actually fires (all nine land inside a single script's own execution,
    /// effectively one frame), while still tolerant of the hook's own capture jitter.</summary>
    private const int BurstWindowMs = 1000;

    /// <summary>True while a segment is active. This is the LEVEL a per-frame caller should
    /// drive off (publish the wire flag, hide peers, etc.) — <see cref="Step"/>'s return value
    /// is only the edge.</summary>
    public bool Active { get; private set; }

    /// <summary>Runs one pass. Call this every frame/tick regardless of whether anything looks
    /// like it changed — the drop-edge detection needs to see every companion-count sample to
    /// tell a real drop from having simply missed a call.</summary>
    public SoloSegmentEvent Step(
        SoloSegmentCatalogue catalogue,
        SoloSegmentInputs inputs,
        bool allowRosterDropTrigger = true)
    {
        int before = _lastCompanionCount;
        int after = inputs.CompanionCount;
        _lastCompanionCount = after;

        if (Active)
        {
            if (_controlledTrigger)
            {
                // Owned by NoteControlledNpc — see _controlledTrigger.
                return SoloSegmentEvent.None;
            }

            if (_duelPinTrigger)
            {
                bool duelModuleChanged = !string.Equals(_activeModule, inputs.Module, StringComparison.OrdinalIgnoreCase);
                if (!duelModuleChanged && _duelPinStillSet)
                {
                    return SoloSegmentEvent.None;
                }

                Active = false;
                _duelPinTrigger = false;
                _duelPinStillSet = false;
                _activeModule = string.Empty;
                return SoloSegmentEvent.Ended;
            }

            if (after == 0)
            {
                _sawEmptyRosterSinceActive = true;
            }

            bool moduleChanged = !string.Equals(_activeModule, inputs.Module, StringComparison.OrdinalIgnoreCase);

            // See _sawEmptyRosterSinceActive's own doc comment: a segment the BURST trigger
            // just started must not read a still-stale (not-yet-dropped) roster count as a
            // refill on the very next pass.
            bool rosterRefilled = after >= 1 && _sawEmptyRosterSinceActive;

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

        if (!allowRosterDropTrigger
            || !dropEdge
            || !catalogue.Contains(inputs.Module)
            || !inputs.RemovedCompanionsAlive
            || inputs.PartySelectionOpen)
        {
            return SoloSegmentEvent.None;
        }

        Active = true;
        _activeModule = inputs.Module;

        // The drop edge itself IS the empty-roster observation (after == 0, by construction of
        // dropEdge above) — no race to guard against here, unlike the burst trigger.
        _sawEmptyRosterSinceActive = true;
        return SoloSegmentEvent.Began;
    }

    /// <summary>
    /// The script-agnostic second trigger (docs/research/star-forge-finale.md §11): called once
    /// per real <c>ExecuteCommandRemovePartyMember</c> engine call the plugin's own detour
    /// observes (<c>RemovePartyMemberHook</c>), independent of and in addition to
    /// <see cref="Step"/>'s roster-count poll. A burst of at least two calls within
    /// <see cref="BurstWindowMs"/> of each other, while in a catalogued module (or ANY module
    /// when <paramref name="anyModuleAllowed"/>), begins a segment immediately — the same
    /// product decision <see cref="Step"/>'s drop edge makes, reached by a different, more
    /// direct signal (docs/research/star-forge-finale.md §9.5's own cost/benefit: "fires at the
    /// exact moment of ground truth, not a polled diff").
    ///
    /// A single call never begins one — the whole point of the debounce is that ONE
    /// <c>RemovePartyMember</c> call is an ordinary, unremarkable script action (a companion
    /// benched normally, a single roster tidy-up); nine in one burst is what marks a story-forced
    /// strip. Every call is still recorded even when it does not (yet) qualify, so a second
    /// qualifying call moments later is judged against the true recent history, not a history
    /// reset by the first call's own refusal.
    ///
    /// A no-op (returns <see cref="SoloSegmentEvent.None"/>, but the call is still recorded)
    /// while <see cref="Active"/> is already true — <see cref="Step"/> alone owns the end
    /// conditions once a segment is running, exactly as it already does for the drop-edge path.
    /// </summary>
    public SoloSegmentEvent NoteRemovePartyMemberCall(
        SoloSegmentCatalogue catalogue, string module, bool anyModuleAllowed, int nowTicks,
        bool allowTrigger = true)
    {
        if (_recentCallCount < MaxRecentCalls)
        {
            _recentCallTicks[_recentCallCount++] = nowTicks;
        }
        else
        {
            Array.Copy(_recentCallTicks, 1, _recentCallTicks, 0, MaxRecentCalls - 1);
            _recentCallTicks[MaxRecentCalls - 1] = nowTicks;
        }

        if (Active || !allowTrigger)
        {
            return SoloSegmentEvent.None;
        }

        if (!anyModuleAllowed && !catalogue.Contains(module))
        {
            return SoloSegmentEvent.None;
        }

        // Wrap-safe: Environment.TickCount wraps roughly every 49.7 days, and unchecked
        // subtraction of two nearby ticks is correct across the wrap the same way every other
        // TickCount-lease comparison in this codebase already relies on (see
        // DialogueGateHook.PeerHoldsConversation).
        int withinWindow = 0;
        for (int i = 0; i < _recentCallCount; i++)
        {
            if (unchecked(nowTicks - _recentCallTicks[i]) <= BurstWindowMs)
            {
                withinWindow++;
            }
        }

        if (withinWindow < 2)
        {
            return SoloSegmentEvent.None;
        }

        Active = true;
        _activeModule = module;

        // Deliberately NOT set true here — see this field's own doc comment. The roster read
        // that would confirm the wipe has typically not even run yet; Step's own Active-branch
        // sets it the first time it genuinely observes CompanionCount == 0.
        _sawEmptyRosterSinceActive = false;
        return SoloSegmentEvent.Began;
    }

    /// <summary>
    /// The third trigger (docs/research/leviathan.md §5.3 #1): a script took a companion under the
    /// player's control (<c>SwitchPlayerCharacter</c>, <c>CSWPartyTable+0xF0</c> from -1 to a slot)
    /// in a module <see cref="SoloSegmentCatalogue.ContainsControlledNpcModule"/> lists. Begins a
    /// segment on the -1 to slot edge and ends it on slot to -1 or a module change. Call every pass,
    /// before <see cref="Step"/>; <paramref name="controlled"/> is <c>+0xF0 != -1</c>.
    /// The first observation counts as an edge (a joiner arriving mid-segment must park too).
    /// </summary>
    public SoloSegmentEvent NoteControlledNpc(string module, bool controlled)
    {
        bool before = _lastControlled;
        _lastControlled = controlled;

        if (Active && _controlledTrigger)
        {
            bool moduleChanged = !string.Equals(_activeModule, module, StringComparison.OrdinalIgnoreCase);
            if (controlled && !moduleChanged)
            {
                return SoloSegmentEvent.None;
            }

            Active = false;
            _controlledTrigger = false;
            _activeModule = string.Empty;
            return SoloSegmentEvent.Ended;
        }

        if (Active || !controlled || before || !SoloSegmentCatalogue.ContainsControlledNpcModule(module))
        {
            return SoloSegmentEvent.None;
        }

        Active = true;
        _controlledTrigger = true;
        _activeModule = module;
        _sawEmptyRosterSinceActive = true;
        return SoloSegmentEvent.Began;
    }

    /// <summary>
    /// Notes the local PC's <c>CSWSObject +0x200</c> min-one-hit-point flag. The caller must
    /// pass the flag read from the local player creature, not from a companion or phantom.
    /// Only a rising edge in a module whose shipped script is proven to call
    /// <c>SetMinOneHP(GetFirstPC(), TRUE)</c> can begin a segment. A rise while the party
    /// selection GUI is open is deliberately consumed and never replays when that GUI closes,
    /// so an optional manual solo/bench action cannot become a story duel. A later clear is the
    /// matching <c>k_ptar_duelend*</c> boundary and ends the segment even when the roster remains
    /// at one or zero companions.
    /// </summary>
    public SoloSegmentEvent NoteDuelPlayerPin(string module, bool pinned, bool partySelectionOpen)
    {
        bool supported = SoloSegmentCatalogue.ContainsDuelPinModule(module);
        if (!supported)
        {
            _lastDuelPin = false;
            _duelPinStillSet = false;
            return SoloSegmentEvent.None;
        }

        bool wasPinned = _lastDuelPin;
        _lastDuelPin = pinned;

        if (Active)
        {
            if (_duelPinTrigger)
            {
                _duelPinStillSet = pinned;
            }

            return SoloSegmentEvent.None;
        }

        if (!pinned || wasPinned || partySelectionOpen)
        {
            return SoloSegmentEvent.None;
        }

        Active = true;
        _duelPinTrigger = true;
        _duelPinStillSet = true;
        _activeModule = module;
        _sawEmptyRosterSinceActive = true;
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
        _sawEmptyRosterSinceActive = false;
        _controlledTrigger = false;
        _lastControlled = false;
        _duelPinTrigger = false;
        _duelPinStillSet = false;
        _lastDuelPin = false;
        _recentCallCount = 0;
    }
}

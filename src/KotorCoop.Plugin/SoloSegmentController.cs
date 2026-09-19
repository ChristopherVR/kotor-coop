using System.Text;
using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// OWNER half of solo-segment spectate (item: <c>solo_segment_spectate</c>,
/// docs/research/star-forge-finale.md §3.3/§3.5): detects, on THIS instance, whether the local
/// player has just become the owner of a story-forced solo segment — the roster stripped by
/// the Bastila-chamber door's nine <c>RemovePartyMember</c> calls, or the continuation of that
/// state through Malak's chamber — and publishes it as <c>block.LocalInSoloSegment</c> for the
/// agent to carry onto <c>PlayerState.FlagSoloSegment</c>.
///
/// All the DECIDING lives in <see cref="SoloSegmentMachine"/> (KotorCoop.Protocol, pure,
/// unit-tested); this class is purely engine reads feeding it and a shared-block write —
/// exactly the split <c>MinigameController</c>/<c>MinigameRule</c> and
/// <c>PartyFreezeRule</c>/<c>PartyPresenceController</c> already use.
///
/// The peer-facing REACTION — parking a spectator's input, the camera follow, the damage gate —
/// is <see cref="SoloSegmentSpectateController"/>, a separate class: this one only ever answers
/// "am I, personally, mid-duel right now", and the hide-all-peers half of the reaction
/// (<c>SoloSegmentRule.ShouldHideAllPeers</c>) is applied agent-side, in
/// <c>CoopAgent.PumpOnce</c>'s own peer-visibility filter — this controller does not touch
/// remote peers at all.
///
/// Kill switch: <c>solo_segment_spectate=0</c> in coop.ini (default ON — live-verified
/// 2026-08-28 via the script-agnostic <see cref="RemovePartyMemberHook"/> burst trigger: the
/// splice installs, a burst begins a segment in both a catalogued module and, with
/// <c>solo_segment_any_module=1</c>, an uncatalogued one too, the owner-side roster genuinely
/// empties, and a real refill ends it — see docs/research/star-forge-finale.md §12).
/// </summary>
internal sealed unsafe class SoloSegmentController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("solo_segment_spectate", true);

    private readonly SoloSegmentCatalogue _catalogue =
        new(SoloSegmentCatalogue.ParseExtraModules(PluginConfig.Read("solo_segment_modules")));

    /// <summary>coop.ini <c>solo_segment_any_module</c> (default 0/OFF): drops the module gate
    /// on the <see cref="RemovePartyMemberHook"/> burst trigger ONLY — the roster-drop-edge
    /// trigger (<see cref="SoloSegmentMachine.Step"/>) stays catalogue-gated regardless, since it
    /// has no equivalent "this is definitely a script, not a bench/death" signal on its own. See
    /// docs/research/star-forge-finale.md §11 and §9.5's own "generalises to ANY future
    /// forced-solo content this mod hasn't researched yet" reasoning.</summary>
    private readonly bool _anyModule = PluginConfig.ReadBool("solo_segment_any_module", false);

    private readonly SoloSegmentMachine _machine = new();

    /// <summary>Highest <see cref="RemovePartyMemberHook"/> serial already drained, so a capture
    /// already fed to <see cref="_machine"/> is never fed twice. Same dedup shape
    /// <see cref="ItemTemplateSpawnController"/> already uses for its own hook ring.</summary>
    private uint _lastDrainedRemovePartySerial;

    /// <summary>Scratch space for <see cref="DrainRemovePartyMemberHook"/>, reused every frame
    /// rather than allocated fresh — sized to <see cref="RemovePartyMemberHook.Capacity"/>, the
    /// most entries a single drain could ever see.</summary>
    private readonly uint[] _freshSerials = new uint[RemovePartyMemberHook.Capacity];

    private readonly int[] _freshSlots = new int[RemovePartyMemberHook.Capacity];
    private readonly int[] _freshTicks = new int[RemovePartyMemberHook.Capacity];
    private readonly int[] _freshOrder = new int[RemovePartyMemberHook.Capacity];

    /// <summary>CGuiInGame + this = the party-selection panel pointer
    /// (<c>CSWGuiPartySelection*</c>), non-null exactly while the screen is open. See
    /// docs/research/galaxy-map-and-party-select.md §2 — the SAME offset
    /// <c>Odyssey.Dialogue.Follow.cs</c>'s own class comment documents having been confused
    /// with the dialogue panel once already; do not reuse that mistake here.</summary>
    private const int GuiPartySelectionPanel = 0x78;

    /// <summary>party table + this = companion count, 0..2 (not counting the PC). Same field
    /// <c>Odyssey.Party.cs</c>'s own <c>TryReadPartyRoster</c> reads for the party MASK; this
    /// controller wants the plain count, which that method does not return separately, so it is
    /// derived here from the mask it does return (popcount).</summary>
    private const int PartyRosterSlots = Odyssey.PartyRosterSlots;

    /// <summary>Per-NPC-roster-slot: was the companion in that slot alive (current hit points
    /// &gt; 0) the last time this controller actually saw them present in the party? Read on
    /// every pass a slot is present, so a drop observed the very next pass compares against a
    /// value that is at most one poll old.</summary>
    private readonly bool[] _lastKnownAlive = new bool[PartyRosterSlots];

    private uint _lastPartyMask;

    private ulong _epoch;

    public void Invalidate()
    {
        _machine.Invalidate();
        RemovePartyMemberHook.Invalidate();
        _lastDrainedRemovePartySerial = 0;
        Array.Clear(_lastKnownAlive);
        _lastPartyMask = 0;
        block.LocalInSoloSegment = 0;
    }

    /// <summary>Session end: same as a module change for this controller's own state — there
    /// is no engine write to undo here (unlike the spectate half), only the level to drop.</summary>
    public void ReleaseSession() => Invalidate();

    public void OnFrame()
    {
        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            return;
        }

        if (Odyssey.TryGetWorldEpoch(out ulong epoch))
        {
            if (epoch != _epoch)
            {
                if (_epoch != 0)
                {
                    Invalidate();
                }

                _epoch = epoch;
            }
        }
        else if (_epoch != 0)
        {
            Invalidate();
        }

        if (!Odyssey.TryReadPartyRoster(out _, out uint partyMask))
        {
            // No party table to read yet (main menu, mid-transition) — nothing to decide this
            // pass. Deliberately does NOT touch block.LocalInSoloSegment: a transient read
            // failure must not blink an active segment off and back on.
            return;
        }

        bool removedAlive = UpdateAliveTrackingAndCheckRemoved(partyMask);
        _lastPartyMask = partyMask;

        int companionCount = System.Numerics.BitOperations.PopCount(partyMask);

        byte* moduleBuf = stackalloc byte[SharedBlock.ModuleNameLength];
        int moduleLength = Odyssey.ReadCurrentModuleName(moduleBuf, SharedBlock.ModuleNameLength);
        string module = moduleLength > 0 ? Encoding.ASCII.GetString(moduleBuf, moduleLength) : string.Empty;

        bool partySelectionOpen = IsPartySelectionOpen();

        SoloSegmentEvent burstEvent = DrainRemovePartyMemberHook(module);
        if (burstEvent == SoloSegmentEvent.Began)
        {
            PluginHost.Log($"solo segment: began in {module} — script trigger "
                + "(RemovePartyMember burst), roster read not required to have caught up yet");
        }

        // The third trigger (docs/research/leviathan.md §5.3 #1): a script put the PLAYER under a
        // companion (SwitchPlayerCharacter, party table +0xF0 -1 -> slot) in the Leviathan brig.
        // Runs before Step, which stays out of a segment this trigger owns.
        SoloSegmentEvent controlledEvent = _machine.NoteControlledNpc(
            module, Odyssey.IsPlayerUnderCompanionControl());
        if (controlledEvent == SoloSegmentEvent.Began)
        {
            PluginHost.Log($"solo segment: began in {module} — a script put the player under a companion "
                + "(SwitchPlayerCharacter, party table +0xF0); a peer here will spectate the host's feed (NEEDS-LIVE)");
        }
        else if (controlledEvent == SoloSegmentEvent.Ended)
        {
            PluginHost.Log("solo segment: ended (the player is back in their own body, or the module changed)");
        }

        SoloSegmentEvent evt = _machine.Step(_catalogue, new SoloSegmentInputs(
            module, companionCount, removedAlive, partySelectionOpen));

        block.LocalInSoloSegment = _machine.Active ? 1u : 0u;

        switch (evt)
        {
            case SoloSegmentEvent.Began:
                PluginHost.Log($"solo segment: began in {module} — this fight is yours, "
                    + "a peer here will spectate");
                break;
            case SoloSegmentEvent.Ended:
                PluginHost.Log("solo segment: ended (roster refilled or the module changed)");
                break;
        }
    }

    /// <summary>
    /// Moves every fresh <see cref="RemovePartyMemberHook"/> capture into
    /// <see cref="_machine"/>'s own burst detector, one call at a time and in capture order (the
    /// debounce window cares about relative timing, so entries must be fed oldest-first). Every
    /// capture gets its own debug log line — deliberately UNCONDITIONAL on whatever
    /// <see cref="SoloSegmentMachine.NoteRemovePartyMemberCall"/> decides, so a live pass can see
    /// the wipe fire (or not) regardless of the machine's own decision (docs/research/star-forge-
    /// finale.md §11). Returns the last <see cref="SoloSegmentEvent.Began"/> seen this pass, if
    /// any — there is at most one real one per drain, since the machine no-ops every call after
    /// the first while already active.
    /// </summary>
    private SoloSegmentEvent DrainRemovePartyMemberHook(string module)
    {
        // Walk every slot rather than tracking which ones wrapped since last pass — the ring is
        // small (RemovePartyMemberHook.Capacity) and this runs once a frame, the same reasoning
        // ItemTemplateSpawnController's own identically-shaped drain already uses. Collected
        // (not fed immediately) because ring INDEX order is not necessarily SERIAL order once
        // the write cursor has wrapped, and the burst debounce cares about relative call timing
        // — entries must reach the machine oldest-first, so freshCount entries are sorted by
        // serial (an insertion sort over at most RemovePartyMemberHook.Capacity items) before
        // any of them is fed in.
        int freshCount = 0;
        uint highest = _lastDrainedRemovePartySerial;

        for (int i = 0; i < RemovePartyMemberHook.Capacity; i++)
        {
            RemovePartyMemberHook.Read(i, out int npcSlot, out int tickMs, out uint serial);

            if (serial == 0 || serial <= _lastDrainedRemovePartySerial)
            {
                continue;
            }

            int insertAt = freshCount;
            while (insertAt > 0 && _freshSerials[_freshOrder[insertAt - 1]] > serial)
            {
                _freshOrder[insertAt] = _freshOrder[insertAt - 1];
                insertAt--;
            }

            _freshOrder[insertAt] = freshCount;
            _freshSerials[freshCount] = serial;
            _freshSlots[freshCount] = npcSlot;
            _freshTicks[freshCount] = tickMs;
            freshCount++;

            if (serial > highest)
            {
                highest = serial;
            }
        }

        _lastDrainedRemovePartySerial = highest;

        SoloSegmentEvent lastBegan = SoloSegmentEvent.None;

        for (int i = 0; i < freshCount; i++)
        {
            int j = _freshOrder[i];
            int npcSlot = _freshSlots[j];
            int tickMs = _freshTicks[j];

            PluginHost.Log($"solo segment debug: RemovePartyMember slot {npcSlot} in {module}");

            SoloSegmentEvent evt = _machine.NoteRemovePartyMemberCall(_catalogue, module, _anyModule, tickMs);
            if (evt == SoloSegmentEvent.Began)
            {
                lastBegan = evt;
            }
        }

        return lastBegan;
    }

    /// <summary>Refreshes <see cref="_lastKnownAlive"/> for every slot currently present, and
    /// returns whether every slot that WAS present last pass but is not present this pass was
    /// alive the last time it was seen. Vacuously true when nothing left — see
    /// <see cref="SoloSegmentMachine"/>'s own doc comment for why a vacuous true here can never
    /// start a segment by itself (it still needs a real drop edge, the module gate and the
    /// party-select gate).</summary>
    private bool UpdateAliveTrackingAndCheckRemoved(uint partyMask)
    {
        for (int slot = 0; slot < PartyRosterSlots; slot++)
        {
            if ((partyMask & (1u << slot)) == 0)
            {
                continue;
            }

            uint objectId = Odyssey.GetPartyRosterObjectId(slot);
            if (objectId is 0 or Odyssey.ObjectInvalid)
            {
                continue;
            }

            nint creature = Odyssey.ResolveServerObject(objectId);
            if (creature != 0)
            {
                _lastKnownAlive[slot] = Odyssey.GetCurrentHitPoints(creature) > 0;
            }
        }

        bool allRemovedWereAlive = true;
        for (int slot = 0; slot < PartyRosterSlots; slot++)
        {
            bool wasPresent = (_lastPartyMask & (1u << slot)) != 0;
            bool stillPresent = (partyMask & (1u << slot)) != 0;

            if (wasPresent && !stillPresent && !_lastKnownAlive[slot])
            {
                allRemovedWereAlive = false;
            }
        }

        return allRemovedWereAlive;
    }

    /// <summary>Range-only pointer sanity — every file in this plugin keeps its own private
    /// copy of this guard rather than sharing one (see e.g. <c>MapRevealController</c>'s own).</summary>
    private static bool LooksLikeHeapPointer(nint p) => p is > 0x10000 and < 0x7FFF0000;

    private static bool IsPartySelectionOpen()
    {
        nint gui = EngineHandles.GetInGameGui();
        if (gui == 0)
        {
            return false;
        }

        nint panel = *(nint*)(gui + GuiPartySelectionPanel);
        return LooksLikeHeapPointer(panel);
    }
}

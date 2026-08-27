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
/// Kill switch: <c>solo_segment_spectate=0</c> in coop.ini (default OFF — NEEDS-LIVE; nothing
/// here has been exercised against a real Star Forge save yet).
/// </summary>
internal sealed unsafe class SoloSegmentController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("solo_segment_spectate", false);

    private readonly SoloSegmentCatalogue _catalogue =
        new(SoloSegmentCatalogue.ParseExtraModules(PluginConfig.Read("solo_segment_modules")));

    private readonly SoloSegmentMachine _machine = new();

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

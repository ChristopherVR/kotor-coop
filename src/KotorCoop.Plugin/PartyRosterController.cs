using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// Both halves of the companion ROSTER channel.
///
/// PUBLISHES this instance's roster — which of the nine NPC slots are recruited, and which are
/// in the active party — every frame, so the host's agent has something to broadcast; and, on
/// an instance that is being told a roster, RECONCILES its own to match by calling the engine's
/// own AddPartyMember / RemovePartyMember / RemoveAvailableNPC routines.
///
/// The gap it fills: those two calls had no channel at all. A conversation that recruits or
/// dismisses a companion runs its scripts on ONE instance — the other is spectating a token,
/// not executing dialogue — so only one party table hears about it. And the party table is the
/// key the whole companion channel resolves by: PartyPresenceController takes the host's "NPC
/// slot 2" and looks slot 2 up in its OWN table. Diverge the tables and the mirror drives the
/// wrong body, or nothing at all, silently and permanently. That is the blocker recorded
/// against <c>force_dialogue_party</c> in InteractionLockController.
///
/// ⚠ A LEVEL, NOT EDGES, and the reasoning is the same one that shaped every channel here that
/// matters: a roster divergence never heals on its own, so a correction sent once is a
/// correction that a single dropped datagram loses forever. The host re-states the complete
/// roster every tick and this reconciles against it every pass; the steady state is that there
/// is nothing to do, which costs two masks and a compare.
///
/// Recruitment itself (AddAvailableNPCByTemplate) needs the blueprint the script named, which
/// is not readable from the roster afterwards — party table +0x30 is a bare flag, set to the
/// literal 1 at 0x00564350. What always diverges in practice is the PARTY SEAT, which a partial
/// or aborted conversation gets wrong on its own, and that is what this reconciles every pass.
///
/// AVAILABILITY additions and removals are reconciled too, behind <c>dialogue_recruit_sync</c>
/// (docs/research/dialogue-consequences.md §4.2), ON BY DEFAULT SINCE 2026-09-18: since K1's
/// nine companion slots are a fixed, game-defined mapping identical on both installs, the
/// ALREADY-broadcast <c>hostAvailable</c> mask is itself sufficient — no wire payload needed —
/// to recruit a peer's extra slot locally through the real engine call
/// (<see cref="Odyssey.AddAvailableNpcByTemplate"/>). Removals use a stricter rule: the host
/// must first publish the bit and then publish a falling edge; an initial host zero is not a
/// destructive command because a client-led recruiting dialogue can briefly lead the host's
/// mirrored script. With the flag OFF a slot the peer has and this instance never recruited is
/// logged, once, and skipped — see <see cref="_unavailableLogged"/> — and only whoever ran the
/// recruiting conversation keeps the companion. History of the default: an EARLIER build poked
/// the availability flag directly
/// (<see cref="Odyssey.TrySetSlotAvailable"/>), which docs/research/party-reconciliation.md
/// Round 75 live-confirmed insufficient on its own (the subsequent spawn has nothing to build
/// from — see that method's own doc comment for the disassembled root cause);
/// <see cref="Odyssey.AddAvailableNpcByTemplate"/> replaced it, going through the same STORY
/// template the engine's own recruitment conversations use. The flag stayed OFF while the
/// per-slot template MAPPING had a single live data point (slot 2 = Carth); all nine entries
/// have since been statically verified against the shipped recruit scripts
/// (<see cref="PartyRosterTemplateTable"/>), two slots (Carth, Zaalbar) are live-confirmed, and
/// the 2026-09-18 seamless-playthrough review (docs/review-seamless-2026-09-18.md) found that
/// shipping OFF split every roster after Taris, so it now ships ON. The seven never-exercised
/// slots remain a live-test target, not a reason to keep the default off.
///
/// ⚠ A RECRUITED COMPANION WHO WAS ALSO A PLACED NPC CAN ALREADY BE STANDING HERE (found live
/// 2026-08-24, Zaalbar/tar_m05aa, docs/research/dialogue-consequences.md's "stale mirrored body"
/// entry). Before falling back to <see cref="Odyssey.AddAvailableNpcByTemplate"/>,
/// <see cref="Reconcile"/> checks the peer's own party-table object id for the slot (published
/// over this SAME channel, GIT-identical to a local body if one exists) and reuses it directly
/// when a live creature with that id resolves here — no template, no fresh spawn, and no
/// duplicate left standing where the original body always was. NEEDS-LIVE.
///
/// ⚠ RECRUITING TAKES TWO PASSES, DELIBERATELY (found live 2026-08-23,
/// docs/research/dialogue-consequences.md). <see cref="Odyssey.AddAvailableNpcByTemplate"/>'s
/// own object is off-stage, not a live in-world body — see <see cref="PartyRosterRecruitRule"/>'s
/// doc comment for the disassembled reason it can never satisfy AddMember's own live-body check.
/// <see cref="Reconcile"/> no longer attempts the add in the same pass it recruits: the pass that
/// sets availability returns, and the very next pass (the ordinary "recruited but bodyless"
/// branch, already used for dismiss/rejoin) spawns a real body and adds it.
///
/// Kill switch: <c>party_roster_sync=0</c> in coop.ini.
/// </summary>
internal sealed unsafe class PartyRosterController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("party_roster_sync", true);

    /// <summary>
    /// Whether an availability bit the peer has and this instance lacks is RECRUITED locally
    /// through the real engine call (<see cref="Odyssey.AddAvailableNpcByTemplate"/>) rather than
    /// only logged and skipped — docs/research/dialogue-consequences.md §4.2. ON by default
    /// since 2026-09-18 (see the class comment for the history; the per-slot template mapping is
    /// statically verified for all nine slots and live-confirmed for two).
    /// coop.ini <c>dialogue_recruit_sync</c>, overridable per session by the host — see
    /// <see cref="EffectiveBehaviourFlag"/>.
    /// </summary>
    private readonly EffectiveBehaviourFlag _pokeAvailability = new(
        "dialogue_recruit_sync", SharedBlock.OverrideBitDialogueRecruitSync,
        PluginConfig.ReadBool("dialogue_recruit_sync", true));

    /// <summary>
    /// docs/research/party-reconciliation.md's client-door root cause, and
    /// <c>Odyssey.TryClampClientPartyCount</c>'s own doc comment for the mechanism: CSWParty
    /// (the struct <c>CSWParty::IsPartyTogether</c> actually reads) holds a HIGHER member count
    /// than CSWPartyTable's because it counts the PC too — not staleness, as first diagnosed.
    /// On a client the companions in it are mirrored bodies standing wherever the host's are,
    /// so IsPartyTogether refuses every door; clamping the count past them is what lets a
    /// client cross. coop.ini <c>party_together_fix</c>, default ON, CLIENT-ONLY since
    /// 2026-08-22: an earlier build ran it on the host as well and took the host's own
    /// companions out of the struct their control and portraits read (see
    /// <see cref="SyncClientPartyTogetherState"/>). Restricting it to clients on the SAME day
    /// let it reach a client scenario it had never run against before — a player who was
    /// already controlling a companion at the moment of connect — and trimmed the PC's OWN
    /// slot instead of a companion's, locking control onto the companion
    /// (docs/research/control-return.md, <see cref="PartyTogetherClampRule"/>). The clamp now
    /// defers while that is true.
    /// </summary>
    private readonly bool _togetherFixEnabled = PluginConfig.ReadBool("party_together_fix", true);

    /// <summary>
    /// Frames between reconciliation passes. Deliberately slow: a roster changes a handful of
    /// times in a playthrough, and each pass that finds work to do enters the script VM. The
    /// PUBLISH half runs every frame regardless — it is three array reads and a dword store,
    /// and the host's broadcast should never be stale.
    /// </summary>
    private const int IntervalFrames = 30;

    private int _countdown;
    private string _occupantWorldModule = "";
    private uint _occupantWorldSerial, _occupantWorldEpoch;

    /// <summary>Bit per slot: this instance has already reported that it cannot honour an add
    /// there because the companion was never recruited here. Reset when the situation changes,
    /// so the log records a real state change rather than one line every half second.</summary>
    private uint _unavailableLogged;

    /// <summary>Bit per slot: the spawn-then-add branch has already reported that
    /// <c>SpawnAvailableNpc</c> refused this slot. Live 2026-09-03 01:31 the line
    /// <c>could not spawn NPC slot 2 (failure 0)</c> was written 849 times in a row, once per
    /// pass, for a companion whose body was standing in the module the whole time (see
    /// <see cref="TryReuseGitPlacedBody"/>). Reset with <see cref="_unavailableLogged"/>.</summary>
    private uint _spawnRefusedLogged;
    private uint _recruitRefusedLogged;

    /// <summary>Host-confirmed availability falling edges awaiting local application. An initial
    /// host zero is never enough to populate this mask; the previous host level must have carried
    /// the bit first.</summary>
    private uint _hostAvailabilityRemovalEdges;

    /// <summary>Do not let a failed native availability removal fill the log every reconcile
    /// pass. A successful retry clears the bit so a later failure is visible again.</summary>
    private uint _availabilityRemovalFailedLogged;

    private uint _lastLocalParty = uint.MaxValue;
    private uint _lastLocalAvailable = uint.MaxValue;
    private uint _lastHostAvailable = PartyRosterAvailabilityRule.UnknownMask;
    private bool _lastHostValid;

    /// <summary>A module change reassigns every object id, so the cached "I already complained
    /// about slot N" state means nothing; the roster itself is save state and survives.</summary>
    public void Invalidate()
    {
        _unavailableLogged = 0;
        _spawnRefusedLogged = 0;
        _recruitRefusedLogged = 0;
        _hostAvailabilityRemovalEdges = 0;
        _availabilityRemovalFailedLogged = 0;
        _lastLocalParty = uint.MaxValue;
        _lastLocalAvailable = uint.MaxValue;
        _lastHostAvailable = PartyRosterAvailabilityRule.UnknownMask;
        _lastHostValid = false;
        _countdown = 0;
        // Native teardown does not yet distinguish travel from save replacement.
        // Captured identities are classified against the next resolved world below.
        PartyOccupantReconcileState.Invalidate();
        block.ClearLocalPartyRoster();

        // A module change reassigns every object id (docs/research/dialogue-consequences.md's
        // 2026-08-24 "stale mirrored body" entry, the same reasoning the roster mask itself
        // already follows on the line above) — an id published before the transition means
        // nothing in the new world and must not survive into it.
        block.ClearLocalPartyRosterObjectIds();
    }

    /// <summary>
    /// The session ended: stop reconciling this instance's party against a peer's.
    ///
    /// <see cref="Reconcile"/> already names "a disconnected agent" as one of the cases where
    /// there is nothing to reconcile against — but the peer roster is an agent-written LEVEL
    /// with a valid bit, and nothing lowered it on teardown, so the claim was false. A
    /// disconnected client went on adding and removing its own party members to match whatever
    /// party the host had at the moment the link died: dismiss Trask after disconnecting and
    /// the reconciler put him straight back.
    /// </summary>
    public void ReleaseSession()
    {
        block.ClearHostPartyRoster();
        block.ClearHostPartyRosterObjectIds();
        Invalidate();
        PluginHost.Log("party roster: your party is yours again — nothing left to reconcile against");
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        PublishLocalRoster();
        SyncClientPartyTogetherState();

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;
        Reconcile();
    }

    /// <summary>
    /// The client-door root cause fix. Runs every frame the roster channel itself is enabled
    /// (cheap: a table-count read, a client-count read, and a compare; the write only fires on a
    /// real divergence) so CSWParty is already correct well before a human walks into a real
    /// door — this plugin never sees that crossing coming, so waiting for the slower roster
    /// reconcile pass (<see cref="IntervalFrames"/>, 30 frames) would leave a window where a door
    /// still refuses.
    ///
    /// Skips entirely when the table count itself is unknown (main menu, mid-transition) rather
    /// than treating "unreadable" as "0 companions" and clamping toward a value that might be
    /// wrong the moment the server settles back in.
    /// </summary>
    private void SyncClientPartyTogetherState()
    {
        if (!_togetherFixEnabled)
        {
            return;
        }

        // ⚠ CLIENTS ONLY. The "divergence" this clamps is not staleness: CSWPartyTable counts
        // COMPANIONS (Trask -> 1) while CSWParty counts the PC as well (PC + Trask -> 2), so the
        // two read differently on every healthy instance. Clamping CSWParty to the table's
        // count drops the last companion out of the struct the selection UI, portraits and
        // follow logic read — which on the HOST meant the player lost control of their own
        // companions the moment the channel came up (reported 2026-08-22). A client mirrors its
        // companions from the host and never commands them, so there the loss is harmless and
        // the clamp is what lets IsPartyTogether pass a door whose "party" is standing wherever
        // the host's companions are. The host-roster valid bit is the cleanest "I am a client
        // in a live session" signal this plugin has (it is only ever written for a client).
        if (!block.TryGetHostPartyRoster(out _, out _))
        {
            return;
        }

        if (!Odyssey.TryGetPartyTableMemberCount(out int tableCount))
        {
            return;
        }

        int clientCount = Odyssey.GetClientPartyMemberCount();
        if (clientCount < 0)
        {
            return;
        }

        // ⚠ DEFERS TO CONTROL-RETURN (docs/research/control-return.md). The clamp trims
        // whichever member sits at the END of CSWParty's array, without regard for who that is
        // — ordinarily always a companion (harmless), but a client who was already controlling
        // one of its own party-roster companions at the moment of connect has the array the
        // other way round, and the member at the end is the PC's own slot. See
        // PartyTogetherClampRule's doc comment for the full chain. Skipping the clamp for as
        // long as that is true costs nothing: ControlReturnController converges control back to
        // the PC within its own bounded budget regardless, and the clamp resumes the instant it
        // does — the ordinary, safe case.
        bool controllingCompanion = IsControllingRosterCompanion();
        // A client can arrive with the PC beyond the current CSWParty count (the live
        // 0x7FFFFFFE case had a companion in member[0], stale/unresolved entries above it, and
        // no PC in the bound). Dropping the count to the table's companion count would make that
        // absence permanent from every bounded consumer. Defer until the PC is visible in the
        // proposed bound; this is a read-only safety check and leaves the existing recovery path
        // responsible for a malformed/reordered client array.
        int targetCount = Math.Max(1, tableCount); // Even a solo party must retain its PC.
        bool playerWithinTargetCount = Odyssey.IsPlayerCharacterWithinClientPartyCount(targetCount);
        if (!PartyTogetherClampRule.ShouldClamp(
                controllingCompanion, clientCount, targetCount, playerWithinTargetCount))
        {
            return;
        }

        if (Odyssey.TryClampClientPartyCount(targetCount, out int before))
        {
            PluginHost.Log(
                $"party together: CSWParty's own member count read {before} against "
                + $"CSWPartyTable's {tableCount} - clamping the client-side struct to {targetCount} "
                + "with the PC retained (this is the field CSWParty::IsPartyTogether actually checks, and the "
                + "one a real door transition gates on)");
        }
    }

    /// <summary>
    /// Publishes this instance's roster for the agent to read.
    ///
    /// ⚠ "COULD NOT READ" AND "NOTHING RECRUITED" ARE WRITTEN DIFFERENTLY. At the main menu and
    /// through a module transition there is no party table, and both masks would come out zero
    /// — bit-identical to a real roster with an empty party. A host publishing the second while
    /// it was really the first would tell its client to dismiss everyone. So a failed read
    /// clears the word entirely (valid bit included) and the agent reports "no reading".
    /// </summary>
    private void PublishLocalRoster()
    {
        if (!Odyssey.TryReadPartyRoster(out uint available, out uint party))
        {
            // A failed read is a world gap, not an empty roster. Do not carry a pre-transition
            // 0->1 edge into the newly resolved party table and accidentally defer a genuine
            // host dismissal there.
            _lastLocalAvailable = uint.MaxValue;
            block.ClearLocalPartyRoster();
            block.ClearLocalPartyRosterObjectIds();
            return;
        }

        if (_lastLocalAvailable != uint.MaxValue)
        {
            // A new local bit may be a client-led dialogue grant racing an old host level, or a
            // host-driven AddAvailableNPC that is already on its way. In either case it cancels
            // any older host-removal edge for this slot; a later 1->0 host transition is
            // required before the slot can be cleared again.
            _hostAvailabilityRemovalEdges &= ~PartyRosterAvailabilityRule.NewlyAvailable(
                _lastLocalAvailable, available);
        }

        byte* currentModuleBytes = stackalloc byte[32];
        int currentModuleLength = Odyssey.ReadCurrentModuleName(currentModuleBytes, 32);
        if (currentModuleLength == 0) return;
        string occupantModule = System.Text.Encoding.ASCII.GetString(currentModuleBytes, currentModuleLength);
        uint occupantSerial = block.WorldLoadSerial, occupantEpoch = block.LivenessNativeEpoch;
        if (_occupantWorldModule.Length > 0 && (_occupantWorldSerial != occupantSerial
            || (_occupantWorldEpoch != occupantEpoch && _occupantWorldModule.Equals(occupantModule, StringComparison.OrdinalIgnoreCase))))
            PartyOccupantHook.Captured.InvalidateWorld();
        _occupantWorldModule = occupantModule; _occupantWorldSerial = occupantSerial; _occupantWorldEpoch = occupantEpoch;

        _lastLocalAvailable = available;
        for (int slot = 0; slot < 9; slot++)
        {
            PartyOccupantHook.Captured.ObserveCachedBody(slot, Odyssey.GetPartyRosterObjectId(slot));
            if ((available & (1u << slot)) != 0 && !Odyssey.LegacyPartySlotSafe(slot, occupantModule))
                PartyOccupantHook.Captured.HoldUnprovenLoadedSlot(slot);
        }
        block.ApplyVerifiedPartyRestore(available, party);
        block.BeginLocalPartyPublication();
        block.SetLocalPartyRoster(available, party);

        // Publish the party table's body identity for every slot, including dismissed or benched
        // members. AddMember writes +0x0C and RemovePartyMember/RemoveAvailableNPC leave the
        // retained native body and its slot identity available for a later faction handoff.
        // Reconcile still consults ids only when the host says a slot is actively in party.
        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            uint objectId = Odyssey.GetPartyRosterObjectId(slot);
            block.SetLocalPartyRosterObjectId(
                slot, PartyCensusRule.IsRosterId(objectId) ? objectId : 0);
        }
        block.PublishLocalPartyOccupants(SharedBlock.PartyRosterValid
            | (available << SharedBlock.PartyRosterAvailableShift) | (party << SharedBlock.PartyRosterPartyShift));


        if (party != _lastLocalParty)
        {
            _lastLocalParty = party;
            PluginHost.Log($"party roster: local party is now 0x{party:X3} "
                + $"(recruited 0x{available:X3})");
        }
    }

    /// <summary>
    /// Brings this instance's active party in line with the one it is being told to hold.
    ///
    /// Removes run before adds, and that ordering is not cosmetic: the party holds at most TWO
    /// companions (CSWPartyTable::AddMember refuses at a count of 2, 0x00565633), so an add of
    /// the right companion can be refused purely because a stale one is still in the seat. Done
    /// the other way round, a straight swap — the ordinary result of one instance changing its
    /// party while the other did not — would fail every pass forever.
    /// </summary>
    private void Reconcile()
    {
        if (!block.TryGetHostPartyRoster(out uint hostAvailable, out uint hostParty))
        {
            // Nobody is telling this instance what its roster should be: the host itself, a
            // client before the first level has arrived, or a disconnected agent. Say so once.
            if (_lastHostValid)
            {
                _lastHostValid = false;
                _unavailableLogged = 0;
                _spawnRefusedLogged = 0;
                _recruitRefusedLogged = 0;
                PluginHost.Log("party roster: no peer roster to reconcile against");
            }

            _lastHostAvailable = PartyRosterAvailabilityRule.UnknownMask;
            _hostAvailabilityRemovalEdges = 0;

            return;
        }

        // Capture this level before conversation/frozen guards. A real host 1->0 edge must not
        // be lost just because the local engine is still finishing the same dialogue or a script
        // currently owns the party table. The edge remains pending until applied or re-added.
        _hostAvailabilityRemovalEdges = PartyRosterAvailabilityRule.UpdateHostRemovalEdges(
            _hostAvailabilityRemovalEdges, _lastHostAvailable, hostAvailable);
        _lastHostAvailable = hostAvailable;

        if (!_lastHostValid)
        {
            _lastHostValid = true;
            PluginHost.Log($"party roster: reconciling against a peer party of 0x{hostParty:X3}");
        }

        // ⚠ A LOCAL CONVERSATION OWNS THE ROSTER WHILE IT RUNS. A recruitment conversation is
        // precisely the thing that changes a party, and its own script is mid-flight; racing it
        // would mean removing a companion the conversation is about to add, or adding one it is
        // about to configure. The same guard PartyPresenceController uses for the same reason.
        if (Odyssey.IsConversationActive)
        {
            return;
        }

        // The engine refuses every roster change while a script drives one specific party
        // member (party table +0xF0 != -1) — tested first by both AddMember (0x00565625) and
        // RemoveMember (0x00565565). Waiting here saves a VM dispatch per pass on a call whose
        // answer is already decided, and keeps the failure counter meaningful.
        if (Odyssey.IsPartyRosterFrozen())
        {
            return;
        }

        if (!Odyssey.TryReadPartyRoster(out uint localAvailable, out uint localParty))
        {
            return;
        }

        if (!block.HostPartyOccupantsCurrent()) return;
        uint heldOccupants = 0;
        for (int slot = 0; slot < 9; slot++)
        {
            uint bit = 1u << slot;
            if ((hostAvailable & bit) == 0 || !block.TryGetHostPartyOccupant(slot, out uint generation, out string template)) continue;
            if (generation != 0 && template.Length > 0 && block.HostPartyOccupantAdopted(slot))
                PartyOccupantReconcileState.Commit(slot, block.HostPartyOccupantSession, generation);
            if (generation != 0 && !block.HostPartyOccupantRequiresBinding(slot) && PartyOccupantHook.Captured.FixedStockIdentity(slot))
            {
                Span<byte> localTemplate = stackalloc byte[16];
                PartyOccupantHook.Captured.CopyTemplate(slot, localTemplate);
                int localLength = localTemplate.IndexOf((byte)0); if (localLength < 0) localLength = 16;
                if (template.Length > 0 && template.Equals(System.Text.Encoding.ASCII.GetString(localTemplate[..localLength]), StringComparison.OrdinalIgnoreCase))
                    PartyOccupantReconcileState.Commit(slot, block.HostPartyOccupantSession, generation);
            }
            if (block.HostPartyOccupantRequiresBinding(slot) && !block.HostPartyOccupantAdopted(slot) && ((localParty | hostParty) & bit) != 0)
            { heldOccupants |= bit; continue; }
            PartyOccupantAction action = PartyOccupantRule.Decide(generation, template.Length > 0,
                PartyOccupantReconcileState.IsApplied(slot, block.HostPartyOccupantSession, generation),
                (localParty & bit) != 0 || ((hostParty & bit) != 0 && (localAvailable & bit) != 0), _pokeAvailability.Resolve(block));
            if (action == PartyOccupantAction.Hold) { heldOccupants |= bit; continue; }
            if (action != PartyOccupantAction.RecruitInactive) continue;
            // Remove availability cache only, never destroy or repurpose a placed script actor.
            if ((localAvailable & bit) != 0 && !Odyssey.RemoveAvailableNpc(slot, out _)) return;
            if (!Odyssey.AddAvailableNpcByVerifiedTemplate(slot, template)) return;
            PartyOccupantReconcileState.Commit(slot, block.HostPartyOccupantSession, generation);
            PluginHost.Log($"party occupant: slot {slot} generation {generation} recruited exact '{template}' (inactive cache)");
            return; // Read native masks again next pass; spawning is separate.
        }

        uint toRemove = localParty & ~hostParty & ~heldOccupants;
        // The feature gate covers both directions. In particular, an opt-out must preserve a
        // locally recruited companion even when the host's older roster has not caught up yet.
        bool availabilitySync = _pokeAvailability.Resolve(block);
        uint toRemoveAvailability = availabilitySync
            ? PartyRosterAvailabilityRule.PendingRemovals(localAvailable, hostAvailable, hostParty)
            : 0;

        // A local mismatch is removable only after the host has published that bit before and
        // then published it cleared. A first host snapshot of zero is intentionally not a
        // destructive command, and a client-led 0->1 edge cancels an older falling edge above.
        toRemoveAvailability &= _hostAvailabilityRemovalEdges;
        uint toAdd = hostParty & ~localParty & ~heldOccupants;

        if (toRemove == 0 && toRemoveAvailability == 0 && toAdd == 0)
        {
            _unavailableLogged = 0;
            _spawnRefusedLogged = 0;
            _recruitRefusedLogged = 0;
            _availabilityRemovalFailedLogged = 0;
            return;
        }

        // ONE OPERATION PER PASS, deliberately. Each is a script-VM dispatch that rebuilds the
        // party's derived state (portraits, follow state, the client-side party list), and the
        // engine's own GUI never issues two back to back either. A swap therefore settles in
        // two passes — under a second — and every pass re-reads the live table, so an operation
        // that silently did not take is seen rather than assumed.
        if (toRemove != 0)
        {
            int slot = LowestSlot(toRemove);
            if (Odyssey.RemovePartyMemberBySlot(slot, out int failure))
            {
                block.PartyRosterRemoves++;
                block.PartyRosterStatus = 0;
                PluginHost.Log($"party roster: removed NPC slot {slot} from the party "
                    + "(the peer no longer has them)");
            }
            else
            {
                block.PartyRosterStatus = (uint)failure;
                PluginHost.Log($"party roster: could not remove NPC slot {slot} (failure {failure})");
            }

            return;
        }

        // Remove an availability bit only after any active seat for that slot has been removed.
        // The verified engine implementation of RemoveAvailableNPC clears the party table's
        // +0x30 flag and deliberately leaves live bodies alone; removing the seat first keeps
        // this operation ordered with the game's own dismiss path and avoids a stale active
        // body being treated as an unavailable companion on the next pass.
        if (toRemoveAvailability != 0)
        {
            int slot = PartyRosterAvailabilityRule.LowestPendingSlot(toRemoveAvailability);
            if (Odyssey.RemoveAvailableNpc(slot, out int failure))
            {
                _hostAvailabilityRemovalEdges &= ~(1u << slot);
                _availabilityRemovalFailedLogged &= ~(1u << slot);
                block.PartyRosterStatus = 0;
                PluginHost.Log($"party roster: cleared availability for NPC slot {slot} "
                    + "(the peer no longer has them recruited)");
            }
            else
            {
                block.PartyRosterStatus = (uint)failure;
                uint bit = 1u << slot;
                if ((_availabilityRemovalFailedLogged & bit) == 0)
                {
                    _availabilityRemovalFailedLogged |= bit;
                    PluginHost.Log($"party roster: could not clear availability for NPC slot {slot} "
                        + $"(failure {failure}); retrying (logged once)");
                }
            }

            return;
        }

        int addSlot = LowestSlot(toAdd);
        uint objectId;
        PartyRosterBodyOrigin origin;

        // Not recruited here. Ordinarily the documented boundary of the channel: availability
        // comes from the recruitment conversation running on this instance too, and nothing in
        // the roster records the blueprint that would let us synthesise it (party table +0x30
        // is a bare 1, written at 0x00564350). Behind dialogue_recruit_sync, recruit through the
        // real engine call instead of giving up — docs/research/dialogue-consequences.md §4.2:
        // K1's nine companion slots are a fixed, game-defined mapping (slot -> blueprint is
        // baked into nwscript.nss and identical on both installs), so the party-roster channel
        // ALREADY carries the one bit that matters (hostAvailable, on the wire every tick); the
        // only missing step was acting on a bit the peer has that we do not.
        if ((localAvailable & (1u << addSlot)) == 0)
        {
            if (!_pokeAvailability.Resolve(block))
            {
                if ((_unavailableLogged & (1u << addSlot)) == 0)
                {
                    _unavailableLogged |= 1u << addSlot;
                    PluginHost.Log($"party roster: a peer has NPC slot {addSlot} in its party, but that "
                        + "companion was never recruited here — recruitment does not replicate "
                        + $"(peer recruited 0x{hostAvailable:X3}, here 0x{localAvailable:X3})");
                }

                return;
            }

            // BEFORE recruiting off-stage: does this instance already have the PEER's own
            // pre-recruit body standing in the world? docs/research/dialogue-consequences.md's
            // 2026-08-24 "stale mirrored body" entry — a companion who was ALSO a placed NPC
            // (Zaalbar in tar_m05aa, live-confirmed) keeps that placed body untouched on a
            // peer whose own recruit dialogue never ran, because AddAvailableNpcByTemplate +
            // SpawnAvailableNpc below always builds a brand-new one instead of reusing it. Object
            // ids are assigned deterministically in GIT order (the same assumption
            // EnemyPresenceController/MaterialisedEnemies already lean on throughout this
            // project), so the peer's own party-table id for this slot — published every frame
            // over the SAME roster channel this method already reconciles against — is the SAME
            // id a matching placed body carries HERE too, if one exists. Finding it lets this
            // pass reuse that body directly (PartyRosterBodyOrigin.LiveInWorld, added in the
            // SAME pass) instead of ever spawning a second one — closing the duplicate at its
            // source rather than cleaning one up afterward. NEEDS-LIVE: unexercised against the
            // real GOG install so far.
            uint hostBodyId = block.GetHostPartyRosterObjectId(addSlot);
            nint gitPlacedBody = PartyRosterBodyReuseRule.IsWorthLookingUp(hostBodyId)
                ? CreatureFinder.FindByObjectId(hostBodyId)
                : 0;

            if (PartyRosterBodyReuseRule.ShouldReuse(
                    hostBodyId, gitPlacedBody != 0, IsOurOwnSpawnedBody(hostBodyId, addSlot))
                && Odyssey.AddAvailableNpcByTemplate(addSlot))
            {
                // ⚠ The slot is recruited through the engine's own AddNPC even though the body is
                // reused, NOT flag-poked. Until 2026-09-03 this branch raw-poked +0x30 ("all
                // AddMember needs is the availability gate") — and that produced a member the
                // engine could not rebuild after the next module move: CreateParty resolves every
                // member from GAMEINPROGRESS:AVAILNPC<slot>, the file AddNPC writes at recruit
                // time, and a member without one came back OBJECT_INVALID and crashed PositionParty
                // (docs/research/transition-crashes.md 2026-09-03; Odyssey.IsSlotAvailable's doc
                // comment). AddNPC's temporary template creature is deleted by the engine itself,
                // so nothing is duplicated here; the placed body found above is what gets added.
                objectId = hostBodyId;
                origin = PartyRosterBodyOrigin.LiveInWorld;
                PluginHost.Log($"party roster: NPC slot {addSlot} recruited - reusing its own "
                    + $"already-placed body 0x{objectId:X} here (GIT-identical to the peer's "
                    + "pre-recruit body) instead of spawning a duplicate - dialogue_recruit_sync");
            }
            else
            {
                // Odyssey.AddAvailableNpcByTemplate: the engine's own AddNPC_2/AddNPC, which sets
                // the flag AND writes the AVAILNPC<slot> cache SpawnAvailableNpc rebuilds a body
                // from (docs/research/party-reconciliation.md Round 75 live-confirmed a bare flag
                // poke leaves nothing to spawn from). It produces NO body — its temporary template
                // creature is deleted by the engine and it returns a success flag, not an id (see
                // its doc comment; until 2026-09-03 this code treated that 1 as an object id and
                // "the add failed with failure 9" was AddMember being handed the number 1). Do not
                // attempt the add here — origin below routes it to the rule, and the very next pass
                // spawns a real body through the branch below and adds it.
                if (!Odyssey.AddAvailableNpcByTemplate(addSlot))
                {
                    block.PartyRosterStatus = 200;
                    if ((_recruitRefusedLogged & (1u << addSlot)) == 0)
                    {
                        _recruitRefusedLogged |= 1u << addSlot;
                        PluginHost.Log($"party roster: recovery waiting for NPC slot {addSlot}: "
                            + $"{Odyssey.RecruitmentFailureReason}; retrying (logged once)");
                    }
                    return;
                }

                _recruitRefusedLogged &= ~(1u << addSlot);

                objectId = 0;
                origin = PartyRosterBodyOrigin.RecruitedOffStage;
                PluginHost.Log($"party roster: recruited NPC slot {addSlot} from its own template "
                    + $"(peer recruited 0x{hostAvailable:X3}, here 0x{localAvailable:X3}) "
                    + "- dialogue_recruit_sync; a body will be spawned and added on a later pass");
            }
        }
        else
        {
            // AddPartyMember needs a LIVE object: AddMember resolves the id through
            // CServerExoApp::GetGameObject at 0x00565666 and refuses one that does not resolve. A
            // recruited companion who has never been in the party here has no body, so put one in
            // the world first — the same pair the game's own party-selection screen performs
            // (CSWGuiPartySelection::AcceptParty: SpawnNPC 0x006BE90C, then AddMember 0x006BEA25).
            objectId = Odyssey.GetPartyRosterObjectId(addSlot);
            if (objectId is 0 or Odyssey.ObjectInvalid
                || Odyssey.ResolveServerObject(objectId) == 0)
            {
                // Fresh engine resolve — the block's copy is agent-written and stale through a
                // module transition (see the EncounterController travel-follow crash).
                nint player = Odyssey.GetPlayerCreaturePointer();
                if (player == 0)
                {
                    return;
                }

                // ⚠ Look for the peer's own body HERE before spawning — live 2026-09-03 01:31
                // (party-reconciliation.md): the slot was already available on this side (the
                // adopted host world carried PT_NPC_AVAIL[2]=1 from an older build's raw flag
                // poke, with no AVAILNPC2.utc behind it), so the branch above never ran, the
                // cache held nothing, and SpawnAvailableNpc refused with failure 0 (no blueprint
                // to spawn from) on EVERY pass — while Carth's own placed body (0x2,
                // GIT-identical to the host's party member) stood at the bridge the whole time.
                if (TryReuseGitPlacedBody(addSlot, out objectId))
                {
                    _spawnRefusedLogged &= ~(1u << addSlot);
                    PluginHost.Log($"party roster: NPC slot {addSlot} is already available here - "
                        + $"reusing its own already-placed body 0x{objectId:X} (GIT-identical to the "
                        + "peer's party member) instead of spawning a duplicate");
                }
                else
                {
                    objectId = Odyssey.SpawnAvailableNpc(addSlot, Odyssey.GetPosition(player), out int spawnFailure);
                    if (objectId is 0 or Odyssey.ObjectInvalid)
                    {
                        block.PartyRosterStatus = (uint)(100 + spawnFailure);
                        if ((_spawnRefusedLogged & (1u << addSlot)) == 0)
                        {
                            _spawnRefusedLogged |= 1u << addSlot;
                            PluginHost.Log($"party roster: could not spawn NPC slot {addSlot} "
                                + $"(failure {spawnFailure}) and no GIT-identical body is here; "
                                + "the add is deferred and retried every pass (logged once)");
                        }

                        return;
                    }

                    _spawnRefusedLogged &= ~(1u << addSlot);
                    PluginHost.Log($"party roster: spawned NPC slot {addSlot} as 0x{objectId:X} to add them here");
                }

                // Deliberately falls through to the add in the SAME pass. A spawned companion that
                // is not yet in the party is an ordinary loose creature — the pre-store flush does
                // not know about it and SaveCreatures does not skip it (only the in-party flag at
                // +0xA88 buys that exemption) — so the window between the two calls is kept to zero
                // rather than a half second.
                //
                // ⚠ If the add still fails, the body is NOT destroyed and NOT re-spawned. SpawnNPC
                // has already written the new id into party table +0x0C, so the next pass resolves
                // it and goes straight to the add. That bounds the damage at one orphaned body per
                // slot for one pass; destroying it instead would leave the table pointing at a
                // creature that no longer exists, which is the worse of the two.
            }

            // Either the cache already held a resolvable body, or SpawnAvailableNpc just placed
            // one — both cases have actually been AddToArea'd.
            origin = PartyRosterBodyOrigin.LiveInWorld;
        }

        // PartyRosterRecruitRule: only a body that has actually been placed in an area can pass
        // AddMember's own live-body check — see that rule's doc comment for the disassembled
        // reason AddAvailableNpcByTemplate's own object can never do this. Recruiting alone is
        // not an add: the very next pass sees localAvailable's new bit, takes the branch above,
        // and spawns (or resolves) a real in-world body before reaching this gate again.
        if (!PartyRosterRecruitRule.CanAddImmediately(origin))
        {
            return;
        }

        if (Odyssey.AddPartyMemberBySlot(addSlot, objectId, out int addFailure))
        {
            block.PartyRosterAdds++;
            block.PartyRosterStatus = 0;
            PluginHost.Log($"party roster: added NPC slot {addSlot} (object 0x{objectId:X}) to the party");
        }
        else
        {
            block.PartyRosterStatus = (uint)addFailure;
            PluginHost.Log($"party roster: could not add NPC slot {addSlot} (failure {addFailure})");
        }
    }

    /// <summary>Lowest set slot in a nine-bit mask. The caller has already established the mask
    /// is non-zero.</summary>
    private static int LowestSlot(uint mask)
    {
        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            if ((mask & (1u << slot)) != 0)
            {
                return slot;
            }
        }

        return 0;
    }

    /// <summary>
    /// Whether the local player is currently DRIVING one of this instance's own party-roster
    /// companions — the whole roster (0..8), not just the currently-published active party,
    /// for the same reason <c>ControlReturnController.IsPartyRosterBody</c> uses the whole
    /// roster: at the moment of connect the companion presence has not arrived yet, and that is
    /// precisely the moment <see cref="PartyTogetherClampRule"/> needs this to be right. The
    /// two controllers ask the same question the same way; see that rule's doc comment for why
    /// <see cref="SyncClientPartyTogetherState"/> needs the answer at all.
    ///
    /// ⚠ ROUND 70 (2026-08-22, docs/research/control-return.md): reads
    /// <see cref="Odyssey.GetControlledCreaturePointer"/>, NOT
    /// <see cref="Odyssey.GetPlayerCreaturePointer"/> — the latter resolves a fixed "player
    /// slot 0" entry in the server's own player list and is immune to every control switch, so
    /// this answered "false" unconditionally before the fix, defeating the whole deferral.
    /// </summary>
    private static bool IsControllingRosterCompanion()
    {
        nint controlled = Odyssey.GetControlledCreaturePointer();
        if (controlled == 0)
        {
            return false;
        }

        uint controlledId = *(uint*)(controlled + 0x04);
        if (controlledId == 0)
        {
            return false;
        }

        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            uint id = Odyssey.GetPartyRosterObjectId(slot);
            if (id == controlledId && id is not 0 and not Odyssey.ObjectInvalid)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a local object id names a body THIS plugin spawned — a peer's phantom or a
    /// materialised enemy stand-in — rather than something the module placed.
    ///
    /// See <see cref="PartyRosterBodyReuseRule.ShouldReuse"/> for why the reuse path must
    /// refuse them: those ids come from a runtime counter the two instances advance
    /// independently, so a peer's roster id can collide with one by arithmetic alone, and
    /// reusing it would seat a remote player's avatar in this instance's party.
    /// </summary>
    /// <summary>The peer's party-table id for <paramref name="slot"/>, if a creature with that
    /// exact id stands in this instance's world and is not one of our own spawned mirrors.
    /// Object ids are assigned deterministically in GIT order, so a companion who was ALSO a
    /// placed NPC (Carth on the Endar Spire bridge, Zaalbar in tar_m05aa) carries the same id
    /// here as the peer's party member — see the comment above the recruit branch.</summary>
    private bool TryReuseGitPlacedBody(int slot, out uint objectId)
    {
        objectId = 0;
        uint hostBodyId = block.GetHostPartyRosterObjectId(slot);
        if (!PartyRosterBodyReuseRule.IsWorthLookingUp(hostBodyId))
        {
            return false;
        }

        bool foundLocally = CreatureFinder.FindByObjectId(hostBodyId) != 0;
        if (!PartyRosterBodyReuseRule.ShouldReuse(hostBodyId, foundLocally, IsOurOwnSpawnedBody(hostBodyId, slot)))
        {
            return false;
        }

        objectId = hostBodyId;
        return true;
    }

    private bool IsOurOwnSpawnedBody(uint objectId, int forSlot)
    {
        if (!PartyRosterBodyReuseRule.IsWorthLookingUp(objectId))
        {
            return false;
        }

        // A body this instance already holds for a DIFFERENT companion. Ids of bodies spawned
        // after load are not GIT-identical across instances: live 2026-09-25 the host's new
        // Canderous was 0x107, the id the joiner had just spawned its own Bastila under, and
        // the joiner "reused" Bastila for slot 1 (AddMember failure 9; Jolee hit HK-47's id the
        // same way). SpawnAvailableNpc records every body in the party table, so the table
        // names each one we made.
        for (int slot = 0; slot < SharedBlock.PartyRosterSlots; slot++)
        {
            if (slot != forSlot && Odyssey.GetPartyRosterObjectId(slot) == objectId)
            {
                return true;
            }
        }

        if (MaterialisedEnemies.ToOwner(objectId) != 0)
        {
            return true;
        }

        for (int i = 0; i < SharedBlock.SlotCapacity; i++)
        {
            if (block.GetPhantomId(i) == objectId)
            {
                return true;
            }
        }

        return false;
    }
}

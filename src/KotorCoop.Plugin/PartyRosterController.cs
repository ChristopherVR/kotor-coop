namespace KotorCoop.Plugin;

/// <summary>
/// Both halves of the companion ROSTER channel.
///
/// PUBLISHES this instance's roster — which of the nine NPC slots are recruited, and which are
/// in the active party — every frame, so the host's agent has something to broadcast; and, on
/// an instance that is being told a roster, RECONCILES its own to match by calling the engine's
/// own AddPartyMember / RemovePartyMember.
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
/// ⚠ WHAT THIS DOES NOT DO: it never makes a companion AVAILABLE. Recruitment itself
/// (AddAvailableNPCByTemplate) needs the blueprint the script named, which is not readable from
/// the roster afterwards — party table +0x30 is a bare flag, set to the literal 1 at
/// 0x00564350. Availability is expected to arrive the way it already does: the same
/// conversation running on both instances. What diverges in practice is the PARTY SEAT, which
/// a partial or aborted conversation gets wrong on its own, and that is what this fixes. A slot
/// the peer has in its party and this instance has never recruited is logged, once, and
/// skipped — see <see cref="_unavailableLogged"/>.
///
/// Kill switch: <c>party_roster_sync=0</c> in coop.ini.
/// </summary>
internal sealed unsafe class PartyRosterController(SharedBlockView block)
{
    private readonly bool _enabled = PluginConfig.ReadBool("party_roster_sync", true);

    /// <summary>
    /// Frames between reconciliation passes. Deliberately slow: a roster changes a handful of
    /// times in a playthrough, and each pass that finds work to do enters the script VM. The
    /// PUBLISH half runs every frame regardless — it is three array reads and a dword store,
    /// and the host's broadcast should never be stale.
    /// </summary>
    private const int IntervalFrames = 30;

    private int _countdown;

    /// <summary>Bit per slot: this instance has already reported that it cannot honour an add
    /// there because the companion was never recruited here. Reset when the situation changes,
    /// so the log records a real state change rather than one line every half second.</summary>
    private uint _unavailableLogged;

    private uint _lastLocalParty = uint.MaxValue;
    private bool _lastHostValid;

    /// <summary>A module change reassigns every object id, so the cached "I already complained
    /// about slot N" state means nothing; the roster itself is save state and survives.</summary>
    public void Invalidate()
    {
        _unavailableLogged = 0;
        _lastLocalParty = uint.MaxValue;
        _lastHostValid = false;
        _countdown = 0;
        block.ClearLocalPartyRoster();
    }

    public void OnFrame()
    {
        if (!_enabled)
        {
            return;
        }

        PublishLocalRoster();

        if (_countdown > 0)
        {
            _countdown--;
            return;
        }

        _countdown = IntervalFrames;
        Reconcile();
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
            block.ClearLocalPartyRoster();
            return;
        }

        block.SetLocalPartyRoster(available, party);

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
                PluginHost.Log("party roster: no peer roster to reconcile against");
            }

            return;
        }

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

        uint toRemove = localParty & ~hostParty;
        uint toAdd = hostParty & ~localParty;

        if (toRemove == 0 && toAdd == 0)
        {
            _unavailableLogged = 0;
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

        int addSlot = LowestSlot(toAdd);

        // Not recruited here. This is the documented boundary of the channel, not a bug to
        // retry into the ground: availability comes from the recruitment conversation running
        // on this instance too, and nothing in the roster records the blueprint that would let
        // us synthesise it (party table +0x30 is a bare 1, written at 0x00564350).
        if ((localAvailable & (1u << addSlot)) == 0)
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

        // AddPartyMember needs a LIVE object: AddMember resolves the id through
        // CServerExoApp::GetGameObject at 0x00565666 and refuses one that does not resolve. A
        // recruited companion who has never been in the party here has no body, so put one in
        // the world first — the same pair the game's own party-selection screen performs
        // (CSWGuiPartySelection::AcceptParty: SpawnNPC 0x006BE90C, then AddMember 0x006BEA25).
        uint objectId = Odyssey.GetPartyRosterObjectId(addSlot);
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

            objectId = Odyssey.SpawnAvailableNpc(addSlot, Odyssey.GetPosition(player), out int spawnFailure);
            if (objectId is 0 or Odyssey.ObjectInvalid)
            {
                block.PartyRosterStatus = (uint)(100 + spawnFailure);
                PluginHost.Log($"party roster: could not spawn NPC slot {addSlot} "
                    + $"(failure {spawnFailure}); the add is deferred to the next pass");
                return;
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
            PluginHost.Log($"party roster: spawned NPC slot {addSlot} as 0x{objectId:X} to add them here");
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
}

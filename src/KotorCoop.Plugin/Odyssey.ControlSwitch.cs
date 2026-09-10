// Odyssey.ControlSwitch.cs - the engine-verified reorder for switching which
// character a client controls, applied manually.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- control switch: engine-verified reorder, manually applied -----------------
    //
    // An earlier raw two-field poke (member[0]'s id at `party+0x24` plus `internal+0x20`)
    // moved INPUT AUTHORITY but never reordered CSWParty's member array, so
    // `ControlReturnController` still never saw a claim: its own detection reads
    // `Odyssey.GetPlayerCreaturePointer` (`CServerExoApp::GetPlayerCreature`, 0x004B2820),
    // which resolves the SERVER's "player slot 0" entry (`CSWSPlayer+0x28`) — not
    // <see cref="GetControlledCreaturePointer"/>, the client-side field a switch reorders.
    // Detection now reads that field instead. Full derivation: docs/research/control-return.md
    // ("Round 70"). ⚠ Round 70's further claim that the server entry is FIXED ("the real PC,
    // never a companion, by construction") was wrong: `CSWParty::SetLeader` ends by calling
    // `CSWSPlayer::SetGameObject` on it (0x6357E6, past the range that pass disassembled), so
    // a real engine switch moves BOTH — live-confirmed 2026-09-02 by the death auto-swap on a
    // host. The manual switches below now do the same (<see cref="SyncServerPlayerToLeader"/>).
    //
    // ⚠⚠ THE ENGINE'S OWN SWITCH FUNNEL WAS TRIED FIRST AND DID NOT WORK LIVE — NEEDS-LIVE,
    // UNRESOLVED, documented here so nobody re-derives this from scratch.
    // `CClientExoAppInternal::ChangeCharacter` (0x005F6B60) — the function a real Tab press
    // (`ChangeCharacterToNextLivingPartyMember` 0x005EDF80/0x005F7960) and a real portrait
    // click (`CGuiInGame::ChangeCharacter` 0x0062B0F0 → `CClientExoApp::ChangeCharacter`
    // 0x005EDC30) both fall through to — was fully disassembled, and its calling convention
    // was CONFIRMED against two real call sites via `xrefs.py 0x5F6B60`:
    // `ChangeCharacterToNextLivingPartyMember` pushes exactly `push 0; push -2` (Tab's own
    // "-2 = swap with member[1]" sentinel) and the portrait path pushes a value computed by
    // `CSWParty::FindMemberIndexById` (0x006349C0, confirmed to be exactly the same bounded
    // array-id-search this file already implements as <see cref="FindMemberIndex"/>). Calling
    // it from this process — with the EXACT `-2` sentinel a real Tab press uses, not just a
    // computed index — left CSWParty's member array completely unchanged in every live test:
    // `internal+0x4B4` (the flag `ChangeCharacter` only sets past its own "was this a no-op"
    // short-circuit, `cmp edi,eax; je` at 0x5F6B91) never moved off 0, meaning
    // `CSWParty::GetControlledCreature` read the identical pointer before and after
    // `SetControlledCreature` ran, for every argument tried. `SetControlledCreature`'s own body
    // (0x00635480) was traced far enough to rule out an index/bounds mistake (the -2 path
    // skips the bounds check entirely and still did nothing), which points at something
    // failing further in — most likely an exception inside `SetControlledCreature`'s own SEH
    // frame, silently unwound before the array write, for a reason no debugger attach could
    // isolate further. A future pass with debugger access should pick this up before trying
    // anything else here; until then, the switch below reproduces the SAME end state
    // (client-control-lock.md: "a real engine control switch reorders the member array so the
    // driven creature becomes member[0]") by direct, id-only writes instead.
    //
    // The REPLACEMENT still fixes the actual bug: it performs the array reorder
    // `SetControlledCreature` was supposed to (so `PartyTogetherClampRule`'s deferral has
    // something real to defer FOR, and a subsequent clamp pass sees the true state), then
    // moves the camera through the engine's own, ALREADY-VERIFIED-WORKING
    // <see cref="SetCameraFollowServerObject"/>/<see cref="RestoreCameraToControlledCreature"/>
    // (both wrap the real `CSWCModule::SetCameraFollow`, 0x0063F9D0 — the same call
    // `ChangeCharacter` itself would have made). Every write is id-only — Recipe A
    // (client-control-lock.md): "VERIFIED crash-proof... every consumer resolves it through
    // the null-safe GetCreatureByGameObjectID" — so this is exactly as safe as the primitive it
    // replaces, just applied to the WHOLE array instead of only member[0].

    /// <summary>CSWParty (client-side) + this = member[0]'s client-space object id — the SAME
    /// field <c>GetSWPartyFn</c>/<see cref="SwPartyMemberCount"/>'s struct exposes at +0x00,
    /// documented in docs/research/client-control-lock.md's "Recipe A" (id-only, VERIFIED
    /// crash-proof: every consumer resolves it through the null-safe
    /// <c>GetCreatureByGameObjectID</c>).</summary>
    private const int SwPartyMember0ClientId = 0x24;

    /// <summary>Byte distance between successive <c>CSWPartyMemberData</c> entries
    /// (client-control-lock.md, confirmed again in <c>SetControlledCreature</c>'s
    /// own <c>imul eax,eax,0x88</c> index arithmetic).</summary>
    private const int SwPartyMemberStride = 0x88;

    /// <summary>The "party npc" flag bit a member's client-space id carries while it names a
    /// companion (live finding: <c>party.member0.client_object_id</c> read
    /// <c>0x80000127</c> for Trask). Masked off before every id comparison in this file so the
    /// same code compares correctly whether the id in hand carries it or not.</summary>
    private const uint ControlPartyNpcFlag = 0x80000000;

    /// <summary>CSWParty's own member-array capacity: the PC plus the server table's own
    /// two-companion cap (<c>CSWPartyTable::AddMember</c> refuses at a count of 2,
    /// 0x00565633, cited in <c>PartyRosterController</c>) — three <c>0x88</c>-byte blocks,
    /// live-confirmed (member[2] reads the empty-slot sentinel <c>0x7F000000</c> on
    /// a one-companion party).</summary>
    private const int SwPartyMemberCapacity = 3;

    /// <summary>Finds <paramref name="wantedId"/> (either flagged or plain — the flag bit is
    /// masked from both sides) in CSWParty's member array and returns its index, or -1. The
    /// SAME bounded search <c>CSWParty::FindMemberIndexById</c> (0x006349C0) performs — the
    /// real portrait-click path's own way of turning an object id into a member index.
    ///
    /// <paramref name="withinLiveCount"/> true scans only the entries CSWParty's OWN count
    /// field currently reports as real. False scans the full <see cref="SwPartyMemberCapacity"/>
    /// instead, ignoring that count — see <see cref="SwitchControlToPartySlot"/>'s remarks for
    /// why a caller ever wants that.</summary>
    private static int FindMemberIndex(nint party, uint wantedId, bool withinLiveCount)
    {
        int count = withinLiveCount ? *(int*)(party + SwPartyMemberCount) : SwPartyMemberCapacity;
        if (count is <= 0 or > SwPartyMemberCapacity)
        {
            if (withinLiveCount)
            {
                return -1;
            }

            count = SwPartyMemberCapacity;
        }

        uint wantedMasked = wantedId & ~ControlPartyNpcFlag;
        if (wantedMasked == 0)
        {
            return -1;
        }

        for (int i = 0; i < count; i++)
        {
            uint id = *(uint*)(party + SwPartyMember0ClientId + (i * SwPartyMemberStride)) & ~ControlPartyNpcFlag;
            if (id == wantedMasked)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Moves <paramref name="targetIndex"/>'s member to the FRONT of CSWParty's array
    /// (member[0]) — the same end state <c>CSWParty::SetControlledCreature</c> was supposed to
    /// produce (client-control-lock.md) — by shifting members <c>0..targetIndex-1</c> up by one
    /// slot and writing the target's id into member[0]. Preserves the relative order of every
    /// other member, matching what <c>SetControlledCreature</c>'s own rotate loop does for the
    /// common case.
    ///
    /// ID-ONLY (Recipe A, client-control-lock.md: "VERIFIED crash-proof... every consumer
    /// resolves it through the null-safe GetCreatureByGameObjectID"). The per-member "extra"
    /// field at <c>+0x80</c> — which the real engine ALSO shifts alongside the id, per
    /// disassembly of <c>SetControlledCreature</c> — is deliberately left in place: the same
    /// "member[0]'s id only, cosmetic desync at worst, never a crash risk" precedent already
    /// accepted elsewhere applies here too, and adding a second field write would only trade
    /// verification effort for a cosmetic gain nothing has asked for yet.
    ///
    /// A no-op when <paramref name="targetIndex"/> is already 0.
    /// </summary>
    private static void RotateControlToFront(nint party, int targetIndex)
    {
        if (targetIndex <= 0)
        {
            return;
        }

        uint targetId = *(uint*)(party + SwPartyMember0ClientId + (targetIndex * SwPartyMemberStride));
        for (int i = targetIndex; i > 0; i--)
        {
            uint* dst = (uint*)(party + SwPartyMember0ClientId + (i * SwPartyMemberStride));
            uint* src = (uint*)(party + SwPartyMember0ClientId + ((i - 1) * SwPartyMemberStride));
            *dst = *src;
        }

        *(uint*)(party + SwPartyMember0ClientId) = targetId;
    }

    /// <summary>Switches the LOCAL player's control to the party-roster companion in
    /// <paramref name="slot"/> (0..8, the same NPC slot index <c>partystate.ps1</c> reports —
    /// Trask is slot 0 in <c>end_m01aa</c>, LIVE-CONFIRMED, not 1): reorders CSWParty's member
    /// array (<see cref="RotateControlToFront"/>) and points the camera at the companion
    /// through the engine's own, already-working <see cref="SetCameraFollowServerObject"/>.
    /// Requires the companion to already be a live MEMBER of CSWParty's array (actually in the
    /// active party, not merely recruited) — there is no other way to name a target. Returns
    /// false, touching nothing, when the slot has no spawned body, is not currently a party
    /// member, or there is no client world.
    ///
    /// ⚠ LIVE FINDING: on a client with <c>party_together_fix</c> (default ON) this
    /// companion's data is very often ALREADY sitting in CSWParty's array (member[1], say) with
    /// its full flagged id (live-confirmed: <c>0x80000127</c> for Trask) — but CSWParty's own
    /// COUNT field has been clamped down to <c>CSWPartyTable</c>'s companions-only count
    /// (<c>PartyRosterController.SyncClientPartyTogetherState</c>, docs/research/control-return.md
    /// "Round 70"). So the clamp that makes a door crossable also makes the companion
    /// unreachable BY INDEX for a deliberate switch, even though nothing about the array's DATA
    /// is missing. The fix is not to fight the clamp: it is temporarily raised to include
    /// exactly the one entry this call needs (never invented data — the id there is verified
    /// first), and the clamp's own deferral (<see cref="PartyRosterController.IsControllingRosterCompanion"/>
    /// via <see cref="GetControlledCreaturePointer"/>) takes over from the very next pass once
    /// the switch has landed, so nothing re-clamps it back down out from under the player.
    /// MAIN THREAD ONLY.</summary>
    public static bool SwitchControlToPartySlot(int slot, out uint rosterId)
    {
        rosterId = GetPartyRosterObjectId(slot);
        if (rosterId is 0 or ObjectInvalid)
        {
            return false;
        }

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = FindMemberIndex(party, rosterId, withinLiveCount: true);
        if (index < 0)
        {
            // Not reachable within the CURRENT (possibly party_together_fix-clamped) count —
            // but the data may already be sitting past it (live finding above). Scan
            // the full array before giving up, and if it is there, raise the count to include
            // exactly that one entry. Never lowers it, never invents an id: the entry's own
            // content was already read and matched.
            index = FindMemberIndex(party, rosterId, withinLiveCount: false);
            if (index < 0)
            {
                return false;
            }

            int* countField = (int*)(party + SwPartyMemberCount);
            if (*countField <= index)
            {
                *countField = index + 1;
            }
        }

        uint previousLeaderId = *(uint*)(party + SwPartyMember0ClientId);
        RotateControlToFront(party, index);
        SyncServerPlayerToLeader(previousLeaderId, rosterId);
        SetCameraFollowServerObject(rosterId);
        return true;
    }

    /// <summary>CSWSPlayer::SetGameObject (0x00561C40, thiscall, <c>ret 4</c>): the server's
    /// own "this human now drives THAT creature" write. Stores the creature's id at
    /// <c>player+0x28</c> (<c>game_object_id</c> — the field <c>CServerExoApp::GetPlayerCreature</c>
    /// 0x4B2820 resolves through, control-return.md Part 1), and when the id actually changed:
    /// clears the player's <c>CSWSPlayerLastUpdateObject</c> action-queue/spell/feat mirrors
    /// so the next update to the client is a full one, re-parents the party item repository
    /// (<c>CSWPartyTable::GetPartyItems</c> → <c>CItemRepository::SetParentId</c>) to the new
    /// creature, and moves the area's players-in-area count between the two creatures'
    /// areas. Callers: <c>CSWParty::SetLeader</c> 0x635480 at 0x6357E6 (every real Tab /
    /// portrait click / death auto-swap), <c>CSWPartyTable::SwitchPlayerCharacter</c>, module
    /// init/unload. Disassembled 2026-09-02 (death-handling.md §6).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> SetPlayerGameObjectFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x00561C40;

    /// <summary><c>CServerExoAppInternal+0x10064</c>: the server's connected-players list
    /// (<c>CExoLinkedList&lt;CSWSClient&gt;</c>, world-up-faction-crash.md §2) — what
    /// <c>CServerExoApp::GetPlayerList</c> 0x4AED90 returns (<c>mov eax,[ecx+4]; mov
    /// eax,[eax+0x10064]; ret</c>).</summary>
    private const int ServerInternalPlayerList = 0x10064;

    /// <summary>Perception ranges on a CSWSCreature (symbol DB: <c>primary_range</c> /
    /// <c>secondary_range</c>, floats). <c>SetLeader</c> gives the new leader the PLAYER row
    /// of <c>ranges.2da</c> and the old leader the creature row.</summary>
    private const int CreaturePrimaryRange = 0x914;
    private const int CreatureSecondaryRange = 0x918;

    /// <summary>The server "player slot" id last read at <c>player+0x28</c> inside
    /// <see cref="SyncServerPlayerToLeader"/>, and when. Exists so other call sites (e.g. the
    /// newcharacter capture's equip-repair, which independently reads
    /// <see cref="GetPlayerObjectId"/>) can log the two values side by side and tell, without a
    /// live debugger, whether a given run saw the engine's server player slot agree with what
    /// this function last observed there — see docs/research/world-adoption-clothes-notes.md's
    /// "concrete next steps" #1. Null until this function has run at least once; a first-time
    /// joiner's chargen module may never trigger a leader switch at all before the equip-repair
    /// window runs, in which case this stays null and that itself is diagnostic.</summary>
    internal static uint? LastKnownServerPlayerRegistered { get; private set; }

    /// <summary>
    /// The half of a real control switch the array rotation above does NOT do, and that this
    /// file's round-70 remarks wrongly believed did not exist: <c>CSWParty::SetLeader</c>
    /// (0x635480) ends by calling <see cref="SetPlayerGameObjectFn"/> on the FIRST connected
    /// player with the new leader's server creature (0x6357E6 — past the 600 bytes the
    /// earlier disassembly covered), so <c>CServerExoApp::GetPlayerCreature</c> FOLLOWS the
    /// leader after any engine-driven switch. Live-measured 2026-09-02 on a host: the death
    /// auto-swap moved <c>GetPlayerCreature</c> to the companion (47 ms after the PC went
    /// down) and the array-only <see cref="SwitchControlToPc"/> left it there, so every
    /// "local player" read in this plugin (cheats, incoming damage, the phantom hit watch)
    /// kept naming the companion while the agent's is_pc pin named the PC. This mirrors the
    /// engine's write, through the engine's own function, plus the one piece of bookkeeping
    /// <c>SetLeader</c> does around it that is cheap to reproduce exactly: the two creatures'
    /// perception ranges swap roles (the old leader has the player ranges, the new one the
    /// creature ranges — exchanging the pairs is the same result as <c>SetLeader</c>'s two
    /// 2DA lookups without needing the 2DA). Skipped: <c>SetLeader</c>'s clearing of the new
    /// leader's <c>perceptions</c> list (+0x920), which the perception update maintains on
    /// its own.
    ///
    /// Self-validating: the player found through the list is trusted only if its
    /// <c>game_object_id</c> already agrees with <see cref="GetPlayerObjectId"/> (the engine's
    /// own resolution of the same field). A disagreement means the list walk is wrong for
    /// this world, and nothing is written. Client-space ids are accepted for both arguments
    /// (the party-npc flag bit is masked, exactly <c>CServerExoApp::ClientToServerObjectId</c>
    /// 0x4B1F70). MAIN THREAD ONLY.
    /// </summary>
    private static bool SyncServerPlayerToLeader(uint previousLeaderId, uint newLeaderId)
    {
        uint previousId = previousLeaderId & ~ControlPartyNpcFlag;
        uint newId = newLeaderId & ~ControlPartyNpcFlag;
        if (newId == 0 || newId == previousId)
        {
            PluginHost.Log($"control: leader unchanged (0x{previousId:X} -> 0x{newId:X}); server player left as 0x{GetPlayerObjectId():X}");
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            PluginHost.Log("control: no server; server player not re-pointed");
            return false;
        }

        nint serverInternal = *(nint*)(server + 4);
        nint list = *(nint*)(serverInternal + ServerInternalPlayerList);
        nint listInternal = list == 0 ? 0 : *(nint*)list;
        nint head = listInternal == 0 ? 0 : *(nint*)listInternal;
        nint player = head == 0 ? 0 : *(nint*)(head + 8);
        if (player == 0)
        {
            PluginHost.Log($"control: no server player to re-point (list 0x{list:X} internal 0x{listInternal:X} head 0x{head:X})");
            return false;
        }

        uint registered = *(uint*)(player + 0x28);
        LastKnownServerPlayerRegistered = registered;
        uint engineSays = GetPlayerObjectId();
        if (registered != engineSays)
        {
            PluginHost.Log($"control: server player walk disagrees with the engine (0x{registered:X} vs 0x{engineSays:X}); leaving GetPlayerCreature alone");
            return false;
        }

        nint newLeader = ResolveServerObject(newId);
        if (newLeader == 0)
        {
            PluginHost.Log($"control: new leader 0x{newId:X} does not resolve; leaving GetPlayerCreature on 0x{registered:X}");
            return false;
        }

        nint previousLeader = ResolveServerObject(previousId);
        if (previousLeader != 0)
        {
            float primary = *(float*)(previousLeader + CreaturePrimaryRange);
            float secondary = *(float*)(previousLeader + CreatureSecondaryRange);
            *(float*)(previousLeader + CreaturePrimaryRange) = *(float*)(newLeader + CreaturePrimaryRange);
            *(float*)(previousLeader + CreatureSecondaryRange) = *(float*)(newLeader + CreatureSecondaryRange);
            *(float*)(newLeader + CreaturePrimaryRange) = primary;
            *(float*)(newLeader + CreatureSecondaryRange) = secondary;
        }

        SetPlayerGameObjectFn(player, newLeader);
        PluginHost.Log($"control: server player re-pointed 0x{registered:X} -> 0x{*(uint*)(player + 0x28):X} (engine now says 0x{GetPlayerObjectId():X})");
        return true;
    }

    /// <summary>Switches the LOCAL player's control back to their own PC: reorders CSWParty's
    /// member array (<see cref="RotateControlToFront"/>), re-points the server's player entry
    /// (<see cref="SyncServerPlayerToLeader"/>) and points the camera at the PC through the
    /// engine's own, already-working <see cref="RestoreCameraToControlledCreature"/>.
    ///
    /// The PC is found by what it IS, not by where the engine's player slots point: the
    /// member whose server creature's stats say is_pc (<see cref="IsPlayerCharacter"/>).
    /// <c>CClientExoAppInternal::GetPlayerCreature</c> (<c>internal+0x20</c>) was used here
    /// before on the strength of client-control-lock.md's "a real ChangeCharacter never calls
    /// SetPlayerCreature" — true of the client-side call itself, but the server half of a real
    /// switch (<c>CSWSPlayer::SetGameObject</c>, see <see cref="SyncServerPlayerToLeader"/>)
    /// invalidates the player's last-update object, and the resulting
    /// <c>ServerToPlayerCreatureUpdate_ControlObject</c> message re-writes <c>internal+0x20</c>
    /// to the new leader (ingame-gui-transition.md §2). Live 2026-09-02, on a host whose PC
    /// had been auto-swapped away by a knock-down: that slot answered the COMPANION, so this
    /// method "returned control to the PC" by rotating Trask to the front of an array he was
    /// already at the front of. Returns false, touching nothing, when there is no client
    /// world, no live party object, or no member is the PC (a party without its PC is not a
    /// state the engine builds). MAIN THREAD ONLY.</summary>
    public static bool SwitchControlToPc()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = FindPlayerCharacterMemberIndex(party, out uint pcId);
        if (index < 0)
        {
            return false;
        }

        uint previousLeaderId = *(uint*)(party + SwPartyMember0ClientId);
        RotateControlToFront(party, index);
        SyncServerPlayerToLeader(previousLeaderId, pcId);
        RestoreCameraToControlledCreature();
        return true;
    }

    /// <summary>The index (and client-space id) of the CSWParty member whose server creature
    /// is the player character by its own stats (<see cref="IsPlayerCharacter"/>), scanning
    /// the live count; -1 when none resolves as one. Independent of every "player" slot the
    /// engine moves on a switch — see <see cref="SwitchControlToPc"/>.</summary>
    /// <summary>One line naming every CSWParty member slot as the engine holds it right now —
    /// the raw id, what it resolves to (vtable, is_pc, hit points) — next to the server's own
    /// player creature and CSWPartyTable's count. The `resolve party` console verb. Written
    /// for the post-wipe rejoin of 2026-09-02, where "you are driving companion 0x129 ...
    /// (switch not ready yet, retrying)" repeated forever and nothing said what the array
    /// actually contained. MAIN THREAD ONLY.</summary>
    public static string DescribeParty()
    {
        nint client = GetClient();
        nint party = client != 0 ? GetSWPartyFn(client) : 0;
        if (party == 0)
        {
            return "party: no client world";
        }

        nint player = GetPlayerCreaturePointer();
        string playerText = player == 0
            ? "server player: none"
            : $"server player: 0x{player:X} id 0x{GetObjectId(player):X} is_pc={IsPlayerCharacter(player)} hp {GetCurrentHitPoints(player)}";
        string tableText = TryGetPartyTableMemberCount(out int tableCount) ? tableCount.ToString() : "?";

        var text = new System.Text.StringBuilder();
        text.Append($"party: CSWParty 0x{party:X} count {*(int*)(party + SwPartyMemberCount)} (CSWPartyTable {tableText}); {playerText}");
        for (int i = 0; i < SwPartyMemberCapacity; i++)
        {
            uint id = *(uint*)(party + SwPartyMember0ClientId + (i * SwPartyMemberStride));
            uint masked = id & ~ControlPartyNpcFlag;
            text.Append($"; member[{i}] 0x{id:X}");
            if (masked is 0 or ObjectInvalid)
            {
                text.Append(" (empty)");
                continue;
            }

            nint obj = ResolveServerObject(masked);
            if (obj == 0)
            {
                text.Append(" -> unresolved");
                continue;
            }

            nint vtable = *(nint*)obj;
            text.Append(vtable == ServerCreatureVtable
                ? $" -> creature 0x{obj:X} is_pc={IsPlayerCharacter(obj)} hp {GetCurrentHitPoints(obj)}"
                : $" -> NOT a creature (0x{obj:X}, vtable 0x{vtable:X})");
        }

        return text.ToString();
    }

    private static int FindPlayerCharacterMemberIndex(nint party, out uint memberId)
    {
        int count = *(int*)(party + SwPartyMemberCount);
        if (count is <= 0 or > SwPartyMemberCapacity)
        {
            count = SwPartyMemberCapacity;
        }

        for (int i = 0; i < count; i++)
        {
            uint id = *(uint*)(party + SwPartyMember0ClientId + (i * SwPartyMemberStride));
            uint masked = id & ~ControlPartyNpcFlag;
            if (masked is 0 or ObjectInvalid)
            {
                continue;
            }

            nint creature = ResolveServerObject(masked);
            if (creature != 0 && IsPlayerCharacter(creature))
            {
                memberId = id;
                return i;
            }
        }

        memberId = 0;
        return -1;
    }

}

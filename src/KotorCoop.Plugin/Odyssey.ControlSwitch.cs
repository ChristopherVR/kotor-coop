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
    // which always resolves the SERVER's fixed "player slot 0" entry — the real PC, never a
    // companion, by construction — not <see cref="GetControlledCreaturePointer"/>, the field
    // that actually moves. Detection now reads that field instead. Full derivation:
    // docs/research/control-return.md ("Round 70").
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

        RotateControlToFront(party, index);
        SetCameraFollowServerObject(rosterId);
        return true;
    }

    /// <summary>Switches the LOCAL player's control back to their own PC: reorders CSWParty's
    /// member array (<see cref="RotateControlToFront"/>) and points the camera at the PC
    /// through the engine's own, already-working <see cref="RestoreCameraToControlledCreature"/>.
    /// The PC's id is read fresh every call, through <see cref="GetClientPlayerCreatureFn"/>
    /// (<c>CClientExoAppInternal::GetPlayerCreature</c>, 0x5F1420 — <c>internal+0x20</c>,
    /// VERIFIED stable across any switch: a real <c>ChangeCharacter</c> never calls
    /// <c>SetPlayerCreature</c>, client-control-lock.md) — no cross-call cache needed. Returns
    /// false, touching nothing, when there is no client world, no
    /// live party object, or (should not happen — the PC is always a member by construction)
    /// the PC does not currently resolve to a CSWParty member. MAIN THREAD ONLY.</summary>
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

        nint pcCreature = GetClientPlayerCreatureFn(client);
        if (pcCreature == 0)
        {
            return false;
        }

        int index = FindMemberIndex(party, *(uint*)(pcCreature + 0x04), withinLiveCount: true);
        if (index < 0)
        {
            return false;
        }

        RotateControlToFront(party, index);
        RestoreCameraToControlledCreature();
        return true;
    }

}

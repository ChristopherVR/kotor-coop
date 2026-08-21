# Party-member reconciliation (research 2026-07-31)

Problem: both instances run their own copy of every party member via follow-AI, drifting.

## Engine findings (corrections to the DB and to trap 5)

CSWPartyTable at CServerExoAppInternal+0x1B770 (GetPartyTable 0x004AEE70). REAL layout
(DB is wrong):
- `+0x00` companion count, MAX 2 (PC not counted)
- `+0x04` int[2] active companion list — holds **NPC slot indices (0..8)**, not object ids
- **`+0x0C` int[9] spawned server object id per NPC slot** — the creature↔slot link;
  0x7F000000 = not spawned
- `+0x30` int[9] avail_npcs (roster ops require !=0)
- `+0xEC` leader (a SLOT INDEX)
- `+0xF0` pt_controlled_npc (-1 normally; **non-(-1) freezes Add/Remove/CreateParty**)
- `+0x190` solo mode

**+0xA88 is the in-party flag**, written by SetInParty 0x004FDB20. SaveCreatures
(0x00507680) SKIPS creatures with +0xA88 set — Handoff 4.8's writer identified.

**Party follow is action id 61**, dispatched in RunActions (0x0057F4A0) via table
0x005801E4 → AIActionPartyFollowLeader 0x00511130 (sets +0xA8C=3, gates on +0xA88, +0x4C0,
`+0x9F0 & 2`). **Only ONE site enqueues it** (ExecuteCommandActionFollowLeader 0x0052CBE0)
— party following is SCRIPT-DRIVEN, so suppression must be HELD and un-suppression
self-heals.

**Trap 5 correction: commandable(+0xE8)=0 does NOT stop a queued follow action.** RunActions
never reads +0xE8, and the follow path (0x0051AC10) deliberately saves/forces/restores it.

Two levers that DO work:
- Per-member: raw `creature+0xA88 = 0` (reversible; = SetInParty(0,0)). Kills follow at the
  RunActions gate. COST: SaveCreatures now serialises that companion into the module —
  must restore in the CSWSArea+0x2C4 pre-store window.
- Party-global: `partyTable+0x190 = 1` (solo mode) — the game's shipped "companions stand
  still" switch. Touches nothing on the creature, so SaveCreatures still skips party
  members. RemoveMember clears it, so re-assert. **Saved in PARTYTABLE.res — restore
  before any save or a single-player load inherits a party that won't follow.**

## Recommended design: ownership split via solo mode + explicit follow

1. Both instances hold `partyTable+0x190 = 1` all session (re-assert each frame; restore on
   disconnect and in pre-store flush). No script issues ActionFollowLeader anywhere.
2. **Key EVERYTHING on NPC slot index (0..8), never object id.** Resolve via
   `partyTable+0x0C+4*slot`. Slots come from the save, identical + stable both sides; ids
   drift.
3. Deterministic ownership: sort active companion slots ascending, round-robin over stable
   peer order (host=0). Both sides derive the identical map. Rebuild on epoch change after
   CreateParty repopulates +0x0C.
4. Owner: when an owned companion's action queue (`obj+0xFC`) is empty, issue
   ActionFollowLeader — AddAction(creature, 0x3D, 0xFFFF, 0, 0) at 0x004CEA20. Engine's real
   follow-AI runs (paths, formation, gait, animation). Publish pos/facing/anim/hp/flags/tick.
5. Non-owner: touch nothing; nothing drives the creature (solo mode + this side never
   queues), so position writes stick (trap 3). Mirror exactly like CombatController does
   enemies: StepToward / SnapTo>15u / SetFacing / SetAnimation / absolute SetCurrentHitPoints
   / min1hp floor with death only on owner's report.
6. Death guard: every frame exempt GetPlayerCreatureId() from suppression AND mirroring
   (PC death swaps "the player" to a companion — everything keyed on the player creature
   must handle it); freeze the ownership map while pt_controlled_npc != -1.

Degenerates cleanly to host-authoritative-mirror at 1 companion (Endar Spire) — no
special-casing.

## Risks (need live measurement first)
1. Solo mode's full behaviour is INFERRED not measured — first test: set +0x190=1 in one
   instance, play 5 min, watch for regressions (combat initiative, stealth, camera).
2. A companion driven by explicit ActionFollowLeader UNDER solo mode is a state the game
   never enters — the party AI script may fight it.
3. Mirrored companion won't swing/parry on the non-owner screen (same gap phantoms have).
4. Dialogue collision (4.10) becomes a second visible symptom.
5. Solo mode saved — restore obligation.
6. 2 companions, 2 players, 3-slot party: leader (+0xEC) not replicated (out of scope).

Size: MEDIUM. New PartyController (~PhantomController + CombatController), new per-tick
PartySnapshot keyed by slot, shared-block v8, agent party-table census. Only genuinely new
engine call: AddAction(0x3D). All other primitives already in Odyssey.cs.

---

# Roster sync (implemented 2026-08-11)

The half of party reconciliation that was still missing: `AddPartyMember` /
`RemovePartyMember` had **no channel at all**. A conversation that recruits or dismisses a
companion runs its scripts on ONE instance — the other is following a dialogue token, not
executing dialogue — so only one party table hears. And the party table is the key the whole
companion channel resolves by (`PartyPresenceController` looks the host's "NPC slot 2" up in
its OWN table), so a divergence makes the mirror drive the wrong body, or none, silently and
for the rest of the session. This is the blocker recorded against `force_dialogue_party`.

## Engine facts established (each with the instruction that proves it)

All against swkotor.exe v1.0.3, image base 0x400000, no ASLR.

**`int AddPartyMember(int nNPC, object oCreature)`** — `CSWVirtualMachineCommands::Execute
CommandAddPartyMember` @ **0x0052DE70**, `__thiscall`, **`ret 8`** → `(this, nCommandId,
nParams)`, the same shape as the `CreateObject` dispatch the plugin already uses.
- pops an INTEGER (`CVirtualMachine::StackPopInteger` 0x005D1000) at 0x0052DE7E, then an
  OBJECT (`StackPopObject` 0x005D10C0) at 0x0052DE9D. A VM pop yields arguments in
  DECLARATION order, so the pushes go in reverse: object first, slot index last.
- hands both to `CSWPartyTable::AddMember(nNPC, oCreature)` at 0x0052DEC4.
- pushes an `int` result back (`StackPushInteger` 0x005D1010) at 0x0052DF21 — **on the
  refusal path too** (0x0052DF1A pushes the zero). The early-out at 0x0052DE87 returns
  0xFFFFF82F and pushes NOTHING, so the return may only be popped when the handler's own
  status is 0.

**`int RemovePartyMember(int nNPC)`** — @ **0x00541C00**, `__thiscall`, **`ret 8`**.
- one integer pop at 0x00541C0C; `CSWPartyTable::GetNPCObject(nNPC, 0, 1)` at 0x00541C3F,
  `RemoveMember(nNPC)` at 0x00541C4D; result pushed at 0x00541CA0. Same status rule.

**`object SpawnAvailableNPC(int nNPC, location lLoc)`** — @ **0x00543ED0**, `__thiscall`,
**`ret 8`**.
- integer pop at 0x00543EDE, then engine structure type 2 (location) via
  `StackPopEngineStructure` 0x005D10A0 at 0x00543EF4; `CSWPartyTable::SpawnNPC` at
  0x00543F2A; object pushed back at 0x00543F36.

**`CSWPartyTable::AddMember`** @ 0x00565620 (`__thiscall`, 8 bytes of args) — its refusals ARE
the channel's preconditions, in the order it tests them:
- `+0xF0 != -1` → refuse (0x00565625). A script driving one party member freezes the roster.
- `+0x00 == 2` → refuse (0x00565633). **Two companions is the ceiling**, which is why removes
  must run before adds: a straight swap otherwise fails on every pass forever.
- `nNPC < 0` (0x00565640) or `>= 9` (0x00565648) → refuse. **Nine slots is the engine's
  bound**, not a chosen cap.
- `+0x30 + 4*nNPC == 0` → refuse (0x00565651). Not recruited.
- the object id must resolve through `CServerExoApp::GetGameObject` (0x00565666) → **the add
  needs a LIVE body**.
- then writes `[+0x04 + 4*count] = nNPC` (0x005656B5) and `[+0x0C + 4*nNPC] = oCreature`
  (0x005656B9), confirming this document's corrected layout above against the DB's.

`CSWPartyTable::RemoveMember` @ 0x00565560 tests the same `+0xF0` (0x00565565), a count > 0,
the 0..8 range and availability.

**Availability is a bare flag**: `CSWPartyTable::AddNPC` writes the literal 1 at 0x00564350
(`mov dword ptr [ebx + ebp*4 + 0x30], 1`). Nothing in the roster records the BLUEPRINT the
recruiting script named — which is the reason recruitment itself cannot be replicated from
the roster (see the boundary below).

**The canonical spawn-then-add pair** is the game's own: `CSWGuiPartySelection::AcceptParty`
(0x006BE560) calls `SpawnNPC` at 0x006BE90C and `AddMember` at 0x006BEA25.

## What was built

- Wire: `PacketType.PartyRoster = 37`, protocol version 22. Two nine-bit masks (available,
  in-party) plus a timestamp — 16 bytes. **A LEVEL**, broadcast every tick by the host,
  including when it is empty (the Endar Spire before Trask joins is a real all-zero roster,
  and a client with no level reconciles nothing).
- Shared block **version 34**, region 0x3300..0x3320, `TotalSize` 0x3300 → 0x3400. One PACKED
  dword per direction: bits 0..8 available, 16..24 in-party, **bit 31 = "somebody wrote
  this"**. Packing removes the tearing question rather than answering it with a serial; bit 31
  separates "no party table to read" (main menu, transition) from "nothing recruited", which
  are otherwise the same two zero masks — and publishing the second while it was really the
  first would tell a client to dismiss everyone.
- Plugin: `PartyRosterController`, publishing every frame and reconciling every 30 frames;
  **one operation per pass**, removes before adds, spawning through `SpawnAvailableNPC` only
  when the slot has no body.

## The boundary, deliberately not crossed

**This does not replicate RECRUITMENT.** Making a companion available needs
`AddAvailableNPCByTemplate`/`ByObject` and therefore the blueprint the script named, which
+0x30 does not record (it is a 1). Availability is expected to arrive the way it already does
— the same conversation running on both instances. What a partial or aborted conversation
actually gets wrong is the PARTY SEAT, and that is what this fixes. A slot the peer has in its
party and this instance never recruited is logged once, by slot, and skipped.

## Giving the companions back (2026-08-15)

Everything this document does to a companion on a mirroring instance is now undone on the
falling edge of `block.Command`, through `PartyPresenceController.ReleaseSession` —
solo mode via `ReleasePartyFreeze`, the Neutral faction hold and the min-1-hp pin, the
roster reconciliation, and the interaction locks that hold a body still. The write-up of
why they were leaking (and of the control-swap suppression that made the returned companion
unclickable, which is the same class of bug) is in `client-control-lock.md`.

Two things a mirroring pass writes are still NOT restored, deliberately, and both self-heal:
`ClaimCreature` leaves `commandable` (+0xE8) at 1, which is a party member's normal value
anyway, and leaves `run_flag` (+0xA98) at 0, which the engine's own follow path rewrites at
the next gait change. Restoring either would mean writing a value we never measured.

Still open: `force_dialogue_party` remains off. The roster now reconciles after a recruitment
conversation, but a player driven into one whose scripts do not run to the end here can still
end up without the companion — that is the availability gap above, not a roster one.

## The presence mirror gets the same roster-freeze guard the roster controller has (2026-08-21)

Roadmap item 9. `PartyRosterController.Reconcile` already backs off while
`Odyssey.IsPartyRosterFrozen()` (party table +0xF0 != -1 — a script has taken one specific
companion under direct control: the Leviathan brig, the Rakatan temple, any forced-solo beat).
`PartyPresenceController` had no such guard, despite driving EVERY companion body every pass —
not just the one the script owns — so a mirroring instance kept re-claiming movement flags and
overwriting health on a body an engine script was mid-sequence with. Fixed by the same edge
check, extracted as `PartyFreezeRule` (`Decide(frozenNow, frozenBefore)` — Drive / ReleaseAndLog
/ StayOff / ResumeAndLog) and wired into `PartyPresenceController.OnFrame` right after the
conversation guard. On the freezing edge it also hands back anything currently held Neutral and
min-1-hp pinned, through `ReleaseAllHeld` — the same call `ReleaseSession` uses on disconnect,
factored out of it — because a companion can walk into the freeze already pinned from a fight
seconds earlier, and the pin alone re-creates the aggro lock if left in place (see
`ApplyCompanionFaction`'s save-hazard note). Two log lines per freeze, not one per frame:
`PartyFreezeRule` is edge-triggered on both sides.

**Solo mode (+0x190) is deliberately NOT released by this guard.** Three reasons, in order of
how much they matter:

1. **It isn't this controller's to release.** Every write to +0x190 lives in
   `KotorGameBridge.TryWritePartyMembers`/`ReleasePartyFreeze` — the C# agent, outside the
   plugin — which re-asserts it every pump regardless of what any plugin controller does this
   frame. `PartyPresenceController` has never written +0x190 and this task's scope (a plugin-side
   guard, no new engine writes) does not license adding a first write to a field a different
   project already owns and re-asserts continuously; a write here would just race the agent's own
   next pump.
2. **The two fields answer different questions.** +0xF0 (`pt_controlled_npc`) says "a script
   currently drives ONE named companion directly"; +0x190 (solo mode) says "the OTHER companions'
   own follow-AI is switched off". Per the engine findings above, the controlled-npc path is not
   gated by solo mode at all — RunActions dispatches follow (action id 61) through a completely
   separate check (+0xA88, +0x4C0, `+0x9F0 & 2`) that a directly-controlled companion does not
   go through. Turning solo mode off for the freeze's duration would not free the scripted
   companion from anything; it would only let the engine's own follow-AI fire on the companions
   the script is NOT driving — reintroducing the exact drift (`RunActions` issuing
   `ActionFollowLeader` on its own) this whole channel exists to suppress.
3. **No live evidence either way.** This project's engine-write rule requires verifying a write
   live before shipping it, and there is no rig access from this task. Absent a measured reason
   to touch a field this controller has never owned, the conservative call is to leave it alone
   and record the reasoning here rather than guess at a new write.

If a future NEEDS-LIVE pass ever measures the controlled companion's AI fighting solo mode during
a real forced-solo sequence, the fix belongs in `KotorGameBridge` (the field's owner) gated on
`IsPartyRosterFrozen`, not in this guard.

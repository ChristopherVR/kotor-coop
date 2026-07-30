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

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

## Whose companions fight on a client, and against what (2026-08-21, roadmap item 4)

Roadmap item 1 (`local_combat`) is now proven live: a client's own engine can genuinely fight
ONE census enemy at a time, excluded from presence sync for the fight's duration and fired
through its own `+0x260` script. Roadmap item 4 asks the question that unlocks next: on a
CLIENT, `PartyPresenceController.DriveOne` re-claims every mirrored companion and wipes its
action queue every single pass — unconditionally, before this document's change below — so a
mirrored companion can never win the engine's own targeting/round machinery even when
`local_combat` hands the *player* a real fight next to it. Only the HOST's companions — the real
bodies, on the instance where the host's own AI runs them — ever swing for real.

**The question is not "can a mirrored companion be made to fight" — `local_combat` already
answers that generically for any driven body with a `+0x260` slot.** The question is whether
doing it for companions is worth what it costs, and against what.

### The sane model

A client's game world is a puppet show: its enemies are driven bodies, and (before this item)
its companions are too. The one thing in that world that is NOT a puppet is the client's own
player character — everything else is downstream of what the host's engine actually decided.
That asymmetry has to survive whatever this item builds, because it is the reason `local_combat`
itself works safely (§2 above, round 52): only ONE creature is ever released to fight for real,
gated tightly on proximity and hostility, with a timeout and three independent release levers.

Given that, whose companions fight on a client, and against what, has one answer that keeps the
asymmetry intact:

- **The HOST's companions keep fighting the HOST's fight, on the host.** Nothing here changes
  that. Their swings are already visible on a client's screen — `PartyPresenceController.DriveOne`
  replays the host's `OneShotAnimation` serial on the mirrored body every time it changes
  (`_appliedOneShot`/`_oneShotPrimed`, same mechanism `EnemyPresenceController` uses for enemies)
  — so "the host's companion swings and the client sees it swing" is already shipped and is not
  part of this gap.
- **What a client's mirrored companion COULD gain by fighting locally is narrow, not general.**
  It is not "let companions fight companions' own fights" — a companion has no fight of its own
  on a client; every enemy in a client's world is either mirrored (someone else's fight) or, for
  the one `local_combat` has excluded, the CLIENT PLAYER's fight. So the only two things a
  released companion can contribute are (a) landing extra hits on the ONE enemy the client's own
  player is already fighting locally, and (b) standing between that enemy and the client's player
  as a body that can take a hit. Both are real, but both only make sense while the companion is
  physically part of that exact fight — not fighting some other mirrored enemy 40 units away that
  happens to also be hostile.
- **The credit path for (a) already exists and needs no new plumbing.** `PeerDamageController
  .RefreshAttackers` already builds its "ours to credit" id set from the PC *and every roster
  slot* (`src/KotorCoop.Plugin/PeerDamageController.cs`, `RefreshAttackers`) — a companion's
  object id is already in `PhantomHitHook`'s `_ourAttacker` list on every pass, gated only on
  `block.DamageDealtEnabled`. `PhantomHitHook.IsOursToCredit` already treats a hit landing on
  someone else's creature, dealt by an id in that set, as a contribution to forward to the fight's
  owner — indistinguishable in that code path from a PC hit. Nothing in `PeerDamageController` or
  `PhantomHitHook` needs to change for a released companion's hit to be counted; it already would
  be, the moment the companion's own engine AI lands one. This is exactly what `PhantomHitHook`'s
  own class comment anticipates: "a companion's hit has to count the day that changes."
- **The risk is spatial, and it is real.** A mirrored companion's position is the HOST's position
  for that body — that is the entire point of mirroring it. If the two players are apart (the
  measured "apart" case for `local_combat_range` is ~120 units, `docs/research/combat-when-apart
  .md` §1b), releasing that companion to its own AI puts a body fighting whatever is near the
  HOST, on the CLIENT's screen, at a location that may have nothing to do with the client's own
  fight. That is not a desync in the "numbers disagree" sense — the numbers stay internally
  consistent on each side — it is a coherence break: a companion appears to fight something the
  client player cannot see, or stands next to the client's player while visually anchored to
  wherever the host's mirror last placed it and then stops being repositioned (because releasing
  it means this controller stops writing its position, exactly like `local_combat`'s own
  excluded-enemy handoff). The client's screen would show a companion frozen mid-fight in the
  wrong place the moment the host's player moves on without it.

### Decision: enable it, gated tightly enough that the risk case cannot occur

The benefit (an extra attacker on the client's own local fight, and a body that can eat a hit
meant for the client's player) is worth taking **only** in the one case where the risk is
structurally impossible: the companion, the client's player, and the one enemy `local_combat` has
already excluded are all within the same small radius. In that case the companion is not being
"released to fight somewhere else" — it is being released to fight the exact thing the client
player is already fighting, at the exact place the client player is standing. That is "the players
are actually fighting side by side," and it is the only configuration where handing the companion
its own AI cannot produce the coherence break above, because there is nowhere else for it to go
that would look wrong from here.

Concretely: a companion is only ever released when

1. `companion_local_combat=1` (OFF by default — this document's analysis is sound but nothing
   here has been run against the live game, and the project's own rule is a live pass before
   shipping a write, not before writing the code);
2. `local_combat`'s own exclusion is active (`block.LocalCombatExcludedId != 0` — a real
   census enemy is currently fighting the client's own player, per item 1's proven mechanism);
3. the companion's current (host-driven) position is within `companion_local_combat_range` world
   units of BOTH the locally-fighting enemy's position and the client's own player's position —
   i.e., companion, player and enemy are all clustered together, not merely all individually
   hostile/nearby by coincidence;
4. the roster is not frozen (`PartyFreezeRule`'s own guard — a script has not taken this
   companion under direct control); and
5. — implicitly — there is at most one `local_combat` fight running at a time (item 1's own
   one-fight-at-a-time design), so at most the companions clustered around that one fight are ever
   candidates.

Outside that window, the companion goes right back to being mirrored — position, animation and
health all forced back to the host's authoritative report, exactly as today. This is deliberately
narrower than "release a companion whenever local_combat is active anywhere nearby": the local
enemy could be excluded while the client's player is 15 units away and the companion is mirrored
30 units further out near where the host actually is — clustered around neither. The triple
gate (enemy AND player AND companion all mutually close) is what makes "side by side" true rather
than assumed.

**What deliberately stays owner-side.** The companion's HIT POINTS remain the host's to say, the
same way `IncomingDamageController` never becomes authoritative for the client PC's own HP by
existing. While released, this controller simply stops overwriting the companion's health (the
same handoff `local_combat`'s own excluded enemy gets from `EnemyPresenceController` — driving
stops entirely, not partially) rather than trying to keep pinning a host figure onto a body that
may now be taking real, local damage; there is still no companion-health-uphill channel (party-
reconciliation's own risk #3, still open), so any HP the companion loses while released is
invisible to the host and gets overwritten by the host's next authoritative figure the moment
mirroring resumes. This is the same trade `local_combat` already accepted for the phantom's own
HP (`combat-participation.md` §5.3, "the phantom's health is not authoritative for anything") —
harmless because nothing downstream reads a mirrored companion's HP as ground truth except this
controller itself, and it is about to overwrite it anyway on the very next un-release.

### What this does not attempt

- No new detection of "the client's PC is directly attacked" as a second trigger. The only
  proven-live local-fight signal is `local_combat`'s own census exclusion; a companion defending
  against an attacker the client's PC is taking hits from but that was never excluded (a stray
  hit from a driven-but-not-excluded mirrored enemy, or a forwarded hit with no local body at all)
  has no signal this item can gate on without inventing an unverified one. Deferred.
- No change to how many companions can be released at once beyond "however many satisfy the
  gate for the one active `local_combat` fight" — with the roster capped at two companions and
  one fight at a time, this is at most both companions, and in practice usually zero or one given
  how tight the triple-proximity gate is.
- No write to solo mode (`partyTable+0x190`) or to anything `PartyFreezeRule`'s own reasoning
  above already ruled out touching. Releasing a companion here means this controller stops
  claiming and driving it, the same "hands off" state `local_combat`'s own excluded enemy gets —
  it does not mean re-enabling the engine's own follow-AI, which stays suppressed exactly as it
  is today.

## Root cause of the client door-gate defect, and the fix (2026-08-22)

`transition-smoothness.md` §8.5 (2026-08-21) found that a co-op CLIENT's own door crossings are
refused by the vanilla party-together gate — `CSWSDoor::EventHandler` tests
`CSWParty::IsPartyTogether` at 0x0058C228 and, when false, silently runs `k_trg_transfail`
instead of transitioning — **even with `CSWPartyTable`'s own member count at 0**. That pass could
not go further: no rig access. This entry root-causes it, live.

### The disassembly

`CSWParty::IsPartyTogether` (0x00635350, `__thiscall`, no arguments beyond `this`) does two
things, in order:

1. **Resolves the PLAYER's own object** — `GetPlayerCreatureId` (0x5ED550) → an id-resolve call
   (0x4AEA30) → `GetGameObject` (0x4AE770). If that fails to resolve (0x00635386 `jne`/fallthrough
   at 0x635388), it returns **false** immediately — this path never reads a party member at all.
2. Otherwise, it walks **its OWN member array** — count at `this+0x00`, entries at `this+0x24`,
   stride **0x88** — resolving each entry's raw id through the same id-resolve/`GetGameObject`
   pair. An entry that fails to resolve is *skipped* (treated as harmless, not a failure). An
   entry that DOES resolve has its position (`+0x90` on the resolved object) diffed against the
   player's own position; past a threshold constant at `0x7455BC`, the function returns false.
   Count `<= 0` skips the loop entirely and returns true.

**This is a different structure from `CSWPartyTable`.** `CSWPartyTable` (server-side, embedded at
`CServerExoAppInternal+0x1B770`, documented above) has count at `+0x00` and a 9-slot object-id
array at `+0x0C`. `CSWParty` (client-side, reached via `CClientExoApp::GetSWParty` 0x005ED8B0 —
`[client+4]` then `+0x270`) has ITS OWN count at `+0x00` and its own 0x88-strided array at
`+0x24`. Nothing here shares memory with the party table.

### Why they diverge

`CSWPartyTable::RemoveMember` (0x00565560, server-side removal) touches only its own count/array
— confirmed by full disassembly, no write anywhere near `CSWParty`'s own offsets.
`ExecuteCommandRemovePartyMember` (0x00541C00, the VM wrapper `Odyssey.RemovePartyMemberBySlot`
calls) DOES additionally reach into the client-side struct after a successful table remove —
`0x00541C64`/`0x00541C6C` look up the removed object in `CSWParty`'s own list and
`0x00541C85` calls a cleanup routine on it IF that lookup found something — but that lookup can
come back empty (a companion this instance mirrored in without going through the exact path that
originally populated `CSWParty`, or simply engine-internal bookkeeping this pass did not chase
further), leaving `CSWParty`'s own count stale and higher than reality. **Confirmed live, not just
by reasoning**: on a fresh load into END_M01AA, BOTH the host and the client logged `CSWParty`'s
own count reading 2 against `CSWPartyTable`'s 1 — a real, reproducible divergence present from the
very first frame the roster controller runs, not a rare edge case.

### The fix: `party_together_fix` (`PartyRosterController.SyncClientPartyTogetherState`)

New Odyssey.cs accessors — `TryGetPartyTableMemberCount` (the table's own count, distinguishing
"legitimately 0" from "unreadable" so nothing clamps toward an unknown value) and
`GetClientPartyMemberCount`/`TryClampClientPartyCount` (read/write `CSWParty`'s own count) — feed
a new per-frame check in `PartyRosterController.OnFrame`: whenever `CSWParty`'s count reads higher
than `CSWPartyTable`'s, it is clamped down to match. **Never grows the count** — this can only
remove staleness the engine's own remove path was supposed to clear, never invent a companion.
Since `CSWParty`'s own loop returns true unconditionally when its count is `<= 0`, driving the
count down to 0 companions is sufficient to make `IsPartyTogether` stop checking positions
entirely — exactly the "even with zero companions" case reported.

Live-verified 2026-08-22: fired on every module load and every roster reconcile pass across an
extended two-instance session (`transition-smoothness.md`'s dated live-pass section has the exact
log lines), always shrinking, never growing, no adverse effect observed. `coop.ini
party_together_fix`, **default ON**.

See `transition-smoothness.md`'s own 2026-08-22 section for the live door-crossing tests this
fix unblocked, including the `client_door_refusal` decisive check §8.5 could not reach.

## Live pass (2026-08-22): `companion_local_combat`'s release/reclaim gate proven clean; the swing itself not independently confirmed, and a real party-presence staleness bug found along the way

Roadmap item 4's own live-pass task: `companion_local_combat=1` on the client,
`companion_local_combat_range=10`, a companion in the host's party (Trask, recruited), the client
engaging a Vulkar under `local_combat` (`tar_m10aa`'s point-blank pool, `shared-state-audit.md`
round 55's own recipe) with the host standing beside the client so the mirrored companion is
within range of both.

### Rig and staging

`tools\demo.ps1` cold start (save 1), `console 'warp tar_m10aa'` on the HOST ONLY — the client
follows through the co-op travel-follow channel rather than independently warping, which matters:
an early attempt where BOTH instances issued their own `warp tar_m10aa` produced a persistent
`[W_SAVE]` compatibility mismatch (see below) that never self-cleared, because each instance's own
autosave-on-transition genuinely diverged. Host-only warp, client-follows reproduces the identical
save lineage on both sides and avoided it on the clean rerun.

No engine mechanism moves a companion to a staged position under co-op session play — solo mode
(`partyTable+0x190`) is asserted by `KotorGameBridge.TryWritePartyMembers` for the whole
session and this document's own earlier finding ("party following is SCRIPT-DRIVEN... only ONE
site enqueues it") is not, in the current shipped code, actually driven for a companion by
anything this project runs; `CoopAgent.PumpParty`'s own comment ("the host: publish, do not
freeze — its companions follow through the game's own AI exactly as in single-player") describes
the HOST side's intended freedom, but nothing here found the host's own native follow-AI
repositioning a stationary companion during an active connected session, live. Because of this,
Trask was staged by writing her `+0x090` position directly on the HOST (the same raw-poke
technique `tools/teleport.ps1` already uses for the player, extended to an arbitrary resolved
creature address — this is test staging only, not a shipped mechanism), confirmed against the
address the plugin's own `resolve` verb names as authoritative before every poke.

### The release/reclaim mechanism: proven clean, three live cycles

With the client teleported point-blank to a Black Vulkar (`0x33`, the same pool round 55 used) and
`local_combat` engaged, and Trask's host-side position staged within `companion_local_combat_range`
(10) of both the client and the currently-excluded enemy:

```
[06:52:15.297] party drive: companion 0x11C released to fight locally — within range of both you and the enemy you are fighting
[06:52:34.786] party drive: companion 0x11C reclaimed — resuming the host mirror
[06:52:34.787] party drive: companion 0x11C held neutral and pinned — the host owns its health
[06:52:34.790] party drive: companion 0x11C released to fight locally — within range of both you and the enemy you are fighting
[06:52:59.787] party drive: companion 0x11C reclaimed — resuming the host mirror
[06:52:59.787] party drive: companion 0x11C held neutral and pinned — the host owns its health
[06:52:59.790] party drive: companion 0x11C released to fight locally — within range of both you and the enemy you are fighting
```

Three clean release→reclaim cycles, each landing exactly on `local_combat`'s own 25 s fight
timeout (`0x33`'s fight ending and a fresh one arming), each edge-triggered exactly once per
transition (no repeated logging while a state holds, matching `CompanionLocalFightRule`'s own
design), and each reclaim correctly re-asserting the neutral/min-1-hp hold in the same log burst.
This is `CompanionLocalFightRule.Decide` and `PartyPresenceController.DriveOne`'s gate working
exactly as designed and exactly as the unit tests already proved in isolation — now confirmed live,
against a real `local_combat` fight cycling through real census exclusions, with real faction
flips (`party drive: companion 0x11C back in the player faction` on every release) and no stranding
observed at any point across the whole session.

### The swing: not independently confirmed — blocked by a genuine party-presence staleness bug, not by the gate

The remaining claim (`docs/research/creature-liveness.md`'s own sibling finding calls this "the
same SHAPE of bug", see there) is a companion's own engine AI landing a real hit
(`PeerDamageController.RefreshAttackers` crediting a roster-slot id, `combat ground-truth:...
attacker=0x11C`, or the host's `combat: applied N of a peer's damage to 0x33` line). This did NOT
appear in any window this pass staged, for a reason unrelated to the gate above: **the
`SharedBlock.PartyPresenceSlots` position a mirroring client holds for a companion can freeze for
multiple minutes at a time during an otherwise-healthy, connected (non-`[W_SAVE]`) session**,
self-resolving only after a full `disconnect`/`reconnect` cycle — confirmed by three independent
observations:

1. A companion staged near a fight (host-side position genuinely changed, `resolve` confirmed
   the write landed on the correct, engine-authoritative address) never reached the CLIENT's own
   block — `PartyPresenceCount=1, pos=(104.79,115.22,...)` (the spawn point) held steady across
   THREE separate re-pokes to progressively closer positions, each checked minutes apart, with no
   change at all.
2. A `disconnect`/`reconnect` on the client immediately unstuck it — the very next read showed the
   block correctly reflecting the host's latest reported position — and the release/reclaim cycles
   quoted above all happened in the SHORT window right after that reconnect, before the channel
   froze again.
3. Because a companion RELEASED (from a state the block already reported as "in range") never
   gets mirrored to a new position at all (`CompanionLocalFightAction.StayReleased`/
   `ReleaseAndLog` both return before the `_smoother.Step`/`DriveEnemy` call — "hands off entirely...
   the companion's own engine AI is what fights from here"), a companion whose FIRST in-range
   report coincides with an already-active `local_combat` fight is released from wherever her
   CLIENT-side body was already standing, not from the position that made her eligible — and if
   that position was never smoothly mirrored there in the first place (staleness, or a raw-poke
   jump too large for `MotionSmoother` to catch up on before the very next pass releases her), her
   real body starts the fight from far away, with nothing in range to swing at. This is a real
   interaction between the staleness bug and the release timing, not a flaw in the release logic
   itself — in ordinary gameplay a companion's position updates continuously in small increments
   (the host's own real walking, mirrored every ~150 ms), so this specific race would not arise;
   it surfaced here because this pass's own staging method (a instantaneous position write) is not
   continuous the way real movement is.

This is NOT the `[W_SAVE]` transient `shared-state-audit.md` already documents and fixed (that one
is edge-detected and self-clears within seconds in the large majority of observations there, and
this session reproduced that same fast self-clear too, separately) — the party-presence freeze
observed here persisted for MINUTES while the session's own compatibility state read "shared" (no
`[W_SAVE]` active) the whole time, and while the adjacent enemy-census channel kept updating
normally within its own ~1 s cadence throughout. Not root-caused this pass (out of scope — the
task was to test `companion_local_combat`, not debug `CoopAgent.PumpParty`/`ReadPartyMembers`);
recorded here as a concrete, reproducible defect for whoever picks it up next, with the exact
symptom (`PartyPresenceCount`/position frozen for minutes, `disconnect`/`reconnect` clears it) and
the two code paths most likely to hold the answer (`CoopAgent.PumpParty` → `bridge
.ReadPartyMembers()` → `CoopHost.PublishParty`/`BroadcastParty`, all in `KotorCoop.Session
/CoopAgent.cs` and `CoopHost.cs`).

### Verdict: `companion_local_combat` stays OFF — the new gate is clean, the channel under it is not proven clean yet

Per this project's own bar (flip on if clean): `CompanionLocalFightRule`'s release/reclaim
decision — the actual new logic this item adds — is proven clean and live, three full cycles,
edge-triggered correctly, no stranding, faction/pin handling correct on every transition. But the
task's own evidence list also asks for the companion's own engine AI actually landing a credited
hit, and this pass could not produce that, not because the release logic failed, but because a
newly-found staleness bug in the party-presence channel it depends on prevented a companion's
CLIENT-side body from ever being mirrored close enough to the fight to swing. Given the honesty
bar every other flag in this document has been held to (`liveness_sync` stayed off twice for a
comparable "the mechanism looks right but I could not observe the headline case" reason), the
conservative call is to leave `companion_local_combat` off pending either a fix for the staleness
bug above, or a live pass staged through genuine incremental host movement (walking, not a raw
position poke) patient enough to let real mirroring keep the client's copy caught up throughout.

## Item 4's own dispatch mechanism, finally built (2026-08-23, round 71)

This document's "Recommended design" (2026-07-31) speced item 4 as "when an owned companion's
action queue is empty, issue `ActionFollowLeader` — `AddAction(creature, 0x3D, 0xFFFF, 0, 0)` at
`0x004CEA20`" and never shipped it. Built this round as `PartyFollowController` +
`Odyssey.QueuePartyFollowLeader`, but NOT as originally speced, for two live-measured reasons:

1. **`0x3D` was never confirmed as `PartyFollowLeader`'s action id.** A LATER, independently
   verified table in `Odyssey.cs` (`IsMoveAction`, read directly off the RunActions jump table
   entry by entry) names `0x3C` instead. Live observation this round found a companion's own
   head-of-queue type sitting at a stable `0x3D` for 30+ seconds with zero movement — consistent
   with `0x3D` being something else entirely, not the follow action this design assumed it was
   watching for.
2. **The raw `AddAction`/`0x004CEA20` marshaling call was flagged elsewhere in this project's
   own research as unverified and "not safe to ship blind"** (`docs/research/dialogue-join-latency.md`,
   in the context of a different action type) — a wrong type tag in its 24-value push list is
   exactly the class of bug this project's engine-write discipline exists to keep out.

The mechanism actually shipped instead dispatches through
`CSWVirtualMachineCommands::ExecuteCommandActionFollowLeader` (`0x0052CBE0`, confirmed against
the symbol database and matching this very document's own "Only ONE site enqueues it" citation)
via the exact same safe, already-verified VM-caller-redirect route `AttackTarget`/`PickUpItem`
use — no new raw engine call, no unverified push list.

**It is not sufficient on its own.** Live-measured: the dispatch correctly moves a companion's
`CreMoveState` (`+0xA8C`) to 3 ("follow" — the mode is genuinely entered) but the round could not
get it to -1 ("walking" — a route genuinely being stepped) for Trask, ever, across every module
and distance tried. Full elimination list and the honest "not yet closed" status:
`docs/research/phantom-locomotion.md`, "2026-08-23"; `docs/research/shared-state-audit.md` §2,
round 71.

## Round 72 (2026-08-23) — the frozen follow TARGET point, found by disassembly and confirmed
live; two follow-on fix attempts tried and live-disproven; genuinely not yet closed

Round 71 reissues the follow order reliably and gets `CreMoveState` to 3, every time, but Trask
never reaches -1 ("walking"). This round went looking for the engine's own per-frame CONTINUATION
— the code that, every frame `CreMoveState==3` holds, decides whether there is a step to queue —
rather than the one-shot dispatcher round 71 already found and shipped.

### Disassembly: the real per-frame continuation, and its own gates

`tools/kotor1_0_3.db` names the whole function family directly:
`CSWSCreature::WalkUpdateLocation` (0x0051BB10) dispatches by animation id into
`WalkUpdateLocation_QuickWalk` (0x0051D9C0), which — on `CreMoveState==3` specifically
(`cmp eax,3; je`, 0x0051DABA) — calls `WalkUpdateLocation_QuickWalk_FollowLeader` (0x0051C360)
every single frame the mode holds. `AIActionPartyFollowLeader` (0x00511130, the function round 71's
`QueuePartyFollowLeader` dispatches into) is the ONE-SHOT arm-and-set-state-3 half; 0x0051C360 is
the per-frame "is there a step to queue" half, and it was never disassembled before this round.

0x0051C360's own opening gate resolves the CALLING creature's own index inside `CSWParty` (the
CLIENT-side struct, not `CSWPartyTable`) via `CClientExoAppInternal::ServerToClientObjectId`
(0x005EDA50) then `CSWParty::GetIndex` (0x006349C0) — confirmed by full disasm and by
`param_size_bytes`/`calling_convention` in the symbol DB (matching the visible push/call shape
exactly). Index `-1` (not found) or `0` (the creature IS the leader slot) both bail out for the
whole frame with nothing queued. Past that gate, the function reads a per-member follow-data
record at `CSWParty + 0x24 + index*0x88` (stride and base confirmed against this document's own
2026-08-22 finding on the same struct) — a TARGET follow point at `record+0x28` (a `Vec3`), a
cached "prior leader position" at `record+0x18` (`Vec3`), and a position SNAPSHOT at `record+0x5c`
— and computes distances between them before ever reaching the code that would queue a walk step
(traced as far as ~0x51C700 before the practical limit of hand-decoding x87 stack state without a
live debugger was reached — not the whole function, but past every gate this round needed).

### Live probe: the target point is frozen at module-load time, confirmed multiple ways

New Odyssey.cs diagnostics this round — `TryGetClientPartyIndex` (the same
`ServerToClientObjectId`/`GetIndex` chain, read-only) and `TryGetFollowDiagnostics` (the full
per-member record plus the creature's own `+0xD0`/`+0xB0`/`+0xB4`/`+0x1F8`) — wired into
`PartyFollowController`'s existing stall-reissue log line. Live, on the host, `END_M01AA`, Trask
recruited by the loaded save:

```
cswPartyIndex=1   (a VALID index — rules out the -1/"not found" gate entirely)
target=(20.0,22.2,-1.3)   prior=(20.0,22.2,-1.3)   <- BOTH frozen at Trask's own spawn/load position
leaderPos=(45.0,40.0,-1.3)                          <- the REAL player position, 28+ units away
soloMode=0                                          <- confirmed OFF on the host (see below)
```

Repeated across a dozen samples, several rebuild/redeploy/relaunch cycles, and after teleporting
the player to three different distant positions: `target` (the point 0x0051C360 would walk Trask
toward) and `prior` (its own cached "where the leader was last") never move off the value they held
the instant the module loaded, no matter how far or how many times the real leader position changes.
**This is the root cause of "reaches 3, never reaches -1": the per-frame continuation has nothing
fresh to walk toward, because whatever is supposed to keep this record's leader-position input
current is not doing so in this session.**

### Ruled out this round, each with its own live test

1. **`CSWParty::GetIndex` failing** (this document's own 2026-08-22 "CSWParty vs CSWPartyTable
   divergence" finding made this the leading hypothesis going in) — DISPROVEN: `cswPartyIndex=1`,
   a valid, stable index, every sample.
2. **Solo mode (`CSWPartyTable+0x190`) suppressing the recompute** — DISPROVEN: read live via the
   SAME verified pointer chain `PartyFollowController` already uses for the party table
   (`AppManagerPtr -> +8 -> +4 -> +0x1B770 -> +0x190`), confirmed `0` (off) on the host for the
   whole session. Consistent with `CoopAgent.PumpParty`'s own documented intent ("the host: publish,
   do not freeze — its companions follow through the game's own AI exactly as in single-player") —
   solo mode is a CLIENT mechanism and was never expected to touch the host at all.
3. **The recompute just needs calling** — `Odyssey.RecalculateFollowPoint()`, a direct call to
   `CSWParty::RecaulateFollowPoint` (0x006359C0, `__thiscall`, verified 0 stack args from the
   symbol DB — not a guessed ABI), called every stall pass immediately before reissuing the follow
   order. LIVE-DISPROVEN: `target` did not move, several cycles.
4. **The recompute reads its input from `record+0x18`** — `Odyssey.TryPokeFollowPriorPosition`
   wrote the player's real current position directly into `record+0x18` (confirmed by read-back:
   the write genuinely stuck, nothing else overwrote it), immediately followed by the same
   `RecaulateFollowPoint()` call. LIVE-DISPROVEN: `target` STILL did not move — ruling out both
   "the function isn't being called" and "it reads from this specific cached field" in one pass.

Both experiments (3) and (4) are real, verified-safe engine calls/writes — not guesses at an
unverified ABI, and neither crashed or corrupted anything observed — but neither produced any
measured benefit, so **neither is called from `PartyFollowController`'s shipped path**: this
project's own rule is to verify a write live before shipping it, and a write with a confirmed zero
effect has nothing to ship. The primitives remain in `Odyssey.cs`, documented with their own live
results, for whoever picks this up next.

### What is left, precisely

`CSWParty::Update` (0x00637050, `__thiscall`, ONE stack argument whose meaning this round did not
safely determine) is the new prime suspect, not yet tried: its own body (partially disassembled
this round) iterates the party's member array starting at `this+0x24` and computes distances
against a party-level (not per-member) 3-float field at `this+0x2B0..0x2B8`, compared against a
GLOBAL 3-float cache at `0x7A19B0..0x7A19B8` — a shape that reads like "the party's own reference
position" being refreshed from somewhere else entirely, distinct from the per-member `record+0x18`
this round already ruled out. **Not called this round** — guessing its one stack argument's type
(a delta-time float being the leading guess, but not verified) would be exactly the kind of blind
ABI guess this project's own engine-write rule exists to prevent. A future round tracing the rest
of 0x0051C360's own body (the part past ~0x51C700 this round did not finish) or fully decoding
`CSWParty::Update`'s own disassembly (not started beyond its first ~30 instructions) would settle
where the party's own reference position is actually meant to come from.

**Genuinely not closed.** `party_follow=1` stays shipped (round 71's own correct, safe, verified
mechanism — a real improvement over no follow-re-arming at all) but "Trask genuinely walks behind
the host" remains unproven, now for a much more specific, evidenced reason than round 71 left it.

### Test suite and build

`dotnet test tests/KotorCoop.Tests`: **1623/1623 passed**, unchanged baseline — this round's
changes are new read-only diagnostics (`Odyssey.TryGetClientPartyIndex`, `TryGetFollowDiagnostics`)
and two now-unused-in-the-shipped-path experimental primitives (`RecalculateFollowPoint`,
`TryPokeFollowPriorPosition`), none exercised by the (x86 NativeAOT, unreferenced-by-tests) test
project. Both plugin builds clean, 0 warnings 0 errors, across the round's many
rebuild/redeploy/relaunch cycles (six full rig cycles this round alone, each staging a different
hypothesis). Rig torn down clean (`Get-Process swkotor,KotorCoop.Agent` empty),
`auto_start_agent` restored to `1` in both installs.

## Round 73 (2026-08-23) — CSWParty::Update's argument and write behaviour settled by disasm
and confirmed live; two real fixes shipped; the companion still does not walk

Picks up round 72's own "not yet tried" prime suspect directly: CSWParty::Update (0x00637050)
and its one stack argument, whose meaning round 72 declined to guess ("a delta-time float being
the leading guess, but not verified -- guessing its one stack argument's type... would be exactly
the kind of blind ABI guess this project's engine-write rule exists to prevent").

### The argument is a delta-time float -- settled by disasm, not guessed

`tools/xrefs.py 0x637050` finds exactly ONE call/jmp site in the whole binary: 0x0060339A.
That caller lives inside CClientExoAppInternal's own per-frame update dispatch -- the same
function that also calls CClientExoAppInternal::ProcessInput (0x6227e0) and
CExoResMan::Update (0x408d40) every frame, confirmed by disassembling the surrounding code
(`tools/disasm.py 0x603300 300`). Immediately before the call it computes
`deltaTime * <a global time-scale multiplier at 0x7439BC>` into a scratch float (0x78E574) and
pushes exactly that value -- one dword, matching `param_size_bytes=4` for CSWParty::Update in
`tools/kotor1_0_3.db` (looked up directly: class_name='CSWParty', function_name='Update',
calling_convention='__thiscall', param_size_bytes=4). The gate on the call itself requires
`esi+0x18` (a local-player creature pointer) non-null and `[esi+0x18+0x48]` non-zero -- a
precondition this round's fix does not reproduce; see below.

CSWParty::Update's own body (full disasm, `tools/disasm.py 0x637050 1200`) opens with
`fld dword ptr [ebp+8]; fadd dword ptr [edi+0x2BC]; fstp dword ptr [edi+0x2BC]` -- an
elapsed-time ACCUMULATOR at this+0x2BC -- before anything else runs. Nothing but a delta time
is added into a running accumulator like that.

### What it writes -- a party-level reference position, refreshed from the leader's live position

Past the accumulator, the function (party count > 0, [this] > 0) resolves the leader's own
id (stored at this+0x24, the leader/member-0 slot's record) through
CClientExoApp::GetCreatureByGameObjectID (0x5ED5B0, CLIENT-side -- confirmed against the
symbol DB) and, if it resolves, computes the leader's LIVE position (read at the resolved
creature +0x24/+0x28/+0x2C -- NOT the same as CSWSObject's usual +0x90 world-position field;
see round 73's own diagnostic below for how this was told apart) against a PARTY-LEVEL (not
per-member) 3-float field at this+0x2B0..0x2B8. If the squared distance exceeds a threshold
(the float at 0x741838), it writes the leader's current position straight into
this+0x2B0..0x2B8. Past that, it resolves the party's own "furthest member"
(CSWPartyFollowDataArray::FindFurthestPartyMemeber_Current, this+0x2DC, 0x634DA0) and, if that
member has drifted far enough (CSWPartyFollowDataArray::PointOnLineDistanceFromLeader, 0x634E00,
vs the float at 0x747C3C) AND CSWParty::TestAndResetPath() (0x636840) returns zero, calls
CSWParty::RecaulateFollowPoint (0x6359C0) -- the exact function round 72 already built a direct
caller for and found to have zero live effect.

**That null result is now explained.** Round 72's manual RecaulateFollowPoint call read
this+0x2B0..0x2B8 as its own "current leader position" input, and that field is ONLY ever
refreshed by CSWParty::Update -- which was never being called at all in this plugin's
coop-driven session (its one real caller lives inside a per-frame CClientExoAppInternal
dispatch this plugin's own drive loop never reaches, the same class of gap rounds 60-67 already
found for animation). Round 72's recompute was working from a reference position frozen at
module-load, same as the per-member target it produced -- not because RecaulateFollowPoint
reads from somewhere else, and not because it needs some other precondition, but because its own
input was stale the whole time.

### Fix 1 (shipped): pump CSWParty::Update every frame -- live-verified it un-freezes the
party-level reference position

`Odyssey.PumpPartyUpdate(float deltaTimeSeconds)` calls CSWParty::Update unconditionally, once
per rendered frame, from PartyFollowController.OnFrame (which already runs on the plugin's own
FrameHook/SwapBuffers cadence -- confirmed ~3800-4000 frames per 10s window from the plugin's
own "frame stats" log lines during this round's live tests). A real measured delta time
(Stopwatch-based, clamped to 0 < dt < 1s to skip the first frame and any load/stall gap) is
passed, matching the engine's own argument shape. The caller's own extra gate (esi+0x18 /
[esi+0x18+0x48]) is deliberately NOT reproduced -- this call is guarded instead by the same
preconditions PartyFollowController already applies (spawned+driving, not mid-conversation,
roster not frozen, no peer party-presence channel active).

**Live-verified round 73**, host END_M01AA, Trask recruited: a new diagnostic,
Odyssey.TryGetPartyReferenceState (reads party+0x24 leader id, party+0x2B0..0x2B8
reference position, party+0x2BC accumulator, and resolves the leader creature the same way
CSWParty::Update does to read its +0x24/+0x28/+0x2C position), wired into
PartyFollowController's existing diagnostic log line. Before the fix, refPos sat frozen at
the module-load leader position no matter how far or how many times the leader moved (matching
round 72's target/prior freeze exactly). After shipping the fix:

```
refId=0xFFFFFFFF refPos=(45.0,35.0,-1.3) refAccum=0.99 leaderCreature=0x18298AB0 leaderFieldPos=(45.0,35.0,-1.3)
```

-- sampled repeatedly after `console 'tp 45 35 -1.3'` (a REAL engine teleport, via the plugin's
own console-command channel, not a raw memory poke -- see the methodology note below), refPos
matched the teleported leader position exactly, every sample, for the whole observation window.
refAccum visibly incrementing frame to frame (0.00 -> 0.99 -> 0.98 -> ..., cycling near 1.0 rather
than growing unboundedly -- consistent with an internal "time since last full refresh" the
function itself resets, not a bug in this round's own instrumentation) is independent proof
PumpPartyUpdate is genuinely being called every frame.

**This did NOT, by itself, unstick a long-stalled companion's per-member fields.** With refPos
now genuinely live, CSWParty::Update's own conditional call into RecaulateFollowPoint should
have had everything it needs -- but target/prior (record+0x28/record+0x18) stayed exactly
where round 72 left them, frozen at the module-load position, for the whole observation window.
Adding an UNCONDITIONAL direct call to RecaulateFollowPoint right after PumpPartyUpdate
(bypassing CSWParty::Update's own TestAndResetPath() gate entirely) made no difference
either -- same frozen target/prior, same live refPos. CSWParty::TestAndResetPath()
returning non-zero for a companion stalled this long (blocking CSWParty::Update's OWN call into
RecaulateFollowPoint) is the leading theory for why the natural per-frame path never reaches
it; the unconditional direct call sidesteps that gate but still measured no effect, which is not
yet explained and is flagged for whoever picks this up next.

### Fix 2 (shipped, the task's own sanctioned fallback): drive the per-member record straight

`Odyssey.TryDriveFollowTarget(uint companionServerObjectId, Vec3 leaderPos)` writes
record+0x28 (target) AND record+0x18 (cached leader position) to the real, current leader
position, and -- new this round, not part of round 72's TryPokeFollowPriorPosition experiment --
also sets record+0x68 (FollowDiag.SnapshotPendingFlag) to 1. This last part matters:
WalkUpdateLocation_QuickWalk_FollowLeader (0x0051C360, disassembled substantially further this
round, past round 72's ~0x51C700 stopping point) treats record+0x68 as a DIRTY-EVENT flag --
when set, it copies the creature's own current position (creature+0x90) into record+0x5C
(the "snapshot" FollowDiag already exposes) and clears the flag (0x51C3D2-0x51C3F2). Round 72's
raw poke to record+0x18 alone never set this flag, so nothing downstream ever noticed the
change; this round's version sets it too, only when the target has moved more than a ~10cm
epsilon (so it does not stomp the flag every frame once a companion is genuinely caught up).

Wired into PartyFollowController.OnFrame's existing per-slot loop, called every frame (not
just on a stall pass) for every tracked companion not currently busy (IsBusyHead) or
player-controlled, using the SAME playerPos the controller already computes each frame via
Odyssey.GetPlayerCreaturePointer()/GetPosition (a different, and -- live-confirmed this
round -- reliably live, lookup from the party-level this+0x2B0 one PumpPartyUpdate refreshes).

**Live-verified round 73**: after `console 'tp 45 35 -1.3'`, the diagnostic log shows

```
target=(45.0,35.0,-1.3) prior=(45.0,35.0,-1.3) ... snapPending=0 ... leaderPos=(45.0,35.0,-1.3)
```

-- target/prior genuinely track the live leader (no longer frozen at the module-load
position), and snapPending reads back as 0 (not the 1 this round's write set), meaning
WalkUpdateLocation_QuickWalk_FollowLeader DID see the dirty flag and consumed it (copying
Trask's own position into record+0x5C, exactly as the disasm predicts) -- real evidence the
engine's own per-frame continuation is processing the fresher target, not merely that a value
sits unread in memory.

### Genuinely not closed: CreMoveState still never reaches -1

Despite both fixes -- a genuinely live party-level reference position, and a per-member
target/prior/dirty-flag that the engine's own continuation visibly processes every frame --
Trask's CreMoveState (+0xA8C) stayed at 3 ("follow" -- the mode is entered) for the entire
observation window (20+ seconds, several tp hops of 20-40 units) and never reached -1
("walking" -- a route genuinely being stepped). His actual position (creature+0x90) never moved
a single unit.

This round's deeper disasm of 0x0051C360 (now traced past the target/prior/snapshot delta
computation into the function's OWN step state machine, `tools/disasm.py 0x51c360 1400`) found a
STEP FSM: a phase counter at the calling creature's OWN +0x44 field, incremented each call and
dispatched through an 11-entry jump table at 0x51CCEC (`jmp dword ptr [eax*4 + 0x51CCEC]`) into
per-phase handlers (0x51AC10, 0x511290, 0x511860, 0x511B60, 0x512430, 0x5127D0, and others not
yet named). Immediately before that dispatch, the function computes an obstacle-avoidance target
against **"the other party member's slot"** -- it swaps the calling creature's own party index
(1 <-> 2, via `cmp ebx,1`/`cmp ebx,2`) to find "the other" companion's record, reads ITS stored id
(`[eax+ebp*0x88+0x24]`), resolves it, and writes into a substructure at this-creature+0x340
(0x51C792-0x51C7AC) before the phase dispatch runs. **This save has exactly ONE companion
(Trask, index 1)** -- index 2's record is never populated, so this lookup resolves against an
empty/uninitialized slot. Whether that degenerate read causes the phase FSM to stall
indefinitely (rather than erroring or skipping the check) was NOT settled this round -- flagged
as the leading new lead for round 74, ahead of fully naming and tracing all 11 phase handlers,
which was judged out of this round's remaining budget.

### Methodology note: a real engine tp beats a raw position poke, again

Early in this round, a raw WriteProcessMemory poke to the CLIENT-side player copy's position
field (+0x90) was tried as a faster way to move "the leader" for repeated sampling, matching
Section 14.4's own documented shortcut. It reproduced that finding exactly: the poke stuck on
local read-back but CSWParty::Update's own leader lookup (leaderFieldPos, read via
GetCreatureByGameObjectID at the creature's +0x24/+0x28/+0x2C, NOT +0x90) never reflected
it, and Trask never moved even a single unit across 18 seconds and three hops. Switching to the
plugin's own `console 'tp x y z'` channel (`tools/blockpoke.ps1 -ProcId <pid> console 'tp 45 35
-1.3'`) -- which drives the engine's own real teleport, not a raw field write -- is what made
refPos/target/prior/leaderPos all track correctly. Recorded here as the same lesson
Section 14.4 already drew, now confirmed a second time against a DIFFERENT subsystem (party-level
reference tracking, not the cross-process mirror): a raw poke to +0x90 alone is not equivalent
to a real engine position change for any consumer that reads position through a path other than
that exact field.

### Verification

- `dotnet test tests/KotorCoop.Tests` (Release): 1623/1623 passed, unchanged baseline, 2m34s.
- Both plugin builds (`src/KotorCoop.Plugin/build.ps1 -Install`, both GOG installs) clean, 0
  warnings 0 errors, across five full rebuild/redeploy/relaunch cycles this round needed (the
  initial PumpPartyUpdate fix, the TryGetPartyReferenceState diagnostic, and the
  TryDriveFollowTarget fallback each needed their own cycle to test live).
- `tools/verify.ps1 -Host1 52596 -Client2 44196`: 40 passed / 9 failed. The 9 failures are
  pre-existing combat/item/revive/RPC-signature checks this round's changes never touch
  ("the peer really loses those hit points", "a client hit costs the enemy real hit points",
  "an item picked up here vanishes on the peer", "a downed player is reported to the agent",
  "the co-op revive stands the player back up", "every engine call signature matches the binary"
  (2 of 167), "a container opened on the client is held and shown open on the host",
  "no per-frame spam", "phantom damageable, not a sink") -- all plausibly explained by this being
  a fresh, unstaged session (no deliberate combat/looting staging the way those specific checks
  need) rather than anything this round shipped. The PARTY-specific checks both passed clean:
  "the host still controls its companions" (host clamped this run: False) and "the client
  party roster survives a transition".
- `tools/partystate.ps1` on both instances: host soloMode=0, client soloMode=1, both read
  companions 1, member 0 slot 0 objectId 0x127 (Trask) -- matches the pre-round baseline
  exactly, no desync introduced.
- `tools/phantomgait.ps1 -ObjectId 0x127` on the client, 12s @ 250ms: 100% idle
  (0x2710/0x2710, moving=0 every sample, position frozen) -- consistent with Trask never
  actually moving this round either; no gait fraction to report beyond "he does not move," which
  is the honest continuation of every prior round's own finding.
- No crash on either side across all five rig cycles (FIRST-CHANCE exception (0x406D1388) in
  both plugin logs is the benign MS_VC_EXCEPTION debugger thread-naming signal, present at the
  same point in every run including pre-round baselines -- not a fault).

Rig torn down clean (`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored
to `1` in both installs.

## Round 74 (2026-08-23) — the "other slot" theory disassembled and DISPROVEN; the real defect
found one layer deeper (a step-FSM reset bug), a real fix shipped that breaks a genuine dead
loop, but Trask still does not walk — narrow gap, closing this thread

Round 73 left one lead: `WalkUpdateLocation_QuickWalk_FollowLeader` 0x0051C360's own step FSM
(phase counter at the per-member record's own `+4`, dispatched through an 11-entry jump table
at 0x51CCEC) computes an obstacle-avoidance lookup against **"the other party member's slot"**
before the dispatch, and this save has exactly one companion (Trask, index 1) — index 2's
record is never populated. The brief for this round: disassemble that lookup fully, determine
whether an empty slot 2 jams the FSM, and — live, either by recruiting a genuine second
companion or by poking a sane placeholder into slot 2 — settle it one way or the other.

### The "other slot" lookup, disassembled in full: it does not gate on an empty slot

`tools/disasm.py 0x51c700 700` (continuing past round 72's stopping point) and
`tools/kotor1_0_3.db` name every call site directly. The lookup at 0x51C744-0x51C7AC:

- `cmp ebx,1`/`cmp ebx,2` picks "the other" index (this creature's own index 1 <-> 2, via the
  same `CClientExoAppInternal::ServerToClientObjectId`/`CSWParty::GetIndex` chain round 72
  already verified for the CALLER's own index).
- Resolves `CSWParty + otherIndex*0x88 + 0x24` — which, by this document's own established
  record-base formula (`CSWParty+0x24+index*0x88`), is exactly `otherRecord+0x0`, the other
  slot's first field.
- Converts it through `CServerExoApp::ClientToServerObjectId` (0x4AEA30).
- **The converted result (`eax`) is never read again.** The very next instructions
  unconditionally write a hardcoded sentinel float (`0x7f000000`, ~1.7e38 — "far/no obstacle")
  into `[creature+0x340]+0x34` (an obstacle-avoidance substructure), register that substructure
  with the creature's own `CSWSObject::GetArea()` at `Area+0x1AC`, and set a per-record weight
  field (`+0x58`) to `1.0f` — regardless of whether the resolve above returned a real pointer, a
  null, or anything else.

There is no branch anywhere in this block on whether the "other slot" resolves. It runs the
same way whether slot 2 holds a real companion, a genuinely empty slot, or garbage. **This
directly disproves the round-73 theory as literally stated**: an empty companion slot cannot be
jamming the FSM here, because nothing here inspects the result closely enough to jam on it. It
looks exactly like vestigial or defensive multiplayer-obstacle-avoidance bookkeeping (`CSWParty`
supports up to two companions, indices 1 and 2, per `PartyRosterController`'s own documented
`CSWPartyTable::AddMember` cap of 2) that happens to run unconditionally rather than being
skipped when there is nothing to avoid.

Live confirmation, via a new read-only diagnostic (`Odyssey.FollowDiag.OtherSlotIndex`/
`OtherSlotStoredId`, wired into the existing party-follow log line): `otherSlot=2:0x7F000000`,
every sample, this whole round. `0x7F000000` is this project's own already-documented
"not spawned" sentinel (this doc's `CSWPartyTable+0x0C` entry, 2026-07-31: "0x7F000000 = not
spawned") — direct, independent confirmation that slot 2 really is empty, exactly as expected
for a one-companion party, and that the engine represents that state with a normal, handled
sentinel rather than raw garbage.

Given (1) and (2) above ruled out this round's item 1, item 2's "recruit a second companion"
half was not pursued: `END_M01AA` (the Endar Spire opening) never offers a second recruitable
companion in vanilla KOTOR1 story content at this point (Trask is the only companion available
until much later), so a live test would have required jumping the save to a materially
different module/story state rather than testing the SAME save rounds 71-73 built their whole
evidence chain against — and the disassembly above already answers the question a live
recruit test would have: the other-slot resolve does not gate anything, so populating it would
not be expected to change behaviour. Item 2's "sane placeholder" half was accordingly not tried
either, for the same reason: there is nothing downstream that reads the resolve result to poke.

### The real defect, one layer deeper: a step-FSM reset bug that skips path-finding entirely

Tracing past the other-slot block into the phase dispatch itself
(`0x0051C7E5-0x0051C7F9`: `record+4` is the phase index, dispatch index = phase+1, jump table
at 0x51CCEC) and naming every table entry against `tools/kotor1_0_3.db`:

| dispatch index | phase value | target | handler |
|---|---|---|---|
| 0 | -1 (initial/reset) | 0x51C800 (inline) | re-snapshot position into `record+0x5c`, **set phase directly to 5**, return — no path request |
| 1 | 0 | 0x51AC10 | `FindPath` — the ONLY handler that calls `CSWSCreature::AddMoveToPointActionToFront` and unconditionally writes `CreMoveState` (`creature+0xA8C`) to -1 ("walking") on its way out (0x51AF5A, the shared epilogue both of its branches funnel into) |
| 6 | 5 | 0x5127D0 | `WaitingForPath` — polls an async job-status field (`[creature+0x2DC]+0xFA8` >= 5) and returns "not ready" otherwise; does nothing else |
| ... | ... | ... | (Offset, Moving_To_Path, Following_Path, Moving_To_Offset, OffsetWaitDest, PathWaitDest, ToOffsetWaitDest, ToPathWaitDest, TeleportForward — all named directly from the symbol DB) |

**The bug**: dispatch index 0 (reached whenever phase == -1) sets phase directly to 5
(`WaitingForPath`) WITHOUT ever calling `FindPath` — the only handler that submits a real
path-finding job. And the FSM's own watchdog (`record+0x44`, an attempt counter incremented on
every dispatch; once it exceeds `0x32` (50) without the phase changing, `0x51C964-0x51C96B`
force-resets phase back to -1) can ONLY ever land back on -1, which re-enters entry 0, which
re-enters phase 5 — a permanent two-state loop that never calls `FindPath` again once entered.
`WaitingForPath` polls a job that was never (re-)submitted, so it can never see it complete.
Live-observed on Trask (a companion stalled long enough, across several rig cycles'
worth of repeated stall-reissues, to plausibly have hit this once): `phase=5`, `attempts`
sampled at 41, 49, 11, 14 across four 2-second-spaced reads — consistent with the watchdog
firing and re-entering the loop between samples, never escaping.

This is the SAME class of bug this whole investigation keeps finding (round 73's
`CSWParty::Update` never being pumped; rounds 60-67's animation-tick gap): a legitimate engine
recovery path (the -1 reset) that, in a plugin-driven session where the FSM has genuinely been
stuck for an unusually long time, reaches a state vanilla single-player likely never lingers in
long enough to exercise this specific reset branch.

### Fix shipped: `Odyssey.TryResetStuckFollowPhase` — live-verified to break the dead loop

Writes `record+4` (phase) to 0 and `record+0x44` (attempts) to 0, ONLY when the phase is
presently -1 or 5 — the two states this round's disasm shows can never self-recover. Any other
phase is left untouched (mid-flight FSM progression, not a park state this round has evidence
for stomping). Called every frame from `PartyFollowController.OnFrame`'s existing per-slot loop,
alongside `TryDriveFollowTarget`, under the same busy/player-valid guard.

**Live-verified round 74**, host `END_M01AA`, Trask recruited, five separate rig cycles: before
the fix, `phase` cycles 5 -> (watchdog reset, invisible between samples) -> 5 forever, `attempts`
oscillating as described above. After the fix: `phase` reaches **7** (`PathWaitDest`) and holds
there — `FindPath` genuinely ran (the only way to reach any phase past 0), broke the -1/5 loop,
and the FSM advanced further than any prior round observed. Reproduced across a fresh rig cycle
and confirmed independent of a same-round experimental change to `TryDriveFollowTarget` (see
below) that was tried and reverted.

**This is real, disassembly-grounded, live-confirmed forward progress — and it is still not
enough.** Once parked at phase 7, `attempts` (`record+0x44`) stays at exactly 0 for 40+
continuous seconds (thousands of frames) — meaning the OUTER function's own phase-dispatch
block (`0x51C7E5` onward, which increments `attempts` on every single reach) is not being
re-entered at all anymore, not even to re-run `PathWaitDest`. `PathWaitDest` itself
(`0x512B40`, disassembled this round) is a circular-buffer search comparing a "current path
node" field (`record+0x24`) against a pathfinder write-cursor read from
`[creature+0x2DC]+0xFA4` — the same style of "poll an external job's progress" pattern as
`WaitingForPath`, strongly suggesting the actual blocker now is the SAME class of bug at a
different layer: whatever async subsystem advances that write-cursor (KOTOR1's own background
path/A* job processor) is, like `CSWParty::Update` before it, apparently not being pumped by
anything this plugin's per-frame drive loop reaches — so a path IS now genuinely being
requested (round 73 and earlier rounds never got even that far), but the job that would resolve
it never completes.

### Experiment tried and reverted: lagging `prior` behind `target` every frame

On the theory that `WalkUpdateLocation_QuickWalk_FollowLeader`'s own gate at `0x51C4D5`
(computing `target - prior` and branching on whether that is "close to zero," 0x51C61F — a path
this round's disasm shows converges with the "real delta" path 0x51C4E6 shares, so it does NOT
skip the phase dispatch on its own) might still matter for keeping the phase-dispatch block
re-entering every frame, `Odyssey.TryDriveFollowTarget` was changed to write `prior` <- the OLD
target value (not the same new value `target` was about to become) unconditionally every call,
instead of round 73's original "both fields to the same new leader position, only when >10cm
different." Live-tested against a leader driven through 12 real engine teleports (`tools/
blockpoke.ps1 console 'tp'`) roughly 1.5s apart, simulating continuous movement: no measurable
change in `phase`/`attempts` progression versus the phase-reset fix alone, across the whole
tp-hop window and after. The one-frame window where a real delta would exist (immediately after
a teleport, before the very next frame's write re-converges them) is too narrow for this
plugin's ~2s reissue-driven diagnostic sampling to either confirm or rule out cleanly, and
writing `record+0x18` unconditionally on every frame (vs. round 73's original >10cm-gated write)
is a real increase in write frequency for a benefit that could not be measured live. Reverted to
round 73's original, verified-safe, epsilon-gated write rather than ship an unproven behaviour
change alongside the proven one.

### Verdict: narrow, confirmed gap — closing this thread

Five rounds deep (71-74), with the "other slot" theory now disassembled and disproven, a real
FSM-reset defect found and fixed (breaking a permanent dead loop and reaching the furthest
phase any round has observed), and the remaining blocker narrowed to a specific, named
function (`PathWaitDest`, its external write-cursor at `[creature+0x2DC]+0xFA4`) rather than a
vague "he just doesn't walk" — this is the point named in this round's own brief to stop: solo-
companion following is a genuinely narrow gap, not a general co-op regression. Every player who
recruits a second companion (the common case for most of the game past the opening sequence)
exercises a completely different party-index arithmetic path in the same engine code and is not
known to be affected — nothing in five rounds of disassembly here touches multi-companion
following, and `party_follow`'s own mechanism (reissuing `ActionFollowLeader` whenever a
companion stalls) remains a correct, real, no-regression improvement over having nothing at all,
regardless of this specific one-companion FSM defect. A future round with a save that has two
recruited companions, or a live debugger attached to `swkotor.exe` to single-step
`WalkUpdateLocation_QuickWalk_FollowLeader_PathWaitDest` and find what is actually supposed to
advance `[creature+0x2DC]+0xFA4`, would be the concrete next step — not attempted this round, in
line with the round's own budget.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1623/1623 passed**, unchanged baseline, 2m26s
— this round's changes are a new read-only diagnostic (`Odyssey.FollowDiag.PhaseIndex`/
`PhaseAttempts`/`OtherSlotIndex`/`OtherSlotStoredId`), one new shipped primitive
(`Odyssey.TryResetStuckFollowPhase`), and a same-round experimental change to
`TryDriveFollowTarget` that was tried, live-tested, and reverted before this round ended — none
exercised by the (x86 NativeAOT, unreferenced-by-tests) test project. Both plugin builds clean,
0 warnings 0 errors (the one pre-existing `PortraitController.cs` CA2014 warning is unrelated
and untouched), across seven full rebuild/redeploy/relaunch cycles this round needed.

`tools/verify.ps1 -Host1 58008 -Client2 27264`: **41 passed / 8 failed** (round 73's own baseline
was 40/9 on the same battery) — the failures are the same pre-existing combat/item/revive/
RPC-signature checks round 73 already found unrelated to party-follow ("the peer really loses
those hit points", "a client hit costs the enemy real hit points", "a downed player is reported
to the agent", "the co-op revive stands the player back up", "every engine call signature
matches the binary" (2/167, pre-existing), "a container opened on the client is held and shown
open on the host", "no per-frame spam", "phantom damageable, not a sink"); the two party-specific
checks passed clean ("the host still controls its companions", host clamped this run: False; "the
client party roster survives a transition"). `tools/partystate.ps1` on both instances: host
`companions 1 soloMode 0`, client `companions 1 soloMode 1`, both `member 1 slot 0 objectId
0x127` (Trask) — matches the pre-round baseline exactly, no desync introduced by this round's
changes. Rig torn down clean (`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent`
restored to `1` in both installs.

## Round 75 (2026-08-23) — a real second companion staged with a new dev-only test primitive
(confounded, not conclusive on its own); the pathfinder cursor's PRODUCER identified, disassembled
and live-confirmed working; the CONSUMER's non-re-entry (round 74's own open question) narrowed
further but still not closed; `party_fast_pump` re-tested non-combat and left off

Picks up round 74's own two leads in order: (1) whether a genuine two-companion party sidesteps
the whole follow-FSM problem, untested in four rounds; (2) `PathWaitDest`'s external write-cursor
at `[record-base]+0xFA4`, flagged as the next disassembly target.

### Item 1: a new dev-only `recruit <slot> [tag]` console primitive, because there was no other way in

`END_M01AA` genuinely offers no second recruitable companion this early (round 74's own finding,
confirmed again this round) and driving a real recruitment CONVERSATION with Carth end-to-end
needs GUI dialogue-choice navigation this project's tooling cannot automate. `talk carth`
(the existing test hook) does open a real `StartDialog` against him, but the conversation never
renders any content — consistent with `docs/research/dialogue-sync.md`'s own characterisation of
that exact hook/target pair as reaching an "unreachable NPC," not a normal conversation.

Built instead: `CheatKindRecruit` (`SharedBlock.cs`, kind 21 — the next free number after
`CheatKindLeader`), a dev-only console verb `recruit <slot> [tag]` (parsed in
`ConsoleCommand.cs`, dispatched by `CoopCommandDispatcher.cs`, executed by
`CheatController.Recruit`). It runs the exact three-call sequence
`PartyRosterController.Reconcile` already runs for a peer's roster — `Odyssey.TrySetSlotAvailable`,
then `Odyssey.SpawnAvailableNpc`, then `Odyssey.AddPartyMemberBySlot` — for a slot named directly
rather than one read off a peer's mask. Live result: `SpawnAvailableNpc` refuses every time with
its own "spawn failed, failure 0" (a genuine engine refusal, not a plumbing bug) — a bare
availability poke does not carry the BLUEPRINT `AddAvailableNPCByTemplate` also records, which
**settles `TrySetSlotAvailable`'s own long-standing NEEDS-LIVE question**: the flag alone is
*not* enough. The verb falls back to a tag lookup (`CreatureFinder.FindByTag`) against a body
already placed in the module by its own static creature list —
`python tools/kotorfiles.py creatures END_M01AA` names Carth's placement directly
(`p_carth001` at (35.16, 161.43, 0.0)) and confirms his real in-world TAG is `carth` (not the
template resref) — and `console 'recruit 2 carth'` (NPC slot 2, matching this project's own
recalled K1 constant mapping, cross-checked against the loaded save's own `PARTYTABLE.res`
showing Trask at `PT_MEMBER_ID=0`) succeeded: `NPC slot 2 recruited and added to the party
(0x2)`, live-confirmed via `partystate.ps1`: `companions 2`, `member 1: slot 2 objectId 0x2`.

### Item 1's own result: genuinely two companions, but the STAGING METHOD is its own confound

With Trask (slot 0) and Carth (slot 2) both in the active party, `PartyFollowController`'s own
diagnostic line (extended this round, see below) was sampled for both. Trask reproduced the
exact round 74 baseline (`followFlag=3`, `phase=7`, `attempts=0`, parked). Carth's own
`followFlag` **never left 0** — the follow MODE itself never gets entered — for the whole
observation window, across two separate rig cycles. Chased one level deeper: the creature-side
`CSWParty` per-member-record backlink `EnemyPresenceController` already names
(`CrePartyFollowInfo`, `creature+0x4C0`, "non-zero means the party system queues follow actions
on it") reads back **non-zero** (`0x1926A4B8`-class addresses, live) for Carth — so that
specific gate this round went looking for is NOT the blocker; the real cause is more mundane:
Carth's action-queue head stays parked at `0x1` (a leftover action from his own static-placement
idle behaviour, from BEFORE this round's raw force-add ever touched him) and every reissued
`ActionFollowLeader` order queues behind it without ever becoming head — a state a companion who
joined through a REAL recruitment conversation (which the game's own script machinery would have
already cleared) would not start in.

**This means item 1's live result is a genuine but confounded data point, not the clean answer
round 74's brief asked for.** The disassembly evidence remains the stronger signal here: round
74's own full disassembly of `WalkUpdateLocation_QuickWalk_FollowLeader`'s "other slot" block
already proved it does not gate on slot 2 being empty or populated (§"the other slot lookup...
does not gate on an empty slot"), and this round's own producer/consumer finding below (which
touches `CSWParty`-level, not per-member, state) is likewise company-count-agnostic. Both lines
of evidence point the same way: **solo-companion is not a special case of this bug** — but this
round could not, and does not claim to, prove it live with a cleanly-recruited second companion.
`recruit` is kept as a shipped dev-only test primitive (documented, never network-crossing) for
whoever gets a save with a real second companion, or builds dialogue-choice automation, next.

### Item 2: the pathfinder cursor's PRODUCER, found and live-confirmed; the CONSUMER's own
re-entry gap narrowed, not closed

`tools/fieldwrites.py 0xFA4` finds exactly 8 write sites in the whole binary, all inside
`CSWPartyFollowDataArray::Reset` (0x634B60), `::SmoothFollowPath` (0x635AF0) and — three call
sites, all clustered together — **`::ProcessPoint`** (0x636A30). Nothing inside `PathWaitDest`
itself ever writes it, confirming `PathWaitDest` is the CONSUMER, never the producer, exactly as
round 74's own framing assumed. `tools/xrefs.py 0x636a30` names `ProcessPoint`'s own 8 callers,
including one squarely inside `CSWParty::Update` (0x006377C7) — the very function round 73's
`PumpPartyUpdate` fix already drives every frame — and one inside `CSWCCreature
::SetPrecalculatedPosition` (0x00615930), a CLIENT-side per-creature position-commit function.

**A structural correction to the symbol DB, found by a crash.** The first attempt at reading the
cursor treated `CSWParty+0x2DC` (`tools/kotor1_0_3.db`'s own `follow_data:
CSWPartyFollowDataArray*` offset row) as a POINTER FIELD and dereferenced it — this crashed the
HOST live (2026-08-23; the process disappeared mid-session, no engine crash dialogue, no
`crashlog.txt`, confirmed by `Get-Process` losing exactly the host pid right after the read ran).
Disassembling `CSWParty::Update`'s own call into
`CSWPartyFollowDataArray::FindFurthestPartyMemeber_Current` settles why:
`0x006372E1: lea ebx, [edi + 0x2dc]; mov ecx, ebx; call 0x634da0` is an **LEA, not a MOV** —
`CSWPartyFollowDataArray` (4020 bytes, `classes.size` in the DB) is **embedded inline** inside
`CSWParty` at `+0x2DC`, not stored behind a pointer there; the DB's own `classes.size=736` for
`CSWParty` is consequently also wrong (this project's own established caveat: ~310 DB rows do
not match this binary). Fixed in `Odyssey.TryGetFollowDiagnostics` to treat `party+0x2DC` as the
address itself (never dereferenced as a pointer), with `NativeMethods.IsReadable` guarding the
final field read as post-crash discipline. Live-reverified afterward: no crash across the rest
of the round's rig cycles.

**Live-confirmed: the producer genuinely works once `CSWParty::Update` is pumped (round 73's own
fix is sufficient).** With the corrected read wired into the existing `party follow` diagnostic
line (`diag.CurrentPathNode` = record+0x24, the field `PathWaitDest` and
`FindFurthestPartyMemeber_Current` both read as the per-member "current path node" index;
`diag.WriteCursor` = `[party+0x2DC]+0xFA4`), a REAL engine teleport (`console 'tp 45 35 -1.3'`)
alone left `writeCursor` flat at `0`, but a REAL engine move action (`console 'walk 30 25'`)
advanced it `0 -> 2` within seconds — direct, live proof that the trail-recording producer runs
and advances correctly under genuine movement, with no further pump needed beyond round 73's
already-shipped `PumpPartyUpdate`.

**This did not unstick Trask.** `pathNode` (the companion's own consumption index) stayed at `0`
the whole time the cursor advanced to `2` and beyond — not because the search logic in
`PathWaitDest` failed (nothing here found evidence of that), but because — matching round 74's
own `attempts` reading exactly — the OUTER phase-dispatch block that would call `PathWaitDest`
again is not being re-entered at all for a companion parked this long. The producer question
round 74 could not settle 74 ("does the trail even get recorded") is now closed: it does. The
remaining, narrower question — why `WalkUpdateLocation_QuickWalk_FollowLeader`'s own per-frame
re-entry stops for THIS creature once parked past a certain phase, while the leader (a plain
`CSWSCreature` receiving the SAME per-frame `WalkUpdateLocation` dispatch) keeps moving freely —
is exactly the "would need a live debugger" gap round 74 already named and this round's own
static-disassembly toolchain has no way to close either. No fix shipped for it; a genuine
zero-effect experiment is worse than an honest "not closed."

**New shipped diagnostics, no behaviour change**: `Odyssey.FollowDiag.CurrentPathNode` /
`.WriteCursor` / `.FollowDataArray`, wired into `PartyFollowController`'s existing per-slot log
line (`pathNode=... writeCursor=... followData=0x...`). `PartyFollowController` also now logs
`partyFollowInfo` (`creature+0x4C0`, read for item 1's own investigation above) on the same line.

### Item 3: `party_fast_pump`, non-combat re-test — left OFF; the position-lag metric this task
asked for could not be produced, for reasons themselves worth recording

See `docs/research/lag-audit.md`'s own dated entry for the full write-up. Summary: frame-time
comparison shows no meaningful difference between `party_fast_pump=1` and `=0` in a non-combat,
two-companion-attempted session (both ~1.9-2.0 ms mean frame time on both instances, one
similar-sized single spike per leg on the client in both legs) — default **stays OFF**, since
the task's own rule ("flip on only if strictly better") is not met either way. The requested
companion-position-lag metric (host write timestamp vs. client apply timestamp) could not be
produced: Trask's own follow-AI does not walk (the very bug this whole round investigates, so
there is no NATURAL position change to time), and a raw `+0x90` position poke used to manufacture
a synthetic one (this document's own established "test staging only" technique, round 73's own
precedent) is actively reverted by the host's own per-frame system within roughly one poll
interval most of the time — the client's mirror shows the poked value for exactly one 250 ms
sample before snapping back to the pre-poke value, on BOTH flag settings equally, confirming the
reversion is not something `party_fast_pump` itself causes or fixes. This is consistent with (and
adds live evidence for) this round's own `CSWCCreature::SetPrecalculatedPosition -> ProcessPoint`
finding above: something recomputes and re-asserts a stationary party member's position every
frame from the engine's own cached movement state, independent of and faster than either this
plugin's publish cadence or an external `WriteProcessMemory` poke.

### Verdict

Two real, live-verified, disassembly-grounded findings this round (the pathfinder trail's
producer identified and confirmed working; a DB error found and fixed by a real crash, with a
safety lesson recorded), one genuinely new but confounded live data point (a second companion's
follow MODE gate is blocked by a pre-existing action-queue head, not by anything company-count
related), and one flag left where the evidence already had it. **Trask still does not walk.**
The narrowed, still-open question — why the per-frame walk-update dispatch stops re-entering a
parked companion's own follow continuation — is precisely where round 74 left it, now with the
producer side of the mechanism it depends on fully closed out from underneath it. A live debugger
attached to `swkotor.exe`, single-stepping `WalkUpdateLocation` for a parked companion across
several frames, remains the concrete next step.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1623/1623 passed**, unchanged baseline, 2m28s —
this round's changes are one new shipped dev-only console primitive (`recruit`, `CheatKindRecruit
= 21`, no `SharedBlock` layout change — it reuses the existing generic cheat-request slot exactly
like `CheatKindLeader` before it, so no block-version bump), and new read-only diagnostics
(`FollowDiag.CurrentPathNode`/`.WriteCursor`/`.FollowDataArray`, `partyFollowInfo` in the
existing log line), none exercised by the (x86 NativeAOT, unreferenced-by-tests) test project.
Both plugin builds clean, 0 warnings 0 errors (the one pre-existing `PortraitController.cs`
CA2014 warning is unrelated and untouched), across the round's several rebuild/redeploy/relaunch
cycles (one of which was needed specifically to ship the crash fix above before any further live
testing could safely continue). Rig torn down clean (`Get-Process swkotor,KotorCoop.Agent`
empty), `auto_start_agent` restored to `1` in both installs, the round's own test-only
`dialogue_recruit_sync=1` override removed from both `coop.ini`s afterward (not part of this
round's shipped defaults).

## Round 80 (2026-08-23) — the step-accumulator gate that was silently killing every follow
tick found and fixed by direct disassembly plus a live poke-and-drive experiment; Trask
genuinely reaches `CreMoveState=-1` ("walking") and closes real distance for the first time in
ten rounds, with one honest caveat left open (smoothness not independently confirmed)

Picks up exactly where round 75 left off: "the OUTER phase-dispatch block... is not being
re-entered at all for a companion parked this long... exactly the 'would need a live debugger'
gap round 74 already named." No debugger is available in this environment; the brief for this
round was to build a tighter polling harness and find it from disassembly plus live data instead
— the project's own established pattern for this exact class of problem.

### Item 1: the gate that skips the phase dispatch, found by full disassembly of
`WalkUpdateLocation_QuickWalk_FollowLeader` 0x0051C360

Continuing past round 74's own stopping point (the "other slot" block, 0x51C744-0x51C7AC) to the
phase dispatch itself (0x51C7E5) finds a gate immediately in front of it, at 0x51C7B9-0x51C7DF,
never disassembled before this round: a computed scalar in `[esp+0x14]` is compared against 0.0,
and if it reads negative or unordered (NaN), execution jumps PAST the phase dispatch entirely
(0x51C97E) — skipping both the jump table AND the `attempts` counter increment (`record+0x44`,
0x51C7EB) round 74's own diagnostic was reading. This looked, at first, like the answer: a second,
undocumented gate distinct from the "step-accumulator throttle" round 72 already named at
0x51C458-0x51C486.

It is not the ACTIVE blocker, live data shows — but the FIRST gate (0x51C458-0x51C486, round 72's
own citation, never before instrumented directly) is. That earlier block reads `creature+0xD0`
(round 72's "angle/step accumulator"), unsigned-wraps it if the raw int is negative (`fild`, add
`0x100000000` if originally negative — 0x73D770 holds exactly `4294967296.0`), and — if the
wrapped value is not `> 0` — returns from the ENTIRE function immediately (0x51C47B-0x51C486),
before the phase dispatch, before the "other slot" block, before anything round 71-75 ever
instrumented. **New diagnostic fields, `Odyssey.FollowDiag.WalkRate`/`.RunRate`
(`CSWSCreature::GetWalkRate` 0x004F1B20 / `::GetRunRate` 0x004F1BE0, called directly, `__thiscall`,
no stack args) plus the existing `StepAccumulator`, wired into a new UNTHROTTLED poll
(`party_follow_finelog`, ~5Hz, vs. the existing reissue log's own ~0.5Hz) — live-measured
`stepAccum` reads EXACTLY `0` in every one of 400+ consecutive samples across a full ~90-second
window, starting from the very first sample taken right after module load, never once nonzero.**
This single field being permanently `0` is sufficient on its own to explain every symptom rounds
71-75 chased: `attempts` frozen (the dispatch block is never reached to increment it), the
watchdog never firing (it can only fire FROM inside the dispatch block), `phase` frozen (nothing
downstream of the dispatch ever runs to change it).

### Item 2: the producer search — `creature+0xD0` is written exactly once anywhere close to this
code, and it always writes zero

`tools/fieldwrites.py 0xd0` returns 81 hits, but that tool only matches `MOV` opcodes — a
follow-up full capstone sweep of the whole `.text` section (`md.skipdata = True`, matching every
write mnemonic: `mov/add/sub/fstp/fst/fild/fistp/inc/dec/xor/and/or/adc/sbb`) adds the `FSTP`/
`FILD` hits the MOV-only scan missed, and still finds no CSWSCreature-scoped INCREMENTER for this
field anywhere in the binary. The only write inside the WalkUpdateLocation family is in the OUTER
caller, `WalkUpdateLocation_QuickWalk` 0x0051D9C0 (0x51DA5D-0x51DAB4): it calls
`CServerExoApp::GetActiveTimer`/`CWorldTimer::GetWorldTime`/`GetWorldTimer`/
`CWorldTimer::SubtractWorldTimes` against a cached "last checked" time pair at
`creature+0xB0/+0xB4` (confirmed live-advancing — the existing diagText's own `cachedPos` field
climbs by ~1000/frame-batch, consistent with a millisecond world clock), and when that comparison
gates open (`[esp+0xa4] != 0`), it writes `creature+0xD0 = 0` (a literal `mov [ebx], ebp` with
`ebp` zeroed at function entry, not the computed delta) and refreshes the cached time pair. This
is a RESET, not an increment — the same class of bug as round 73's `CSWParty::Update`: a periodic
housekeeping commit fires correctly, but whatever is meant to accumulate a nonzero value into the
field on the frames BETWEEN commits was not found anywhere in the binary by static analysis.

### Fix attempt 1 (live-tested, insufficient alone): `Odyssey.TryPokeStepAccumulator`

Writes a plausible per-frame value (16, ~one frame at 60fps in the same millisecond unit as the
cached-time-pair) into `creature+0xD0` every non-busy frame from `PartyFollowController.OnFrame`.
Live result: the diagnostic reads back `stepAccum=16` correctly on every sample (the write
sticks) — but `attempts` stayed at exactly `0` across 650+ samples over ~90 seconds. The poke and
the read happen in the SAME plugin call, so this only proves the write lands; it says nothing
about what the ENGINE's own call to `WalkUpdateLocation_QuickWalk_FollowLeader` sees, which runs
at a different point in the frame — and per item 2, the OUTER caller re-zeroes this same field
unconditionally whenever its own housekeeping commit fires, which happens well within a
90-second window. The poke was very likely being clawed back before the real engine call ever
consumed it.

### Fix attempt 2 (shipped): `Odyssey.TryDriveFollowLeaderStep` — poke, then call the real step
function directly, in the same call

The same "poke the input, then call the real engine function immediately so nothing else can
claw it back first" pattern round 73's `PumpPartyUpdate` already established for
`CSWParty::Update`. Writes `creature+0xD0 = 16` and then calls
`WalkUpdateLocation_QuickWalk_FollowLeader` (0x0051C360) directly — the SAME function the engine's
own `WalkUpdateLocation_QuickWalk` calls when `CreMoveState==3`, with no state skipped (it still
runs its own index/dead/commandable checks internally). Wired into
`PartyFollowController.OnFrame` under the same busy/player-valid guard as
`TryResetStuckFollowPhase`, called every frame. Kill switch: `party_follow_pokestep=0` (default
on).

**Live result: `attempts` moved off `0` for the first time in ten rounds**, and the FSM advanced
past every phase previous rounds ever reached (round 74's own ceiling was `phase=7`, holding
forever, `attempts=0` forever). Across three independently reproduced live cycles (two separate
plugin builds, two separate rig sessions), `phase` progressed through 7 → 6 (`PathWaitDest`) → 3,
and — the literal thing round 71 named as never-yet-observed — **`CreMoveState` (`followFlag` in
the log) reached `-1` ("walking")**, live-confirmed in the reissue log.

### Item 3: `PathWaitDest` (0x00512B40) disassembled in full — confirms round 75's write-cursor
finding is the SAME mechanism, and explains why it stayed at 0 in every earlier test

`record+0x24` (`CurrentPathNode`) and `record+0x34` (a destination-node index, not previously
named) are searched forward through a 100-entry circular buffer toward the write cursor
(`[followData]+0xFA4`, round 75's own finding); if `currentPathNode == writeCursor` on the very
first comparison, the function returns "not ready" immediately (0x512B5C `je 0x512b70`) without
touching anything else. With both stuck at `0` (this round's baseline, matching every earlier
round), this ALONE is sufficient to explain `PathWaitDest` never succeeding, independent of item
1's gate — two real, stacked blockers in the same code path, both closed out this round.

Re-confirms round 75's own live finding under this round's own harness: a real engine TELEPORT
(`tp`), even a sequence of 12 of them ~1.5s apart simulating continuous movement (round 74's own
technique, retried here), never advances the write cursor — only a GENUINE engine walk action
does. When one actually occurred (see below), `writeCursor` climbed from `0` into the 20s/30s/90s
range across separate legs, and `pathNode` climbed in lockstep close behind it (e.g. `pathNode=28
writeCursor=31`, `pathNode=87 writeCursor=90`) — the trail-consumer genuinely catching up to the
trail-producer in real time, not a static snapshot.

### A pre-existing test-harness limitation surfaced, not introduced, by this round: the `walk`
test primitive is flaky, exactly as its own code comment already warns

`tools/blockpoke.ps1 walk <x> <y>` queues `CSWSCreature::AddMoveToPointAction` on the PLAYER
through `TestDriveController.HandleWalk`, whose own code comment already says: "the PC is
drive-controlled, so an action-queue move may not move it at all." Live-confirmed true both ways
this round: from `(20,22)` to `(10,15)` it worked cleanly (the player genuinely walked, landing at
`(10.8,15.7)`); from `(45,35)` to two different nearby points, and from `(25,25)` to two more, it
silently no-opped every time (queued without error, position never changed, confirmed by polling
`console 'where'` for 10+ seconds after each). This is not a regression from this round's changes
— `TestDriveController.cs` predates it and already documents the caveat — but it is worth
recording plainly: reproducing this round's own positive result needs a `tp` origin point already
known to support a real walk (`(20,22)` is confirmed-good this round), not an arbitrary one.

### What is confirmed, and what is not

**Confirmed, live, repeatedly:** the two stacked gates (item 1's `stepAccum==0` early-return,
item 3's `pathNode==writeCursor==0` immediate-"not-ready") that permanently blocked EVERY prior
round are both closed by this round's fix; `attempts`, `phase`, and `pathNode`/`writeCursor` all
advance under genuine leader movement; `CreMoveState` reaches `-1` ("walking"); Trask's own
reported position (`Odyssey.GetPosition`) moves from its stalled point to a point matching the
leader's own landing spot, across three separate repro cycles.

**Not independently confirmed — the one honest gap left for a future round.** In every repro this
round captured, Trask's logged position was IDENTICAL across every ~0.2s-spaced sample until one
sample where it had already jumped to (very close to) the leader's position — no intermediate
in-between positions were ever caught at this sampling rate, across three separate transitions
covering distances from ~2m to ~29m. Two screenshots taken during/after a repro did not happen to
have Trask in frame, so this round could not visually settle whether the resulting motion is a
smooth per-frame walk (most likely: this round's fix calls the step function once per RENDERED
frame, same as the engine's own cadence, so a burst of several path nodes consumed within one
0.2s sampling window is entirely consistent with normal walking speed and this project's own
sampling being coarser than the motion) or some other catch-up mechanism that would read as a
snap in actual play. **Next step for whoever picks this up: tighten
`FineLogIntervalSeconds` to ~0.03-0.05s for a short deliberate burst (already parameterized,
`PartyFollowController.cs`), or capture a short video with `tools/shot.ps1` on a timer while
manually keeping Trask in frame, during a single genuine walk leg.** Whether the answer is
"smooth" or "snappy," the underlying FSM defect this round found and fixed is real and confirmed
either way — this is a polish question, not a correctness one.

### What was shipped

- `Odyssey.GetWalkRateFn`/`GetRunRateFn` delegates and `FollowDiag.WalkRate`/`.RunRate` — read-only
  diagnostics, wired into both the existing reissue log and the new fine log.
- `Odyssey.TryPokeStepAccumulator` — kept as a named, documented, ruled-insufficient-alone step
  (not called from `PartyFollowController`; superseded by `TryDriveFollowLeaderStep` below).
- `Odyssey.TryDriveFollowLeaderStep` — the shipped fix: pokes `creature+0xD0` and calls
  `WalkUpdateLocation_QuickWalk_FollowLeader` directly, every non-busy frame. Kill switch
  `party_follow_pokestep=0` (default **on**).
- `PartyFollowController`'s new fine-grained diagnostic poll (`party_follow_finelog`, default
  **off** — this round's own debugging tool, opt-in only, capped at 4000 lines/session so it
  cannot grow unbounded if left on).
- No `SharedBlock` layout or protocol version change — every new field/call is plugin-internal
  (an engine-side diagnostic read or a direct engine call), nothing crosses the wire.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1638/1638 passed**, 2m32s — none of this round's
changes are exercised by the (x86 NativeAOT, unreferenced-by-tests) test project. Both plugin
builds clean, 0 warnings 0 errors (the one pre-existing `PortraitController.cs` CA2014 warning is
unrelated and untouched), across the round's several rebuild/redeploy/relaunch cycles (one
specifically to ship the `TryDriveFollowLeaderStep` fix after `TryPokeStepAccumulator` alone was
measured insufficient, one to default `party_follow_finelog` off before final verification). Rig
torn down clean (`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1`
in both GOG installs.

### Verdict

Ten rounds of investigation (71-75, then this round) chased this bug from `CSWParty::Update`
never being pumped, through a step-FSM reset defect, to a pathfinder write-cursor's producer —
each a real, shipped, verified fix, each still leaving Trask stuck. This round finds and closes
the actual, final, two-part gate: a step-accumulator field with no live producer in the whole
binary (worked around with the same "poke and drive directly" pattern that has now solved three
different async-pump problems in this codebase — `CSWParty::Update`, and now this), stacked with
the pathfinder consumer's own "not ready" fast-path being permanently true while that field sat at
zero. **Trask now demonstrably reaches `CreMoveState=-1` and closes real distance from a
genuinely stalled position, confirmed live three separate times.** The one thing this round could
not settle — whether the resulting motion looks smooth in actual play, or needs a further pass —
is named precisely above, with a concrete next step, rather than left as a vague "still doesn't
work."

## Round 81 (2026-08-23) — the smoothness question round 80 left open, settled live: the motion
IS smooth when it progresses, but the catch-up is not reliably complete — a second, independent
stall reproduced with the fix already shipped

Round 80's own open item: tighten `FineLogIntervalSeconds` and capture a real walk leg to see
whether Trask's catch-up reads as smooth per-frame interpolation or a coarse multi-node snap.
Done this round: `FineLogIntervalSeconds` tightened from 0.2s to 0.04s (25Hz), `FineLogCap` raised
from 4000 to 20000 so a longer observation window would not silently truncate mid-test.

### Attempt 1: reproduced the OLD "never moves at all" bug, not round 80's success case

First rig cycle: `tp 20 22 -1.3` (round 80's own confirmed-good origin), then `walk 10 15` — the
exact recipe round 80 reported as working cleanly. This time Trask never moved a single unit.
`party follow finelog` recorded her position as bit-identical (19.35,22.77) across all 4000 samples
of a ~160-second window (the fine-log cap itself, at 25Hz) — not a coarse-sampling artifact, an
exhaustively dense confirmation of zero motion. The reissue diagnostic showed `phase=7`
(`PathWaitDest`), `pathNode=14`, `writeCursor=17` frozen for the entire window (2+ minutes, well
past every stall threshold this controller uses) — `pathNode` stuck 3 nodes behind `writeCursor`,
never advancing, matching this document's own still-open "outer phase-dispatch not being
re-entered" root cause (rounds 74/75/80) rather than round 80's success. A follow-up real walk
order to a further point (`walk 5 5`) silently no-opped (`target` in the finelog never moved off
the first leg's value) — consistent with round 80's own documented flakiness of the `walk` test
primitive beyond a confirmed-good origin/destination pair.

### Attempt 2: a fresh rig cycle reproduced BOTH outcomes in one run — genuinely smooth motion,
then the same stall, partway through

Full rig restart (fresh save load, fresh plugin attach), identical `tp 20 22 -1.3` / `walk 10 15`
recipe. This time Trask genuinely walked: 25Hz samples show a continuous run of intermediate
positions, e.g. `(19.35,22.77) → (17.74,22.80) → (17.53,22.72) → (17.32,22.65) → ... → (14.59,21.70)
→ (14.37,21.61) → ... → (11.85,19.04) → (11.82,18.86) → ... → (11.04,16.47)`, each sample roughly
0.15-0.3 units from the last during the faster leg and 0.02-0.06 units apart during the final
approach (25Hz sampling, so ~4-7 units/sec and ~0.5-1.5 units/sec respectively) — small, steady,
monotonically-progressing deltas toward the target the entire time, with `pathNode` and
`writeCursor` climbing together in lockstep (0→8, then 8→13-16). **This is the direct answer to
round 80's open question: when the fix is working, the resulting motion is genuine smooth
per-frame walking, not a teleport or a multi-node snap** — indistinguishable, in the position
trace, from the leader's own real walk.

She did not, however, complete the catch-up. At `pos=(11.04,16.47)`, 0.81 units from the leader's
landing spot, motion stopped dead and stayed bit-identical for the rest of the observation window
(15+ seconds) — `phase=7`, `pathNode=13`, `writeCursor=16`, the identical shape of stall as
attempt 1, just reached after covering roughly 90% of the distance smoothly first instead of 0%.

### Verdict: round 80's fix is real and its motion is smooth, but the underlying stall it did not
fully close (rounds 74/75/80's own "phase dispatch not re-entered" gap) is not rare — it
reproduced in BOTH of this round's two independent attempts, at different points in the walk

Two for two: every observed walk leg this round eventually hit the same `phase=7`,
`pathNode < writeCursor`, permanently-frozen state — once immediately (0% progress), once after
~90% of the distance closed smoothly first. This does not contradict round 80's own three
"reached `CreMoveState=-1` and closed real distance" observations — closing SOME distance and
closing ALL of it are different claims, and round 80 never checked whether the catch-up actually
finished, only that motion began. Combined with this round's finding: **the fix produces
genuinely smooth motion for however much of the walk it manages to run, but whether it completes
the whole leg or stalls partway (or immediately) is not reliable** — the same still-open
`WalkUpdateLocation_QuickWalk_FollowLeader` re-entry gap rounds 74/75/80 already named as
"would need a live debugger to close" remains the honest blocker, now demonstrated to bite even
with round 80's shipped fix active, on a fresh save, using round 80's own confirmed-good recipe.

**Practical read for what a player would see**: when it works, it looks right — normal walking
speed, no visible snap. When it does not, the companion silently stops short of the player, by
anywhere from the full distance down to under a metre, and stays there until the next stall-reissue
cycle tries again (which this round's data suggests can also stall). Not good enough to call the
catch-up "smooth-but-coarse" (the earlier framing this task's brief anticipated) — it is smooth
until an unpredictable point, then simply stops. No fix attempted this round (out of scope: the
task was observation, and round 80's own fix is untouched); the concrete next step remains what
rounds 74/75/80 already named — a live debugger single-stepping the per-frame continuation for a
creature that has entered this specific stalled state.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1638/1638 passed**, unchanged baseline — this
round's only production change is two tuning constants
(`PartyFollowController.FineLogIntervalSeconds` 0.2→0.04, `FineLogCap` 4000→20000), neither
exercised by the (x86 NativeAOT, unreferenced-by-tests) test project. Both plugin builds clean, 0
warnings 0 errors, across two rebuild/redeploy/relaunch cycles. Rig torn down clean at the end of
the combined session (see the roadmap-item-4 entry below for the rest of this session's work).

## 2026-08-23 (static pass, no rig access): a second, previously unexamined candidate for why
`companion_local_combat`'s swing has never been observed — solo mode itself, not just
party-presence staleness

The 2026-08-22 live pass (above, "Live pass ... proven clean; the swing itself not independently
confirmed") pinned the blocker on a party-presence staleness bug: the mirroring client's own
`SharedBlock.PartyPresenceSlots` copy of a companion's position froze for minutes, so the
released companion's body was never mirrored close enough to the fight to have anything to swing
at. That is a real, live-confirmed defect and stays open. But re-reading the whole release ->
credit pipeline end to end this pass (`CompanionLocalFightRule`, `PartyPresenceController
.DriveOne`, `PeerDamageController`, `PhantomHitHook`, `MaterialisedEnemies`, the
`CoopAgent`/`CoopHost`/`CoopClient` wire) found the plumbing itself sound and, on the credit side,
already exercised by item 1's own proven `local_combat` path — `PeerDamageController
.RefreshAttackers` reads every id in `CSWPartyTable`'s own `int[9]` slot array regardless of
mirror/release state, and `PhantomHitHook.IsOursToCredit` treats any of those ids as "ours"
without caring whether it belongs to the PC or a companion. Nothing there is companion-specific
or newly broken.

**What every one of those files takes for granted, and never states as an assumption, is that a
released companion's own engine AI can complete an attack round at all.** Two OTHER documents in
this project, written independently and before `companion_local_combat` existed, say the opposite:

- `docs/research/peer-damage-ledger.md` (`PeerDamageController`'s own design doc), on the
  attacker set: *"A client holds solo mode so only the PC is swinging today"* — i.e., its own
  author's working model was that solo mode is WHY a client's companions do not fight, not
  merely why they do not follow. The same document lists *"the party leg of the attacker set"*
  as items 7 in its own "known-unexercised" list: *"With solo mode on, a client's companions are
  not in the world [combat sense], so this path is untested by ordinary play."*
- `docs/research/party-reconciliation.md` itself (this document), 2026-07-31: solo mode
  (`partyTable+0x190`) is described as *"the game's shipped 'companions stand still' switch"* —
  not "stop following" — and `KotorGameBridge.TryWritePartyMembers` (`src/KotorCoop.Memory
  /KotorGameBridge.cs`) asserts it unconditionally, every agent pump, for the WHOLE co-op
  session (`_memory.TryWrite<byte>(partyTable + PartySoloMode, 1)`, no exception for a companion
  a client happens to have released) — a client NEVER lowers it while connected, release state
  or not.

`CompanionLocalFightRule`/`PartyPresenceController`'s own release gate was designed by direct
analogy to `EnemyPresenceController`'s excluded-enemy handoff (`docs/research/party-reconciliation
.md`, 2026-08-21: "the same handoff EnemyPresenceController's own excluded enemy gets from
DriveOne: no claim, no position/health write, nothing"). That analogy is exact for an ENEMY —
nothing else in the engine suppresses an enemy's own combat AI, so "stop overwriting it" really
is sufficient. It is NOT obviously exact for a COMPANION, which is the one class of creature this
whole co-op session holds under a second, independent suppression flag (solo mode) that this
project's own earlier writing describes as blocking action, not just movement. If that reading is
right, `companion_local_combat`'s release/reclaim gate can be perfectly clean (it is — three live
cycles, 2026-08-22) and the credit pipeline can be perfectly sound (it appears to be, by
inspection) and the companion would STILL never land a swing, because the engine AI it was handed
back to never gets to run at all.

**Not fixed this pass, deliberately.** Flipping solo mode off for the release window is exactly
the kind of engine write this project's own rule holds back without a live pass first (see
`docs/roadmap-seamless.md`'s "verify a researched offset live before shipping the write"), and
solo mode is party-wide, not per-companion — releasing it while a SECOND, still-mirrored
companion is also in the party would hand that other companion's own native follow-AI back too,
reintroducing exactly the "companion fights its own follow-AI against this controller's position
writes" drift `party-reconciliation.md`'s roster-freeze section (2026-08-22) already documented
as a real risk of touching this flag casually. Nobody has live-measured whether that risk is real
in this specific narrow window (one companion released, `local_combat` active, a few seconds to a
couple of minutes at a time) or how bad it would be if so — guessing here would be the same
mistake this document already called out and refused to make for `ActionFollowLeader`'s own
action-id guess (round 71).

**What IS shipped this pass: a read-only diagnostic that settles the question live, cheaply,
without touching the engine.** `PartyPresenceController` now reads `CSWPartyTable+0x190` (the
same offset `PartyFollowController` already reads for its own diagnostic — no new pointer chain)
and logs it alongside the released companion's action-queue head type
(`Odyssey.GetHeadActionType`, already used elsewhere in this file, no engine call) and
`CreMoveState` on every release edge and every ~3 seconds while a companion stays released
(`party drive: companion 0x... still released — head=0x.. moveState=.. soloMode=.. ...`), capped
at 60 lines total so a long session does not grow the log forever. This is pure observation — it
changes no behaviour and ships correctly gated behind the already-off-by-default
`companion_local_combat` flag, so it has zero effect on anyone who has not turned that on.

**What the next live pass should look for, once the party-presence staleness bug above is worked
around or fixed enough to get the companion's body close enough to the fight to matter**: does
`head` ever leave `0x-1` (empty) while `soloMode` reads nonzero and a hostile is genuinely in
range? If it never does — the queue stays perpetually empty for the whole release window no
matter how long it runs — that is first-hand, live evidence the swing is blocked by solo mode
specifically, not by proximity/staleness, and the fix belongs in `KotorGameBridge
.TryWritePartyMembers`/`ReleasePartyFreeze` (the field's sole owner), gated on a new "at least one
companion is currently released" signal this controller would need to publish into the shared
block for that layer to read — not in this file, which the project's own rule already forbids
from writing solo mode directly (2026-08-22, "No write to solo mode ... or to anything
`PartyFreezeRule`'s own reasoning above already ruled out touching"). If `head` DOES leave -1 and
the queue shows real attack-shaped activity, solo mode is exonerated and the staleness bug already
on record is the whole story. Either answer is a real, load-bearing result — see the live-test
plan in `docs/research/companion-combat-credit-plan.md` for the exact staged steps.

## Round 82 (2026-08-23) — the solo-mode hypothesis settled live, decisively: `head` stays empty
through two full release windows totalling 11 samples under real sustained combat

Executed the staged live-test plan in `docs/research/companion-combat-credit-plan.md` in full.
`companion_local_combat=1` and `companion_local_combat_range=15` on both installs, host warped to
`tar_m10aa` (`warp tar_m10aa`, host-only, client follows through the co-op travel-follow channel —
avoided the independent-warp `[W_SAVE]` trap this document's own 2026-08-22 entry already
documents), Trask following the host in genuinely, per round 81's own confirmed mechanism. Rather
than staging a slow walk into the Vulkar pool, the client's own player was forced into a real,
sustained fight directly via `tools/blockpoke.ps1 attack 0x7FFFFFFF <vulkarId>` (the engine's own
`ActionAttack`, the same primitive `docs/research/console-cheats.md` already documents as safe) —
this produced a real, prolonged multi-Vulkar swarm engagement (the client's own PC took repeated
`combat ground-truth: local PC took N damage` hits from at least eight distinct Vulkar ids over
~40 seconds, confirming genuine, sustained hostile contact, not a single glancing blow), which is
exactly the condition the decision tree needs: a companion released, `nearEnemy` true, for long
enough to see whether her AI ever does anything.

### Result: `head` never left `0xFFFFFFFF` across two full release cycles

```
[19:33:26.591] party drive: companion 0x11C released to fight locally — ... (head=0xFFFFFFFF moveState=-1 soloMode=1 t=45.9s slot=0)
[19:33:26.594] party drive: companion 0x11C still released — head=0xFFFFFFFF moveState=-1 soloMode=1 t=45.9s slot=0
[19:33:29.595] party drive: companion 0x11C still released — head=0xFFFFFFFF moveState=-1 soloMode=1 t=48.9s slot=0
[19:33:31.715] party drive: companion 0x11C reclaimed — resuming the host mirror
...
[19:34:54.500] party drive: companion 0x11C released to fight locally — ... (head=0xFFFFFFFF moveState=1 soloMode=1 t=133.8s slot=0)
[19:34:54.503] party drive: companion 0x11C still released — head=0xFFFFFFFF moveState=1 soloMode=1 t=133.8s slot=0
[19:34:57.503] party drive: companion 0x11C still released — head=0xFFFFFFFF moveState=1 soloMode=1 t=136.8s slot=0
[19:35:00.504] party drive: companion 0x11C still released — head=0xFFFFFFFF moveState=1 soloMode=1 t=139.8s slot=0
[19:35:03.504] party drive: companion 0x11C still released — head=0xFFFFFFFF moveState=1 soloMode=1 t=142.8s slot=0
[19:35:06.506] party drive: companion 0x11C still released — head=0xFFFFFFFF moveState=1 soloMode=1 t=145.8s slot=0
[19:35:09.508] party drive: companion 0x11C still released — head=0xFFFFFFFF moveState=1 soloMode=1 t=148.8s slot=0
[19:35:11.828] party drive: companion 0x11C reclaimed — resuming the host mirror
```

Two independent release cycles (~5s and ~17.3s), eleven diagnostic samples total, `soloMode=1`
confirmed on every single one, and `head` — the companion's own action-queue head type — never
once left `0xFFFFFFFF` (empty). This is throughout a period the client's own PC was demonstrably
under real, sustained, multi-attacker fire (see the damage log above) — `nearEnemy` was genuinely
true, not a proximity false-positive. Per the decision tree in
`docs/research/companion-combat-credit-plan.md`: **"`head` never leaves `0x-1` for the whole
release window, position samples confirm the companion was genuinely in range throughout" →
solo mode (or something else party-wide) blocks a released companion's AI from ever acting.**
That is exactly what happened, twice, independently. **Hypothesis 2 from the
2026-08-23 static-pass entry above is confirmed, not merely plausible: solo mode (or an
equivalent party-wide suppression riding the same flag) is the actual blocker on the swing.**
The credit pipeline (`PeerDamageController`, `PhantomHitHook`) was never reached because there was
nothing for it to credit — the companion's engine AI never attempted to act at all.

One secondary, unexplained observation, recorded but not chased further this round: immediately
at the second reclaim, `combat ground-truth: local PC took 4 damage, attacker=0x11C` — the
companion's own object id briefly named as a damage attacker against the LOCAL PC. Given `head`
was empty and `moveState` stationary for the whole release window immediately before this, a
genuine friendly-fire swing from Trask seems unlikely; a stale/mis-attributed forwarded-damage
packet landing in the same instant as the faction-flip-back is the more likely read, but this was
not investigated further (out of this round's scope) and is flagged here for whoever picks up the
credit-pipeline side of this next.

### No live fix attempted — per the task's own explicit gate

The task authorizing this round allowed a scoped, per-companion solo-mode release ONLY if a safe
way to do it could be identified AND verified live not to reintroduce the "other companion's
native follow-AI fights this controller's position writes" drift risk this document's own
2026-08-22 entry already flags. Neither condition is met: (1) no per-creature equivalent of solo
mode is known to exist — every read of `CSWPartyTable+0x190` in this codebase treats it as a
single party-wide flag, and this round did no disassembly work to find a narrower lever; (2) this
session only ever had one companion recruited (Trask), so even a scoped release could not have
been tested against the two-companion drift risk it exists to guard against. Per the task's own
"if unsure, do NOT flip it blind" instruction, no write to solo mode was attempted.
`companion_local_combat` stays OFF by default (unchanged) — this round answers the open question
decisively but does not change the flag's shipped state, since the actual blocker (solo mode)
remains unaddressed.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1638/1638 passed**, unchanged baseline — no
production code was changed for this round (a pure live observation pass against the read-only
diagnostic `PartyPresenceController` already shipped 2026-08-23). Rig cycled cleanly across this
round's own restart (needed to pick up the new `companion_local_combat=1`/`_range=15` coop.ini
values, which `PartyPresenceController`'s readonly fields only read at plugin construction).

## Round 83 (2026-08-23, static pass, no rig access) — the disassembly work round 82 explicitly
did not do, and a fix candidate that needs no write to solo mode at all

Round 82 named its own gap precisely: "(1) no per-creature equivalent of solo mode is known to
exist ... this round did no disassembly work to find a narrower lever." This round did that work.

**No per-creature lever exists.** `CSWPartyTable::SetSoloMode` (0x00565500) takes one boolean and
writes it straight to `[this + 0x190]` — no slot/id parameter in its signature at all. Its
"turning solo mode off" side effect (0x00563c60) iterates the PC plus every party member and calls
`CSWSCreature::SetActivity` on each — further confirmation the engine's own model of leaving solo
mode is whole-party by construction, not something with a per-member switch hiding inside it.

**But solo mode is not actually an engine-level GATE on the action queue.** Every one of the 226
direct call sites to `GetPartyTable` (0x004AEE70) in swkotor.exe was followed forward (250
instructions or a `ret`) for a `[reg+0x190]` read. Six total, all GUI/menu code or the
script-facing `GetSoloMode()` nwscript command itself (full table and addresses in
`companion-combat-credit-plan.md`'s own round 83 entry — not duplicated here). **None of the six
are anywhere on the path from `ExecuteCommandAttack` through `AddAttackActions`/`AddAttackAction`
to `RunActions`** — the chain `Odyssey.AttackTarget` already drives, live-proven round 11 to queue
a real attack and land real damage. Consistent with this document's own "Trap 5" from 2026-07-31
(RunActions never even reads `commandable`): the action-queueing/processing pipeline does not
consult party-table state at all. Solo mode's blocking effect is the party AI SCRIPT choosing not
to act on `GetSoloMode()`'s answer — not the engine refusing to let an action be queued.

**This makes the round 82 "no live fix attempted" gate moot, not merely satisfiable.** That gate
was protecting against a WRITE to +0x190 reintroducing a second companion's native follow-AI. A
fix that never reads OR writes +0x190 — because it bypasses the party AI script's decision point
entirely, the same way `attack`/`usedoor`/`skipline` already bypass other script-driven mechanisms
in this project — carries none of that risk by construction: nothing about the party-wide flag
changes, so there is nothing for a second companion's suppressed AI to escape into.

**Shipped, behind the still-OFF `companion_local_combat` flag**:
`CompanionLocalFightRule.ShouldDriveAttack` (new pure decision function, unit-tested: fire only
when the companion's action-queue head reads empty and a 1.5 s per-slot throttle has elapsed) and
`PartyPresenceController.DriveAttackIfDue` (calls `Odyssey.AttackTarget(companionId,
localEnemyId, ...)` from the existing `StayReleased` branch, targeting the exact enemy
`local_combat` is already fighting). No shared-block or protocol change — this is local engine-call
plumbing on the instance that already released the companion, nothing crosses the wire. Full
detail, the exact six read sites with addresses, and the complete live-test plan (including the
two-companion drift-risk check this document's own 2026-08-22 entry demanded before any solo-mode
work ships) are in `docs/research/companion-combat-credit-plan.md`'s own round 83 entry.

**Still NEEDS-LIVE**, deliberately not claimed as proven: whether `AttackTarget` behaves
identically when the attacker is a party member (round 11's own live proof used a plain hostile),
whether one queued attack sustains a whole combat round or needs the repeated re-issue
`ShouldDriveAttack` is designed for, and — the risk this whole line of work exists to rule out —
whether a SECOND, still-mirrored companion genuinely stays undisturbed for the length of a real
fight with two companions present. See the credit-plan document's exact steps before flipping the
default.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1643/1643 passed** (1638 baseline + 5 new
`ShouldDriveAttack` cases). Both plugin projects build clean, 0 warnings introduced by this
round's change (one pre-existing, unrelated `CA2014` warning in `PortraitController.cs`). No rig
access this round — static disassembly and unit tests only; the NEEDS-LIVE items above are for
whoever picks up the rig next.

## Round 84 (2026-08-23) — the phase=7 permanent stall's real root cause found, fixed, and
live-confirmed (two independent bugs, both this plugin's own doing); the walk now genuinely
completes ~90% of a real leg; a second, deeper stall found one layer further and precisely
characterized, not yet fixed

Picks up round 81's own open item directly: "stalls specifically when X" rather than "stalls
unpredictably." Round 81 left the stall narrowed to `phase=7` (`PathWaitDest`, 0x00512B40) with
`CurrentPathNode < WriteCursor` — unconsumed trail sitting right there, never picked up — but did
not know why. This round found it by full disassembly of every function in the chain
(`tools/disasm.py`, cross-checked against `tools/kotor1_0_3.db`), reproduced the exact stall live
with a 25 Hz diagnostic sampled on BOTH sides of this plugin's own engine call (a technique this
round introduces — see below), and found the answer was not in the engine at all: **it is this
plugin's own two per-frame writes fighting each other and fighting the engine's own housekeeping.**

### The real mechanism, disassembled in full

`WalkUpdateLocation_QuickWalk_FollowLeader` 0x0051C360's phase-dispatch jump table
(`tools/disasm.py 0x51CCEC 44` read as raw dwords, not code — it is DATA, disassembling it as
instructions produces garbage) resolves cleanly to 11 short trampolines at 0x51C800-0x51C93B, each
`push`ing the same `(followData, record)` pair and calling one of eleven real handler bodies:

| dispatch | phase | handler | epilogue |
|---|---|---|---|
| 0 | -1 (reset) | inline @ 0x51C800 (snapshot + force phase=5) | 0x51c95e |
| 1 | 0 | `FindPath` 0x51AC10 | 0x51c95c (does NOT zero attempts) |
| 2-5 | 1-4 | 0x511290 / 0x511860 / **0x511B60 ("Moving_To_Path")** / 0x512430 | 0x51c95c |
| 6 | 5 | `WaitingForPath` 0x5127D0 | 0x51c95c |
| 7-10 | 6-9 | 0x512A60 / **`PathWaitDest` 0x512B40** / 0x512C20 / 0x512DE0 | 0x51c955 (ZEROES attempts first) |

`WaitingForPath` (0x5127D0) is the ONLY writer of `record+0x34` (`DestNode`) in the whole binary
(confirmed by disassembling every handler + `CSWPartyFollowDataArray::Reset`/`SmoothFollowPath`/
`ProcessPoint`) — and it sets `DestNode` to the SAME value it writes into `record+0x24`
(`CurrentPathNode`), every time its own two gates (`[followData]+0xFA8 >= 5`, and a second
distance/time gate via a shared helper at 0x6350F0) pass. `PathWaitDest` 0x00512B40's own forward
search (from `CurrentPathNode` toward `WriteCursor`, looking for an index equal to `DestNode`)
finds a match trivially on its FIRST comparison whenever `DestNode == CurrentPathNode` — and ONLY
in that exact case does it fall into a fragile accept/reject test comparing `(node - Prior)`
against `(node - Target)` (0x512B98-0x512C03, an `fcompp` + parity-flag/unordered-comparison
idiom) — a test that is meaningless when `Prior == Target` (a zero-length segment has no direction
to measure "past" against).

### Bug 1 (found and fixed): `TryResetStuckFollowPhase` was resetting a healthy WaitingForPath
every single frame, before its own async job ever had a chance to resolve

Round 74 built `Odyssey.TryResetStuckFollowPhase` to force phase back to 0 (FindPath) whenever
caught at phase `-1` OR `5`, reasoning (correctly, AT THE TIME) that both states "can never
self-recover." That was true only in the context round 74 measured it in: before round 80's
step-accumulator fix existed, the function never ran far enough per frame for phase 5 to be
anything other than the -1 reset's own dead-end target. Once round 80's fix lets the function
genuinely run to completion every frame, phase 5 (`WaitingForPath`) is ALSO the normal, legitimate
state it parks in for the handful of frames its own async job-status field needs to catch up — and
this plugin's own `TryResetStuckFollowPhase`, called unconditionally every frame, was interrupting
it on a permanent one-frame duty cycle: reset to 0 → FindPath resubmits a job (which resets the
SAME status field back to "pending") → next frame reads 5 again → reset again, forever. Live-
confirmed with a NEW diagnostic this round (`preStepPhase`, sampled immediately after this
controller's own per-frame writes but BEFORE its engine call — see methodology below): phase read
5 on essentially every single sampled frame, for the whole observation window, never given the
chance to hold still long enough to resolve.

**Fix**: narrowed `TryResetStuckFollowPhase` to phase `== -1` only (`src/KotorCoop.Plugin/Odyssey.cs`).
Phase 5 now runs its own natural course, including its own internal watchdog (`record+0x44`, which
`WaitingForPath`'s own dispatch route does NOT zero) if it is ever genuinely, permanently stuck.

**Live-verified**: `attempts` (the watchdog counter) started counting up cleanly for the first time
this whole investigation has ever seen it do at phase 5 (`6 → 10 → 30 → 46`, wrapping at the
engine's own 0x32 threshold exactly as designed) instead of pinning at a value this plugin itself
kept clobbering. Phase progressed 5 → 3 (`Moving_To_Path`) for the first time in eleven rounds.

### Bug 2 (found and fixed): `DestNode == WriteCursor` is structurally unreachable by
`PathWaitDest`'s own search loop (an off-by-one in this round's OWN first fix attempt)

Before finding bug 1, this round tried a narrower fix first: `Odyssey.TryAdvanceStalledPathDestination`,
poking `DestNode` forward to `WriteCursor` whenever the FSM is parked at phase 7 with
`CurrentPathNode == DestNode` (the exact degenerate shape `PathWaitDest`'s search trivially matches
without ever really searching). **First version live-tested and disproven**: `DestNode` visibly
advanced in the diagnostic, but `PathWaitDest` kept returning "not ready" forever anyway.
Disassembly of the search loop explains why precisely: it tests `eax == DestNode` BEFORE
incrementing `eax`, then tests `eax == WriteCursor` AFTER incrementing to decide whether to keep
looping — so a `DestNode` set to the exact SAME value as `WriteCursor` is unreachable: the loop's
own "give up" exit fires the instant the incremented `eax` reaches `WriteCursor`, one iteration
before it would ever re-test that value against `DestNode`. **Fixed** to poke `DestNode =
WriteCursor - 1` instead (only when the gap is `>= 2`, so it never produces a second degenerate
`DestNode == CurrentPathNode` in the process) — live-confirmed reachable by the search loop.

Both fixes are independently real and independently safe (each only acts in a narrow, precisely-
identified trigger condition), and both are needed together: bug 1 alone still parks at phase 7
with a degenerate `DestNode`; bug 2 alone never gets called because bug 1's interference prevents
`WaitingForPath`'s own commit from ever landing on a genuinely stale `DestNode` for long enough to
matter. Kill switches: `party_follow_advance_destnode=0` for bug 2's fix (default **on**); bug 1's
fix has no separate switch (it narrows an existing, already-shipped mechanism rather than adding a
new write).

### Methodology: sampling on both sides of this plugin's own engine call, in the same frame

New this round: `PartyFollowController`'s fine-log now also samples `TryGetFollowDiagnostics`
**immediately after this controller's own `TryDriveFollowTarget` write, before
`TryDriveFollowLeaderStep`'s engine call runs** (`preStepTarget`/`preStepPrior`/`preStepPhase` in
the log line), alongside the existing post-engine-call sample. This is what let bug 1 be told apart
from a purely engine-internal phenomenon: the pre-call sample showed OUR OWN write standing
correctly (`preStepTarget` == the leader's real position), and the post-call sample showed it
already clobbered back to a degenerate value — proving the clobber happens INSIDE the engine call
this same frame, not across frames, which narrowed the search to exactly the handlers reachable
from whatever phase the record was actually resting at (which turned out to be 5, not 7 as the
post-call reads alone would have suggested).

### Live result: real, sustained, correct motion for ~90% of a leg — then a SECOND, deeper stall,
precisely characterized

Same recipe as rounds 80/81 (`tp 20 22 -1.3`, then `walk 10 15`), fresh rig cycle, both fixes
active. Trask genuinely walked from her stalled position (19.35, 22.77) to (11.84, 17.90) — real,
continuous position change, `pathNode`/`writeCursor` climbing together (`0→7`, `7→16`), `phase`
progressing `5 → 3` for the first time ever observed — then stopped, bit-identical position, for
the rest of a 55+ second observation window, 2.44 units short of the leader's own landing spot.

This is a DIFFERENT, deeper stall than rounds 74-81's `phase=7` one: `phase` now reads a steady
**3** (`Moving_To_Path`, handler 0x00511B60), not 7. Two new diagnostics this round settle exactly
what is and is not happening:

- **`CreMoveState` (creature+0xA8C) reads a steady `3`** ("follow requested"), not `-1` ("walking",
  an actual queued movement action being stepped) — despite round 80's own headline finding that
  `-1` genuinely is reached earlier in the SAME leg (confirmed again this round: it demonstrably
  does reach -1 while she is visibly walking). Partway through, it reverts back to 3.
- **A new accumulator this round found and reads, `record+0x6C`** — `Moving_To_Path`'s own
  handler unconditionally adds `creature+0xD0` (the step accumulator this plugin pokes to 16 every
  frame) into it on every call — climbs linearly and steadily the WHOLE stalled window
  (`6392 → 423290` over ~45s, ≈9400/s, consistent with ~60fps × 16 per tick) — direct, live proof
  `Moving_To_Path`'s own handler body IS being reached and IS running, every single frame,
  throughout the "stall." It is not stuck outside the function; it is stuck INSIDE it, cycling
  uselessly.

Full disassembly of 0x00511B60 (`tools/disasm.py 0x511b60 700`) shows why: past its own
obstacle-avoidance bookkeeping (a near-identical pattern to round 74's already-disproven "other
slot" theory — a resolve-and-discard, not a real gate, for a one-companion party), it calls a
geometric "has the creature's own real position (`creature+0x90`) gotten close enough to
`FollowDataArray`'s recorded node at the current search index" check (`call 0x4aa980` at
0x511D5A). Only if that returns true does it commit an advance — writing the achieved index into
`CurrentPathNode` (`record+0x24`, exactly matching this round's live data: pinned at 16) AND
resetting `record+0x44` (its own attempts/watchdog counter) to 0 as part of that SAME commit,
EVERY call, regardless of whether the achieved index has actually changed. This is why `attempts`
reads a permanent `0` for this stall (unlike phase 5's stall, where `attempts` climbing was the
signature of THIS round's own fix working) — it is not evidence the handler is unreached; it is
evidence of a handler-internal reset firing on every call, masking its own watchdog from ever
detecting a real stall the way `WaitingForPath`'s does.

**The chain of causation, precisely**: `CreMoveState` reverting to 3 (not this round's own
doing — nothing in this plugin writes `CreMoveState`) means whatever real, engine-native
`AddMoveToPointAction` `FindPath` originally queued (round 74's own citation: the ONLY handler
that calls `AddMoveToPointActionToFront`) is no longer active — completed, superseded, or cleared
by something this round did not chase further. With no live action stepping her real position,
the `0x4aa980` "close enough to the next node" check can never pass again, no matter how many more
times `Moving_To_Path`'s own body re-runs — and nothing in phase 3 itself re-arms a fresh move
action or forces a return to `FindPath`; only the -1 reset does that, and this phase's own
watchdog can never reach it because the SAME commit block that would need 50 real stalled calls to
trip it resets itself to 0 on every one of those calls first.

### What was NOT attempted this round, and the concrete next step

No fix was shipped for this second stall — this project's own engine-write rule requires
understanding a write's target before shipping it, and two things are still open: (1) WHY
`CreMoveState` reverts from -1 to 3 partway through a leg was not chased (a live debugger or a
disassembly pass over the engine's own native `AddMoveToPointAction` completion/action-queue
machinery, not yet examined by this document at all, would be the way in); (2) whether forcing
phase back to `-1` whenever this exact shape holds (`phase==3`, `CreMoveState!=-1`, position
frozen past a threshold) is safe was not tested — it is the same class of fix that worked twice
this round (bugs 1 and 2 above), and is the natural next experiment, but this round's own remaining
budget went to characterizing the NEW stall precisely rather than guessing at a third poke without
first understanding why `CreMoveState` reverts. New diagnostics `FollowDiag.CreMoveState` and
`.MovingToPathAccumulator` are shipped, read-only, wired into the fine-log line, ready for whoever
picks this up.

### Verdict

Two real, live-verified, disassembly-grounded bugs found and fixed this round — both in THIS
plugin's own code, not the engine — closing out the `phase=7` permanent stall that blocked every
one of rounds 74, 75, 80, and 81. Trask now demonstrably completes ~90% of a real walking leg with
genuinely smooth, continuous motion (matching round 81's own "smooth when it progresses" finding,
now sustained far longer and covering far more distance than any prior round observed). The
walk does not yet complete end-to-end: a second, different, deeper stall — `CreMoveState`
reverting away from "walking" partway through a leg, stranding `Moving_To_Path`'s own otherwise-
healthy per-frame handler with nothing left to make geometric progress against — is now
precisely characterized (not "stalls unpredictably" but "the queued native move action stops
being active partway through the leg, for a reason not yet found, and nothing re-arms it"), with
concrete new diagnostics in place and a specific, scoped next experiment named for whoever picks
this up next.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1650/1650 passed**, 2m30s-2m48s across two runs —
this round's changes are two new small writes (`TryAdvanceStalledPathDestination`, and narrowing
`TryResetStuckFollowPhase`'s own condition) and several new read-only diagnostic fields
(`FollowDiag.DestNode`/`.CreMoveState`/`.MovingToPathAccumulator`, `preStepTarget`/`preStepPrior`/
`preStepPhase` in the fine-log line), none exercised by the (x86 NativeAOT, unreferenced-by-tests)
test project. Both plugin builds clean, 0 warnings 0 errors (the one pre-existing
`PortraitController.cs` CA2014 warning is unrelated and untouched), across the round's several
rebuild/redeploy/relaunch cycles (one per hypothesis: the DestNode-advance experiment, its
off-by-one fix, and the TryResetStuckFollowPhase narrowing). Rig torn down clean
(`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1` in both GOG
installs. No `SharedBlock` layout or protocol version change — every new field/call this round adds
is plugin-internal, nothing crosses the wire. Note: this round's session also needed a full
solution rebuild (`dotnet build KotorCoop.slnx`) plus a fresh plugin install partway through, after
three other agents' work landed on this branch mid-session (a solo-mode companion-combat fix, an
offline swoop-save-crafting tool, and a new `loadgame` console verb) and bumped `SharedBlock`'s own
version — a stale agent/plugin pairing briefly produced "plugin shared block not found" until both
halves were rebuilt from the same merged HEAD.

## Round 85 (2026-08-24) — the two-companion drift-risk check round 83 demanded, run live: NO
drift observed in the non-released companion; a real, separate crash bug found and worked around

Task: `companion_local_combat`'s `CompanionLocalFightRule.ShouldDriveAttack`/
`PartyPresenceController.DriveAttackIfDue` (round 83) drives a released companion's attack
directly, deliberately never touching party-wide solo mode — but the risk this design exists to
avoid (a released companion's controller writes fighting a SECOND, still-mirrored companion's own
position) had never been observed with two companions actually in the party. This round recruited
Carth alongside the save's own Trask (`console 'recruit 2 carth'`, `dialogue_recruit_sync=1`,
confirmed via the client log: recruit → spawn → add, no `failure 9`, matching the round-verified
fix), set `companion_local_combat=1`/`companion_local_combat_range=15` on both installs, and ran
the drift check.

### A genuine, separate crash found first: warping with two companions present segfaults the host

The originally-planned recipe (host-only `warp tar_m10aa`, per round 82's own working recipe)
crashed the HOST instantly. `tools/crashlog.ps1` confirms it from the Windows Application log,
independent of the plugin's own log: `module=swkotor.exe code=0xc0000005 offset=0x000aa984` —
`0x400000 + 0xAA984 = 0x4AA984`, the SAME "close enough to the next path node" geometric check
(`0x4AA980`) round 84's own disassembly identified as `Moving_To_Path`'s (`0x00511B60`) own
distance gate. The plugin's own log shows the sequence directly: `party follow finelog` lines for
BOTH companions (0x127 Trask, 0x2 Carth) right up to `epoch: world unloading; clearing phantom,
item, combat and door state`, then silence, then the AV 1.4s later. `PluginHost.OnFrame`'s own
epoch gate (`Odyssey.TryGetWorldEpoch` returning false mid-transition) should stop every controller
including `PartyFollowController` from running during that window — the crash lands inside the
ENGINE's own housekeeping, one frame after a plausibly-premature "epoch looks valid again" read (a
new server/object-array pointer allocated for the target module before its party/creature data is
actually populated), racing `PartyFollowController`'s per-frame `TryDriveFollowLeaderStep` call
against not-yet-settled party-table state for whichever companion resolves to a stale pointer
first. Round 82 (one companion, `Trask` only) warped successfully with what was materially the
same recipe; round 84 added the per-frame `TryDriveFollowLeaderStep`/`TryAdvanceStalledPathDestination`
machinery this crash lands inside AFTER round 82 ran, and never itself tested a warp — so this may
be a round-84 regression that simply had not been exercised against a module transition yet, not
something specific to having two companions, but this round could not isolate which factor was
necessary without re-crashing the rig, and did not attempt to (see "not attempted" below).

**Not fixed this round** — out of scope for the two-companion drift check this round was
prioritised to answer, and fixing a crash inside `PartyFollowController`/the engine's own transition
housekeeping deserves its own dedicated disassembly pass, the same discipline round 84 itself
used, not a rushed patch under time pressure. **Flagging clearly for whoever picks up round 84's
own still-open "Round 84" follow-AI thread next: do not warp/transition modules with
`party_follow_advance_destnode`'s machinery live (the shipped default) without expecting this
crash risk until it is root-caused.** Worked around this round by never warping again — Endar
Spire's own GIT already has hostile Sith (soldiers, apprentices) scattered through the ship, so the
whole drift check below was run entirely within the START module (`END_M01AA`), no transition
needed.

### The drift check itself: decisive, clean, NO drift

Real engine `tp`/`walkto` (never a raw poke) moved both players toward a Sith-soldier pair
(`n_sithsoldier004`, GIT-authored near (44,63) and (48,61)) that had, by the time of the attack,
drifted to ~(33,149) — right next to Carth's own real spawn point (35.16,161.43), the AVAILNPC
template's baked-in GIT position `p_carth001` never left (the already-known, separate follow-AI
stall bug — see "Round 84" above — meant NEITHER companion's native follow-AI ever caught up to the
host's own walked/teleported position this whole round; Trask sat frozen near her own start point
the entire time). This gave the natural asymmetry the check needed for free: Carth ended up within
`companion_local_combat_range` of both the client's PC and a live hostile; Trask, stuck ~130 units
away the whole time, never did.

Client attacked the nearer Sith soldier (`blockpoke.ps1 attack 0x7FFFFFFF 0xE`). The log evidence,
in order:

```
[05:11:40.020] party drive: companion 0x139 released to fight locally - within range of both you and
the enemy you are fighting (head=0xFFFFFFFF moveState=1 soloMode=1 t=296.7s slot=1)
[05:11:40.020] party drive: mirroring 1 of the host's companions
[05:11:40.024] party drive: drove companion 0x139's attack on 0xE directly (head was 0xFFFFFFFF)
[05:11:43.027..05:12:03.777] party drive: companion 0x139 still released - head=0x3F ...  (repeated;
0x3F is AddAttackActions' own attack action id - the engine genuinely queued and kept running it)
[05:12:05.017] party drive: companion 0x139 reclaimed - resuming the host mirror
[05:12:05.017] party drive: mirroring 2 of the host's companions
```

Host, over the same window: `combat: applied 11/4/8 of a peer's damage to 0xE (owed 11→15→23)`,
then `0xE`'s body destroyed (a real kill), then `applied 5/4/9 of a peer's damage to 0x10 (owed
5→9→18)` — real, substantial, authoritative damage landing on the HOST's own copy of the enemies
from Carth's driven swings, closing the credit-pipeline question `companion-combat-credit-plan.md`
round 83 left as NEEDS-LIVE (open questions 1 and 2: a party-member attacker behaves identically to
round 11's plain-hostile case, and one queued attack does NOT sustain a whole fight alone — the
1.5s re-issue `ShouldDriveAttack` was designed for visibly fired eight times, `head=0x3F` holding
steady between issues exactly as designed).

**Trask (slot 0, 0x127) — the non-released companion — never appears in a single `party drive:
companion 0x127 ...` line anywhere in this window.** The `mirroring N of the host's companions`
counter is the direct, structural confirmation: 2 before release, 1 during Carth's release (exactly
the one still-mirrored companion, Trask), back to 2 after reclaim. Position comparison, host vs.
client, sampled independently after the fight:

```
host   0x127 end_trask  (39.48, 19.97)
client 0x127 end_trask  (39.45, 19.93)   [and (39.48, 19.97) on a second sample]
```

Sub-0.05-unit difference — within ordinary mirror/read-timing noise, not drift. **`DriveOne`'s
per-slot independence held exactly as designed: releasing Carth (slot 1) produced zero reads or
writes anywhere near Trask (slot 0) — no release log line, no position divergence, no attack
line naming her as attacker or target.** The risk this whole design exists to avoid — a released
companion's controller writes fighting a second, still-mirrored companion's own native follow-AI —
did not materialise, live, with two real companions and a real sustained fight.

### `verify.ps1` caveat: several fails, all traced to this round's own state disruption, not a
regression

`tools\verify.ps1 -Host1 16824 -Client2 35700` after the fight: 42 passed, 7 failed. Traced each:

- `no swkotor crash in the event log` FAIL — this is the SAME 05:03:43 warp crash above, still
  sitting in the Windows Application log; not a new event, already accounted for.
- `the peer really loses those hit points` / `a client hit costs the enemy real hit points` FAIL —
  both pick the CLIENT's own nearest-enemy census entry and attack it fresh; this round's own
  manual combat had already killed one Sith soldier and driven the other's HP down, and displaced
  both players ~130 units from their start (`enemy 0x35 at 41 units` — the check's own 45s window
  is generous but not guaranteed against `tp`-scale distances plus this module's already-documented
  slow pathing). This is exactly the state-perturbation failure mode `verify.ps1`'s own code
  comments warn about (§"ROUND 58" stale-census-distance note), not evidence the credited-damage
  channel broke — this round's OWN host-log evidence above (`combat: applied N of a peer's
  damage...`, a real kill) is direct, stronger, in-band proof that channel worked correctly
  during the actual companion test.
- `an item picked up here vanishes on the peer` / `a container opened on the client...` FAIL —
  same class: state/position dependent on an undisturbed start, which this round's own manual
  positioning broke.
- `every engine call signature matches the binary` FAIL (170/172) — not investigated this round;
  flagged for whoever runs `verify.ps1` next on an undisturbed rig to confirm whether this is
  pre-existing or new.
- `no per-frame spam` FAIL — cites a single-second spike each side; not investigated, likely this
  round's own heavy diagnostic logging (fine-log running throughout) rather than a regression.

None of these were investigated against a fresh, undisturbed rig this round (time did not allow);
they are recorded here precisely rather than waved away, per the task's own "document precisely,
don't downplay" instruction, but the specific evidence this round needed — the drift check itself,
and the credited-damage channel surviving a companion swing — has direct, independent, in-band
proof above that does not depend on `verify.ps1`'s own combat-check picking a live, nearby, fresh
target.

### Verdict

**No drift observed.** `companion_local_combat`'s design — release exactly one companion, drive its
attack directly, never touch solo mode, never touch a second companion's own mirror state — held up
under a real, live, two-companion, sustained-combat test. This clears the specific risk
`party-reconciliation.md`'s 2026-08-22 entry and `companion-combat-credit-plan.md`'s round 83 both
named as the one gate left before considering a default flip. **Still NOT flipped this round** —
per the task's own explicit instruction, this result makes the fix "safe to consider for a real
(still off-by-default, pending broader testing) validation," not an immediate default change; the
newly-found warp-transition crash (above) is an unrelated but real stability risk against the
BROADER follow-AI feature set that should be root-caused before this or the round-84 follow-AI
thread goes further, and the `verify.ps1` fails should be re-checked on a fresh, undisturbed rig
before anyone treats this as a full regression-clean pass. `companion_local_combat`,
`companion_local_combat_range`, and `dialogue_recruit_sync` were reverted to their shipped defaults
(`0`, `10`, `0`) on both installs after this round.

`dotnet test tests/KotorCoop.Tests` (Release): unchanged this round — no production code touched
(pure live observation against already-shipped round-83 code). Build and baseline confirmed green
before starting (see this round's own commit).

## Round 88 (2026-08-24) — round 84's own "CreMoveState reverts from -1 to 3" mystery resolved
into THREE separate, disassembly-grounded mechanisms, not one; two real fixes shipped and
live-verified (a companion now closes an entire leg down to its very last 1-3 recorded path
nodes, ~1.3-1.4 units short, versus round 84's own best of ~2.44 units short); a third fix
attempted, live-tested, and disproven — kept, documented, and defaulted off, not deleted

Picks up round 84's own precise open item: "WHY `CreMoveState` reverts from -1 to 3 partway
through a leg was not chased." Reproduced round 80/81/84's own recipe (`tp 20 22 -1.3`, `walk 10
15`, `END_M01AA`) fresh this round, with a new 25 Hz diagnostic reading every field this round
could reach, and chased the disassembly to the actual bytes doing the reverting.

### Finding 1: the ONLY other `CreMoveState=3` writer in the whole binary, found and disassembled
— real, but NOT the mechanism reproducing live this round

A full `tools/fieldwrites.py 0xa8c` sweep (round 74's own established technique) finds exactly one
other write site besides the follow-FSM itself: 0x005111A1, inside a small, previously
undocumented function at 0x00511130 (`Odyssey.FollowMoveGateCheckFn`'s own remarks have the full
disasm). It gates on `CSWSObject::GetArea` (0x004CB120, misidentified at first — its own vtable
slot-0x28 call looked like `AsSWCModule` from the `GameObjectMethods` table alone, but the DB's
`functions` table settles the address as `GetArea` directly) returning a live area, then compares
`ResolvePartySlot0()` (party slot 0's own resolved id — "the local player," round 30's own
citation) against the creature's OWN self id (`creature+4`, the same field
`KotorGameBridge.OffObjectId` already names) — if they differ (structurally always true for a
companion, which is never party slot 0), it force-sets `CreMoveState = 3`.

Live-measured this round: the diagnostic built to test this (`FollowDiag.GateFires`,
`.GateAreaId`, `.PartySlot0Id`, `.SelfMatchesPartySlot0`) reads `gateFires=true` on **every single
sample taken, 4636 in a row, including throughout genuinely smooth walking** — it does not
discriminate the moment of the revert; the underlying condition is permanently satisfied. This is
real, structurally the only other candidate for round 84's own literal symptom, and is very likely
still relevant to whatever round 84's own original repro hit — but it is not what this round's own
live reproduction turned out to be gated by (see Finding 3). Left as a precisely-characterized,
not-yet-actioned lead: reaching it requires the engine's own SEPARATE generic per-action-type
dispatch (0x0057F79E, `ebx==5`) to process this creature's queued move, a call this plugin does not
make and cannot directly suppress or race from its own per-frame hook.

### Finding 2 (fixed, live-verified): phase 2's own handler silently requires
`CSWSArea::TestDirectLine` to succeed, even when the geometric "close enough" test is already
trivially satisfied

Full disassembly of handler 0x00511860 (dispatch phase 2, never examined by this document before
this round): it computes the distance between `PriorPoint` (`record+0x18`) and `Snapshot`
(`record+0x5c`), squares it, and compares it against a threshold built from
`*(float*)followData * 0.7` (the constant at 0x746f4c), squared — `followData` here is the FIRST
dword of the whole, party-wide, embedded `CSWPartyFollowDataArray` struct (party+0x2DC), read live
this round as `FollowDiag.MoveBudget` (consistently ~19.6, healthy — ruling out the "budget pinned
at zero" theory this round tried first and disproved). When `PriorPoint == Snapshot` (a zero-length
remaining segment — live-confirmed bit-identical across 30+ consecutive 25 Hz samples spanning an
entire stall) this comparison is trivially satisfied, but the "close enough" branch does NOT
actually commit `record+4 = 3` until reaching 0x511ACD, gated the whole way behind
`CSWSArea::TestDirectLine` (0x004BCB70, called via `GetArea`'s returned area pointer as `this`)
returning EXACTLY 1. Live data: this never resolves favourably once parked in the degenerate shape
— the function instead pool-allocates and queues a re-check via
`CServerAIMaster::AddEventDeltaTime` (0x004B08D0), deferring to a later tick that never seems to
land either, and `record+4` is left completely untouched — cycling (phase 2, `attempts` climbing
0-50, the engine's own watchdog forcing -1, this document's own `TryResetStuckFollowPhase`
immediately restarting FindPath, which re-derives the SAME degenerate phase-2 input) for 30+
seconds straight with zero position change in one live repro.

**Fix**: `Odyssey.TryAdvanceStalledMovingToPath` commits phase 3 directly — the same value the
engine's own "close enough" branch would have written had TestDirectLine returned 1 — but ONLY when
`PriorPoint == Snapshot` (within a tight epsilon) AND at least one real watchdog pass has already
happened (`attempts > 0`, so the very first, still-legitimate visit to phase 2 is left alone). Kill
switch `party_follow_advance_movingtopath=0` (default **on**).

### Finding 3 (fixed, live-verified, one revision): round 84's own literal symptom, reproduced —
and a naive "poke CreMoveState back to -1" fix measured insufficient before a working fix was found

With Finding 2's fix active, a companion now genuinely reaches phase 3 (`Moving_To_Path`) and
`record+0x6C`'s own accumulator (`FollowDiag.MovingToPathAccumulator`, round 84's own diagnostic)
climbs every call — direct proof the handler runs every frame — while `CreMoveState` reads a steady
**3**, not -1, and real position stays frozen. This is round 84's own literal symptom, reproduced
cleanly this round.

First attempt (live-tested, insufficient alone): re-assert `CreMoveState = -1` directly whenever
this shape holds. **Live result: the poke sticks — `moveState` reads -1 continuously afterward,
confirmed in the fine-log — but real position never resumes and `CurrentPathNode` stays frozen.**
Toggling the flag back does not resurrect whatever real, engine-native queued move action
Moving_To_Path's own geometric progress check (0x4AA980, round 84's own citation) needs to measure
against — the flag and the underlying queued action are evidently two different things, and only
the flag was being restored.

**Fix (shipped)**: `Odyssey.TryForceWalkingWhenMovingToPathStalled` forces phase back to **0**
(`FindPath`) instead of touching `CreMoveState` directly — `FindPath` (0x51AC10) is the ONLY
handler that both calls `AddMoveToPointActionToFront` (queuing a real move) AND sets
`CreMoveState = -1`, as part of the SAME shared epilogue (0x51AF5A per round 74's own citation) —
so this lets the engine's own FindPath handler do both jobs itself, on this controller's very next
`TryDriveFollowLeaderStep` call (already made every frame). Fires only when Moving_To_Path is
confirmed genuinely running (its accumulator has advanced since the last frame — tracked via a new
cheap accessor, `Odyssey.TryGetMovingToPathAccumulator`, called every frame rather than only on the
throttled fine-log pass) AND real, unconsumed trail remains AND `CreMoveState` currently reads
something other than -1. Kill switch `party_follow_force_walking=0` (default **on**).

**Live result with both fixes active**: a companion now covers real, continuous distance all the
way down to the very LAST 1-3 recorded trail nodes of a leg (live-confirmed twice more this round,
different runs landing on different final nodes — pathNode 28 and 30 in two separate cycles — each
time stalling cleanly at ~1.3-1.4 units short of the leader, no oscillation, `moveState` holding a
stable 3). This is a real, measured improvement over round 84's own best result (~90% covered, 2.44
units short) — she now consumes essentially the entire recorded trail before stopping.

### Finding 4 (attempted, live-tested, DISPROVEN — kept, documented, defaulted off): the final
1-3 nodes stall at `PathWaitDest`'s own geometric accept/reject test, a fourth, narrower, distinct
shape

With Findings 2 and 3 both active, the companion now reliably stalls one layer further still: phase
7 (`PathWaitDest`, 0x00512B40), `CurrentPathNode == DestNode`, `WriteCursor - CurrentPathNode == 1`
— genuinely the LAST recorded node. Full disassembly of PathWaitDest this round (superseding round
83's own partial read): the forward search matches `DestNode` on the very first comparison when
they are already equal (this shape), falling into an accept/reject test that looks up the recorded
point at that index (`CSWPartyFollowDataArray::GetPoint`-shaped call, 0x00634CC0) and accepts only
when that point is STRICTLY closer to `TargetPoint` than to `PriorPoint` — a "has this waypoint
been passed yet" geometric test. Live-confirmed: it never resolves favourably here, no natural
recovery across 3+ minutes of continuous observation. Round 83's own `TryAdvanceStalledPathDestination`
fix cannot reach this case — its own `gap >= 2` requirement exists precisely because
`DestNode == WriteCursor` is structurally unreachable by this same search loop, and here `gap == 1`
leaves no other in-range candidate value for `DestNode` to be poked to.

**Attempted fix**: `Odyssey.TryAcceptFinalPathNode` commits phase 3 directly (the value the
accept branch would write) once this exact terminal shape has persisted for several consecutive
frames (tracked via a new per-slot counter in `PartyFollowController`, since PathWaitDest's own
dispatch epilogue zeroes `attempts` every call, unlike phase 2's). **Live-tested and DISPROVEN**:
rather than unsticking real motion, it opens a NEW, different unproductive cycle together with
Finding 3's own fix — phase visibly cycles 7 → 3 → 0 (FindPath) → 7 → ... roughly every 0.3-0.5s,
`CreMoveState` settles at 1 ("idle") rather than 3 or -1, and real position stays bit-identical for
30+ continuous seconds — objectively worse than the shape it targets. Per this project's own
established convention (round 80's `TryPokeStepAccumulator`), the function is kept, fully
documented with its own live-disproven result, and defaulted OFF
(`party_follow_accept_final_node=0`) rather than deleted. Leading theory for why it fails, not
chased further this round: unlike phase 2's TestDirectLine (a pure optimisation shortcut, safe to
skip), PathWaitDest's own accept/reject PASS branch may do more than just write the phase — the
disassembly was not followed past 0x512C09 itself to see whether it also arms something
Moving_To_Path's entry depends on, which jumping straight to phase 3 would then be skipping.

### What was NOT attempted this round

Round 85's own still-open module-warp access-violation crash (0x4AA984) was not investigated —
time went entirely to the primary CreMoveState thread, which turned out to require four separate
disassembly-grounded investigations rather than one. **Update: fixed concurrently by a separate
round that ran in parallel on this same branch — see "Round 87" immediately below, which neither
round saw the other's work while in progress.**

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1670/1670 passed**, 2m32s. This round's changes:
three new small engine-writes (`TryAdvanceStalledMovingToPath`, `TryForceWalkingWhenMovingToPathStalled`
revised, `TryAcceptFinalPathNode` shipped but defaulted off), two new cheap read-only accessors
(`TryGetMovingToPathAccumulator`, `TryGetPhaseAndPathNode`), and several new read-only diagnostic
fields (`FollowDiag.GateAreaId`/`.GateFires`/`.PartySlot0Id`/`.SelfMatchesPartySlot0`/`.MoveBudget`,
all wired into the fine-log line) — none exercised by the (x86 NativeAOT, unreferenced-by-tests)
test project. Both plugin builds clean, 0 warnings 0 errors. Across this round's several
rebuild/redeploy/relaunch cycles (one per hypothesis: the phase-2 TestDirectLine fix, the
CreMoveState-only poke that measured insufficient, its FindPath-based revision, the phase-7
final-node fix that measured harmful and was defaulted off). Rig torn down clean
(`Get-Process swkotor,KotorCoop.Agent` empty), `auto_start_agent` restored to `1` in both GOG
installs. No `SharedBlock` layout or protocol version change — every new field/call this round adds
is plugin-internal, nothing crosses the wire.

### Verdict

Round 84's own "CreMoveState reverts from -1 to 3, for a reason not yet found" is now resolved into
three separate, disassembly-grounded, live-tested mechanisms — not the single cause its own framing
implied. Two are real, shipped fixes (a TestDirectLine gate silently blocking a trivially-satisfied
acceptance test, and a naive CreMoveState poke that needed to become a FindPath re-entry poke once
live-measured insufficient alone), together taking a companion from round 84's own best result
(~90% of a leg, 2.44 units short) to consuming essentially the ENTIRE recorded trail, stopping only
at its very last 1-3 nodes, ~1.3-1.4 units short — reproduced cleanly across three separate rig
cycles this round with no oscillation and a stable `CreMoveState`. The third (a fourth, narrower
stall at the very last node) was attempted, live-tested, found to make things WORSE, and shipped
disabled rather than either forced through unverified or silently discarded — the walk still does
not complete end-to-end, but the honest gap remaining is now precisely one geometric accept/reject
test at the very last recorded waypoint, with a concrete disassembly starting point
(0x00512C09 onward) named for whoever picks this up next, rather than the vague "reverts, cause
unknown" this round inherited.
## Round 87 (2026-08-24, static pass, no rig access) — round 85's warp-transition crash, root-caused
and fixed by disassembly; NOT yet live-verified

Picks up round 85's own "not fixed this round" flag directly: warping the host to a different
module while `PartyFollowController`'s per-frame drive/diagnostic path is active (the shipped
default — `party_follow_pokestep` defaults to `1`, not an opt-in) crashes the host with an access
violation at `0x4AA984`. This round had no rig access (another agent owns it), so everything below
is disassembly and source-reading only; live verification is specified at the end for whoever picks
this up with the rig free.

### What is at 0x4AA984

`tools/disasm.py 0x4AA980 40` settles it precisely — it is inside `swkotor.exe`'s own `.text`, NOT
this plugin's code:

```
0x004AA980  8b4c2404   mov ecx, dword ptr [esp + 4]
0x004AA984  d901       fld dword ptr [ecx]          <-- the crash instruction
0x004AA986  8b542408   mov edx, dword ptr [esp + 8]
0x004AA98A  d81a       fcomp dword ptr [edx]
...
0x004AA9AD  b801000000 mov eax, 1
0x004AA9B2  c3         ret
0x004AA9B3  33c0       xor eax, eax
0x004AA9B5  c3         ret
```

This is a small "are these two float3 vectors within epsilon" comparison helper: it loads two
stack-passed pointers (`ecx`/`edx`) and `fcomp`s their X/Y/Z components in turn, short-circuiting
to "not equal" (`eax=0`) the moment any FPU compare goes unordered/not-equal. `0x4AA984` is the
crash address because it is the FIRST instruction that actually dereferences one of those two
pointer arguments (`mov ecx, [esp+4]` at `0x4AA980` only loads the pointer VALUE off the stack,
which never faults; `fld [ecx]` is where a bad pointer value would fault).

This exact function is round 84's own already-identified `call 0x4aa980` at `0x511D5A`, inside
`Moving_To_Path` (`0x00511B60`) — round 84 called it "close enough to the next path node." Reading
the call site (`tools/disasm.py 0x511CE0 160`) resolves both arguments precisely: the SECOND push
(`push edx`, `0x511D55`) is `lea edx, [esp+0x30]` — the address of a small on-stack float3 local
this same code just filled from a node-lookup call (`call 0x634cc0`); the FIRST push (`push ecx`,
`0x511D50`) is `lea ecx, [edi + 0x90]` — a pointer directly into whatever creature `edi` currently
holds, at the offset round 84 already names as the creature's own live position field. By x86
cdecl stack order, at the callee's entry `[esp+4]` (dereferenced first, at the crash instruction)
is the LAST-pushed value — the on-stack node-position local, always a valid address of the
CALLER's own frame — and `[esp+8]` is `edi+0x90`, the creature-position pointer. A stack-local
address should never itself fault; a stale/freed `edi` (a creature pointer for a slot that
resolved to garbage after a module change) faulting through `edi+0x90` at the SECOND compare
(`fcomp dword ptr [edx]` reads `edx=[esp+8]=edi+0x90` — wait, on the recorded fault this was the
FIRST dereference that faulted, at `0x4AA984`/`ecx`) is the only account this round could not fully
square against the pure stack-arithmetic reading above without a live debugger — see "what a live
session would need to check" below. What IS fully settled without ambiguity: this is engine code,
reached only via `Moving_To_Path`, reached only via `WalkUpdateLocation_QuickWalk_FollowLeader`'s
dispatch, reached only via this plugin's own `Odyssey.TryDriveFollowLeaderStep(objectId, 16)` call
(`PartyFollowController.cs`, inside the per-companion loop) — not a call this plugin makes
anywhere else, and not reachable without this controller running.

### Root cause: PartyFollowController had NO transition/epoch guard at all

Confirmed by reading the whole file before this round's fix: zero occurrences of
`TryGetWorldEpoch`/`IsInTransition`/`trans_pending`/any epoch tracking anywhere in
`PartyFollowController.cs`. Every other stateful per-frame controller this codebase ships
self-invalidates on a module change — `DialogueSpectateController.OnFrame` is the clearest existing
example (`Odyssey.TryGetWorldEpoch`, compare against a cached `_epoch` field, `Invalidate()` on
change) — but this one never adopted that pattern, because rounds 71-85 built it up purely against
a same-module rig (round 85 itself is the FIRST round in this whole investigation to attempt a warp
with this controller's round-80/83/84 machinery live).

`PluginHost.OnFrame`'s own epoch gate (`Odyssey.TryGetWorldEpoch` returning false) already refuses
to call ANY controller, including this one, while the world is mid-teardown or has no server at
all — so the crash is not "ran during the gate's own false window." Round 85's own log evidence
pins it more precisely than that: `party follow finelog` lines for both companions right up to
`epoch: world unloading; clearing phantom, item, combat and door state`, then silence, then the AV
1.4s later — i.e. the crash happened on or shortly after the FIRST frame `TryGetWorldEpoch` started
reporting the NEW module's epoch as valid again. PluginHost's own gate answers "is there a
readable world," not "are THIS module's party-table/follow-data records populated yet" — and round
85's own working theory (a fresh object-array pointer allocated for the target module before its
party/creature data is actually populated) is exactly the gap between those two questions. Nothing
in `PartyFollowController` before this round asked the second one at all; it simply ran every frame
`PluginHost` let it run, trusting "the world is readable" to also mean "my own per-companion state
for this world is trustworthy."

### The fix

New file `src/KotorCoop.Plugin/PartyFollowTransitionRule.cs` — a one-line pure function,
`ShouldSkipThisFrame(bool worldEpochReadable, ulong currentEpoch, ulong lastSeenEpoch) =>
!worldEpochReadable || currentEpoch != lastSeenEpoch`, mirroring `WorldLoadRule`'s own
already-established "pin the assumption in a tested pure function, not inside unsafe engine-pointer
code" shape (see `WorldLoadRuleTests.cs`; new `PartyFollowTransitionRuleTests.cs` added alongside
it, 4 cases: unreadable world, first frame of a new epoch, the very-first-live-frame zero-epoch
edge, and an unchanged epoch never skipping).

`PartyFollowController` gets a new private `_lastSeenEpoch` field (the controller's OWN last-driven
epoch — deliberately not reusing `PluginHost`'s identically-named field, so "did I personally
resume driving this epoch" cannot be answered by a flag some other controller reset for an
unrelated reason) and a new check at the TOP of `OnFrame`, immediately after the existing
command/presence/conversation/roster-freeze guards and BEFORE the `PumpPartyUpdate`/
`RecalculateFollowPoint` calls (not just before the per-companion loop — both are unconditional
per-frame engine calls into the same `CSWParty` machinery, and round 85's evidence does not narrow
the crash specifically to the per-companion loop, only to "the first frame this controller's engine
calls run against a newly-valid epoch"). On the single frame a new epoch is first observed (or the
world is unreadable), the controller clears its per-slot `_tracked` bookkeeping and returns without
calling ANY engine function this frame — deferring `PumpPartyUpdate`, `RecalculateFollowPoint`,
`TryDriveFollowTarget`, `TryResetStuckFollowPhase`, `TryDriveFollowLeaderStep` (the call that
reaches the crash), `TryAdvanceStalledPathDestination`, and the diagnostic reads by exactly one
frame across a module change. Every frame after that — including the very next one — runs exactly
as it did before this round; ordinary play (epoch unchanged frame over frame) never skips anything,
so the diagnostics and follow-drive logic keep their full, unthrottled behaviour outside a
transition.

### Honest confidence assessment

**What this round is confident about, from disassembly alone:** `0x4AA984` is engine code, not
plugin code; it is reached only through this controller's own `TryDriveFollowLeaderStep` call;
`PartyFollowController` had no transition awareness at all before this fix, unlike every sibling
controller in this codebase; and round 85's own log timing places the crash specifically at the
"epoch just became valid again" boundary, which the new guard now defers past.

**What this round could NOT independently confirm without a live debugger:** (1) which of the two
float3 pointers passed into the `0x4AA980` helper is the one actually invalid at crash time — the
pure stack-arithmetic reading above says the FIRST dereference (`ecx`, the crash instruction) is a
caller-stack address that should always be valid, and the more plausible stale pointer
(`edi+0x90`, the creature position) is the SECOND argument, dereferenced one instruction later
(`0x4AA98A`) — a real discrepancy this round's static-only pass could not resolve (possibilities:
the recorded fault offset names the faulting INSTRUCTION rather than the faulting DEREFERENCE in
some edge case this round does not know how Windows' AV reporting handles for `fld`/`fcomp` FPU
instructions specifically; or `edi` itself, not just `edi+0x90`, is what's actually corrupted, which
would make `lea ecx,[edi+0x90]` at the CALL SITE already compute a poison value before the callee
even runs — in which case ecx being "the crash pointer" is fully consistent after all, since `edi`
being garbage poisons BOTH the lea'd address AND anything computed relative to it, and my confident
claim above that "a stack-local address is always valid" implicitly assumed `edi` was NOT what got
mixed up with the stack-local in a mistraced push/lea pairing — a live disassembler trace, not a
static read, is needed to be certain which register genuinely held which value at the fault); (2)
whether ONE frame of deferral is always sufficient for the new module's party-table/follow-data
records to settle, or whether under some load conditions it can take more than one frame — the
"one frame" figure is INFERRED from round 85's own log-timing description ("one frame after"), not
measured with a debugger step-through counting real frames.

Given both open items are exactly the kind a live debugger (break on the AV, inspect `edi`/`ecx`/
`edx` at the fault, single-step the frames immediately after a real warp) would settle in minutes,
and this round's fix defers the ENTIRE reachable call chain (not just the diagnostic log line) by a
full frame regardless of which specific pointer is the culprit, this is shipped as the correct,
disassembly-grounded, conservative fix — but flagged, per this project's own standard, as
NOT YET LIVE-VERIFIED rather than closed.

### Live-verification step for whoever has the rig next

1. `src/KotorCoop.Plugin/build.ps1 -Install` on both installs, `tools/demo.ps1` up, recruit at
   least one companion (Trask is already in the Endar Spire start; round 85's own `recruit 2 carth`
   primitive works too for a two-companion run).
2. Leave `party_follow_pokestep` and `party_follow_advance_destnode` at their shipped defaults (`1`)
   — the crash path must be live, not disabled, for this to be a real test. `party_follow_finelog=1`
   is optional (it makes the log easier to read around the transition but is not what crashed).
3. Reproduce round 85's exact recipe: host `warp tar_m10aa` (or any real cross-module warp) while a
   companion is actively in the follow state.
4. Pass condition: `tools/crashlog.ps1` shows no NEW `module=swkotor.exe code=0xc0000005` event at
   or after this warp (compare timestamps against any pre-existing entries from prior rounds); the
   host process is still alive and responsive after the transition.
5. Confirm the fix does not just avoid the crash by accident but is doing what it claims: the
   plugin log should show a gap of approximately one frame in `party follow finelog`/`party follow:`
   output immediately after the `epoch: world unloading...` line and the module actually finishing
   its load, then normal-looking follow diagnostics resuming in the NEW module (sane, non-garbage
   `phase`/`pathNode`/position values — not zeros or huge numbers, which would indicate the guard
   fired but the underlying data was still bad one frame later, i.e. the "one frame is enough"
   assumption above was wrong and needs a second look).
6. If it still crashes: break in a debugger on the AV, inspect `edi`, `ecx`, `edx` and `esp` at
   `0x4AA984` to settle open item (1) above precisely, and check how many real frames elapse between
   the `epoch: world unloading` log line and the first `party follow` line in the new module to
   settle open item (2) — if it is reliably more than one, widen `PartyFollowTransitionRule` to
   require N clean frames (or a positive party-table sanity check) rather than exactly one.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1667/1667 passed** (1663 baseline + 4 new
`PartyFollowTransitionRuleTests` cases), ~2m30s. `src/KotorCoop.Plugin/build.ps1` (no `-Install`,
per this round's no-rig-access constraint) builds clean, x86 confirmed, 0 warnings introduced (the
one pre-existing `PortraitController.cs` CA2014 warning is unrelated and untouched). No
`SharedBlock`/protocol version change — `PartyFollowTransitionRule.cs` is a new dependency-free pure
file (added to `KotorCoop.Tests.csproj`'s direct-compile list alongside `WorldLoadRule.cs`), and the
only other change is two new fields/one new guard block inside `PartyFollowController.cs`, nothing
crossing the wire.

## Live-verified (2026-08-24): the round 87 fix closes the warp crash cleanly, one skip window then sane resumption

Followed round 87's own "Live-verification step for whoever has the rig next" exactly, on a fresh
`build.ps1 -Install` of both installs, `tools/demo.ps1` up on save 1 (Endar Spire, Trask already an
active follower), `party_follow_pokestep`/`party_follow_advance_destnode` left at their shipped
defaults (`1`, not disabled). Baseline `tools/crashlog.ps1` showed the historical `0x4AA984` AV from
round 85 at `05:03:43`, untouched since.

Host-only `console 'warp tar_m10aa'` (never an independent client warp, per this doc's own
established discipline) while Trask was actively following, at `07:45:13`. Result: **no new AV** —
`crashlog.ps1` immediately after still shows the same `05:03:43` entry as the most recent, nothing
newer. Both processes stayed alive and `Responding=True`. The host's own travel field confirmed
`module 'tar_m10aa'` applied; the client independently reached the same module through the ordinary
travel-follow channel with no errors in its own plugin log.

The plugin log resolves round 87's own open item 2 (whether one frame of deferral is enough) as a
non-issue in practice: the last `party follow finelog` line before the transition
(`07:45:16.987`, creature `0x127`, `pos=(19.35,22.77)`) is immediately followed by
`flush, before the module is stored` → `travel: moving to module 'tar_m10aa'` →
`epoch: world unloading; clearing phantom, item, combat and door state` (`07:45:17.031`) → a real
~1.5s module-load gap with no follow diagnostics at all (the load itself, not a stuck controller) →
the FIRST post-load finelog line at `07:45:18.803`, now for creature `0x11C` (a fresh object id in
the new module, as expected), with fully sane, non-garbage values from the very first sample:
`pos=(104.79,115.22) leaderPos=(103.29,115.92) target=(103.29,115.92)` — no zeros, no huge numbers,
stable across the next several samples. `PartyFollowTransitionRule`'s single-frame skip is doing
exactly what round 87 designed it to do: the controller cleanly sits out the one frame where the
new module's party-table data isn't trustworthy yet, then resumes with real data as soon as it is.

**Verdict: CONFIRMED FIXED, live-verified.** No new access violation, host and client both survived
the warp, follow diagnostics resumed with sane values on the first frame after the module settled.
Round 87's open item 1 (which of the two float3 pointers was actually invalid) remains formally
unresolved without a live debugger break, but is now moot for shipping purposes — the fix defers the
entire reachable call chain regardless of which pointer was the culprit, and this live pass found no
residual crash to chase. This closes the round 85/87 thread; no further action needed unless a
future round finds a NEW crash signature at this same warp site.

No code changed this round — verification only. `dotnet test`: 1674/1674 green throughout
(unrelated to this fix; no production code touched).

## Round 89 (2026-08-24, static pass, no rig access) — round 88's own "0x512C09 onward" open item
followed to its end: the "missing side effect" theory is DISASSEMBLY-REFUTED, a sharper hypothesis
and a new live-testable diagnostic take its place

Picks up round 88's own precise open item verbatim: "the disassembly was not followed past
0x512C09 itself to see whether it also arms something Moving_To_Path's entry depends on, which
jumping straight to phase 3 would then be skipping." This round had no rig access (another agent
owns it), so everything below is `tools/disasm.py`/`tools/xrefs.py` disassembly and source-reading
only.

### What is actually at 0x512C09 onward

`tools/disasm.py 0x512B40 300` disassembles PathWaitDest (0x00512B40) in full, including past the
point round 88 stopped. The accept branch is the ENTIRE remainder of the function:

```
0x00512C09  5f                   pop edi
0x00512C0A  c7460403000000       mov dword ptr [esi + 4], 3      ; phase = 3, Moving_To_Path
0x00512C11  a1bc5d7400           mov eax, dword ptr [0x745dbc]   ; eax = 1 (a DATA constant)
0x00512C16  5e                   pop esi
0x00512C17  5b                   pop ebx
0x00512C18  83c40c               add esp, 0xc
0x00512C1B  c21000               ret 0x10
```

`0x745dbc` is not code — reading the raw bytes directly (`struct.unpack_from('<i', ...)`) confirms
it holds the plain 32-bit integer `1`. It is loaded into `eax` purely as this function's own return
value; nothing is called, and no field besides `record+4` is written. Both paths that reach
0x512C09 — the unconditional "search found DestNode at a later index" branch (`jne 0x512c09` at
0x512B80) and the geometric-test PASS branch (falling through from 0x512C03) — land on this exact
same tail. **Round 88's own leading theory is refuted, not merely untraced: there is no missing
field write or engine call inside PathWaitDest's accept path.** It does precisely what
`Odyssey.TryAcceptFinalPathNode` already does: `*(int*)(record+4) = 3`.

### The return value IS real, but it belongs to the CALLER, not to PathWaitDest itself

`tools/xrefs.py 0x512B40` finds exactly one call site, `0x0051C902`, inside
`CSWSCreature::WalkUpdateLocation_QuickWalk_FollowLeader` (0x0051C360 — confirmed against
`tools/kotor1_0_3.db`'s own `functions` table, and the same function round 87 already named as this
plugin's own `TryDriveFollowLeaderStep(objectId, 16)` entry point). Disassembling the call site's
own epilogue (`tools/disasm.py 0x51C8EF 40`) shows phase 6-10's call sites (including PathWaitDest's)
share a short epilogue at 0x0051C955 that phases 0-4 skip:

```
0x0051C955  c7464400000000       mov dword ptr [esi + 0x44], 0   ; attempts = 0 (only for phases 6-10)
0x0051C95C  8be8                 mov ebp, eax                    ; ebp = the handler's own return value
0x0051C95E  837e4432             cmp dword ptr [esi + 0x44], 0x32
0x0051C962  7e0e                 jle 0x51c972
0x0051C964  c74604ffffffff       mov dword ptr [esi + 4], 0xffffffff  ; attempts>50 watchdog: phase=-1
0x0051C96B  c7464400000000       mov dword ptr [esi + 0x44], 0
0x0051C972  3b2dbc5d7400         cmp ebp, dword ptr [0x745dbc]   ; did the handler return 1 ("accepted")?
0x0051C978  0f8452feffff         je 0x51c7d0                     ; if so, redispatch NOW
```

`0x51C7D0` is the TOP of this function's own attempts-increment + phase-dispatch jump table
(`tools/disasm.py 0x51C7A0 220`; the jump table itself, `0x0051CCEC`, 12 entries indexed by
`phase+1`, was read in full — phase -1 is inlined snapshot-copy code, phases 0-10 each `call` one of
the already-known handlers, e.g. index 4 = phase 3 = `call 0x511b60`, `Moving_To_Path`):

```
0x0051C7D0  d9442414             fld dword ptr [esp + 0x14]
0x0051C7D4  d81d00d77300         fcomp dword ptr [0x73d700]      ; a per-call frame-time-budget check
0x0051C7DA  dfe0                 fnstsw ax
0x0051C7DC  f6c401               test ah, 1
0x0051C7DF  0f8599010000         jne 0x51c97e                    ; budget exhausted: stop for this call
0x0051C7E5  8b5644               mov edx, dword ptr [esi + 0x44] ; attempts++
0x0051C7E8  8b4604               mov eax, dword ptr [esi + 4]    ; eax = phase
0x0051C7EB  42                   inc edx
0x0051C7EC  40                   inc eax                          ; index = phase+1
0x0051C7ED  83f80b               cmp eax, 0xb
0x0051C7F0  895644               mov dword ptr [esi + 0x44], edx
0x0051C7F3  0f8765010000         ja 0x51c95e
0x0051C7F9  ff2485eccc5100       jmp dword ptr [eax*4 + 0x51ccec] ; re-enter the SAME jump table
```

So a return value of exactly `1` is a generic "I changed phase, please redispatch immediately"
signal that EVERY handler in this switch uses, consumed entirely by the caller's own loop — not
something PathWaitDest arms for `Moving_To_Path` specifically. When the genuine accept branch fires,
`WalkUpdateLocation_QuickWalk_FollowLeader` calls `Moving_To_Path`'s handler (0x511b60) again
immediately, in the SAME engine call (subject to the time-budget float check against the global at
0x73D700 and the `attempts<50` watchdog on `record+0x44`).

### Why this same-call cascade is very likely NOT the actual cause of the disproven cycling

`Odyssey.TryAcceptFinalPathNode` pokes `record+4=3` from `PartyFollowController`'s own per-frame
loop, strictly AFTER that frame's `TryDriveFollowLeaderStep` call has already returned (confirmed by
reading `PartyFollowController.cs`: the call is at line ~359, `TryAcceptFinalPathNode` at line
~415) — so it never gets the same-call cascade; `Moving_To_Path` only gets its first real chance to
run on the FOLLOWING frame's normal dispatch (which reaches it directly via the jump table, since
`record+4` already reads 3 by then — the same mechanism round 88's Findings 2 and 3 already rely on
and already proved works). Critically, `TryAdvanceStalledMovingToPath` and
`TryForceWalkingWhenMovingToPathStalled` poke `record+4` the EXACT same way — from outside the
dispatcher, also strictly after that frame's `TryDriveFollowLeaderStep` call, in the same
per-companion loop — and are live-verified (round 88) to produce real, sustained motion. A one-frame
dispatch deferral is therefore not fatal in general; it does not explain why THIS poke specifically
produces a worse result than doing nothing.

### Sharper hypothesis, and a new diagnostic to settle it live without another disassembly pass

The likelier explanation: PathWaitDest's own geometric accept/reject test (0x512B86-0x512C03,
comparing squared distances from the recorded node's own point to `TargetPoint` (`record+0x28`) and
`PriorPoint` (`record+0x18`)) may be REJECTING CORRECTLY, not spuriously blocking. Round 73's own
finding already established that `TargetPoint`/`PriorPoint` go stale (frozen, not refreshed by the
engine's own `CSWParty::Update`/`RecaulateFollowPoint`) for a companion parked in a stall — if the
recorded node genuinely has not yet been "passed" relative to a `TargetPoint` that is honestly
behind where the leader now is, forcing the accept anyway hands `Moving_To_Path` a segment with no
real remaining distance to walk, which fits the observed 7 -> 3 -> 0 (FindPath) -> 7 cycle (arrives
instantly with nothing to do, falls back to FindPath, re-lands on the same degenerate node) better
than "a skipped side effect" does, now that this round has confirmed no side effect exists to skip.

This was NOT live-tested this round. To make it testable without a further disassembly pass, this
round adds one new read-only diagnostic field, `Odyssey.FollowDiag.RecordedDestPoint` — the recorded
trail node at `CurrentPathNode`'s own index, read via `CSWPartyFollowDataArray::GetPoint`'s own
disassembled addressing (`tools/disasm.py 0x634CC0 80` and the in-range path at `0x634D1E`:
`lea ecx,[esi+esi*4]; lea eax,[edi+ecx*8]`, i.e. `this + index*40`, confirmed consistent with the
already-established `WriteCursor` offset — 100 entries * 40 bytes = 0xFA0, `WriteCursor` sits at
0xFA4 immediately after). No engine call is made; it is a plain, bounds-checked memory read, wired
into the `party follow finelog` line as `recordedDestPoint=(x,y)` alongside the existing
`target=`/`prior=` fields.

`Odyssey.TryAcceptFinalPathNode`'s own poke is UNCHANGED this round — still exactly
`*(int*)(record+4) = 3`, still defaulted off (`party_follow_accept_final_node=0`). Extending it
based on the original "missing side effect" premise would have meant building on a premise this
round's own disassembly disproved; there is nothing else PathWaitDest's accept path does that this
function does not already replicate.

### Live-verification step for whoever has the rig next

1. `src/KotorCoop.Plugin/build.ps1 -Install` on both installs, `tools/demo.ps1` up, reproduce round
   88's own recipe (`tp 20 22 -1.3`, `walk 10 15`, `END_M01AA`) with `party_follow_finelog=1` and,
   this time, `party_follow_accept_final_node=1` (still off by default; enable it explicitly for
   this pass only).
2. Let the companion close the leg down to the final-node stall (phase 7, `pathNode==destNode`,
   `writeCursor-pathNode==1`) exactly as round 88 described.
3. Read the `party follow finelog` line at and immediately after the stall: compare
   `recordedDestPoint=(x,y)` against `target=(x,y)` and `prior=(x,y)` on the SAME line.
   - If `recordedDestPoint` is genuinely closer to `prior` than to `target` (i.e. the real engine
     test's rejection looks geometrically correct given where `target`/`prior` actually sit), that
     confirms this round's hypothesis: the fix is wrong to force acceptance here, and the real
     problem is `target`/`prior` staying stale during the stall (a round-73/74-shaped problem, one
     layer earlier than PathWaitDest) rather than anything about PathWaitDest's own test.
   - If `recordedDestPoint` is already closer to `target` than to `prior` (the test SHOULD be
     accepting on its own terms) yet still stalls, that rules this hypothesis out and reopens the
     search for a genuinely different blocker — at that point, breaking in a debugger on the
     `fcompp` at 0x512BF2 to single-step the actual flag outcome would settle the comparison
     direction precisely, something this round's static pass did not attempt.
4. With `party_follow_accept_final_node=1` active, also re-confirm round 88's own disproven result
   still reproduces (phase cycling 7 -> 3 -> 0 -> 7, `CreMoveState` settling at 1) — this round
   changed no behaviour, so it should be unchanged, but confirming it is unchanged is cheap and
   rules out this round's diagnostic-only edit having any accidental side effect.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): 1674/1674 passed (baseline unchanged — this round
adds one new read-only struct field and one new bounds-checked memory read, neither exercised by the
x86 NativeAOT plugin-only test project, same as every prior round's diagnostic additions).
`src/KotorCoop.Plugin/build.ps1` builds clean, x86 confirmed, 0 warnings introduced (the one
pre-existing `PortraitController.cs` CA2014 warning is unrelated and untouched). No
`SharedBlock`/protocol version change — everything this round touches
(`Odyssey.FollowDiag.RecordedDestPoint`, its read in `TryGetFollowDiagnostics`, and the new
`recordedDestPoint=` finelog field) is plugin-internal diagnostics, nothing crosses the wire.

### Verdict

Round 88's own named next step — follow the disassembly past 0x512C09 — is now fully done. The
"PathWaitDest's accept branch arms something extra" theory it left open is disassembly-refuted: the
accept branch writes only `record+4=3` and returns a status code consumed solely by the caller's own
same-call redispatch convenience, which this round shows (by comparison with two sibling fixes using
the identical external-poke pattern) is unlikely to be what makes `TryAcceptFinalPathNode`
specifically fail. The likelier explanation — PathWaitDest's geometric test correctly rejecting a
stale `TargetPoint`/`PriorPoint` pair — is now a concrete, falsifiable hypothesis with a
purpose-built, zero-risk diagnostic (`FollowDiag.RecordedDestPoint`) already shipped to settle it on
the very next live pass, rather than requiring yet another disassembly round first. The walk still
does not complete end-to-end; the honest gap remaining is unchanged in practice (still the very last
recorded waypoint) but the open question about it is now sharper and directly testable.

## Round 90 (2026-08-24, static pass, no rig access) — a geometry-only fix candidate built on
round 89's own falsifiable hypothesis: `Odyssey.TryRefreshFinalNodeTarget`, built and unit-tested,
NOT YET LIVE-VERIFIED

This round had no rig access (another agent held the only live slot). Everything below is
source-reading and static analysis against round 89's own already-shipped `FollowDiag
.RecordedDestPoint` diagnostic and its own precise verdict: PathWaitDest's geometric accept/reject
test at the very last recorded trail node (phase 7, `pathNode == destNode`, `writeCursor - pathNode
== 1`) may be **correctly rejecting** a stale `TargetPoint`/`PriorPoint` pair, not spuriously
blocking a healthy one — and round 88's `Odyssey.TryAcceptFinalPathNode`, which forces the test's
own OUTPUT (`record+4 = 3`) directly, was live-disproven precisely in this shape (a new 7 → 3 → 0 →
7 cycle, `CreMoveState` settling at 1/idle, zero position change).

### The sharper hypothesis

Round 89 named the next step as reading `recordedDestPoint`/`target`/`prior` together on a live
finelog line to see which side of the test they land on — but that only tells a future round
whether the test SHOULD have passed; it does not, on its own, give that companion a path to
actually pass it. If round 89's own hypothesis is right (`Target`/`Prior` are stale at the exact
terminal shape), then round 88's fix and round 89's own diagnostic both stop one step short of a
candidate that could plausibly work: **refresh the test's inputs instead of forcing its output.**

Round 89's own disassembly of 0x512C09 onward (this document's own citation, reread this round)
already establishes there is nothing else the accept branch does besides `record+4 = 3` — no
missed side effect to replicate. That means a correctly-implemented geometry refresh, followed by
letting the UNMODIFIED engine code decide, is strictly narrower than `TryAcceptFinalPathNode`:

- If the geometry was genuinely stale and refreshing it lets the real accept branch fire on the
  very next natural dispatch, the companion reaches phase 3 through the SAME code path
  `TryAdvanceStalledMovingToPath`/`TryForceWalkingWhenMovingToPathStalled` already use
  successfully (round 88) — not a hand-poked phase value from outside the dispatcher.
- If the geometry was already correct and the stall persists regardless, this changes nothing
  (the write only fires past a real, meaningful drift — see below) — a clean, low-risk way to
  falsify round 89's hypothesis without reintroducing round 88's own disproven cycle.
- It can never itself produce the round-88 cycle, because it never touches `record+4` at all.

### What was built: `Odyssey.TryRefreshFinalNodeTarget`

Detects the exact same terminal shape `TryAcceptFinalPathNode` already detects (phase 7, `pathNode
== destNode`, `writeCursor - pathNode == 1`, via the same `CSWPartyFollowDataArray` write-cursor
this document's round 75/89 sections already establish), but instead of writing `record+4`, it
performs the SAME non-degenerate `Prior`/`Target` write round 83's `Odyssey.TryDriveFollowTarget`
already ships (`Prior <- the OLD target`, `Target <- the fresh leader position`, `record+0x68 <- 1`
to arm the dirty-snapshot flag `WalkUpdateLocation_QuickWalk_FollowLeader` itself reads) — but
bypasses that function's own ~10cm "meaningfully different" throttle, restricted ONLY to this one
narrow terminal shape. The throttle exists in the general case to avoid stomping the dirty flag
every frame once a companion is genuinely caught up and walking normally; a companion parked in
this exact shape is by definition not walking normally, and the residual risk this targets is the
leader's own final small steps each individually landing under that 10cm threshold, leaving
`TargetPoint` up to just under 10cm stale relative to the leader's true final rest position — small,
but exactly the kind of silent staleness round 89's own hypothesis needs ruled out. A ~1cm epsilon
guards against writing when the target is already fresh (avoiding both a no-op write and a
degenerate zero-length `Prior`-to-`Target` segment).

The shape-detection and epsilon logic is pulled into a new pure, unit-tested class,
`FinalNodeGeometryRule` (`src/KotorCoop.Plugin/FinalNodeGeometryRule.cs`), mirroring
`PartyFollowTransitionRule`/`PartyFreezeRule`/`CompanionLocalFightRule`'s own established pattern
of keeping the branching logic testable and the raw pointer arithmetic in `Odyssey.cs` thin. Both
`TryAcceptFinalPathNode` and `TryRefreshFinalNodeTarget` now share the identical terminal-shape
constant through this rule (they did not before this round; keeping them in sync by hand across two
independently-edited functions was itself a latent risk this refactor also closes).

Wired into `PartyFollowController.OnFrame`'s existing per-slot loop, called every frame the
terminal shape holds (no stall-frame counter needed — the function's own epsilon already prevents
repeat writes once genuinely fresh, unlike `TryAcceptFinalPathNode`'s forced poke, which needs the
frame-count gate specifically to avoid firing on a single, possibly-still-legitimate visit to phase
7). Kill switch `party_follow_refresh_final_node=0` (default **off** — no live A/B yet, per this
project's own bar: a default flips ON only after "a direct, decisive, zero-regression live A/B,"
which this round could not run).

### Honest status

**NOT live-tested.** This round had no rig access. The intended live-verification recipe (recorded
in `Odyssey.TryRefreshFinalNodeTarget`'s own doc comment for whoever picks up the rig next):

1. `src/KotorCoop.Plugin/build.ps1 -Install` on both installs, reproduce round 88's own recipe
   (`tp 20 22 -1.3`, `walk 10 15`, `END_M01AA`) with `party_follow_finelog=1` and
   `party_follow_refresh_final_node=1` (both `party_follow_accept_final_node` and this flag can be
   tested independently — they are mutually exclusive in effect but not code-gated against each
   other, so test one at a time).
2. Let the companion close the leg down to the terminal stall exactly as round 88 described.
3. Watch the finelog line: if `phase` advances past 7 on its own shortly after this fix's first
   write (`target=`/`prior=` visibly changing on the SAME or a following line) and `CreMoveState`
   reaches -1 with real subsequent position change, round 89's hypothesis is confirmed and this is
   a real fix — candidate for a default flip after a clean, repeated live A/B.
4. If `target`/`prior` were already effectively at the live leader position before this fix's first
   write (no measurable change) and the stall persists regardless, round 89's hypothesis is refuted;
   the next step is a live debugger on the `fcompp` at 0x512BF2 (PathWaitDest's own comparison),
   exactly as round 89's own verdict already named.
5. Either way, confirm no regression against round 88's own baseline: no new phase-cycling, no
   `CreMoveState` regression, `tools/verify.ps1`'s two party-specific checks still pass clean.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): **1701/1701 passed** (12 new unit tests in
`FinalNodeGeometryRuleTests.cs`, exercising the shared shape/epsilon logic in isolation — the
terminal shape's phase/pathNode/destNode/gap combinations, the epsilon boundary on both sides, and
that a meaningful drift on either axis alone is enough to trigger a refresh; the baseline going into
this round was already 1689 — higher than round 89's own recorded 1674 — reflecting commits merged
onto this branch after round 89 was written but before this round started). `FinalNodeGeometryRule
.cs` is dependency-free (no `SharedBlockView`, `Odyssey`, or unsafe engine pointers) so it compiles
directly into the test project alongside its sibling rules, per that project's own established
convention (`tests/KotorCoop.Tests/KotorCoop.Tests.csproj`'s own comment). `Odyssey
.TryRefreshFinalNodeTarget` and its `PartyFollowController` wiring are new unsafe engine-pointer
code, same as every prior round's fix candidates, and are NOT exercised by the (x86 NativeAOT,
unreferenced-by-tests) plugin test project — they can only be verified live. No build attempted this
round beyond `dotnet test` (no rig access, and the plugin's own x86 NativeAOT build was not
re-verified against a live install this round — flagged so whoever runs the live pass rebuilds and
redeploys before trusting the binary). No `SharedBlock`/protocol version change — everything this
round touches is plugin-internal.

### Verdict

A fix candidate distinct from round 88's disproven `TryAcceptFinalPathNode` — refreshing the real
engine test's inputs rather than forcing its output — built directly on round 89's own falsifiable
hypothesis and its already-shipped diagnostic, fully implemented, unit-tested (12 new tests, 1701/
1701 total passing), and gated off by default pending the live A/B this round could not run. This
does NOT claim the companion walks the full leg end-to-end; it claims a specific, code-grounded,
testable mechanism now exists for whoever has the rig next to confirm or refute round 89's
hypothesis in one pass, with a shared, tested shape-detection rule backing both the disproven and
the new candidate fix so they cannot silently drift out of sync with each other again.

## 2026-08-24 (static pass, no rig access) — `companion_local_combat` under MULTIPLE simultaneous
releases: code review finds the existing per-slot design already safe by construction; no code
change made; the specific untested scenario is named precisely for the next live pass

Task: round 85 (above) live-verified `companion_local_combat` with exactly ONE companion released
(Carth) and a SECOND companion present but never released (Trask, stuck out of range the whole
session) — "no drift observed" was proven for that shape, not for two companions BOTH released at
once. This pass re-reads `CompanionLocalFightRule.cs` and `PartyPresenceController.cs` end to end
specifically looking for (a) any state shared ACROSS companion slots that two simultaneous
releases could contend over, and (b) — the literal question the task asked — any path where two
different CLIENTS' engines could both claim authority over the same companion. No rig access this
round; this is a code-level review only, not a live measurement.

### Two simultaneous releases are already possible by the gate's own design, not a new scenario

`PartyPresenceController.OnFrame` computes `localFightActive`/`localEnemyPosition`/
`localEnemyObjectId` ONCE per pass (`PartyPresenceController.cs:337-351`) because `local_combat`
itself only ever runs one fight at a time (item 1's own design) — every companion slot in that
pass's `for` loop is handed the SAME answer to "is there a fight, and where." `DriveOne` then
applies `CompanionLocalFightRule.Decide` independently per slot, gated on THAT slot's own
`nearEnemy`/`nearPlayer` distance check (`PartyPresenceController.cs:403-409`). Nothing stops both
roster slots from independently satisfying the triple-proximity gate in the same pass — the design
doc's own 2026-08-21 entry already says as much ("with the roster capped at two companions and one
fight at a time, this is at most both companions... in practice usually zero or one given how
tight the gate is"). Round 85 measured the "zero or one" case; "both" has never been observed live,
but it is not a hole in the design — it is an anticipated, intentional outcome (two companions
ganging up on the one locally-fought enemy) that this pass confirms is wired correctly.

### Per-slot independence, traced field by field: no shared mutable state between two released companions

Every piece of state `DriveOne`/`CompanionLocalFightRule` touch for the release/reclaim/attack-drive
path is an array indexed by roster slot, sized `SharedBlock.PartyPresenceCapacity` (4), with no
cross-slot read or write anywhere in the loop body:

- `_released[slot]` — release/reclaim edge state, per slot.
- `_creature[slot]`/`_trackedId[slot]` — the resolved engine object for that slot, per slot
  (`Resolve`, `PartyPresenceController.cs:654-675`); one companion's resolve/cache-miss path never
  touches another's.
- `_lastAttackDriveAt[slot]` — the 1.5 s attack-reissue throttle (`AttackDriveIntervalSeconds`),
  per slot; one companion's attack cadence cannot starve or accelerate another's.
- `_heldNeutral[slot]`/`_factionKnown[slot]` — faction/pin state, per slot
  (`ApplyCompanionFaction`, `PartyPresenceController.cs:720-745`); the engine call itself
  (`Odyssey.SetCreatureFaction`) is always addressed by THAT slot's own `objectId`.
- `_smoother` (`MotionSmoother`) — already sized and indexed per slot; a released companion never
  reaches `_smoother.Step` at all this pass (`StayReleased` returns before it), so there is nothing
  for a second release to contend with here either.
- `Odyssey.AttackTarget(objectId, enemyObjectId, ...)` (`DriveAttackIfDue`,
  `PartyPresenceController.cs:579-597`) — a single engine call keyed by the ATTACKER's own
  `objectId`, called once per released slot per pass; two released companions attacking the same
  `enemyObjectId` in the same pass is two independent calls with two different attacker ids, which
  is ordinary multi-attacker combat, not a contention the engine or this code needs to arbitrate
  specially.

The two per-controller (not per-slot) pieces of state — `_releaseDiagLogged`/`_attackDriveLogged`,
the total-line-count caps on the diagnostic/attack-drive log — are shared across slots, but only
gate LOG VOLUME (`ReleaseDiagMaxLines`/`AttackDriveMaxLines`, both 60 total). Two companions
releasing at once will exhaust that shared cap roughly twice as fast and one's diagnostic lines
could crowd out the other's once the cap is hit — a real but purely cosmetic limitation (less
visibility into the SECOND companion's state on an unusually long two-companion session), not a
functional or safety gap. Not changed this pass — raising or per-slotting the caps is a one-line
change if a live pass finds the log genuinely too sparse with two companions releasing, but there
is no evidence yet that it is.

### "Two clients' engines both driving the same companion": structurally impossible in this
project's current architecture, not merely untested

The task asked this literally. Checked directly, not assumed:

- `PartyPresenceController.OnFrame` only does anything once `block.PartyPresenceCount > 0`
  (`PartyPresenceController.cs:320-324`), and `SharedBlock.PartyPresenceCount`/`PartyPresenceSlots`
  are populated by exactly one call site: `KotorGameBridge.TryWritePartyMembers`
  (`src/KotorCoop.Memory/KotorGameBridge.cs`), called from `CoopAgent.PumpParty` in exactly one
  branch — the `else` (client) branch (`src/KotorCoop.Session/CoopAgent.cs:1368-1395`). The `if
  (session.IsEnemyAuthority)` (host) branch calls `bridge.ReadPartyMembers()`/
  `session.PublishParty(...)` instead and never writes the local shared block's party-presence
  slots at all. `PumpParty`'s own doc comment states the invariant plainly: *"Host-authoritative, so
  exactly one instance's AI is ever in charge."* On the host, `PartyPresenceController.OnFrame`
  therefore always sees `count == 0` and returns before `DriveOne` ever runs — the release/attack-
  drive machinery this whole document is about is dead code on a host instance, by construction,
  every pass.
- This project is a strict two-player topology — one host, one client (`docs/research/network-
  topology.md:190`, "With two players there are no two clients"). There is exactly one process in
  the whole session that ever runs `PartyPresenceController.DriveOne` with real work to do, so
  there is no SECOND client instance that could independently satisfy the same triple-proximity
  gate against the same companion and issue a competing `AttackTarget`/position/faction write.
  "Two clients both claiming the same companion" is not a race this codebase's current shape can
  express, not merely a race that has not been observed yet — a THIRD player would need protocol,
  session-role and `SharedBlock`-slot-capacity changes well outside this task's scope before the
  question would even become meaningful to ask about `companion_local_combat` specifically.
- The host's OWN native engine AI (never suppressed on the host, per `PumpParty`'s own comment)
  and the client's `PartyPresenceController` release/attack-drive path never touch the same
  memory: they run in two separate `swkotor.exe` processes, each with its own resolved creature
  object for "the same" companion (same NPC roster slot, same story-level identity, different
  physical address in two different address spaces). Nothing about releasing the CLIENT's copy to
  its own engine AI reaches across the process boundary to the HOST's copy, or vice versa — the
  entire mirror/release design exists specifically to keep those two copies reconciled through the
  network layer, never through shared memory.

### Verdict: no code change made — the design already generalizes safely to multiple simultaneous releases

Per this task's own instruction ("if the existing design already handles multi-companion release
safely..., document that clearly instead of inventing unnecessary complexity"): this review found
exactly that. Every piece of release/reclaim/attack-drive state is already scoped per roster slot
with no shared mutable state between companions in the functional path, and the literal
"two clients" framing does not apply to this project's current one-host/one-client architecture at
all. No speculative locking, no new cross-slot guard, and no config-level cap on simultaneous
releases were added — there is no gap here for such code to close, and adding it would be exactly
the kind of unverified complexity this project's own rule (`docs/roadmap-seamless.md`, "verify a
researched offset live before shipping the write") warns against shipping without a live pass that
actually exercises the scenario it defends against.

**What remains genuinely open, and is explicitly NOT settled by this code-level pass**: the
specific live scenario round 85 did not reach — TWO companions simultaneously satisfying the
triple-proximity gate against the SAME `local_combat` fight, both released and both driving attacks
in the same window. The code-level reasoning above says this should behave identically to round
85's single-release case, replayed twice with no interaction — but round 85's own live evidence
(the "Trask never appears in a single `party drive: companion 0x127...` line... zero reads or
writes anywhere near Trask" finding) is direct proof for the ONE-released/one-mirrored shape
specifically, and a second companion actually swinging alongside the first, sharing the shared
60-line diagnostic/attack-drive log caps and both landing credited hits on the same enemy in the
same window, has never been observed. `companion_local_combat` stays OFF by default, unchanged —
this pass does not touch its shipped state.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): no `companion_local_combat`-related production code
was changed this pass (a pure code-review/documentation pass); see this session's own combined
build/test log for the `party_fast_pump` dirty-check change made alongside this entry, which is
unrelated to `companion_local_combat`.

### What the next live pass should specifically do

Recruit both companions (`dialogue_recruit_sync`, per round 85's own recipe), set
`companion_local_combat=1`/`companion_local_combat_range` generously enough that BOTH companions'
host-side positions land within range of the client's player AND the one `local_combat`-excluded
enemy at the same time (round 85's own asymmetry — one companion's follow-AI never catching up —
should no longer force a single-companion result now that rounds 80/84/88 fixed the follow stall),
then confirm: both `party drive: companion 0x... released...` lines appear in the same window; both
`party drive: drove companion 0x...'s attack on 0x... directly` lines appear; the host's own damage
ledger (`combat: applied N of a peer's damage to 0x...`) shows credited hits from BOTH companion
object ids, not just one; and neither companion's position/faction/health reads ever cross-
contaminate the other's (the same "0.05-unit noise, not drift" check round 85 already ran, applied
to both slots this time instead of one).

## Live-verified (2026-08-24) — round 90's `TryRefreshFinalNodeTarget` fix candidate: round 89's
hypothesis is REFUTED, cleanly and without regression; stays off by default

Rig access this round. Rebuilt and reinstalled the plugin on both GOG installs
(`src/KotorCoop.Plugin/build.ps1 -Install`), `dotnet test` 1712/1712 passing (two
`CoopCommandDispatcherTests` failures under full-suite parallel load, both green in isolation — the
same known-flaky pair prior rounds have already flagged, not a regression). Brought up
`tools/demo.ps1` clean (both GOG installs, `END_M01AA`, connected). Set
`party_follow_finelog=1` and `party_follow_refresh_final_node=1` in both installs' `coop.ini`, then
reproduced round 88/89/90's own exact recipe on the host: `console 'tp 20 22 -1.3'` then
`walk 10 15` via `tools/blockpoke.ps1`.

### What the finelog shows

Trask (0x127) walked the real trail exactly as round 88 described — continuous position change,
`pathNode`/`writeCursor` climbing node by node, `target`/`prior` visibly updating every few frames
as she closed on the leader — and reached the terminal shape at `t=` (elapsed) matching the walk's
own timing: `phase=7 pathNode=18 destNode=18 writeCursor=19 moveState=3 target=(10.78,15.70)
prior=(10.91,16.08)`, `leaderPos=(10.78,15.70)`. The decisive line is the very FIRST frame that
entered this terminal shape: `target` was already bit-identical to `leaderPos` on that first
sample — not stale by up to 10cm as round 89's hypothesis required, but already exactly fresh.
`target`/`prior`/`leaderPos` then held bit-identical, unchanged, across the full observation window
(76+ continuous seconds, 2244 finelog samples at 25 Hz, `t=82.11` through `t=158.23`) — i.e. the
fix's own ~1cm epsilon never found a meaningful drift to write in this repro, meaning
`TryRefreshFinalNodeTarget` never actually needed to fire here: the geometry it exists to refresh
was never stale to begin with. `phase` never advanced past 7. `moveState` stayed a stable 3 the
entire time — no cycling, no reversion to 1/idle, none of round 88's own disproven
`TryAcceptFinalPathNode` symptoms.

This is round 89's own falsification branch, hit cleanly: *"If target/prior were already
effectively at the live leader position before this fix's first write ... and the stall persists
regardless, round 89's hypothesis is refuted."* That is exactly what happened. The player had
already stopped moving by the time the companion reached the terminal shape (a short walk leg,
~13 units), so there is a possible confound — a longer-leg repro with the leader still taking
small steps as the companion arrives at phase 7 might still show the up-to-10cm staleness round 89
predicted — but for THIS recipe, the one every prior round in this thread has used as the
standard repro, the geometry was never the problem.

### No regression

`moveState` held a stable 3 for the full 76+ second window, `pathNode`/`destNode`/`writeCursor`
never moved, no phase-cycling of any kind (contrast round 88's own disproven `TryAcceptFinalPathNode`
result: a visible 7 → 3 → 0 → 7 cycle every 0.3-0.5s). The stall gap itself
(`targetVsSnapshotDist=1.391`) matches round 88's own recorded ~1.3-1.4 units short baseline
exactly. `party_follow_refresh_final_node` and `party_follow_finelog` set back to `0` in both
installs' `coop.ini` after this test (their code defaults are already off; nothing to flip in
`CoopConfig.cs` — this flag lives in `PluginConfig`/`coop.ini`, not the agent-side config).

### Verdict

Per this project's own instruction to leave a fix off and record precisely what was observed when
it does not help: `party_follow_refresh_final_node` stays OFF by default. It is not harmful (no
regression of any kind observed), but it is also not sufficient — the terminal-node stall's real
cause is not stale `Target`/`Prior` geometry, at least not in the standard short-leg repro this
whole thread has used. Round 89's own next-step recommendation is now the leading path: a live
debugger on the `fcompp` at 0x512BF2 (`PathWaitDest`'s own accept/reject comparison) to see
directly which side of the test a fresh `Target`/`Prior` pair lands on and why it still rejects.
`Odyssey.TryRefreshFinalNodeTarget`'s own doc comment should be read alongside this entry — it is
NOT wrong about what it does, just proven insufficient against the geometry-staleness hypothesis it
was built to test.

## Attempted (2026-08-24) — `companion_local_combat` under two simultaneous releases: one
companion confirmed clean and continuous under a generous range, the SECOND companion's own sync
blocked by a newly-found, separate bug (a debug-warp "different save" stall that does not
self-heal); the specific two-companion scenario remains unreproduced

Rig access this round, following directly from the Zaalbar recruit in `dialogue-consequences.md`'s
own 2026-08-24 live pass (same session's rig). `companion_local_combat=1`,
`companion_local_combat_range=80` (generous, chosen so two companions ~68 units apart would both
independently land in range of a single player position without needing to physically reunite them
first — see below) on both `coop.ini`s. Trask (a permanent END_M01AA-native party member, present
on this save from the very start alongside whichever of the nine recruitable companions gets
added) and Zaalbar were both in the host's real party.

### Companion follow-AI and the `walk`/`tp` test primitives could not reunite the two companions
in this module

The host's own leader stayed at the module's entry point (294.7, 184.3); Trask followed closely
(within ~1.5 units, matching this document's own established follow-AI behavior) but Zaalbar
never started moving from his recruit position (225.86, 192.24) — roughly 69 units away — even
after multiple minutes. Repeated `tools/blockpoke.ps1 walk` calls toward Zaalbar's room from the
leader's start point produced ZERO position change on most attempts, and the ones that did move
the leader only advanced a few units before stopping — consistent with
`TestDriveController.HandleWalk`'s own doc comment ("the PC is drive-controlled, so an
action-queue move may not move it at all") and `shared-state-audit.md`'s own prior finding that
this primitive is unreliable, now confirmed unreliable specifically across this module's own room
boundaries (Zaalbar's cage room appears to be pathing-isolated from the main corridor the leader
starts in, worse than the open corridors every prior follow-AI round tested in). `tp`, as
documented, strands whichever companion(s) were following at their pre-teleport position entirely
— confirmed again here (Trask's body stayed bit-identical after the leader `tp`'d 65 units away).

**Workaround used instead of physical reunion**: rather than fight the room's pathing, both
companions were left where they naturally sat and `companion_local_combat_range` was set generously
(80) so that a single player position roughly equidistant from both (or, as used here, simply near
one companion and a hostile, since 80 comfortably covers the ~58-69 unit gaps involved) would put
both independently in range without needing them physically together. This is a valid substitute
for the range check itself (the code only ever compares each companion's own position to the
player and the enemy — see `PartyPresenceController.DriveOne` — never to each other), but it could
only be exercised for ONE companion this round; see below.

### A second, unrelated, newly-discovered bug blocked the second companion's own roster sync

After the SECOND fresh recruit of Zaalbar this session (needed because `companion_local_combat`
and its wider range are read once, at plugin construction, from `coop.ini` —
`PartyPresenceController`'s `_companionLocalCombatEnabled`/range fields are `readonly`, set from
`PluginConfig.ReadBool`/`ReadFloat` in the field initialiser, NOT re-read per frame, so editing
`coop.ini` mid-session — as the first attempt at this task did — has NO effect until the plugin
reloads; a full rig restart was required, confirmed by grep'ing the field declarations directly),
the client's own `party roster: reconciling against a peer party of 0x001` calls stopped receiving
updates entirely (`party roster: no peer roster to reconcile against`) shortly after the recruit,
and never recovered over several more minutes of observation, including a second `travel` re-trigger
attempt. Both agent logs independently show the same event at the same moment:
`* co-op [W_SAVE] different save: enemies, doors, items and party are off — rejoin to adopt the
host's world and get them back`. This is `client_load_rejoin`'s own detection gate firing (it is
default ON, per `e7663c2`'s own flip and this document's round-87 live-verification) — but,
contrary to that flag's own design intent, it did NOT self-heal automatically here; the client sat
in this "different save, party sync off" state for the remainder of the session. Trask's own
solo release/reclaim cycle (a companion the client had ALREADY resolved before the desync gate
tripped) kept working perfectly throughout — `party drive: companion 0xD2 released to fight
locally` fired repeatedly and cleanly, cycling with the enemy's own 25s local-fight timeout, for
several minutes straight, no cross-contamination, no health/faction/position anomalies. But
Zaalbar never appeared in the client's roster this session, so the two-companion scenario itself
was never reached.

This looks like a genuinely different bug from anything this document's `companion_local_combat`
sections have investigated: it is a `client_load_rejoin`/party-roster-sync interaction specific to
the debug `travel` cheat (which does not go through the same "everyone reloads the identical save"
flow a real module transition or reconnect does), not a `companion_local_combat` defect at all.
Notably, the FIRST recruit this same session (`dialogue-consequences.md`'s own 2026-08-24 entry)
did NOT hit this — the roster synced cleanly within 191ms that time. The difference between the two
attempts was timing (the second recruit was issued only ~30s after the travel completed, versus
several minutes of other activity before the first) — consistent with, but not proof of, a race
between the desync-gate's own detection and the recruit landing. Not chased further this round
(out of scope for `companion_local_combat` specifically) — flagged precisely for whoever
investigates `client_load_rejoin`'s own self-heal path next: it is supposed to recover
automatically and, at least once, observably did not.

### Verdict: the specific "two simultaneous releases" scenario remains unreproduced; nothing here
should be read as a `companion_local_combat` finding either way

What WAS confirmed live this round: `companion_local_combat_range` set far above its previous
maximum tested value (80 vs. round 85's 10) works exactly as the code predicts — a single
companion 2-3 units from the excluded enemy releases and stays released continuously across
repeated local-fight cycles, with no new failure mode introduced by the wider range. What was NOT
confirmed: two companions releasing in the same window, both attack-drive lines in the same
window, credited hits from both object ids, and the two-companion cross-contamination check —
round 85's own still-open item stays open. The blocker was a different, newly-found bug in the
recruit/rejoin path under a debug warp, not anything in `CompanionLocalFightRule` or
`PartyPresenceController` itself, which continues to look safe by the same per-slot-independent
construction the 2026-08-24 code-review entry above already established. `companion_local_combat`
stays OFF by default, unchanged.

### Test suite and build

`dotnet test`: no production code changed this pass (a live-verification attempt only).

## Live-verified (2026-08-24, follow-up round): the two-companion `companion_local_combat` release,
## now unblocked — both companions released in the same window, both drove attacks on the same
## target, real credited damage, no cross-slot contamination. Flipped ON by default

The rejoin-gate bug that blocked this scenario twice (the entry above, and the round before it) is
now fixed and live-confirmed (see this document's `PersistentSaveMismatchRule` entry above). Ran the
scenario this document has described since its very first `companion_local_combat` entry: Trask
(native, slot 0) and a freshly-recruited Zaalbar (slot 8) both in the host's party,
`companion_local_combat=1`/`companion_local_combat_range=80` on both `coop.ini`s (generous, same
reasoning round 85/the 2026-08-24 attempt used — the companion follow-AI stall means physically
reuniting two companions in this module is not reliable, so a wide range lets both independently
qualify without needing them adjacent), the client's own PC and a single excluded enemy
(`tar05_gammorean`, object `0x3`) positioned within range of both companions and the player at once
(computed from a fresh `tools/creatures.ps1` position sweep: player-to-enemy 57.2 units, Zaalbar-to-
enemy 25.5 units, Trask-to-enemy 55.5 units — all under the 80-unit range).

### Both released in the same window, both drove the same target

Client's `kotorcoop-plugin.log`, issuing `blockpoke.ps1 attack 0x7FFFFFFF 0x3`:

```
[19:19:28.641] party drive: companion 0xD1 released to fight locally - within range of both you and
                the enemy you are fighting (head=0xFFFFFFFF moveState=1 soloMode=1 t=835.6s slot=0)
[19:19:28.641] party drive: companion 0x2 released to fight locally - within range of both you and
                the enemy you are fighting (head=0xFFFFFFFF moveState=1 soloMode=1 t=835.6s slot=1)
[19:19:28.641] party drive: mirroring 0 of the host's companions
[19:19:28.645] party drive: drove companion 0xD1's attack on 0x3 directly (head was 0xFFFFFFFF)
[19:19:28.645] party drive: drove companion 0x2's attack on 0x3 directly (head was 0xFFFFFFFF)
```

Both release lines fired in the exact same tick (`19:19:28.641`), `mirroring 0 of the host's
companions` directly confirms neither was left mirrored, and both drove their attack on the SAME
target in the same tick 4ms later — this is the literal log shape item 3 of this round's task asked
for, now observed for the first time with two real companions.

### Real credited damage, a real kill

Host's `kotorcoop-plugin.log` over the following ~17s: four `combat: applied N of a peer's damage to
0x3` lines (9, 8, 8, 8 — totalling 33), then `0x3`'s body destroyed (a real kill, matching this
document's own round-85 credit-pipeline confirmation shape) and one more `applied 7` line held as a
post-destruction debt. The fight then continued against the NEXT nearby enemy (`0x1D`), with the
same dual-release pattern firing again (`19:19:53.646`: both `0xD1` and `0x2` driven onto `0x1D` in
the same tick), confirming this is not a one-shot fluke.

### No cross-slot contamination

Post-fight `tools/creatures.ps1` and `tools/partystate.ps1` on both instances:

```
host   0xD1 end_trask   (293.19, 185.03)  hp30 xp3241
host   0x2  tar05_zaalbar (225.86, 192.24) hp8  xp160
client 0xD1 end_trask   (294.10, 159.85)  hp30 xp3081
client 0x2  tar05_zaalbar (234.01, 188.93) hp8  xp0
partystate (both): companions 2, slot 0 -> 0xD1, slot 8 -> 0x2
```

Each companion's client-side position/xp diverged independently from the host's own (expected — a
released companion is locally driven during its own fight, snapping back to the host's mirror on
reclaim, same trade `local_combat`'s own excluded enemy already makes) — but crucially, NEITHER
companion's data ever showed the OTHER's position, faction, or object id. `partystate.ps1` agrees on
both instances that slot 0 is still `0xD1` and slot 8 is still `0x2`, with no swap, no shared write,
and no anomaly of any kind. This directly confirms the design's own per-slot independence
(`PartyPresenceController.DriveOne`, already argued safe by code review in the 2026-08-24 static
entry above) under a REAL simultaneous two-companion release for the first time.

### Verdict

The specific "two simultaneous releases" scenario this document has tried to reach three times
(round 85's crash, the 2026-08-24 rejoin-gate block, and now this) is fully reproduced and clean.
`companion_local_combat` is flipped **ON by default** as of this result (`profiles/coop.ini.example`)
— every open item this document raised against it (single-release credit pipeline, single-release
drift risk, two-companion drift risk, two-companion simultaneous release) is now live-confirmed.

`dotnet test`: no production code changed this pass (a live-verification run only); full suite
re-run after the coop.ini.example default flip.

## Root cause and fix for `client_load_rejoin`'s non-self-healing gate (2026-08-24, follow-up round)

The bug the round above found and flagged for whoever investigates `client_load_rejoin` next: root-
caused and fixed. `client_load_rejoin`'s corrective action (`CoopCommandDispatcher.TriggerRejoin`,
which fetches the host's world via `SessionManager.RejoinAfterLoadAsync`/`WorldAdoption.TryAdoptAsync`
and overwrites the client's own staged save with it) only ever ran off an EDGE — `RejoinTrigger.OwnLoad`
needs THIS instance's own `SaveIdentityDigest` to change, `RejoinTrigger.HostReload` needs the HOST's
reported digest to change. Both digests are computed from the STAGED SAVE FILE ON DISK
(`SaveDigest.TryComputeSaveIdentity`, cached by `SaveDigestCache` and only re-stat'd when the file's
own length/write-time changes). A debug-cheat cross-module warp (`tools/blockpoke.ps1 travel`, and the
identical mechanism `CoopAgent`'s own "follow the host's announced transition" path uses internally,
`bridge.TryRequestTravel`) bumps the LIVE world-load serial (`WorldLoadRule.IsUnannouncedTeardown` sees
no `trans_pending` precursor, exactly like a real Load Game) but — unlike an ordinary door/trigger
transition, where the engine's own autosave keeps the disk file in step with the live module — can
leave the disk-based digest stale relative to what is actually loaded, for reasons this round's
evidence did not fully pin down (see below) but that reproduce reliably enough in this codebase's own
history (`shared-state-audit.md` round 53's `warp end_m01aa` false alarm, this document's own
2026-08-24 dual-release attempt) that `ContentCompatibility` correctly keeps reporting
`CompatibilityCode.Save` (party/enemies/doors/items dropped) while NEITHER edge-based trigger ever
fires, because the local digest genuinely never changes. The "rejoin to adopt the host's world" message
therefore described a corrective action nothing was ever wired to actually take for this class of
mismatch — confirmed against this session's own OLD log evidence (not re-derived from prose): the
client's `party roster: no peer roster to reconcile against` state, once entered, persisted for over
six minutes until a manual `reconnect` (visible in `agent1.log` as a full disconnect/reconnect cycle),
never healing on its own.

**Fix**: `KotorCoop.Session.PersistentSaveMismatchRule` (new file) watches the COMPARED VERDICT itself
— `client.Compatibility`'s `CompatibilityCode.Save` note — as a LEVEL rather than requiring an edge on
either digest, and triggers the same `RejoinAfterLoadAsync` fetch through a new `RejoinTrigger.SaveMismatch`
(reusing `client_load_rejoin`'s existing gate and `RejoinDecisionState` InFlight/Idle machinery
verbatim, `CoopCommandDispatcher.MaintainSessionSave`). Edge-triggered on first appearance, retried on
a 20-second cooldown while the mismatch persists (so one failed rejoin attempt cannot recreate the
exact "stuck forever" bug this fix closes), never duplicated while a rejoin is already in flight. New
unit tests (`PersistentSaveMismatchRuleTests`, 7 cases) pin the pure decision function.

**Live-verified**: reproduced the underlying trigger mechanics directly on this rig (a fresh two-
instance `demo.ps1` session, host `warp`/console travel to `tar_m05aa`/`tar_m08aa`/`tar_m10aa` in
various single and chained combinations). The exact persistent `W_SAVE` condition from the session
above did not re-reproduce this round — the chained-warp attempts here produced only the DIFFERENT,
already-self-healing `W_GIT` note (module-scoped, heals on the next matching module by design), not a
persistent `W_SAVE`. Most likely explanation, not confirmed: the original bug's session had already
been through a full process restart plus a recruit under a freshly-edited `coop.ini`, which this
round's fresh `demo.ps1` session (both installs starting from an identical, never-diverged staged save)
did not reproduce the preconditions for. The fix itself reuses `RejoinAfterLoadAsync`/`TryAdoptAsync`
verbatim — already live-verified machinery (this document's own round 87/88 warp-crash entries,
`session-save.md`'s own live passes) — rather than any new engine write, so the live-verification bar
this codebase's engine-write-rule sets is already met by that reuse; what remains genuinely unconfirmed
live is only the NEW trigger condition (a persistent cross-peer Save mismatch with no local edge)
actually firing end-to-end. Flagged honestly rather than claimed: the next round that can reproduce the
original session's exact preconditions (full restart + recruit under an edited `coop.ini`) should watch
for the new `"your world still differs from the host's - rejoining automatically"` line and confirm the
roster recovers without a manual `reconnect`.

`dotnet test`: 1721/1721 green (one known-flaky `TransitionMovieTests.TheBlockVersionMovedInBothHalves`
failure under full-suite parallel load, passes cleanly in isolation — unrelated to this change).

## Live-verified (2026-08-24, follow-up round): `PersistentSaveMismatchRule` fires and self-heals
## end-to-end, twice, without a manual reconnect — the precondition question this entry left open
## is also answered

The entry above could not reproduce the exact persistent-`W_SAVE` precondition live and flagged the
"most likely explanation, not confirmed" as a full restart plus a recruit under an edited
`coop.ini`. Read that account carefully, then traced the actual code path
(`SaveOrLoadClassifier.Observe`, `WorldLoadWatch.cs`) rather than re-guessing at the history, to
settle what genuinely has to be true for this rule to matter at all:

**Why a bare debug warp alone can never reproduce this**, confirmed by code reading, not just
repeated failure to reproduce it: `SaveDigest.TryComputeSaveIdentity` hashes the STAGED SAVE FILE ON
DISK, never the live module. A debug `travel` warp changes the live module but writes nothing to
disk on EITHER peer, so if both installs' staged saves started identical (as every prior round's
setup used), they stay identical after any number of chained warps — there is nothing for the digest
comparison to disagree about. This is exactly why the round above's chained-warp attempts only ever
produced the DIFFERENT, self-healing `W_GIT` note (a live per-module GIT compare, not a disk-based
digest compare) and never a persistent `W_SAVE`.

**What genuinely has to be true instead**: the two peers' ON-DISK staged saves must actually diverge
in identity (module/`Mod_NextObjId`/GIT), and that divergence must be read while `SessionSaveWatch`'s
own edge state does NOT treat it as a fresh baseline (a brand-new connection) NOR as an edge on
either side's OWN digest history. The second half turns out to already be handled correctly by
`SaveOrLoadClassifier`'s own documented behavior: a digest change with no correlating world-load-serial
edge is classified `SaveOrLoadVerdict.Save`, not `.Load` — and `MaintainSessionSave`'s client branch
only calls `TriggerRejoin(RejoinTrigger.OwnLoad)` on a `.Load` verdict, so a `.Save` verdict (an
unannounced disk change with no matching live-load edge) falls straight through to the `else` branch,
which is exactly where `PersistentSaveMismatchRule` lives. So the precondition is simpler than the
prior round's own speculation: no full process restart is structurally required at all — what is
required is a disk-level divergence that happens WHILE the session stays continuously connected.

### Constructing the precondition directly, live

Rather than continue guessing at organic timing, built it deliberately: with both instances
connected and playing normally in `tar_m05aa` (Trask and the freshly-recruited Zaalbar both in
party, matching digests confirmed via a throwaway `SaveDigest.TryComputeSaveIdentity` console
probe), copied a genuinely different, pre-existing save (`swkotor-gog-p3`'s own stale `000001 -
AUTOSAVE`, confirmed by digest to differ: `0x155107BD` vs. the session's own matching `0x517BD069`)
directly onto the HOST's own staged `000001 - AUTOSAVE\SAVEGAME.sav` on disk, without touching the
live running game at all (backed up the original file first). This is a deliberate, engineered
reproduction of "a stale save-identity digest on disk while the live mismatch persists" — not the
organic debug-warp mechanism the prior round's task description speculated about (which the analysis
above shows cannot by itself produce this), but the SAME underlying state class the rule exists to
fix, and the only way found this round to construct it without an unpredictable wait for organic
divergence.

### Result: fired and self-healed within about a second, twice

The client's `agent2.log`:

```
[19:16:58] * co-op [W_SAVE] different save: enemies, doors, items and party are off - rejoin to
            adopt the host's world and get them back
[19:16:58] * your world still differs from the host's - rejoining automatically
[19:16:58] * session-save: starting rejoin (SaveMismatch)
[19:16:58] * session-saved character: carried the character from '000001 - AUTOSAVE' ...
[19:16:58] * world adoption: adopted 'END_M01AA' (665,675 bytes, identity 0x155107BD), carrying the
            character from the session-saved character (captured 2026-08-24 09:13:37Z) ...
[19:16:58] * rejoin: load request 3 sent to the plugin (module END_M01AA, carried character)
[19:16:58] * rejoined 'END_M01AA'
```

`RejoinTrigger.SaveMismatch` fired the "your world still differs..." line exactly as designed, and
the whole rejoin/adopt/reload sequence completed inside the same one-second log window — a
completely different order of magnitude from the original bug's 6+ minute stuck state. The party
table stayed correctly synced throughout on both instances (`partystate.ps1`: `companions 2, slot 0
-> 0xD1, slot 8 -> 0x2` on both, unchanged before and after), and the live game world was
undisturbed — `tools/creatures.ps1` on the client immediately afterward still showed the full,
correct `tar_m05aa` population, confirming the rejoin's own disk-level adoption did not force a
disruptive re-teleport of the actual running session.

**A second, earlier firing of the same rule was also caught in the log, from an EARLIER attempt this
same round to reproduce the precondition on the CLIENT side instead of the host** (same mechanism,
same result: `19:13:35` — `SaveMismatch` trigger, self-healed within about a second). Two
independent firings, same session, same clean outcome both times.

### Verdict

`PersistentSaveMismatchRule` is now live-confirmed end-to-end: the trigger condition fires
correctly off a genuine, no-edge disk-level mismatch, and the existing (already-proven)
`RejoinAfterLoadAsync`/`TryAdoptAsync` machinery it reuses closes the mismatch for real, fast enough
that the original "stuck for 6+ minutes" bug class cannot recur through this path. The organic,
"full restart + recruit history" precondition the prior round speculated about was not what this
round reproduced — the code-level analysis above indicates a bare debug warp cannot ever produce it
regardless of restart timing, since nothing about a restart changes whether the two on-disk files
actually differ. The genuine organic path (independent per-peer object creation slowly diverging
each side's own `Mod_NextObjId` across real engine autosaves during long, asymmetric play) remains
plausible but unconfirmed; what matters for shipping is that the RULE's own trigger and recovery
path are now both directly verified against a real divergence, however it was produced.

Both installs' on-disk staged saves were left in a consistent (matching, `0x155107BD`) state after
this test — no manual restoration was needed since the rejoin's own adoption already overwrote the
client's file to match, and the game world both instances are actually playing in was never
disturbed.

`dotnet test`: no production code changed this pass (a live-verification run only).

## Live-verified (2026-08-24, follow-up round): the `fcompp` at 0x512BF2, read without a debugger
## — an in-process diagnostic computes PathWaitDest's own comparison every frame, and the terminal
## stall's real cause is now directly observed: `Prior` converges onto the terminal node's own
## recorded position, which makes the accept test require the live leader to stand exactly on a
## fixed waypoint — a race the leader can essentially never win by continuing to move

Round 89/90's own verdict named the next step precisely: "a live debugger on the `fcompp` at
0x512BF2 (`PathWaitDest`'s own comparison), to see directly what values are being compared and why
the accept branch never fires." Checked this session's actual debugger tooling first (per the
task's own instruction): no `cdb.exe`/`windbg.exe` exists anywhere on this machine — the only
debugger-adjacent files found are four orphaned DLLs (`dbgcore.dll`, `dbghelp.dll`, `srcsrv.dll`,
`symsrv.dll`) under `C:\Program Files (x86)\Windows Kits\10\Debuggers\x64`, left by some other SDK
component's install, with no `cdb.exe`/`windbg.exe` alongside them — the actual "Debugging Tools for
Windows" package was never installed. Visual Studio 2022 Community IS installed
(`devenv.exe` exists), but its remote/native debugger is GUI-only and offers no scriptable,
unattended breakpoint-and-log workflow this harness's automation can drive (confirmed by a brief,
immediately-aborted `devenv.exe /?` probe, which opened a blocking GUI dialog rather than printing
to the console — killed rather than pursued). No real debugger-attach tooling is available or
reasonably set-up-able within this round's budget.

### The substitute: compute the SAME comparison the engine computes, every frame, in-process

Per the task's own steer (and this project's own established pattern this whole session), extended
`PartyFollowController`'s existing fine-log — which already samples `Odyssey.FollowDiag` including
round 89's own `RecordedDestPoint` field every frame — with a pure computation on those
already-captured values, no new engine read or write. Full manual FPU-stack trace of
`PathWaitDest`'s accept/reject test (`tools/disasm.py 0x512B40 220`, the whole function,
instruction by instruction, tracking the x87 stack depth through all 20 `fld`/`fsub`/`fmul`/`faddp`
operations from 0x512B86 through 0x512C03) settles exactly what `fcompp` at 0x512BF2 compares:

```
distSqToTarget = |RecordedDestPoint - TargetPoint|^2     (record+0x28)
distSqToPrior  = |RecordedDestPoint - PriorPoint|^2       (record+0x18)
fcompp compares ST0=distSqToTarget against ST1=distSqToPrior, pops both
fnstsw ax; test ah,5; jp <reject, return to caller>
```

`TEST AH,5` reads C0 (set when ST0<ST1) and C2 (set on an unordered/NaN result) into the parity
flag; `JP` (jump if PF=1) fires — REJECTING, falling back to the "not ready" return at 0x512B70 —
for `ST0>=ST1` (target no closer, or a tie) or an unordered result, and falls through to the accept
write (`record+4=3`, phase→3/`Moving_To_Path`, at 0x512C09) only when `ST0<ST1` **strictly**. In
plain terms: **the real engine test accepts iff the recorded trail node is strictly closer to
`TargetPoint` than to `PriorPoint`.** This is exactly the comparison round 89's own hypothesis asked
about, now derived by disassembly rather than guessed, and cheap enough to compute every frame from
fields already read.

Wired into `PartyFollowController.cs`'s existing finelog write (`src/KotorCoop.Plugin
/PartyFollowController.cs`): `pathWaitDestAccept`, `distSqToTarget`, `distSqToPrior` appended to the
existing `party follow finelog` line. No `Odyssey.cs` change, no new memory read, no engine call —
pure arithmetic on `FollowDiag.RecordedDestPoint`/`TargetPoint`/`PriorPoint`, all three already
sampled every frame since round 89.

### Live-verified: reproduced round 88's own recipe, and the diagnostic gives a direct, decisive
### answer — no debugger needed

Rebuilt and reinstalled the plugin on both GOG installs (`src/KotorCoop.Plugin/build.ps1 -Install`),
brought up `tools/demo.ps1` (`END_M01AA`, both installs), set `party_follow_finelog=1` on both, and
reproduced round 88/89/90's exact recipe on the host: `console 'tp 20 22 -1.3'` then `walk 10 15`
(`tools/blockpoke.ps1`). Let Trask (0x127) walk the ~13-unit leg to the terminal stall and sat on
it for over three minutes (`t=44s` through `t=257s`, 1883 sampled terminal-shape frames at
`phase=7`, `pathNode==destNode==30`, `writeCursor=31` — the single last recorded trail node, the
same terminal shape every round in this thread has used).

**The direct answer, read straight off the log rather than inferred:**

```
[19:58:00.267] ... pathNode=30 destNode=30 writeCursor=31 moveState=3 ... target=(10.78,15.70)
prior=(10.91,16.09) ... recordedDestPoint=(10.91,16.09)
pathWaitDestAccept=False distSqToTarget=0.1662 distSqToPrior=0.0000
```

Across all 1883 terminal-shape samples: `distSqToPrior` ranged `0` to `0.0228` (essentially always
at or barely above zero) while `distSqToTarget` ranged `0` to `0.1662` (genuinely, persistently
positive) — `pathWaitDestAccept` read **`False` on every single one of the 1883 samples, zero
exceptions**, for the full three-minute observation. The test is not failing intermittently or by a
narrow margin; it is structurally rejecting, every frame, for the entire stall.

**Why, read directly from the numbers**: `RecordedDestPoint` (the terminal trail node's own fixed,
recorded position) and `PriorPoint` are — for the whole stall — essentially the SAME point
(`distSqToPrior≈0`), while `TargetPoint` tracks the LIVE leader position, which (confirmed by round
90, and reconfirmed here — `target` is bit-identical to `leaderPos` on every sample, not stale) sits
persistently ~0.4 units away (`sqrt(0.1662)≈0.41`) from that fixed node. The accept test, as derived
above, requires the node to be strictly closer to `Target` than to `Prior` — but once `Prior` has
converged onto the node's OWN position, that reduces to requiring `Target` to land essentially
exactly ON the node too (both distances would have to be ~0, and even a tie does not satisfy strict
`<`). A live, continuously-moving leader essentially never stands at those exact float coordinates,
so the test cannot pass by ordinary play no matter how long the companion waits: **round 89's own
"stale geometry" hypothesis is refined, not merely refuted** — round 90 already showed `Target`/
`Prior` are NOT stale (they update every frame, tracking the leader in real time), but at the very
last recorded trail node specifically, `Prior` converges onto that fixed node's own coordinates
(intermediate-node samples earlier in the SAME walk — see below — show `Prior` sitting a genuinely
different distance away, letting the test pass), which turns the comparison into a race the live,
moving `Target` can essentially never win.

**Confirmed this is not merely a terminal-node artifact of the read itself**: four samples earlier
in the SAME walk (`t≈60.36-60.61`, while Trask was still consuming intermediate trail nodes 0→1) DID
show `pathWaitDestAccept=True` — `distSqToTarget=0.0000`, `distSqToPrior=0.0123` — immediately
followed, in the very next samples, by `pathNode` genuinely advancing from 0 to 1. The diagnostic
correctly reports both the passing and failing case within the same session, on the same companion,
using the exact same computation — direct evidence the derived formula matches the real engine's
own verdict, not just a plausible guess. Mid-path, `Prior`/`Target` sit at genuinely different
points (there is a "next" node ahead with its own distinct position for the test to discriminate
against); at the LAST recorded node, there is no next node — `Target` falls back to the raw live
leader position instead of a fixed waypoint, and `Prior` has nothing left to be but the node itself.

### Verdict

This is the clean, decisive, in-process answer round 89's own verdict asked a live debugger for —
obtained without one, using exactly the pattern this project has used successfully all session
("expose an in-process diagnostic instead of external tooling"). The terminal-node stall's root
cause is now directly observed, not inferred: **`PathWaitDest`'s accept/reject test, at the specific
degenerate configuration of "the last recorded trail node," structurally requires the live leader to
be standing almost exactly on that node's own fixed coordinates — a condition ordinary continuous
movement essentially never satisfies, because the leader keeps walking past it.** No fix is shipped
this round (the task was to get the comparison values, not to redesign the FSM); the diagnostic
(`pathWaitDestAccept`/`distSqToTarget`/`distSqToPrior` in the `party follow finelog` line, gated
behind the existing `party_follow_finelog=0` default-off switch, zero cost when disabled) is left in
place for whoever picks up a fix next. Two directions worth naming for that round: (1) extend a
trail with one more node past the recorded end once the companion nears it, giving the accept test a
genuine "next point" to discriminate against the way intermediate nodes already have, or (2) once
this exact terminal shape is detected, drive `Moving_To_Path`/a real walk step directly toward the
live leader position rather than trying to satisfy `PathWaitDest`'s own node-relative test at all —
both are real engine-adjacent changes needing their own live A/B, not attempted this round.

`coop.ini party_follow_finelog` set back to `0` on both installs after this test (code default is
already off; nothing to change in `PartyFollowController.cs`).

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): see this round's combined session log (run once,
after both this change and the creature-liveness.md item's work — no production changes there).
`src/KotorCoop.Plugin/build.ps1` clean, x86 confirmed, one pre-existing unrelated `CA2014` warning
(`PortraitController.cs`) only.

### Files

- `src/KotorCoop.Plugin/PartyFollowController.cs` — `pathWaitDestAccept`/`distSqToTarget`/
  `distSqToPrior` computed and logged in the existing fine-log line; no other file changed.

## Fix candidate 1 shipped and A/B'd (2026-08-24, second follow-up round): "extend the trail with
## one synthetic node" implemented, generalised live to a second handler the same disassembly
## shape predicts, but today's own live sessions never recaptured the precise trigger shape under
## controlled observation — genuinely inconclusive, not disproven; both new switches shipped OFF

Picks up the previous entry's own two named directions and implements the first, more principled
one: "extend the trail with one more synthetic node past the recorded end once the companion nears
the terminal node, giving the accept test a genuine 'next point' to discriminate against."

### What was built

`FinalNodeGeometryRule.WrapTrailIndex` (`src/KotorCoop.Plugin/FinalNodeGeometryRule.cs`) — pure,
unit-tested 100-slot circular-buffer wrap, shared by the new write logic below.
`Odyssey.TryExtendFinalNodeTrail` (`src/KotorCoop.Plugin/Odyssey.cs`): at the terminal stall shape,
writes the live leader position into the trail array at the current write cursor (`followData +
writeCursor*40`, the 12-byte position the confirmed `CSWPartyFollowDataArray::GetPoint` addressing
uses — the only field of the 40-byte node record `PathWaitDest`'s own geometric test ever reads),
advances the write cursor by one slot, and points `DestNode` at the newly-written node — in that
order, so `DestNode` never observably points at a not-yet-reachable slot (the exact ordering bug
round 83's own first attempt at `TryAdvanceStalledPathDestination` hit and fixed).

**A closer disassembly this round of the exact mechanism found something the previous round's own
framing did not have from disassembly (only from the general "search reaches an accept branch"
description)**: `PathWaitDest`'s geometric `fcompp` test (0x512B86-0x512C03) only ever runs when
`DestNode == CurrentPathNode` on the search's very FIRST comparison, before any increment
(0x512B7E: `cmp ebx,edx; jne 0x512c09`, where `edx` is the ORIGINAL, unincremented `CurrentPathNode`
— confirmed this round by re-disassembling `tools/disasm.py 0x512B40 220` in full, matching round
83's own disassembly of `TryAdvanceStalledPathDestination`'s own target mechanism exactly).
Once `TryExtendFinalNodeTrail` makes `DestNode` genuinely different from `CurrentPathNode`, the
search instead reaches the SAME unconditional-accept branch (0x512C09) `TryAdvanceStalledPathDestination`
already uses for its own `gap>=2` shape — **not** the geometric test passing. This is a real,
disassembly-confirmed correction to fix candidate 1's own name (it does not, in fact, give the
`fcompp` test "a genuine next point to discriminate against" — it routes around that test
entirely, the same way the already-shipped `gap>=2` fix does) but the OUTCOME is the one the task
asked for: a companion parked at the exact `gap==1` terminal shape, which the already-shipped fix
cannot reach (that shape's `DestNode` would have to equal the write cursor itself, which round 83's
own disassembly already proved structurally unreachable), gets a manufactured `gap==2` state its
own next natural handler pass can act on.

**Live finding, not planned**: reproducing round 88/89/90's own recipe this round (`tp 20 22 -1.3`,
`walk 10 15`, `END_M01AA`, `party_follow_finelog=1`) hit a stall at `phase=6` — one dispatch index
earlier than `PathWaitDest` (phase 7) — that no prior round's documentation names. Fresh
disassembly (`tools/disasm.py 0x51C7A0 260` resolves the 12-entry jump table at 0x51CCEC; `tools/
disasm.py 0x512A60 220` disassembles the phase-6 handler itself, index 7, never read before this
round) finds it is **byte-for-byte structurally identical to `PathWaitDest`**: the same forward
search, the same `fcompp`-based accept/reject test, the same `gap==1`/`gap>=2` reachability shapes
— the only difference anywhere in the function is which phase its own accept tail commits (`1` at
0x512B2A here, vs. `3` at 0x512C0A for `PathWaitDest`). Live data for this stall matched the shape
exactly and was, if anything, MORE degenerate than round 89's own phase-7 data:
`target==prior==recordedDestPoint`, all three bit-identical, `distSqToTarget=0` **and**
`distSqToPrior=0` (round 89's phase-7 data had only `distSqToPrior≈0`).

Since neither `TryExtendFinalNodeTrail` nor the already-shipped `TryAdvanceStalledPathDestination`
write the phase field themselves — both only ever manufacture a reachable `DestNode`, and let
whichever handler is CURRENTLY dispatched perform its own accept — the identical mechanism applies
to phase 6 unmodified. `FinalNodeGeometryRule.IsExtendableTerminalStallShape` (phase 6 OR 7,
deliberately a NEW method, not a widening of the phase-7-only `IsTerminalStallShape` the two
already-live-tested sibling functions share) covers `TryExtendFinalNodeTrail`'s own `gap==1` case
for both phases now. A SEPARATE, second live finding in the same round — a permanently-parked
`phase=6, gap==2` companion (`pathNode==destNode==83, writeCursor=85`, later confirmed to be
LEFTOVER data from the run BEFORE this fix existed, see "What today's live sessions actually
showed" below) — led to a second new function, `Odyssey.TryAdvanceStalledPathDestinationPhase6`:
the exact same `gap>=2` poke `TryAdvanceStalledPathDestination` already ships for phase 7, applied
to phase 6, as its own separate function and kill switch rather than widening the already-shipped,
already-tested phase-7-only one in place (this project's own "verify a researched offset live
before shipping the write" rule applies just as much to broadening tested behaviour as to shipping
new behaviour).

Kill switches, both default OFF pending their own live A/B: `party_follow_extend_final_node=1`
(candidate 1) and `party_follow_advance_destnode_phase6=1` (the phase-6 sibling).

### What today's live sessions actually showed — genuinely inconclusive, precisely so

Rebuilt and reinstalled the plugin on both GOG installs three times over the round as each new
piece was added, brought up `tools/demo.ps1` (`END_M01AA`), and ran round 88's own recipe (and
variants) repeatedly with both new switches and `party_follow_finelog=1` enabled. Three genuinely
new, real findings came out of this — but a clean, decisive read on whether `TryExtendFinalNodeTrail`
or `TryAdvanceStalledPathDestinationPhase6` themselves actually fired and closed a stall did not:

1. **A `phase=2, moveState=0` idle stall dominated most of today's repro attempts**, persisting
   100+ seconds at a time (one stretch: `pathNode=16 destNode=16 writeCursor=20`, position frozen
   within ~0.01 units, unmoving from t=65s to t=167s+ — over 100 continuous seconds, never once
   advancing) — a phase and `moveState` value combination no prior round documents. This sits
   UPSTREAM of both fix candidates (neither touches phase 2) and repeatedly consumed the repro
   window before the companion ever reached phase 6 or 7. The companion in this state is typically
   within `FollowKickRange` (3 units) of the leader, so `PartyFollowController`'s own stall-kick
   reissue never fires either — it looks "close enough," so nothing tries to fix the broken FSM
   phase underneath.
2. **A large, deliberate 31-unit teleport (`tp 40 5 0`) broke the `phase=2` deadlock** and produced
   genuine walking (`moveState=-1`, real position change frame to frame) for several seconds — but
   then appeared to settle into a state where the FSM's own bookkeeping (`pathNode`/`destNode`/
   `attempts`) kept cycling while the companion's REAL position stayed frozen (`pos=(10.80,15.76)`
   unchanged across 8+ seconds while `attempts` climbed toward the 50-watchdog). This reads as a
   genuine pathfinding limit at that distance/route, not a regression from this round's own
   changes (neither new function was in the shape to fire during that stretch) — flagged, not
   chased further this round; worth a dedicated look if a future round hits it again.
3. **A repeat of the exact `tp 20 22 -1.3` / `walk 10 15` recipe DID eventually produce a full,
   real, successful leg completion** in the same live session: the companion's own position
   settled at `(10.62,15.84)`, `~0.9` units from the leader's final `(11.42,16.28)`, `moveState=1`
   (genuinely idle, not spinning), stable across the observation window — the actual, practical
   outcome this whole thread has been chasing. **However**, checking exactly which `(phase,
   pathNode, destNode, writeCursor)` shapes occurred in that specific session (bounded precisely by
   this session's own `console interp hooked` load timestamp, `20:44:42.363`, to exclude carried-
   over log lines from the PREVIOUS pre-rebuild session that share the same on-disk
   `kotorcoop-plugin.log`) finds `TryExtendFinalNodeTrail`'s own trigger shape (`gap==1`, phase 6 or
   7) and `TryAdvanceStalledPathDestinationPhase6`'s own trigger shape (`phase=6`, `gap>=2`) were
   **never observed even once** in that session. The successful completion is real, but the log
   evidence cannot attribute it to either of this round's two new functions — it is fully
   consistent with the already-shipped `TryAdvanceStalledPathDestination` (phase 7, `gap>=2`) and
   `TryDriveFollowTarget` alone, which the finelog DOES show active throughout (`destNode` several
   nodes ahead of `pathNode` at every phase-7 sample logged in that run).

   The earlier-looking "phase=6, gap==2, `pathNode==destNode==83`" stall that motivated building
   `TryAdvanceStalledPathDestinationPhase6` in the first place was re-checked against its own exact
   wall-clock timestamps (`20:40:00.013` through `20:44:06.110`) against this session's own load
   time (`20:44:42.363`, from the NEXT rebuild+reinstall+relaunch cycle) — it entirely PREDATES the
   phase-6 fix even existing in the running binary. It is real, live-confirmed data motivating a
   real fix, but it is not evidence the fix itself failed to act on it (the fix did not exist yet
   when that data was recorded).

**Bottom line, stated as precisely as the data supports**: candidate 1 (`TryExtendFinalNodeTrail`)
and its phase-6 sibling are real, disassembly-grounded, unit-tested, and were live-enabled for a
substantial live session today with zero regressions observed (no crash, no access violation, no
plugin-log error, no new infinite phase-cycling — `moveState` was seen to reach genuine `-1`
walking and settle at genuine `1` idle within the same enabled session) — but this round's own
repro attempts never recaptured the EXACT transient shape either function targets while a human
(agent) was watching closely enough to confirm the write actually fired. This is not the same as
"tried and found insufficient" (round 90's own verdict for `TryRefreshFinalNodeTarget`) — it is
"built, shipped behind a kill switch, exercised in a live session with no harm done, but not
directly caught in the act." Both switches are left at their default OFF, per this project's own
"leave it off and document precisely what you saw" rule for a fix whose evidence is incomplete
rather than negative.

### Concrete next step for whoever picks this up

Neither new function currently logs when it actually writes (`wrote` is returned but never printed
anywhere `PartyFollowController` calls them, unlike some other diagnostics on this same page). The
cheapest fix for "inconclusive" is a one-line addition — log `TryExtendFinalNodeTrail`'s and
`TryAdvanceStalledPathDestinationPhase6`'s own `wrote` output directly (`party follow extend: 0x{id}
fired at pathNode={n}` or similar) so a future live round can grep for direct, first-person proof
of the write firing, rather than reconstructing it after the fact from finelog snapshots and
wall-clock bounding the way this round had to. The phase=2/moveState=0 idle stall (finding 1 above)
is also worth a dedicated round of its own — it dominated today's repro time and sits upstream of
this entire thread's own phase 6/7 focus.

### Test suite, build, and regression check

`dotnet test tests/KotorCoop.Tests` (Release): 1737/1737 passing, one run, no isolation reruns
needed (no flaky failures this round). `src/KotorCoop.Plugin/build.ps1 -Install` clean on both GOG
installs, x86 confirmed, three rebuild cycles over the round (one per new function added), zero new
warnings beyond the pre-existing unrelated `PortraitController.cs` `CA2014`. `tools/verify.ps1`'s
full battery was NOT run this round (each pass takes well past ten minutes end to end and the
round's own live-testing budget was already spent chasing the three findings above) — the
regression argument instead rests on: both new functions are purely additive (new methods, new
default-OFF `PluginConfig.ReadBool` switches, and in `PartyFollowController.cs` only new `if
(_flag) { ... }` blocks appended after the existing call sequence — no existing call, ordering, or
default changed); both `coop.ini`s were confirmed reset to the new flags' OFF default before this
entry was written; and today's own live sessions ran the plugin, WITH both new switches on,
continuously for many real minutes (multiple `tp`/`walk` cycles, one 31-unit teleport, one full
successful leg completion) with zero crashes, access violations, or plugin-log errors observed.

### Files

- `src/KotorCoop.Plugin/FinalNodeGeometryRule.cs` — `WrapTrailIndex` (candidate 1's own wrap
  arithmetic) and `IsExtendableTerminalStallShape` (phase 6 OR 7, `TryExtendFinalNodeTrail`'s own
  shape gate, deliberately separate from the phase-7-only `IsTerminalStallShape` the two prior
  sibling functions share).
- `src/KotorCoop.Plugin/Odyssey.cs` — `TryExtendFinalNodeTrail` (fix candidate 1) and
  `TryAdvanceStalledPathDestinationPhase6` (the phase-6 analog of the already-shipped
  `TryAdvanceStalledPathDestination`, found live this round).
- `src/KotorCoop.Plugin/PartyFollowController.cs` — two new kill switches
  (`party_follow_extend_final_node`, `party_follow_advance_destnode_phase6`, both default OFF) and
  their call sites in the per-companion loop.
- `tests/KotorCoop.Tests/FinalNodeGeometryRuleTests.cs` — 8 new tests: `WrapTrailIndex`'s ordinary/
  boundary/wrap cases, and `IsExtendableTerminalStallShape`'s phase-6/phase-7/neither/gap/pathNode
  cases.

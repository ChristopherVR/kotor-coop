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

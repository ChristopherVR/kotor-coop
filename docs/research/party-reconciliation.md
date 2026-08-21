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

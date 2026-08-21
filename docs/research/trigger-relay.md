# TriggerRelay — making a generic trigger's OnEnter fire for both players

Why a client walking through a story trigger left the host's world untouched, what the engine
actually does when a real player enters a `CSWSTrigger`, and what this pass built to close the
gap — parallel to `docs/research/client-encounters.md` throughout, because it is the same
mechanical problem (a phantom never generates the subarea-entered event a real player's
movement does) applied to a different object type with a different failure mode.

Everything below was read out of `swkotor.exe` (GOG 1.0.3, MD5
`06B34D1B8A1ECEFBAAD0BF5E26556C71`) with `tools/disasm.py`, `tools/xrefs.py` and `tools/q.py`,
or out of the shipped game data with `tools/kotorfiles.py`. **Nothing here has been checked on
a running game** — see §7 for exactly what to test.

Tag key: **VERIFIED** = read out of the binary or the game data · **INFERRED** = a reasonable
reading not independently confirmed a second way · **DESIGN** = a choice made here ·
**NEEDS-LIVE** = must be confirmed on the running game.

---

## 1. The gap

A generic trigger is a `CSWSTrigger` GIT placement whose `.utt` template carries a non-empty
`ScriptOnEnter` — the classic "walk into the room, the scene starts" — as opposed to a trap
(`TrapFlag = 1`) or an area transition (`LinkedToModule` set). Unlike an encounter, a client's
own generic triggers are **not** disarmed: nothing in this codebase turns them off, and the
client's own engine fires `ScriptOnEnter` correctly for the client's own real character. So
today, when a client walks through a story trigger, its script runs — once, on the client's
instance, exactly as vanilla KOTOR would.

The failure is narrower than encounters' but has the same root cause. Two shapes of script hit
it:

- **`GetEnteringObject() == GetFirstPC()`-gated.** The script only acts for the entering
  player. On the client's own instance that check passes (the client's real PC entered). On the
  HOST's instance, nothing ever enters that trigger at all — the client's presence there is a
  phantom, placed by a direct `CSWSObject::SetPosition` write, and (client-encounters.md §2,
  verified there for exactly the same reason) a phantom never runs the engine's own movement
  functions that generate the subarea-entered event. So the script never fires on the host,
  ever, regardless of the gate.
- **Object-id-consuming.** A story `k_p*_en` script commonly calls `CreateObject` (spawn a
  plot actor) or `DestroyObject` (remove one, or itself). If that only ever runs on the
  client's engine, the client's object-id counter advances or frees an id that the host's
  counter never does — the identical drift risk `DisableLocalEncounters` exists to prevent for
  encounter spawns (Handoff 4.5), just triggered by a script instead of `SpawnCreatureFromSpawnList`.

What is explicitly **not** broken and **not** this pass's problem: cutscene ACTORS already
replicate through presence (docs/research/enemy-materialisation.md's plot-flag path); a
dialogue the trigger starts is already covered by the one-conversation lock, AS LONG AS
somebody's engine actually calls `StartDialog` — which is exactly the case a
`GetFirstPC()`-gated trigger firing on neither instance's real PC breaks; and mid-module
`PlayMovie` (a script call with no transition involved) is a separate problem, addressed
separately and much less completely in §6.

## 2. What the engine does when a player walks in — VERIFIED

The mechanism through event 0xC (`CSWSCreature::HandleSubAreaStateChanges`) is identical to
encounters' (client-encounters.md §2): `CSWSArea::InSubAreas 0x004BF470` tests a moving
creature against every subarea object — triggers, doors, areas of effect and encounters share
one list — and a newly-entered one raises event 0xC, answered by the object's own
`EventHandler`. For a trigger that answer is `CSWSTrigger::EventHandler 0x0058F140`.

Read end to end (`tools/disasm.py 0x58F140 0x680`), the event-0xC branch does, in order:

1. **Cache the entering object.** `CSWCCMessageData::GetObjectID(0) 0x004D64C0` is called on
   the event immediately, before any branching, and the result is stored at
   `CSWSTrigger + 0x29C` — 0x0058F1FA-0x0058F203. Undocumented in the symbol database; named
   here by the same "cache who entered before firing anything" shape
   `CSWSEncounter::EventHandler` uses at its own undocumented `+0x2E0`
   (client-encounters.md §3).
2. **Decide what KIND of trigger this is**, by two runtime field reads, not by any
   `Type`/`TrapFlag` field surviving into the live object under those names:
   - `CExoString::GetLength 0x005E5790` on `linked_to_module` (`+0x238`) — non-empty means an
     AREA TRANSITION, and the branch that follows (0x0058F27A onward) handles the module warp,
     never touching a script at all.
   - a mine/trap-activity gate at `+0x2BC` (undocumented) — non-zero routes to
     `CSWSTrigger::OnEnterMine 0x0058D570` instead of the generic path.
3. **For the remaining case — not a transition, not mine-active — run OnEnter.** At
   0x0058F504-0x0058F517:
   ```
   eax = [esi+4]                          ; the trigger's OWN object id
   RunScript(Deref(VirtualMachinePtr), esi+0x24C, eax, 1)
   ```
   `esi+0x24C` is the `ScriptOnEnter` `CExoString` field, and `CVirtualMachine::RunScript
   0x005D0FC0` is the SAME dispatcher already proven live in this codebase for
   `CSWSDoor::OpenDoor`'s OnOpen call and `Odyssey.FireCreatureScriptSlot0x260` — a queue-based
   call, not synchronous, that runs the named script with `OBJECT_SELF` = the object whose
   field it was handed.

### `ScriptOnEnter` at `CSWSTrigger + 0x24C` — VERIFIED TWICE

1. `CSWSTrigger::LoadTrigger 0x0058DA80` assigns the GFF field labelled `"ScriptOnEnter"`
   (string literal at `0x00749A28`, found by byte search and confirmed by `tools/xrefs.py`
   landing inside `LoadTrigger`'s own range) into `[esi+0x24C]` at
   0x0058DBAD-0x0058DBBF — immediately after `ScriptHeartbeat` (`+0x244`) and immediately
   before `ScriptOnExit` (`+0x254`), each field 8 bytes apart (a `CExoString` is
   pointer+length).
2. `EventHandler` reads the SAME field back at 0x0058F50A, cited above.

### Why a phantom never triggers this by itself — VERIFIED (same mechanism as encounters)

`CSWSCreature::UpdateSubareasOnMoveTo` / `…OnJumpPosition` are called only from the engine's own
movement paths (`WalkUpdateLocation`, `DriveUpdateLocation`, `DoCombatStep`, the jump actions,
`CServerExoAppInternal::MovePlayerToArea`) — the exact same twelve call sites
client-encounters.md §2 already enumerates for encounters, because it is the same function on
the same object hierarchy (`CSWSCreature`), regardless of what kind of subarea it entered. A
phantom moved by `Odyssey.SetPhantomPosition` (a direct `SetPosition` write) generates none of
them. So no amount of phantom movement produces event 0xC for a trigger any more than it does
for an encounter — this is not a second, independently-verified fact, it is the SAME fact
client-encounters.md already established, reused here because it does not depend on which kind
of subarea object answers the event.

## 3. Why idempotence cannot be free here — VERIFIED (the difference from encounters)

`CSWSEncounter::SpawnIfAppropriate` gets free idempotence from a real engine field:
`started` is set on the first successful spawn and tested on entry (client-encounters.md §3).
`CSWSTrigger` has no equivalent. Nothing in `CSWSTrigger`'s 0x318-byte layout (per the symbol
database's `offsets` table plus everything read out of `LoadTrigger`/`EventHandler` this pass)
tracks "have I fired before" — a generic trigger's `OnEnter` fires EVERY time an eligible object
enters the polygon, exactly as it would for two different real players walking through in
vanilla KOTOR. The SCRIPT decides whether a repeat does anything (destroying itself, checking a
global), and that decision is compiled into an `.ncs` this codebase cannot read.

So `TriggerRelayStore` (`src/KotorCoop.Session/TriggerRelayStore.cs`) keeps its OWN per-module
"already asked for" set and marks a trigger fired the moment it hands out a request for it —
**DESIGN, optimistic**: before any confirmation the engine call actually ran, on the same
"NotArmed is deliberately silent, no retry" trade `EncounterController.Refuse` already accepts
for the sibling channel. The alternative is an unbounded retry loop chasing a transient refusal
(the trigger not yet resolved on a fresh module load, say); a request this pass never retries
costs one missed relay, once, which is the trade already made everywhere else uncertainty meets
idempotence in this codebase.

**⚠ What this does NOT solve.** If the HOST's own real PC independently walks through the same
trigger — its own engine fires `OnEnter` normally, outside this whole channel — this store has
no way to know that happened, and a later client report for the same id will still be relayed
and fired a SECOND time on the host, by this channel, on top of the host's own native fire.
Whether that is harmless depends entirely on the script (many `k_p*_en` scripts destroy
themselves or check a global on the way in, which absorbs a duplicate call for free; some do
not). This is the SAME "the second engine's run may be suppressed or may double-run depending
on ordering" risk the task that produced this pass named going in, and it is not fully solvable
without decompiling every `.ncs` this pattern could match — see §5's design note and §7 item 4.

## 4. Where `GetEnteringObject()` gets its answer — INFERRED

`FireTrigger` (`src/KotorCoop.Plugin/OdysseyTriggers.cs`) writes the requesting peer's phantom
id into `CSWSTrigger + 0x29C` before calling `RunScript`, on the strong analogy to
`CSWSEncounter`'s proven `+0x2E0` pattern (client-encounters.md §3: cache the entering object,
THEN run the code that uses it). The WRITE itself is VERIFIED — it is the field EventHandler's
own event-0xC handler writes, cited in §2 above. Whether the NWScript engine function backing
`GetEnteringObject()` actually reads this exact field, for a TRIGGER'S OnEnter specifically, has
not been independently confirmed a second way (unlike encounters, where the whole channel was
live-verified end to end on `tar_m03aa` — see client-encounters.md §7). This is the single
largest INFERRED claim in this pass and the first thing the live test in §7 should settle.

## 5. What ships

```
non-owner plugin  TriggerRelayController (watch)   which relay-eligible polygons contain the local player
     ↓ block 0x3B88/0x3B94
non-owner agent    CoopAgent.PumpTick (client half)  filters against the host's last-known fired set, publishes
     ↓ TriggerRelayPacket (PacketType 48), peer -> host
host session       TriggerRelayStore                fresh + same-module + deduped + FIRED-SET DEDUPED
     ↓ block 0x3BA4/0x3BAC
host plugin        TriggerRelayController (fire)     OdysseyTriggers.FireTrigger
     ↓
host engine        RunScript(vm, trigger+0x24C, triggerId, 1)   -- the SAME call EventHandler makes
     ↓ TriggerRelayPacket (PacketType 48), host -> all, PeerId == 0
every peer          watch stops re-reporting ids the host has already fired
```

`OdysseyTriggers.FireTrigger` is EventHandler's event-0xC generic-script branch, reproduced:

```csharp
[trigger + 0x29C] = enteringObjectId;       // cache the entering object, same as the engine
RunScript(vm, trigger + 0x24C, trigger's own id, 1);
```

**Deliberately NOT reproduced: the transition and mine/trap branches.** This channel only ever
calls `FireTrigger` on a trigger that already passed TWO independent gates before its id ever
left the reporting instance's watch — the coop.ini allowlist pattern matched against the
`ScriptOnEnter` resref (DESIGN, see below), AND a runtime re-check
(`OdysseyTriggers.IsRelayEligible`) of the SAME two fields `EventHandler` itself branches on
(`linked_to_module` length and the `+0x2BC` gate) — so a trap or a transition trigger is never
even a candidate, on either end of the wire.

### The allowlist — DESIGN

`trigger_relay_patterns` (default `k_p*_*`) is checked against the docs-derived inventory in
§8: it matches all 69 story-cutscene scripts across the nine early-game modules surveyed and
none of the 10 non-story generic scripts (`k_trg_partyinit`, `k_bant_trig`, `k_trg_stealth`) or
the 4 no-op zone-catalogue markers. This is the SAME kind of narrowing `player_only` suspension
was for encounters — a deliberate choice about what "counts" as the feature this channel exists
for, made once and checked against real data rather than asserted.

Guards, all in `TriggerRelayController` and `OdysseyTriggers`:

| Guard | Why |
| --- | --- |
| Verify-bytes on `InTrigger`'s prologue before any call, logged on refusal | the house rule — a mismatch disables the channel for the run rather than calling whatever is actually there |
| Vtable check (`0x00749A38`) on every id, every time | the request comes off the wire; an id that names a door must not be called as a trigger |
| `linked_to_module` length + `+0x2BC` gate re-checked at fire time, not just at watch time | defense in depth — the same two fields `EventHandler` itself branches on |
| coop.ini allowlist pattern checked at watch time, before an id is ever published | traps and transitions never leave the reporting instance even if the runtime gate above somehow missed one |
| Requesting peer must have a live phantom (`Active`, `Health > 0`) | mirrors `EncounterController.FindPhantom` exactly |
| Watch pass skipped during a local conversation | the dialogue system moves the participants; mirrors the encounter watch |
| Requests and the watch flag cleared by `SessionScopedBlockLevels` (`KotorGameBridge.cs`) | the 0m lesson: a level nothing refreshes is state that never goes away — this pass adds `trigger requests`/`trigger watch` to that list explicitly |
| Everything dropped on a module change, request serial re-latched | `TriggerRelayController.Invalidate`, mirrors the encounter controller |
| `trigger_relay=0` in coop.ini | kill switch, **default OFF** — unlike `client_encounters` (default ON, live-verified), this channel has no engine-level one-shot flag to lean on and has never run against the live game |

## 6. Mid-module movies — what is and is not wired up

The task that produced this pass asked for the minimal safe version of relaying a mid-module
`PlayMovie` (a script's own cinematic call, not a transition — those already replicate via
`TransitionMovies`, docs/research/cinematics.md). What shipped is the DECISION half only:

- `MovieRelayQueue.Decide` (`src/KotorCoop.Plugin/MovieRelayQueue.cs`) — pure, tested — answers
  `PlayNow` / `DeferToNextTransition` / `Ignore` for a receiving peer, given whether the
  announcement names its own module, whether a name survived parsing, whether the peer is idle,
  in dialogue, in combat, and whether the engine's own transition queue is already busy. `Defer`
  means queuing through the SAME `MovieStaging`/`ModeQueueOnly` path transition movies already
  ride — no new engine call.
- The DETECTION half — `CinematicWatch` capturing WHICH resref a mid-module `PlayMovie` call
  played — is **not implemented**. `CinematicWatch` today reads the move-to-module transition
  queue (`CClientExoAppInternal+0x2D8`) and the coarse `IsMoviePlaying`/stall signals; neither
  tells you the name of a movie played by a bare `PlayMovie(...)` NWScript call with no
  transition involved, because nothing persists that name anywhere `CinematicWatch` already
  reads. The likely target for a follow-up pass is the NWScript command dispatch entry for
  `ExecuteCommandPlayMovie` — findable the same way `CSWSTrigger::EventHandler` was found for
  this pass, via `tools/xrefs.py` on a string literal or a known caller — but locating and
  verify-byte-gating a NEW engine hook was judged out of scope for this pass's time budget
  against the risk of shipping an unverified hook into a codebase whose stated rule is "always
  verify a researched offset live before shipping the write."
- `movie_relay=0` in `coop.ini` is defined and documented as reserved: setting it to 1 today
  changes nothing, because nothing yet calls `MovieRelayQueue.Decide` from a live detection.

This is stated in full here, in the profile comment, and in the README, rather than left to be
discovered: a gate that exists but does nothing is a worse trap than no gate at all if it is not
said out loud.

## 7. NEEDS-LIVE — nothing here has run against the game yet

Unlike client-encounters.md, this document was written entirely from static RE and offline game
data; there has been no live pass. `trigger_relay=0` by default reflects that. In order of what
each answers:

1. **Trigger object ids match across instances.** Same argument as encounters
   (client-encounters.md §4: `CSWSArea::LoadGIT`'s fixed load order, TriggerList included) but
   not independently measured for THIS channel. `blockpoke triggers` on both sides with the
   same save loaded is the cheapest check.
2. **The watch sees the polygon and applies the allowlist.** Walk the client through a story
   trigger; its log should read `triggers: standing in 1 generic trigger(s), first 0x…`. Walk
   it through `k_bant_trig` or a zone-catalogue marker instead and confirm the log says
   nothing — that is the allowlist and the no-op/other-script exclusion both working.
3. **The host fires it.** Host log `triggers: fired OnEnter of 0x… for peer N (phantom 0x…)`.
4. **§4's INFERRED claim.** Whatever the trigger's script actually does (a bark, a global set,
   a spawn) should be OBSERVABLE on the host's own screen/world state after the fire. If nothing
   visible happens despite a "fired" log line, `GetEnteringObject()` is not reading
   `CSWSTrigger + 0x29C` the way this pass assumed, and the write is dead weight — the
   RunScript call still ran (proven by the encounter channel's identical call shape being
   live-verified elsewhere), but whatever the script does with `GetEnteringObject()` did not
   see the phantom.
5. **The double-fire risk named in §3.** With the host's own real PC AND a client both able to
   reach the same trigger, deliberately cross it with both to see whether the script's own
   idempotence (or lack of it) produces a visibly duplicated effect (two barks, a
   `CreateObject` spawning twice). This is the one risk this pass could not remove, only bound.
6. **The fired-set broadcast actually suppresses re-reporting.** After item 3 fires once, the
   client's log should stop reporting that trigger id on every subsequent watch pass while
   still standing in the polygon — confirms `GetFiredTriggerIds` filtering in `CoopAgent`.

### An exact live-test script

Standing recommendation, mirroring client-encounters.md §7's shape:

1. Host and client both load a save in `end_m01ab` (the corridor after the Endar Spire's
   opening cutscenes — `k_stlthawrd` / `k_trg_stealth` sits there too, useful as a NEGATIVE
   control since it is excluded by the default allowlist).
2. Set `trigger_relay=1` on both `coop.ini`s and restart the session (the gate is read once, at
   plugin load, like every other `PluginConfig.ReadBool` gate in this codebase).
3. On the CLIENT, use `pwsh tools/blockpoke.ps1 walk <x> <y>` to walk the local player's real
   PC through `end_killsith`'s polygon (`k_pend_killsith`, tag `end_killsith`) — coordinates
   from `python tools/kotorfiles.py gff "end_m01ab:m01ab.git" --path "TriggerList"`'s
   `Geometry` list, offset by the trigger's own `XPosition`/`YPosition`.
4. Watch the CLIENT's log for `triggers: standing in 1 generic trigger(s), first 0x…`.
5. Watch the HOST's log for `triggers: fired OnEnter of 0x… for peer N (phantom 0x…)` within a
   couple of ticks.
6. `pwsh tools/blockpoke.ps1 triggers` on the HOST should show `result: fired 1 refused 0`.
7. Confirm nothing repeats: leave the client standing in the polygon for several more seconds
   and confirm the host's `fired` counter does not climb past 1 and the client's watch log goes
   quiet (item 6 above).
8. For the mid-module movie decision only (§6): `MovieRelayQueueTests` already exercises the
   pure logic; there is no live step, because nothing calls it yet.

`end_m01aa` (the standard demo save's own module) has generic triggers too (14 of them, all
STORY — see §8) and works identically; `end_m01ab` is suggested first because it also has the
one negative-control script (`k_trg_stealth`) in the same module, so both the positive and
negative cases can be checked in one session without a second transition.

## 8. Inventory — the nine early-game modules

`END_M01AA`, `END_M01AB`, and every Taris exterior module from `TAR_M02AA` through `TAR_M03AA`
alphabetically (`TAR_M02AA`, `TAR_M02AB`, `TAR_M02AC`, `TAR_M02AD`, `TAR_M02AE`, `TAR_M02AF`,
`TAR_M03AA` — the later Taris modules `tar_m03ab`/`ad`/`ae`/`af`/`mg` are past the requested
range and not included). Read with `python tools/kotorfiles.py gff` against each module's
`.git` (`TriggerList`) and each placement's `.utt` template (resolved from the module's own
`_s.rim` first, then the global `chitin.key`, matching `game.module_read`'s own search order).

**83 generic-shaped trigger placements total.** Every one of them has `Type = 0` (not an area
transition, per the `.utt`'s own field) and `TrapFlag = 0` (not a trap) — this early-game slice
of the retail game keeps area transitions and traps out of `TriggerList` entirely in these nine
modules (transitions here are door-driven; the earliest traps appear later in the game), so
`Type`/`TrapFlag` did not need to filter anything in this particular table. That is a fact
about THIS SLICE of the game, not a guarantee — `IsRelayEligible`'s runtime gate (§5) is what
actually protects a later module with real transition/trap triggers in its `TriggerList`.

Classification is by `ScriptOnEnter`'s resref, heuristically: `k_pend_*` / `k_ptar_*` = STORY
(matches the default allowlist), a non-empty script that is neither = "generic (other)",
empty = "no-op". **These are heuristics, said plainly**: an `.ncs` cannot be decompiled by
anything in this toolchain, so "one-shot" is inferred from the script's NAME pattern and from
whether the SAME script name is shared by more than one placement (`ptar_homint` appears twice
in `tar_m02aa` alone, both `k_ptar_homint_en` — a script authored to be entered repeatedly, or
one whose own idempotence check absorbs the repeat, cannot be told apart from here), never
measured directly.

| module | resref | tag | OnEnter | OnExit | class |
|---|---|---|---|---|---|
| end_m01aa | end_trig02 | end_trig02 | k_pend_trig02 | - | STORY |
| end_m01aa | end_trig_expld01 | end_trig_expld01 | k_pend_explode | - | STORY |
| end_m01aa | end_trig_expld01 | end_trig_expld01 | k_pend_explode | - | STORY |
| end_m01aa | end_trig_expld01 | end_trig_expld01 | k_pend_explode | - | STORY |
| end_m01aa | end_trig_expld01 | end_trig_expld01 | k_pend_explode | - | STORY |
| end_m01aa | end_trig_expld01 | end_trig_expld01 | k_pend_explode | - | STORY |
| end_m01aa | end_reinfrc | end_reinfrc | k_pend_reinfrc06 | - | STORY |
| end_m01aa | end_trig_expl001 | end_trig_expld01 | k_pend_explodrd | - | STORY |
| end_m01aa | end_trig_expl001 | end_trig_expld01 | k_pend_explodrd | - | STORY |
| end_m01aa | bridgeexplosion1 | BridgeExplosion1 | k_pend_bridge04 | - | STORY |
| end_m01aa | bridgeexplosi001 | BridgeExplosion2 | k_pend_bridge05 | - | STORY |
| end_m01aa | end_sealbridge | end_sealbridge | k_pend_bridge06 | k_pend_bridge07 | STORY |
| end_m01aa | end_sealbridge | end_sealbridge | k_pend_bridge06 | k_pend_bridge07 | STORY |
| end_m01aa | nulltrigger | NullTrigger | k_pend_pctut | - | STORY |
| end_m01ab | end_trig_carth02 | end_trig_carth02 | k_pend_carth_t02 | - | STORY |
| end_m01ab | end_trig10 | end_trig10 | k_pend_explode02 | - | STORY |
| end_m01ab | k_stlthawrd | k_stlthawrd | k_trg_stealth | - | generic (other) |
| end_m01ab | end_killsith | end_killsith | k_pend_killsith | - | STORY |
| end_m01ab | g_zoncata001 | g_zoncata01 | - | - | no-op |
| end_m01ab | g_zoncata002 | g_zoncata02 | - | - | no-op |
| tar_m02aa | ptar_homint | ptar_homint | k_ptar_homint_en | k_ptar_homint_ex | STORY |
| tar_m02aa | ptar_homint | ptar_homint | k_ptar_homint_en | k_ptar_homint_ex | STORY |
| tar_m02aa | tar02_npcext | tar02_npcext | k_ptar_npcext_en | - | STORY |
| tar_m02aa | newgeneric001 | SithTalk | k_ptar_sithtalk | - | STORY |
| tar_m02aa | newgeneric003 | larrim | k_ptar_lartalk | - | STORY |
| tar_m02aa | tar02_rodiantrig | tar02_rodiantrigger | k_ptar_rodiantrg | - | STORY |
| tar_m02ab | tar02_npcext | tar02_npcext | k_ptar_npcext_en | - | STORY |
| tar_m02ab | tar02_swoop02ab | tar02_swoop02ab | k_ptar_swoop02ab | - | STORY |
| tar_m02ab | tar02_swoop02ab | tar02_swoop02ab | k_ptar_swoop02ab | - | STORY |
| tar_m02ab | tar02_swoop02ab | tar02_swoop02ab | k_ptar_swoop02ab | - | STORY |
| tar_m02ab | tar02_npcext | tar02_npcext | k_ptar_npcext_en | - | STORY |
| tar_m02ab | tar02_strgrd | tar02_strgrd | k_ptar_strgrd_en | - | STORY |
| tar_m02ab | g_partyinit002 | G_PARTYINIT001 | k_trg_partyinit | - | generic (other) |
| tar_m02ab | newgeneric001 | tar02_m4h8kl | k_ptar_m4h8kl_en | - | STORY |
| tar_m02ab | tar02_npcext | tar02_npcext | k_ptar_npcext_en | - | STORY |
| tar_m02ab | tar02_strdrk | tar02_strdrk | k_ptar_strdrk_en | - | STORY |
| tar_m02ab | tar02_drunks2 | tar02_drunks2 | k_ptar_drunks2 | - | STORY |
| tar_m02ab | g_partyinit001 | G_PARTYINIT001 | k_trg_partyinit | - | generic (other) |
| tar_m02ab | k_bant_trig | Banter | k_bant_trig | - | generic (other) |
| tar_m02ab | tar02_openbase | tar02_openbase | k_ptar_openbase | - | STORY |
| tar_m02ab | tar02_racstr | tar02_racstr | k_ptar_racstr_en | - | STORY |
| tar_m02ac | tar02_gurspk | tar02_gurspk | k_ptar_gurspk_en | - | STORY |
| tar_m02ac | tar02_bhtstr | tar02_bhtstr | k_ptar_bhtstr_en | - | STORY |
| tar_m02ac | tar02_npcext | tar02_npcext | k_ptar_npcext_en | - | STORY |
| tar_m02ac | newgeneric001 | tar02_spngan | k_ptar_spngan_en | - | STORY |
| tar_m02ac | newgeneric002 | CloseLabDoor | k_ptar_closedoor | - | STORY |
| tar_m02ac | tar02_npcext | tar02_npcext | k_ptar_npcext_en | - | STORY |
| tar_m02ac | g_partyinit001 | G_PARTYINIT001 | k_trg_partyinit | - | generic (other) |
| tar_m02ac | tar02_cleangana | tar02_cleangana | k_ptar_cleangana | - | STORY |
| tar_m02ac | tar02_gurscold | tar02_gurscold | k_ptar_gurscold | - | STORY |
| tar_m02ac | k_bant_trig | Banter | k_bant_trig | - | generic (other) |
| tar_m02ad | tar02_crtpty | tar02_crtpty | k_ptar_crtpty_en | - | STORY |
| tar_m02ad | tar02_sartlk | tar02_sartlk | k_ptar_sartlk_en | - | STORY |
| tar_m02ad | tar02_yuntlk | tar02_yuntlk | k_ptar_yuntlk_en | - | STORY |
| tar_m02ad | tar02_sithtortur | tar02_sithtorture | k_ptar_sithtort | - | STORY |
| tar_m02ad | tar02_npcext | tar02_npcext | k_ptar_npcext_en | - | STORY |
| tar_m02ad | tar02_sithresume | tar02_sithresume | k_ptar_sithresum | - | STORY |
| tar_m02ad | tar02_sithatk | tar02_sithatk | k_ptar_sithat_en | - | STORY |
| tar_m02ad | ptar_homint | ptar_homint | k_ptar_homint_en | k_ptar_homint_ex | STORY |
| tar_m02ad | g_partyinit001 | G_PARTYINIT001 | k_trg_partyinit | - | generic (other) |
| tar_m02ad | k_bant_trig | Banter | k_bant_trig | - | generic (other) |
| tar_m02ad | tar02_playmusic | tar02_playmusic | k_ptar_playmusic | k_ptar_stopmusic | STORY |
| tar_m02ad | tar02_playmusic | tar02_playmusic | k_ptar_playmusic | k_ptar_stopmusic | STORY |
| tar_m02ae | tar02_jertlk | tar02_jertlk | k_ptar_jertlk_en | - | STORY |
| tar_m02ae | tar02_chrdlg | tar02_chrdlg | k_ptar_chrdlg_en | - | STORY |
| tar_m02ae | tar02_npcext | tar02_npcext | k_ptar_npcext_en | - | STORY |
| tar_m02ae | tar02_spawncand | tar02_spawncand | k_ptar_spawncand | - | STORY |
| tar_m02ae | ptar_striparmor | ptar_striparmor | k_ptar_striparmo | - | STORY |
| tar_m02ae | tar02_candtlk | tar02_candtlk | k_ptar_candtlk | - | STORY |
| tar_m02ae | tar02_strdue | tar02_strdue | k_ptar_strdue_en | - | STORY |
| tar_m02af | tar02_carbas | tar02_carbas | k_ptar_carbas_en | - | STORY |
| tar_m02af | tar02_carstr | tar02_carstr | k_ptar_carstr_en | - | STORY |
| tar_m03aa | tar03_swoop03aa | tar03_swoop03aa | k_ptar_swoop03aa | - | STORY |
| tar_m03aa | tar03_swoop03aa | tar03_swoop03aa | k_ptar_swoop03aa | - | STORY |
| tar_m03aa | tar03_und1st | tar03_und1st | k_ptar_und1st_en | - | STORY |
| tar_m03aa | tar03_candcs | tar03_candcs | k_ptar_candcs_en | k_ptar_candcs_ex | STORY |
| tar_m03aa | tar03_npcext | tar03_npcext | k_ptar_npcext_en | - | STORY |
| tar_m03aa | tar03_gangcs | tar03_gangcs | k_ptar_gangcs_en | - | STORY |
| tar_m03aa | g_partyinit001 | G_PARTYINIT001 | k_trg_partyinit | - | generic (other) |
| tar_m03aa | k_bant_trig | Banter | k_bant_trig | - | generic (other) |
| tar_m03aa | g_zoncata001 | zonecatalogue01 | - | - | no-op |
| tar_m03aa | g_zoncata002 | zonecatalogue02 | - | - | no-op |
| tar_m03aa | transithelptrigg | TransitHelpTrigger | k_ptar_transhelp | - | STORY |

Summary: **69 STORY** (default-allowlisted), **10 generic (other)** (excluded by default —
`k_trg_partyinit` × 5, `k_bant_trig` × 4, `k_trg_stealth` × 1), **4 no-op** (no `ScriptOnEnter`
at all — the `g_zoncata` zone-catalogue markers, which exist for the minimap system, not for a
scene). `end_m01aa` alone — the standard demo save's own module — has 14, all STORY, so the
standard demo save CAN exercise this feature (unlike encounters, which client-encounters.md §5
notes `end_m01aa` cannot).

## 9. Addresses relied on

| Address | Symbol | Used for |
| --- | --- | --- |
| `0x00749A38` | `CSWSTrigger` vtable | identifying a trigger |
| `0x0058CE40` | `CSWSTrigger::InTrigger` | the watch's polygon test (verify-bytes) |
| `0x0058F140` | `CSWSTrigger::EventHandler` | the behaviour being reproduced (read, not called) |
| `0x005D0FC0` | `CVirtualMachine::RunScript` | the fire — reused from Odyssey.cs's already-verified declaration |
| `0x004D64C0` | `CSWCCMessageData::GetObjectID` | how the engine caches the entering object (read, not called) |
| `0x0058DA80` | `CSWSTrigger::LoadTrigger` | confirms the `ScriptOnEnter` field offset independently |
| `0x0058D570` | `CSWSTrigger::OnEnterMine` | the branch this channel deliberately never takes (read, not called) |
| `0x004AE6B0` | `CServerExoApp::GetModule` | area resolution, reused from Odyssey.cs |
| `0x004BF470` | `CSWSArea::InSubAreas` | how the engine finds triggers at a point (read, not called) |
| `0x00749A28` | string literal `"ScriptOnEnter"` | confirms the LoadTrigger field assignment is the field this pass reads |

Trigger fields (symbol database unless noted): `object +0x0`, `linked_to +0x230`,
`linked_to_module +0x238`, `scripts +0x244` (`ScriptHeartbeat +0x244`,
**`ScriptOnEnter +0x24C` (undocumented, this pass)**, `ScriptOnExit +0x254`), `geometry_count
+0x284`, `geometry +0x288`, `geometry_indices +0x298`, **entering-object cache `+0x29C`
(undocumented, this pass)**, `faction +0x2B8`, **mine/trap gate `+0x2BC` (undocumented, this
pass)**, `trap_detectable +0x2C0`, `trap_disarmable +0x2C4`, `trap_type +0x2D4`. Area: subarea
list `+0x1A0`/`+0x1A4` (same list `CSWSEncounter::AddToArea` fills — client-encounters.md §4).

## 10. Files

- `src/KotorCoop.Plugin/OdysseyTriggers.cs` — verify-bytes, `ListAreaTriggers`,
  `IsInsideTrigger`, `IsRelayEligible`, `PeekScriptOnEnter`, `FireTrigger`. A standalone file
  rather than an Odyssey.cs addition (several concurrent worktree agents already touch that
  file); every primitive either calls Odyssey.cs's own public twin or re-declares a
  private primitive from the SAME already-verified address Odyssey.cs documents.
- `src/KotorCoop.Plugin/TriggerRelayController.cs` — watch (non-owner) and fire (owner),
  parallel to `EncounterController`.
- `src/KotorCoop.Plugin/MovieRelayQueue.cs` — the mid-module movie DECISION half (§6), pure
  and tested; linked into `KotorCoop.Tests` the same way `MovieStaging.cs` is.
- `src/KotorCoop.Plugin/SharedBlock.cs` — region `0x3B88..0x3BD8`, fit inside the existing
  tail-slack padding without growing `TotalSize`.
- `src/KotorCoop.Memory/KotorGameBridge.cs` — the block's external (agent-process) half, plus
  the `SessionScopedBlockLevels` teardown entries.
- `src/KotorCoop.Session/TriggerRelayStore.cs` — freshness, module filter, dedupe, the
  per-module fired-set.
- `src/KotorCoop.Session/TriggerScriptPatterns.cs` — the allowlist glob matcher (the tested
  copy; `OdysseyTriggers.MatchesPattern` is a necessary duplicate — the plugin project takes
  zero project references).
- `src/KotorCoop.Protocol` — `TriggerRelayPacket`, `PacketType.TriggerRelay` (48), protocol
  `Version` 32.
- `tools/blockpoke.ps1 triggers` — read both halves; write a fire request by hand.

# Client-triggered encounters — how KOTOR decides an ambush fires, and where to stand in

Why a client walking over an ambush trigger used to get nothing, what the engine actually
does when a player enters an encounter, and the one call that reproduces it on the host.

Everything below was read out of `swkotor.exe` (GOG 1.0.3, MD5
`06B34D1B8A1ECEFBAAD0BF5E26556C71`) with `tools/disasm.py`, `tools/xrefs.py` and
`tools/q.py`, or out of the shipped game data with `tools/kotorfiles.py`. **Nothing here has
been checked on a running game** — see §7 for exactly what to test.

Tag key: **VERIFIED** = read out of the binary or the game data · **DESIGN** = a choice made
here · **NEEDS-LIVE** = must be confirmed on the running game.

---

## 1. The gap

Every peer that does not own enemies has its encounters switched off —
`KotorGameBridge.DisableLocalEncounters` writes `started = 1` over every `CSWSEncounter` in
the module. The reason is sound and is not being revisited: an encounter
spawn creates creatures, creature creation advances that instance's object-id counter, and
identical object ids across instances are the key the enemy, item, door, placeable and loot
channels all rest on (Handoff 4.5).

⚠ It used to write `active = 0` as well, and that **poisoned the player's own save**.
`CSWSEncounter::SaveEncounter` (0x591350) serialises `active` (+0x238) into the GIT and does
not serialise `started` (+0x278) — verified by disassembly: the function references +0x238
and +0x23C from that pair and nothing else. So a client who saved while joined wrote
"inactive" into their save for every encounter in the module, permanently, and those
encounters never fired again in single-player either. One field is enough anyway:
`SpawnIfAppropriate` (0x593BE0) early-outs on `active == 0` **or** `started == 1`, and sets
`started = 1` itself at 0x593C39 when it spawns — so writing `started` is exactly claiming
the encounter has already fired. It is now re-asserted every 15 s within a module rather than
once, because the binary was not exhaustively swept for a path that clears it.

The cost was that the disarm is total. A client could walk into the middle of a Black Vulkar
ambush and nothing at all happened, on either screen, until the host happened to walk into
it too.

## 2. What the engine does when a player walks in — VERIFIED

Four hops, all in the binary:

| # | What | Address |
| --- | --- | --- |
| 1 | Movement updates the creature's subarea membership | `CSWSCreature::UpdateSubareasOnMoveTo 0x0051B7B0`, `…OnJumpPosition 0x0051B940` |
| 2 | The area tests the point against every subarea object | `CSWSArea::InSubAreas 0x004BF470` |
| 3 | A newly-entered subarea becomes script event **0xC** | `CSWSCreature::HandleSubAreaStateChanges 0x00516020` (`mov word [edi], 0xC` at `0x0051638B`) |
| 4 | The encounter answers event 0xC | `CSWSEncounter::EventHandler 0x00594220` |

`InSubAreas` identifies an encounter by a vtable cast: slot `+0x5C` (index 23). On the
encounter vtable `0x00749D98` that slot is `Global::self_return 0x00641DB0`; on the creature
vtable `0x007470C8` it is `Global::return_zero 0x0063E7F0`. Then it calls
`CSWSEncounter::GetInActivateArea 0x00590090` with the point.

**Event 0xC's whole body is two things** (`0x00594241`–`0x00594261`):

```
[encounter + 0x2E0] = event->GetObjectID(0)      ; who entered
SpawnIfAppropriate(thatObjectId, -1.0f)
```

…followed by running `OnEntered` (`RunScript(&[encounter+0x2E8], encounterId, 1)` through
`CVirtualMachine::RunScript 0x005D0FC0`).

### Why a phantom never triggers this by itself — VERIFIED

`UpdateSubareasOnMoveTo` / `UpdateSubareasOnJumpPosition` have six call sites each, and all
twelve are the engine's own movement paths: `WalkUpdateLocation`,
`WalkUpdateLocation_QuickWalk`, `…_ForcePushed`, `DriveUpdateLocation`, `DoCombatStep`,
`AIActionJumpToObject`, `AIActionJumpToPoint`, `CSWSCreature::AddToArea`,
`CServerExoAppInternal::MovePlayerToArea`, `AddCharListRequest`. A phantom is placed by
direct `CSWSObject::SetPosition 0x004CD1A0` writes (the realtime-creature-movement recipe),
which appears in none of them. **So no amount of phantom movement on the host ever produces
event 0xC.** That is the mechanical reason the host has to be asked rather than left to
notice.

## 3. `SpawnIfAppropriate` — the one honest entry point — VERIFIED

`CSWSEncounter::SpawnIfAppropriate 0x00593BE0`, `__thiscall`, `ret 8`:
`(uint enteringObjectId, float difficultyOverride)`. All three engine call sites pass
`-1.0f` for the override — `SetActive 0x00593C68`, `AIUpdate`'s reset path `0x00594150`, and
`EventHandler 0x00594261`.

Its body, in order:

```
if (active[+0x238] == 0)            return;      ; 0x00593BE3
if (started[+0x278] == 1)           return;      ; 0x00593BED
[+0x28C] = 0
UpdateActivateAreaList(objectId)                 ; 0x00590DA0
if ([+0x28C] != 1)                  return;      ; 0x00593C0A
CalculateSpawnPool(difficulty)                   ; 0x005931D0
if (spawnPool[+0x2D8] <= 0.0f)      return;      ; 0x00593C1F, constant at 0x0073D700 == 0.0
BuildSpawnList()                                 ; 0x005935D0
started[+0x278] = 1
```

It does **not** create anything. The creatures appear on the server's next
`CSWSEncounter::AIUpdate 0x00593FB0`, whose very first act is
`SpawnCreatureFromSpawnList 0x00591CA0` — which early-outs on an empty spawn list
(`[+0x25C]`) and otherwise anchors its spawn-point search on
`GetCreatureByGameObjectID([+0x2E0])`, the object stored by event 0xC. So the host's own
engine, on its own thread, does the spawning; we only decide.

**Idempotence is free.** `started` is set by firing and tested on entry, so calling twice is
a no-op the engine implements itself. That is what lets the whole channel be a repeated
level rather than a delivered event.

### `UpdateActivateAreaList 0x00590DA0` — the actual eligibility test — VERIFIED

It walks the area's X-sorted object index (`CSWSArea::GetFirstObjectIndiceByX 0x00506F20`
seeded from the encounter's `minX [+0x2A0]`, stopping past `maxX [+0x2A4]`) and, for each
creature:

| Gate | Address | What it means |
| --- | --- | --- |
| resolves as a creature | `0x00590E5A` | `CServerExoApp::GetCreatureByGameObjectID 0x004AE770` |
| `GetAIStateReputation(encounterId) == 2` | `0x00590E9E` | **hostile.** `0x0057CD90` returns 1 for reputation ≥ 90, **2 for ≤ 10**, else 0 |
| `vtable[+0x94]() == 0` | `0x00590EAF` | `CSWSCreature::GetDead 0x004EF820` — a corpse does not trigger |
| id == the argument → skip geometry | `0x00590ECC` | the "I already know this one entered" short circuit |
| …else bounding box + `GetInActivateArea` | `0x00590EF4` | the polygon test |
| `player_only [+0x318]` | `0x00590F18` | if set: `stats[+0xA74] -> is_pc[+0x6C]` non-zero, **or** `CSWPartyTable::GetIsMember_2 0x00563740` == 1 |
| → sets `[+0x28C] = 1` | `0x00590F4E` | "somebody eligible is in here" |

Two consequences matter:

- **Passing an object id bypasses the geometry test for that object.** That is not a hack we
  invented; it is how the engine handles its own OnEntered, where the trigger system has
  already established that the object entered.
- **`player_only` is the gate that would silently stop everything.** See §5.

## 4. Identifying an encounter across two instances — VERIFIED

`CSWSArea::LoadGIT 0x0050DD80` calls its per-list loaders in a fixed order, read straight
out of the function: `LoadCreatures 0x00504A70`, `LoadItems 0x00504DE0`,
`LoadDoors 0x0050A0E0`, `LoadTriggers 0x0050A350`, **`LoadEncounters 0x00505060`**,
`LoadWaypoints 0x00505360`, `LoadSounds 0x00505560`, `LoadPlaceables 0x0050A7B0`,
`LoadStores 0x005057A0`, … `LoadEncounters` constructs one `CSWSEncounter` per GIT entry in
file order (`Constructor 0x00593C70` → `LoadFromTemplate 0x00593A90` → `AddToArea
0x00593520`), taking ids from the same monotonic counter as everything else.

So the Handoff 4.5 argument for creature ids covers encounter ids verbatim: **same save,
same GIT, same order, same ids.** `tools/kotorfiles.py objectids <module>` reproduces the
assignment offline and already lists `Encounter List` among the lists it walks. This is
still NEEDS-LIVE as a *measurement* — the offline predictor has been checked against saved
`.sav` GITs for creatures, not specifically for encounters.

### The area keeps the list for us — VERIFIED

`CSWSEncounter::AddToArea` appends the encounter's object id to the area's subarea array at
`CSWSArea + 0x1A0` (data), `+0x1A4` (count), `+0x1A8` (capacity) — the tail at
`0x00593588`–`0x005935BB`, and the same array `InSubAreas` walks. So enumerating a module's
encounters is a short list walk plus a vtable check, not a memory sweep.

## 5. `player_only` is the whole difficulty — VERIFIED from the game data

Dumped every `.ute` referenced by every module GIT in the retail install:

- **47 encounter placements across 22 modules.** `end_m01aa` (the Endar Spire, the standard
  test save) **has none at all** — there is no `Encounter List` in its GIT. Nothing about
  this feature can be exercised from the existing saves.
- **34 of the 47 set `PlayerOnly = 1`**, including *every* Taris, Korriban and Star Forge
  combat encounter (`ptar_vulkaraptenc`, `VulkarEncounter`, `G_RAKGHLGROUP01`,
  `k34_enc_shyrack`, `k35_enc_sith`, `sta_assault`, …).
- **Every combat encounter is `Faction = 1`** (hostile).
- Only 5 have a non-empty `OnEntered`, and all 5 are `Active = 0` — script-armed, not
  walk-armed.
- All but one are `Reset = 0`: fire once, `started` stays 1 forever.

A phantom is a spawned creature. Its `stats + 0x6C` (`is_pc`) is 0 and it is not in the
party table, so on 34 of 47 encounters `UpdateActivateAreaList` would reject it and the
feature would appear to work while doing nothing.

**What the phantom DOES pass**: since combat participation (Handoff 0l) a live peer's
phantom is in faction **0 (PLAYER)** via `CSWSFaction::AddMember`, the same faction as a
real PC. Against a `Faction = 1` encounter, `GetAIStateReputation` therefore returns the
same 2 (hostile) it returns for the PC. The reputation gate needs nothing added.

### The choice — DESIGN

`player_only` is suspended for the duration of the one call and restored in a `finally`.
Rationale:

- The remote player **is** a player. The engine cannot tell, because their body in this
  instance is a spawned creature. Suspending the flag says exactly that, and nothing else.
- The alternative — writing `is_pc = 1` on the phantom — is far broader: `is_pc` is read all
  over the engine (XP, death, party, saving) and the phantom is deliberately not a PC.
- It runs on the game thread inside one synchronous call, so nothing else observes the
  transient.

⚠ It also means the engine's `player_only` intent is *deliberately* overridden for a remote
player. If a module ever uses `player_only` to mean "not a summoned creature" rather than
"a player", this is where that would show up.

## 6. What ships

```
client plugin  EncounterController (watch)   which activate areas contain the local player
     ↓ block 0x1DE4/0x1DEC
client agent   CoopAgent.PumpEnemies         publishes the set
     ↓ EncounterTriggerPacket (PacketType 32)
host session   EncounterTriggerStore         fresh + same-module + deduped
     ↓ block 0x1DFC/0x1E04
host plugin    EncounterController (fire)    Odyssey.FireEncounter
     ↓
host engine    SpawnIfAppropriate → AIUpdate → SpawnCreatureFromSpawnList
```

`Odyssey.FireEncounter` is `EventHandler`'s event-0xC body with the phantom substituted:

```csharp
[encounter + 0x2E0] = phantomId;     // anchor the spawn on the body that "entered"
[encounter + 0x28C] = 0;             // SpawnIfAppropriate's own precondition
playerOnly = [encounter + 0x318]; [encounter + 0x318] = 0;
SpawnIfAppropriate(encounter, phantomId, -1.0f);
[encounter + 0x318] = playerOnly;
return [encounter + 0x278] == 1 ? Started : EngineDeclined;
```

**Deliberately NOT done: the `OnEntered` script is not run.** `EventHandler` runs it after
SpawnIfAppropriate, and running it here would give a module script a phantom as
`GetEnteringObject` — a body whose `is_pc` is 0 and which is not in the party. In the retail
game this costs nothing (the only five encounters with an `OnEntered` are `Active = 0` and
never walk-armed), and the exact call is recorded above if it turns out to be wanted.

Guards, all in `EncounterController`:

| Guard | Why |
| --- | --- |
| Verify-bytes on both entry points before any call, logged on refusal | the house rule; the checked bytes include the `[+0x238]`/`[+0x278]` displacements, so a layout change fails too |
| Watch only where `SetEncounterWatch(true)` was written | the enemy owner's engine fires its own; the watch is dead weight there |
| Vtable check (`0x00749D98`) on every id, every time | the request comes off the wire; an id that names a door must not be called as an encounter |
| Requesting peer must have a live phantom (`Active`, `Health > 0`) | a dead peer's phantom leaves faction PLAYER and would fail the reputation gate anyway |
| Watch pass skipped during a local conversation | the dialogue system moves the participants; the enemy drive skips for the same reason |
| Requests and the watch flag cleared by `ClearCoopState` | the 0m lesson: level state nothing refreshes is state that never goes away |
| Everything dropped on a module change, request serial re-latched | ids reset on transition (Handoff 4.8) |
| `client_encounters=0` in coop.ini | kill switch |

## 7. NEEDS-LIVE — nothing below has been run

`end_m01aa` has no encounters, so **the standard demo save cannot exercise this at all.**
Use a module that has them: `tar_m03aa` (4 × `ptar_vulkaraptenc`) is the earliest convenient
one; `korr_m34aa` has 7.

1. **Encounter object ids match across instances.** `blockpoke encounters` on both sides
   with the same save loaded, or compare against
   `python tools/kotorfiles.py objectids tar_m03aa`. Everything else assumes this.
2. **The watch sees the polygon.** Walk the client into an ambush area; its log should read
   `encounters: standing in 1 activate area(s), first 0x…`. This is the single cheapest
   check that `GetInActivateArea` is being called correctly.
3. **The host fires it.** Host log `encounters: started 0x… for peer N (phantom 0x…)`, and
   real Vulkars appearing on the host's screen.
4. **`player_only` suspension is doing its job.** Comment out the suspension and the same
   run should report `EngineDeclined` on `ptar_vulkaraptenc`. If it fires either way, the
   phantom is passing the PC test some other way and the suspension can be dropped.
5. **⚠ Whether the client can SEE the result.** See §8 — the expected answer today is *no*,
   and this run is what settles it.
6. **Calling into the engine from the frame hook.** `SpawnIfAppropriate` allocates and reads
   blueprints. The 0n crash (`CloseDialogue` from the frame hook) is the precedent that
   *when* a call runs can matter as much as whether. Watch for a fault at or just after the
   fire.
7. **The re-fire storm that should not happen.** A client standing in an already-started
   encounter re-reports every tick forever. The host should log nothing after the first
   fire (`NotArmed` is deliberately silent). If the log fills up, the `started` read-back is
   wrong.

## 8. ⚠ The blocker this work uncovered: the mirror cannot see the spawns

`docs/research/enemy-presence-sync.md` and Handoff say "the host keeps its encounters;
whatever they spawn reaches the client through the enemy channel." **That is not true of the
code as written, and it was already not true before this change.**

`CombatController.Apply` and `EnemyPresenceController.DriveOne` both begin with
`Odyssey.ResolveServerObject(enemy.ObjectId)` and return when it is 0. The channel *drives
existing local copies*; it never creates one. A creature that exists only in the owner's
instance — which is exactly what an encounter spawn is — resolves to nothing on the mirror
and is silently skipped.

So today, a host-triggered encounter is invisible to the client; with this change, a
client-triggered one is invisible to the client too. Worse, since combat participation the
spawned enemies **can hurt the client through its phantom**, so the failure mode is damage
from nothing you can see.

This is orthogonal to who triggers — it is a missing materialisation path, and it needs its
own pass. The fence posts:

- The mirror already knows how to spawn and drive a creature: `Odyssey.SpawnCreature`
  (`ExecuteCommandCreateObject 0x0052F820`) plus the phantom recipe. What it lacks is *which
  blueprint*.
- Sending a 16-byte resref per enemy blows the one-datagram budget (35 B × 32 already sits
  at 1165 of 1200).
- **The cheap key**: an encounter's creature list (`CSWSEncounter + 0x2B0`, count `+0x2B4`,
  loaded from the `.ute`'s `CreatureList`) is *identical in both instances*. The owner could
  send `(ownerObjectId, encounterId, creatureListIndex)` — one byte instead of sixteen — and
  the mirror resolves the blueprint against its own copy of the same encounter.
- That needs an owner-id → local-id indirection in `EnemyPresenceController` and
  `CombatController`, both of which currently assume id identity everywhere. That is the
  real cost, and the reason it is not in this change.

Until then, `client_encounters=0` is the honest setting if invisible enemies are worse than
no enemies.

## 9. Addresses relied on

| Address | Symbol | Used for |
| --- | --- | --- |
| `0x00749D98` | `CSWSEncounter` vtable | identifying an encounter |
| `0x00590090` | `CSWSEncounter::GetInActivateArea` | the watch's polygon test (verify-bytes) |
| `0x00593BE0` | `CSWSEncounter::SpawnIfAppropriate` | the fire (verify-bytes) |
| `0x00590DA0` | `CSWSEncounter::UpdateActivateAreaList` | the eligibility gates (read, not called) |
| `0x00594220` | `CSWSEncounter::EventHandler` | the behaviour being reproduced |
| `0x00593FB0` | `CSWSEncounter::AIUpdate` | where the spawn actually happens |
| `0x00591CA0` | `CSWSEncounter::SpawnCreatureFromSpawnList` | ditto; reads `+0x2E0` |
| `0x00593520` | `CSWSEncounter::AddToArea` | proves the area's subarea list holds encounters |
| `0x004BF470` | `CSWSArea::InSubAreas` | how the engine finds encounters at a point |
| `0x00516020` | `CSWSCreature::HandleSubAreaStateChanges` | where event 0xC is made |
| `0x0051B7B0` / `0x0051B940` | `CSWSCreature::UpdateSubareas…` | why a phantom never makes one |
| `0x0050DD80` / `0x00505060` | `CSWSArea::LoadGIT` / `LoadEncounters` | encounter id determinism |
| `0x0057CD90` | `CSWSObject::GetAIStateReputation` | 1 friendly / 2 hostile / 0 neutral |
| `0x004EF820` | `CSWSCreature::GetDead` | vtable `+0x94`, the corpse gate |
| `0x00563740` | `CSWPartyTable::GetIsMember_2` | the other half of `player_only` |
| `0x0073D700` | float `0.0` | the spawn-pool comparand |

Encounter fields (symbol database unless noted): `active +0x238`, `spawn_list +0x258`
(count `+0x25C`), `started +0x278`, `area_list +0x284`/`+0x288`, **`eligible +0x28C`
(undocumented)**, `geometry +0x290`/`+0x294`, geometry index `+0x298`/`+0x29C`, bounds
`+0x2A0`(minX)/`+0x2A4`(maxX)/`+0x2A8`(minY)/`+0x2AC`(maxY) (undocumented),
`creatures_list +0x2B0`/`+0x2B4`, `spawn_points +0x2B8`/`+0x2BC`, `max_spawns +0x2C4`,
`current_spawns +0x2C8`, **`last_entered +0x2E0` (undocumented)**, `script_on_entered
+0x2E8`, `player_only +0x318`. Area: subarea list `+0x1A0`/`+0x1A4` (undocumented),
game objects `+0x190`/`+0x194`. Creature stats: `is_pc +0x6C`, `faction_id +0x78`.

## 10. Files

- `src/KotorCoop.Plugin/Odyssey.cs` — the encounters section: verify-bytes,
  `ListAreaEncounters`, `IsInsideEncounter`, `FireEncounter`.
- `src/KotorCoop.Plugin/EncounterController.cs` — watch (client) and fire (owner).
- `src/KotorCoop.Plugin/SharedBlock.cs` — region `0x1DE0..0x1E60`.
- `src/KotorCoop.Memory/KotorGameBridge.cs` — the block's external half.
- `src/KotorCoop.Session/EncounterTriggerStore.cs` — freshness, module filter, dedupe.
- `src/KotorCoop.Protocol` — `EncounterTriggerPacket`, `PacketType.EncounterTrigger` (32).
- `tools/blockpoke.ps1 encounters` — read both halves; write a fire request by hand.
- `tools/verify.ps1` — checks 23 and 24 (plumbing only; see §7).

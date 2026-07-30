# kotor-coop: handoff

Cold-start document. Read this and you should not need to re-derive anything.

Repo: `D:\Development\kotor-coop`
Goal: real multiplayer co-op for Star Wars: Knights of the Old Republic 1, retail binary,
no other dependencies. C# throughout.

---

## 0. Addendum, session of 2026-07-30 (evening)

Everything below the line was true at handoff; this section is what changed since.

**Built and verified live (two GOG instances):**

- **Epoch invalidation** (was 7.9's two latent bugs). `PluginHost.OnFrame` now opens with
  `Odyssey.TryGetWorldEpoch` — gate on `loading_module == 0 && server_mode == 2`, epoch =
  object-array pointer (replaced by `UnloadModule`) + FNV of the module name from
  `CServerInfo+0xFC`. On change every controller's state is dropped before anything runs.
  Pre-store flush: `CSWSModule+0x220 -> CSWSArea+0x2C4 trans_pending` opens a ~150-frame
  window in which phantoms and replicated items are destroyed (one per frame) so
  `StoreCurrentModule` cannot bake them into the save.
- **The module filter now uses the real module name** (`EngineGlobals.TryReadModuleName`,
  `serverInternal+0x10004 -> +0xFC`), not `area{id:X}` — area ids recycle across modules.
  Side effect: KOTOR and the mock now agree on module names (trap 8 partly obsolete).
- **Door sync, verified live** (unlock leg). Doors ride the existing reliable max-merge
  flag channel: key `0x44<<24 | moduleHash8<<16 | objectId`, value
  `openState(0-2, 3=destroyed) | 4 unlocked` — monotonic by construction. Agent scans the
  `CSWSDoor` vtable `0x7498B0` (open `+0x2CC`, locked `+0x2C4`), baselines silently per
  module, diffs. Plugin `DoorController` applies: unlock is a byte write; open goes
  through `SetOpenState(state, deferred=1)` (verified by disasm: writes pending `+0x2CD`,
  plays the swing via vtable `+0x7C`) with a forced `MoveToNextOpenState` (`0x589680`,
  commits state + walkmesh) if the engine has not committed after ~3 s. Verified: unlock
  written in instance 1 propagated and logged `door 0x46: unlocked on the session's
  authority` in instance 2 within ~5 s. The open leg is coded but was not observed live.
- **Client-side encounters disabled** (`active`=0 `+0x238`, `started`=1 `+0x278`,
  vtable `0x749D98`), once per module, on every non-enemy-authority peer.
- **Dialogue guard** (7.9 step 0) was already built and wired; confirmed present.
- **Item drop regression-tested** on the new build: replicated in <1 s both ways.

**Built after live user feedback, needs live verification:**

- **Movement fidelity.** The phantom now runs when its player runs (the peer's raw
  animation travels in the slot at `+0x1C`, previously never written; walk `0x2712` is
  the only walking gait), goal-resend threshold dropped 2.5 -> 0.6 units with a 5-step
  re-path cooldown, facing is mirrored on arrival, and a phantom >15 units adrift snaps
  via `SetPosition` instead of walking a catch-up.
- **Equipment sync v1.** `PlayerState` gained three resref strings (body armor, right
  weapon, left weapon), read from `CSWSCreature+0xA2C -> CSWInventory` (slot pointers at
  `+0x4 + 4*slot`: body `+0x08`, right `+0x14`, left `+0x18`; item tag lowercased as the
  resref). The plugin equips the phantom for real: `CreateItemOnObject` (`0x52F570`) then
  `ActionEquipItem` (`0x5355F0`, instant, OBJECT_SELF redirected), both resolved by
  handler-address scan. Missing resref (peer has a mod we lack) fails soft at create.
  ⚠ Untested risk: `n_commm` may lack proficiency/armor feats and refuse some equips —
  watch for `equip: ... refused` in the plugin log; fallback is calling
  `CSWSCreature::EquipItem` (`0x4feb90`) directly, which needs its 5 args disassembled.
- **Shared block is now version 5** (signature `... 05 00 00 00 ...`), `TotalSize 0x920`:
  door slots at `0x690/0x698`, equip resrefs at `0x7A0` (8 peers x 3 x 16 ascii).
  `tools/pluginstate.ps1` now matches any version.

**User direction (recorded in auto-memory too):** sync *everything* visible (equipment,
map presence), movement must feel real-time, expect users to have mods, prefer
game-integration over memory-editing — an in-game "Connect" main-menu flow (via 4.6's
direct GUI-event firing plus an override `.gui` mod) should replace external menu
clicking, and the map should show the other player (candidate: engine waypoints with map
notes moved per-tick). NWScript has no networking, so the dinput8 plugin remains the
backbone regardless.

**The engine-side load path, finally mapped (supersedes the dead end in 4.2).**
The route 4.2 could not find is `CServerExoApp::ExecuteLoadModule(saveInfo*)` at
**`0x004AEBF0`** — SendLoadGameRequest was a red herring; the GUI never relies on the
message being serviced. The working sequence, read out of
`CSWGuiSaveLoad::LoadPCAutoSave` (`0x006CA250`, thiscall, one arg = save entry) and its
call site in `LoadGame` (`0x006CB0E0`):

1. `CClientExoApp::SetInputClass` (`0x005EDA60`) / `DisableInput` (`0x005EDBE0`), and
   `SetLoadStep(4, 0x17)` (`0x005EDFD0`) — the loading-screen GUI state.
2. `CServerExoApp::UnloadModule` (`0x004AE8A0`), then `0x004AED00(1)`.
3. `CServerExoApp::SetDoingStartNewModule(1)` (`0x004AF090`).
4. `ExecuteLoadModule(saveInfo)` — the actual transition.

`LoadPCAutoSave` also shows how to build the `saveInfo` without the GUI list: it
allocates **0xD0** bytes via `operator new` (`0x006FA7E6`), constructs it with
`0x005DD9C0`, and fills it from the literals `"AUTOSAVE"`/`"%06d - %s"` (`0x007455A0`) and
the `"SAVES:"` alias (`0x00745174`) — i.e. directory `000000 - AUTOSAVE`. So the
no-input-injection load is: copy the co-op save over the autosave slot on disk (a save is
just a directory of 5 files), construct `CSWGuiSaveLoad` (`0x006CC680`, args
`(owner=[mainmenu+0x18], 0, 1)`; main menu object at `clientInternal+0x280`, only at the
main menu) and call `LoadPCAutoSave` — or skip the panel entirely and replicate its body.
Spare shared-block space at `0x970-0x97C` is reserved for the request/ack fields; the
handler must run BEFORE PluginHost's epoch gate, which returns early whenever there is no
server (i.e. exactly at the main menu). Not yet implemented — needs crash-testing.

Also mapped for the same flow: `CSWGuiMainMenu::OnLoadSaveGame` (`0x0067B1A0`, thiscall,
arg = a control whose `+0x4C` must be non-zero) validates `mainmenu+0x140C`, allocates
the 0x1160-byte `CSWGuiSaveLoad`, and shows it via `0x0040BC70(owner, panel, 2, 1)`.
`CSWGuiSaveLoad::LoadGame` takes a populated LIST ENTRY (flags `+0x1C4`, name `+0x210`),
so driving it requires `PopulateGameList` (`0x006CC160`) — the autosave route avoids all
of that.

**New traps:**

22. **A game window that has never been foreground-activated ignores all injected input**
    — `DeactivateRenderWindow` ran `SetInputActive(0)` and nothing re-enabled it. The
    menu renders, `GetForegroundWindow` can even report the game, clicks and hovers do
    nothing, and no in-game cursor is rendered (that absence is the tell). Fix: a real
    activation cycle — minimise then restore the window — then input lands. This is what
    "cannot drive the menu: foreground pid is 0" degenerates into after a transient
    focus failure at launch.
23. **The game's menu cursor can be positioned by writing `CClientExoAppInternal+0x3C8/+0x3CC`
    from outside**, then clicking with button-only `SendInput`. But hover/hit state only
    refreshes on a real mouse-move event, so send a net-zero relative wiggle between the
    write and the click (`scratchpad/menuclick.ps1` had this working except the final
    hit test — untested further once minimise/restore fixed the root cause).
24. **`Read-Host` in a non-interactive `demo.ps1` run dies instead of waiting.** The
    script's instance-2 fallback prompt assumes a human terminal.
25. **Menu input injection is intermittently dead even with the game foreground and the
    window activation-cycled.** Observed repeatedly on the evening of 2026-07-30: the
    same fresh-launch + `agent load` sequence succeeded five times and then failed five
    times across both instances, with `GetForegroundWindow` returning the game and the
    plugin forwarding DirectInput correctly (an A/B with the plugin removed also
    succeeded once and proved nothing — the failures did not correlate with the plugin
    in the end). Root cause not identified; a human clicking the menu always works. This
    unreliability is the operational argument for the in-process load above.

### 0.2 Third addendum, remote session, second tranche (same evening, ~20:45)

⚠ **Shared-block collision warning for the load-path plan above:** the "spare space at
`0x970-0x97C`" reserved for the load request/ack fields is spare no longer — **shared
block version 7** (committed 31ffa96) occupies `0x96C-0x9A4`. Current v7 map, all new
fields beyond v6:

```
0x96C  u32  agent -> plugin   remote-pause desired (any peer holds a pause)
0x970  u32  plugin -> agent   remote-pause applied (echo guard for flag publication)
0x974  u32  agent -> plugin   journal request serial (written LAST)
0x978  32b  agent -> plugin   journal quest tag, ascii
0x998  i32  agent -> plugin   journal state
0x99C  u32  plugin -> agent   journal applied serial
0x9A0  i32  plugin -> agent   journal status
TotalSize 0x9C0 — free space for the load request/ack: 0x9A4-0x9BC.
```

Built this tranche (all committed, 118 tests pass, plugin builds; **untested in game**):

- **Journal replication** (quest merge): reliable JournalOp/Ack + version tripwire in
  the world snapshot + multi-part snapshot repair — the flag channel's shape with a
  string payload. Agent reads entries externally (`internal+0x1B770` party table,
  journal list at `+0x11C`: `{SJournalEntry* data, int size}`, tag CExoString `@+0x18`,
  state `@+0x20`, stride 0x3C — derived from GetState/SetState disassembly); apply goes
  through the plugin: `CSWSJournal::SetState` (`0x005C5A40`, thiscall
  `(CExoString* tag, int state, int bOverrideHigher=0)`), which also refreshes the GUI.
- **Client damage counts**: clients stream their enemy census to the host
  (EnemyReport, module-scoped, 2 s freshness); the host floors its own enemies at the
  reported minimum (never below 1; death stays host-side). Fixed two latent bugs:
  enemy health read UNSIGNED (an overkilled enemy at -5 hp read as 65531, alive), and
  phantom exclusion relied on the plot flag the redesign below removes — now by object
  id from the block's phantom list.
- **Phantom vulnerability** (7.4's design, replacing plot): no plot, min-1-hp, attack
  bit (`+0x9F0 & ~4`) cleared, `PacifyCreature` (`0x004FD960`, thiscall, no args) on
  the owner leaving combat or going down. The owner's in-combat state travels in the
  new **PlayerState flags dword** (bit0 dialogue, bit1 paused, bit2 combat; slot
  `+0x3C`).
- **Shared pause**: either player's pause pauses both. Player pause is mask bit 0x01
  (`TogglePauseState 0x004BA610` → internal `SetPauseState 0x004B8110`, which drives
  `CWorldTimer::PauseWorldTimer` at `0x004ADFF0`). Plugin `PauseController` holds/
  re-asserts the bit via `SetPauseState(1, x)` while any peer's own-pause flag is up;
  the mirrored pause is excluded from republication by an edge-latch in the bridge plus
  the `0x970` echo-guard field. Only the pauser can release.
- **Wire protocol bumped to version 3** (PlayerState flags + journal + reports), so
  old agents are cleanly rejected at Hello rather than desyncing.

### 0.1 Second addendum, parallel remote session (same evening)

A remote session ran concurrently with the one above and added, on top of its work:

- **The repo is now under git** (task 7.8): baseline commit of the whole working state,
  then one commit per feature. `tools/__pycache__` is ignored; `tools/kotor1_0_3.db` is
  committed.
- **Tasks 40-42** (travel-together, phantom name plates, death-pose replication) — all
  engine detail in **4.11**. These bumped the shared block to **version 6, TotalSize
  0x980** (superseding the "version 5 / 0x920" note above): travel request at `0x920`
  (serial u32, module char[32] `0x924`, waypoint char[32] `0x944`, applied u32 `0x964`,
  status i32 `0x968`). Plugin and agents must be rebuilt together and `dinput8.dll`
  reinstalled in BOTH game directories before the next live run.
- 105 tests pass (9 new for the follow-the-host policy in `TravelPumpTests`).
- ⚠ The remote session killed stray `KotorCoop.Agent` processes at ~19:50 and ~19:54 to
  unblock builds (trap 12). If those belonged to a live run of this session, that is why
  it died; the two `swkotor.exe` instances were left untouched.

---

## 1. Current state

**Two live KOTOR instances now play co-op.** The old environmental blocker is gone: the
single-instance guard is a named mutex, not DRM, and the GOG build has no DRM at all.

| Capability | Status |
| --- | --- |
| Transport, session, interpolation, world-flag sync | done, 82/82 tests pass |
| Reading the real player out of `swkotor.exe` | working |
| In-process plugin loaded by the game itself | working |
| Once-per-frame callback on the game's main thread | working |
| Spawning a creature via the engine's own `CreateObject` | working |
| Driving that creature so it walks (pathfinding, animation) | working |
| **Two KOTOR instances running at once** | **working** (see 4.1) |
| **Each instance seeing the other player as a phantom, both ways** | **working** |
| **Phantom wearing the remote player's appearance** | **working** |
| **Dropping an item and it appearing in the other game** | **working** |
| **Player health replicated (the `hp 0` bug is fixed)** | **working** |
| **Host-authoritative enemy health, mirrored to the client** | **working, verified** |
| **A host-side enemy death killing the client's copy for real** | **working, verified** |
| **Item pickup detected and removed from the session** | **working, verified** (7.1) |
| **Phantom is neutral + plot, so it is not an aggro sink** | **working, verified** |
| **Auto-pause suppressed** | **working, verified** (`0x17000` -> `0x00000`) |
| World/quest state replication | code done; the table reads correctly live, cross-instance replication untested |
| Epoch invalidation, door sync, encounters off, equipment sync | **built** — see addendum 0 for what was verified live |
| **Module-transition following (travel together)** | **built** (SharedBlock v6, TravelController; see 4.11), untested in game |
| **Phantom name plates (peer's name in the selection band)** | **built** (see 4.11), untested in game |
| **Player death mirrored as a phantom death pose** | **built** (ambient state 4; see 4.11), untested in game |

Evidence from the two-instance run of 2026-07-30 (pids 35464 and 61108, both on the Endar
Spire, `area1`):

```
instance 1 plugin            instance 2 plugin
spawned phantom 1: id 0x130  spawned phantom 1: id 0x12F
move#2 -> goal (17.38,22.06) move#2 -> goal (17.55,21.58)
move#3 -> goal (14.81,21.46) move#3 -> goal (14.94,20.97)
move#4 -> goal (12.24,20.86) move#4 -> goal (12.39,20.08)
```

Live memory read of both processes at rest, showing each phantom tracking the other
process's real player, and both wearing appearance row 114 (the PC's row, not the
`n_commm` blueprint's 17):

```
pid 35464  player 0x7FFFFFFF (11.68,19.73) appr 114     phantom 0x130 (12.24,20.86) appr 114
pid 61108  player 0x7FFFFFFF (10.78,20.64) appr 114     phantom 0x12F (12.39,20.08) appr 114
```

Item drop, one keystroke on instance 1, both plugins reacting within 15 ms:

```
[12:29:38.495] i1: item slot 0: spawned item 0x10001 as object 0x131 at (12.68,20.73,-1.27), area 0x1
[12:29:38.510] i2: item slot 0: spawned item 0x10001 as object 0x130 at (12.68,20.73,-1.27), area 0x1
```

Confirmed by scanning both processes for `CSWSItem` (vtable `0x748DA0`): a real
`g_w_lghtsbr01` at identical world coordinates in both games, each with a non-null client
twin and the visibility flag set, so both are genuinely rendered and clickable.

Despawn is proven too — restarting an agent withdraws its slots and the plugin destroys
the body it owned: `item slot 0: destroyed 0x131 (item 0x10001)`.

Combat replication, verified on the two-instance run of 2026-07-30 (pids 59540 host and
34660 client). Both agents independently agree on the enemy census, and an enemy damaged
on the host is mirrored on the client within a tick:

```
baseline    host 0x14 hp 15        client 0x14 hp 15
host -> 6   host 0x14 hp 6         client 0x14 hp 6        (mirrored, not commanded)
host -> 0   host reports dead      client: "combat: killed 0x14 on the owner's authority"
                                   client 0x14 no longer exists - the real death path ran
untouched   host 0x17 hp 1         client 0x17 hp 1        (nothing else disturbed)
dashboards  "25 known (24 alive, 1 dead)" on BOTH sides
```

The damage in that run was applied by writing the host's memory rather than by swinging a
sword, because staging a real fight on demand proved unreliable — see 7.5. What is being
demonstrated is the *replication*: the host's authority over enemy health and death
reaching the client and being executed there through the engine's own death path.

---

## 2. Architecture

```
KotorCoop.Agent (external, x64)          KotorCoop.Plugin (in-process, x86 NativeAOT)
  attaches with OpenProcess                loaded by the game as dinput8.dll proxy
  finds player by creature vtable          hooks GDI32!SwapBuffers IAT for main-thread tick
  runs the UDP session                     calls CreateObject to spawn phantoms
  writes peers into the shared block  -->  queues ActionMoveToLocation to walk them
```

The two halves meet at a `SharedBlock` the plugin allocates inside the game. The agent
finds it by scanning for a 16-byte signature. Division of labour is deliberate: **all
discovery is external, all engine calls are in-process.**

```
src/KotorCoop.Protocol   wire format: packets, codec, player state, ground items
src/KotorCoop.Memory     OpenProcess, AOB scanner, offset profiles, KotorGameBridge
src/KotorCoop.Session    UDP transport, host/client, interpolation, flags, ground items
src/KotorCoop.Agent      CLI: launch, autotrack, track, run, scan, info
src/KotorCoop.MockGame   stand-in game process for testing without KOTOR
src/KotorCoop.Plugin     in-process plugin (NativeAOT win-x86 -> dinput8.dll)
tests/KotorCoop.Tests    82 tests
profiles/                JSON offset profiles
docs/re/                 144 KB of verified reverse-engineering research (4 files)
```

Two replication channels share one shape, and it is worth seeing the symmetry:

| | avatars | world flags | ground items |
| --- | --- | --- | --- |
| carrier | `WorldSnapshot`, every tick | `FlagOp`, retransmit-until-ack | `ItemOp`, retransmit-until-ack |
| divergence detector | n/a, it is a stream | `FlagVersion` in the snapshot | `ItemVersion` in the snapshot |
| repair | next tick | `FlagSyncRequest` -> `FlagSnapshot` | `ItemSyncRequest` -> `ItemSnapshot` |
| in-game | plugin spawns/walks a creature | not wired to the game yet | plugin spawns/destroys an item |

Positions are disposable, so they stream. "A lightsaber is lying there" is world truth, so
it is reliable and versioned.

Enemies are a fourth channel and a different shape again: `EnemySnapshot`, host → clients
every tick, unreliable. Death is a *level* in that state rather than an event, so a lost
packet costs one tick of staleness instead of a permanently missed kill.

### Authority

| Thing | Owner | Enforcement |
| --- | --- | --- |
| A player's own character | that player's instance | separate processes; nothing else can drive it |
| A peer's phantom | the peer | friendly faction + `plot`, so it cannot be fought or killed locally |
| Enemies | the **host** | clients pinned with `min1hp`, so only the host can land a killing blow |
| Ground items | whoever dropped it, until someone picks it up | possessor field decides |
| Party members | **nobody yet** — both instances run their own. See 7.4. |

---

## 3. Environment

**The GOG build is now the primary target.** Use it, not the Steam copy.

| | GOG (use this) | Steam (legacy) |
| --- | --- | --- |
| path | `C:\GOG Games\Star Wars KOTOR (GOG)` | `C:\Program Files (x86)\Steam\steamapps\common\swkotor` |
| sha256 | `D3432B32...C173E381A` | `34E6D971...39F34C88` |
| DB `game_version` row | `gog 103` | `steam 103` |
| entry point | `0x006FB38D`, inside `.text` | `0x0086D2ED`, inside `.bind` |
| disk bytes == memory bytes | **yes** | no, packed |
| second instance | works, see 4.1 | refused by Steam DRM |

Second GOG copy for player 2: **`D:\Games\swkotor-gog-p2`** (6.02 GB, plain robocopy of
the first). Both have the plugin installed and both inis are windowed.

Both builds share the identical layout, which was verified before trusting any of it:
same `SizeOfImage 0x4C3000`, same section table, and the hardcoded addresses spot-checked
byte for byte in the GOG image — `CSWSCreature` vtable at `0x007470C8`, the `SwapBuffers`
IAT slot at `0x0073D00C`, and the first instructions of `GetPlayerCreature`,
`ExecuteCommandCreateObject`, `ExecuteCommandMoveToPoint` and `StackPushInteger`. The
plugin then hooked the same IAT slot in the live GOG process, so the match holds at
runtime, not just on disk.

- x86 32-bit, ImageBase `0x400000`, **no ASLR**, so every address here is absolute and
  stable. The image spans `0x400000`-`0x8C3000`.
- The GOG entry point is in `.text`, so unlike the Steam build **the bytes on disk are the
  bytes in memory** and static disassembly is trustworthy. That is how the single-instance
  guard in 4.1 was found without attaching a debugger.
- .NET 10 SDK; Visual Studio 2022 Community with MSVC 14.44 x86 toolset; Windows SDK
  10.0.26100. Python 3.12 at `C:\Python312\python.exe`.
- `swkotor.ini` backup in each copy: `swkotor.ini.kotorcoop-backup`. Both inis have
  `FullScreen=0` and `AllowWindowedMode=1`.

### The address database (most valuable single asset)

A complete symbol-labelled Ghidra RE of this exact binary, as a queryable SQLite file:
977 classes, 9,710 functions, 4,720 member offsets, 16 global pointers.

- Source: https://github.com/LaneDibello/Kotor-Patch-Manager -> `AddressDatabases/kotor1_0_3.db`
- Thread: https://deadlystream.com/topic/11948-kotor-1-gog-reverse-engineering/
- Its `game_version` table lists `steam 103` with the same sha256 as the installed exe.

A copy is committed at **`tools/kotor1_0_3.db`** (2.3 MB).

Schema: `classes(class_name,size,vtable)`, `offsets(class_name,member_name,offset)`,
`functions(class_name,function_name,address,calling_convention,param_size_bytes)`,
`global_pointers(pointer_name,address)`, `game_version(...)`.

### Helper scripts

All of these are committed in **`tools/`**. Paths in the examples below assume you run them
from the repo root.

| Script | Purpose |
| --- | --- |
| `q.py` | query the address DB (`schema`, `class`, `offsets`, `func`, `globals`, `sql`) |
| `disasm.py` | disassemble a VA range (GOG only: disk bytes == memory bytes) |
| `xrefs.py` | find call/jmp sites and address literals targeting a VA |
| `demo.ps1` | bring up the whole two-instance co-op session |
| `movewin.ps1` | move / minimise / restore an instance's window |
| `pausestate.ps1` | decode the server pause-reason bitmask |
| `pluginstate.ps1` | read the plugin's shared block: frame rate, phantoms, heartbeat |
| `tooltip.ps1` | detect KOTOR's selected-object name band in a screenshot |
| `wheel.ps1` | scroll the mouse wheel over the game window |
| `creatures.ps1` | enumerate live creatures by vtable, with tag/stats/position |
| `vmtable.ps1` | resolve the live NWScript routine table and dump slots |
| `shot.ps1` | screenshot the game's client area (handles the window offset) |
| `click.ps1` | click at client-relative coordinates |
| `sendkey.ps1` | send scan-code keystrokes via SendInput |
| `walktest.ps1` | drive the character and sample a memory address |
| `writetest.ps1` | write a position and count engine overwrites |
| `exports.ps1` | dump a PE export table |
| `devenv.ps1` | import the MSVC x86 developer environment |
| `checkcands.ps1` | test a list of candidate addresses for a plausible position |
| `crop.ps1` | crop and upscale part of a screenshot (for reading small UI text) |

Note `q.py` and several `.ps1` files have the old scratchpad path hardcoded as a default;
point them at `tools/kotor1_0_3.db` when you first use them.

---

## 4. Verified engine facts

Subsections were added as the research landed, so they are not in numeric order in the
file. Read them in this order; use the numbers to search.

| | |
| --- | --- |
| 4.0 | an unfocused instance pauses, and the plugin has to stop it |
| 4.1 | the single-instance guard, and how it is defeated |
| 4.2 | starting a save without the menu — what was tried, and why it does not work |
| 4.3 | health, damage and death (**current HP is `+0xDC`, not `+0xE0`**) |
| 4.4 | ground items: the exact "is it on the floor" test |
| 4.5 | object ids are the same in both instances |
| 4.6 | the GUI cursor is writable (the proper fix for menu automation) |
| 4.7 | world and quest state — where KOTOR keeps it |
| 4.8 | module transitions destroy everything, and the counters reset |
| 4.9 | reading the game's own data files |
| 4.10 | dialogue silently disarms every enemy, and it collides with combat authority |
| 4.11 | module travel, creature naming, and the death-pose route (built 2026-07-30 evening) |

### 4.0 An unfocused instance DOES pause, and the plugin has to stop it

An unfocused KOTOR pauses itself, which is fatal for co-op because only one of two windows
can hold focus. The plugin's `FocusKeeper` suppresses it, and that suppression is load
bearing — not a safety net.

Be careful measuring this, because it is easy to get a false negative. An early set of
measurements found the pause bitmask reading `0x00` while unfocused and even while
minimised, with the frame counter running at full speed:

```
FOCUSED     frames 162,663 -> 164,008    ~337 fps    mask 0x00
MINIMISED   frames 165,254 -> 166,891    ~346-473 fps mask 0x00
```

and that led to the wrong conclusion. A later reading of an instance genuinely left in the
background for minutes, with no agent attached, showed:

```
mask 0x02  PAUSED: window-not-focused
```

and the plugin logs `cleared focus-loss pause (#1)` during ordinary two-instance runs. So
the pause is real but does not fire on every deactivation — the short, synthetic
alt-tab used in the first test did not reproduce it. **Do not conclude "it never pauses"
from a brief test.** The frame counter is also not a proxy for it: rendering continues
while `CWorldTimer` is paused, so frames keep climbing on a simulation that has stopped.

The mechanism: `Global::DeactivateRenderWindow` (`0x00401D90`), reached from the
`WM_ACTIVATEAPP` handler at `0x00402951`, calls `CServerExoApp::SetPauseState(2, 1)`,
which calls `CWorldTimer::PauseWorldTimer`.

The pause state is a **bitmask of reasons**, one byte at `CServerExoAppInternal + 0x10078`,
reachable as `[[[0x7A39FC]+8]+4]+0x10078`. Bit `0x02` is the focus-loss reason.
`tools\pausestate.ps1` decodes it.

`FocusKeeper` clears bit `0x02` once per frame when the agent asks it to (`--kotor` mode
turns it on automatically), leaving every other pause reason alone so a genuine
player-requested pause still works.

### 4.1 The single-instance guard, and how it is defeated

This was recorded as an environmental blocker requiring a second machine. It is not. The
whole guard is nine instructions at **`0x00404222`**:

```
push 0x0073D75C            ; "swkotor", the mutex name
call [0x0073D050]          ; CreateMutexA
mov  edi, eax
test edi, ebx / je ...     ; NULL handle -> skip
call [0x0073D05C]          ; WaitForSingleObject(handle, 0)
test eax, eax / je ...     ; WAIT_OBJECT_0 -> we own it, carry on
call [0x0073D200]          ; CloseHandle
or   eax, -1               ; return -1  <-- the entire single-instance guard
```

`or eax,-1` is exactly the exit code a second instance produces, which is how it was
identified. Give the second process a different mutex name and it starts normally.

`KotorCoop.Agent`'s `launch --instance 2` does that: `CreateProcess` suspended, rewrite
the ASCII string at `0x0073D75C` from `swkotor` to `kcoopN`, `ResumeThread`. Safe because
that address is referenced from **exactly one place** in `.text` (verified by scanning the
whole section for the literal) — the guard's own `push`. Nothing on disk is modified and
no game code is patched; the replacement must be at most 7 characters so it fits in place.
It refuses to resume unless it reads back the expected `swkotor` first, so a wrong build
is a clean failure rather than a corrupted `.rdata`.

Steam's copy additionally has its own DRM wrapper (`Application load error 5:0000065434`),
which is why the GOG build is the target.

### 4.10 Dialogue silently disarms every enemy — and it collides with combat authority

**This is a design constraint on combat, not a dialogue detail, and it is the reason to
settle dialogue before building any more of combat.**

`CSWSDialog::ClearAllPlayerHostileActions` (`0x005A0940`) walks the area object list
(`area+0x190` / `+0x194`), picks out every creature with `+0xA88 == 0` (not party) and
`GetReputation(player) <= 10` (hostile), and calls `CSWSObject::ClearAllActions(1)` on each
plus the same on its client twin. It is called from `CSWSObject::UpdateDialog`
(`0x004CD5AD`), so it runs on **every dialog update tick**, not once when the conversation
opens.

So while one player is in a conversation, every hostile in *that* instance has its action
queue wiped continuously — a partial combat-off switch. In the peer's instance the same
enemies keep fighting normally.

Now combine that with the host-authoritative enemy model already built (1, 4.3):

- **The host starting a conversation pacifies enemies for both players**, because the
  client mirrors the host's enemy state.
- **A client starting a conversation** desynchronises enemy behaviour for its duration —
  the client sees a frozen fight, the host sees a live one.

Neither is acceptable, and neither is visible in a test where nobody talks during a fight.
Whatever dialogue design is chosen has to answer "what do enemies do while a peer is in
conversation" *before* combat replication is extended.

Related, from the same work: the input lock is `CClientExoAppInternal + 0x9C` (0 world,
1 minigame, 2 full-screen GUI, 3 dialog), entered from exactly one site
(`CGuiInGame::ShowDialogEntry+0x46`, `0x0062B776`) and left in `CGuiInGame::CloseDialog`
(`0x006335A4`). `ProcessInput` at `0x00623895` jumps over the whole movement block when the
class is non-zero. The **world keeps simulating during dialogue** — confirmed by sweeping
every read of the gate — which is precisely why the disarm has to re-fire every tick.

Do not be misled by `IgnoreConversationStartRange` (`CServerExoAppInternal + 0x1B914`): it
has exactly one reader and only suppresses proximity culling of *bark bubbles*. The real
range gate is `GetIsInUseRange` (`0x004F6000`), and failing it queues a move rather than
refusing the conversation.

### 4.11 Module travel, creature naming, and the death-pose route

Three engine surfaces verified and wired up on the evening of 2026-07-30. All three ship
in **SharedBlock version 6** (the block grew to `0x980`; the agent's scan signature now
carries `06`), so both the plugin and the agents must be rebuilt together, and the copies
of `dinput8.dll` in BOTH game directories must be reinstalled before the next live run.

**Module travel** — `CServerExoApp::SetMoveToModulePending` (`0x004AECC0`) is a plain
dword store to `internal+0x10080`; `SetMoveToModuleString` (`0x004AECD0`) and
`SetMoveToModuleStartWaypoint` (`0x004AED30`) assign `CExoString`s at `internal+0x10084`
and `+0x1008C` through `CExoString::operator=` (`0x005E5C50`), which copies with the
engine's own allocator, so stack-local sources are safe. The engine's MainLoop notices
the pending flag and runs the entire transition. Wired as: client agents follow the
host's module (`CoopAgent.PumpTravel` — host-led only so no ping-pong, debounced 15 s,
never into `stunt*` modules, never into the `areaXX` fallback name, not while the local
player is in dialogue, `--no-follow` disables). The plugin's `TravelController` flushes
every spawned phantom and item BEFORE raising the pending flag, because
`StoreCurrentModule` would bake them into the outgoing module (4.8).

**Creature naming** — a creature's first name (the selection band) is a `CExoLocString`
at `stats+0x34` (`CSWSCreature::GetFirstName`, `0x004F8310`, returns exactly that
address). The engine's own rename flow (call site `0x00521B4C`) builds it with
`CExoLocString::AddString(0, &exoString, 0)` (`0x005EA340`, `__thiscall`, args pushed as
`nID`, `CExoString*`, `gender`); the plugin does `ClearLocString` (`0x005E9EA0`) +
`AddString` in place on the field, right after spawn so the client twin is created with
the name already set. The agent writes each peer's name into the remote slot's 24-byte
name field, which had been reserved since version 1 and never used.

**Death pose** — the client's animation sync rejects all three death constants written to
`+0xD4` (trap 21b). The working route: `CSWSCreature::SetAmbientAnimationState`
(`0x004EFEF0`, a byte store to `+0x4C4`) then re-issue idle `0x2710` through the
`SetAnimation` vtable slot, which remaps the idle through the ambient state using the
jump table at `0x004F0F50`. That table, decoded from the exe: states 3/4/14 produce the
death poses `0x2718`/`0x2716`/`0x27AC`; values outside 2..14 fall through to the normal
idle, so **state 1 stands the creature back up**. `PhantomController` drops a phantom
when its player's health is `<= 0` (clearing the action queue first, or a queued move
stands the body back up) and raises it on recovery. The health read in the bridge is now
**signed** — a downed PC's hit points go negative (threshold -10), and the old unsigned
read reported -3 as 65533.

### 4.7 World and quest state — where KOTOR keeps it

`CSWGlobalVariableTable` is **embedded** in `CServerExoAppInternal`:

```
gvt = [[0x7A39FC]+0x08]+0x04 + 0x000100FC
  +0x00000  identifiers[1775], stride 0x18: char name[22], u16 (type<<14 | index)
  +0x0A668  booleans   — bit array, MSB-first within each byte, 900 usable
  +0x0A6D9  numbers    — 500 SIGNED bytes
  +0x0B230  strings[5] / +0x0A8D0 locations[100]
```

Everything needed is **plain memory at fixed offsets**, so the external agent replicates
this with no engine calls — reading and writing the arrays is exactly what the engine's own
setters do (`SetValueBoolean` `0x00529570`, `SetValueNumber` `0x00529680`).

The catalogue is fixed for a session and comes out of the save, so **two instances loading
the same save get identical names AND identical slot indices**. That makes `(type, index)`
a complete network key: no name hashing, and no chance of two variables colliding on one
id. A catalogue digest is computed so a mismatch (different mods) can be detected rather
than silently corrupting quest state.

⚠ **The in-memory index is NOT the on-disk catalogue position.** Measured live against the
Endar Spire save: the four non-zero numbers hold the same values in both places, but at
different slots —

```
GLOBALVARS.res : [128]=1  [193]=1  [269]=5  [297]=11
live memory    : [145]=1  [184]=1  [93]=5   [181]=11
```

and the difference is not a constant offset, so it is a genuinely different ordering rather
than a base adjustment. This does not affect the network key, because both instances derive
the index from their own memory by the same route. It does mean **`tools\globalvars.py`'s
indices cannot be used to identify a live slot** — use it to learn names and to diff two
saves, and read the live catalogue (the `identifiers` array) when you need to map a running
game's slot to a name.

Scale, from the real Endar Spire save: **1185 declared globals** — 809 boolean, 369 number,
5 location, 2 string — of which 4 are non-default. The whole live state is
`113 + 500 = 613` bytes, so diffing it every tick is a `memcmp`.

Two rules that are not optional:

- **Update the shadow copy when applying a remote value.** Otherwise the next scan sees it
  as a fresh local change and broadcasts it back, and the two instances echo forever.
- **Merge upwards, never overwrite.** KOTOR's booleans are latches (`*_DONE`, `*_DEAD`) and
  its numbers are stage counters; the engine itself refuses to lower a journal quest state.
  Taking the maximum makes the merge a join — order-independent and idempotent, so a
  reordered or replayed packet cannot walk a quest backwards.

Not replicated: locations and strings. Every one in a real save is a per-player scratchpad
(`K_LAST_MODULE`, `K_LAST_LOCATION`, `K_STUNT_MODULE`) or cutscene staging. `K_CURRENT_PLANET`
and `k_vis_*` are excluded for the same reason.

The **journal is a separate store**, not derived from globals: `CSWSJournal` at
`partyTable + 0x11C`, entries of stride `0x3C`, and `SetState` (`0x005C5A40`) is the call
that also refreshes the GUI. It needs its own small channel.

### 4.8 Module transitions destroy everything, and the counters reset

`CServerExoAppInternal::UnloadModule` (`0x004B9240`) **destroys the entire
`CGameObjectArray` and allocates a fresh one**, whose constructor resets the id counters to
0 (ascending) and `0x7FFFFFFF` (descending). Two consequences:

- Trap 13 is confirmed with a mechanism: every server object pointer is dangling.
- **Id drift does not carry across a transition** — it is wiped. But bare object ids must
  never be compared across a module boundary; the key is `(moduleName, objectId)`.

**A KOTOR 1 module contains exactly one area**, which removes a whole axis from the design.

Detecting a transition: **do not use area id at `+0x8C`** (7.3 said to) — it is an object id
from a counter that resets, so different modules routinely share one. Use the module *name
string* at `CServerInfo + 0xFC`, reached via `CServerExoAppInternal + 0x10004`. Gate all
work on `loading_module` (`+0x100B4`) being 0 and `server_mode` (`+0x10008`) being 2.

**The serious one:** `StoreCurrentModule` writes the live area back out on module exit, and
`SaveCreatures` only skips creatures with `+0xA88` set (the PC and party). **A mod-spawned
phantom or ground item is therefore serialised into the saved module** and comes back as a
permanent NPC on a revisit, offsetting that instance's ids for the rest of the playthrough.
Phantoms and replicated items must be destroyed before the module is stored. `CSWSArea +
0x2C4` goes 1 about 0.5 s ahead of a door/trigger transition and is the flush window.

Following a peer is cheap and low risk — it is exactly what the shipped `warp` cheat does:
`SetMoveToModuleString` (`0x004AECD0`), optionally `SetMoveToModuleStartWaypoint`
(`0x004AED30`), then `SetMoveToModulePending(1)` (`0x004AECC0`). The engine's own MainLoop
does the rest with every invariant intact.

### 4.5 Object ids are the same in both instances (verified)

Two instances loaded from the same save assign **identical object ids** to everything the
module places. Measured live, heap addresses differ but ids match exactly:

```
              instance 1        instance 2
end_sith2     id 0x14           id 0x14
end_sith3     id 0x17           id 0x17
end_trask     id 0x127          id 0x127
```

The mechanism: `CGameObjectArray::AddInternalObject` allocates from a monotonic counter
(characters count *down* from `0x7FFFFFFF`, which is why the PC is that value), and
`CSWSArea::LoadCreatures` walks the GIT "Creature List" in file order. Same save, same
order, same ids.

**This is what makes combat replication cheap** — an enemy needs no cross-instance key
invented for it, its object id already is one.

The caveat is drift: anything either instance creates that the other does not shifts the
counter for everything created afterwards (phantoms, replicated items, encounter spawns).
So build any id mapping at area load, before mod-side spawns, and rebuild it after a
transition.

### 4.9 Reading the game's own data files

`tools\kotorfiles.py` (stdlib only, read-only) decodes KEY/BIF, RIM, ERF/MOD/SAV, GFF,
2DA and TLK, with a CLI: `key`, `list`, `gff`, `2da`, `extract`, `tlk`, `creatures`,
`objectids`. Exercised over 246 RIMs / 7,725 GFF resources with no failures.

Two findings from it that change decisions:

**The save records object ids explicitly.** `SAVEGAME.sav` is an ERF containing
`<MODULE>.sav`, whose `.git` gives every instance an `ObjectId` field — creatures, doors,
placeables, triggers, waypoints and sounds. And `Module.ifo` carries `Mod_NextObjId0`, the
id the next runtime-created object will get. So the id map can be built **offline and
exactly**, rather than hoping ids coincide. (`Mod_NextObjId0` = 295 = `0x127` in this save,
which is precisely the id Trask was observed to take, because he is re-instantiated from
`AVAILNPC0.utc` on load.) Read the field; do **not** infer order from the save GIT, which
is written sorted by X.

**The phantom walk speed of 5.4 was wrong — and also dead code.** The constant fed a
`maxDistance` local that was computed and never read, so it bounded nothing; a phantom's
speed comes entirely from the engine, because movement is an `ActionMoveToLocation` the AI
executes. Both are now deleted. Recorded in case speed ever does need setting: it is **not**
`creaturespeed.2da`. `CSWSCreature::GetWalkRate` (`0x004F1B20`) and `GetRunRate`
(`0x004F1BE0`) read the creature's appearance record, i.e. `appearance.2da`
`walkdist` / `rundist` — for the PC's row 114 that is 1.6 walk / 3.96 run.

Also worth knowing: `baseitems.2da`'s `container` column is dead (0 for all 92 rows) — a
container is marked by `HasInventory` on the *placeable* blueprint. And `stacking` is 99 for
nearly every item type, so the destroy-on-pickup stack-merge case from 4.4 is the **normal**
case, not an edge case.

### 4.6 The GUI cursor is writable (fixes menu automation properly)

Trap 19 records that KOTOR's menu cursor is accumulated from DirectInput deltas and cannot
be positioned by moving the OS pointer. It can, however, be **written directly**, which is
the clean fix and is not yet implemented:

```
appInternal = *(*(void**)(*(void**)0x007A39FC + 0x04) + 0x04)   // CClientExoAppInternal
  +0x3C8  int mouse_x     authoritative, clamped to SCREEN_WIDTH
  +0x3CC  int mouse_y     authoritative, clamped to SCREEN_HEIGHT
GUI_MANAGER_PTR 0x007A39F4 -> CSWGuiManager
  +0x00/+0x04  mouse x/y  (mirror; overwritten from +0x3C8 on every input event)
  +0x08        control currently under the cursor  <-- read back to VERIFY before clicking
```

`CClientExoAppInternal::RestoreMousePositionTo` (`0x005F4EA0`) writes the authoritative
pair; `CSWGuiManager::HandleMouseMove` (`0x0040C1E0`) updates hover state. Call both, then
inject only the button down/up — never mouse motion, which would clobber it.

Why relative stepping could never work: `CExoInputInternal::ScaledValue` divides the raw
axis delta by 100 and **clamps to ±1.0**, so a delta of −5000 and one of −100 are
identical.

Better still, the menu can be driven without a cursor at all by firing a control's event
directly — `CSWGuiControl::HandleInputEvent` is vtable slot `+0x3C`, and the main menu's
Load Game button is at `CSWGuiMainMenu + 0x5B4` bound to event `0x27`
(`0x0067B1A0 OnLoadSaveGame`). `CSWGuiMainMenu*` lives at `appInternal + 0x280` and is null
outside the main menu.

### 4.2 Starting a save without the menu: what was tried and why it does not work yet

Goal: launch straight into a save, no menu at all. **Not achieved.** The findings below are
the expensive part; do not redo them.

The GUI's load path ends at `CClientExoApp::SendLoadGameRequest` (`0x005ED680`), which
writes `WriteDWORD(saveId) + WriteCExoString + WriteCExoString` and is handled server-side
by `CSWSMessage::HandlePlayerToServerModuleMessage` (`0x00524800`) calling
`CServerExoApp::LoadGame(saveId, CExoString*, CExoString*, player)` (`0x004AE6F0`).
`CGuiInGame::DoQuickLoad` (`0x00633C50`) calls the same request with NULL for the second
string, so the strings are not all mandatory.

Reproducing it from the plugin got as far as "accepted and ignored":

| Step | Result |
| --- | --- |
| `SendLoadGameRequest(id, NULL, NULL)` from the menu | **crashes the process** |
| ... because **`APP_MANAGER+0x08` (the server) is NULL at the main menu** | measured directly |
| Add `CAppManager::CreateServer` (`0x00401380`) first | no crash; request returns success |
| Add `CServerExoApp::StartServices` (`0x004AE8F0`) | still nothing loads |
| Add `SetLoadFromSaveGame` (`0x004AF060`) + `SetLoadingSaveGame` (`0x005EDFC0`) | still nothing loads |
| Real empty `CExoString`s instead of NULL, and the save directory name | still nothing loads |

So the message is accepted and never serviced. `CSWGuiSaveLoad::LoadGame` (`0x006CB0E0`)
does considerably more around the request - GUI state transitions, input class changes,
`CGuiInGame` teardown - and one of those is what actually drives the transition. Finishing
this means replaying that sequence, not just sending the message.

The command line does not help either: the only switches `Global::checkCommandLineArgs`
(`0x00401700`) understands are `-ecf`, `-epf` and `-epr`, all capture/profiling.

What ships instead is menu automation (`agent launch --load-save <n>`), which needs no
human input but does briefly show the menu. Its own limitation is in 7.2.

### Object model

`CSWSCreature` size `0xAC8`, **vtable `0x007470C8`**. Scanning private committed memory for
that dword finds every creature with no false positives. `CSWSObject` (size `0x228`) is
embedded at offset 0.

| Offset | Field |
| --- | --- |
| `+0x004` | object id (player is `0x7FFFFFFF`) |
| `+0x008` | object type (creature = 5) |
| `+0x018` | tag, `CExoString` = `{char* @0, uint len @4}` |
| `+0x08C` | area id |
| `+0x090` | **position**, 3 floats |
| `+0x09C` | orientation, 3 floats (direction vector, not an angle) |
| `+0x0D4` | animation |
| `+0x0E0` | hit points (NOT the Vitality the UI shows) |
| `+0x0E8` | **commandable** (ClearAllActions no-ops unless 1; AI needs 1 to run the queue) |
| `+0x0FC` | action queue, `CExoLinkedList<CSWSObjectActionNode>` |
| `+0x1F8` | visible-to-player (engine's walk driver skips off-screen creatures) |
| `+0x1FC` | dirty-flag bit array (0 anim, 1 pos, 2 orient) |
| `+0x224` | client twin `CSWCObject*` |
| `+0x4C0` | party follow info (non-null on party members) |
| `+0x9F0` | bit 1 (`& 2`) = movement permitted; clear means no walk animation |
| `+0xA50` | appearance_info, `0x24` bytes (copy the PC's look with a memcpy of this) |
| `+0xA60` | appearance type, u16, `appearance.2da` row (PC is 174 in the current save) |
| `+0xA74` | `CSWSCreatureStats*` |
| `+0xA8C` | movement state (1 idle, 2 requested, 3 follow, -1 walking) |
| `+0xA98` | run flag (0 walk, nonzero run) |

`CSWSCreatureStats`: experience `+0x068`, **is_pc `+0x06C`**, faction `+0x078`,
strength `+0x0E9` (then dex/con/int/wis/cha, each followed by its modifier byte).

`CSWCObject` (client twin, size `0xFC`): position `+0x024`, orientation `+0x030`.

Animations: idle `0x2710`, walk `0x2712`, run `0x2714`, set through **vtable slot `+0x7C`**.

### The NWScript VM (how to call engine functions)

```
[0x007A3A00] -> CVirtualMachine*
             -> *that -> CVirtualMachineInternal*
             -> +0x3D4 -> implementer (vtable reads back 0x007450E4)
             -> +0x0C  -> routine table, 772 dwords indexed by routine number
```

| What | Value |
| --- | --- |
| `table[243]` | `0x0052F820` `ExecuteCommandCreateObject` |
| `table[21]` and `table[382]` | `0x0053FE00` `ExecuteCommandMoveToPoint` (ActionMoveToLocation) |
| implementer `+0x04` | has_caller_object |
| implementer `+0x08` | caller_object_id (the VM's OBJECT_SELF) |
| internal `+0x1AC` | stack pointer. **Reads 158 on an idle game, NOT 0** |
| internal `+0x1C4` | call_stack_size. **0 when idle; this is the real idle test** |

Dispatch is `__thiscall int(implementer, routineNumber, paramCount)`. Arguments go on the
VM stack, pushed in **reverse declaration order**.

| Helper | Address |
| --- | --- |
| `StackPushInteger` | `0x005D1010` |
| `StackPushString` | `0x005D1090` (takes `CExoString*`) |
| `StackPushEngineStructure` | `0x005D10B0` (`nType`, `void*`); location type = **2** |
| `StackPopObject` | `0x005D10C0` |

`CScriptLocation` is `0x18` bytes: `{Vector position; Vector orientation}` where orientation
is a normalised direction vector. Both the string and the engine structure are **copied** by
the engine, so stack locals are fine and nothing needs freeing.

Blueprint used for phantoms: **`n_commm`** (generic male commoner, appearance 17, faction 5
neutral, no conversation, no plot flag, stock AI). Avoid `p_*` party templates: henchman AI,
a conversation and `NoPermDeath`.

No post-registration is needed. `AddInternalObject` runs inside `CSWSObject::Constructor`
(triggered by passing id `0x7F000000`), `AddObjectToArea` runs inside
`CSWSCreature::AddToArea`, and the renderable client twin is created automatically by the
engine's own object-update pipeline.

### Frame hook

Patch the single IAT dword at **`0x0073D00C`** (`GDI32!SwapBuffers`, `__stdcall BOOL(HDC)`).
Only two code references exist, both once per frame on the initial thread, and the swap is
the last thing in the frame after both `CClientExoApp::MainLoop` (`0x005ED7D0`) and
`CServerExoApp::MainLoop` (`0x004AE860`). 4-byte aligned so the store is atomic. Install
from `DirectInput8Create`, which is verified main-thread.

The loop runs at roughly **317 Hz**, not the frame rate, so per-frame work must be
allocation-free and rate-limited off a wall clock.

### Other useful addresses

| Address | Function |
| --- | --- |
| `0x004B2820` | `CServerExoApp::GetPlayerCreature` |
| `0x004AE750` | `CServerExoApp::GetGameObject` |
| `0x004AE6B0` | `CServerExoApp::GetModule` |
| `0x004FA100` | `CSWSCreature::AddToArea` |
| `0x0050DFD0` | `CSWSArea::AddObjectToArea` |
| `0x004CD1A0` | `CSWSObject::SetPosition` (raises the position dirty bit) |
| `0x004CD170` | `CSWSObject::SetOrientation` |
| `0x004CCD80` | `CSWSObject::ClearAllActions` |
| `0x004EDBA0` | `CSWSCreature::ForceMoveToPoint` (proper synchronous teleport) |
| `0x00516630` | `CSWSCreature::WalkUpdateLocationDistance` (see trap 4) |
| `0x004AF3D0` | `CServerAIMaster::RemoveObject` (writing -1 to ai_level does NOT work) |
| `0x004F8B60` | `CSWSCreature::AddMoveToPointAction` (17 args, undecoded bitmask) |
| `0x00547F60` | `ExecuteCommandCreateItemOnFloor` (used for shared ground items) |
| `0x0052FF20` | `ExecuteCommandDestroyObject` (used to despawn them) |
| `0x005D1030` | `CVirtualMachine::StackPushFloat` |
| `0x005D10D0` | `CVirtualMachine::StackPushObject` |

Globals: `VIRTUAL_MACHINE_PTR 0x007A3A00`, `APP_MANAGER_PTR 0x007A39FC`,
`AURORA_PTR 0x007A39F8`, `RULES_PTR 0x007A3A28`.

### 4.3 Health, damage and death

**`+0xE0` is MAX hit points. Current is `+0xDC`.** That off-by-four is the whole reason the
dashboard used to read `hp 0` while the character sheet said 25/28. Verified two ways:
`CSWSObject::SetCurrentHitPoints` (`0x004CC280`) writes `+0xDC`, and
`ExecuteCommandSetMaxHitPoints` writes `+0xE0` and *then* calls it.

| Offset | Field |
| --- | --- |
| `+0x0DC` | **current hit points** (u16 meaningful) |
| `+0x0E0` | max hit points, as stored (u16) |
| `+0x0E4` | temporary hit points |
| `+0x0F8` | `plot` — blocks **all damage**, not just death |
| `+0x200` | `min1hp` — damage lands, health floors at 1, death effect early-outs |
| `+0x4E0` | in combat (0/1) |
| `+0x504` | attack target as NWScript reports it |
| `+0x9C8` | `CSWSCombatRound*` |
| `+0x9C8` → `+0x9CC` | **the persistent target the retarget logic actually reads** |
| `+0x9D4` | mirror of `stats.is_pc`; switches the death threshold from 0 to **-10** |
| `+0x920` / `+0x924` | perception list and count; an enemy must perceive you to target you |

**Writing `+0xDC` is enough to move the health bar.** There is no dirty flag for health:
`CSWSMessage::ComputeUpdateRequired` diffs live health against a per-player snapshot every
update. That also resolves part of the old "unexplained" note in 7.6.

All damage funnels through one path, each link with exactly one caller:
`CSWSEffectListHandler::OnApplyDamage` (`0x004DFA40`) → `CSWSCreature::DoDamage`
(`0x004F3830`) → `CSWSObject::DoDamage` (`0x004CCF80`).

**Never replicate damage numbers.** `DoDamage` applies a per-difficulty multiplier,
temporary hit points, and damage reduction/resistance/immunity before subtracting, all
per-instance. Replaying "took 7 damage" on the far side does not produce 7. Replicate
absolute health.

There is **no `EffectDeath` routine in KOTOR** (NWN has one; this does not). To kill
deterministically, apply an instant lethal damage effect — that runs the real death path
including animation, corpse, XP and the OnDeath script:

```
EffectDamage        routine 79   handler 0x00531AE0   (amount clamped to 0..10000)
ApplyEffectToObject routine 220  handler 0x0052E570
```

Death itself is `GetDead`: health `<= 0`, or `<= -10` for a party member.

⚠ **Routine-table trap:** some handlers occupy *two* slots — `0x00539610` is both
`GetCurrentHitPoints` (49) and `GetMaxHitPoints` (50), exactly as `0x0053FE00` is both 21
and 382. Scanning the table for a handler address and taking the first hit can therefore
give you the wrong routine.

### 4.4 Ground items: the exact "is it on the floor" test

**An item is on the floor if and only if `CSWSItem + 0x268` (possessor) is `0x7F000000`.**
This is the engine's own predicate: `CSWSCreature::AIActionPickUpItem` gates the entire
pickup on it at `0x005174E9`, and `AcquireItem` re-validates it.

Pickup **moves** the item — same allocation, same object id — and
`CSWSItem::RemoveFromArea` sets area id to `0x7F000000` and position to `(0,0,0)`.
So the old area-id probe did work, but only as a proxy; it also fires on a module unload
and inside the brief window in `AddToArea` where an item is legitimately area-less.

**The exception that matters:** if the item merges into a stack the picker already carries
(credits, medpacs, grenades, ammo), `CItemRepository::AddItem` **destroys** the object and
its id becomes permanently unresolvable. That is still a pickup, so both outcomes must be
handled. Object ids are never recycled — the counter only increments.

`+0x26C` (`item_repo`) is **not** related: it is the repository an item owns as a
*container*. Using it as a pickup test would be completely wrong.

To hand an item to a specific creature rather than destroying it everywhere:
`CServerExoApp::CreatureAcquireItem` (`0x004AEB40`) takes an object id and a resref and is
the direct counterpart of the `CreateItemOnFloor` already in use.

### Ground items

⚠ **`GetGameObject` hands back the `CSWSObject` inside an item, not the `CSWSItem` base**
(verified live — see 7.1). Every offset below that is relative to the `CSWSItem` base is
therefore `0x10` further along than what the lookup returns. Check where the object id
lands — `+0x14` means you have the base, `+0x04` means you have the inner `CSWSObject` —
before indexing anything.

`CSWSItem` size `0x298`, **vtable `0x00748DA0`**. Unlike `CSWSCreature` it does NOT embed
`CSWSObject` at offset 0: a `CSWItem` base occupies `0x00`-`0x0F` and **`CSWSObject`
starts at `+0x10`**. So relative to the `CSWSItem` pointer, object id is `+0x14`, tag
`+0x28`, area id `+0x9C`, position `+0xA0`, client twin `+0x234`. Getting this wrong reads
plausible-looking garbage, so `Odyssey.ResolveServerObject` tests where the object id
actually lands rather than assuming either layout.

Two NWScript routines do the work. Their **routine numbers are not hardcoded** — unlike
`CreateObject` (243) and `ActionMoveToLocation` (21), which were verified live, these were
never confirmed, so the plugin scans the 772-entry routine table for the handler address
the symbol database names and uses the index it finds:

```
CreateItemOnFloor(string sTemplate, location lLoc, int bUseAppearAnimation)   -> 0x00547F60
DestroyObject(object oDestroy, float fDelay, int bNoFade, float fDelayUntilFade) -> 0x0052FF20
```

An item created this way gets a client twin automatically, exactly as creatures do, so it
renders and is clickable with no extra work. Verified live: `client_twin 0x1AEE9DF0`,
`visible 1`.

---

## 5. How to run it

### The two-player demo, one command

```powershell
dotnet build D:\Development\kotor-coop\KotorCoop.slnx
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
# and copy the same dinput8.dll into D:\Games\swkotor-gog-p2

pwsh tools\demo.ps1          # launch both, load saves, connect the agents
pwsh tools\demo.ps1 -Save 2  # a different save slot (1 = top of the list)
pwsh tools\demo.ps1 -Stop    # shut it all down
```

Instance 1 loads its save automatically. Instance 2 currently does not (7.2), so the
script prompts once: click Load Game / Game 1 / Load in that window and press Enter.
Everything else, including both agents, is automatic.

Drop an item from either side (the agents watch these paths):

```powershell
Set-Content "$env:TEMP\kotorcoop-demo\drop1.txt" g_w_lghtsbr01
Set-Content "$env:TEMP\kotorcoop-demo\drop2.txt" g_i_medeqpmnt01
```

Interactively, the agent dashboard drops on **D**. The drop file exists because a
redirected or scripted agent has no console to read a keystroke from.

### The pieces by hand

```powershell
dotnet test D:\Development\kotor-coop\KotorCoop.slnx        # 82 tests

$agent = "src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe"

# instance 1 normally; instance 2 needs the renamed mutex
& $agent launch --windowed --load-save 1 --game-path 'C:\GOG Games\Star Wars KOTOR (GOG)\swkotor.exe'
& $agent launch --instance 2 --game-path 'D:\Games\swkotor-gog-p2\swkotor.exe'

# drive an instance that is already sitting at the menu into a save
& $agent load --pid <pid> --load-save 1

# one agent per instance. --pid, NOT --process: two swkotor.exe are running
& $agent run --pid <pid1> --kotor --host --port 27140 --name Lanik
& $agent run --pid <pid2> --kotor --join 127.0.0.1:27140 --name Malak
```

Mock-only co-op, no game needed:

```powershell
$mock = "src\KotorCoop.MockGame\bin\Debug\net10.0-windows\KotorCoop.MockGame.exe"
& $mock --host --name Revan
& $mock --join 127.0.0.1 --name Malak
```

### Diagnostics

`agent scan --pid <n>` gives an interactive REPL with `creatures`, `int <value>`,
`find <aob>`, `near <addr>`, `dump <addr> [n]`, `ptr <addr>`, `snap`, `diff changed|same`.
Each plugin logs to `kotorcoop-plugin.log` beside its own `swkotor.exe`, which is the
reason for two separate game folders rather than launching one twice.

Every tool in `tools/` takes `-ProcId <n>` to pick between the two instances.
`tools\creatures.ps1 -ProcId <n>` is the quickest way to see whether a phantom exists and
where it is.

---

## 6. Traps, all of which cost real time

1. **`FullScreen=0` alone is silently ignored.** KOTOR 1 needs `AllowWindowedMode=1` too.
   Without it the game takes an exclusive fullscreen display mode, and then GDI screen
   capture returns a **stale frame that never updates** while DirectInput **ignores
   injected input**. The tooling still appears to run: three full discovery runs were
   burned "successfully" walking a character that never moved. Verify by checking the
   desktop resolution and that the client area has a non-zero screen origin like `(8,31)`.
2. **The Windows firewall prompt steals focus.** The first UDP bind raises a dialog hosted
   by `PickerHost.exe`, which takes foreground so injected input stops reaching the game.
   Its window has an **empty title**, so closing dialogs by title misses it. Kill
   `PickerHost.exe` specifically.
3. **A position write to `+0x90` sticks only if nothing is driving that creature.**
   Measured overwrites over ~5 s: idle player 0, idle NPC 0, party member running follow-AI
   **319** (~64/s), client twin `+0x24` **252**. It is not client-versus-server; it is who
   owns the movement.
4. **`WalkUpdateLocationDistance` does not work standalone.** Measured: returns 1, never
   writes its `newPos` argument, creature never advances. Its third argument *is* written,
   with the unit direction, so the documented argument roles are wrong. It is an internal
   helper of `AIActionMoveToPoint` and depends on state the action sets up. Use the action
   queue instead.
5. **Setting commandable (`+0xE8`) to 0 stops the AI running the action queue**, so a
   creature made non-commandable ignores every move action. Counter-intuitive if you set it
   to "stop the AI interfering".
6. **The VM stack pointer is 158 on an idle game, not 0.** Guarding on `sp != 0` refused
   three spawn attempts out of four. Only `call_stack_size == 0` is a valid idle test;
   check stack neutrality afterwards instead.
7. **A 12-byte magic signature was not unique enough.** The scanner takes the first hit, hit
   a false match at a lower address, and the agent silently published garbage
   `(0, 0, 2.4e29)` to every peer. The signature is now 16 bytes (magic + version + guard
   `0xC0FFEE01`) and a mismatch is fatal rather than a warning.
8. **The module filter hides every peer when the two sides name areas differently.** KOTOR
   reports `area1`, the mock reports `end_m01aa`. Use `--no-module-filter` when bridging
   different games.
9. **Never sweep your own address space.** Full sweeps against a separate process are fine
   (120 MB in 0.1 s on live KOTOR), but the same code against the calling process
   intermittently kills the CLR with `0x80131506` inside `coreclr.dll`. Snapshot buffers
   also live inside the space being captured, so each capture swallows the previous one.
   `TakeSnapshot`/`FindPointersTo` grew `minAddress`/`maxAddress` for this.
10. **Differential float scanning loses badly on KOTOR.** ~200 MB of the process is graphics
    memory, and the translation rows of per-frame transform matrices are indistinguishable
    from a position by any static test. Exact-integer scanning wins: `int 3155` gave 8 hits
    in 0.4 s versus millions of float candidates that never converged.
11. **NativeAOT does support `win-x86`**, which is why the plugin is C#. It shells out to
    `link.exe` and `vswhere.exe`, so it only builds inside an MSVC developer environment.
12. **A leftover `KotorCoop.Agent.exe` locks its own binary** and the next build fails with
    MSB3027. Kill stray agent/mock processes before rebuilding.
13. **An area transition destroys and recreates every object**, so all addresses change and
    the plugin's phantom list goes stale. Untested.
14. **`SetCursorPos` is invisible to KOTOR.** The game reads the mouse through DirectInput,
    which watches the injected-input stream; `SetCursorPos` moves the pointer without
    generating that input, so the game never registers a hover and no object name ever
    appears. A grid sweep looking for tooltips found nothing at all until the move was
    reissued through `SendInput` with `MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE`. Clicks
    worked the whole time, which makes this easy to misread as "the game ignores hovers".
    `tools\click.ps1` now moves via `SendInput` and takes `-HoverOnly`.
15. **The name band under an object is a *selection* label, not a hover tooltip.** It stays
    on the last selected object regardless of where the cursor goes, so using it as a
    "what is under the cursor" oracle silently reports the same object for every probe
    position. `tools\tooltip.ps1` detects the band; just do not read it as hover state.
16. **A phantom shows no name band at all**, because the spawned commoner has no first or
    last name set. Do not use "no tooltip" as evidence that an object is absent.
17. **The plugin outlives the agent.** It is loaded by the game, so a restarted agent
    inherits whatever the previous session spawned. Without an explicit reset those items
    stay on the floor forever, owned by a slot nobody tracks; `KotorGameBridge` now clears
    all item slots the moment it finds the plugin block.
18. **Both game copies must be separate folders**, even though the mutex rename means one
    folder would technically run twice. Each plugin writes `kotorcoop-plugin.log` beside
    its own executable, and two instances sharing a folder interleave into one file.
19. **KOTOR's menu cursor is not the OS cursor.** It is accumulated from DirectInput
    relative deltas and clamped to the client area, so putting the OS pointer on a button
    does not put the game's cursor there. Verified directly: a click landed on exactly the
    right screen pixel for "Load Game" while "New Game" stayed highlighted. This is why
    menu automation works for a window at its launch position and fails for one that has
    been moved. See 7.2.
20. **`SendInput` silently does nothing if `cbSize` is wrong.** The x64 `INPUT` struct is
    40 bytes; a P/Invoke declaration that marshals to any other size makes every call a
    no-op that returns 0, and the cursor simply never moves. An hour went into "the game
    ignores injected input" that was really a struct with two extra padding fields.
21b. **The client silently drops eight animation constants.**
    `CSWCCreature::ClientServerSyncHandler_Animation` (`0x0060C100`) opens with an explicit
    reject list covering attack (`0x2719`, `0x277D`), parry (`0x271C`), dodge (`0x271B`),
    damage (`0x271E`) and the three death poses (`0x2716`, `0x2718`, `0x27AC`). Writing any
    of them to `+0xD4` and raising the dirty bit does **nothing at all** — the write
    succeeds and no animation plays. Death poses must go through
    `SetAmbientAnimationState` (`0x004EFEF0`) followed by `SetAnimation(0x2710)`; a real
    weapon swing needs `CutsceneAttack` (routine 503).
21c. **Verifying a field is not verifying a behaviour.** The phantom ownership guard set
    exactly the values it was supposed to, was confirmed live by reading them back, and was
    still badly wrong (see 7.4). Nothing was in combat at the time, so the failure mode
    could not appear. When a change is about how the engine will *behave*, the test has to
    provoke the behaviour.
21. **A one-element byte array holding 0 is falsy in PowerShell.** `if (-not $b)` on the
    result of a successful 1-byte read of an unpaused game reports "read failed". Compare
    against `$null` explicitly.

---

## 7. Next steps, in priority order

### 7.0 Staging a real fight on demand is harder than it sounds

Worth knowing before anyone repeats it. The Endar Spire's `end_sith2` / `end_sith3` sit at
(46.78,33.27) and (45.18,32.45) and are **completely inert**: teleporting the player next
to them produced no aggro over 15 s, and clicking one selected it (the "SITH SOLDIER" name
band appeared) without starting a fight. They appear to be scripted placements waiting on a
trigger rather than live encounters.

That is why the verified combat run in section 1 applied damage by writing memory. It
demonstrates the replication, not the fighting. To get a genuine fight for the next round,
the options are: walk to an encounter that is actually armed, fire the trigger that arms
these, or command an attack through the engine — `ActionAttack` is NWScript routine 37,
handler `0x0052E7F0`, and the plugin already has the machinery to dispatch a routine with
OBJECT_SELF redirected (that is how `ActionMoveToLocation` and `ActionPickUpItem` work).
The last of those is the cheapest and is the recommended next step.

`tools\sethp.ps1 -ProcId <n> -ObjectId 0x14 [-Set <hp>]` reads or writes a creature's
current health by object id, which is how the run above was staged.

### 7.1 Item pickup — VERIFIED end to end

`ItemController.ProbeOneSlot` now uses the engine's own predicate — an item is on the
floor exactly when `CSWSItem+0x268` (possessor) is `0x7F000000` — instead of comparing area
ids. See 4.4.

The first live run of that change **failed usefully**. The client spawned its copy of a
dropped lightsaber and 36 ms later reported it gone:

```
15:38:55.272  item slot 0: spawned item 0x10001 as object 0x131 at (18.45, 21.79, -1.27)
15:38:55.308  item slot 0: item 0x10001 (object 0x131) gone (destroyed, or merged into a stack)
```

and because a local vanish is announced to the session, that false positive propagated a
removal and deleted the item in *every* instance — the host's dashboard dropped to
`ground items (0)` immediately after reporting the drop.

It took **three attempts** to fix, and the reason is worth recording because it is a trap
anyone touching item code will hit.

`CServerExoApp::GetGameObject` returns, for an item, a pointer to the **`CSWSObject`
embedded inside** the `CSWSItem` — not the `CSWSItem` base. Instrumenting the probe proved
it outright:

```
item slot 0: spawned item 0x10001 as object 0x12E at (18.45, 21.79, -1.27), area 0x1
item slot 0: item 0x10001 (object 0x12E) gone (ptr 0x19153B88, id@+0x14 0x0)
```

The pointer is perfectly good; `+0x14` reads 0 because the object id is at `+0x04` of what
was returned. Every field indexed from the `CSWSItem` base — including the possessor at
`+0x268` — is therefore off by `0x10`.

This is also why the *original* area-id probe worked: `Odyssey.ResolveServerObject` tests
`+0x04` first, that test matches for items too, and it returns the pointer unchanged — so
area id at `+0x8C` landed correctly by accident of the same shift.

The probe now accepts **either** shape (object id at `+0x14` = `CSWSItem` base, at `+0x04`
= the `CSWSObject` inside it, in which case subtract `0x10`) and sanity-checks the vtable
before trusting a field `0x268` bytes in. There is also a three-probe grace period after a
spawn.

The two earlier attempts failed because each fixed a real-looking problem that was not the
cause: first an over-strict vtable check applied to the wrong pointer, then a timing guard.
Guessing cost two full rebuild-and-relaunch cycles; **one diagnostic log line found it
immediately**.

**Verified working**, run of 2026-07-30, pid 57956 with a mock peer:

```
item slot 0: spawned item 0x10001 as object 0x130 at (18.45, 21.79, -1.27), area 0x1
   ... item stays on the floor, dashboard reads "ground items (1)" ...
pickup: player 0x7FFFFFFF told to take object 0x130
item slot 0: item 0x10001 (object 0x130) taken by creature 0x7FFFFFFF
   ... dashboard reads "ground items (0)" ...
```

So the whole chain holds: spawn, persist without a false positive, a real engine
`ActionPickUpItem`, detection through the engine's own possessor predicate — which also
names the creature that took it — and removal propagated to the session. Trigger a pickup
with `P` on the dashboard, or by writing `pickup` into the drop file.

### 7.1b Older note — what the pickup chain has to prove

Drop and despawn are both proven. What has **not** been observed is the third leg: a
player picking an item up locally, and it disappearing in the other game.

The mechanism is written. `ItemController.ProbeOneSlot` checks one live slot every 20
frames and treats the item as gone when its object id no longer resolves, or resolves to
an object no longer in the area it was spawned into — which is what a pickup does, since
pickup moves the item into an inventory rather than destroying it. It then reports
`ItemStateVanished`, the bridge picks that up in `CollectVanishedItems`, and the session
broadcasts a `Remove` op that makes every other instance destroy its copy. The destroy
half of that chain is proven independently (see 1).

What is missing is one confirming run. It was not obtained because **the automated harness
could not land a mouse click on the item**: KOTOR needs a pixel-accurate click on a small
floor model, the camera sits close behind the character so the item is usually occluded,
and the two obvious oracles both misled (traps 14-16). This is about thirty seconds of
work for a human with a mouse:

1. `pwsh tools\demo.ps1`, load a save in both.
2. `Set-Content "$env:TEMP\kotorcoop-demo\drop1.txt" g_w_lghtsbr01`.
3. In instance 2, walk onto the item and click it.
4. Expect `item slot 0: ... left the floor - picked up locally` in instance 2's plugin log,
   then `destroyed` in instance 1's, and `ground items (0)` in both agent readouts.

If the probe turns out not to fire, the likely cause is the area-id test: confirm what
`+0x9C` (`CSWSItem` -> `CSWSObject.area_id`) reads once the item is in an inventory. If it
keeps its old area id, switch the test to the item's parent/repository pointer
(`CSWSItem.item_repo` at `+0x26C`) becoming non-null instead.

### 7.2 Auto-load is reliable for one instance and not for two

`agent launch --load-save <n>` drives the main menu into a save with no human input, and
`agent load --pid <n> --load-save <n>` does the same to an instance already running. It
waits for `GamePhase.MainMenu` before clicking (see `GameLifecycle`) and for
`GamePhase.InWorld` after, so it never guesses at sleeps.

**It works every time for the first instance and never for the second.** The cause is
identified and the fix is calibration work:

KOTOR's menus do not read the OS pointer. The game accumulates DirectInput **relative**
deltas into a cursor of its own, clamped to the client area. Moving the OS pointer onto
"Load Game" therefore highlights whatever the game's own cursor happens to be over -
directly observed: the click landed at the right screen pixel and "New Game" stayed
highlighted. It happens to work for a window at the launch position and fails for one
placed beside it.

Pinning the game cursor by sending a large negative relative move (clamping it to the
top-left) and then stepping out by the wanted offset **does** move it, so the approach is
sound, but it is not one unit per pixel: the game applies a mouse sensitivity multiplier.
Calibrating that - move a known delta, find the cursor, solve for the scale, ideally
reading sensitivity out of `swkotor.ini` - is the remaining work. Keyboard navigation of
the menu is the other candidate and was not tried properly.

Until then `tools\demo.ps1` loads instance 1 automatically and prompts once for instance 2.

### 7.3 Finish the phantom

- **Facing on arrival.** The engine picks it from the path direction; arbitrary once stopped.
  Set orientation explicitly after the move completes.
- ~~**Appearance.**~~ Done. The remote player's `appearance.2da` row now travels in
  `PlayerState` and is written to the phantom's `+0xA60`; the engine's own appearance
  update pushes it to the render side next frame. Verified: phantoms read row 114,
  matching the PC, not the `n_commm` blueprint's 17. Note this is a **single u16 write**,
  not the `0x24`-byte memcpy of `+0xA50` the old note suggested — copying the whole
  struct would also drag across the other player's equipped-item object ids, which are
  meaningless in this process.
- **Multi-peer.** Still only ever tested with one phantom. Spawn is rate-limited to one
  per 30 frames, capped at 8.
- **Module transitions.** Detect an area change (area id at `+0x8C`, or the module
  pointer) and re-spawn phantoms rather than driving stale pointers. The same applies to
  ground items, which currently keep their spawn-time area id.
- **Name plates.** Phantoms have no name at all, so KOTOR shows no label (trap 16). Set
  the creature's first name to the peer's name.
- **Goal-resend threshold.** The phantom only re-paths when the peer has moved more than
  2.5 units, so it settles up to 2.5 units from the true position — visible as a slight
  lag when a peer stops. Worth lowering once it is clear how much churn the pathfinder
  tolerates.

### 7.4 Ownership, and what "seamless co-op" needs next

The target is NWN / Souls-seamless: each player drives exactly one character and cannot
command anyone else's.

**What the architecture already guarantees.** Two full games are bridged, not one game
with two inputs, so a peer physically cannot control another peer's PC - different
processes, different saves. A phantom is not a party member, and KOTOR only lets you
command party members, so there is no route to ordering a peer's avatar around either.
That half is free.

**What was added, and the bug it originally shipped with.** A phantom is marked as
belonging to a remote player when it spawns (`Odyssey.MarkAsRemotePlayer`): `CSWSObject.plot`
(`+0xF8`) is set so this instance cannot kill it, since the remote player is the only
authority on whether their character is alive.

The first version **also copied the local player's faction onto the phantom**, on the
reasoning that this would stop anything treating it as an enemy. That was wrong in a way
worth recording, because the fields were verified correct and the behaviour still was not:

- Faction **0 is PLAYER**. Giving a phantom faction 0 makes enemies evaluate it exactly as
  they evaluate a PC — a valid, hostile target. It stops *allies* attacking it, which is
  the half that was checked, and actively invites *enemies*, which was not.
- `plot` does more than prevent death: `OnApplyDamage` (`0x004DFAC8`) zeroes every damage
  component, so health never moves.
- `CreateNewAttackActions` (`0x005B6980`) releases a target only when it reads dead, and
  contains no plot, `min1hp` or health check at all.

Together those make an **immortal aggro sink**: the first enemy to pick a phantom attacks
it forever and never attacks the real local player again. Now fixed by simply not touching
the faction — `n_commm` ships as faction 5 (Neutral), which reads as ~50 reputation both
ways, so enemies and allies both ignore it.

The lesson generalises: verifying that a field holds the intended value is not verifying
that the engine behaves as intended. Two live instances sat side by side with the fields
set correctly and the defect invisible, because nothing was fighting.

A better phantom (designed, not built) keeps damage visible without the sink: clear `plot`,
set `min1hp` (`+0x200`) so damage floors health at 1, clear bit `0x04` of `+0x9F0` so it
can never swing back, and call `CSWSCreature::PacifyCreature` (`0x004FD960`) to release
aggro when the peer leaves combat. `min1hp` alone re-creates the lock, so the pacify call
is not optional.

Relevant offsets: `CSWSObject.plot +0xF8`, `dead_selectable +0xF4`, `min1hp +0x200`,
`CSWSCreatureStats.faction_id +0x78`, `CSWSCombatRound + 0x9CC` (the target the retarget
logic actually reads — not `+0x504`, which is what NWScript reports).

**What is still open, and it is the real work:**

- **Party duplication.** Both instances run their own copy of Trask, Carth and the rest,
  independently. Nothing reconciles them, so they drift apart immediately. Seamless co-op
  needs a decision here: either the host is authoritative for party members and clients
  suppress or hide their duplicates, or party members are assigned an owning instance
  each. Neither is implemented.
- **Combat is entirely uninvestigated.** Nothing is known yet about whether an enemy
  engages a phantom, whether the two instances agree on who is in combat, whether combat
  rounds line up, or what happens when the same enemy dies in one game and not the other.
  Enemies are per-instance, so the likely starting position is that each player fights
  their own private copy of every encounter and the results diverge. Replicating combat
  properly means picking an authority for enemy state and damage, which is a bigger design
  question than anything solved so far.
- **Death.** Phantoms cannot die, but nothing yet replicates a real player's death.

### 7.5 Publish local state from inside the plugin (task #9, still open)

The plugin knows nothing about the local player; the external agent does the reading. Moving
that in-process would give facing, module name and correct health for free, and remove the
`PlayerCreature` pointer handshake. `CServerExoApp::GetPlayerCreature` is at `0x004B2820`.

### 7.6 Correctness gaps worth closing

- **`hit_points +0xE0` is not the Vitality the UI shows** (read 12 while the sheet said
  25/28). Current vitality is probably in `CSWSCreatureStats`. The profile schema also only
  reads `int32`, while Vitality is two `uint16`.
- **Facing conversion in `KotorGameBridge` is unverified.** reone and xoreos agree that for
  GIT data facing is `-atan2(XOrientation, YOrientation)` with 0 = +Y and forward
  `(-sin f, cos f)`, but the runtime `Vector` at `+0x9C` has not been checked against a
  turning character.
- **Open inconsistency:** `docs/re/object-model.md` records that a server->client push
  requires both the visibility flag `+0x1F8` and the position dirty bit `+0x1FC`, and only
  `SetPosition` raises that bit. That implies a raw `+0x90` write should not update the
  render at all, yet it demonstrably does. Unexplained. Do not build on either account.
- **Only `--kotor` mode uses `KotorGameBridge`.** `profiles/kotor1.json` is still an
  unusable template and `profiles/kotor1-session.json` is an `Absolute` heap anchor kept
  only as evidence. Consider deleting or clearly marking both.

### 7.7 What real co-op still needs beyond the demo

The demo proves the transport and the two hardest engine primitives. The gap to a mod
someone would actually play is mostly *scope*, not mechanism:

- **Item identity is a resref, not an item.** A dropped lightsaber replicates as the
  string `g_w_lghtsbr01`, so upgrades, charges, stack size and item properties are lost,
  and the item is *copied* rather than moved — nothing removes it from the dropper's
  inventory. Real sharing means serialising `CSWSItem` state and hooking the inventory.
- **World flags are not wired to the game.** `EventFlagStore` is complete and tested but
  nothing reads or writes KOTOR's own globals, so doors, quests and dead bosses do not
  agree between instances. This is the "which of the thousands of booleans are global
  truth" problem and is the largest remaining design question.
- **No combat, dialogue or cutscene coordination.** Two players in the same conversation
  or the same fight is entirely unexplored.
- **Health is wrong.** See 7.4; the dashboard shows `hp 0` because `+0xE0` is not Vitality.

### 7.9 Two live bugs the transition research found, and the next build

**Both of these are latent right now and will fire on the first module transition.**

1. `CombatController._killed[]` is never cleared. After a transition the id counters reset,
   so those remembered ids name completely different objects and the controller will
   suppress kills on innocent ones.
2. `PhantomController._phantoms[]` holds raw `CSWSCreature*`. `UnloadModule` frees every
   object, so the first frame after a transition dereferences freed memory —
   `SetAnimation` reads a vtable off it and calls through it. The same applies to
   `SharedBlock+0x258 PlayerCreature`, which the agent writes and the plugin trusts.

The fix is an epoch check as the **first** statement in `PluginHost.OnFrame`: gate on
`loading_module == 0 && server_mode == 2`, compute an epoch key from
`(objectArrayPtr, moduleObjectId, moduleNameHash)`, and on a change clear every controller's
state *before* any of them runs. Plus a pre-store flush of phantoms and replicated items,
so they are not baked into the save (4.8).

**Recommended next build, in order:**

0. **Freeze enemy publication while the local player is in dialogue** (4.10, 7.10). Small —
   read the input class at `CClientExoAppInternal + 0x9C` and skip `PublishEnemies` when it
   is 3 — but it closes a hole in combat replication that is already built.
1. **Epoch invalidation + pre-store flush.** Fixes the two bugs above and protects saves.
2. **Door sync.** Assessed as the highest impact for the lowest risk of anything left: a
   door open for one player and shut for the other is the most visible possible desync.
   Read `door+0x2CC` (open state) and `+0x2C4` (locked); apply through
   `CSWSDoor::OpenDoor` (`0x00589C70`) / `SetOpenState` (`0x00589600`), never by poking the
   field — the walkmesh follows the field but **the model does not**, so a raw write gives
   a door you can walk through that still looks shut. Locked is a plain write. Reuses the
   existing reliable `FlagOp` transport; no id translation needed.
3. **Disable encounters on the client.** Two dword writes per encounter at area load
   (`+0x238 Active = 0`, `+0x278 Started = 1`). Encounter spawns are the one engine-side
   source of id drift and would otherwise break the key everything else rests on.
4. **Container contents**, after 7.1 is confirmed — it reuses the same item machinery.

### 7.10 Dialogue: the design that follows from 4.10

This section is **design reasoning, not reverse engineering** — it is derived from the
verified facts in 4.10 and 4.7, and none of it has been tried. A fuller write-up was
commissioned and did not survive repeated API overloads; if it arrives later, merge it.

The starting point is that **each instance owns a private copy of every NPC**, so nothing
in the engine prevents both players talking to "the same" character at once. There is no
conflict to resolve at the engine level — only at the design level.

**Option 1 — both talk independently.** Costs nothing to implement, because it is what
already happens. Each player gets the full conversation in their own game, and the
*outcomes* converge through the global-variable channel (4.7), whose merge-upwards rule is
idempotent and order-independent — exactly the right shape for quest state. What does
**not** converge is side effects: a conversation that grants an item, awards XP or recruits
a party member runs twice, once per instance. For KOTOR that is duplicated loot and double
XP, which is a balance problem rather than a correctness one.

**Option 2 — lock the NPC.** First player to open a conversation claims it over the
existing reliable op channel; the other instance refuses to start one with that object id
until it is released. Object ids match across instances (4.5), so the claim needs no
translation. Fixes duplicate rewards. The cost is that the second player is locked out of
story content they cannot even watch — the conversation is happening in another process, so
there is nothing to spectate. That is a worse experience than the duplication it prevents.

**Option 3 — host-only dialogue.** Cleanest authority story and the worst game: in KOTOR
most of the content *is* the conversations, so the client becomes a passenger.

**Recommendation for the proof of concept: option 1, plus one guard.** It needs no new
mechanism, keeps both players playing, and converges on quest state for free now that the
global channel exists. Accept the duplicated rewards and write them down as a known
limitation.

The guard is required, and it is the part that interacts with work already built. Per 4.10,
a player in conversation has every hostile in *their* instance disarmed on every dialog
tick. Because enemies are host-authoritative, a host conversation would propagate that
pacification to the client. So: **while the local player is in dialogue, stop publishing
enemy state and let the peer hold its last snapshot.** Detect it by reading the input class
at `CClientExoAppInternal + 0x9C` (3 = dialog); freezing publication rather than sending
"all enemies idle" keeps the client's fight coherent for the few seconds a conversation
lasts. The same read is the natural hook for "peer is busy talking" in the UI later.

**The fuller design, later.** Option 2's claim mechanism becomes worthwhile once rewards
matter, and it pairs naturally with replicating the *journal* (4.7) rather than inferring
quest state from globals alone. Suppressing a cutscene for the non-participating player was
not investigated; it is the obvious next question, because a conversation that stages a
cutscene will move that instance's camera and party while the peer keeps playing.

### 7.8 Housekeeping

- Helper script defaults are fixed and every tool now takes `-ProcId`. New tools this
  round: `demo.ps1`, `movewin.ps1`, `wheel.ps1`, `tooltip.ps1`.
- **The repo is still not under version control.** `git init` and commit; there is a
  `.gitignore` already. Consider whether the 2.3 MB `tools/kotor1_0_3.db` belongs in git
  or should be fetched by a script.
- `docs/re/` is 144 KB of research across `object-model.md`, `frame-hook.md`, `spawn.md`,
  `engine-movement.md`, `rendering.md`. Section 11 of `rendering.md` holds corrections to
  `object-model.md` that were never merged in. None of it yet covers items or the
  single-instance guard; section 4 above is the only record of those.

---

## 8. Task list state

| # | Task | Status |
| --- | --- | --- |
| 6 | Locate object list and creature vtable | done |
| 7 | Find and call the creature spawn path | **done, working** |
| 8 | Drive the phantom through engine movement | **done, working** (via action queue, not the researched primitive) |
| 9 | Publish authoritative local state from inside the game | open |
| 10 | Main-thread dispatch | **done, working** |
| 11 | Test writing `CSWSObject.position` | done |
| 12 | Durable player anchor | done (superseded by `KotorGameBridge`) |
| 13 | Phantom walk animation and facing | done (animation); facing on arrival open |
| 14 | Decide the single frame-hook site | resolved: `SwapBuffers` IAT |
| 15 | Run two KOTOR instances at once | **done, working** (mutex rename, 4.1) |
| 16 | Two real instances seeing each other | **done, working**, both directions |
| 17 | Phantom appearance matches the remote player | **done, working** |
| 18 | Shared ground items | drop and despawn **done**; pickup detection unverified, 7.1 |
| 19 | Auto-load a save with no human input | **works for instance 1**, unreliable for instance 2, 7.2 |
| 20 | Keep an unfocused instance running | **done**: the pause is real; `FocusKeeper` clears it, 4.0 |
| 21 | Ownership: peers cannot fight or kill each other | **fixed** after the first version made phantoms an immortal aggro sink, 7.4 |
| 34 | Auto-pause suppressed (six option bits) | **done**: two instances auto-pausing independently is a desync source |
| 35 | Dialogue in co-op | designed (7.10), not built; the enemy-publication guard is the one piece that matters now |
| 22 | Combat across instances | **done, verified**: host-authoritative enemy health and death |
| 23 | Fix current HP (`+0xDC`, not `+0xE0`) | **done, verified**: health reads 12 instead of 0 |
| 24 | Item pickup detection via the possessor field | **done, verified**: drop, persist, engine pickup, detect, remove |
| 25 | Combat participation: aggro, targeting, player death | researching |
| 26 | Dialogue and cutscenes in co-op | researching |
| 27 | Module / area transitions | researching |
| 28 | World and quest state wired to engine globals | researching |
| 29 | Doors, placeables, containers, triggers | researching |
| 30 | Game data file readers (KEY/BIF, RIM, GFF, 2DA) | **done**: `tools\kotorfiles.py`, 4.9 |
| 31 | World/quest globals replicated | **code done, untested in game**, 4.7 |
| 32 | Doors / placeables / containers | doors **built**; unlock leg **verified live** (addendum 0); placeables/containers open |
| 33 | Module transitions: epoch invalidation + pre-store flush | **built** (addendum 0); a real door/trigger transition not yet exercised live |
| 36 | Freeze enemy publication during dialogue | **done** (was 7.9 step 0) |
| 37 | Client encounters disabled | **built** (addendum 0) |
| 38 | Movement fidelity: run gait, 0.6u resend, facing, snap | **built**, awaiting live feel-check (addendum 0) |
| 39 | Equipment sync v1: armor + weapons on the phantom | **built**, untested live; watch for proficiency refusals (addendum 0) |
| 40 | Travel together: clients follow the host across modules | **built, untested in game**, 4.11 (SharedBlock v6) |
| 41 | Phantom name plates: peer's name in the selection band | **built, untested in game**, 4.11 |
| 42 | Player death mirrored as a phantom death pose | **built, untested in game**, 4.11 |
| 43 | In-game Connect flow (plugin-driven save load, then a .gui mod) | designed; CSWGuiSaveLoad::LoadGame 0x6CB0E0, see 4.6 and addendum 0's load path — ⚠ block offsets moved, see 0.2 |
| 44 | Map presence: peers as map-note waypoints | idea recorded, unverified |
| 45 | Journal replication (quest merge) | **built, untested in game**, addendum 0.2 |
| 46 | Client damage counts on the host (enemy report min-merge) | **built, untested in game**, addendum 0.2 |
| 47 | Shared pause across instances | **built, untested in game**, addendum 0.2 |
| 48 | Phantom vulnerability without the aggro sink (no plot, min1hp, pacify) | **built, untested in game**, addendum 0.2 |

---

## 9. One-paragraph summary for a fresh session

kotor-coop is a C# co-op mod for retail KOTOR 1, and it now runs as actual two-player
co-op on one machine. Two GOG instances run side by side — the retail single-instance
guard is just a `CreateMutexA("swkotor")` at `0x00404222`, so `agent launch --instance 2`
starts the second copy suspended, renames that one string, and resumes it. Each instance
gets an external agent (which finds the player by scanning for the `CSWSCreature` vtable
`0x007470C8` and testing `is_pc`) plus an in-process NativeAOT `win-x86` plugin loaded as
a `dinput8.dll` proxy, which hooks the `GDI32!SwapBuffers` IAT slot at `0x0073D00C` for a
main-thread per-frame callback. On that callback the plugin calls the engine's own
NWScript routines: `CreateObject` to spawn a real creature per remote player,
`ActionMoveToLocation` to walk it with pathfinding and animation, and
`CreateItemOnFloor`/`DestroyObject` to materialise and remove shared ground items. Both
players see each other move, both wear each other's real appearance, and an item dropped
by one appears as a genuine engine object at identical coordinates in the other. 82 tests
pass. Startup is close to unattended: `agent launch --load-save <n>` drives the menu into
a save on its own, reliably for the first instance and not yet for the second (7.2), and
an unfocused or minimised instance keeps simulating at full speed, which was measured
rather than assumed (4.0). Three things are written but unproven: item *pickup*
propagation (7.1), and the ownership guard that makes a peer's avatar unkillable and
friendly (7.4). Combat across instances is completely unexplored and is the largest open
question — enemies are per-instance, so each player currently fights their own private
copy of every encounter, and fixing that means choosing an authority for enemy state.

# Odyssey object model in swkotor.exe (VERIFIED against this build)

## The finding that changed the project

A complete, symbol-labelled Ghidra reverse-engineering of `swkotor.exe` v1.0.3 exists
publicly, published as a queryable SQLite database of class sizes, vtable addresses, member
offsets, function addresses and global pointers.

- Thread: https://deadlystream.com/topic/11948-kotor-1-gog-reverse-engineering/ (by LaneDibello)
- Repo: https://github.com/LaneDibello/Kotor-Patch-Manager
- Database: `AddressDatabases/kotor1_0_3.db`

Contents: 977 classes, 9,710 functions, 4,720 member offsets, 16 global pointers.

**It covers this exact executable.** Its `game_version` table lists
`KOTOR1 / steam 103 / Windows` with sha256
`34E6D971C034222A417995D8E1E8FDD9F8781795C9C289BD86C499A439F34C88`, and the installed
`swkotor.exe` hashes to the same value. Not a similar build: the same bytes. Combined with
the absence of ASLR (image base is always `0x400000`), every address below is directly
usable at runtime.

## Odyssey is Aurora with the prefix renamed

`CSWS*` is server-side, `CSWC*` is client-side, paralleling NWN's `CNWS*`/`CNWC*`. The
`CGameObject` vtable is slot-for-slot identical to NWN's with exactly one deletion (NWN's
`AsNWSPlayerTURD`, the disconnected-player corpse, meaningless in a single-player game).
The large body of public NWN reverse engineering therefore transfers almost verbatim.

Consequence: **KOTOR keeps an internal client/server split even though it is single-player.**
Every server object has a `client_object` back-pointer. This is what explains the mirror
problem recorded in `ARCHITECTURE.md`.

## Verified struct layout

`CSWSCreature`: size `0xAC8`, vtable **`0x007470C8`**. `CSWSObject` (size `0x228`) is
embedded at offset 0, so its offsets apply directly to a creature pointer.

| Offset | Field | Notes |
| --- | --- | --- |
| `+0x000` | vtable | `0x007470C8` identifies a creature |
| `+0x004` | object id | `ulong` |
| `+0x008` | object type | word |
| `+0x018` | tag | `CExoString` = `{char* @0, ulong len @4}` |
| `+0x08C` | area id | `ulong` |
| **`+0x090`** | **position** | **`Vector`, 3 floats - server-side, writable** |
| `+0x09C` | orientation | `Vector`, 3 floats (Euler, not a quaternion) |
| `+0x0D4` | animation | `int` |
| `+0x0E0` | hit points | `int` |
| `+0x0FC` | action queue | `CExoLinkedList<CSWSObjectActionNode>` |
| `+0x224` | client object | `CSWCObject*` |
| `+0x4C0` | party follow info | `CSWSCreaturePartyFollowInfo*` - non-null on party members |
| `+0xA74` | creature stats | `CSWSCreatureStats*` |

`CSWSCreatureStats` (size `0x1B8`): experience `+0x068`, is_pc `+0x06C`,
faction id `+0x078`, strength `+0x0E9` (then dex/con/int/wis/cha, each followed by its
modifier byte).

`CSWCObject` (client twin, size `0xFC`): position `+0x024`, orientation `+0x030`.

## Enumerating creatures: solved

Scan private committed memory for the 4-byte value `0x007470C8`. Every hit is a
`CSWSCreature`. No heuristics, no false positives. Implemented in
`KotorCoop.Memory/KotorGameBridge.cs`.

Verified live on Taris Upper City South, 16 creatures:

```
object       id          tag              position                hp  STR     XP  isPC
0x1BFA2390   0x7FFFFFFF  (none)           ( 98.47,  92.65, -0.00) 12   16   3155     1
0x1BFC46E8   0xC3        carth            ( 95.24,  95.32, -0.00) 40   13   6321     0
0x1C097410   0x12        ithorian         ( 97.22,  91.50, -0.00)  8   13      0     0
0x1C0EEF28   0x02        dia022           ( 72.61, 110.85, -0.00) 28   13      0     0
... 12 more named NPCs (twilek, trooper1, droid1, duros, bith, tar02_*, ...)
```

Cross-validation, all independent:

- Player object id is exactly `0x7FFFFFFF`. Character ids count **down** from `0x7FFFFFFF`
  while normal object ids count **up** from 0, meeting at `OBJECT_INVALID = 0x7F000000`.
- `is_pc = 1` on that object and nothing else: an unambiguous player test.
- Strength 16 and experience 3155 match the in-game character sheet exactly.
- Every z is within a hair of 0, correct for a flat Taris interior.
- `carth` appears with a real tag. Earlier heuristic scanning never found him.

## Writing position: who owns the creature decides

Measured on the live game. A write to `CSWSObject.position @ +0x90` is held at ~125 Hz for
several seconds and the engine's overwrites counted:

| creature | engine overwrites in ~5s | write held? | screen |
| --- | --- | --- | --- |
| player (`is_pc=1`, idle) | **0** | yes | relocated, camera and minimap followed |
| `carth` (party, running follow-AI) | **319** (~64/s) | no | unchanged |
| `ithorian` (idle NPC) | **0** | yes | relocated |
| *client* twin `CSWCObject+0x24` | **252** (~63/s) | no | flicker only |

So the rule is **not** simply client-versus-server. It is: **a position write sticks unless
some system is actively driving that creature.** The player's position is driven by input,
so an idle player does not fight the write. An AI creature executing a follow action
recomputes its position every frame and always wins.

Two practical consequences:

1. **Idle creatures are drivable as remote-player avatars.** This is what
   `KotorGameBridge.TryWriteRemotePeers` does, and it works today with no code injection.
2. Party members are unusable as phantom bodies. `party_follow_info @ +0x4C0` identifies
   them, which is how the bridge excludes Carth automatically.

### Demonstrated end to end

The agent attached to the live game as host, a `MockGame` peer joined over UDP, and the
peer's position was written into the claimed phantom creature. Sampled from the game's own
memory while the peer walked:

```
dia022  (99.60,  92.90) -> (118.93, 125.97) -> (87.85, 103.12)
        (125.11, 112.14) -> (87.79, 120.14) -> (118.68, 97.15) -> (99.41, 130.35)
```

The local player never moved. That is a remote player's movement rendered inside KOTOR.

### What this still does not do

Both reimplementations (reone, xoreos) show that a raw position set performs no walkmesh
raycast, no ground snap, no room reassignment, no trigger check and **no animation change**.
Confirmed by observation: the phantom teleports between positions in an idle pose rather
than walking. Animation is selected purely from a movement-type field that a position write
never touches, and any explicit animation request is dropped while a creature is walking or
running. Making a phantom look convincing needs the engine's movement path, which is what
the in-process plugin is for. See `docs/re/engine-movement.md`.

## Useful addresses (this build)

| Address | Function |
| --- | --- |
| `0x004B2820` | `CServerExoApp::GetPlayerCreature` |
| `0x004AEA40` | `CServerExoApp::GetPlayerCreatureId` |
| `0x004AE750` | `CServerExoApp::GetGameObject` |
| `0x004AE770` | `CServerExoApp::GetCreatureByGameObjectID` |
| `0x004AE6B0` | `CServerExoApp::GetModule` |
| `0x004AED70` | `CServerExoApp::GetObjectArray` |
| `0x0050DFD0` | `CSWSArea::AddObjectToArea` |
| `0x0050DC40` | `CSWSArea::RemoveObjectFromArea` |
| `0x004D8230` | `CGameObjectArray::GetGameObject` |
| `0x004D80E0` | `CGameObjectArray::AddInternalObject` |

Globals: `VIRTUAL_MACHINE_PTR 0x007A3A00`, `APP_MANAGER_PTR 0x007A39FC`,
`AURORA_PTR 0x007A39F8`, `RULES_PTR 0x007A3A28`, `EXO_RESOURCE_MANAGER_PTR 0x007A39E8`.

The NWScript command table is a flat array of **772** DWORD function pointers, with a
`< 772` bounds check at `0x0052C0D4` and its allocation (`push 0x0C10` = 772*4) at
`0x0054C964`. That table is the route to `CreateObject` and every other engine routine.

`CGameObjectArray` is a chained hash table: bucket heads at `+0x00`, index is
`id & (size-1)`, nodes are `{index @0, game_object @4, next @8}`.

`CSWSArea` (size `0x2D4`) holds `game_objects` at `+0x190` as a flat heap array of 32-bit
object ids with the count at `+0x194`. `CSWSModule` (size `0x224`) holds a **single** area
pointer at `+0x220` - Odyssey is single-area-resident, which is why area transitions are
loading screens and why a peer in another module has no meaningful local position.

## Caveats

- `hit_points @ +0xE0` read 12 for the player while the character sheet showed Vitality
  25/28, and 28 for `dia022`. So `+0xE0` is not the Vitality the UI displays; current
  vitality more likely lives in `CSWSCreatureStats`. Unresolved.
- The orientation-to-facing conversion in `KotorGameBridge` is **UNVERIFIED**. reone and
  xoreos independently agree that for GIT data facing is `-atan2(XOrientation, YOrientation)`
  with 0 = +Y and forward = `(-sin f, cos f)`, but the runtime `Vector` at `+0x9C` has not
  been checked against a turning character.
- A module transition destroys and recreates every object, so all addresses change. The
  bridge re-scans after repeated read failures, but this has not been exercised across an
  actual area change.
- ~~The numeric values of KOTOR's internal `GAME_OBJECT_TYPES` enum are not in the
  database.~~ **Recovered independently by two later research passes: creature = 5**
  (`byte[0x00746634]`). Full table in `docs/re/rendering.md`.
- **Correction: `+0xA4C` is not the appearance field.** It reads 0 on all 16 live creatures
  including the PC. The real one is `+0xA60` (`appearance_info.appearance_type`, u16), which
  is the `appearance.2da` row; the PC is row 174 in the current save. See
  `docs/re/rendering.md`.
- **Correction to the "mirror" explanation.** `CServerAIMaster::UpdateState` only pushes a
  server-to-client update when the visibility flag at `+0x1F8` is set **and** the matching
  dirty bit at `+0x1FC` is set (0 = animation, 1 = position, 2 = orientation), and only
  `CSWSObject::SetPosition` (`0x004CD1A0`) raises bit 1. So the reason a raw `+0x90` write
  leaves an idle pose is that `ClientServerSyncHandler_Position` (`0x00611F50`), which
  reselects walk/run from `creaturespeed.2da`, is never invoked.
  OPEN INCONSISTENCY: that mechanism implies a raw write should not update the render at
  all, yet it demonstrably does (the character visibly relocated, camera and minimap
  followed). Something else is refreshing position unconditionally. Not yet explained;
  do not build on either account until it is.

## Why this supersedes the earlier approach

| | Heuristic scanning | Address database + vtable scan |
| --- | --- | --- |
| Creatures found | 1 (the PC only) | **16, all of them** |
| Carth | not found | found, tagged, and correctly excluded as party |
| Player identification | inferred from stats | `is_pc` flag, id `0x7FFFFFFF` |
| Position field | client mirror, unwritable | server-side `+0x90`, writable |
| Names/tags | none | real engine tags |
| Durability | heap address, died on restart | vtable value is static, no ASLR |
| Remote avatar | impossible | **working** |

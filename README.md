# kotor-coop

A .NET proof of concept for co-op in Star Wars: Knights of the Old Republic.

It is an **external agent**: it attaches to the game with `OpenProcess` /
`ReadProcessMemory` / `WriteProcessMemory`, reads the local player out of memory,
replicates it over UDP, and writes remote players back in. Nothing is injected, no DLL is
loaded into the game, and no game file is modified.

Everything above the memory layer is game-agnostic. Everything game-specific is a JSON
offset profile in [`profiles/`](profiles).

## Status

| Piece                                                             | State                     |
| ----------------------------------------------------------------- | ------------------------- |
| Transport, session, interpolation, world-flag sync                | done, tested              |
| Memory layer (scanner, pointer chains, profiles, read/write)      | done, tested against a live process |
| Two-instance co-op against the mock harness                       | **working, verified**     |
| Offset discovery tooling (`autotrack`, `scan`, `int`, `find`, `near`) | done                  |
| **Reading the real player position out of live swkotor.exe**      | **working, verified**     |
| Facing, health and module name from swkotor.exe                   | not yet located           |
| A durable (restart-proof) KOTOR profile                           | needs a multi-level pointer chain |
| Drawing a second player inside KOTOR                              | **not done** - see [limits](#what-this-does-not-do) |

### Two-instance co-op: where it actually stands

Bridging **live KOTOR to a second peer over the network works today**. The mock peer
renders your real character walking around, streamed over UDP:

```
1 Lanik   pos (134.79, 97.84)  ->  (133.50, 98.82)  ->  (132.63, 99.49)
```

Two *KOTOR* instances seeing each other does not work yet, blocked by two separate things:

| Blocker | Status |
| --- | --- |
| **Steam DRM refuses a second instance.** From the original directory a second process exits with code 0 in 0.3s. From a copied directory it gets further but the Steam stub fails with "Application load error 5:0000065434", and `steam_appid.txt` plus `SteamAppId`/`SteamGameId` env vars do not satisfy it. | **Not solvable here.** Going further means defeating the DRM wrapper, which is out of scope. Use a DRM-free (GOG) copy, or run the second instance on another machine or VM: the agent is already networked and does not care. |
| **Nothing in KOTOR to draw a remote player as.** Stock KOTOR has no spare creature object to drive. | Open. The promising route is hijacking a party member (Carth is in the party and is a real creature in the world), but the follow-AI will contend with the writes, and his live creature record has not been located yet. |

The first is an environment problem with known workarounds. The second is the real
engineering, and it is the same wall described in [what this does not do](#what-this-does-not-do).

### Reading live KOTOR

The agent has been run against a real save (Taris, character "Lanik Star") and read the
live player position while the character walked:

```
KotorCoop agent  KOTOR 1 (session-scoped) [pid 37324 swkotor, x86] anchor=0x23066A04
session  hosting on 0.0.0.0:27055
memory   read ok
local    pos (117.79, 89.31)  ->  (119.40, 90.64)  ->  (122.27, 91.81)  ->  (128.48, 87.60)
```

73 unit and integration tests pass, including a real host/client pair over loopback UDP
and a full attach-scan-read-write cycle against a separate live process.

## Quick start: co-op in two terminals, no game required

`KotorCoop.MockGame` is a stand-in game process. It allocates a player structure in
unmanaged memory and renders whatever remote avatars the agent writes back into it. The
agent attaches to it with the same code path it uses on `swkotor.exe`, so this exercises
the real scanner and the real memory reads and writes.

```bash
dotnet build

# terminal 1
dotnet run --project src/KotorCoop.MockGame -- --host --name Revan

# terminal 2
dotnet run --project src/KotorCoop.MockGame -- --join 127.0.0.1 --name Malak
```

Both windows show a top-down map. `@` is you, numbers are other players.

```
WASD  move          M  change module (tests the area filter)
Q/E   turn          F  toggle a shared world flag
                    X  take damage
                    Esc quit
```

Walk around in one window and watch the other. Press `F` in either and the flag flips in
both: that is the world progression channel, not just avatar replication.

## Testing against the real game

```bash
# 1. launch KOTOR windowed (backs up swkotor.ini first)
dotnet run --project src/KotorCoop.Agent -- launch --windowed

# 2. load a save, take control of your character

# 3. find your coordinates and watch them live
dotnet run --project src/KotorCoop.Agent -- track --process swkotor
```

`track` runs a guided differential scan: it takes every float in the process that could be
a world coordinate, then asks you to walk, stop, walk, stop, keeping only the addresses
that behave the way a position does. A few rounds cuts millions of candidates to a
handful. It then prints your coordinates live as you move, plus a profile snippet.

Full walkthrough, including turning a session-scoped address into a durable pointer path:
[`docs/FINDING-OFFSETS.md`](docs/FINDING-OFFSETS.md).

Your install is already identified: Steam `swkotor.exe` v1.0.3, x86, image base
`0x400000`, **no ASLR**, entry point inside a `.bind` packer section. See
[`profiles/kotor1.json`](profiles/kotor1.json).

## Commands

```
KotorCoop.Agent
  launch    start KOTOR, optionally windowed
  track     guided offset discovery + live position readout
  run       attach and run the co-op loop against a completed profile
  scan      raw interactive memory scanner
  info      modules and memory regions of a target
  profiles  list offset profiles

KotorCoop.MockGame
  --host / --join <host[:port]>   run a session
  --name --port --module --tick --interp
  --auto --exit-after <s>         unattended runs, for scripted verification
```

## How it works

```
   swkotor.exe                    KotorCoop.Agent                    network
  +-------------+     read       +-----------------+
  |  player     | -------------> |  ProfileGame    |  PlayerState
  |  structure  |                |  Bridge         | -----------+
  |             | <------------- |  (JSON offsets) |            |
  +-------------+     write      +-----------------+            v
                                                        +----------------+
                                 +-----------------+    |  CoopHost      |
                                 |  SnapshotBuffer | <--|  or CoopClient | <--> UDP
                                 |  (interpolate)  |    |                |
                                 +-----------------+    |  EventFlagStore|
                                                        +----------------+
```

- **Star relay, not mesh.** Clients talk only to the host, which broadcasts the combined
  world. One machine decides what is true. Same trade FromSoftware's netcode makes.
- **Avatars are unreliable, world flags are reliable.** A lost position update is
  superseded 50 ms later; "the door is open" is retransmitted until acknowledged.
- **Remote players are interpolated on a 100 ms delay**, never extrapolated. That is the
  difference between a smooth remote avatar and the rubber-banding Seamless Co-op players
  complain about.
- **A 4-byte flag version rides every snapshot.** Clients detect divergence from that and
  request a full flag set only when it actually differs.

More detail: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

## What this does not do

Being blunt about the gap, because it is the whole difficulty of the project:

**Reading KOTOR is a scanning exercise. Writing a second player into it is not.** Stock
KOTOR has no spare creature slot to drive and no concept of a remote entity, so there is
nothing to write a remote avatar into. `profiles/kotor1.json` is therefore read-only and
the agent honestly reports `write: n/a`. Getting past that means spawning and driving a
real creature through the engine's own object system, which is a reverse engineering
project, not a configuration change.

Also out of scope here: dialogue arbitration (KOTOR's conversation system is a blocking
full-screen cinematic built around one local player), combat state, module transition
gating, inventory, and save handling.

The realistic long-term path for actual KOTOR co-op is
[reone](https://github.com/seedhartha/reone), an open-source reimplementation of the
Odyssey engine, where multiplayer is an ordinary large feature rather than an
archaeology project. This repository is the honest version of what an external agent can
do against the retail binary.

## The in-process plugin

External memory access can read KOTOR perfectly but cannot write to it usefully: the
position field is a per-frame mirror the engine rewrites 63 times a second (measured), so
writes never reach the simulation. Anything that creates or moves a remote player has to
run *inside* the process and go through the engine's own functions.

`src/KotorCoop.Plugin` is that in-process half, and it is **C#** - .NET 10's NativeAOT
supports `win-x86`, which is what makes a managed plugin possible inside a 32-bit game
from 2003.

It is built as `dinput8.dll` and installed beside `swkotor.exe`. Windows searches the
executable's own directory before the system directory for a DLL that is not a KnownDLL,
so **the game loads it itself**: no injector, no `CreateRemoteThread`, nothing to keep
running. All six DirectInput exports are re-implemented and forwarded to the real system
DLL; `DirectInput8Create` is the hook point, because the game calls it during startup.

```bash
# build only
pwsh src/KotorCoop.Plugin/build.ps1

# build and install into the game directory
pwsh src/KotorCoop.Plugin/build.ps1 -Install

# remove it again
pwsh src/KotorCoop.Plugin/build.ps1 -Uninstall
```

Verified working end to end:

```
[19:20:17.344] forwarding to C:\WINDOWS\system32\dinput8.dll (module 0x714F0000)
[19:20:17.344] === KotorCoop plugin loaded into pid 28524 ===
[19:20:17.345] shared block at 0xFFD0000 (592 bytes)
[19:20:17.345] worker thread started
```

The plugin publishes a `SharedBlock` whose layout is byte-identical to the mock harness's
`GameStateBlock`, so the existing tested scanner and `ProfileGameBridge` drive it with
**no new .NET code** - `profiles/plugin.json` is a near copy of `profiles/mockgame.json`.
The agent locates it by signature scan (1 hit, exact match with the plugin's own log) and
reads a heartbeat that distinguishes a live plugin from a stale block in freed memory.

The build script refuses to install a non-x86 image, because an x64 DLL would simply fail
to load and the symptom - game runs, plugin never appears - looks exactly like a code bug.

## Layout

```
src/KotorCoop.Protocol   wire format: packets, codec, player state
src/KotorCoop.Memory     OpenProcess, AOB scanner, pointer chains, offset profiles
src/KotorCoop.Session    UDP transport, host/client, interpolation, world flags
src/KotorCoop.Agent      CLI: launch, autotrack, track, run, scan, info
src/KotorCoop.MockGame   stand-in game process for testing without KOTOR
src/KotorCoop.Plugin     in-process plugin (NativeAOT win-x86, installs as dinput8.dll)
tests/KotorCoop.Tests    73 tests
profiles/                JSON offset profiles
```

## Requirements

.NET 10 SDK, Windows. The agent must run as the same user that launched the game (or
elevated), or `OpenProcess` returns access denied.

# kotor-coop

Seamless-style two-player co-op for **Star Wars: Knights of the Old Republic** (retail
GOG build), in C#. Two full copies of the game run side by side — locally or across a
network — and an external agent plus a tiny in-process plugin bridge them into one shared
world, the way FromSoftware's Seamless Co-op mod bridges Elden Ring.

No game file is modified. The plugin installs as a `dinput8.dll` proxy the game loads
itself; everything else attaches from outside with `OpenProcess`.

## What works today

Both players, each in their own full game:

- **See each other** as a real engine creature ("phantom") wearing the other player's
  actual appearance, armor and weapons, with their name in the selection band, walking
  and running at true speed on the real walkmesh.
- **Travel together**: when the host crosses into another area, the client follows
  through the engine's own module-transition path (the same machinery the built-in
  `warp` cheat uses). `--no-follow` opts out.
- **Fight the same fight**: enemy health and death are host-authoritative and mirrored
  live; clients stream their local damage back and the host merges it in, so both
  players' hits count. Enemies replicate as absolute health, never damage numbers,
  because each instance applies its own difficulty modifiers.
- **Share the world**: dropped items materialise on the other side as real, clickable
  objects and vanish when picked up; doors open and unlock for both; the story globals
  and the quest journal merge upward, so one quest line advances for everyone.
- **Pause together**: either player's pause pauses both worlds, and only the pauser can
  release it.
- **Go down together**: a player at zero health drops their phantom into a real death
  pose on the other side, and it stands back up on recovery.
- **See each other on the map**: each peer is a live pin on the M-screen map, riding the
  engine's own map-note waypoints — no interface hacking.
- **Share one party**: the host drives the companions (Trask, Carth...) with the game's own
  AI and each client freezes and mirrors them, so they stay one synchronised group instead
  of two that drift. `--no-party-sync` opts out.
- **Connect from the game**: a "Co-op" button on the main menu starts the session from a
  `coop.ini` (host/join, address, password, name). A password keeps strangers out; a
  dropped player reconnects automatically and keeps their slot.

An unfocused or minimised instance keeps simulating (the plugin suppresses the focus
pause), which is what makes one-machine testing practical at all.

### Mods work, even mismatched ones

Two players do not have to run the same mods. At join time each side fingerprints its
install — the executable, the override folder, `appearance.2da`, the journal, the string
table — and the two compare notes. The result is a **per-channel** decision, not
all-or-nothing:

- a different **executable** is refused (every engine address is per-binary);
- a different **`appearance.2da`** just drops the appearance sync (the phantom keeps a
  default look) rather than risk painting the wrong body;
- a different **save** or a mod-**edited module** drops the object-id-keyed channels
  (enemies, doors, items) for that module while phantoms, journal and the rest continue;
- a different **global-variable table** drops shared quest state (its keys would name
  different variables);
- differing **override folders** — the normal case for two modded installs — just
  produce a warning that explains any gear or item that does not appear.

Everything co-op ships as a game file uses a `k_coop_` name, so it can never collide with
a mod, and only ever adds to the override folder.

Verified live: two GOG instances on one machine — phantoms both ways, appearance and
equipment, run-gait, item drop/pickup, enemy health/death mirroring, door unlock
propagation. The compatibility handshake, map presence, name plates and world-digest are
unit-tested (142 tests) with their live two-instance pass pending a stable
unattended-load path. See `Handoff.md` and `docs/research/` for the precise per-feature
status and the reverse-engineering behind each.

## Requirements

- Windows, .NET 10 SDK; MSVC x86 toolset for the plugin (NativeAOT `win-x86`).
- **The DRM-free GOG build of KOTOR 1** (v1.0.3). The Steam copy's DRM wrapper refuses
  a second instance and its packed binary defeats static analysis; the GOG build has
  neither problem. Every engine address in the plugin is verified against this exact
  binary (no ASLR, so addresses are absolute and stable).
- For two instances on one machine: a second copy of the game directory. The retail
  single-instance guard is just a named mutex; `agent launch --instance 2` starts the
  second copy with the mutex renamed in memory.

## Quick start

```powershell
dotnet build KotorCoop.slnx
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
# copy the same dinput8.dll into the second game directory too

pwsh tools\demo.ps1          # launch both instances, load saves, connect the agents
pwsh tools\demo.ps1 -Stop    # shut everything down
```

By hand, the pieces are:

```powershell
$agent = "src\KotorCoop.Agent\bin\Debug\net10.0-windows\KotorCoop.Agent.exe"

& $agent launch --windowed --load-save 1 --game-path 'C:\GOG Games\...\swkotor.exe'
& $agent launch --instance 2 --game-path 'D:\Games\swkotor-gog-p2\swkotor.exe'

# one agent per instance; --pid because two swkotor.exe are running
& $agent run --pid <pid1> --kotor --host --port 27140 --name Revan
& $agent run --pid <pid2> --kotor --join 127.0.0.1:27140 --name Malak
```

No game handy? `KotorCoop.MockGame` is a stand-in process the agent drives through the
identical code path:

```powershell
dotnet run --project src/KotorCoop.MockGame -- --host --name Revan
dotnet run --project src/KotorCoop.MockGame -- --join 127.0.0.1 --name Malak
```

## How it works

```
 swkotor.exe  (x86)                agent (x64, external)              network
+-------------------+   found by   +------------------+
| dinput8.dll proxy |  signature   |  KotorGameBridge |   PlayerState,
|  (C# NativeAOT)   | <----------> |  reads player,   |   items, enemies,   UDP
|  SharedBlock      |    scan      |  writes peers    | <----------------> peers
|  frame hook       |              |  runs the        |   flags, journal
|  engine calls     |              |  CoopSession     |
+-------------------+              +------------------+
```

- **Division of labour**: all discovery is external (the agent scans for the creature
  vtable, reads state, runs the UDP session); all engine calls are in-process (the
  plugin hooks the `SwapBuffers` IAT slot for a main-thread per-frame callback and
  calls the engine's own NWScript machinery: `CreateObject`, `ActionMoveToLocation`,
  `CreateItemOnFloor`, `SetState`, ...). The two halves meet at a versioned shared
  block the plugin allocates and the agent finds by signature.
- **Star relay, not mesh.** Clients talk only to the host; one machine decides what is
  true. Same trade FromSoftware makes.
- **Avatars stream, world truth is reliable.** Positions are unreliable and superseded
  every 50 ms; flags, items, doors and journal entries are retransmitted until acked,
  with version tripwires in every snapshot for divergence repair.
- **Everything monotonic merges upward** (quest states, door states, globals), so
  replayed or reordered packets can never walk the world backwards.
- **Authority is explicit**: your character is yours alone; enemies are the host's
  (clients' copies are pinned at 1 hp and mirror the host's outcomes, with client
  damage merged in host-side); a phantom belongs to its player and cannot be killed
  locally, only knocked about.

`Handoff.md` is the deep documentation: verified engine addresses and structures,
traps (each of which cost real time), per-feature status, and the research trail.
`docs/re/` holds the underlying reverse-engineering notes; `tools/` the helper scripts
and the symbol-database queries everything was derived from.

## Layout

```
src/KotorCoop.Protocol   wire format: packets, codec, player state
src/KotorCoop.Memory     OpenProcess, AOB scanner, engine bridge, offset profiles
src/KotorCoop.Session    UDP transport, host/client, interpolation, stores, agent pump
src/KotorCoop.Agent      CLI: launch, load, run, scan, track, info
src/KotorCoop.Plugin     in-process plugin (NativeAOT win-x86, installs as dinput8.dll)
src/KotorCoop.Memory     OpenProcess, scanner, engine bridge, GameFiles/ (KEY/BIF/GFF/2DA/TLK)
src/KotorCoop.MockGame   stand-in game process for testing without KOTOR
tests/KotorCoop.Tests    unit + loopback integration tests
profiles/                JSON offset profiles; profiles/override ships k_coop_ game files
docs/research/           verified reverse-engineering write-ups behind each feature
tools/                   RE helpers, demo scripts, the kotor1_0_3 symbol database
```

## Honest limitations

- **Dialogue and cutscenes are per-player.** Both players can talk to the same NPC and
  each gets the full conversation; outcomes converge through the globals/journal
  channels, but rewards can duplicate. While a player is mid-conversation the session
  freezes enemy publication for them (the engine disarms hostiles during dialogue).
- **Party members are not reconciled yet** — each instance runs its own copies. The
  design (an ownership split via the engine's solo-mode flag) is in
  `docs/research/party-reconciliation.md`, pending live measurement.
- **Item identity is a blueprint resref**: upgrades and charges don't travel yet.
- **The world-time compatibility check covers the globals catalogue**; save-identity and
  per-module GIT digests (to also gate on a mod-edited module) are a planned follow-up.
- **Unattended startup**: the in-process save load (no menu clicking) reaches the world
  but a login step after it is still being finished (`Handoff.md`); menu-click loading
  works but is a focus-lottery.
- KOTOR 2 is untouched, and only GOG v1.0.3 addresses are known.

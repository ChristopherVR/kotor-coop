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
- **Fight the same fight — and see the same fight**: enemy health and death are
  host-authoritative and mirrored live; clients stream their local damage back and the
  host merges it in, so both players' hits count. And the fight *looks* the same on both
  screens: the host streams every enemy's position, facing, stance and weapon swings,
  and the client drives its copies to match frame by frame instead of letting two AIs
  play two private versions of the same battle. (Verified live: 27/27 creatures at
  identical positions and health across both games, rendered bodies included.)
- **Enemies fight back — at both of you.** Your partner's body is a real combat target in
  your game, not scenery: enemies pick it, swing at it, and every point of damage that
  lands is forwarded and applied to their actual character on their own machine, through
  the engine's own damage pipeline (their resistances, their difficulty, their floating
  numbers). Verified live: a hit landed on one machine took the other player from 12 hit
  points to 5.
- **Share the world**: dropped items materialise on the other side as real, clickable
  objects and vanish when picked up; doors open and unlock for both; the story globals
  and the quest journal merge upward, so one quest line advances for everyone — verified
  live, a quest advanced on one machine lands in the other's real journal, once.
- **Pause together**: either player's pause pauses both worlds, and only the pauser can
  release it.
- **Go down together, and get each other back up**: a player at zero health drops their
  phantom into a real death pose on the other side. KOTOR never truly kills a party member —
  it downs them — so a partner who walks over picks you straight back up, and the game's own
  recovery still applies if nobody is close. Nobody gets dropped to the main menu alone
  either: a party wipe only ends the game once *every* player is down, and then it ends for
  both of you together.
- **See each other on the map**: each peer is a live pin on the M-screen map, riding the
  engine's own map-note waypoints — no interface hacking.
- **Share one party**: the host drives the companions (Trask, Carth...) with the game's own
  AI and each client freezes and mirrors them — through the same frame-by-frame drive as
  enemies, swings included — so they stay one synchronised group instead of two that
  drift. `--no-party-sync` opts out.
- **Start the whole thing from the game's own menu**: a "Co-op" button on the main menu
  opens a real KOTOR panel — the same list widget the Load Game screen uses, with its native
  font, borders and scrollbar. One player clicks **Host**; the other sees them listed with
  their player count and clicks **Connect**. Both games load their own save and come up in
  one shared world. No console, no command line, no alt-tabbing: the agent is started by the
  mod itself when the game launches. A password keeps strangers out; a dropped player
  reconnects automatically and keeps their slot. `coop.ini` still works for a fixed host if
  you prefer.
- **Type commands in-game**: press `~` for the game's own console and type
  `connect <ip> [password]`, `disconnect` or `reconnect` at any time — the session is torn
  down and rebuilt live, no restart. If the host wants a password you didn't give, the
  console prompts you to retype it. Status and co-op replies print on-screen while it's open.
- **Talk to each other**: `say <message>` in the ~ console sends a line to the other players
  (the host relays it), so you can coordinate without seeing each other's screens.
- **One conversation at a time, like Baldur's Gate 3**: while any player is talking, nobody
  else can start a dialogue — enforced inside the engine, so clicking an NPC simply does
  nothing and tells you why. The other player spectates the owner's conversation live, with
  the real lines and reply picks, in the game's own dialogue UI, and a late joiner catches
  up to the right line.
- **Scripted scenes play out the same on both screens**: cutscene actors and story NPCs
  replicate their positions, stances and movements, while staying exempt from the combat
  channel — the module decides who is un-killable, not the session.

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

Verified live on two GOG instances: the whole session started from inside the game (Host on
one, Connect on the other, both loading their real saved characters with no injected
clicks), phantoms both ways with appearance and equipment, run-gait, item drop/pickup,
enemy health/death mirroring, enemy and companion presence (positions, health and rendered
bodies identical across both games), door unlock propagation, map pins, and going down and
being picked up by the other player. The mod-compatibility handshake is unit-tested (266
tests); it has been observed agreeing on two identical installs but its mismatch paths have
not had a live pass. See `Handoff.md` and `docs/research/` for the precise per-feature
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

# copy the agent next to the game so the mod can start it itself
Copy-Item src\KotorCoop.Agent\bin\Debug\net10.0-windows\* 'C:\GOG Games\Star Wars KOTOR (GOG)' -Recurse -Force
```

Then just play: launch KOTOR, click **Co-op** on the main menu, and one of you presses
**Host** while the other picks that server and presses **Connect**. Optionally drop a
`coop.ini` beside the game to set your player name, a password, or a fixed host address.

For development — two instances driven unattended on one machine:

```powershell
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

- **Only the host can switch party character.** The companions are driven by the host's own
  AI and mirrored to everyone else, so a client taking one over would fight its own mirror;
  Tab and the portraits are locked there on purpose. On the host they work normally.
- **The minimap shows only you.** The engine's minimap draws the area image and exactly one
  arrow — there is no map-note or party loop in it to borrow. Your partner is a live pin on
  the full M-screen map instead.
- **One conversation at a time.** While a player is talking, nobody else can start a
  dialogue; the other player spectates rather than getting their own copy. That is the
  design (it keeps one plot line advancing), not a limitation to be lifted. While a player
  is mid-conversation the session freezes enemy publication for them, because the engine
  disarms hostiles during dialogue.
- **A remote player can be worn down but not finished off.** Enemies now genuinely fight
  both players (see above), but KOTOR floors a party member at 1 hit point and downs them
  through a separate path, so forwarded damage stops at 1 rather than dropping them.
  Being downed still works normally from damage in your own game.
- **Enemies choose targets purely by proximity.** There is no threat shaping, so which
  player an enemy picks is the engine's nearest-hostile rule and nothing else.
- **Item identity is a blueprint resref**: upgrades and charges don't travel yet.
- **The world-time compatibility check covers the globals catalogue**; save-identity and
  per-module GIT digests (to also gate on a mod-edited module) are a planned follow-up.
- KOTOR 2 is untouched, and only GOG v1.0.3 addresses are known.

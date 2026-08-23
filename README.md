# kotor-coop

Seamless-style two-player co-op for **Star Wars: Knights of the Old Republic** (retail
GOG build), in C#. Two full copies of the game run side by side — locally or across a
network — and an external agent plus a tiny in-process plugin bridge them into one shared
world, the way FromSoftware's Seamless Co-op mod bridges Elden Ring.

No game file is modified. The plugin installs as a `dinput8.dll` proxy the game loads
itself; everything else attaches from outside with `OpenProcess`.

## Status at a glance

A scannable index over the detailed bullets below. "Live-verified" means someone watched it
work on two real GOG instances; "Implemented" means it is built, unit-tested, and either
off by default or waiting on a live pass; the linked doc always has the dated evidence.

### Synced and working

| Area | Status | Detail |
| --- | --- | --- |
| Phantom presence, appearance, walk/run animation | Live-verified, both directions, 0% idle-while-moving | [phantom-locomotion.md](docs/research/phantom-locomotion.md) |
| Travel — either player leading, announced transitions | Live-verified, ~9x faster follower warp | [transition-smoothness.md](docs/research/transition-smoothness.md) |
| Doors — either player, both directions | Live-verified | [shared-state-audit.md](docs/research/shared-state-audit.md) §2 round 57 |
| Story triggers (OnEnter) — either player, both directions | Live-verified, protocol 36 | [trigger-relay.md](docs/research/trigger-relay.md) |
| Encounters/ambushes — a client walking in fires them for both | Live-verified | see below |
| Combat — enemy health/death mirror, forwarded damage to your phantom | Live-verified | see below |
| Combat — a client's own hit lands on the host's real enemy | Live-verified, 4 ways | round 58 |
| `local_combat` — a mirrored enemy fights a client through that client's own engine, real lethality, real kills, natural aggro | Live-verified, ON by default | rounds 52-55 |
| Non-damage effects (stun/slow/frightened/choke/forcepush/whirlwind) | Live-verified both directions, ON by default | [effect-sync.md](docs/research/effect-sync.md) |
| Container/corpse open-close animation + use-lock | Live-verified, ON by default | [container-sync.md](docs/research/container-sync.md) |
| Dialogue — one conversation at a time, spectate with real lines, late-join adopt | Live-verified | [dialogue-sync.md](docs/research/dialogue-sync.md) |
| Dialogue — a trigger/cutscene-started conversation, both engines racing to `StartDialog` | Live-verified, lock adopts cleanly | dialogue-sync.md |
| Dialogue — auto-join is forced-conversation-only, `off` by default for ordinary chat | Live-verified both sides: ordinary chat never auto-pulls; a genuinely forced conversation does, after fixing a real bug where it could be silently refused | dialogue-sync.md |
| Dialogue — rapid-skip desync, root-caused and fixed | Live-reproduced on demand (an in-process `skipline` verb, no OS input needed); `dialogue_fast_join=1` closes it completely, now the shipped default | dialogue-skip-desync.md |
| Combat presence freeze after a followed conversation ends | Live-measured and cut ~60% (2.7s → ~1.1s) by fixing an oversized debounce | dialogue-sync.md round 78 |
| Container open/close cross-instance latency | Live-measured; the real bug was a dropped release packet falling back to a 5s timeout — fixed, ~5.1s → ~290ms (~18x) | container-sync.md §10 |
| Mid-module movies (a script's own `PlayMovie`) | Live-verified, ON by default | [cinematics.md](docs/research/cinematics.md) §10.9 |
| Item identity (stack/charges/upgrades) crossing the wire | Implemented; apply path not yet live-verified | [item-identity.md](docs/research/item-identity.md) |
| Journal/globals merge-upward, including removal and decrease | Live-verified | [quest-sync.md](docs/research/quest-sync.md) |
| Map pins, minimap arrows, pause-together, downed/revive + spectate camera | Live-verified | — |
| Control lock-in (client stuck as a companion on connect) | Fixed and live-verified, round 70 | [control-return.md](docs/research/control-return.md) |
| Session lifecycle — in-game Co-op menu, console connect/reconnect, mod-compat per-channel degrade | Live-verified for the agreeing case | shared-state-audit.md |
| Companion follow-AI (Trask now walks to catch up) | Round 88 resolved round 84's own "`CreMoveState` reverts mid-leg" mystery into three separate engine mechanisms and fixed two of them live — a companion now consumes an entire leg's recorded trail down to its last 1-3 nodes (~1.3-1.4 units short), up from round 84's own ~90%/2.44-units-short best | [party-reconciliation.md](docs/research/party-reconciliation.md); a fourth, narrower stall at the very last waypoint (a geometric accept/reject test) was attempted, live-disproven, and shipped off by default — not yet closed end-to-end |
| Mod-compatibility mismatch paths | Live-exercised 7 of 8 categories (override, modules, 2da, appearance, save-identity default + strict-refuse, exe) | every observed behavior matched the code exactly — [mod-compatibility.md](docs/research/mod-compatibility.md) round 2026-08-23; module-GIT and globals-catalogue mismatches deliberately not forced, per the doc's own recommendation |
| Dialogue recruitment sync — duplicate-body defect | Root-caused (an off-stage recruitment cache was being mistaken for an addable party member) and fixed; live-confirmed clean across all four verification steps | [dialogue-consequences.md](docs/research/dialogue-consequences.md); `dialogue_recruit_sync` stays off by default (per-slot template mapping still only confirmed for one of nine companions) |
| `loadgame` console verb (deterministic mid-session Load, no mouse clicks) | Built and live-confirmed: a real teardown/reload in 3.1s, correct character/party swap, the classifier fires correctly | [session-save.md](docs/research/session-save.md); unblocks future live testing of the mid-session save/load gates below |
| Mid-session save/load — rejoin gates (`client_load_rejoin`, `host_load_readopt`) | Both bugs from round 86 root-caused, fixed, and now LIVE-CONFIRMED both directions on a two-instance rig — the round-86 "actively refused it" failure is gone entirely | [session-save.md](docs/research/session-save.md) round 87/88; a direct, decisive, zero-regression result, so both gates now ship **ON by default** |
| Party-follow module-warp crash | Root-caused (the one per-frame controller with no transition-epoch guard) and fixed; LIVE-CONFIRMED: no new access violation, diagnostics resume correctly one frame after a warp | [party-reconciliation.md](docs/research/party-reconciliation.md) round 87/88 |
| Companion local combat — two-companion drift risk | CONFIRMED clean live: a released companion fought and landed real credited damage while the OTHER, non-released companion stayed within 0.05 units of the host's position throughout, no drift | [party-reconciliation.md](docs/research/party-reconciliation.md) round 85; `companion_local_combat` stays off pending broader testing, not because of this result |
| Minigames — swoop-race trigger precondition | SOLVED via a new NCS bytecode disassembler: the trigger was never a race-start gate at all — it's an unconditional collectible-flag setter. A `.dlg` sweep across every Taris module then found the real gate is not a single condition script but the payoff of the "Search for Bastila" questline — give Gadon (`GadonThek112`, module `tar_m11aa`) the prototype swoop accelerator, and the conversation's terminal node (`k_ptar_bekrace`) disassembles to a module-transition call naming `tar_m03af`, a dedicated swoop-track module | [minigames.md](docs/research/minigames.md) §13–§14; `Tar_SwoopRace`/`k_ptar_swoop` turned out to be a post-race "you already won" flag, not an entry gate — four rounds of flag-guessing plus one dialogue-hypothesis were chasing the wrong mechanism the whole time |

### Still needs work

| Area | Status | Blocker |
| --- | --- | --- |
| `party_fast_pump` (companion-position latency) | Tested twice (combat: worse; non-combat: no measurable difference) | OFF by default, real cause of reported lag still open — [lag-audit.md](docs/research/lag-audit.md) |
| Minigames — swoop racing, now that the real gate is known | Live-tested: `Tar_SwoopEng=1` alone was enough to open Gadon's dialogue into the exact six-node chain the disassembly predicted — the flag theory is confirmed correct. But the terminal node's own module-transition action never fired within several minutes of real time; a clean stall, not a crash, position never moving | [minigames.md](docs/research/minigames.md) §14; three unresolved hypotheses recorded for the next pass |
| Creature liveness reconciliation (a body alive on one screen, dead on the other) | CONFIRMED working live for the first time — a debug `--enemy-census-cap` flag makes the out-of-census case reachable, and the Kill→Destroy correction fires correctly | OFF by default; a separate, still-unfixed `DestroyObject`-scan-freshness issue is untouched by this result — [creature-liveness.md](docs/research/creature-liveness.md) |
| KOTOR 2 | Untouched | only GOG v1.0.3 addresses are known |

## What works today

Both players, each in their own full game:

- **See each other** as a real engine creature ("phantom") — the other player's actual
  appearance, armor and weapons, with a working walk/run cycle (not a slide) on the real
  walkmesh, both directions.
- **Travel together, either player leading.** Whoever crosses into another area first, the
  other follows immediately through the engine's own transition path — both loading screens
  overlap instead of running back to back. `follow_lead=host` restores host-only leading;
  `--no-follow` opts out.
- **Fight the same fight.** Enemy health and death are host-authoritative and mirrored live;
  the host streams every enemy's position, facing and swings, so the fight looks the same on
  both screens instead of two AIs running two private battles.
- **Enemies fight back at both of you.** A hit on your partner's phantom lands for real on
  their own machine, through the engine's own damage pipeline. A mirrored enemy can also
  fight a client directly through that client's own engine — real kills, real lethality,
  natural aggro, no invulnerability with several enemies engaged at once.
- **Share the world.** Dropped items materialise as real, clickable objects on both sides;
  doors open, unlock, and lock for use both ways; a container or corpse one player has open
  locks for everyone else and shows the same open/close animation; story globals and the
  quest journal merge upward (including removals and decreases) so one quest line advances
  for everyone.
- **Pause together**: either player's pause pauses both worlds; only the pauser releases it.
- **Go down together, get each other back up.** A downed player's phantom drops into a real
  death pose on the other side; a partner who walks over revives them. A party wipe only ends
  the game once *every* player is down. A downed player's camera follows a partner and Tab
  cycles through everyone worth watching. `spectate_on_down=0` opts out.
- **See each other on the map**: a coloured arrow per player on the minimap and a live pin on
  the M-screen map.
- **Share one party.** The host drives the companions with the game's own AI; each client
  mirrors them frame by frame, swings included, and now reliably walks to catch up after
  being idle. `--no-party-sync` opts out.
- **Start from the game's own menu.** A "Co-op" button opens a real KOTOR panel; one player
  hosts, the other connects, both load their own save into one shared world. No console, no
  alt-tabbing. A password keeps strangers out; a dropped player reconnects automatically.
- **Type commands in-game.** Press `~` for the console: `connect`, `disconnect`, `reconnect`
  any time, no restart. Tab-completes (including the game's own console verbs), Up/Down
  recalls history, a hint line shows usage.
- **Stage a test without grinding to it.** A handful of verbs behave differently depending on
  who typed them — `god`, `topup`, `standup`, `slay`, `tp`, `dropitem`, `applyeffect`, and
  more. `docs/research/console-cheats.md` has the full reference.
- **Talk to each other**: `say <message>` in the console sends a line to every player.
- **One conversation at a time, like Baldur's Gate 3.** While a player is talking, nobody
  else can start a dialogue — the other player spectates live, in the game's own UI, with the
  real lines and picks, and a late joiner catches up instantly. Auto-join only pulls the other
  player in for a conversation the *engine itself* forces (an ambush, a scripted cutscene) —
  ordinary chat stays optional (`dialogue_auto_join=off` by default; `near`/`always` opt in).
- **Scripted scenes play out the same on both screens**: cutscene actors and story NPCs
  replicate position and movement, exempt from the combat channel.

An unfocused or minimised instance keeps simulating, which is what makes one-machine
testing practical at all. See the status table above and `docs/research/` for the
live-verification evidence and exact numbers behind each of these.

### Mods work, even mismatched ones

Two players do not have to run the same mods. At join time each side fingerprints its
install — the executable, the override folder, `appearance.2da`, the journal, the string
table — and the two compare notes. The result is a **per-channel** decision, not
all-or-nothing:

- a different **executable** is refused (every engine address is per-binary);
- a different **`appearance.2da`** just drops the appearance sync (the phantom keeps a
  default look) rather than risk painting the wrong body;
- a mod-**edited module** drops the object-id-keyed channels (enemies, doors, items, party)
  *for that module only* — they come back by themselves in the next module the two installs
  agree on — while phantoms, journal and the rest continue throughout;
- a different **save** drops the same channels and tells you to rejoin, because joining
  *takes the host's world* (your character, their world) and that makes the two saves
  identical by construction; `--require-identical-save` refuses instead;
- a different **global-variable table** drops shared quest state (its keys would name
  different variables);
- differing **override folders** — the normal case for two modded installs — just
  produce a warning that explains any gear or item that does not appear.

Every one of those decisions carries a code (`E_EXE`, `W_GIT`, `W_SAVE`, ...) naming the
channels it switched off, and is **said on screen** as it happens — a channel that goes quiet
for safety is otherwise indistinguishable from one that broke.

Everything co-op ships as a game file uses a `k_coop_` name, so it can never collide with
a mod, and only ever adds to the override folder. Verified live on two identical GOG
installs, started entirely from the game's own menu; the mismatch paths themselves are
unit-tested but have not had a live pass — see "Honest limitations" below.

## Requirements

- Windows. The packaged zip needs nothing else; building from source needs the .NET 10 SDK
  and the MSVC x86 toolset for the plugin (NativeAOT `win-x86`).
- **The DRM-free GOG build of KOTOR 1** (v1.0.3). The Steam copy's DRM wrapper refuses
  a second instance and its packed binary defeats static analysis; the GOG build has
  neither problem. Every engine address in the plugin is verified against this exact
  binary (no ASLR, so addresses are absolute and stable).
- For two instances on one machine: a second copy of the game directory. The retail
  single-instance guard is just a named mutex; `agent launch --instance 2` starts the
  second copy with the mutex renamed in memory.

## Quick start

Grab `kotor-coop-<version>.zip` (built with `pwsh tools\package.ps1`, or from a release),
extract it anywhere, and install it into your game directory:

```powershell
pwsh install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
```

That's the plugin, the agent and the co-op override files all in place in one step —
`install.ps1 -Uninstall` takes them back out again. No .NET SDK or MSVC toolset needed;
those are only for building from source (below).

Then just play: launch KOTOR, click **Co-op** on the main menu, and one of you presses
**Host** while the other picks that server and presses **Connect**. Optionally edit the
`coop.ini` the package dropped beside the game to set your player name, a password, or a
fixed host address.

### Building from source

```powershell
dotnet build KotorCoop.slnx
pwsh src\KotorCoop.Plugin\build.ps1 -Install -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'

# copy the agent next to the game so the mod can start it itself
Copy-Item src\KotorCoop.Agent\bin\Debug\net10.0-windows\* 'C:\GOG Games\Star Wars KOTOR (GOG)' -Recurse -Force
```

Or build the same zip `install.ps1` above unpacks: `pwsh tools\package.ps1` writes it to
`dist\kotor-coop-<version>.zip`.

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

`docs/research/` is the deep documentation: verified engine addresses and structures,
traps (each of which cost real time), per-feature status, and the research trail.
Start with `docs/research/shared-state-audit.md` for what actually replicates today and
what only looks like it does. `tools/` holds the helper scripts and the symbol-database
queries everything was derived from.

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
  Tab and the portraits are locked there on purpose. Disconnecting hands the whole party
  straight back. If you were controlling a companion when you connected, control and the
  camera come back to your own character automatically, live-verified. `control_return=0`
  opts out. See [control-return.md](docs/research/control-return.md).
- **A companion now reliably walks to catch up for nearly an entire leg**, fixed round 80, root
  cause corrected round 84, deepened round 88. What looked like one open mystery (`CreMoveState`
  reverting mid-leg) turned out to be three separate, disassembly-grounded engine mechanisms —
  two are now fixed and live-verified (a companion consumes essentially the whole recorded trail,
  stopping only 1-3 nodes short, up from round 84's own ~90%); a third, narrower stall at the
  very last waypoint was attempted, live-tested, found to make things WORSE, and shipped off by
  default rather than forced through. A related crash — warping modules while the follow
  diagnostics run (shipped default) could access-violate the host — is root-caused (the
  controller was the one per-frame system in the codebase with no transition-epoch guard),
  fixed, and now LIVE-CONFIRMED: no new access violation, diagnostics resume with sane values
  one frame after the warp. See
  [party-reconciliation.md](docs/research/party-reconciliation.md).
- **A mirrored companion cannot swing on a client yet**, except for one narrow, off-by-default
  case (`companion_local_combat=1`) that releases a companion standing close enough to both
  players and the enemy to its own engine AI. The release/reclaim mechanism is live-verified
  clean; round 82 confirmed client-side "solo mode" as the blocker (it stops a released
  companion's AI from ever queuing an attack); round 83 shipped a fix candidate that sidesteps
  solo mode entirely rather than trying to scope it — it drives the released companion's attack
  directly through the same call the `attack` console cheat already proves lands real damage.
  Round 85 ran the fix's own drift-risk check for the first time with two companions live: the
  released companion landed real credited damage while the OTHER, non-released companion held
  within 0.05 units of the host's position the whole time — no drift. `companion_local_combat`
  stays off pending broader testing, not because of a problem found here. See
  party-reconciliation.md.
- **Companion-mirror lag** ("lag even on localhost"): investigated twice (`party_fast_pump`),
  found no benefit either time, left off. The follow-AI bug that blocked a clean measurement
  is now fixed, so this is open to re-measure. See [lag-audit.md](docs/research/lag-audit.md).
- **One conversation at a time.** By design, not a bug — it keeps one plot line advancing.
  The other player spectates rather than getting their own copy.
- **The Endar Spire's command deck floors damage at 1 hp for everyone** — that's KOTOR's own
  tutorial script (`SetMinOneHP`), not this mod. See
  [min-one-hitpoint.md](docs/research/min-one-hitpoint.md).
- **Enemies target purely by proximity.** No threat shaping.
- **The mod-compatibility check is now live-verified against real mismatches**, not just the
  agreeing case. A round of live fixtures exercised different executable, override folder,
  modules census, 2DA, appearance.2da, save identity (both the default degrade and the strict
  `--require-identical-save` refuse-and-evict path) — every one matched the code's own
  degradation matrix exactly. Module-GIT and globals-catalogue mismatches remain deliberately
  unforced (too risky to fabricate safely); two installs whose *same* resref names a
  differently-modded item still aren't detected. See
  [mod-compatibility.md](docs/research/mod-compatibility.md).
- **Minigames**: the turret sequence is CONFIRMED a genuinely separate module — spectate/park
  is structurally forced there (not a design choice) and the current parked-peer behavior is
  verified clean. The swoop-race `CSWMiniGame` subsystem is confirmed architecturally *capable*
  of running in-place with no module boundary (forced-pointer live test). Four rounds of
  guessing which global flag gates the race all failed for the same reason: they were reading
  the wrong script/node entirely. A new NCS bytecode disassembler (built this session, no such
  tool existed before) found the entrance trigger is not a race gate at all — just an
  unconditional collectible flag setter — and a full `.dlg` sweep across every Taris module
  found the real gate is a story-quest conversation with Gadon (`tar_m11aa`) whose terminal
  node disassembles to a call naming a separate module, `tar_m03af`, as its argument. Live-tested:
  the flag theory checked out exactly (it opens the predicted six-node dialogue chain), but the
  terminal node's own transition action never actually fired in several minutes of observation —
  a clean stall, not a crash, still in honest tension with the in-place-minigame finding above
  (minigames.md §14.6, three unresolved hypotheses recorded). Pazaak is unrelated and uncovered.
  See [minigames.md](docs/research/minigames.md).
- **Non-damage combat effects** (stun, slow, frightened, choke, force push/whirlwind) forward
  live, both directions, on by default. Knockdown, Paralyze and Sleep are captured but never
  replayed — their apply-side engine call is still unverified. See
  [effect-sync.md](docs/research/effect-sync.md).
- **Mid-session save/load**: a joiner's progress now survives to the next session (their
  character is captured and preferred on rejoin). The regression check that matters most — does
  an ordinary co-op travel warp ever get misclassified as a Load Game action — is CONFIRMED
  clean live, both directions, and a new `loadgame <slot>` console verb calls the real engine
  Load path directly and is live-confirmed to work (a genuine teardown/reload in ~3s, correct
  character/party swap) — the old mouse-click blocker is moot now. Tested against a genuinely
  different save for the first time, both rejoin gates turned up real, distinct bugs — not what
  a "timing race" first looked like. `host_load_readopt`'s classifier/trigger logic was correct
  all along; the actual problem was that a real `agent run --host` session never served its world
  for a client to fetch in the first place (only the standalone `agent serve-world` verb did).
  `client_load_rejoin`'s classifier silently discarded its digest-change signal because the
  `loadgame` verb produces it in the opposite order the correlation window assumed. Both
  root-caused, fixed, and now LIVE-CONFIRMED both directions on a two-instance rig — correct
  rejoin sequences, clean module/character convergence, and a clean regression check with both
  gates off. A direct, decisive, zero-regression result, so both gates now ship **ON by
  default**. See [session-save.md](docs/research/session-save.md).
- **Dialogue reward-node consequences** (item grants) are built and forward automatically,
  on by default. Recruitment sync defaults off — an earlier raw availability poke was
  live-confirmed insufficient (the engine had nothing to spawn a body from) and has been
  replaced with a call through the real recruitment routine, now CONFIRMED live end-to-end for
  Carth (appears in both parties' rosters correctly, exactly one body, correct party table).
  A duplicate-body defect found in that pass — the primary spawn path could fall through to an
  older mechanism — is root-caused (the "duplicate" was actually the engine's own off-stage
  recruitment cache being mistaken for an addable party member) and fixed, live-confirmed
  clean. The per-slot companion mapping is confirmed for only one of nine companions so far.
  Alignment is left to diverge on purpose (two people making different choices in one story).
  See [dialogue-consequences.md](docs/research/dialogue-consequences.md).
- **A body left alive on one screen and dead on the other** can be corrected by a liveness
  channel that's now CONFIRMED working live for the first time: a debug-only
  `--enemy-census-cap` agent flag shrinks the 32-creature census on demand, and the
  Kill→Destroy correction fired cleanly in a real out-of-census kill. Off by default; a
  separate, still-unfixed `DestroyObject`-scan-freshness issue from an earlier round is
  untouched by this result. See [creature-liveness.md](docs/research/creature-liveness.md).
- **Alignment (light/dark) is per-player on purpose** — no sync channel needed. An
  alignment-gated dialogue option reflects whichever player owns the conversation.
- KOTOR 2 is untouched, and only GOG v1.0.3 addresses are known.

The detailed, dated evidence behind every fix and every open item above — exact numbers,
disassembly, live-test logs — lives in `docs/research/`, linked from each bullet and indexed
in `docs/research/shared-state-audit.md`.

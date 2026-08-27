# kotor-coop

[![CI](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml/badge.svg)](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml)

Seamless-style two-player co-op for **Star Wars: Knights of the Old Republic** (retail GOG
build), in C#. Two full copies of the game run side by side — locally or across a network —
and an external agent plus a tiny in-process plugin bridge them into one shared world, the way
FromSoftware's Seamless Co-op mod bridges Elden Ring.

No game file is modified. The plugin installs as a `dinput8.dll` proxy the game loads itself
(or behind a tiny chain loader when another mod already owns that name); everything else
attaches from outside with `OpenProcess`.

## Status at a glance

| Area | Status | Doc |
| --- | --- | --- |
| Presence, appearance, animation, minimap | Live-verified | [phantom-locomotion.md](docs/research/phantom-locomotion.md) |
| Travel — transitions, doors, triggers | Live-verified | [transition-smoothness.md](docs/research/transition-smoothness.md) |
| Combat — mirrored enemies, damage, effects | Live-verified (a reported "client can't hit an Endar Spire Sith" was root-caused 2026-08-28 to that creature starting in a FRIENDLY faction — not a co-op defect) | [combat-participation.md](docs/research/combat-participation.md) |
| World state — containers, items, journal | Live-verified (container-lock arbitration confirmed correct AND fast-path-fixed at a 1000ms budget; `ItemTemplateSpawnHook` confirmed live end to end, 2026-08-28) | [shared-state-audit.md](docs/research/shared-state-audit.md) |
| Dialogue — spectate, late-join, skip-fix | Live-verified | [dialogue-sync.md](docs/research/dialogue-sync.md) |
| Dialogue recruitment sync | Live-verified for two companions; one fix unconfirmed | [dialogue-consequences.md](docs/research/dialogue-consequences.md) |
| Session lifecycle — menu, connect, save/load rejoin | Live-verified; the `host_load_readopt` regression and the STUNT_00 escape-pod crash are both CLOSED — same root cause, a dangling-pointer write storm in `PartySelectSuppressionController`, fixed 2026-08-27 and reconfirmed clean across three independent live re-verifications (last 2026-08-28) | [session-save.md](docs/research/session-save.md), [galaxy-map-and-party-select.md](docs/research/galaxy-map-and-party-select.md) |
| Three-peer sessions (3+ players) | Live-verified — `demo3.ps1`/`verify3.ps1`, 34/34 checks passed, chat/phantoms/hit-tables all agreeing across three instances | [live-verification.md](docs/live-verification.md) |
| Companion follow-AI | Live-verified, one known stall left (A/B on two fix candidates reconfirmed inconclusive a fourth time, 2026-08-28; stays off) | [party-reconciliation.md](docs/research/party-reconciliation.md) |
| Companion local combat | Narrow case live-verified | [party-reconciliation.md](docs/research/party-reconciliation.md) |
| Mod compatibility | Live-verified for most mismatch categories | [mod-compatibility.md](docs/research/mod-compatibility.md) |
| Sharing `dinput8.dll` with another mod | Live-verified (chain loader) | [src/KotorCoop.Loader](src/KotorCoop.Loader/dinput8_loader.c) |
| Minigames — swoop race | Blocked, needs a debugger | [minigames.md](docs/research/minigames.md) |
| Minigames — Pazaak detection, "waiting" notice | Live-verified 2026-08-28 (a range-guard bug that silenced detection was found and fixed); `pazaak_detect` flipped ON by default | [minigames.md](docs/research/minigames.md) |
| Creature liveness reconciliation | Off by default, open | [creature-liveness.md](docs/research/creature-liveness.md) |
| Peer-away detection (`FlagAway`) when a partner quits to the main menu | Live-verified 2026-08-27 (an initial pass failed; the fix landed and re-verified the same day) | [disconnect-reconnect.md](docs/research/disconnect-reconnect.md) |
| Persistent connection indicator | Live-verified 2026-08-27 (partial — data path confirmed, on-screen render unconfirmed) | [connect-flow.md](docs/research/connect-flow.md) |
| Protocol-version-mismatch reject (`E_PROTO`) | Built, not live-verified | [connect-flow.md](docs/research/connect-flow.md) |
| Script-granted items outside dialogue (`script_item_sync`) | Built, live-verified, ON by default | [dialogue-consequences.md](docs/research/dialogue-consequences.md) |
| Companion experience (`companion_xp_sync`) | Live-verified, ON by default | [companion-state-sync.md](docs/research/companion-state-sync.md) |
| Companion equipment (`companion_gear_sync`) | Body-slot re-equip live-confirmed 2026-08-28 (925 ms/235 ms propagation); weapon-slot re-equip blocked by a newly-found engine-call bug the same round — stays off | [companion-state-sync.md](docs/research/companion-state-sync.md) |
| Shared party item pool (`party_pool_sync`) | Live-verified, one bug fixed; a second (the depositing side — reproduced 2026-08-28 on the host's own deposit, not on a client's) is still open — stays off | [party-pool-sync.md](docs/research/party-pool-sync.md) |
| Merchant store stock (`store_sync`) | Stays off — the 2026-08-28 `PopulateStoreItemListBox` crash is root-caused and fixed (`ShowStoreGui` was missing two arguments) — NEEDS-LIVE | [store-sync.md](docs/research/store-sync.md) |
| Fog-of-war reveal (`map_reveal_sync`) | Live-verified, on by default | [map-reveal.md](docs/research/map-reveal.md) |
| Star Forge finale — Bastila/Malak duel spectate (`solo_segment_spectate`) | Built, off — a 2026-08-28 live pass opened the real door but never reached the detection precondition (the roster wipe fires from the Bastila dialogue, not the door), so the spectate machinery is still unexercised | [star-forge-finale.md](docs/research/star-forge-finale.md) |
| Internet play — public address discovery (STUN), UPnP port mapping | Built, unit-tested, not live-verified against a real router; no relay/hole-punching | [network-topology.md](docs/research/network-topology.md) |

"Live-verified" means watched working on two real GOG instances; "Off by default" means built
and unit-tested but not shipped enabled. See "Honest limitations" below and `docs/research/`
for the dated evidence behind every row.

## What works today

Both players, each in their own full game:

- **See each other** as a real engine creature ("phantom") with real appearance, armor and
  weapons, walking/running on the real walkmesh, and on the minimap/M-screen map.
- **Travel together, either player leading**, through the engine's own transition path;
  loading screens overlap. `follow_lead=host` restores host-only leading, `--no-follow` opts out.
- **Fight the same fight**: enemy health/death are host-authoritative and mirrored live; a hit
  on your partner's phantom lands for real, and non-damage effects (stun, slow, choke, ...) follow.
- **Share the world.** Dropped items, doors, containers/locks, story triggers, and quest/journal
  state all merge upward. **Pause** and **downed/revive** (spectate camera,
  `spectate_on_down=0` to opt out) work the same way, together.
- **Share one party**, host-driven AI mirrored to clients (`--no-party-sync` opts out), and
  **start from the game's own menu** — a "Co-op" button, password-protected, auto-reconnect.
- **Type commands in-game** (`~` — see [docs/CONSOLE.md](docs/CONSOLE.md)) and **hold one
  conversation at a time**, Baldur's Gate 3-style: the other player spectates live with a late
  joiner catching up instantly; auto-join only pulls someone in when the *engine* forces it.

An unfocused or minimised instance keeps simulating, which is what makes one-machine testing
practical. **Mods don't have to match**: at join time each side fingerprints its install
(executable, override folder, `appearance.2da`, journal, string table) and the two compare
notes **per channel** — a mismatched module only drops that module's channels, a different
save asks you to rejoin (or refuses, with `--require-identical-save`), and every decision is
said on screen with a code (`E_EXE`, `W_GIT`, `W_SAVE`, ...). See
[mod-compatibility.md](docs/research/mod-compatibility.md).

## Requirements

- Windows. The packaged zip needs only the .NET 10 runtime; building from source needs the
  .NET 10 SDK and the Visual Studio 2022 MSVC x86 C++ toolset (NativeAOT `win-x86` plugin, C
  chain loader).
- **The DRM-free GOG build of KOTOR 1** (v1.0.3), not Steam — Steam's DRM refuses a second
  instance and its packed binary defeats static analysis (no ASLR). KOTOR 2 is untouched.
- For two instances on one machine: a second copy of the game directory — the single-instance
  guard is just a named mutex, renamed in memory by `agent launch --instance 2`.

## Quick start (players)

Download `kotor-coop-<version>.zip` from the
[releases page](https://github.com/ChristopherVR/kotor-coop/releases) — a tagged release, or
`latest`, rebuilt from every push to `main` that passes build and test. The zip is a
game-folder image: **extract it straight into the folder that contains `swkotor.exe`**, or
extract it anywhere and **double-click `KotorCoop.Agent.exe`** — it finds the GOG install (or
asks), copies everything in, and offers to launch.

No scripts, nothing to configure. Launch KOTOR, click **Co-op** on the main menu, one of you
presses **Host** while the other picks that server and presses **Connect** — the game starts
the agent itself in the background. Everything optional lives in `coop.ini`
([docs/CONFIG.md](docs/CONFIG.md)); the zip's own `README.md` covers play, configuration and
troubleshooting without needing this repo. CLI equivalent:
`KotorCoop.Agent.exe install|uninstall --game-path <dir>` or `install.ps1`.

### Sharing `dinput8.dll` with another mod

Only one file can be called `dinput8.dll`. If another mod already owns it (ReShade, an ASI
loader, a widescreen fix), the setup notices and offers to keep it (the command line refuses
unless told `--chain` or `--force`):

```powershell
KotorCoop.Agent.exe install --game-path '...' --chain                        # keep the other mod
KotorCoop.Agent.exe install --game-path '...' --chain --load ReShade32.dll   # and load extra DLLs
```

Chain mode installs the [`KotorCoop.Loader`](src/KotorCoop.Loader/dinput8_loader.c) wrapper —
6 KB of plain C, no runtime — as `dinput8.dll`, our plugin as `KotorCoop.Plugin.dll`, renames
the other mod to `dinput8.prev.dll`, and writes a `dinput8.chain` list. Each entry forwards to
the next, the first proxy being ours; `coop.ini`'s `dinput8_next` overrides the plugin's next
hop explicitly. `uninstall` restores the other mod under its original name.

## Building from source

```powershell
dotnet build KotorCoop.slnx                     # agent, session, protocol, memory, tests
dotnet test  tests\KotorCoop.Tests
pwsh src\KotorCoop.Plugin\build.ps1 -Install    # plugin (NativeAOT x86) + agent + overrides -> the GOG default dir
pwsh src\KotorCoop.Loader\build.ps1             # the chain loader (only needed for chain mode)
pwsh tools\package.ps1                          # everything, zipped: dist\kotor-coop-<version>.zip
```

`build.ps1 -Install` deploys into `C:\GOG Games\Star Wars KOTOR (GOG)` by default (`-GameDir`
for another copy); both native builds import the MSVC x86 environment themselves via
`tools\devenv.ps1` (vswhere-located, so any VS edition works). `-WarnAsError` is what CI runs.

For development — two instances driven unattended on one machine:

```powershell
pwsh tools\demo.ps1          # launch both instances, load saves, connect the agents
pwsh tools\verify.ps1        # PASS/FAIL per feature against the live pair
pwsh tools\demo.ps1 -Stop    # shut everything down
```

`demo.ps1` does **not** deploy the plugin — run `build.ps1 -Install` first for every game copy
the rig uses. Driving the agent by hand, `KotorCoop.MockGame` (a stand-in process for testing
without KOTOR), and the CI/release pipeline: see [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md).

## Console

Press `~` in-game for the console. `connect`/`disconnect`/`reconnect` manage the session
without a restart; Tab-completes (including the game's own verbs), Up/Down recalls history. A
handful of verbs (`god`, `topup`, `tp`, `dropitem`, `applyeffect`, and more) stage a test
without grinding to it. Full reference: [docs/CONSOLE.md](docs/CONSOLE.md).

## Configuration

Everything lives in one `coop.ini` beside the game, parsed by two independent halves — the
external agent and the in-process plugin each read their own keys from it. Full table of
every key, its shipped default and what it does: [docs/CONFIG.md](docs/CONFIG.md).

## How it works

```
swkotor.exe (x86): dinput8.dll proxy, SharedBlock, frame hook, engine calls
       <--- signature scan / shared block --->
agent (x64, external): KotorGameBridge reads player, writes peers, runs CoopSession
       <--- UDP: PlayerState, items, enemies, flags, journal --->
```

Discovery is external, engine calls are in-process; clients talk only to the host (star relay,
not mesh); authority is explicit (your character is yours, enemies are the host's); positions
stream and are superseded regularly, while flags/items/doors/journal retransmit until acked and
merge monotonically upward. Full design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Layout

```
src/KotorCoop.Protocol   wire format: packets, codec, player state
src/KotorCoop.Memory     OpenProcess, AOB scanner, engine bridge, GameFiles/ (KEY/BIF/GFF/2DA/TLK)
src/KotorCoop.Session    UDP transport, host/client, interpolation, stores, agent pump
src/KotorCoop.Agent      CLI: launch, load, run, scan, track, info
src/KotorCoop.Plugin     in-process plugin (NativeAOT win-x86, installs as dinput8.dll)
src/KotorCoop.Loader     generic dinput8 chain loader (C) for coexisting with other dinput8 mods
src/KotorCoop.MockGame   stand-in game process for testing without KOTOR
tests/KotorCoop.Tests    unit + loopback integration tests
profiles/                JSON offset profiles, coop.ini.example; profiles/override ships k_coop_ game files
docs/research/           verified reverse-engineering write-ups behind each feature
tools/                   package/install scripts, demo rig, RE helpers, the kotor1_0_3 symbol database
.github/workflows/       ci.yml (build, test, package on every push), release.yml (after green CI: rolling "latest" on main, full release on a v* tag)
```

## Honest limitations

- **Only the host can switch party character.** Companions are driven by the host's own AI and
  mirrored to everyone else; control returns to your own character automatically on connect
  (`control_return=0` opts out). See [control-return.md](docs/research/control-return.md).
- **Companion follow-AI still stalls at the last waypoint of a leg** — two fix candidates make
  progress but neither closes it (A/B reconfirmed inconclusive a fourth time, 2026-08-28); both
  stay off by default. See [party-reconciliation.md](docs/research/party-reconciliation.md).
- **A mirrored companion can't fight for a client** except through `companion_local_combat`
  (on by default), which releases a companion standing close enough to both players and the
  enemy to its own engine AI. See [party-reconciliation.md](docs/research/party-reconciliation.md).
- **One conversation at a time**, by design — the other player spectates rather than getting
  a separate copy.
- **Companion experience sync is live-verified and on by default** (`companion_xp_sync`).
  **Equipment sync (`companion_gear_sync`) stays off** — the body slot is live-confirmed working
  (see the status table above), but a weapon re-equip still doesn't reach even the host's own
  engine state past the first attempt. **Dialogue recruitment sync is live-confirmed for two companions**
  (`dialogue_recruit_sync`, on by default); a fix for a stale pre-recruit body is implemented but
  not yet confirmed live. See [companion-state-sync.md](docs/research/companion-state-sync.md)
  and [dialogue-consequences.md](docs/research/dialogue-consequences.md).
- **Mod-compatibility mismatch handling is live-verified for most categories**, but
  module-GIT/globals-catalogue mismatches are unforced in testing and a same-resref,
  differently-modded item isn't detected. See [mod-compatibility.md](docs/research/mod-compatibility.md).
- **Swoop racing is blocked.** The entry gate is found, but the terminal transition call never
  fires; diagnosing further needs a debugger this project doesn't have. See
  [minigames.md](docs/research/minigames.md).
- **Creature liveness reconciliation** (a body alive on one screen, dead on the other) ships
  off by default; the correction mechanism works, a freshness theory is unconfirmed. See
  [creature-liveness.md](docs/research/creature-liveness.md).
- **Chain mode serialises `dinput8` proxies, it doesn't merge them** — a proxy that always
  loads the system DLL itself must be last in `dinput8.chain`; "load me" DLLs (`.asi`) go anywhere.
- **Each player really fights one enemy at a time.** `local_combat` hands a client exactly one
  hostile to its own engine at a time; every other engaged enemy stays mirrored (damage still
  lands), and the damage wire names only the last attacker. See [combat-participation.md](docs/research/combat-participation.md).
- **The party's shared item pool syncs, but a live-verified double-apply bug keeps it off by
  default** (`party_pool_sync`, one fixed, one open on the depositing side — status table above).
- **The Star Forge's Bastila and Malak duels spectate, but off by default and unproven live**
  (`solo_segment_spectate`): parks the other player's input/camera on the fight instead of a
  second combatant wandering in. The detection precondition itself has not yet been reached
  live (see the status table above), so the input lock hasn't been watched stop a real press.
- **The shared merchant store channel (`store_sync`) is built but stays off** — a real engine
  crash was found, root-caused and fixed (see the status table above); the fix is NEEDS-LIVE.
- **`coop.ini` divergence is negotiated, not silent.** Every behaviour flag rides a digest
  compared at join; a difference always warns on both screens (`W_CFG`), and nine
  host-authoritative keys are additionally adopted by a disagreeing client at runtime. See
  [mod-compatibility.md](docs/research/mod-compatibility.md)'s "Behaviour-flag negotiation".
- **The Endar Spire's command deck floors damage at 1 hp for everyone** — KOTOR's own tutorial
  script, not this mod ([min-one-hitpoint.md](docs/research/min-one-hitpoint.md)).
- **No gamepad/controller support.** The server browser, password entry and `~` console take
  typed/clicked input only — a co-op-added limitation, not a change to the base game's input.
- **Internet play works without a relay for most home routers, not all.** A host's public
  address is found automatically (STUN) and the port forwarded automatically via UPnP; UPnP-off
  needs a manual forward, named in the `status`/`public_address` output. No relay/hole-punching,
  so a restrictive ("symmetric") NAT with UPnP also off can't be reached. Built and unit-tested
  against fake responders, not yet watched against a real router. See
  [network-topology.md](docs/research/network-topology.md).

## Documentation map

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the design and why it looks like this.
- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) — build, test, the live rig, CI/release
  pipeline, repo layout, code conventions, and how to add a feature end-to-end.
- [docs/CONFIG.md](docs/CONFIG.md) — every `coop.ini` key and CLI flag, with defaults.
- [docs/CONSOLE.md](docs/CONSOLE.md) — every in-game console verb.
- [docs/roadmap-seamless.md](docs/roadmap-seamless.md) — what's left before "seamless,"
  tiered by priority.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — common problems and their causes.
- [docs/live-verification.md](docs/live-verification.md) — the two-instance checklist that
  turns "built" into "live-verified" here.
- [docs/FINDING-OFFSETS.md](docs/FINDING-OFFSETS.md) and [docs/re/](docs/re/) — how engine
  addresses were found, and the core RE independent of any one feature.
- [docs/research/README.md](docs/research/README.md) — the full per-feature research index.
- [tools/README.md](tools/README.md) — what every helper script in `tools/` does.
- [tools/package-README.md](tools/package-README.md) — the README that ships inside the zip.

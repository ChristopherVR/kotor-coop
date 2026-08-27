# kotor-coop

[![CI](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml/badge.svg)](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml)

Seamless-style two-player co-op for **Star Wars: Knights of the Old Republic** (retail
GOG build), in C#. Two full copies of the game run side by side — locally or across a
network — and an external agent plus a tiny in-process plugin bridge them into one shared
world, the way FromSoftware's Seamless Co-op mod bridges Elden Ring.

No game file is modified. The plugin installs as a `dinput8.dll` proxy the game loads itself
(or behind a tiny chain loader when another mod already owns that name); everything else
attaches from outside with `OpenProcess`.

## Status at a glance

| Area | Status | Doc |
| --- | --- | --- |
| Presence, appearance, animation, minimap | Live-verified | [phantom-locomotion.md](docs/research/phantom-locomotion.md) |
| Travel — transitions, doors, triggers | Live-verified | [transition-smoothness.md](docs/research/transition-smoothness.md) |
| Combat — mirrored enemies, damage, effects | Live-verified | [combat-participation.md](docs/research/combat-participation.md) |
| World state — containers, items, journal | Live-verified | [shared-state-audit.md](docs/research/shared-state-audit.md) |
| Dialogue — spectate, late-join, skip-fix | Live-verified | [dialogue-sync.md](docs/research/dialogue-sync.md) |
| Dialogue recruitment sync | Live-verified for two companions; one fix unconfirmed | [dialogue-consequences.md](docs/research/dialogue-consequences.md) |
| Session lifecycle — menu, connect, save/load rejoin | Live-verified | [session-save.md](docs/research/session-save.md) |
| Companion follow-AI | Live-verified, one known stall left | [party-reconciliation.md](docs/research/party-reconciliation.md) |
| Companion local combat | Narrow case live-verified | [party-reconciliation.md](docs/research/party-reconciliation.md) |
| Mod compatibility | Live-verified for most mismatch categories | [mod-compatibility.md](docs/research/mod-compatibility.md) |
| Sharing `dinput8.dll` with another mod | Live-verified (chain loader) | [src/KotorCoop.Loader](src/KotorCoop.Loader/dinput8_loader.c) |
| Minigames — swoop race | Blocked, needs a debugger | [minigames.md](docs/research/minigames.md) |
| Creature liveness reconciliation | Off by default, open | [creature-liveness.md](docs/research/creature-liveness.md) |

"Live-verified" means watched working on two real GOG instances; "Off by default" means built
and unit-tested but not shipped enabled. See "Honest limitations" below and `docs/research/`
for the dated evidence behind every row.

## What works today

Both players, each in their own full game:

- **See each other** as a real engine creature ("phantom") with real appearance, armor and
  weapons, walking/running on the real walkmesh, and on the minimap/M-screen map.
- **Travel together, either player leading**, through the engine's own transition path;
  loading screens overlap instead of running back to back. `follow_lead=host` restores
  host-only leading, `--no-follow` opts out.
- **Fight the same fight**: enemy health/death are host-authoritative and mirrored live; a
  hit on your partner's phantom lands for real through the engine's own damage pipeline,
  and non-damage effects (stun, slow, choke, ...) follow it.
- **Share the world.** Dropped items, doors, containers and locks, story triggers, and
  quest/journal state all merge upward for everyone. **Pause** and **downed/revive** (with
  spectate camera, `spectate_on_down=0` to opt out) work the same way, together.
- **Share one party**, host-driven AI mirrored to clients (`--no-party-sync` opts out), and
  **start from the game's own menu** — a "Co-op" button, password-protected, auto-reconnect.
- **Type commands in-game** (`~` — see [docs/CONSOLE.md](docs/CONSOLE.md)) and **hold one
  conversation at a time**, Baldur's Gate 3-style: the other player spectates live with a
  late joiner catching up instantly, and auto-join only pulls someone into a conversation
  the *engine itself* forces. Cutscene actors and story NPCs replicate the same way.

An unfocused or minimised instance keeps simulating, which is what makes one-machine
testing practical at all. **Mods don't have to match**: at join time each side fingerprints
its install (executable, override folder, `appearance.2da`, journal, string table) and the
two compare notes **per channel**, not all-or-nothing — a mismatched module only drops that
module's object-id-keyed channels, a different save asks you to rejoin (or refuses, with
`--require-identical-save`), and every decision is said on screen with a code (`E_EXE`,
`W_GIT`, `W_SAVE`, ...). See [mod-compatibility.md](docs/research/mod-compatibility.md).

## Requirements

- Windows. The packaged zip needs only the .NET 10 runtime; building from source needs the
  .NET 10 SDK and the Visual Studio 2022 MSVC x86 C++ toolset (NativeAOT `win-x86` plugin,
  C chain loader).
- **The DRM-free GOG build of KOTOR 1** (v1.0.3), not Steam — Steam's DRM refuses a second
  instance and its packed binary defeats static analysis. Addresses are verified against this
  binary (no ASLR). KOTOR 2 is untouched.
- For two instances on one machine: a second copy of the game directory — the single-instance
  guard is just a named mutex, renamed in memory by `agent launch --instance 2`.

## Quick start (players)

Download `kotor-coop-<version>.zip` from the
[releases page](https://github.com/ChristopherVR/kotor-coop/releases) — a tagged release, or
`latest`, which is rebuilt from every push to `main` that passes build and test — extract it
anywhere, and install it into your GOG game directory:

```powershell
pwsh .\install.ps1 -GameDir 'C:\GOG Games\Star Wars KOTOR (GOG)'
```

That's the plugin, the agent, the co-op override files and a `coop.ini` template in place in
one step; `-Uninstall` takes them back out and never touches your `coop.ini`. The zip's own
`README.md` walks through play, configuration and troubleshooting without needing this repo.

Then just play: launch KOTOR, click **Co-op** on the main menu, and one of you presses
**Host** while the other picks that server and presses **Connect**. Everything else lives in
`coop.ini` — see [docs/CONFIG.md](docs/CONFIG.md).

### Sharing `dinput8.dll` with another mod

Only one file can be called `dinput8.dll`. If another mod already owns it (ReShade, an ASI
loader, a widescreen fix), `install.ps1` refuses rather than overwrite it and offers chain
mode:

```powershell
pwsh .\install.ps1 -GameDir '...' -Chain                          # keep the other mod
pwsh .\install.ps1 -GameDir '...' -Chain -Load ReShade32.dll,x.asi # and load extra DLLs too
```

Chain mode installs the [`KotorCoop.Loader`](src/KotorCoop.Loader/dinput8_loader.c) wrapper —
6 KB of plain C, no runtime — as `dinput8.dll`, our plugin as `KotorCoop.Plugin.dll`, renames
the other mod to `dinput8.prev.dll`, and writes a `dinput8.chain` list. The loader loads every
entry and forwards the game's DirectInput calls to the first proxy (ours); the plugin forwards
to the entry after itself, and the last one reaches Windows' real `dinput8.dll`.
`coop.ini`'s `dinput8_next` overrides the plugin's next hop explicitly. `-Uninstall` restores
the other mod under its original name.

## Building from source

```powershell
dotnet build KotorCoop.slnx                     # agent, session, protocol, memory, tests
dotnet test  tests\KotorCoop.Tests
pwsh src\KotorCoop.Plugin\build.ps1 -Install    # plugin (NativeAOT x86) + agent + overrides -> the GOG default dir
pwsh src\KotorCoop.Loader\build.ps1             # the chain loader (only needed for chain mode)
pwsh tools\package.ps1                          # everything, zipped: dist\kotor-coop-<version>.zip
```

`build.ps1 -Install` deploys into `C:\GOG Games\Star Wars KOTOR (GOG)` by default; pass
`-GameDir` for another copy. Both native builds import the MSVC x86 environment themselves via
`tools\devenv.ps1` (vswhere-located, so any VS edition works). `-WarnAsError` on any of them is
what CI runs: an analyzer warning fails the build instead of scrolling past.

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
without a restart; Tab-completes (including the game's own console verbs), Up/Down recalls
history. A handful of verbs behave differently depending on who typed them — `god`,
`topup`, `tp`, `dropitem`, `applyeffect`, and more — for staging a test without grinding to
it. Full verb reference, arguments and authority rules: [docs/CONSOLE.md](docs/CONSOLE.md).

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

Discovery is external, engine calls are in-process; clients talk only to the host (star
relay, not mesh); authority is explicit (your character is yours, enemies are the host's);
positions stream and are superseded regularly, while flags/items/doors/journal are
retransmitted until acked and merge monotonically upward. Full design:
[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

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

- **Only the host can switch party character.** Companions are driven by the host's own AI
  and mirrored to everyone else. Disconnecting hands the whole party back; control returns
  to your own character automatically on connect. `control_return=0` opts out. See
  [control-return.md](docs/research/control-return.md).
- **Companion follow-AI still stalls at the last waypoint of a leg**, in a narrow,
  well-characterised case; two fix candidates exist but neither is confirmed live, so both
  ship off by default. See [party-reconciliation.md](docs/research/party-reconciliation.md).
- **A mirrored companion can't fight for a client** except through `companion_local_combat`
  (on in the shipped `coop.ini`), which releases a companion standing close enough to both
  players and the enemy to its own engine AI. See
  [party-reconciliation.md](docs/research/party-reconciliation.md).
- **One conversation at a time**, by design — the other player spectates rather than
  getting a separate copy.
- **Dialogue recruitment sync is live-confirmed for two companions** (`dialogue_recruit_sync`,
  on in the shipped `coop.ini`); a fix for a stale pre-recruit body (companions who are also
  placed NPCs) is implemented and unit-tested but not yet confirmed live. See
  [dialogue-consequences.md](docs/research/dialogue-consequences.md).
- **Mod-compatibility mismatch handling is live-verified for most categories**, but
  module-GIT and globals-catalogue mismatches are deliberately unforced in testing, and two
  installs whose *same* resref names a differently-modded item still aren't detected. See
  [mod-compatibility.md](docs/research/mod-compatibility.md).
- **Swoop racing is blocked.** The entry gate is found and understood, but the terminal
  transition call inside the game's own delayed-dispatch scheduler never fires; diagnosing
  further needs stepping through native engine code with a debugger this project doesn't
  have access to. See [minigames.md](docs/research/minigames.md).
- **Creature liveness reconciliation** (a body alive on one screen, dead on the other) ships
  off by default. The correction mechanism itself works; a separate freshness theory
  remains unconfirmed either way. See [creature-liveness.md](docs/research/creature-liveness.md).
- **Chain mode serialises proxies, it doesn't merge them.** A second `dinput8` proxy after
  ours works because ours forwards to it; a proxy that always loads the system DLL itself
  must be the last proxy in `dinput8.chain`. Plain "load me" DLLs (`.asi`) go anywhere.
- **The Endar Spire's command deck floors damage at 1 hp for everyone** — KOTOR's own tutorial
  script, not this mod ([min-one-hitpoint.md](docs/research/min-one-hitpoint.md)). Enemies
  target purely by proximity; alignment is per-player by design, no sync channel needed.

## Documentation map

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the design and why it looks like this.
- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) — prerequisites, build, test, the live rig,
  the CI/release pipeline, repo layout, code conventions, and how to add a feature end-to-end.
- [docs/CONFIG.md](docs/CONFIG.md) — every `coop.ini` key and CLI flag, with defaults.
- [docs/CONSOLE.md](docs/CONSOLE.md) — every in-game console verb.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — common problems and their causes.
- [docs/live-verification.md](docs/live-verification.md) — the two-instance checklist that
  turns "built" into "live-verified" here.
- [docs/FINDING-OFFSETS.md](docs/FINDING-OFFSETS.md) and [docs/re/](docs/re/) — how engine
  addresses were found, and the core RE (movement, object model, rendering, frame hook, spawn) independent of any one feature.
- [docs/research/README.md](docs/research/README.md) — the full per-feature research index.
- [tools/README.md](tools/README.md) — what every helper script in `tools/` does.
- [tools/package-README.md](tools/package-README.md) — the README that ships inside the zip.

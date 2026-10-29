# kotor-coop

[![CI](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml/badge.svg)](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml)
[![License: GPL-3.0-only](https://img.shields.io/badge/license-GPL--3.0--only-blue)](LICENSE)
![Status: experimental](https://img.shields.io/badge/status-experimental-orange)

A free, open-source mod that lets two to eight people play **Star Wars: Knights of the Old
Republic** (the GOG or Steam version) together. Each player runs their own copy of the game, on
one desk or over the internet, and the mod keeps everyone in one shared world, each as their own
character.

It is built in the spirit of **Elden Ring's Seamless Co-op**: nobody sets up a server and there
is no lobby to leave the game for. You start KOTOR, press **Co-op** on the main menu, and host or
connect. Friends drop in and drop out of the same world.

> **This is an experiment, and it is rough.** Read [Where this came from](#where-this-came-from)
> and [Other KOTOR co-op projects](#other-kotor-co-op-projects) before you invest an evening in it.

[Website and player guides](https://christophervr.github.io/kotor-coop/) ·
[Latest release](https://github.com/ChristopherVR/kotor-coop/releases/latest) ·
[Limitations](docs/limitations.md) · [Troubleshooting](docs/TROUBLESHOOTING.md)

## Install (one click)

1. Download **`kotor-coop-<version>-setup.exe`** from the
   [latest release](https://github.com/ChristopherVR/kotor-coop/releases/latest) (it is also on Nexus Mods).
2. Close KOTOR and run it. It finds your GOG or Steam copy (or lets you pick the folder), shows a
   short options page, and installs. Say yes to the firewall question if you want to host.
3. Start KOTOR and press **Co-op** on the main menu.

Both players need the **same mod version**. Prefer no installer? Download the Windows zip, extract it
into its own folder (not over the game), and run `KotorCoop.Agent.exe`; it does the same job.
Steam Deck / Linux players use the tarball and `install.sh` (see [Steam](#steam-and-linux)).

The files are not code-signed, so Windows SmartScreen may say "Windows protected your PC": choose
**More info**, then **Run anyway**. Every release lists SHA-256 checksums so you can verify what you
downloaded. If antivirus removes a file, please report it rather than adding an exclusion.

### Your first session

- **Host:** press **Co-op > Host** (from any save, or from a new game once you reach the first autosave).
- **Everyone else:** pick the host in the list and press **Connect**, or choose **Connect to an
  address...** and type what the host gives you. Over the internet, use a tunnel program such as
  [playit.gg](https://playit.gg); there is no relay server.
- The first time you join, the normal character-creation screen opens and you build your own
  character. It is yours on every later join, and your single-player saves are not touched.
- Press `~` for the console and **Enter** to chat ([console reference](docs/CONSOLE.md)).

Back up your `saves` folder before the first session. While connected, load the save called **Co-op**
to keep your progress, not Auto Save. Your own autosave is kept first as "Autosave before co-op".
Everything the installer changes, and how to remove it, is in [What the mod changes](#what-the-mod-changes).

## Requirements

- 64-bit Windows (Linux and Steam Deck through Proton, see below).
- **KOTOR 1, version 1.0.3**, from GOG or Steam. KOTOR 2 is not touched.
- The game runs at its own 800x600 size in co-op; wide-screen resolutions fell back to 800x600 when tried.
- Two copies on one PC need a second copy of the game folder, and at least one of the two must be GOG
  (Steam allows only one running copy).

## What to expect

Two people (up to eight) each run KOTOR. You see each other as real characters, walk through the same
doors, fly to the same planets, fight the same enemies, watch the same conversations, and keep your own
backpack and credits. Saving, loading, quitting to the menu and even the host's game crashing are
handled; the other player rejoins on its own.

- **One conversation at a time.** Whoever starts talking chooses the replies; the other player clicks
  the same person to watch.
- **The host leads the party.** Only the host switches the active party member and picks the party before
  a departure.
- **Backpacks and credits are personal.** Quest items are the exception, so the story works for both.
- **Minigames are single-seat.** One of you races or plays; the other waits.
- **Enemies get tougher with more players.** Playing alone changes nothing.
- **A complete two-player run from start to finish has not been played yet.** The early game and selected
  later scenes have been tried on two real copies; expect rough edges elsewhere.

The activity-by-activity list is [docs/limitations.md](docs/limitations.md). Read it before planning a long session.

| Area | Status | Details |
| --- | --- | --- |
| Getting into a game together | Works, with catches | [limitations](docs/limitations.md#getting-into-a-game-together) |
| Moving around the world | Works, with catches | [limitations](docs/limitations.md#moving-around-the-world) |
| Fighting | Works, with catches | [limitations](docs/limitations.md#fighting) |
| Talking to people | Works, with catches | [limitations](docs/limitations.md#talking-to-people) |
| Party, items and shops | Works, with catches | [limitations](docs/limitations.md#the-party-items-and-shops) |
| Minigames (Pazaak, swoop racing, turrets) | Works, with catches | [limitations](docs/limitations.md#minigames) |
| Story paths | Tried in staged scenes, not a full run | [limitations](docs/limitations.md#story-paths) |
| Controls | Works, with catches | [limitations](docs/limitations.md#controls-and-interface) |

**Mods do not have to match.** When you connect, the two installs compare notes and tell you on screen what
differs and what was switched off because of it. For a first session, use matching game mods and the same
co-op version.

## Where this came from

This project did not start as a mod. It started as a question: **how far can you take a 2003 game with
nothing but memory scanning and hooking, and no source code and no engine access?**

KOTOR has no networking layer for its story mode at all, and the community's long-standing answer was
that co-op is impossible without the executable's source. So the early versions did the obvious
experiment. A separate program attached to the running game, found the player's position in memory by
scanning for values and diffing snapshots, read it, and wrote the other player's position back. That
moved what the renderer drew but not what the simulation knew, so the "other player" had no collision
and could not be hit or talked to. The interesting part was everything we had to learn to fix that:

- **Finding things without symbols.** Signature scans against the *running* process (the executable on
  disk is packed, so the bytes in `.text` are not the bytes that execute), value-diff scans, and
  pointer paths from static globals. See [docs/FINDING-OFFSETS.md](docs/FINDING-OFFSETS.md) and
  [docs/re/](docs/re/).
- **Driving the engine instead of poking it.** Raw memory writes leave derived state stale, so the plugin
  spawns real creatures and moves them through the engine's own object system, the same calls a script
  would make. The notes on what those calls are, and the ones that crashed, are in
  [docs/research/](docs/research/README.md).
- **Hooks, kept to a minimum.** A small native plugin loaded as `dinput8.dll` (the game loads it by
  itself, so the executable on disk is never modified) installs a handful of hooks: one per frame, one
  in the main loop (which also runs at the menu) and one on the developer console's command interpreter.
  Everything else is calls into the engine.
- **A lot of trial and error.** The research directory is a lab notebook: roughly 150 write-ups of what
  worked, what crashed and why, kept because the dead ends are as useful as the findings.

The co-op mod is a **side effect** of that experiment. Once the avatars were real objects the engine
treated like any other creature, sharing doors, dialogue, combat, journals and saves became a series of
solvable problems, and at some point it turned into something people could actually play.

Because of that history, the codebase is unusual: an x86 NativeAOT C# plugin inside the game, an x64
helper that talks to it over a shared memory block, a UDP star topology with the host as authority, and a
large amount of reverse-engineering knowledge written down alongside the code. If you want to learn how
KOTOR's engine ticks, the documentation may be worth more than the mod.

### Elden Ring-style "seamless"

The design target was Elden Ring's Seamless Co-op, not a traditional multiplayer lobby. Concretely:

- **No server to run.** Whoever is hosting is just a player; everyone talks to the host.
- **Drop in, drop out.** Join a running game, leave, rejoin. A crash on either side is survived.
- **Your character is yours.** You keep your own character, backpack and credits, and the shared world
  (doors, containers, quests, enemies, the map) is the host's.
- **The game stays the game.** You play the normal campaign in the normal UI; the mod adds a Co-op
  button, a chat line and a console verb or two.

[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) compares what Elden Ring gives its co-op mod for free with
what had to be built here for KOTOR.

## Other KOTOR co-op projects

We are not the only ones working on this, and **the other projects may well turn out better in the long run.**
Two more recent ones we know of:

- [**masterxxstabs/KOTOR-COOP**](https://github.com/masterxxstabs/KOTOR-COOP): a persistent dedicated
  server that keeps the campaign state alive when nobody is playing, with a launcher and server browser
  and a client DLL. A "world that persists" design rather than a "drop in on a friend" one.
- [**McChaffeeFAF/kotor-coop**](https://github.com/McChaffeeFAF/kotor-coop): a fork of the
  reone open-source engine reimplementation with online co-op.
  Because it owns the engine, it does not depend on hooking the original executable.

Both have clear advantages over this project's approach. A server-authoritative design and a re-implemented
engine do not have to fight the original executable's internals, so they can in principle be more stable and
easier to maintain. Hooking the real game, as we do, gets you the original game and its mod ecosystem
exactly as it is, at the price of brittleness (a single exact executable version, a lot of engine
state to keep consistent, and crashes when we guess wrong). If one of those projects does what you need,
use it. This one is open source so that anything useful in it, including the reverse-engineering
notes, can be reused by them or by anyone else.

(Descriptions are from each project's public page and may be out of date; check them directly.)

## Steam and Linux

The Steam copy is the same game wrapped in a copy-protection layer, so installing is identical. Steam-to-Steam
and Steam-to-GOG sessions both work. Two copies on one PC need at least one GOG copy. The optional Steam
friends-list "Join Game" invite is explained in [docs/research/steam-support.md](docs/research/steam-support.md).

The release also carries a Linux tarball with `install.sh`, a bash port of the installer for the Windows
game running under Proton or Wine (including Steam Deck). It prints the launch option you need. The
installer is tested on Linux; gameplay on Linux is not a verified configuration. See
[docs/research/proton.md](docs/research/proton.md).

## What the mod changes

- It adds its files beside `swkotor.exe` (`dinput8.dll`, the plugin, `coop.ini`, a `coop-mods` folder, logs).
  The game executable is not changed. If another mod already uses `dinput8.dll` (ReShade, an ASI loader),
  the installer sets the two up side by side automatically.
- It turns on the game's cheat console setting (`EnableCheats=1` in `swkotor.ini`); the original file is kept
  as `swkotor.ini.kcoop-orig` and uninstall restores it.
- It keeps your own autosave as "Autosave before co-op" (the two most recent are kept) before co-op uses the
  Auto Save slot, adds a "Co-op" save and `saves\kotorcoop-character.*` for your co-op character.
- **Uninstall** from Windows "Installed apps", or `KotorCoop.Agent.exe uninstall`. It removes the mod files,
  logs and remembered address, and keeps `coop.ini`, the "Co-op" save and your co-op character.

Updates preserve `coop.ini`. Everything lives in that one commented file
(`profiles/coop.ini.example`; the full list is [docs/CONFIG.md](docs/CONFIG.md)). For a bug report,
double-click `KotorCoop.CollectLogs.cmd` or run `KotorCoop.Agent.exe logs` to make a support zip.
Problems are covered in [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).

## For developers

### Build from source

Needs the .NET 10 SDK and the Visual Studio 2022 MSVC x86 C++ tools (for the plugin and the chain loader).

```powershell
dotnet build KotorCoop.slnx                     # agent, session, protocol, memory, tests
dotnet test  tests\KotorCoop.Tests
pwsh src\KotorCoop.Plugin\build.ps1 -Install    # plugin + agent + overrides into the GOG default folder
pwsh tools\package.ps1                          # dist\kotor-coop-<version>-{setup.exe,windows.zip,linux.tar.gz}
```

`package.ps1` builds `setup.exe` when [Inno Setup 6](https://jrsoftware.org/isinfo.php) is installed
(`winget install JRSoftware.InnoSetup`). The solution build does not compile the plugin; use `build.ps1`.
To drive two instances unattended on one machine, see `tools/demo.ps1` and `tools/verify.ps1`.
The full build, test, live-rig and release reference is [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md).

### How it works

```
the game (unmodified on disk): a small in-process plugin adds a few hooks and a shared memory block
       <--- shared memory, found by signature scan --->
the agent (a separate program): reads your character, writes everyone else's, talks to the
other players over the network
       <--- UDP: positions, items, enemies, flags, journal --->
```

Everyone talks only to the host; authority is explicit (your character is yours, enemies are the host's).
Full design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

### Releases

A release is a tag. Push `v<VERSION>` and GitHub Actions rebuilds from it, runs the tests and installer
checks, and publishes `setup.exe`, the zip, the tarball and `SHA256SUMS.txt`. The release checklist is
[docs/RELEASE-READINESS.md](docs/RELEASE-READINESS.md). The Nexus Mods listing text and upload steps are
in [nexus/](nexus/README.md).

### Documentation map

- [docs/limitations.md](docs/limitations.md): what works and what doesn't, for players.
- [docs/status-engineering.md](docs/status-engineering.md): the same list with the evidence behind each status.
- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md): build, test, the live rig, CI and releases, code conventions.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): the design and why it looks like this.
- [docs/CONFIG.md](docs/CONFIG.md) and [docs/CONSOLE.md](docs/CONSOLE.md): every `coop.ini` key and console verb.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md): common problems and causes.
- [docs/FINDING-OFFSETS.md](docs/FINDING-OFFSETS.md), [docs/re/](docs/re/) and
  [docs/research/](docs/research/README.md): how engine addresses were found and what each feature learned.
- [docs/PLAYTEST.md](docs/PLAYTEST.md) and [docs/live-verification.md](docs/live-verification.md): what only a person at the keyboard can prove.
- [tools/README.md](tools/README.md): what every helper script does.

## Contributing

Issues and pull requests are welcome; start with [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md). When reporting
a bug include the release version, GOG or Steam, player count and steps to reproduce, and review logs before
sharing (remove addresses, passwords and personal paths). Never attach game files. Security reports:
[SECURITY.md](SECURITY.md).

## License and game ownership

Copyright (c) 2026 ChristopherVR and contributors. Original mod code, generated mod assets and project
documentation are licensed under **GNU GPL version 3 only** with the
[KOTOR 1 linking permission](KOTOR-LINKING-EXCEPTION.txt), without warranty. See [LICENSE](LICENSE) and
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

This unofficial fan mod does not own the game or its assets and is not endorsed by or affiliated with its
rights holders. It contains no game files and no DRM bypass; each player needs a lawfully acquired,
properly licensed game. We do not condone piracy. No publisher permission is claimed; see
[docs/LEGAL.md](docs/LEGAL.md) for the scope and the remaining uncertainty.

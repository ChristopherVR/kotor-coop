# kotor-coop

[![CI](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml/badge.svg)](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml)

A free mod that lets two to eight people play **Star Wars: Knights of the Old Republic** (the
retail GOG or Steam build) together. Two full copies of the game run side by side — on one
desk or over the internet — and an external helper program (the "agent") plus a small
in-game plugin keep them in one shared world, each player in their own character. It plays
the way FromSoftware's Seamless Co-op mod plays Elden Ring: nobody hosts a "co-op server",
you just start the game and connect.

No game file is changed. The plugin installs as a `dinput8.dll` the game already loads on
its own (or behind a tiny loader if another mod already uses that name); the agent attaches
from outside, the same way a debugger would.

**Players:** the plain-language download and install site is
[christophervr.github.io/kotor-coop](https://christophervr.github.io/kotor-coop/) (source in
[`site/`](site/)). This README is the same information for someone reading the repository —
players and curious developers both.

## Status at a glance

**You can play the whole game together.** Two people (up to eight) each run their own copy
of KOTOR, one hosts, the other connects from a **Co-op** button on the main menu, and from
then on you are in one world: you see each other as real characters, walk through the same
doors, fly to the same planets, fight the same enemies with one shared health bar per enemy,
loot one shared inventory, and watch the same conversations play out. Saving, loading,
quitting to the menu and even the host's game crashing are all handled — the other side
rejoins on its own.

What you should know before starting:

- **One conversation at a time.** Whoever starts talking chooses the replies; the other
  player watches it live — two people steering one dialogue would break the story.
- **The host owns the party.** Only the host swaps the active party member and picks the
  party before a departure. A companion recruited in a conversation reaches both players by
  default; a companion fighting *for the other player*, through their own copy of the game,
  is off by default (`companion_local_combat = 1` in `coop.ini` turns it on).
- **Merchants and the shared stash are one pool** — what one of you buys or takes is gone for
  the other. The host can set a buy/sell price multiplier (`store_prices` in `coop.ini`) and
  the joiner automatically uses it.
- **Minigames are single-seat.** One of you plays Pazaak or races; the other waits.
- **Enemies alternate between you** rather than sticking to whoever hurt them most — the
  game's own AI keeps pulling them back to the host.
- **A controller plays the game, but can't type.** An Xbox-style pad walks, fights and works
  every menu; the address/password box, chat and the console still need a keyboard.
- **Internet play works** over a tunnel tool such as playit.gg. A router's own automatic
  port-opening (UPnP) is still unconfirmed, and there is no relay server for networks it
  can't reach.
- **Enemies get tougher with more players** (more health, harder hits), on by default; set
  `enemy_health` / `enemy_damage` to `100` in `coop.ini` for the normal game.
- **A few things are off by default and still being proved live**: story flags reaching the
  other player (`local_var_sync`) and party-placed mines (`placed_mine_sync`) — each has a
  `coop.ini` key, described in [docs/limitations.md](docs/limitations.md).
- **Known issue:** disconnecting and reconnecting without loading a save can occasionally
  make the mod think you're on a different save and turn shared-world syncing off; loading
  your game once and reconnecting clears it. A fix is in progress.

The full list, activity by activity, is [docs/limitations.md](docs/limitations.md); the
dates, counts and research behind each line are in [docs/status-engineering.md](docs/status-engineering.md).

## Requirements

- Windows, or Linux / Steam Deck through Proton or Wine (see
  [Linux and Steam Deck](#linux-and-steam-deck-proton)). The packaged zip has no
  prerequisites — the agent carries its own .NET runtime.
- **KOTOR 1, v1.0.3.** Developed and tested against the DRM-free build; the Steam copy is the
  same binary under a DRM wrapper and is supported — see [Steam](#steam) below. KOTOR 2 is
  untouched.
- For two instances on one machine: a second copy of the game directory. The single-instance
  guard is just a named mutex, renamed by `agent launch --instance 2`.

## Quick start (players)

Download `kotor-coop-<version>-windows.zip` (or, on Linux / Steam Deck,
`kotor-coop-<version>-linux.tar.gz`) from the
[releases page](https://github.com/ChristopherVR/kotor-coop/releases) — a numbered version
for the most stable build, or `latest` for the newest one that's passed its automated checks.

The zip is a game-folder image: **extract it straight into the folder that contains
`swkotor.exe`**, or extract it anywhere and **double-click `KotorCoop.Agent.exe`** — it finds
your KOTOR install (GOG or Steam, including libraries on other drives), or asks if it finds
none or several, copies everything in, and offers to launch. No scripts, nothing to
configure. If another mod already owns `dinput8.dll` (ReShade, an ASI loader, ...), the
installer notices and chains kotor-coop alongside it automatically — see
[Sharing `dinput8.dll` with another mod](#sharing-dinput8dll-with-another-mod) below.

## First session

Launch KOTOR, click **Co-op** on the main menu. One of you presses **Host** (from any save,
or from a fresh game once you've reached the first autosave); the other picks that server in
the list and presses **Connect**. The agent starts itself in the background — nothing to
launch by hand.

**Which character you bring.** Co-op is one world — the host's — with each player in their
own body. Your **co-op character** is separate from any single-player save on the same
install: the mod makes it with you the first time you join, keeps it
(`saves\kotorcoop-character.*`), and carries it into the host's world on every join after
that. `coop.ini`'s `join_mode` controls this (`auto`, the default; `newcharacter` makes a new
one; `save` carries your own most recent save instead). While you are connected the mod keeps
its own dedicated save — **"Co-op"** in the Load Game list — refreshed every 10 minutes and as
you leave, so your co-op progress survives a crash. **Your Quick Save is never touched by the
mod**: it stays exactly whatever you last made it yourself, pressing F4 or the Save menu. The
game's own Auto Save slot is still used mechanically to bring the host's world in the instant
you join or rejoin (a KOTOR engine requirement, not something this mod chose), and — same as
any single-player game — the engine keeps autosaving through it as you walk through doors while
connected; treat it as ambient scratch space the way you always did, and load "Co-op" for the
save that is actually meant to be kept. Details:
[docs/research/world-adoption.md](docs/research/world-adoption.md) and
[docs/research/session-save.md](docs/research/session-save.md).

**Playing over the internet.** The reliable way today is a free tunnel program on the host,
such as [playit.gg](https://playit.gg). To join, pick **Connect to an address...** at the
bottom of the in-game server list (or `connect address:port` at the `~` console) and enter
the address your host shares. The mod also tries to open your router's port automatically
(UPnP); treat that as a bonus, not something to rely on yet.

**Playing with a controller.** Plug in any Xbox-style (XInput) pad and it just works, in
menus and in the world. Typing works too: open the address box, the password box, chat
(`Enter`) or the `~` console and an on-screen character grid appears — D-pad or the left stick
picks a letter, `A` types it, `B` backspaces, `X` is a space, `Y`/`Start` sends, `Back`
cancels, `LB` switches to upper case. `gamepad = 0` in `coop.ini` turns the whole layer off.
Details: [docs/research/gamepad.md](docs/research/gamepad.md).

### Sharing `dinput8.dll` with another mod

Only one file can be called `dinput8.dll`. Every install path detects a foreign one (ReShade,
Ultimate ASI Loader, ...) and installs as a chain automatically — no flag, no prompt beyond a
one-line summary of what was found. `--force` overwrites the foreign DLL outright instead;
`--chain`/`--load` force chain mode or add extra DLLs by hand:

```powershell
KotorCoop.Agent.exe install --game-path '...'                                # auto-detects and chains if needed
KotorCoop.Agent.exe install --game-path '...' --chain --load ReShade32.dll   # force chain mode, plus an extra DLL
KotorCoop.Agent.exe install --game-path '...' --force                       # overwrite the foreign DLL instead
```

Chain mode installs a small C loader as `dinput8.dll`, our plugin as `KotorCoop.Plugin.dll`,
renames the other mod to `dinput8.prev.dll`, and writes a `dinput8.chain` list; `.asi` mods
are left untouched. `install --check` reports the install layout without changing anything,
and `uninstall` restores the other mod under its original name.

## What works and what doesn't

| Area | Status | Details |
| --- | --- | --- |
| Getting into a game together (host/join, your character, saving, internet) | Works, with catches | [limitations.md § Getting into a game together](docs/limitations.md#getting-into-a-game-together) |
| Moving around the world (doors, galaxy map, companions following) | Works | [limitations.md § Moving around the world](docs/limitations.md#moving-around-the-world) |
| Fighting (shared enemies, real local combat, effects) | Works, with catches | [limitations.md § Fighting](docs/limitations.md#fighting) |
| Talking to people (one conversation at a time, recruiting) | Works, with catches | [limitations.md § Talking to people](docs/limitations.md#talking-to-people) |
| Party, items and shops | Works, with catches | [limitations.md § The party, items and shops](docs/limitations.md#the-party-items-and-shops) |
| Minigames (Pazaak, swoop racing) | Single-seat, both of you still get the reward | [limitations.md § Minigames](docs/limitations.md#minigames) |
| Controls (keyboard/mouse, gamepad) | Works | [limitations.md § Controls and interface](docs/limitations.md#controls-and-interface) |
| Linux and Steam Deck | Should work — not yet run on real hardware | [limitations.md § Linux and Steam Deck](docs/limitations.md#linux-and-steam-deck) |
| Story beats past Taris (Leviathan return, Manaan's sea floor, the ending) | Mostly untested with two players; a few moments known not to work yet | [docs/review-seamless-2026-09-18.md](docs/review-seamless-2026-09-18.md) |

**Mods don't have to match**: at join time each side fingerprints its install and the two
compare notes — a mismatched module only drops that module's channels, a different save asks
you to rejoin, and every decision is said on screen with a short code. See
[docs/research/mod-compatibility.md](docs/research/mod-compatibility.md).

Read [docs/limitations.md](docs/limitations.md) in full before planning a start-to-end run —
it's ordered the way you'll actually hit things (joining, moving, fighting, talking, party
and shops, minigames, controls).

## Steam

The Steam copy of KOTOR is the same binary this mod was built against, wrapped in an
encryption layer that unwraps itself before the game's own code runs, so installing is
identical to a DRM-free copy. **Steam ↔ Steam** and **Steam ↔ DRM-free** sessions both work
(the latter live-verified). Two copies on one PC still needs at least one DRM-free copy,
since Steam itself only allows one running instance. Full write-up, including the optional
Steam friends-list "Join Game" invite: [docs/research/steam-support.md](docs/research/steam-support.md).

## Linux and Steam Deck (Proton)

Both halves of the mod are plain Windows programs that run inside the game's Wine/Proton
prefix with nothing extra to install — the plugin is a native DLL with no runtime, and the
agent carries its own .NET. Take the Linux package from the releases page and run its
installer, then add one launch option so Wine loads the mod's `dinput8.dll` instead of its
own:

```bash
tar -xzf kotor-coop-<version>-linux.tar.gz && ./kotor-coop-<version>/install.sh
WINEDLLOVERRIDES="dinput8=n,b" %command%        # Steam / Steam Deck launch options
```

(Heroic: environment variable `WINEDLLOVERRIDES=dinput8=n,b`; Lutris: runner options → DLL
overrides `dinput8` → `n,b`.) Everything after that — hosting, joining, `coop.ini`, the
controller layer — is the same as on Windows.

**Honest status: nothing here has run on a real Linux machine yet.** It's built and reasoned
against Wine's documented behaviour, and both halves log `running under Wine x.y` so a report
from a real Deck or desktop tells us immediately where it got to. First live report wins.
Full write-up: [docs/research/proton.md](docs/research/proton.md).

## Settings

Everything lives in one `coop.ini` beside the game. The shipped file
(`profiles/coop.ini.example`) is short by design — connecting, your character, screen,
internet play and a few switches, each in plain language with a comment. Settings that
default off (like `local_var_sync` and `placed_mine_sync` above) are experimental: they work
in testing but haven't had every path checked live yet, so they ship off until they have. The
mod has a lot more internal tuning switches for development; they aren't in `coop.ini` on
purpose and setting one by hand is unsupported. Full table of every key, its shipped default
and what it does: [docs/CONFIG.md](docs/CONFIG.md).

Press `~` in-game for a console (`connect`/`disconnect`/`reconnect` manage the session
without a restart; Tab-completes, Up/Down recalls history) and **Enter** to chat with your
partner. Full reference: [docs/CONSOLE.md](docs/CONSOLE.md).

## Troubleshooting

Common problems and their actual causes — a missing Co-op button, nothing happening at all,
another mod owning `dinput8.dll`, a second instance that won't launch, and more — are in
[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md). If something goes wrong, run
`KotorCoop.Agent.exe logs` to collect one zip with the logs and a description of your setup,
and attach it to a bug report.

## Building from source

Needs the .NET 10 SDK and the Visual Studio 2022 MSVC x86 C++ toolset (for the NativeAOT
plugin and the C chain loader).

```powershell
dotnet build KotorCoop.slnx                     # agent, session, protocol, memory, tests
dotnet test  tests\KotorCoop.Tests
pwsh src\KotorCoop.Plugin\build.ps1 -Install    # plugin (NativeAOT x86) + agent + overrides -> the GOG default dir
pwsh tools\package.ps1                          # dist\kotor-coop-<version>-windows.zip + -linux.tar.gz
```

`build.ps1 -Install` deploys into `C:\GOG Games\Star Wars KOTOR (GOG)` by default
(`-GameDir` for another copy). For development — two instances driven unattended on one
machine:

```powershell
pwsh tools\demo.ps1          # launch both instances, load saves, connect the agents
pwsh tools\verify.ps1        # PASS/FAIL per feature against the live pair
pwsh tools\demo.ps1 -Stop    # shut everything down
```

`demo.ps1` does **not** deploy the plugin — run `build.ps1 -Install` first. The full build,
test, live-rig and release-pipeline reference, plus repo layout and code conventions, is
[docs/CONTRIBUTING.md](docs/CONTRIBUTING.md).

## How it works

```
the game (unmodified): a small in-process plugin adds a few hooks and a shared memory block
       <--- reads/writes the running game's memory --->
the agent (a separate program): reads your character, writes everyone else's, talks to the
other players over the network
       <--- network: positions, items, enemies, flags, journal --->
```

Everyone talks only to the host, not to each other. Authority is explicit: your character is
yours, enemies are the host's. Full design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Where the project is

The mod is playable start to finish for two players through Taris, and the same systems
should carry the rest of the campaign, but nobody has played a complete two-player run past
that point yet, and a handful of late-game story moments (the Leviathan prison break, the
dark-side temple choice, seeing the ending) are known not to work correctly for the second
player. The project's working checklist of what still needs a live check — and what closed
recently — is [docs/review-seamless-2026-09-18.md](docs/review-seamless-2026-09-18.md); the
evidence behind every "works" claim on this page is
[docs/status-engineering.md](docs/status-engineering.md).

## Documentation map

- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) — build, test, the live rig, CI/release pipeline, repo layout, code conventions.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the design and why it looks like this.
- [docs/CONFIG.md](docs/CONFIG.md) — every `coop.ini` key and CLI flag, with defaults.
- [docs/CONSOLE.md](docs/CONSOLE.md) — every in-game console verb.
- [docs/limitations.md](docs/limitations.md) — what works and what doesn't, in plain language, for players.
- [docs/status-engineering.md](docs/status-engineering.md) — the same list with the evidence behind every status, for developers.
- [docs/review-seamless-2026-09-18.md](docs/review-seamless-2026-09-18.md) — the current open-items checklist before the campaign is verified end to end.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — common problems and their causes.
- [docs/PLAYTEST.md](docs/PLAYTEST.md) — a checklist of things only a person at the keyboard can prove, with exact steps.
- [docs/live-verification.md](docs/live-verification.md) — the two-instance checklist that turns "built" into "live-verified" here.
- [docs/FINDING-OFFSETS.md](docs/FINDING-OFFSETS.md) and [docs/re/](docs/re/) — how engine addresses were found.
- [docs/research/README.md](docs/research/README.md) — the full per-feature research index.
- [tools/README.md](tools/README.md) — what every helper script in `tools/` does.
- [tools/package-README.txt](tools/package-README.txt) — the README that ships inside the zip.

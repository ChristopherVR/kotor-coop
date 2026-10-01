# kotor-coop

[![CI](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml/badge.svg)](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml)

A free mod that lets two to eight people play **Star Wars: Knights of the Old Republic** (the
GOG or Steam version) together. Each player runs their own copy of the game, on one desk or over
the internet, and the mod keeps everyone in one shared world, each in their own character. It
plays the way a seamless co-op mod does: nobody sets up a "server". You start the game, press
**Co-op** on the main menu, and connect.

No game file is changed. The mod loads the way the game already loads its own helper library, and
it can share that spot with other mods such as ReShade.

**Players:** the plain-language install and play guides are in [`site/`](site/). This README covers
the same ground for anyone reading the repository, players and developers both.

## What to expect

Two people (up to eight) each run KOTOR. One hosts, the other presses **Connect**, and from then on
you see each other as real characters, walk through the same doors, fly to the same planets, fight
the same enemies, watch the same conversations, and keep your own backpack and credits. Saving,
loading, quitting to the menu and even the host's game crashing are handled; the other player
rejoins on its own.

Things worth knowing before you start:

- **One conversation at a time.** Whoever starts talking chooses the replies and the other player
  watches. Two people steering one conversation would break the story.
- **The host leads the party.** Only the host switches the active party member and picks the party
  before a departure. Let the host lead companion recruiting and the big story choices.
- **Your backpack and credits are your own.** Quest items are the exception, so the story works for both.
- **Minigames are single-seat.** One of you races or plays; the other waits and is released after.
- **Enemies get tougher with more players** (more health, harder hits). Playing alone changes nothing.
- **Over the internet, use a tunnel program** such as [playit.gg](https://playit.gg). The built-in
  automatic port opening did not work on the one home connection it was tried on, and there is no relay server.
- **A complete two-player run from start to finish has not been played yet.** The early game and
  selected later scenes have been tried on two real copies; expect rough edges elsewhere.

The honest, activity-by-activity list is [docs/limitations.md](docs/limitations.md). It is written for
players, and it is the page to read before you plan a long session.

## Requirements

- Windows, or Linux and the Steam Deck through Proton or Wine ([details](#linux-and-steam-deck)). The
  download needs nothing else installed.
- **KOTOR 1, version 1.0.3**, from GOG or Steam. KOTOR 2 is not touched.
- To run two copies on one PC you need a second copy of the game folder, and at least one of the two
  must be the GOG version (Steam allows only one running copy).

## Install

Download the file for your computer from the
[Releases page](https://github.com/ChristopherVR/kotor-coop/releases): the newest numbered release
is the stable one, and **latest** is the newest build of the main branch that passed its automatic
checks.

- **Windows:** `kotor-coop-<version>-windows.zip`. Either extract it straight into the folder that
  contains `swkotor.exe`, or extract it anywhere and double-click `KotorCoop.Agent.exe`. It finds
  your KOTOR install (GOG or Steam, including other drives), copies everything in and offers to launch.
- **Linux and Steam Deck:** `kotor-coop-<version>-linux.tar.gz`, then see [below](#linux-and-steam-deck).

If another mod already uses the same helper file (ReShade, an ASI loader and similar), the installer
notices and sets the two up side by side automatically. See [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)
if anything looks off.

## Your first session

Start KOTOR and click **Co-op** on the main menu.

- **Host:** press **Host** (from any save, or from a new game once you reach the first autosave).
- **Everyone else:** pick the host in the list and press **Connect**. To join over the internet,
  choose **Connect to an address...** at the bottom of the list and type what the host gives you.

The first time you join, the normal character-creation screen opens and you build your own
character. That character is yours on every later join, and your single-player saves are never touched.
While connected, the mod keeps a save called **Co-op** in your Load Game list. Load that one to keep
your progress, not Auto Save.

**Controller:** plug in any Xbox-style pad and it works in menus and in the world. Typing an address,
password or chat line brings up an on-screen letter grid.

**Console and chat:** press `~` for the console and **Enter** to chat with your partner. The full list
is in [docs/CONSOLE.md](docs/CONSOLE.md).

## What works and what doesn't

| Area | Status | Details |
| --- | --- | --- |
| Getting into a game together | Works, with catches | [limitations § Getting into a game together](docs/limitations.md#getting-into-a-game-together) |
| Moving around the world | Works, with catches | [limitations § Moving around the world](docs/limitations.md#moving-around-the-world) |
| Fighting | Works, with catches | [limitations § Fighting](docs/limitations.md#fighting) |
| Talking to people | Works, with catches | [limitations § Talking to people](docs/limitations.md#talking-to-people) |
| Party, items and shops | Works, with catches | [limitations § The party, items and shops](docs/limitations.md#the-party-items-and-shops) |
| Minigames (Pazaak, swoop racing, turrets) | Works, with catches | [limitations § Minigames](docs/limitations.md#minigames) |
| Story paths (Dantooine Jedi training, the Leviathan, the dark-side temple, Manaan) | Works, with catches; tried in staged scenes, not a full run | [limitations § Story paths](docs/limitations.md#story-paths) |
| Controls | Works, with catches | [limitations § Controls and interface](docs/limitations.md#controls-and-interface) |
| Linux and Steam Deck | Should work; not yet run on real hardware | [limitations § Linux and Steam Deck](docs/limitations.md#linux-and-steam-deck) |

**Mods do not have to match.** When you connect, the two installs compare notes and the mod tells
you on screen what differs and what it switched off because of it.

## Steam

The Steam copy is the same game wrapped in a copy-protection layer, so installing is identical.
Steam-to-Steam and Steam-to-GOG sessions both work. Two copies on one PC need at least one GOG copy.
Details, including the optional Steam friends-list "Join Game" invite:
[docs/research/steam-support.md](docs/research/steam-support.md).

## Linux and Steam Deck

Both halves of the mod are Windows programs that run inside the game's Wine or Proton prefix with
nothing extra to install. Extract the Linux download, run its installer, then add one launch option so
Wine loads the mod's helper file:

```bash
tar -xzf kotor-coop-<version>-linux.tar.gz && ./kotor-coop-<version>/install.sh
WINEDLLOVERRIDES="dinput8=n,b" %command%        # Steam / Steam Deck launch options
```

(Heroic: set the environment variable `WINEDLLOVERRIDES=dinput8=n,b`. Lutris: runner options, DLL
overrides, `dinput8` set to `n,b`.) After that, hosting and joining work as on Windows.

**Honest status: nobody has run this on a real Linux machine yet.** The first report from a real Deck
or desktop will tell us where it got to. Details: [docs/research/proton.md](docs/research/proton.md).

## Settings

Everything lives in one `coop.ini` beside the game, short and commented in plain language
(`profiles/coop.ini.example`). A few options worth knowing:

- `inventory_mode = shared` makes everyone use one shared stash instead of personal backpacks.
- `companion_local_combat = 1` lets a companion fight for the other player in a fight away from the host.
- `placed_mine_sync = 1` shares mines you place yourself.
- `enemy_health` and `enemy_damage` set to `100` give the normal game's enemy toughness.
- `follow_lead = host` lets only the host lead area changes and galaxy-map trips.

The full list of every key and its default is [docs/CONFIG.md](docs/CONFIG.md). The installer never
overwrites your settings file, so after an update compare yours with the example.

## Troubleshooting

A missing Co-op button, nothing happening, another mod using the same helper file, a second copy that
will not launch, and more are in [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md). If something goes
wrong, run `KotorCoop.Agent.exe logs` to make one zip of logs and your setup, and attach it to a bug report.

## For developers

### Building from source

Needs the .NET 10 SDK and the Visual Studio 2022 MSVC x86 C++ tools (for the plugin and the chain loader).

```powershell
dotnet build KotorCoop.slnx                     # agent, session, protocol, memory, tests
dotnet test  tests\KotorCoop.Tests
pwsh src\KotorCoop.Plugin\build.ps1 -Install    # plugin + agent + overrides into the GOG default folder
pwsh tools\package.ps1                          # dist\kotor-coop-<version>-windows.zip and -linux.tar.gz
```

`build.ps1 -Install` deploys into `C:\GOG Games\Star Wars KOTOR (GOG)` by default (`-GameDir` for
another copy). To drive two instances unattended on one machine:

```powershell
pwsh tools\demo.ps1          # launch both instances, load saves, connect the agents
pwsh tools\verify.ps1        # PASS/FAIL per feature against the live pair
pwsh tools\demo.ps1 -Stop    # shut everything down
```

`demo.ps1` does not deploy the plugin, so run `build.ps1 -Install` first. The full build, test,
live-rig and release reference is [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md).

### Releases

The release number is the `VERSION` file. Every push to `main` that passes build and tests refreshes
the rolling **latest** pre-release. When `VERSION` names a version that has no GitHub release yet, the
first green build of it also becomes a numbered release, marked **Latest**, using
`docs/release-notes/v<version>.md` as its notes when that file exists (and GitHub's change list
otherwise). To ship a release: write the notes file, change `VERSION`, push. A `v*` tag pushed by
hand still works too.

### How it works

```
the game (unmodified): a small in-process plugin adds a few hooks and a shared memory block
       <--- reads/writes the running game's memory --->
the agent (a separate program): reads your character, writes everyone else's, talks to the
other players over the network
       <--- network: positions, items, enemies, flags, journal --->
```

Everyone talks only to the host. Authority is explicit: your character is yours, enemies are the
host's. Full design: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

### Documentation map

- [docs/limitations.md](docs/limitations.md): what works and what doesn't, in plain language, for players.
- [docs/status-engineering.md](docs/status-engineering.md) and the
  [limitations evidence log](docs/research/limitations-evidence-log-2026-10-02.md): the same list with the evidence behind every status.
- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md): build, test, the live rig, CI and release pipeline, repo layout, code conventions.
- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md): the design and why it looks like this.
- [docs/CONFIG.md](docs/CONFIG.md): every `coop.ini` key and command-line flag, with defaults.
- [docs/CONSOLE.md](docs/CONSOLE.md): every in-game console verb, and the agent window.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md): common problems and their causes.
- [docs/PLAYTEST.md](docs/PLAYTEST.md): things only a person at the keyboard can prove, with exact steps.
- [docs/live-verification.md](docs/live-verification.md): the two-instance checklist that turns "built" into "live-verified".
- [docs/review-seamless-2026-09-18.md](docs/review-seamless-2026-09-18.md): the open-items checklist before the campaign is verified end to end.
- [docs/FINDING-OFFSETS.md](docs/FINDING-OFFSETS.md) and [docs/re/](docs/re/): how engine addresses were found.
- [docs/research/README.md](docs/research/README.md): the full per-feature research index.
- [tools/README.md](tools/README.md): what every helper script in `tools/` does.
- [tools/package-README.txt](tools/package-README.txt): the README that ships inside the zip.

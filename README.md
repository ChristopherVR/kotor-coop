# kotor-coop

[![CI](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml/badge.svg)](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml)

Seamless-style two-player co-op for **Star Wars: Knights of the Old Republic** (retail GOG build), in C#. Two full
copies of the game run side by side — locally or across a network — and an external agent plus a tiny in-process
plugin bridge them into one shared world, the way FromSoftware's Seamless Co-op mod bridges Elden Ring.

No game file is modified. The plugin installs as a `dinput8.dll` proxy the game loads itself (or behind a tiny
chain loader when another mod already owns that name); everything else attaches from outside with `OpenProcess`.

## Status at a glance

**You can play the whole game together.** Two people (up to eight) each run their own copy of
KOTOR, one hosts, the other connects from a **Co-op** button on the main menu, and from then on
you are in one world: you see each other as real characters, walk through the same doors, fly to
the same planets, fight the same enemies with one shared health bar per enemy, loot one shared
inventory, and watch the same conversations play out. Saving, loading, quitting to the menu and
even the host crashing are all handled — the other side rejoins on its own.

What you should know before starting:

- **One conversation at a time.** Whoever starts talking to someone chooses the replies; the other
  player watches it live. That's deliberate — two people steering one dialogue would break the
  story.
- **The host owns the party.** Only the host swaps the active party member and picks the party
  before a departure. Companions follow the host's game.
- **Merchants and the shared stash are one pool** — what one of you buys or takes is gone for the
  other. A shop's stock is only re-read while someone has it open, and a shop that has an infinite
  supply of something will never show it changing (harmless).
- **Minigames are single-seat.** One of you plays Pazaak or races; the other is told they're waiting.
- **Enemies alternate between you rather than sticking to whoever hurt them most** — the game's own
  AI keeps pulling them back to the host.
- **A controller plays the game, but can't type.** An Xbox-style pad walks, fights and works every menu;
  the address/password box, chat and the console still need a keyboard.
- **Internet play has only been tested on a local network so far.** The pieces that should make it
  work on a typical home router are built (the host's address is found and the port opened
  automatically), but nobody has yet watched them against a real router; very restrictive networks
  can't be reached at all — there is no relay server.

The full list, activity by activity, is [docs/limitations.md](docs/limitations.md). Every line
there has been watched on two real copies of the game unless it says otherwise; the dates, counts
and research behind each one are in [docs/status-engineering.md](docs/status-engineering.md).

## What works today

Both players, each in their own full game:

- **See each other** as a real in-game character with your real appearance, armour and weapons,
  walking on the real floor, on the minimap and on the map screen.
- **Travel together, either player leading** — doors, area transitions and the galaxy map. The
  other player follows through the same loading screen (or the same take-off movie).
  `follow_lead=host` restores host-only leading, `--no-follow` opts out.
- **Fight the same fight**: enemy health and deaths come from the host's game and are mirrored
  live; your hits land, your own game really fights up to three enemies at once, and stuns, slows,
  chokes and heals reach your partner's real body. Going down puts you in a spectator camera until
  you're revived; a full party wipe reloads and rejoins automatically.
- **Share the world.** Dropped items, doors, containers and locks, story triggers, quests, the
  journal, credits, the party's inventory and merchant stock all stay in step. **Pause** pauses
  both games.
- **Share one party**, driven by the host's game and mirrored to everyone (`--no-party-sync` opts
  out), including a companion's equipment, experience and recruitment.
- **Start from the game's own menu** — a "Co-op" button, password-protected, auto-reconnect — and
  **type commands in-game** (`~`, see [docs/CONSOLE.md](docs/CONSOLE.md)) or **chat** (Enter).
- **Hold one conversation at a time**, Baldur's Gate 3-style: the other player spectates live and a
  late joiner catches up instantly.

An unfocused or minimised copy keeps running, which is what makes one-machine testing practical.
**Mods don't have to match**: at join time each side fingerprints its install and the two compare
notes — a mismatched module only drops that module's channels, a different save asks you to rejoin
(or refuses, with `--require-identical-save`), and every decision is said on screen with a code
(`E_EXE`, `W_GIT`, `W_SAVE`, ...). See [mod-compatibility.md](docs/research/mod-compatibility.md).

## Requirements

- Windows, or Linux / Steam Deck through Proton or Wine (see [Linux and Steam Deck](#linux-and-steam-deck-proton)).
  The packaged zip has no prerequisites — the agent ships with its own .NET runtime inside one exe. Building from
  source needs the .NET 10 SDK and the Visual Studio 2022 MSVC x86 C++ toolset (NativeAOT `win-x86` plugin, C chain
  loader).
- **KOTOR 1, v1.0.3.** A DRM-free build is what everything here is developed and tested against. The Steam copy is
  the *same binary* under a DRM wrapper, its addresses match at runtime, and it is supported: a Steam client and a
  DRM-free host have been verified in one live session — see [Steam](#steam) below. KOTOR 2 is untouched.
- For two instances on one machine: a second copy of the game directory — the single-instance guard is just a named
  mutex, renamed in memory by `agent launch --instance 2`.

## Quick start (players)

Download `kotor-coop-<version>-windows.zip` (or, on Linux / Steam Deck, `kotor-coop-<version>-linux.tar.gz`)
from the [releases page](https://github.com/ChristopherVR/kotor-coop/releases) — a tagged release, or `latest`,
rebuilt from every push to `main` that passes build and test. The zip is a game-folder image: **extract it straight
into the folder that contains `swkotor.exe`**, or extract it anywhere and **double-click `KotorCoop.Agent.exe`** —
it finds the GOG install (or asks), copies everything in, and offers to launch.

No scripts, nothing to configure. If another mod already owns `dinput8.dll` (ReShade, an ASI loader, ...), both
install paths notice and set kotor-coop up to run alongside it automatically — see
[Sharing `dinput8.dll` with another mod](#sharing-dinput8dll-with-another-mod) below; nothing extra to do. Launch
KOTOR, click **Co-op** on the main menu, one of you presses **Host** while the other picks that server and presses
**Connect** — the game starts the agent itself in the background. Everything optional lives in `coop.ini`
([docs/CONFIG.md](docs/CONFIG.md)); the zip's own `README.md` covers play, configuration and troubleshooting without
needing this repo. CLI equivalent: `KotorCoop.Agent.exe install|uninstall --game-path <dir>` or `install.ps1`.

### Joining: which character you bring

Co-op is one world — the host's — with each player in their own body. Your **co-op character** is separate from any
single-player game on the same install: the mod makes it with you the first time you join and keeps it
(`saves\kotorcoop-character.*`), and every join after that uses it. `coop.ini`'s `join_mode` says which:

| `join_mode` | what happens |
| --- | --- |
| `auto` (the default) | **First join:** connect, and the game's own character-creation screens open; make your character exactly as you normally would. Your game starts as usual; a few seconds later the mod quicksaves it and pulls you into the **host's** world with the character you just made (live-verified: 2 min 10 s, the player does nothing but chargen; arrival is at the module entry, ~5 units from the host). **Every later join:** that same character is carried straight into the host's world, whatever single-player saves you have — it keeps everything you earned in co-op, refreshed on every in-game save you make while connected |
| `newcharacter` | make a new co-op character on this join even though you already have one (it replaces the old one) |
| `save` | the old way: the character from your own most recent save is carried into the host's world. If your world already matches the host's, it just loads; if it does not, the mod fetches the host's world first (`auto_adopt_world`, on by default) |

Either way **your own saves are never touched** — only the autosave slot is written — so leaving the session and
loading your own game again costs nothing. What you give up when the worlds differ is the world you were playing:
party, inventory, companions, globals and journal all come from the host (your party is inherited cosmetically),
because in co-op those are shared. You bring your character; the host brings the world. A mismatched save
auto-adopts the host's world (`auto_adopt_world`, on by default), and a host serves its own world by default too.

See [docs/research/world-adoption.md](docs/research/world-adoption.md) for the mechanism.

### Sharing `dinput8.dll` with another mod

Only one file can be called `dinput8.dll`. Every install path — the CLI, `install.ps1`, `build.ps1 -Install` and the
double-click setup — detects a foreign one and installs as a chain automatically: no flag, no prompt beyond a single
summary line naming what was found (`ForeignDinputIdentity` recognises ReShade, Ultimate ASI Loader and a plain copy
of Windows' own `dinput8.dll` — the last of those isn't a mod, so it's just overwritten, no chain needed). `--force`
still overwrites a foreign DLL outright instead of chaining it if that's what you want, and `--chain`/`--load` remain
available to force chain mode or add extra DLLs explicitly:

```powershell
KotorCoop.Agent.exe install --game-path '...'                                # auto-detects and chains if needed
KotorCoop.Agent.exe install --game-path '...' --chain --load ReShade32.dll   # force chain mode, plus an extra DLL
KotorCoop.Agent.exe install --game-path '...' --force                       # overwrite the foreign DLL instead
```

Chain mode installs the [`KotorCoop.Loader`](src/KotorCoop.Loader/dinput8_loader.c) wrapper — 6 KB of plain C, no
runtime — as `dinput8.dll`, our plugin as `KotorCoop.Plugin.dll`, renames the other mod to `dinput8.prev.dll`, and
writes a `dinput8.chain` list. Each entry forwards to the next, the first proxy being ours; `coop.ini`'s
`dinput8_next` overrides the plugin's next hop explicitly. `.asi` mods beside the game (or in `scripts\`/`plugins\`)
are left untouched and named in the install summary. Re-running install over an existing chain install upgrades the
plugin in place and never duplicates a `dinput8.chain` entry — including one a player added by hand. `uninstall`
restores the other mod under its original name, and `install --check`/`install.ps1 -Check` reports the install
layout and verifies it (every chain entry exists, both markers are present, `coop.ini` exists) without changing
anything.

## Steam

Full write-up: [docs/research/steam-support.md](docs/research/steam-support.md).

**The Steam copy of KOTOR is the same binary this mod was built against.** Steam ships retail 1.0.3 wrapped in
Valve's SteamStub v3, which encrypts only the `.text` section on disk and decrypts it in place before the game's own
entry point runs. Everything else — the section table, `.rdata`, `.data`, `.rsrc`, the import table, the fixed
`0x00400000` load address — is byte-identical to the DRM-free file. So every hardcoded engine address is correct on
both at runtime, and the plugin loads the same way (`dinput8.dll` is a static import of both, and we start from
`DirectInput8Create` — well after the wrapper has finished).

**Live-verified on 2026-08-29** (docs/research/steam-support.md §18): the Steam copy was launched through Steam with
the mod installed, every hook installed with no byte-pattern refusal, and a **Steam client joined a DRM-free host** in
one session — connect, mutual presence, and a module transition with the world transferred across.

### Installing into the Steam copy

Exactly the same as any other install: put the files in the folder that holds `swkotor.exe` — normally
`C:\Program Files (x86)\Steam\steamapps\common\swkotor`. `KotorCoop.Agent.exe` (double-clicked, or
`install --game-path`) finds Steam installs on its own now, including libraries on other drives, and labels each one
with the form its exe is actually in. If another mod already owns `dinput8.dll` there, use chain mode exactly as you
would on a DRM-free install — the chain loader is a file-name convention among mods and has nothing to do with the
DRM wrapper.

Do **not** unwrap or replace the Steam `swkotor.exe`. The wrapper is not in the way of anything.

What that leaves is a much shorter list of real limitations:

| | |
| --- | --- |
| **Steam ↔ Steam** | Works. Both peers are identical in every respect the handshake looks at. |
| **Steam ↔ DRM-free** | **Works** (verified live 2026-08-29). The handshake now hashes the *running image* rather than the exe file, so the wrapped and unwrapped builds agree — they genuinely are the same code once loaded. Both peers need this version or newer; against an older build the executable check silently downgrades to a warning. |
| **Two copies on one PC** | Not possible from a Steam install: the wrapper goes through the Steam client, which allows one copy. Verified — `agent launch --instance 2` renames the mutex, and Steam still refuses. Use a DRM-free copy for the second instance (one of each works: run the Steam copy first, the DRM-free one as instance 2). Irrelevant for two players on two machines. |
| **Steam overlay** | Not a problem in practice. With the overlay loaded, the frame hook installed normally (`frame hook installed: hooked IAT 0x73D00C`) and kept firing, so the overlay does not own that import slot. If a future overlay build ever does, the plugin refuses rather than corrupting it and says so in `kotorcoop-plugin.log` — turn the overlay off for KOTOR (Properties → General → uncheck "Enable the Steam Overlay while in-game"). |

### Friends-list invites ("Join Game")

Optional and off unless you set it up. When it is on, hosting publishes a Steam rich-presence `connect` string so
friends can join from the friends list without being told an IP, and accepting an invite dials it through the normal
join path (password included, so you are never asked for one you were never given). Two console verbs make this
visible and reachable from in-game rather than only from a log file: `steamstatus` (is it active, your SteamID64, and
— while hosting — the exact string a friend receives) and `steaminvite <steamid64>` (push an invite to one friend
explicitly, for when they are not looking at your profile — see [docs/CONSOLE.md](docs/CONSOLE.md)).

Verified live on 2026-08-29 as far as a single machine can take it: the library loads, Steam accepts the agent as app
32370, and the `connect` string is published. **Re-verified and extended 2026-09-04**
(docs/research/steam-support.md §19): `ISteamUser::GetSteamID` is now bound too (this is what makes `steamstatus`
possible), `InviteUserToGame` was called for the first time and returned success, and `--steam-join` was driven
against a real second instance — not just decoded — ending in a normal connected session (the one address it
couldn't reach, its own public IP from inside the same LAN, is ordinary NAT hairpinning, not a Steam-invite bug). What
still needs a second Steam account is unchanged: a distinct friend actually receiving and accepting an invite.

It needs `steam_api64.dll` beside `KotorCoop.Agent.exe`, and **this project cannot ship one** — Valve licenses the
Steamworks redistributable to Steam partners, not to mod authors. Copy one from the Steamworks SDK or from any Steam
game you own (most ship it; look for `steam_api64.dll` under a game's folder). Drop it next to the agent and it is
picked up on the next run — no other setup, and the agent writes its own `steam_appid.txt` beside itself. Without the
DLL you get a single line in the log and everything else works exactly as before, which is why `steam_invites` is on
by default. Both players need to own KOTOR on Steam; while the agent runs, Steam shows you as playing KOTOR. It works
from a **GOG** install too — owning KOTOR on Steam is what matters, not launching it from there.

```ini
steam_invites = 1     ; coop.ini; 0 stops the agent talking to Steam at all
```

```powershell
KotorCoop.Agent.exe run --steam-join 'kc1/203.0.113.7:27140'   # what "Join Game" passes; also usable by hand
```

## Linux and Steam Deck (Proton)

Full write-up: [docs/research/proton.md](docs/research/proton.md).

Both halves of the mod are Windows programs that run inside the game's Wine/Proton prefix, and both are built so
that the prefix needs nothing added: the plugin is a native x86 DLL with no runtime, and since 2026-09-03 the
packaged agent is one self-contained `KotorCoop.Agent.exe` with the .NET runtime inside (that is also why a
Windows player no longer needs .NET installed). The installer recognises Wine and looks for the game where Proton,
Heroic and Lutris put it (`Z:\home\…` Steam libraries, `Games\Heroic`, `Games`, `STEAM_COMPAT_INSTALL_PATH`).

What a Linux player does differently, in short: take the Linux package from the releases page and run its
installer — a bash port of the C# installer (`tools/install.sh`) that runs natively, finds the game in the Steam
libraries / Heroic / Lutris, chains an existing `dinput8.dll` mod the same way, and has the same `--check` and
`--uninstall` — then add the DLL override that makes Wine load our `dinput8.dll` instead of its built-in one:

```bash
tar -xzf kotor-coop-<version>-linux.tar.gz && ./kotor-coop-<version>/install.sh
WINEDLLOVERRIDES="dinput8=n,b" %command%        # Steam / Steam Deck launch options
```

(The Windows installer also works from inside the prefix — `protontricks-launch --appid 32370 KotorCoop.Agent.exe`,
Heroic's *Run EXE on prefix*, Lutris' *Run EXE inside Wine prefix* — and extracting either archive straight into
the game folder is a plain install.) CI installs the tarball on an Ubuntu runner against a stand-in game folder on
every push (plain, chain, upgrade, uninstall), so the installer itself is exercised even though the game is not.

(Heroic: game settings → environment variable `WINEDLLOVERRIDES=dinput8=n,b`; Lutris: runner options → DLL
overrides `dinput8` → `n,b`.) Everything after that — hosting, joining, `coop.ini`, the controller layer with the
Deck's own controls — is the same as on Windows. The recipe and the caveats are spelled out in the packaged README.

**Honest status: nothing here has run on a Linux machine yet.** The engineering is done and reasoned against
Wine's documented behaviour, the plugin and agent log `running under Wine x.y` so a report from a real Deck or
desktop tells us immediately where it got to, and the open questions are listed in
[proton.md](docs/research/proton.md) §6. First live report wins.

## Building from source

```powershell
dotnet build KotorCoop.slnx                     # agent, session, protocol, memory, tests
dotnet test  tests\KotorCoop.Tests
pwsh src\KotorCoop.Plugin\build.ps1 -Install    # plugin (NativeAOT x86) + agent + overrides -> the GOG default dir
pwsh src\KotorCoop.Loader\build.ps1             # the chain loader (only needed for chain mode)
pwsh tools\package.ps1                          # dist\kotor-coop-<version>-windows.zip + -linux.tar.gz
```

`build.ps1 -Install` deploys into `C:\GOG Games\Star Wars KOTOR (GOG)` by default (`-GameDir` for another copy);
both native builds import the MSVC x86 environment themselves via `tools\devenv.ps1` (vswhere-located, so any VS
edition works). `-WarnAsError` is what CI runs. For development — two instances driven unattended on one machine:

```powershell
pwsh tools\demo.ps1          # launch both instances, load saves, connect the agents
pwsh tools\verify.ps1        # PASS/FAIL per feature against the live pair
pwsh tools\demo.ps1 -Stop    # shut everything down
```

`demo.ps1` does **not** deploy the plugin — run `build.ps1 -Install` first for every game copy the rig uses.
Driving the agent by hand, `KotorCoop.MockGame` (a stand-in process for testing without KOTOR), and the CI/release
pipeline: see [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md).

## Playing with a controller

Plug in any Xbox-style (XInput) controller and it just works — in the menus as well as in the world, and
in the base game too, which never supported one. Left stick walks and turns, right stick is the cursor,
A is the default action, B/Start is Escape, X cancels combat, Y switches character, LB/RB cycle targets,
LT pauses, RT clicks what the cursor points at (as with a mouse, one click selects a door, container or
character and a second click on it walks over and uses it — the game has no click-to-move, the left stick
is how you walk), the D-pad is action slots 1-4, Back opens the map, LS click is stealth. On any menu or panel A clicks, X right-clicks, the D-pad scrolls and pages; RS click
forces cursor mode on and off. Your own `swkotor.ini` key bindings are respected. The one thing a pad
can't do is type: the address/password box, chat and the `~` console still need a keyboard. `gamepad = 0`
in `coop.ini` turns it off; `gamepad status` in the console shows what it is doing. Details:
[docs/research/gamepad.md](docs/research/gamepad.md).

## Console

Press `~` in-game for the console. `connect`/`disconnect`/`reconnect` manage the session without a restart;
Tab-completes (including the game's own verbs), Up/Down recalls history. A handful of verbs (`god`, `topup`, `tp`,
`dropitem`, `applyeffect`, and more) stage a test without grinding to it. Full reference: [docs/CONSOLE.md](docs/CONSOLE.md).

## Configuration

Everything lives in one `coop.ini` beside the game, parsed by two independent halves — the external agent and the
in-process plugin each read their own keys from it. The shipped file (`profiles/coop.ini.example`) is short by
design — every player setting there is, in plain language: connecting, your character, screen, internet play and a
few advanced switches. The mod has a lot more internal tuning/bisect switches for development; they aren't in
`coop.ini` on purpose, and setting one by hand is unsupported. Full table of every key, player-facing or not, its
shipped default and what it does: [docs/CONFIG.md](docs/CONFIG.md).

## How it works

```
swkotor.exe (x86): dinput8.dll proxy, SharedBlock, frame hook, engine calls
       <--- signature scan / shared block --->
agent (x64, external): KotorGameBridge reads player, writes peers, runs CoopSession
       <--- UDP: PlayerState, items, enemies, flags, journal --->
```

Discovery is external, engine calls are in-process; clients talk only to the host (star relay, not mesh); authority
is explicit (your character is yours, enemies are the host's); positions stream and are superseded regularly, while
flags/items/doors/journal retransmit until acked and merge monotonically upward. Full design:
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

Two documents, for two readers:

- **[docs/limitations.md](docs/limitations.md)** — for players. What works, what works with a
  catch, what is deliberately not shared, and what nobody has been able to test on real hardware
  yet, in everyday words, ordered the way you'll hit it (joining, moving, fighting, talking, party
  and shops, minigames, controls). Read this before planning a start-to-end run.
- **[docs/status-engineering.md](docs/status-engineering.md)** — for developers. The same list
  with the dates, counts, rule names and research links behind each status, plus the full feature
  table and detailed engineering notes that used to live on this page.

The things most likely to surprise a first-time pair are already in "Status at a glance" above:
one conversation at a time, the host owns the party, minigames are single-seat, a controller can't type, and
internet play is untested against a real router. Things that *look* like bugs but aren't — the
1-hp floor on the Endar Spire, an old `coop.ini` keeping a stale default — are listed at the end of
docs/limitations.md.

## Documentation map

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the design and why it looks like this.
- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) — build, test, the live rig, CI/release pipeline, repo layout, code conventions, and how to add a feature end-to-end.
- [docs/CONFIG.md](docs/CONFIG.md) — every `coop.ini` key and CLI flag, with defaults.
- [docs/CONSOLE.md](docs/CONSOLE.md) — every in-game console verb.
- [docs/limitations.md](docs/limitations.md) — what works and what doesn't, in plain language, for players.
- [docs/status-engineering.md](docs/status-engineering.md) — the same list with the evidence behind every status, plus the full feature table, for developers.
- [docs/roadmap-seamless.md](docs/roadmap-seamless.md) — what's left before "seamless," tiered by priority.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — common problems and their causes.
- [docs/live-verification.md](docs/live-verification.md) — the two-instance checklist that turns "built" into "live-verified" here.
- [docs/FINDING-OFFSETS.md](docs/FINDING-OFFSETS.md) and [docs/re/](docs/re/) — how engine addresses were found, and the core RE independent of any one feature.
- [docs/research/README.md](docs/research/README.md) — the full per-feature research index.
- [tools/README.md](tools/README.md) — what every helper script in `tools/` does.
- [tools/package-README.txt](tools/package-README.txt) — the README that ships inside the zip.

# kotor-coop

[![CI](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml/badge.svg)](https://github.com/ChristopherVR/kotor-coop/actions/workflows/ci.yml)

Seamless-style two-player co-op for **Star Wars: Knights of the Old Republic** (retail GOG build), in C#. Two full
copies of the game run side by side — locally or across a network — and an external agent plus a tiny in-process
plugin bridge them into one shared world, the way FromSoftware's Seamless Co-op mod bridges Elden Ring.

No game file is modified. The plugin installs as a `dinput8.dll` proxy the game loads itself (or behind a tiny
chain loader when another mod already owns that name); everything else attaches from outside with `OpenProcess`.

## Status at a glance

| Area | Status | Doc |
| --- | --- | --- |
| Presence, appearance, animation, minimap | Live-verified. A peer rendering as a stranger or as Trask after a warp — both root causes closed 2026-08-29 (`PeerIdentityRule`, `PartyCensusRule`) | [phantom-locomotion.md](docs/research/phantom-locomotion.md), [name-plates.md](docs/research/name-plates.md) |
| Travel — transitions, doors, triggers | Live-verified | [transition-smoothness.md](docs/research/transition-smoothness.md) |
| Combat — mirrored enemies, damage, effects | Live-verified (a reported "client can't hit an Endar Spire Sith" was root-caused 2026-08-28 to that creature starting in a FRIENDLY faction — not a co-op defect). Non-damage effect sync (`effect_sync`): 12 kinds mapped, 12/12 live-verified both directions (2026-09-03); the replay-on-every-module-arrival the last pass exposed is closed (`PeerEffectReplayRule`) | [combat-participation.md](docs/research/combat-participation.md), [effect-sync.md](docs/research/effect-sync.md) |
| World state — containers, items, journal | Live-verified (container-lock arbitration confirmed correct AND fast-path-fixed at a 1000ms budget; `ItemTemplateSpawnHook` confirmed live end to end, 2026-08-28) | [shared-state-audit.md](docs/research/shared-state-audit.md) |
| Dialogue — spectate, late-join, skip-fix | Live-verified | [dialogue-sync.md](docs/research/dialogue-sync.md) |
| Dialogue recruitment sync | Live-verified for two companions; one fix unconfirmed | [dialogue-consequences.md](docs/research/dialogue-consequences.md) |
| Session lifecycle — menu, connect, save/load rejoin | Live-verified; the `host_load_readopt` regression and the STUNT_00 escape-pod crash are both CLOSED — same root cause, a dangling-pointer write storm in `PartySelectSuppressionController`, fixed 2026-08-27 and reconfirmed clean across three independent live re-verifications (last 2026-08-28) | [session-save.md](docs/research/session-save.md), [galaxy-map-and-party-select.md](docs/research/galaxy-map-and-party-select.md) |
| Joining without a save (`join_mode = newcharacter`) | Live-verified 2026-08-29 end to end — chargen, quicksave capture, arrival in the host's served world | [world-adoption.md](docs/research/world-adoption.md) |
| Three-peer sessions (3+ players) | Live-verified — `demo3.ps1`/`verify3.ps1`, 34/34 checks passed, chat/phantoms/hit-tables all agreeing across three instances | [live-verification.md](docs/live-verification.md) |
| Companion follow-AI | **Stall fixed and on by default, 2026-08-29** (`party_follow_stall_recovery`). Root cause named: the engine's own `PathWaitDest` accept is a STRICT compare against a recorded trail node that has collapsed onto the cached prior position, so it can never pass and the companion freezes — reproduced 4/4, including a leader teleported 16.5 units away that the companion never recovered from. A runtime wedge detector now gathers the party through the engine's own `PositionParty`; five reps, every wedge recovered on the first attempt. Still by design: a MIRRORED companion's follow-AI does not run at all | [party-reconciliation.md](docs/research/party-reconciliation.md) |
| Companion local combat | Narrow case live-verified (release/attack); damage TAKEN by the released companion now forwards to the host and is spent as real damage on its own body (`companion_damage_sync`, ON by default), closing the "released companion is invulnerable" gap — NEEDS-LIVE | [party-reconciliation.md](docs/research/party-reconciliation.md), [companion-state-sync.md](docs/research/companion-state-sync.md) §13 |
| Mod compatibility | Live-verified for most mismatch categories | [mod-compatibility.md](docs/research/mod-compatibility.md) |
| Sharing `dinput8.dll` with another mod | Live-verified (chain loader) | [src/KotorCoop.Loader](src/KotorCoop.Loader/dinput8_loader.c) |
| Minigames — swoop race | Root-caused 2026-08-29 (the blocker was the test recipe, not the mod); race runs end to end, peers locked out of the single-seat track | [minigames.md](docs/research/minigames.md) |
| Minigames — Pazaak detection, "waiting" notice | Live-verified 2026-08-28 (a range-guard bug that silenced detection was found and fixed); `pazaak_detect` flipped ON by default | [minigames.md](docs/research/minigames.md) |
| Creature liveness reconciliation | Live-verified 2026-09-03 (destroy/kill dispatch, 49-creature population, zero freshness disagreements), ON by default | [creature-liveness.md](docs/research/creature-liveness.md) |
| Peer-away detection (`FlagAway`) when a partner quits to the main menu | Live-verified 2026-08-27 (an initial pass failed; the fix landed and re-verified the same day) | [disconnect-reconnect.md](docs/research/disconnect-reconnect.md) |
| Host loss (`host_loss_release`) — a client whose host vanishes | Live-verified 2026-08-29 — every plugin-side lock releases within ~6s (`host_loss_timeout_ms` 5000, `PacketType.HostBye`), the client plays solo, and auto-rejoins when the host returns | [disconnect-reconnect.md §11](docs/research/disconnect-reconnect.md) |
| Persistent connection indicator | Live-verified 2026-08-27 (partial — data path confirmed, on-screen render unconfirmed) | [connect-flow.md](docs/research/connect-flow.md) |
| Protocol-version-mismatch reject (`E_PROTO`) | Built, not live-verified | [connect-flow.md](docs/research/connect-flow.md) |
| Script-granted items outside dialogue (`script_item_sync`) | Built, live-verified, ON by default | [dialogue-consequences.md](docs/research/dialogue-consequences.md) |
| Companion experience (`companion_xp_sync`) | Live-verified, ON by default | [companion-state-sync.md](docs/research/companion-state-sync.md) |
| Companion equipment (`companion_gear_sync`) | Live-verified 2026-08-28, ON by default — body slot, right weapon and left weapon all confirmed (the earlier weapon-slot bug, a wrong internal slot encoding, is fixed), mid-session propagation under 3s | [companion-state-sync.md](docs/research/companion-state-sync.md) |
| Shared party item pool (`party_pool_sync`) | **On by default (mode 2), 2026-08-29.** Host-authoritative absolute snapshot + client intents: only the host publishes state, clients request. Live-verified twice on a two-instance rig with zero fabricated units - three rapid host deposits mirroring one-for-one, a client deposit and withdrawal each applied exactly once, two five-minute idles with zero publishes. Verified with `dialogue_item_sync`/`script_item_sync` ON, which caught and closed the last cross-channel echo. Mode 1 (the op-log) stays disqualified and available only at `=1` | [party-pool-sync.md](docs/research/party-pool-sync.md), [shared-inventory-v2.md](docs/research/shared-inventory-v2.md) |
| Merchant store stock (`store_sync`) | **On by default (mode 2), 2026-08-29.** Same host-authoritative design as the party pool. Three clean live sessions in a row: a client buy, a client sell-back, a host re-buy that succeeded exactly once, a second re-buy correctly refused, and 155s of idle with zero engine writes on either side - every write accounted for, none spurious. Reads a shop only while its GUI is open (engine limitation); mode 1 stays disqualified and available only at `=1` | [store-sync.md](docs/research/store-sync.md), [shared-inventory-v2.md](docs/research/shared-inventory-v2.md) |
| Fog-of-war reveal (`map_reveal_sync`) | Live-verified, on by default | [map-reveal.md](docs/research/map-reveal.md) |
| Star Forge finale — Bastila/Malak duel spectate (`solo_segment_spectate`) | Live-verified 2026-08-28, ON by default — a script-agnostic `RemovePartyMemberHook` burst trigger reaches the segment without needing the Bastila dialogue: the roster genuinely empties, the peer's phantom hides, the spectator's input parks and camera follows, a real refill ends it. Only the real-physical-WASD half of the input lock remains unprovable (injected keystrokes never reach this game) | [star-forge-finale.md](docs/research/star-forge-finale.md) |
| Internet play — public address discovery (STUN), UPnP port mapping | Built, unit-tested, not live-verified against a real router; no relay/hole-punching | [network-topology.md](docs/research/network-topology.md) |

"Live-verified" means watched working on two real GOG instances; "Off by default" means built
and unit-tested but not shipped enabled. See "Honest limitations" below and `docs/research/`
for the dated evidence behind every row.

## What works today

Both players, each in their own full game:

- **See each other** as a real engine creature ("phantom") with real appearance, armor and weapons, walking/running
  on the real walkmesh, and on the minimap/M-screen map.
- **Travel together, either player leading**, through the engine's own transition path; loading screens overlap.
  `follow_lead=host` restores host-only leading, `--no-follow` opts out.
- **Fight the same fight**: enemy health/death are host-authoritative and mirrored live; a hit on your partner's
  phantom lands for real, and non-damage effects (stun, slow, choke, ...) follow.
- **Share the world.** Dropped items, doors, containers/locks, story triggers, and quest/journal state all merge
  upward. **Pause** and **downed/revive** (spectate camera, `spectate_on_down=0` to opt out) work the same way.
- **Share one party**, host-driven AI mirrored to clients (`--no-party-sync` opts out), and **start from the game's
  own menu** — a "Co-op" button, password-protected, auto-reconnect.
- **Type commands in-game** (`~` — see [docs/CONSOLE.md](docs/CONSOLE.md)) and **hold one conversation at a time**,
  Baldur's Gate 3-style: the other player spectates live (clicks AND skips both swallowed) with a late joiner
  catching up instantly; auto-join only pulls someone in when the *engine* forces it.

An unfocused or minimised instance keeps simulating, which is what makes one-machine testing practical. **Mods
don't have to match**: at join time each side fingerprints its install (executable, override folder,
`appearance.2da`, journal, string table) and the two compare notes **per channel** — a mismatched module only drops
that module's channels, a different save asks you to rejoin (or refuses, with `--require-identical-save`), and
every decision is said on screen with a code (`E_EXE`, `W_GIT`, `W_SAVE`, ...). See
[mod-compatibility.md](docs/research/mod-compatibility.md).

## Requirements

- Windows. The packaged zip needs only the .NET 10 runtime; building from source needs the .NET 10 SDK and the
  Visual Studio 2022 MSVC x86 C++ toolset (NativeAOT `win-x86` plugin, C chain loader).
- **KOTOR 1, v1.0.3.** A DRM-free build is what everything here is developed and tested against. The Steam copy is
  the *same binary* under a DRM wrapper, its addresses match at runtime, and it is supported: a Steam client and a
  DRM-free host have been verified in one live session — see [Steam](#steam) below. KOTOR 2 is untouched.
- For two instances on one machine: a second copy of the game directory — the single-instance guard is just a named
  mutex, renamed in memory by `agent launch --instance 2`.

## Quick start (players)

Download `kotor-coop-<version>.zip` from the
[releases page](https://github.com/ChristopherVR/kotor-coop/releases) — a tagged release, or `latest`, rebuilt from
every push to `main` that passes build and test. The zip is a game-folder image: **extract it straight into the
folder that contains `swkotor.exe`**, or extract it anywhere and **double-click `KotorCoop.Agent.exe`** — it finds
the GOG install (or asks), copies everything in, and offers to launch.

No scripts, nothing to configure. Launch KOTOR, click **Co-op** on the main menu, one of you presses **Host** while
the other picks that server and presses **Connect** — the game starts the agent itself in the background.
Everything optional lives in `coop.ini` ([docs/CONFIG.md](docs/CONFIG.md)); the zip's own `README.md` covers play,
configuration and troubleshooting without needing this repo. CLI equivalent:
`KotorCoop.Agent.exe install|uninstall --game-path <dir>` or `install.ps1`.

### Joining: which character you bring

Co-op is one world — the host's — with each player in their own body. How your body gets there depends on what you
already have, and `coop.ini`'s `join_mode` says which:

| your situation | `join_mode` | what happens |
| --- | --- | --- |
| you have a save | `save` (the default) | your save's character is carried into the host's world. If your world already matches the host's, it just loads; if it does not, the mod fetches the host's world first (`auto_adopt_world`, on by default) |
| you have never played this game | `newcharacter` | connect, then choose **New Game** at the main menu and create a character exactly as you normally would. Your game starts as usual; a few seconds later the mod quicksaves it and pulls you into the **host's** world with the character you just made (live-verified: 2 min 10 s, the player does nothing but chargen; arrival is at the module entry, ~5 units from the host) |

Either way **your own saves are never touched** — only the autosave slot is written — so leaving the session and
loading your own game again costs nothing. What you give up when the worlds differ is the world you were playing:
party, inventory, companions, globals and journal all come from the host (your party is inherited cosmetically),
because in co-op those are shared. You bring your character; the host brings the world. A mismatched save
auto-adopts the host's world (`auto_adopt_world`, on by default), and a host serves its own world by default too.

A `newcharacter` join leaves a save of your own behind — the one the mod made to carry your character across — so
from the next session on, `join_mode = save` (the default) does the right thing without being told.

See [docs/research/world-adoption.md](docs/research/world-adoption.md) for the mechanism.

### Sharing `dinput8.dll` with another mod

Only one file can be called `dinput8.dll`. If another mod already owns it (ReShade, an ASI loader, a widescreen
fix), the setup notices and offers to keep it (the command line refuses unless told `--chain` or `--force`):

```powershell
KotorCoop.Agent.exe install --game-path '...' --chain                        # keep the other mod
KotorCoop.Agent.exe install --game-path '...' --chain --load ReShade32.dll   # and load extra DLLs
```

Chain mode installs the [`KotorCoop.Loader`](src/KotorCoop.Loader/dinput8_loader.c) wrapper — 6 KB of plain C, no
runtime — as `dinput8.dll`, our plugin as `KotorCoop.Plugin.dll`, renames the other mod to `dinput8.prev.dll`, and
writes a `dinput8.chain` list. Each entry forwards to the next, the first proxy being ours; `coop.ini`'s
`dinput8_next` overrides the plugin's next hop explicitly. `uninstall` restores the other mod under its original
name.

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

Optional and off unless you set it up. Verified live on 2026-08-29 as far as a single machine can take it: the
library loads, Steam accepts the agent as app 32370, and the `connect` string is published. When it is on, hosting publishes a Steam
rich-presence `connect` string so friends can join from the friends list without being told an IP, and accepting an
invite dials it through the normal join path (password included, so you are never asked for one you were never
given).

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

## Building from source

```powershell
dotnet build KotorCoop.slnx                     # agent, session, protocol, memory, tests
dotnet test  tests\KotorCoop.Tests
pwsh src\KotorCoop.Plugin\build.ps1 -Install    # plugin (NativeAOT x86) + agent + overrides -> the GOG default dir
pwsh src\KotorCoop.Loader\build.ps1             # the chain loader (only needed for chain mode)
pwsh tools\package.ps1                          # everything, zipped: dist\kotor-coop-<version>.zip
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

## Console

Press `~` in-game for the console. `connect`/`disconnect`/`reconnect` manage the session without a restart;
Tab-completes (including the game's own verbs), Up/Down recalls history. A handful of verbs (`god`, `topup`, `tp`,
`dropitem`, `applyeffect`, and more) stage a test without grinding to it. Full reference: [docs/CONSOLE.md](docs/CONSOLE.md).

## Configuration

Everything lives in one `coop.ini` beside the game, parsed by two independent halves — the external agent and the
in-process plugin each read their own keys from it. Full table of every key, its shipped default and what it does:
[docs/CONFIG.md](docs/CONFIG.md).

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

**Full player-facing status list:** [docs/limitations.md](docs/limitations.md) (2026-09-02) —
what works, what's partial, and what's not supported, ordered the way you'd actually hit it
(joining/rejoining, travel, combat/downed/revive, dialogue, party management, stores/minigames,
3+ peers, input), plus a list of things that look like bugs on the test rig but aren't real
limitations. The bullets below remain the short-form summary; the linked doc is the one to check
before planning a start-to-end run — biggest current blockers are the galaxy-map/escape-pod party
picks needing a human, and a few input-lock and attribution features that are built but never
watched live (the vendor-purchase gold ledger was verified 2026-09-03 — it found and fixed a
stale store-panel gold cache that could wrap the purse to a fortune).

- **Only the host can switch party character**, mirrored to everyone else; control returns to your own character automatically on connect (`control_return=0` opts out). A mirrored companion's follow-AI never runs at all while mirroring holds, by design (the reclaim that keeps local AI from contesting a driven position also blocks the engine's own follow dispatcher). Where follow-AI DOES run, its long-standing "companion stands still and never catches up" stall is fixed and on by default (`party_follow_stall_recovery`, 2026-08-29): a runtime detector spots the exact wedged follow record and gathers the party through the engine's own routine, the same one a module transition uses — five reps, every wedge recovered on the first attempt. It relocates the companion rather than walking it back; the long-distance pathfinding limit underneath is real and still unfixed, so a companion left far behind is gathered, not walked. `disconnect` then `reconnect` reliably re-mirrors a companion. A mirrored companion can't fight for a client except through `companion_local_combat` (on by default); a companion it releases now forwards the damage it actually takes back to the host, which spends it as real damage on its own authoritative body (`companion_damage_sync`, on by default, NEEDS-LIVE) — before this, a released companion's damage went nowhere and the host's own mirror write overwrote it on the very next pass, making it effectively invulnerable. See [control-return.md](docs/research/control-return.md), [party-reconciliation.md](docs/research/party-reconciliation.md) and [companion-state-sync.md](docs/research/companion-state-sync.md) §13.
- **One conversation at a time**, by design — the other player spectates rather than a separate copy; any peer may start one (initiator-led replies), and (2026-08-29) a spectator's SKIP is swallowed the same way clicks already were (`DialogueSkipHook`). See [dialogue-sync.md](docs/research/dialogue-sync.md).
- **Losing the host doesn't lock you out, live-verified.** A client whose host crashes, quits, or drops off the network releases every co-op lock within `host_loss_timeout_ms` (default 5000ms — measured live at 6.1s) and keeps playing solo — companions, doors and dialogue all keep working — while re-Helloing automatically in the background; the host coming back re-mirrors everything with no player action needed (`host_loss_release`, on by default, `PacketType.HostBye` fast path). See [disconnect-reconnect.md §11](docs/research/disconnect-reconnect.md).
- **Companion experience and equipment sync are both live-verified and on by default** (`companion_xp_sync`, `companion_gear_sync` — see the status table); **dialogue recruitment sync is live-confirmed for two companions** (`dialogue_recruit_sync`, on by default), though a fix for a stale pre-recruit body isn't yet confirmed live. See [companion-state-sync.md](docs/research/companion-state-sync.md), [dialogue-consequences.md](docs/research/dialogue-consequences.md).
- **Mod-compatibility mismatch handling is live-verified for most categories**, but module-GIT/globals-catalogue mismatches are unforced in testing and a same-resref, differently-modded item isn't detected. See [mod-compatibility.md](docs/research/mod-compatibility.md).
- **Swoop racing works, but only one player at a time.** The "the race never starts" report turned out to be a wrong test recipe, not the mod: the race branch of Gadon's dialogue needs the `Tar_SummonGadon` global and three real reply picks, and with those it runs end to end. A KOTOR track is a single-seat area, so `minigame_lock_peers` (on by default, `minigame_lock_timeout_s=600`) holds the other players still with a "&lt;name&gt; is racing - waiting" notice and refuses to warp them onto the track; rewards (gold, journal, the `Tar_Swoop*` globals) reach everyone through the ordinary channels. The keyboard half of that peer lock stays NEEDS-LIVE (injected keystrokes never reach this game); the `delay_probe` diagnostic ships OFF. **Creature liveness reconciliation** (a body alive on one screen, dead on the other) ships off by default, correction mechanism works, a freshness theory is unconfirmed. See [minigames.md](docs/research/minigames.md) §24 and [creature-liveness.md](docs/research/creature-liveness.md).
- **A peer could render as the wrong character (a stranger, or Trask) after a warp — both root causes closed, 2026-08-29.** The phantom's client twin is now re-dressed after re-spawn (`PeerIdentityRule`), and a host companion no longer leaks into the enemy census during the ~5s its follow pointer is null after a transition (`PartyCensusRule`, roster-based) — stand-ins built for a genuine enemy are dressed too. See [name-plates.md](docs/research/name-plates.md) and [TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md).
- **Chain mode serialises `dinput8` proxies, it doesn't merge them** — a proxy that always loads the system DLL itself must be last in `dinput8.chain`; "load me" DLLs (`.asi`) go anywhere.
- **Each player really fights up to 3 enemies at a time (`local_combat_slots`, default 3, NEEDS-LIVE).** `local_combat` hands a client up to `local_combat_slots` hostiles to its own engine at once (1 reproduces the original single-fight behaviour); every other engaged enemy beyond that count stays mirrored (damage still lands), and the damage wire names only the last attacker. Built and unit-tested 2026-09-02; live confirmation of N simultaneous fights is still open. See [combat-participation.md §11](docs/research/combat-participation.md).
- **The party's shared item pool and merchant store stock are synced, at mode 2** (`party_pool_sync`/`store_sync`, both ON by default since 2026-08-29 — see the status table). Two live-verified limitations remain: a shop's stock can only be READ while some player has that shop's GUI open (an engine limitation, unchanged from mode 1), and an infinite-stock item's buys never replicate because the engine's own stock never changes for one. Mode 1's bidirectional op-log stays disqualified and is reachable only by writing `=1` explicitly. See [party-pool-sync.md](docs/research/party-pool-sync.md), [store-sync.md](docs/research/store-sync.md) and [shared-inventory-v2.md](docs/research/shared-inventory-v2.md).
- **The Star Forge's Bastila and Malak duels spectate, ON by default and live-verified** (`solo_segment_spectate`), except the input lock is only confirmed to engage its own mechanism, never confirmed to stop a real PHYSICAL WASD press (injected keystrokes never reach this game — same limit as the swoop peer lock above).
- **Chaining the console `warp` verb rapidly can crash the game** — a pre-existing, unfixed `swkotor.exe` defect, not co-op-specific and not gated by any `coop.ini` setting. A 12-transition real-doors-only soak found zero crashes on the same build a `warp`-driven soak crashed by transition 2-3 on. See [transition-crashes.md](docs/research/transition-crashes.md) and [docs/CONSOLE.md](docs/CONSOLE.md)'s `warp` row.
- **`coop.ini` divergence is negotiated, not silent.** Every behaviour flag rides a digest compared at join; a difference always warns on both screens (`W_CFG`), and nine host-authoritative keys are additionally adopted by a disagreeing client at runtime. See [mod-compatibility.md](docs/research/mod-compatibility.md)'s "Behaviour-flag negotiation".
- **The other player's movement should look continuous, not bursty.** Until 2026-08-29 it did not: the agent's item cache was forcing a full ~180 MiB scan of the game's memory once a second on the same thread that writes remote players' positions, so the pump ran at 12.8 Hz instead of 30 with individual ticks up to 2.4 seconds. Every world scan now runs on a background thread and serves its previous result while refreshing; measured on the rig, the worst tick went from 2,409 ms to 8.4 ms and the pump holds 30 Hz. If a peer still moves in bursts, that is a bug worth reporting — turn on `perf_trace` and send the agent log. See [lag-audit.md](docs/research/lag-audit.md).
- **The Endar Spire's command deck floors damage at 1 hp for everyone** — KOTOR's own tutorial script, not this mod ([min-one-hitpoint.md](docs/research/min-one-hitpoint.md)).
- **No gamepad/controller support.** The server browser, password entry and `~` console take typed/clicked input only — a co-op-added limitation, not a change to the base game's input.
- **Internet play works without a relay for most home routers, not all.** A host's public address is found automatically (STUN) and the port forwarded via UPnP (manual otherwise, named in `status`/`public_address`); no relay/hole-punching, so a restrictive ("symmetric") NAT with UPnP off can't be reached. Built and unit-tested, not yet watched against a real router. See [network-topology.md](docs/research/network-topology.md).

## Documentation map

- [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md) — the design and why it looks like this.
- [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) — build, test, the live rig, CI/release pipeline, repo layout, code conventions, and how to add a feature end-to-end.
- [docs/CONFIG.md](docs/CONFIG.md) — every `coop.ini` key and CLI flag, with defaults.
- [docs/CONSOLE.md](docs/CONSOLE.md) — every in-game console verb.
- [docs/roadmap-seamless.md](docs/roadmap-seamless.md) — what's left before "seamless," tiered by priority.
- [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) — common problems and their causes.
- [docs/live-verification.md](docs/live-verification.md) — the two-instance checklist that turns "built" into "live-verified" here.
- [docs/FINDING-OFFSETS.md](docs/FINDING-OFFSETS.md) and [docs/re/](docs/re/) — how engine addresses were found, and the core RE independent of any one feature.
- [docs/research/README.md](docs/research/README.md) — the full per-feature research index.
- [tools/README.md](tools/README.md) — what every helper script in `tools/` does.
- [tools/package-README.md](tools/package-README.md) — the README that ships inside the zip.

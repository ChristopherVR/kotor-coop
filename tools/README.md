# tools/

Helper scripts for driving, probing and reverse-engineering the game, plus the symbol
database (`kotor1_0_3.db`) most of the Python tools query. PowerShell scripts talk to a
running `swkotor.exe`; most Python scripts are offline analysis against the binary and
game files. `laya_playtest.py` is a Windows live-game controller.

`dotnet run --project tools/MemoryBench -c Release` runs the local memory-search and
ASCII-allocation microbenchmarks. It needs no game instance; see
[the performance review](../docs/performance-review-2026-09-15.md) for methodology and limits.

## Rig: bring up, verify, package

| Script | What it does |
| --- | --- |
| `demo.ps1` | Launches both KOTOR instances, loads saves, starts both agents connected — the standard two-player dev rig. `-Stop` tears it down. |
| `demo3.ps1` | The same, for three instances (one host, two joiners) — exercises peer-list and slot-table paths two players can't. |
| `verify.ps1` | Runs the co-op verification battery against a live `demo.ps1` session and prints PASS/FAIL/SKIP per feature. LAN beacon/browser checks are excluded. Use `-IncludeTransitionCheck -TransitionModule <different-module>` on a disposable session for real travel away and back; module reloads reset phantom damage counters. Repeated runs restore temporary health, position and purse edits, but cannot undo kills, journal changes or opened doors. Load a fresh baseline for complete coverage; skips are listed separately and are not passes. |
| `verify3.ps1` | The three-player battery, against `demo3.ps1` — checks only two players could not fail. |
| `laya_playtest.py` | Bounded local Laya MCP playtest with native observations, synthetic keyboard movement/replies, attack/dialogue/door requests, screenshots and a JSON action journal. Requires `laya[mcp]` and Codex's `laya-kotor-pilot` stdio registration. Run `python tools/laya_playtest.py --host <pid> --client <pid> --steps 60 --seconds 600 --output <new-evidence-directory>`. `--snapshot --host <pid>` only observes. Version 97 only; assumes the GOG 1.03 executable addresses used by the existing native probes. Laya abstentions use an explicitly logged deterministic fallback. Dialogue reply text is unavailable; numbered replies are exploratory. Local `opendoor` is a development instrument and may bypass story locks, so this is assisted exploration, not proof of a normal campaign playthrough. No model-generated save, spawn, warp or teleport commands. Run offline guards with `python -m unittest discover -s tools -p test_laya_playtest.py`. |
| `coop_playtest.py` | Two-player playtest harness on the isolated rig. Both players act each step (dialogue replies with their real text, fights, containers, NPCs, doors via engine pathfinding, joining the partner's conversation, closing menus); `--policy laya` asks Laya through the same MCP registration, `--policy rules` uses fixed rules; `--policy claude` runs Claude Code headless (`claude -p --model sonnet`, no tools, Read only with `--claude-screenshots`) for the real decisions only (two or more non-routine options, no plain fight), fed the active journal quests' current text, area, nearby named characters, the last 12 decisions and its own last plan, cached per situation and capped by `--claude-budget N` (default 150, rules afterwards, recorded in `report.json` under `claude`); `gotalk_` options walk to a named character and talk. A referee compares the two games after every step (crash, area, connection, conversation join, living creatures, door and container open/locked state, enemy health, the host's companions on the joiner, each side's stand-in for the other player's health, journal entries, story globals, player experience, new exception lines in either plugin log; inventory and credits are personal by default and never compared) and writes `findings.md`, `report.json` and both screenshots per finding. Builds on `laya_playtest.Game` and its command allowlist (no warps, spawns, teleports or save edits). `python tools/coop_playtest.py --host <pid> --client <pid> --policy laya --minutes 20 --output <new-evidence-directory>`, with the Laya venv's Python for `--policy laya`. Offline tests: `python -m unittest discover -s tools -p "test_*playtest.py"`. |
| `kotor_lore.py` | Read-only story text for the playtest policy: `journal_text(tag, state)` and `quest_name(tag)` from `global.jrl` + `dialog.tlk`, `area_name(module)`, `creature_names(module)`. Tests: `test_kotor_lore.py` (skipped without the GOG install). |
| `verify-package.ps1` | Runs the extracted Windows package's self-contained installer through plain/chained install, check, upgrade, broken-chain rejection, removal and support bundling/redaction/version checks, using new disposable fixtures with paths containing spaces. Preserves settings and custom loader entries; never launches a game. CI runs it before uploading packages. Pass `-PackageDirectory <extracted zip>` and optionally a new `-WorkDirectory <evidence folder>`. |
| `install.ps1` | Thin PowerShell wrapper over `KotorCoop.Agent.exe install`/`uninstall` (`src/KotorCoop.Agent/PackageInstaller.cs`), shipped in the zip for command-line installs; `-Chain`/`-Load`/`-Force`/`-Uninstall` map to the agent's flags. Players don't need it: the zip is a game-folder image and double-clicking the agent runs the setup. Also: `-UpdateDefaults` (delegates to the agent migration: preview older player defaults, preserve settings unless opted in, and back up before applying) and `-EnableCrashDumps`/`-DisableCrashDumps` (turns Windows Error Reporting local minidumps for `swkotor.exe` on/off; needs an elevated shell). |
| `CoopIniDefaults.ps1` | Legacy research helper, no longer shipped or used by install.ps1: the pure parsing/decision logic behind `-UpdateDefaults` — `ConvertFrom-CoopIni` (parse a `coop.ini`-shaped file), `Get-CoopIniDrift` (which keys still hold an old shipped default, and which shipped keys are entirely absent), `Update-CoopIniDefaults` (rewrite just the drifted lines in place). No side effects when dot-sourced, so it can be exercised on its own — see "Manual verification" below. |
| `package.ps1` | Builds the two release packages as one game-directory image (plugin as `dinput8.dll`, agent minus its pdbs, `coop.ini`, `Override\k_coop_*`, `chain-loader\dinput8.dll`, README): `kotor-coop-<version>-windows.zip` with `install.ps1`, KotorCoop.CollectLogs.cmd and KotorCoop.DEFAULTS.ini, and `kotor-coop-<version>-linux.tar.gz` with `install.sh` (executable, LF; written through `System.Formats.Tar` because Windows' `tar.exe` records no exec bit). Also produces `kotor-coop-<version>-symbols.zip` (the agent's pdbs, CI-artifact-only, never attached to a release) so a maintainer can still symbolicate a crash report. `-WarnAsError` is what CI runs. |
| `install.sh` | The Linux / Steam Deck installer, shipped in the tarball: a bash port of `PackageInstaller.cs` that runs natively (no Wine). Finds the game in the Steam libraries (`libraryfolders.vdf`, Flatpak), `~/Games/Heroic`, `~/Games`; same plain / chain / upgrade decisions, marker checks (`grep -a` on the UTF-16LE plugin marker), `--check` PASS/FAIL lines and `--uninstall` as the C# installer; ends by printing the `WINEDLLOVERRIDES` launch option. CI's `linux-install` job runs it on Ubuntu against a stub game folder on every push. Keep it in step with `PackageInstaller.cs`. |
| `chungus/` | Builds the arena boss's game files from nothing but code: `artifacts/chungus-venv/Scripts/python tools/chungus/build.py` writes his sculpted, skinned model (`k_coop_chungus.mdl/.mdx`), two textures and two sounds into `profiles/override` (`--preview DIR` also renders pictures of the model and every animation, so a change can be looked at without the game). `kmdl.py` is a writer for the K1 binary model format and `mdldump.py <file.mdl> [--nodes] [--anims] [--cover]` dumps any model's structure. Needs its own environment (`python -m venv artifacts/chungus-venv`, then numpy, scipy, Pillow, scikit-image, fast-simplification and xatlas). `voice.ps1` makes the raw words of his one spoken line with a Windows text-to-speech voice (run once; the result is checked in). See docs/research/chungus-boss.md. |
| `installer/kotor-coop-setup.iss` | Inno Setup 6 script for `kotor-coop-<version>-setup.exe`, which `package.ps1` builds beside the zip when `ISCC.exe` is installed (`winget install JRSoftware.InnoSetup`; skipped with a warning otherwise). A front end only: it unpacks the staged package to a temp folder and runs `KotorCoop.Agent.exe install --game-path <chosen folder>` with the options page as `--windowed`, `--no-firewall` and `--set key=value` (name, port, `agent_window`, `menu_button`, `steam_invites`), so `PackageInstaller.cs` stays the one installer. The options page prefills from the folder's existing `coop.ini`/`swkotor.ini`. Uninstall runs `KotorCoop.Agent.exe uninstall` from a temp copy and keeps `coop.ini`. `/CURRENTUSER` skips the UAC prompt; `/VERYSILENT /DIR=<game>` installs unattended with the current settings. Not yet part of the public release assets. |
| `package-README.txt` | Not a script: the plain-text README staged into the release zip as `README.txt`, with `{{VERSION}}` filled in by `package.ps1`. |
| `generate-icon.ps1` | One-shot: draws `src/KotorCoop.Agent/Assets/icon.ico` (a placeholder badge) from scratch with `System.Drawing.Common`. Re-run only if that placeholder needs regenerating; the `.ico` itself is checked in and `KotorCoop.Agent.csproj`'s `ApplicationIcon` picks it up on every build. |
| `session.ps1` | An earlier two-instance launcher with the same shape as `demo.ps1`. `demo3.ps1` still calls it "the recipe that survives repetition" for its own instance-1 launch. `verify.ps1` uses the `demo.ps1` drop-file and baseline conventions. |
| `devenv.ps1` | Imports the MSVC developer environment (vswhere-located, any VS edition) into the current shell, so `cl.exe`/`link.exe`/`vswhere.exe` resolve. Dot-sourced by both native build scripts; `. tools\devenv.ps1` standalone. |

## Live probes

`menu-layout-probe.ps1 -ProcId <pid> -ExpectedWidth 1920 -ExpectedHeight 1080 -OutPath <json>`
records the physical client size (with engine-size fallback), engine viewport, button geometry and loaded DLL names without
writing game memory. It fails if the requested mode fell back or Co-op is hidden, offscreen
or overlapping a button. Rendering and mouse/controller activation still need separate checks;
see [menu compatibility](../docs/research/menu-widescreen-compatibility.md).

`Test-LoaderEnhancedBridge.ps1` builds x86 fixtures and checks that the loader forwards
Enhanced's optional input counters to a secondary DLL while keeping DirectInput on the
first proxy. It also checks the no-provider case. Build `src/KotorCoop.Loader/build.ps1`
first; no game files or third-party mod assets are needed for this test.

`Test-LoaderEnhancedStartup.ps1` builds x86 fixtures for the early display-hook bootstrap:
recognized game with renderer present or loaded later, renderer absent, and unknown game.
It checks one-time priming, unchanged command-line delivery, and renderer import ownership.
It also requires the built loader and uses no game/mod assets.

For an owned private-desktop rig with `isolate_real_input=1`,
`blockpoke.ps1 -ProcId <pid> rig 'input click 965 504'` and
`blockpoke.ps1 -ProcId <pid> rig 'screenshot menu front'` send agent-free diagnostics through
the native command channel. Input uses addressed messages and the virtual DirectInput queue;
screenshots read the OpenGL framebuffer. Neither activates a window or moves the OS cursor.
Arguments are limited to 31 ASCII bytes; a pending native command is refused. Enhanced's
isolated hit-test bridge supplies the virtual cursor only inside its native menu hit tests;
normal play with `isolate_real_input=0` does not install that bridge. Use
`Start-CoopBackgroundRig.ps1` for disposable installs on a private, non-input desktop and
its recorded `-StatePath ... -Stop` to clean up the owned process.

Read (and sometimes write) a running game's or plugin's state directly, for verifying a
channel without playing through a whole scenario.

| Script | What it does |
| --- | --- |
| `blockpoke.ps1` | Reads and writes named fields of the plugin's shared block directly — the primitive every other write-based probe below is built on. |
| `pluginstate.ps1` | Reads the plugin's shared block: frame counter (proves the frame hook is alive), flags, version. |
| `partystate.ps1` | Reads the party table: solo-mode flag, active NPC slots, each companion's object id and position. |
| `pausestate.ps1` | Reads the server's pause bitmask (a byte of reasons, not a bool). |
| `sethp.ps1` | Reads or writes a creature's current hit points by object id, for testing enemy-health mirroring without a real fight. |
| `creatures.ps1` | Enumerates live `CSWSCreature` objects using the public Ghidra address database. |
| `textqueue.ps1` | Dumps the engine's own on-screen text queue (column, row, text — `Global::AurPostString`'s list) directly from process memory: proves what every overlay controller actually asked the game to draw, without needing a working screenshot (`shot.ps1` can show a stale or, on some machines, a black frame — see docs/research/console-overlay.md's 2026-09-04 addendum). |
| `teleport.ps1` | Writes a player's world position directly (`CSWSObject.position`) — test-only, since nothing else contests an idle player's position. |
| `crashlog.ps1` | Shows how the game last died, from the plugin log (access violations, managed exceptions), the .NET NativeAOT fail-fast path the plugin log can't cover, and points at `kotorcoop-crash.bin` (below) when a game directory has one. |
| `crashbin.ps1` | Decodes `kotorcoop-crash.bin`: the last first-chance fault written straight to disk by `CrashProbe`'s vectored stub, so it survives a process TERMINATED before the plugin's worker thread ever turns the same record into a log line — see `CrashProbe.cs`'s top-of-file doc comment (2026-09-02). |
| `inputalive.ps1` | Answers "will menu automation work on this instance?" before anything tries to click — KOTOR's menu input can silently die. |
| `barkprobe.ps1` | Fires a bark end to end on a live two-instance session and reports whether it arrived. |
| `dlgprobe.ps1` | Measures a shared conversation end to end on a live session. |
| `dlgjoinlatency.ps1` | Measures joiner-side dialogue join-latency timing `dlgprobe.ps1` doesn't print. |
| `phantomgait.ps1` | Polls a driven body's actual engine animation state every 250ms, to catch "moves but never animates" as data instead of a screenshot. |
| `transition-announce-measure.ps1` | Measures the announced-transitions live test (before/after the `transition_announce` gate) from both instances' own plugin logs. |
| `control-return-live.ps1` | Live-verifies the control-return regression fix: a client controlling a companion at connect time ends up back on its own PC. |
| `live-liveness.ps1` | Drives the staged creature-liveness scenarios against a running `demo.ps1` pair and reports PASS/FAIL per scenario. |
| `liveness-freshness-probe.ps1` | Paired census/resolve sampling for the `DestroyObject`-scan-freshness question in creature-liveness.md. |

## Input/capture primitives

Low-level OS-level window, input and screenshot building blocks other scripts and live
tests compose.

| Script | What it does |
| --- | --- |
| `movewin.ps1` | Moves a process's main window — used to place two instances side by side for one screenshot. |
| `suspend.ps1` | Suspends or resumes a process, so a minimised second instance stops competing for DirectInput. |
| `click.ps1` | Clicks (or, with `-HoverOnly`, just hovers) a window at a pixel coordinate. *(no header comment — undocumented one-off.)* |
| `sendkey.ps1` | Sends keystrokes to a window. *(no header comment — undocumented one-off; behaviour inferred from its parameters: `-Keys`, `-HoldMs`, `-GapMs`.)* |
| `wheel.ps1` | Scrolls the mouse wheel over a window (KOTOR maps it to camera zoom). |
| `shot.ps1` | Captures a screenshot of a window or the full screen. *(no header comment — undocumented one-off.)* |
| `siteclip.ps1` | Records every open `swkotor` window's client area as one side-by-side GIF for the player site (`site/media/`): ffmpeg `gdigrab` of the desktop region, per-window crops stacked with `hstack`, two-pass palette. Takes a `-Action` script block that runs once recording has started (drive it with `blockpoke.ps1 console ...`). The monitors must be awake or every game frame captures black; a player only walks while its own window has focus. See `site/media/README.md`. |
| `crop.ps1` | Crops and scales an image file. *(no header comment — undocumented one-off.)* |
| `tooltip.ps1` | Detects KOTOR's hover tooltip (a bright cyan underline) in a screenshot. |

## Feature test / raw-memory diagnostics

| Script | What it does |
| --- | --- |
| `checkcands.ps1` | Checks a list of candidate addresses against a position-delta offset. *(no header comment — undocumented one-off; behaviour inferred from its parameters: `-Candidates`, `-PositionDelta`.)* |
| `vmtable.ps1` | Reads a vtable out of a running process by raw `ReadProcessMemory`. *(no header comment — undocumented one-off.)* |
| `walktest.ps1` | Exercises a movement/walk address against a running instance. *(no header comment — undocumented one-off.)* |
| `writetest.ps1` | Writes a test delta to a given address and holds it for a few seconds. *(no header comment — undocumented one-off.)* |
| `exports.ps1` | Reads a PE file's export table directly from the bytes on disk. *(no header comment — undocumented one-off.)* |

The task brief that asked for this index named six header-less scripts; the actual count in
this directory is ten (`checkcands.ps1`, `click.ps1`, `crop.ps1`, `exports.ps1`, `q.py`,
`sendkey.ps1`, `shot.ps1`, `vmtable.ps1`, `walktest.ps1`, `writetest.ps1`) — all now covered
with an inferred one-line description in place of a missing header.

## Offline Python analysis

Static analysis against `swkotor.exe` and the symbol database — GOG build only; none of these
touch a running game.

| Script | What it does |
| --- | --- |
| `callaudit.py` | Audits every engine function the plugin calls against the binary and the symbol DB; `--markdown` regenerates engine-api.md's table. |
| `dbaudit.py` | Validates the whole symbol database against the binary, function by function. |
| `disasm.py` | Disassembles a range of `swkotor.exe` by virtual address. |
| `xrefs.py` | Finds call/jmp sites targeting an address. |
| `fieldwrites.py` | Finds instructions that write a given struct field offset — "who assigns this field" versus `xrefs.py`'s "who calls this". |
| `checkstub.py` | Re-emits and disassembles `CrashProbe.BuildHandler`'s hand-assembled exception stub — now including the `SetFilePointer`/`WriteFile` calls that hand the record to `kotorcoop-crash.bin` — byte for byte, as an offline correctness check. |
| `globalvars.py` | Reader for KOTOR 1 save-game GFF files, specialised for `GLOBALVARS.res`. |
| `kotorfiles.py` | Pure-stdlib readers for the Odyssey engine's data formats (KEY/BIF/GFF/2DA/TLK and friends). |
| `saveinspect.py` | Counts what a save baked into its module — `SAVEGAME.sav` → nested module image → `.git` — and flags the co-op ghosts among them (`coop_phantom` creatures, `coop_mapnote`/untagged-enabled map pins, `Mod_NextObjId0`). The offline oracle for the save-time flush (docs/research/save-ghosts.md): a clean Endar Spire deck save reads 25 / 58; `--all <saves dir>` sweeps every save, `--expect-clean` exits 1 on any ghost. |
| `patch_mainmenu.py` | Builds the `Override\mainmenu*.gui` that turns the dormant `BTN_WARP` control into the visible "Coop" button. |
| `q.py` | Runs an ad-hoc SQL query against `kotor1_0_3.db`. *(no header comment — undocumented one-off.)* |

## Manual verification: `CoopIniDefaults.ps1`

The current installer uses the agent's SettingsMigration with xUnit and packaged installer
coverage. The legacy CoopIniDefaults.ps1 helper can still be inspected manually. It has no side effects
when dot-sourced — dot-source it and call the functions directly against a scratch file:

```powershell
. tools\CoopIniDefaults.ps1

# A synthetic pre-2026-08-27 coop.ini: menu_button still holds the OLD default.
@"
[coop]
mode = join
menu_button = 0
"@ | Set-Content 'scratch-coop.ini'

$player   = ConvertFrom-CoopIni -Path 'scratch-coop.ini'
$shipped  = ConvertFrom-CoopIni -Path 'profiles\coop.ini.example'
$drift    = Get-CoopIniDrift -PlayerIni $player -ReferenceIni $shipped

$drift.Drifted.Key       # -> menu_button (still 0; effect_sync/trigger_relay are simply
                          #    absent from the synthetic file, so they show up in MissingKeys
                          #    instead of Drifted — only an explicit OLD value counts as drift)
$drift.MissingKeys.Count # -> every other key the shipped file sets that the synthetic file doesn't

Update-CoopIniDefaults -Path 'scratch-coop.ini' -Drifted $drift.Drifted
Get-Content 'scratch-coop.ini'   # menu_button = 1, every other line byte-identical, same order

Remove-Item 'scratch-coop.ini'
```

Re-run this after editing `$script:CoopIniDriftedDefaults` (adding a newly-changed key) or the
parsing logic — both are plain functions with no game, process or registry dependency, so the
check above is quick and fully offline. `install.ps1` itself calls these same functions after a
real install; verifying them here is equivalent to verifying that code path without needing a
game directory or an old `coop.ini` on disk.

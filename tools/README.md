# tools/

Helper scripts for driving, probing and reverse-engineering the game, plus the symbol
database (`kotor1_0_3.db`) most of the Python tools query. PowerShell scripts talk to a
running `swkotor.exe`; the Python scripts are offline, static analysis against the binary and
game files — no running game needed.

## Rig: bring up, verify, package

| Script | What it does |
| --- | --- |
| `demo.ps1` | Launches both KOTOR instances, loads saves, starts both agents connected — the standard two-player dev rig. `-Stop` tears it down. |
| `demo3.ps1` | The same, for three instances (one host, two joiners) — exercises peer-list and slot-table paths two players can't. |
| `verify.ps1` | Runs the full co-op verification battery against a live `demo.ps1` session and prints PASS/FAIL per feature. |
| `verify3.ps1` | The three-player battery, against `demo3.ps1` — checks only two players could not fail. |
| `install.ps1` | Installs a packaged release (plugin, agent, override assets, `coop.ini` template) into a game directory — what a player runs, and what `build.ps1 -Install` runs too. Refuses to overwrite a foreign `dinput8.dll`; `-Chain` (optionally `-Load a.dll,b.asi`) installs the chain loader as `dinput8.dll` beside the plugin and the other mod instead. `-Uninstall` removes only what it placed, recognising its own DLLs by marker. |
| `package.ps1` | Builds the release zip `install.ps1` unpacks: the NativeAOT plugin, the C chain loader, the agent, the `coop.ini` template, the override assets, the installer and a version-stamped `README.md` (`package-README.md`). `-WarnAsError` is what CI runs. |
| `package-README.md` | Not a script: the README staged into the release zip, with `{{VERSION}}` filled in by `package.ps1`. |
| `session.ps1` | An earlier two-instance launcher with the same shape as `demo.ps1`. `demo3.ps1` still calls it "the recipe that survives repetition" for its own instance-1 launch. **Note:** `verify.ps1`'s own header comment says it assumes `session.ps1` brought the rig up, but the rest of that file consistently references `demo.ps1` (its `--drop-file` convention, its starting save/footlocker) — that one header line looks stale. |
| `devenv.ps1` | Imports the MSVC developer environment (vswhere-located, any VS edition) into the current shell, so `cl.exe`/`link.exe`/`vswhere.exe` resolve. Dot-sourced by both native build scripts; `. tools\devenv.ps1` standalone. |

## Live probes

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
| `teleport.ps1` | Writes a player's world position directly (`CSWSObject.position`) — test-only, since nothing else contests an idle player's position. |
| `crashlog.ps1` | Shows how the game last died, from the plugin log (access violations, managed exceptions) and the .NET NativeAOT fail-fast path the plugin log can't cover. |
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
| `checkstub.py` | Re-emits and disassembles `CrashProbe.BuildHandler`'s hand-assembled exception stub, byte for byte, as an offline correctness check. |
| `globalvars.py` | Reader for KOTOR 1 save-game GFF files, specialised for `GLOBALVARS.res`. |
| `kotorfiles.py` | Pure-stdlib readers for the Odyssey engine's data formats (KEY/BIF/GFF/2DA/TLK and friends). |
| `patch_mainmenu.py` | Builds the `Override\mainmenu*.gui` that turns the dormant `BTN_WARP` control into the visible "Coop" button. |
| `q.py` | Runs an ad-hoc SQL query against `kotor1_0_3.db`. *(no header comment — undocumented one-off.)* |

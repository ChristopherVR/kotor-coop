# KMRP compatibility

## Issue #1 — startup and ASI loader coexistence

**Report:** [issue #1](https://github.com/ChristopherVR/kotor-coop/issues/1) launches
with KMRP's Modern Driver Compatibility and High FPS Fix both disabled, but crashes
with the original options. The reporter has not isolated either option, supplied a
support bundle, or verified multiplayer. The ASI error wording/version is unknown.

### Demonstrated loader defect

Co-op previously renamed every foreign DirectInput proxy to `dinput8.prev.dll`.
Ultimate ASI Loader chooses its original-library forwarding implementation from its
own filename. In [9.7.4's `LoadOriginalLibrary`](https://github.com/ThirteenAG/Ultimate-ASI-Loader/blob/v9.7.4/source/dllmain.cpp),
`dinput8.dll` is supported, but `dinput8.prev.dll` reaches **“This library isn't
supported.” → `ExitProcess(0)`**. This happens without an access violation, so a
co-op crash record is not guaranteed. Its exported `IsUltimateASILoader` marker is
also a more reliable identifier than an ASCII product name (the tested release has
the export but not the old ASCII name marker).

KMRP's [driver component record](https://github.com/RayesDiyab/KMRP-KOTOR-Modern-Restoration-Patch/blob/4b34210f4674e175bee21a5dfa77653e359da003/docs/third-party-driver-compat.md)
documents standalone Ultimate ASI Loader installations, including older Windows
packages and the current Windows 7/8 route. Newer Windows installs can instead use
KPM's `patches/k1-modern-driver-compatibility.dll`; **not every KMRP install has this
loader conflict**. Without the reporter's files, it is a demonstrated failure
mechanism consistent with the report, not a confirmed diagnosis of their crash.

### Fix

Preserve Ultimate ASI Loader byte-for-byte as root `dinput8.dll`. Put co-op's native
chain loader in its documented `dinput8Hooked.dll` original-library slot:

```text
game → Ultimate ASI Loader (dinput8.dll)
     → co-op chain loader (dinput8Hooked.dll)
     → KotorCoop.Plugin.dll → next proxy / system DirectInput
```

Do not list the ASI host in `dinput8.chain`: that would recurse. Keeping the host in
the game root also preserves its root/scripts/plugins ASI search and INI paths;
moving it into a subdirectory under the original basename would not. No third-party
binary is patched or bundled. Reinstall migrates old co-op chains holding a detected
ASI loader at `dinput8.prev.dll`, preserves other chain entries, and refuses a foreign
`dinput8Hooked.dll` before modifying files. Uninstall removes only our marked wrapper
and leaves the ASI host/payloads alone. Windows and Linux installers use this layout.

Support reports now include the chain, `patch_config.toml`, known KMRP settings and
its old/new `kmrp-kpm.log` locations with the existing text redaction. The system
inventory names the ASI host/hooked slot, KPM runtime and optional component modules;
no executable, ASI payload or game asset is attached.

### High FPS Fix and rendering hooks

Inspected KMRP commit `4b34210f4674e175bee21a5dfa77653e359da003`'s bundled
`HighFpsFixes-1.0.1 by D3M0/HighFpsFixes.kpatch`, all **36** KOTOR 1 manifest sites.
The downloaded archive SHA-256 was
`B02313D83129691BE383E312EA28A13FC97ED586A8DB627C177F28A911AA868D`.
No plugin source address literal falls in any declared patch span (or within 32
bytes of it). This is a source-site sanity check, **not** a full runtime write audit
or proof of gameplay compatibility; computed writes, timing and deferred Steam
patching remain untested. No High FPS engine edits were added speculatively.

K1DC and co-op both use the `SwapBuffers` import at `0x0073D00C`. Co-op's existing
`FrameHook` already chains executable foreign image targets and avoids replacing a
later renderer hook on uninstall; the loader fix does not bypass or remove K1DC's
rendering hook. A live rendering test is still needed.

### Validation

- Installer unit regressions cover fresh ASI hosting, migration, repeated upgrades,
  uninstall, export/wide-string detection and refusal to clobber the hooked slot.
  `dotnet build KotorCoop.slnx --no-restore -warnaserror` passed without warnings;
  all 147 focused installer, support-report, KMRP identity/admission and loopback
  tests passed after the final C# changes.
- `tools/Test-LoaderAsiHost.ps1` builds native x86 fixture DLLs and executes the real
  co-op C loader: input reaches the primary proxy and a separate root `.asi` loads.
  It passed both with a basename-sensitive stand-in and the actual upstream 9.7.4
  `Ultimate-ASI-Loader.zip` binary (SHA-256
  `D5A059AA467A7A7127C8F6169F79FA63FF0F55986EE9EB2FD9A281BEBF2AA2E6`).
  Optional reproduction: pass `-AsiLoader <path-to-dinput8.dll>`; nothing is downloaded
  by the test. The default stand-in runs in CI without third-party assets.
- `bash tools/test-asi-host-install.sh` passed under Ubuntu/WSL, testing fresh install,
  old-chain repair, upgrade, uninstall and collision refusal. This is an installer
  test, not Proton gameplay.

### 2026-11-03 — live startup reproduction and fixed-layout retests

**The reported failure pattern is reproduced locally**, not just in fixture DLLs.
The reporter's exact environment remains unknown: no support bundle or exact
version/settings list was attached to issue #1 when rechecked.

Tested on Windows 11 Pro N `10.0.26300`, NVIDIA GeForce RTX 5080, with the original
Steam executable (`34E6D971...F34C88`) and Steam running. Games ran sequentially on
private, non-input desktops in disposable `artifacts/live-installs/kmrp-issue1-*-live-20261103`
copies. No personal installation or save was modified. No K1 Restoration was present.
The plugin was rebuilt from this working tree; helper autostart was disabled to
isolate startup. No networking or multiplayer gameplay was attempted.

Used the **published Windows KMRP 1.5.1 binary**, not upstream master:

| Input | SHA-256 |
| --- | --- |
| `KMRP-Windows-1.5.1.exe` | `E9657FBCCFA2403456E655CBB961EDCA3FB667D1818A1B340B09C621BE3D7F1E` |
| Bundled Ultimate ASI Loader | `EC2F4824ECA58DD40F425756A4A7CEC77B8E381F21D11D7C846EC4B339B617AB` |
| Bundled K1DC **1.2.0** `.asi` | `A339FDE58D9787DB5CC723D43E4491653BB5BE0FB1254EA3A793E0C3F29E40B8` |
| Bundled High FPS Fix **1.0.1** runtime DLL | `6CC7691AA3A8353CC3B3759EB980E1B55703767E5303F538705C6CD8C9CF4DC4` |

KMRP's own `InstallEngine`, INI configuration and optional driver installation code
was invoked from that assembly with reflection. Its settings were supplied in memory,
without writing the user's global `KMRP/settings.json`; NVIDIA profile and DPI-registry
installer operations were deliberately skipped. Runtime binaries and patch config were
written by the release's own methods. This tests the actual runtime integration, **not
the full interactive installer**. Resolution was 1024x768, windowed; controller support,
HD icons, map-marker fixes and debug logs were enabled in every KMRP case.

| Configuration | Observed result |
| --- | --- |
| Vanilla | Game/renderer windows created; remained running |
| Co-op only | Main menu reached; Co-op button shown, labelled and bound |
| KMRP with both optional components, no co-op | Game/renderer windows created; remained running |
| Co-op + KMRP, both optional components off | Main menu reached; Co-op button bound |
| Co-op + KMRP, High FPS only | Main menu reached; Co-op button bound |
| Co-op + KMRP, driver only, **old renamed-loader layout** | **3/3** runs blocked by `ASI Loader` / `This library isn't supported.` |
| Co-op + KMRP, both on, **old layout** | **3/3** failed: one ASI error dialog and two access-violation exits |
| Driver only, **fixed ASI-host layout** | **3/3** reached the main menu with the Co-op button bound |
| Both on, **fixed layout** | **3/3** reached the main menu with the Co-op button bound |

Windows Application Error recorded the old-layout crash as **`0xC0000005`**, faulting
module **`k1-modern-driver-compatibility._unloaded`**, offset **`0x0006C8C0`**. It
happened before co-op's normal startup log was written. The unsupported-name dialog
was captured by reading windows belonging only to the owned test process on its
private desktop; it was not inferred from process liveness alone. We did not click
through it: the harness terminated that owned process after recording the failure.

Only the loader layout was changed for the six successful retests; the executable,
KMRP modules, driver payload and High FPS DLL were unchanged. Each successful driver
case logged K1DC's shader installation (`57/57` programs), and a fresh plugin startup
reached `menu: Co-op button shown, labelled and bound` at viewport 1024x768. Initial
fixed-layout runs were observed for 55 seconds each, subsequent runs for 40 seconds.
This is startup evidence, not a rendering-quality or campaign test. In particular,
the presence of High FPS in two failing runs does **not** establish a separate High
FPS bug; it also ran successfully without the driver and with the corrected loader.

An initial attempt omitted the copied Steam rig's `steam_appid.txt` and produced
Steam `Application load error 5:0000065434`, including on vanilla. Those runs are
**excluded** from the compatibility results above. Copying the existing rig's app-ID
file resolved that baseline problem without modifying or unwrapping the executable.

Local evidence is retained in `artifacts/kmrp-issue1-live/`: timestamped process/window
observations and state JSON, `*-before-fix` logs, isolated-install and matrix scripts,
and `both-fixed-support.zip` (15 redacted text entries, no game binaries). The real
fixed install passed `PackageInstaller.Check`. Discovering the actual driver-log
location also added `logs/K1DriverCompat.log` and `kmrp-controller-events.log` to the
support-report allowlist, with regressions excluding unrelated logs.

The broad CI-filtered suite passed **6,459 tests** before that final log-allowlist
addition; all **148 focused tests** passed after it. The NativeAOT x86 plugin rebuilt
with warnings as errors. All owned test games were stopped after observation.

**Still NEEDS-LIVE:** two-player join/load/travel/combat with matching KMRP options,
other GPUs/storefronts/OSes, fullscreen and other resolutions. Ask the reporter for
their support report to determine whether their exact fault matches this reproduction.
No issue comment or release was published by this investigation.

## 2026-10-30 — false executable mismatch with matching runtime patches

**Report:** two players could connect on clean installs over Hamachi, then received the
executable-version refusal after both installed KMRP 1.5.1 for Windows. No support bundle
or live image dumps accompanied the report.

**VERIFIED by source inspection:** co-op hashes the loaded executable's complete `.text`
before installing its own hooks. This solves the Steam encrypted-file mismatch but does
not exclude another mod's already-installed hooks. KMRP's pinned
[1.5.1 applier](https://github.com/RayesDiyab/KMRP-KOTOR-Modern-Restoration-Patch/blob/v1.5.1/src/controller-native/K1KpmApplier.cpp)
allocates its code with `VirtualAlloc(nullptr, ...)` and adds the resulting address delta
to operands in game-code patch runs. Its
[runtime documentation](https://github.com/RayesDiyab/KMRP-KOTOR-Modern-Restoration-Patch/blob/v1.5.1/src/kpm-runtime/README.md)
also describes deferred patching on Steam. Equal releases therefore need not produce
equal bytes at co-op's startup sampling point.

**INFERRED:** runtime allocation or patch timing explains the reported refusal. Without
the two players' logs, this is a demonstrated failure mechanism, not a confirmed diagnosis
of their particular machines. It is independent of Hamachi's packet routing.

### Change

The original live image digest and strict default remain. A separate optional SHA-256
identity covers a supported KMRP install's executable file, `patch_config.toml`,
`KotorPatcher.dll`, Bink proxy and original, root DLL/ASI files, root mod INIs, the complete
`patches` and `configs` trees, and any `dinput8.chain` plus its local DLL entries.
Names are relative and ordered; full file contents are hashed without a timestamp cache.
`swkotor.ini`, `coop.ini`, root logs and absolute installation paths are excluded. Screen
resolution can differ while patch inputs match. Patch-option INIs must match exactly.

The accepted config shape is the relative `patches/<id>.dll` layout emitted by
[KMRP 1.5.1's installer](https://github.com/RayesDiyab/KMRP-KOTOR-Modern-Restoration-Patch/blob/v1.5.1/src/patcher/KpmEdition.cs).
There must be a `kmrp` entry, all referenced modules must exist, and KMRP/controller
option files must exist when applicable. Missing/unreadable inputs, linked paths, external
module paths and unrecognized layouts supply no fallback. Inputs stay open without
write/delete sharing during hashing; the directory census and parsed config are checked
again before publishing. This is an install-time check; install or update mods only with
the game closed, then restart both games.

If both live images are known and differ, matching nonempty KMRP identities admit the
connection with `W_KMRP`. The other content compatibility rules still apply. Absent,
malformed or differing identities retain `E_EXE`, unless the host explicitly chooses the
existing `exe_mismatch = allow` setting. A bare mod name/version is never sufficient.
The error message now mentions runtime patches and settings as well as KOTOR 1.03.

Protocol 87 / content scheme 3 carries this separate field in Hello and Welcome. Both
players must update. Raw image hashes remain in diagnostics alongside the KMRP identity.
The file identity deliberately does not normalize different storefront executables: the
existing live-image route still handles ordinary Steam/GOG interoperability; this fallback
requires identical executable file bytes too.

### Verification and limits

**VERIFIED by automated tests:** same relative install contents in different folders match
despite different timestamps, resolutions and simulated runtime detour addresses; same-size
edits with restored timestamps change identity; missing/locked inputs and unsupported
paths disable the fallback; changed runtime modules still refuse. Hello/Welcome preserve
the identity and reject truncation. Real UDP loopback tests cover admission with matching
files and refusal with a changed patch, with `exe_mismatch` off on both peers. Appearance
mismatches still disable their channel.

Validation on this checkout: `dotnet build KotorCoop.slnx --no-restore -warnaserror`
passed with no warnings. The CI-filtered suite (`Category!=Soak&Category!=LocalOnly`)
passed 6,406 tests and exposed one new test-fixture error: an uninitialized behaviour
digest is normalized by the existing decoder. After using `BehaviourDigest.Default` in
that fixture and completing the input-audit tests, the affected suites rebuilt with
warnings as errors and passed all 197 tests. The entire broad suite was not rerun after
those final changes. `git diff --check` passed.

**NEEDS-LIVE:** install the built agent on two licensed game copies with matching KMRP
1.5.1 options, restart both games, join, create/load characters, move, fight, talk and cross
an area boundary. Repeat with different screen resolutions. This change addresses admission;
matching installed files do not prove that both runtimes applied successfully or that their
hooks coexist with co-op. KMRP gameplay, its menu layout and its controller support are not
certified by the synthetic or socket tests. No engine memory writes or plugin hooks changed.

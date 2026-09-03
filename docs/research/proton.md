# Linux, Steam Deck and Proton

Status: **implemented, NEEDS-LIVE end to end** (2026-09-03). There is no Linux machine on the
rig; everything below is engineered against Wine's documented behaviour and the mod's own
process model, and each claim is tagged. The first report from a real Deck or desktop is the
verification step.

## 1. What has to run where

The mod is two Windows programs, and under Proton both live inside the game's own prefix:

| Piece | What it is | Under Wine |
| --- | --- | --- |
| `dinput8.dll` | NativeAOT **win-x86** DLL, loaded by `swkotor.exe` through its static `dinput8` import; forwards every export to the real DirectInput and hooks the engine from inside | Loaded by Wine's PE loader exactly like on Windows **once the load order says "native first"** (§3). It then `LoadLibrary`s `%SystemRoot%\system32\dinput8.dll` for the real exports (`Exports.cs`) — under Wine that is Wine's built-in DirectInput, which is what we want: the game's input still comes from Wine's evdev/SDL backend, our wrapper sits on top of it |
| `KotorCoop.Agent.exe` | **win-x64** .NET process, started by the plugin (`AgentLauncher`, `CreateProcessW` + a kill-on-close job object) and talking to the game through `OpenProcess`/`ReadProcessMemory`/`WriteProcessMemory` on the shared block | A 64-bit Windows process started from a 32-bit one — fine in a WoW64 prefix, which every Proton prefix is. Cross-bitness process memory access is supported by Wine's wineserver |
| The network | UDP on `27140` (default), STUN, UPnP | Wine passes sockets straight through to Linux; STUN/UPnP are ordinary UDP |

Nothing needs to be installed *into* the prefix. That is the point of the one engineering
change this took (§2): before it the agent was a framework-dependent publish that needed a
Windows .NET 10 runtime, and getting one into a Proton prefix is a `winetricks`-grade chore
that fails more often than it works (the .NET installer wants a real Windows Installer service;
Proton's prefixes deliberately do not carry one).

## 2. Self-contained single-file agent

`src/KotorCoop.Agent/KotorCoop.Agent.csproj` now has a property group conditioned on
`_IsPublishing` (the property the `dotnet publish` CLI sets; the SDK's own `PublishRelease`
default keys off it):

```xml
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
<SelfContained>true</SelfContained>
<PublishSingleFile>true</PublishSingleFile>
<EnableCompressionInSingleFile>true</EnableCompressionInSingleFile>
<PublishTrimmed>false</PublishTrimmed>
```

Result of `dotnet publish -c Release`: **one `KotorCoop.Agent.exe` of ~38 MB** (runtime inside,
compressed) plus the `KotorCoop.*.pdb`s — nothing else. That matters for `tools/package.ps1`
and `PackageInstaller.cs`, whose uninstall rule is "every `KotorCoop.*` file in the game
directory is ours": a plain self-contained publish would have dropped ~200 `System.*.dll`
beside `swkotor.exe` and tripped the stray-file check; single-file keeps the layout to one
file. `package.ps1` now also refuses an agent exe under 20 MB, so a regression to a
framework-dependent publish cannot ship silently.

Things checked while choosing this:

* **`dotnet build` is unchanged** (framework-dependent, plain `bin\Debug\net10.0-windows`) —
  that is what `tools/demo.ps1` and `build.ps1 -Install` run on the rig. The rig therefore still
  runs the framework-dependent agent; the packaged one is the self-contained one. Same code,
  same behaviour, different host.
* **Trimming was tried and rejected.** `PublishTrimmed=true` fails `-warnaserror` with five
  IL2026s: `GameProfile.Load/Save` and `ClientCharacterPersistence`'s sidecar use reflection-based
  `JsonSerializer`, and `CoopAgent.TopFrame` uses `StackFrame.GetMethod()`. Trimmed size would be
  ~13 MB; not worth source-generated JSON contexts in a process that already works.
* **Single-file and `AppContext.BaseDirectory`**: every "beside this exe" lookup (`InstallCommand`,
  `ProfileLocator`, `SteamInviteService`) uses `AppContext.BaseDirectory`, which in a single-file
  app is the exe's directory (not an extraction directory — .NET 6+ runs managed assemblies from
  the bundle in place). Nothing used `Assembly.Location`, which would be empty.
* The self-contained exe was run on the Windows rig (`--help`, 0.5 s cold start) — VERIFIED.
* **Wine and a single-file .NET host**: the apphost maps its own file and loads the bundled
  runtime through ordinary `CreateFileMapping`/`VirtualAlloc`; .NET's self-contained apps are
  a routine thing to run under Wine 7+ and Proton 8+. NEEDS-LIVE on our exe.

## 3. Why the `dinput8` override is needed

Wine implements `dinput8.dll` itself. For DLLs it implements, Wine's default load order is
*built-in first*, and a same-named DLL beside the exe is ignored — the game runs, our plugin is
never loaded, nothing in the game folder changes. The standard fix (the same one every
`dinput8`-based loader — ASI loaders, RE Framework, ScriptHook — documents for Linux) is a
per-DLL override to *native first, built-in as fallback*:

```
WINEDLLOVERRIDES="dinput8=n,b" %command%      # Steam launch options (Steam Deck included)
```

Heroic: game settings → Advanced → environment variable `WINEDLLOVERRIDES` = `dinput8=n,b`.
Lutris: Configure → Runner options → DLL overrides, `dinput8` → `n,b`.

`n,b` rather than `n` on purpose: if our file is missing (uninstalled, or extracted to the wrong
folder) the game still starts with Wine's own DirectInput instead of failing to launch.

The plugin's own forward to the "real" `dinput8.dll` is by full path into `system32`
(`Exports.cs`, `NativeMethods.GetSystemDirectoryPath()`), which an override on the bare name
does not touch — so there is no risk of the plugin loading itself recursively. INFERRED from
Wine's load-order rules; the override is NEEDS-LIVE as "required" (it may turn out Wine already
prefers a native DLL in the application directory, in which case it is merely harmless).

## 4. Finding the game from inside a prefix

`PackageInstaller`'s detection was registry + `C:\Program Files (x86)\Steam` + `GOG Games`
walks, none of which exist in a Proton prefix. Wine maps `Z:\` to `/`, so Linux paths are
reachable as `Z:\home\<user>\...`. The additions (`ProtonPaths.cs`, pure and unit-tested in
`ProtonPathsTests.cs`):

* `STEAM_COMPAT_INSTALL_PATH` — Proton sets it to the game's install directory
  (`/home/deck/.local/share/Steam/steamapps/common/swkotor`); converted to `Z:\home\deck\...`.
  This is the one that hits when the installer is run through `protontricks-launch` or as the
  game's own launch command.
* Linux Steam roots under every `Z:\home\*` and `Z:\root`: `.steam/steam`, `.local/share/Steam`,
  the Flatpak `.var/app/com.valvesoftware.Steam/.local/share/Steam`; each root's
  `steamapps/libraryfolders.vdf` goes through the existing VDF regex, with POSIX `"path"`
  values converted the same way — which is how a Steam Deck's SD-card library
  (`/run/media/mmcblk0p1`) is found.
* Heroic (`~/Games/Heroic/*`) and Lutris (`~/Games/*`) install folders, walked like the "GOG
  folder" case (every subdirectory checked for `swkotor.exe`).

The walk only runs when `Z:\home` exists, and every candidate is verified for a real 1.0.3
`swkotor.exe` as before, so a Windows box with a `Z:` drive is unaffected.

## 5. Knowing we are under Wine

`WineDetection` (shared source between the x86 plugin and the x64 agent): `GetModuleHandleW("ntdll.dll")`
→ `GetProcAddress(…, "wine_get_version")`. The export exists only in Wine's ntdll; when present
it is called (cdecl, returns `const char*`) and `wine_get_host_version` gives the kernel
sysname/release. Never throws; cached. The plugin logs
`running under Wine <version> (<host>)` once at startup and the agent prints the same in its
banner and in the installer's setup output, so a Linux report starts with the exact Wine/Proton
build. AOT-clean: unmanaged function pointers, no delegates — the plugin build under
`-WarnAsError` is the proof.

## 6. What could still go wrong — the NEEDS-LIVE list

In the order a Linux player would hit them:

1. **The override.** §3. If `kotorcoop-plugin.log` never appears, this is it.
2. **`DirectInput8Create` through Wine's built-in dinput8.** The plugin wraps the `IDirectInput8`
   and `IDirectInputDevice8` vtables it hands out (`DirectInputKeyboardHook`, [gamepad.md](gamepad.md)
   §2). Wine's objects are ordinary COM vtables, so the wrap should be transparent; the keyboard
   path `GetDeviceData` and mouse `GetDeviceState` shapes were verified on Windows only.
3. **`AgentLauncher` → `CreateProcessW` of a 64-bit exe from the 32-bit game, with
   `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`.** Wine implements both; the kill-on-close semantics
   under Wine are the part to watch (if the agent outlives the game, that is what to report).
4. **Shared-block discovery**: the agent's `ReadProcessMemory` scan of the game for `KOCOOP01`
   and `Process.MainModule` for the game directory. Wine supports cross-bitness RPM and
   `QueryFullProcessImageName`; `MainModule` enumerates through `K32EnumProcessModules` on a
   WoW64 target — NEEDS-LIVE.
5. **The SwapBuffers IAT hook (`FrameHook`)** on Wine's `opengl32` — an IAT patch in
   `swkotor.exe`'s own import table, independent of who implements the target.
6. **Input from Steam Input on a Deck**: Proton's `xinput1_4` sees Steam's virtual Xbox
   controller; our `XInputSource` loads `xinput1_4`/`1_3`/`9_1_0` dynamically and treats "no
   pad" as fine.
7. **Text entry** (address/password box, chat, console) — keyboard only; the Deck's on-screen
   keyboard sends key events through Steam Input, which should reach the game's buffered
   DirectInput read like a real keyboard.
8. **Clipboard**: `CoopCommandDispatcher`'s address copy shells out to `clip`, which Wine does not
   ship; it is a silent try/catch and the address is printed anyway.
9. **`launch --instance 2`** on one Linux machine — the single-instance mutex rename is an
   in-process memory write, so it should be prefix-independent, but two prefixes are two Windows
   as far as the games are concerned; untried.
10. **Only the Windows binary is in scope.** GOG and Steam sell no Linux build of KOTOR 1, so a
    Heroic/Lutris/Proton install is always the Windows 1.0.3 exe under Wine — the case
    documented here. The macOS port (a different Aspyr binary) has different addresses and is
    out of scope.

## 7. Recipe for the first live report

```
# Steam / Steam Deck (Desktop Mode)
tar -xzf kotor-coop-*-linux.tar.gz && ./kotor-coop-*/install.sh    # tools/install.sh, native bash
# Properties → Launch Options:
WINEDLLOVERRIDES="dinput8=n,b" %command%
# start the game, then:
tail -f ~/.local/share/Steam/steamapps/common/swkotor/kotorcoop-plugin.log
```

The tarball is the Windows zip's game-directory image with `install.sh` in place of
`install.ps1` — a bash port of `PackageInstaller.cs` (same marker detection, plain/chain/
upgrade rules, `--check` lines and `--uninstall`), because the C# installer can only run inside
the prefix. CI's `linux-install` job installs the tarball on Ubuntu against a stub game folder
on every push, so the Linux-side installer is VERIFIED even though everything in §6 remains
NEEDS-LIVE.

Expected first lines: the plugin version, `running under Wine <x.y> (Linux <kernel>)`, the
hook install lines, then `agent: started KotorCoop.Agent.exe as pid N (tied to the game's
lifetime)`. Whatever line is *missing* is
the answer to §6. Send both logs.

## 8. See also

* [gamepad.md](gamepad.md) — the controller layer a Deck goes through.
* [steam-support.md](steam-support.md) — the Steam build itself (same on Linux; the DRM
  wrapper unpacks before our code runs).
* README "Sharing `dinput8.dll` with another mod" — chain mode under Wine needs the same
  single override (the loader is then the file named `dinput8.dll`; the plugin is
  `KotorCoop.Plugin.dll`, which nothing overrides).

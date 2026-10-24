# Widescreen and replacement-menu compatibility

Updated 2026-10-24. Cold 1280x720, 1920x1080, 2560x1440 and 3840x2160 launches now render and accept
clicks at the drawn menu targets in the supplied Enhanced v3 mod on this PC. This is a GOG
1.03, windowed, isolated-input result, not a claim of universal mod compatibility.

## Runtime placement

`ConnectMenuController` reuses the engine's embedded `BTN_WARP`. It changes its label,
extent and click/accept handlers in memory; it does not replace `mainmenu*.gui`.
`MenuColumnRule` reads the live visible button rectangles, including layouts supplied by mods.

The live page-heap check found that the old scan length, `BTN_WARP + 0x200` (`0x1448`),
extended beyond the menu allocation. Its readability guard discarded the whole scan,
silently leaving Co-op at its original rectangle. The scan now ends just after the last
button's flags (`0x1290`). Both ranges were checked against the same running menu.

Placement requires at least three distinct, non-overlapping button rows sharing a left
edge, centre or right edge (four pixels of tolerance). It uses the column's most common
size and smallest row pitch, then tries below, above, right and left. Candidates must fit
both viewport dimensions and avoid every visible button. Invalid/offscreen rectangles
cannot establish a column. If no slot is found, an existing safe Co-op rectangle is retained;
otherwise Co-op is hidden and the log points to F9. Decorations are not part of the scan,
so geometry alone cannot prove a good visual result.

Binding tracks the button pointer, viewport width and height. The retail constructor's hidden
flag also triggers rebinding when a rebuilt menu reuses its previous address. A live resolution
reload can restore "Warp" on the same visible object; viewport tracking fixes that case.
A replacement that removes
`BTN_WARP`, replaces its native class, uses different coordinates or overwrites its callbacks
still needs separate investigation.

## Supplied Enhanced v3 archive

The user's archive identifies itself as KOTOR Enhanced Menu and Controller Support v3.0:

- Nexus page: [mod 1893](https://www.nexusmods.com/kotor/mods/1893).
- Archive: `KOTOR ENHANCED MENU AND CONTROLLER SUPPORT 1893 3.0 2026-10-21T22-14Z I9srg45w.zip`.
- SHA256: `46A959525DDD0701AA274AE24689A03C11644E8BC09892455B471C19EADCDB18`.
- Contains `opengl32.dll`, `dinput8.dll`, configuration and Override assets; it also generates
  GUI resources at startup. Its README reports Steam KOTOR I 1.03 validation; GOG is unverified
  by the author. The older version string in its runtime log is documented in its build notes.
- No vendor DLLs, fonts, textures or GUI files are included in Co-op's source or release assets.

The archive was extracted into a disposable GOG 1.03 rig at
`artifacts/live-installs/menu-widescreen-20261024`. Co-op's installer preserved Enhanced's
input DLL as `dinput8.prev.dll`. The resulting chain was the Co-op loader at `dinput8.dll`,
then `KotorCoop.Plugin.dll`, then `dinput8.prev.dll`. Enhanced's OpenGL proxy stayed in place.
The real player installations were not updated.

Integration failures reproduced and fixed:

1. Enhanced already owns the game's `SwapBuffers` import. Co-op previously refused to install
   its frame hook there. It now chains through the current executable target in a foreign
   loaded image, rejects null/non-executable/private-memory/self targets, and preserves a later
   hook during uninstall. Live logs confirmed chaining; native framebuffer screenshots worked.
2. Enhanced's renderer looks up `KotorEnhancedInputDeviceCount` and
   `KotorEnhancedPhysicalMouseSerial` on the DLL named `dinput8.dll`. Renaming its proxy broke
   that lookup. The loader now forwards those zero-argument counter exports to their provider
   in the chain, returning zero when absent. The live log changed from input proxy missing
   to loaded, with two filtered keyboard/mouse devices. This does not verify a physical controller.
3. The game validates INI dimensions at `0x5F0FA9`, before its first display enumeration.
   Enhanced's `EnumDisplaySettingsA` wrapper installs its widescreen support only when called;
   that was too late, so cold 720p/1080p launches had already selected 800x600. The loader now
   prepares a one-shot `GetCommandLineA` import shim for the recognized x86 1.03 executable
   (timestamp `0x402BC2D9`, image base `0x400000`). Outside DllMain, the first executable
   command-line call primes Enhanced's own display wrapper with `ENUM_CURRENT_SETTINGS`,
   then restores the import and forwards the unchanged command line. It requires Enhanced's
   marker export and renderer ownership of the display import. It does not load modules or
   call the renderer under loader lock or copy vendor code. The separate windowed-mode guard
   below does patch recognized engine instructions in memory.
4. Enhanced's native hit-test replacements use drawing extents and the OS cursor only when
   the game is foreground. An unfocused game falls back to retail hit/local coordinates,
   explaining the reproduced submenu offsets during background diagnostics. The optional
   `EnhancedMenuInputBridge` recognizes Enhanced's existing hooks at `0x40B690` and `0x40BA20`
   and chains them. Only within those synchronous hit/local-coordinate calls does it supply
   the engine's virtual cursor and game-window identity to Enhanced's own two User32 imports.
   All other calls forward unchanged. Installation requires `isolate_real_input=1`, recognized
   renderer jump ownership and untouched User32 imports. It never activates a window or moves
   the physical cursor. No bridge is installed in ordinary `isolate_real_input=0` play. This
   fixes isolated diagnostics; it does not establish a physical-input or DPI bug in Enhanced.
5. At desktop-sized resolutions, `CClientExoAppInternal::SetVideoMode` forces fullscreen
   even when `FullScreen=0`. On the private desktop the switch fails, but GUI construction
   continues with `glCompressedTexImage2DARB` still null: the call at `0x424F33` faults at
   EIP 0. The loader checks the complete 27-byte signature at `0x5F198A`, then changes two
   bytes at `0x5F1992` to use the caller's requested windowed path. Explicit fullscreen's
   earlier branch is untouched. This requires the same recognized executable and Enhanced
   ownership checks as the bootstrap; no executable on disk is changed.
6. Enhanced's generated full-canvas roots retain their startup dimensions after a mode
   change. Retail `CSWGuiPanel::Draw` rejects any root wider/taller than the current GUI
   viewport, so 1024x768 → 1280x720 omits all controls. `EnhancedMenuViewportController`
   checks both native branch signatures and permits clipping only origin-zero roots with
   the captured Enhanced startup dimensions, while no game server is active. Other panels
   retain the original bounds checks. It preserves root/child extents: resizing roots
   broke Enhanced's managed-GUI classification and produced offset mouse targets in an
   earlier experiment. This is a draw fix, not live GUI regeneration.
7. A window client resize can leave the base OpenGL viewport at its previous size. Removing
   the 4K rig's decorations changed the client from 3840x2130 to 3840x2160 but left a 30-pixel
   drawing offset. At the rendered menu frame boundary, the controller refreshes the base
   viewport once per window/client-size change when the measured client and engine dimensions
   agree. Offset viewports and clients smaller than the requested engine mode are left alone.
   Pixel measurement moved the Co-op border from y=1506..1577 to its actual extent
   y=1476..1547. Clicking `(2896,1490)`, above the old border, then opened the browser.

## Verification on this PC

All follow-up game launches used a private non-input desktop, never `SwitchDesktop`, window
activation or physical keyboard/mouse input. Screenshots came from the game's OpenGL
framebuffer. Commands used addressed messages and the plugin's isolated DirectInput queue;
`input status` reported foreground false. Earlier baseline runs used a transparent,
non-activating window. Page heap was enabled. No session was joined and no gameplay save
was loaded. The real player installations were not changed.

| Check | Observed result |
| --- | --- |
| Stock 800x600, page heap | Before the scan fix: Co-op `(368,443,235,29)`. After: `(368,432,235,24)`, matching the column; visible and no button overlap. Geometry evidence only from that earlier run. |
| Enhanced + Co-op, actual 1024x768 | Physical client size, engine size and framebuffer agreed. Screenshot showed the Co-op label and border at `(592,516,235,24)`. A background mouse click at `(710,528)` opened the native server browser. |
| Browser close and main-menu rebuild, 1024x768 | F9 closed the browser; logs and a screenshot confirmed the rebuilt Co-op button was placed and rebound. |
| Enhanced submenus, 1024x768 | Before bridge: drawn Graphics `(306,444)` activated Feedback. After bridge: the same click opens Graphics; screenshot confirms it. |
| Cold 1280x720 | Before startup fix: 800x600, generated buttons offscreen. After: engine/framebuffer 1280x720, visible Co-op; drawn Co-op, browser Cancel, address row/Connect, address-modal Cancel and Options/Graphics activated correctly. No connection submitted. |
| Cold 1920x1080 | Before: Enhanced alone and combined fell back to 800x600. After: two cold combined launches held 1920x1080. Visible Co-op and browser; drawn browser Cancel and Options/Graphics activated correctly. |
| Cold 2560x1440 | Engine/framebuffer 2560x1440. Co-op `(1696,984,470,48)` fits the live column. Click `(1930,1008)` opens browser, Cancel `(1834,1274)` closes it, Options `(1930,890)` then Graphics `(766,820)` opens Graphics. |
| Live 1280x720 to 1280x768 | Controls remain visible, with the old 720-high generated layout until restart. Before viewport rebinding: Co-op becomes "Warp". After: label remains Co-op, drawn `(965,504)` opens browser and `(917,637)` closes it. Framebuffer/probe confirm 1280x768. |
| Live 1024x768 → 1280x720 → 1024x768 | Before clipping fix: controls disappear at 720p. After: Graphics and Options Close work at their drawn locations, Co-op opens the browser and Cancel returns to the menu at both resolutions. Roots remain 1024x768, preserving Enhanced hit classification. Layout remains generated for startup resolution. |
| Cold 3440x1440 | Still selects 800x600. Windows display enumeration on this PC does not advertise 3440x1440 (or 5120x1440). Do not force unsupported modes or claim ultrawide rendering from geometry tests. |
| Cold 3840x2160 | Before windowed guard: combined and loader+Enhanced-only launches fault at EIP 0 after failed forced fullscreen. After: repeated combined launches reach the menu. Final run has measured 3840x2160 client, engine and framebuffer; the private rig removes decorations without activation. Co-op, browser Cancel and Options/Graphics work at drawn targets, including a Co-op click near its upper edge after stale-viewport repair. No exclusive-fullscreen claim. |
| Initial combined startup | One earlier 1024 run faulted at `0x45A321` during menu/font construction. Subsequent 1024/720/1080/1440 launches reached the menu; root cause remains unresolved. |
| Steam 1.03, normal foreground input, physical controller, load/quit-to-menu, 5120x1440 | Not verified in this pass. |

Enhanced generates GUI assets at startup; its README advises restarting for resolution
changes. The clipping fix preserves drawing and input but cannot bring every old control
inside a much smaller viewport, regenerate fonts or reflow child layouts. Restart at a supported
mode for that. The transition checks establish preserved controls/binding, not dynamic rescaling.
The supplied configuration also renders small text at 4K; font scaling was not changed here.

Enhanced uses F10 for its settings. Co-op also uses F10 for its in-world duel list when
optional PvP is enabled; that key conflict remains. PvP is off by default.

Local evidence (ignored artifacts, not release assets):

- `artifacts/menu-enhanced-evidence/fixed720-probe.json`, `bridge1080-probe.json`,
  `fixed1440-probe.json`, `rebind768-probe.json`: actual engine sizes and button geometry.
- `widescreen-fixed-plugin.log`, `widescreen-fixed-enhanced.log` in that directory: cumulative
  session diagnostics, including bootstrap, input bridge, mode changes and rebinding.
- `ultra-probe.json`, `fixed2160*.json`, `first2160-plugin.log`, `2160-noplugin-enhanced.log`:
  rejected ultrawide request and 4K crash evidence.
- Rig `kotorcoop-shots/fixed720.bmp`, `fixed720browser.bmp`, `fixed720address.bmp`,
  `bridge1080-cancel.bmp`, `bridge1080-graphics.bmp`, `fixed1440.bmp`,
  `fixed1440browser.bmp`, `fixed1440graphics.bmp`: rendered menus and navigation.
- Rig `rebind1024graphics.bmp`, `rebindswitch720.bmp`, `rebindmain768.bmp`,
  `rebind768browser.bmp`, `rebind768cancel.bmp`: corrected drawn hit target, remaining
  blank-control transition, and successful Co-op rebinding (all in `kotorcoop-shots/`).
- Older `combined-720.json`/`cold720.json` and corresponding screenshots describe pre-fix
  failures. A geometry pass alone is never a visual usability pass.
- `clip720-probe.json`, `clip-plugin.log` and rig `clipswitch720.bmp`, `clipclose720.bmp`,
  `clipbrowser720.bmp`, `clipreturnbrowser.bmp`, `clipreturncancel.bmp`: clipping and
  drawn-target navigation across the 1024/720 round trip.
- `finalswitch-plugin.log` and rig `finalswitch720.bmp`, `finalswitchbrowser.bmp`,
  `finalswitchcancel.bmp`: repeated 1024 → 720 navigation with the final viewport-refresh build.
- `viewport4k-client.txt`, `viewport4k-probe.json`, `viewport4k-plugin.log`,
  `windowed-loader.log` and rig `viewport4k*.bmp`: final 4K launch, client/viewport alignment
  and navigation. `final4k*.bmp` precede the stale-viewport repair and have a 30-pixel offset.

## Repeatable checks

Final checks on this PC passed: 29 focused tests (27 menu-placement and two browser-details
tests), both native counter-export fixture cases, all five native startup fixture cases,
and the loader and NativeAOT x86 plugin builds with `-WarnAsError`. Follow-up logs are in
`artifacts/menu-enhanced-evidence/menu-regression.log`, `bridge-regression.log`,
`final-menu-tests.log`, `final-bridge-tests.log`, `final-startup-tests.log`,
`windowed-build.log` and `viewport-final-build.log` (older logs describe prior builds).

`MenuColumnRuleTests` covers stock and HR Menus geometry, captured Enhanced geometry,
rejected-mode/offscreen layouts, synthetic widescreen and ultrawide columns, variable widths,
collisions, duplicates and overflowing coordinates. `Test-LoaderEnhancedBridge.ps1` compiles
native x86 fixtures to verify optional exports with and without a provider, including an
optional-export-first call and preservation of the primary DirectInput proxy.
`Test-LoaderEnhancedStartup.ps1` verifies priming exactly once with Enhanced present or loaded
after the loader, safe no-op without Enhanced or for an unknown executable, preservation of
the command line and restoration/preservation of import owners. It also verifies that the
windowed guard changes only the two expected bytes for recognized Enhanced fixtures, does
not apply without Enhanced or to unknown executables, and refuses a mismatched instruction
signature. Panel clipping and GL viewport refresh were tested in the real private-desktop rig.

```powershell
dotnet test tests/KotorCoop.Tests --filter 'FullyQualifiedName~MenuColumnRuleTests|FullyQualifiedName~NativeBrowserEmptyDetailsTests'
pwsh src/KotorCoop.Loader/build.ps1 -WarnAsError
pwsh tools/Test-LoaderEnhancedBridge.ps1
pwsh tools/Test-LoaderEnhancedStartup.ps1
pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError
pwsh tools/menu-layout-probe.ps1 -ProcId 1234 -ExpectedWidth 1920 -ExpectedHeight 1080 -OutPath artifacts/menu-1080p.json
```

The root solution does not build the native plugin. The read-only probe records physical
client size when available, otherwise engine dimensions, plus buttons and loaded DLL names.
It fails on a rejected requested mode or unsafe Co-op geometry. It cannot establish that the
main menu is currently drawn, that a mod's hit areas agree with its rendering, or that a
physical controller works. Always pair its result with screenshots and actual activation,
then repeat after closing the browser and changing resolution.

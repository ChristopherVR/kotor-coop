# Running the rig without taking over the screen, mouse or keyboard (2026-09-25)

Goal (verbatim from the request that started this): "launch the game in isolation, not viewing
it on our main screen, while still letting the AI take images etc. as it requires — we want to
play games while this still runs." So a rig run must not occupy the user's visible monitor,
steal keyboard focus, move the user's real mouse cursor, or pause/break when unfocused — and
screenshots of each instance must still work.

## The three pieces, and what changed

### 1. Screenshots: `tools\shot.ps1`

**Before:** `CopyFromScreen` at the window's screen position — a screen-RECTANGLE grab. Correct
only while the window is genuinely on top, on-screen and unoccluded; anything covering it (or
moving it off-monitor) got captured instead of the game.

**Now:** tries `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)` first — asks DWM directly for that
window's own composed surface, independent of what is drawn on top of it or where it sits on
the desktop. No activation, no `SetForegroundWindow`. Falls back automatically to the old
screen-rectangle path (which does need the window on-screen, unoccluded and briefly raised) if
`PrintWindow` fails or comes back a single flat colour — the known failure shape for a window
whose surface DWM never actually composited. `-ForceScreenCapture` skips straight to the
fallback. Same parameters as before (`-OutPath -ProcessName -ProcId -FullScreen`); nothing about
the calling contract changed.

**Live-verified (2026-09-25, against a plain window — the rig itself was locked by the other
session)**: captured Notepad's own content correctly (a) while a topmost Calculator window
fully covered it on screen, and (b) while Notepad sat 10000px right of the desk's 4480px-wide
virtual screen, entirely off every monitor. **NEEDS-LIVE against `swkotor.exe`** — the mechanism
is identical (PrintWindow does not care what the window draws with), but nobody has run it
against the actual game window yet because the rig was locked for this whole pass. Watch for
the one documented limitation: PrintWindow needs DWM composition, which an **exclusive
fullscreen** swapchain bypasses. Run windowed (the rig already does — `tools\demo.ps1` never
asks for fullscreen).

### 2. Synthetic input: the plugin's `input` console verb

**Before:** `tools\click.ps1` used real `SendInput`, which moves the user's actual OS cursor and
needs the window focused and on top (`docs/research/mouse-cursor-input.md` §7's activity gate;
`SendInput` with `MOUSEEVENTF_ABSOLUTE` also delivers to whatever window is under that *screen*
point, not the target HWND, the moment anything is on top or the window moved).

**Now:** a new plugin-side console verb, `input click <x> <y> [left|right]` / `input move <x>
<y>` / `input key <dik> [down|up|tap]`, plumbed exactly the way `gamepad` already is (Session
`ConsoleCommand` → Agent `CoopCommandDispatcher` → `SharedBlock.CheatKindInput = 103` →
`CheatController` → the new static `InputInjectionController`,
`src/KotorCoop.Plugin/InputInjectionController.cs`). It reuses machinery this repo had already
proven live rather than inventing a new one:

* **Mouse**: the exact `PostMessage` window-message path `GamepadController` already uses for
  its cursor (`docs/research/mouse-cursor-input.md` §6 "Recipe A" — `WM_LBUTTONDOWN`/`WM_
  LBUTTONUP` carry the position in `lParam`; the engine's `BufferEvent_2` buffers the X/Y/button
  edge from that single message and re-runs hover before dispatch, so no separate
  `WM_MOUSEMOVE` is needed for a click), **minus** the one call `GamepadController.ApplyCursor`
  makes that we must never make here: `SetCursorPos`. `InputInjectionController` never touches
  the OS cursor. Coordinates are CLIENT pixels — the same space `tools\shot.ps1` captures — so a
  caller can point at a screenshot pixel with no translation (no `ClientToScreen`; PostMessage's
  `lParam` is already client-relative, unlike `SendInput`).
* **Keyboard**: the existing `DirectInputKeyboardHook` proxy — this plugin IS `dinput8.dll`, and
  `DirectInputKeyboardHook` already wraps the real `IDirectInputDevice8` keyboard's
  `GetDeviceData`/`GetDeviceState` to append synthetic key events to the engine's own buffered
  read (`src/KotorCoop.Plugin/DirectInputKeyboardHook.cs`, built for the gamepad layer). `input
  key` just calls its existing `Enqueue`, no new hook needed.
* **Unfocused delivery**: reuses the SAME two flags the virtual-pad rig path already proved live
  (`gamepad sim`, 2026-09-03/09-24 per that file's doc comments) — `Odyssey.EnsureInputActive()`
  (flips the engine's own `WM_ACTIVATE`-driven "input active" flag on directly, since an
  isolated window's messages would otherwise be dropped at `WinMessageHandler`'s activity gate)
  and `DirectInputKeyboardHook.DeliverWhileUnacquired` (answers a keyboard read with the queued
  events even though the real device can never ACQUIRE while the window sits in the
  background — its own per-frame formula in `GamepadController.OnFrame` keeps the flag on for
  exactly as long as events are pending, so `InputInjectionController` does not need any state
  of its own).

`tools\click.ps1` now goes through this by default (`blockpoke.ps1 console 'input click ...'`);
the old `SendInput` path is kept behind `-RealMouse`. Added `-Right` for a right-click.

**Built and unit-tested (2026-09-25)**: `pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError`
clean, `dotnet build KotorCoop.slnx` clean, the console-verb-table coverage test and the
existing `ConsoleCompletionEngineTests.NoCoopVerbShadowsARetailConsoleVerb` pass with `input`
added. **NEEDS-LIVE** — never run against the actual game: the rig was locked by another session
for this entire pass. The mechanism it reuses (Recipe A mouse messages, the DirectInput keyboard
proxy, the two unfocused-delivery flags) is each independently live-verified elsewhere in this
repo (dates above); what has NOT been exercised live yet is this specific new console verb and
its plumbing through `CheatKindInput = 103`. Next session with the rig free: `blockpoke.ps1
console 'input status'`, then `input click 485 322` on the main menu's Load Game button (client
px per §5 of mouse-cursor-input.md) and confirm the load screen opens.

### 3. Window placement: `demo.ps1`/`demo3.ps1 -Isolated`

**Correction from the first live run (2026-09-25).** Off-screen placement does NOT work for
the game. With both KOTOR windows at x=6000 and x=6830, `shot.ps1` captured solid black. The
same windows moved back onto a monitor captured normally: 294 of 300 sampled points were lit,
with PrintWindow and with a plain screen grab alike. Windows only composes a D3D window's
content while the window overlaps a monitor. The earlier "works at x=10000" check used
Notepad, which draws through GDI.

Two ways around it were measured on the live game:
- A 5-pixel sliver left on the monitor edge captured normally (293/300).
- A fully transparent, click-through layered window ON the monitor captured normally
  (294/300). The screen at the same spot showed whatever was behind it (all 300 points differed
  from the captured game image), so the user never sees it.

`-Isolated` uses the second: **ghost mode** (`movewin.ps1 -Ghost`, undo with `-Unghost`). The
window gets `WS_EX_LAYERED` with alpha 0, `WS_EX_TRANSPARENT` (mouse clicks pass through to
whatever is underneath), `WS_EX_TOOLWINDOW` (not on the taskbar or in Alt-Tab), and the bottom
of the z-order. `-MonitorX` now defaults to 0, so the windows sit on the primary monitor,
invisibly. The first capture right after a window returns to a monitor can come back black;
the next one a few seconds later is fine.

Also verified live on a ghosted, unfocused window: `click.ps1` (the plugin's `input click`)
opened the Messages screen, and the capture showed it. `click.ps1` needed one fix on the way:
it splatted `-ProcId` as an array, which reached blockpoke as a positional string. It now uses
a hashtable splat.

The weighing below is kept for the record. The driver option is still the answer if a truly
separate monitor is wanted.


Three options were weighed for "where does the window live":

| Option | Verdict |
| --- | --- |
| **Off-screen position on the real desktop, never activated** | **Chosen.** No driver install, `movewin.ps1`'s existing `SetWindowPos(..., SWP_NOACTIVATE)` already does this correctly (it always has — the positioning calls never activated), and DWM keeps composing a window that is merely positioned outside every monitor's rectangle (confirmed live in the `shot.ps1` test above: PrintWindow saw full content at x=10000 on a 4480px-wide desk). |
| **A separate non-interactive `CreateDesktop`** | Rejected without implementation. `PrintWindow`/DWM capture do not cross a window-station/desktop boundary the way this needs; making it work would mean switching capture to a WDDM/desktop-duplication API scoped to that other desktop, and launching `swkotor.exe` itself with a non-default `STARTUPINFO.lpDesktop` is a bigger, riskier change to how the rig launches the game than this pass's budget allowed. Left as a documented option, not attempted. |
| **A virtual/indirect display driver (e.g. the open-source "IddSampleDriver"/"Virtual Display Driver")** | Evaluated, not installed (per the task's own instruction not to install drivers). This is the right answer if the user ever wants the windows to visibly exist on a real (virtual) monitor rather than merely off the edge of the real one's rectangle — e.g. to later record them, or if off-screen positioning ever proves unreliable on some GPU/driver combination. **User install step**, if wanted: install a signed IDD (Windows requires either a signed driver or test-signing mode enabled), which creates a new `\\.\DISPLAY`n reported to Windows; then pass `-MonitorX -MonitorY` set to that display's origin (read from `Get-CimInstance -ClassName Win32_VideoController` or Windows' own Display Settings) instead of the default far-off-screen coordinates. No plugin or script change needed for this — `-MonitorX`/`-MonitorY` already exist for exactly this purpose. |

`-Isolated` (both `demo.ps1` and `demo3.ps1`):

* Positions every instance at `($MonitorX, $MonitorY)` and onward (default `$MonitorX = 6000`,
  `$MonitorY = 0` — past this rig's own 4480px-wide virtual screen) instead of `(0,0)`/`(830,0)`.
* Un-minimises with `SW_SHOWNOACTIVATE` (`movewin.ps1`'s new `-NoActivate` switch) instead of
  `SW_RESTORE`, everywhere the scripts un-minimise a window, so no launch-time restore steals
  the foreground either.
* Positioning itself was ALREADY non-activating (`movewin.ps1` has always passed
  `SWP_NOACTIVATE`) — the only things `-Isolated` had to change were the coordinates and the
  un-minimise call.

**One known, documented gap — not silently swept under**: `agent launch --load-save` /
`agent load` (the automated main-menu navigation that picks the save) still calls
`GameInput.FocusGameWindow` (`src/KotorCoop.Agent/GameInput.cs`), which DOES call
`SetForegroundWindow` — this predates the plugin's new `input` verb and was never touched this
pass. So under `-Isolated`, each window is still briefly focused/activated *during the automated
load step only*; once both instances are loaded and the final side-by-side (now off-screen)
placement runs, neither is ever activated again, and no OS input is ever injected again either
(that is what `tools\click.ps1`/blockpoke's `input` verb replace). Converting the agent's own
menu-click driver to the plugin's `input` verb (which already works with no world loaded, i.e.
at the main menu) would close this gap; it is a separate, larger change to
`KotorCoop.Agent/GameInput.cs`'s click path, out of scope for this pass's budget.

## 4. PrintWindow is unreliable for this game; the plugin now takes its own screenshots (2026-09-25)

**Finding, measured live:** `tools\shot.ps1`'s `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`
path (§1 above) is NOT reliable against this game. A KOTOR window fully visible on screen and
unoccluded — a plain `CopyFromScreen` of the same window had 294/300 lit sample points, i.e.
genuinely on screen and rendering — captured solid BLACK via `PrintWindow` on the very same
pass. An earlier pass had seen `PrintWindow` work against a window of the same kind. DWM
evidently does not consistently have a composed surface to hand back for this game's OpenGL
swapchain, even when the window is demonstrably visible and on top — so `PrintWindow`'s
correctness is intermittent, not just "needs composition" as §1 originally documented, and it
can no longer be the default.

**Fix: a plugin-side screenshot**, since the one thing guaranteed to hold the real pixels is the
GL context itself. New console verb `screenshot <name> [front]` / `screenshot status`, plumbed
exactly like `gamepad`/`input`/`stealth` (Session `ConsoleCommand` →
`CoopCommandKind.Screenshot` → Agent `CoopCommandDispatcher` → `SharedBlock.CheatKindScreenshot
= 105` / `IGameBridge.CheatKinds.Screenshot = 105` → plugin `CheatController` →
`src/KotorCoop.Plugin/ScreenshotController.cs`):

* **Hook site**: the existing `GDI32!SwapBuffers` IAT hook (`src/KotorCoop.Plugin/FrameHook.cs`,
  IAT slot `0x0073D00C`) — the same one `ChatInputController`/`ChatOverlay`/`GamepadController`
  already run on every frame. `CheatController.OnFrame` (which reads the console-driven cheat
  request) runs late inside that same callback, i.e. BEFORE the real `SwapBuffers` call the
  Detour makes at the very end — so a screenshot request lands inside the SAME
  already-proven-safe frame slot every other per-frame plugin write uses, with no new hook.
* **GL calls**: `glReadBuffer(GL_BACK` or `GL_FRONT)`, `glPixelStorei(GL_PACK_ALIGNMENT, 1)`,
  `glReadPixels(0, 0, w, h, GL_BGR_EXT, GL_UNSIGNED_BYTE, buffer)`, `glGetError()` for
  diagnostics, and `glGetIntegerv(GL_VIEWPORT, ...)` as a fallback for `w`/`h` if the engine's
  own `Odyssey.ScreenWidth`/`ScreenHeight` globals (`SCREEN_WIDTH`/`SCREEN_HEIGHT`,
  `0x0078D1D4`/`0x0078D1D8` — already used by `GamepadController`/`InputInjectionController` for
  the same client-pixel space `tools\shot.ps1` captures) read zero. All five resolved once via
  `GetProcAddress` on `opengl32.dll` — they are core GL 1.1 exports, no
  `wglGetProcAddress`/extension-string dance needed. No `wglMakeCurrent`: the render thread
  already has its GL context current at this point in the frame (it just finished drawing),
  which is also why this must run from the frame hook and nowhere else.
* **Two buffers, one call site, no deferred hook**: `back` (default) reads `GL_BACK` at this
  point in THIS frame — the just-rendered, not-yet-presented image. `front` reads `GL_FRONT` at
  the exact same point — which holds whatever the PREVIOUS frame's swap already presented, i.e.
  the currently-displayed image, since a front buffer's contents are stable between swaps. Both
  reads happen synchronously in the same `ScreenshotController.Capture` call; there is
  deliberately no "read after our own hooked swap" mechanism (which would need a second,
  unverified hook point) — `front` does not need one, because GL_FRONT is valid to read at any
  time, not only immediately after a swap.
* **Format**: 24-bit uncompressed BMP, written with zero dependencies (no System.Drawing, no
  zlib) — `GL_BGR_EXT` pixel format matches a BMP's native byte order exactly, and
  `glReadPixels`' row order (bottom row first, GL's lower-left origin) matches a
  positive-`biHeight` BMP's row order (also bottom-up) exactly, so no row flip and no channel
  swap are needed. ⚠ NEEDS-LIVE: confirm the image is right-side up against the real game —
  nothing in this pass could run `swkotor.exe` (rig locked by the main session); if it comes out
  upside down the fix is a negative `biHeight` (top-down BMP) or reversing the row-copy loop,
  not both.
* **Transport**: the cheat-text slot is 31 bytes (`SharedBlock.CheatTextLength` less the null) —
  nowhere near an absolute path. So the verb takes a short NAME
  (`ScreenshotController`'s own safety check: letters/digits/`_`/`-`, max 24 chars) and always
  writes to a fixed, predictable location, `<gamedir>\kotorcoop-shots\<name>.bmp`.
  `tools\shot.ps1` generates the name, polls for that file, and opens/converts it to whatever
  `-OutPath` was actually asked for.
* **Latching**: no per-frame re-capture — `CheatController.OnFrame`'s own existing
  `_appliedSerial` gate (shared by every cheat kind) already ensures `Execute`/`HandleConsole`
  runs exactly once per new console line, so no separate latch was needed for this verb
  specifically.
* **Needs no world**: handled at the very top of `CheatController.Execute`, before the "no
  player loaded" gate — same as `gamepad`/`input` — so a main-menu screenshot works (useful for
  confirming an `input click` on the main menu actually landed).

**`tools\shot.ps1`** (updated): the plugin path is now the DEFAULT — `blockpoke.ps1 console
'screenshot <name>'` (same mechanism `click.ps1`'s default `input` path already uses), then
polls for the BMP and opens/converts it. `-PrintWindow` forces the old PrintWindow-first chain
(§1); `-ForceScreenCapture` forces the screen-rectangle grab; the new `-Front` switch requests
`screenshot <name> front`. The plugin path automatically falls back to `-PrintWindow`'s chain
(and from there to `-ForceScreenCapture`) if the console verb gets no reply (agent not attached
to this instance) or the BMP never appears within 8s — the script always reports which method
actually produced the image. Needs the out-of-process agent attached to the target instance,
the same precondition every other `blockpoke console ...` verb already has (`gamepad`, `input`);
`-PrintWindow`/`-ForceScreenCapture` do not need it, which is why they remain the fallback chain
rather than being retired.

**Which method to use**: the plugin path (default) needs no window visibility, composition, or
occlusion state at all — it is correct whether the window is on top, covered, ghosted
(`movewin.ps1 -Ghost`), or off-screen, because it never asks the desktop compositor anything.
`-PrintWindow` is kept for when no agent is attached (e.g. debugging the rig launch itself,
before a session exists) but is now known-unreliable, not a fallback to trust blindly.
`-ForceScreenCapture` still needs the window genuinely on-screen and briefly foregrounded, same
as always. **NEEDS-LIVE**: this whole verb (both `back` and `front`), and the BMP's row
orientation, against the real game — the rig was locked by the other session for this entire
pass.

## 5. Input isolation: the REAL keyboard/mouse must never reach an isolated window (2026-09-25)

**The bug, as reported.** Live on 2026-09-25, with `-Isolated` running (both windows ghosted —
invisible, click-through, on the user's primary monitor while they played something else): the
host's in-game camera kept swinging between two plugin screenshots taken seconds apart, with
nobody at that instance's keyboard or mouse. This is unacceptable on its own (a rig run must not
be affected by, or leak into, whatever the user is doing on their real desktop) and it is also a
correctness hazard for every other test this rig runs — a screenshot, a click assertion, a
combat log could all be quietly contaminated by the user's own unrelated mouse movement.

### Mechanism (evidence)

**VERIFIED (static RE, this repo's own disassembly, docs/research/mouse-cursor-input.md and
docs/re/frame-hook.md) — two completely separate input paths exist, and only one of them was
ever guarded:**

1. **The cursor / clicks are window-message-driven, not DirectInput.** `Global::WinMessageHandler
   0x00402800` reads `WM_MOUSEMOVE`/`WM_*BUTTON*` straight out of `lParam` and that is the ONLY
   place the cursor position (`CClientExoAppInternal+0x3C8/+0x3CC`) is ever written — see
   mouse-cursor-input.md's whole answer sheet. `DIMOUSESTATE.rgbButtons` is never even read.
   Consequence: a `WS_EX_TRANSPARENT` ghost window (`movewin.ps1 -Ghost`, §3 above) already
   protects this path structurally — Windows routes mouse hit-testing straight through a
   transparent window to whatever is underneath it, so the ghosted KOTOR window does not receive
   real `WM_MOUSEMOVE`/`WM_*BUTTON*` for the point under the user's real cursor at all. This is
   why `input click`/`input move` (`InputInjectionController`, `PostMessage`-based) were already
   safe and already worked unfocused (§2 above) — and also why this path was NOT the bug.

2. **The camera is DirectInput-driven, and nothing guarded it.** The engine polls a SEPARATE
   `IDirectInputDevice8` mouse object once a frame, immediate mode, `cbData 0x10`
   (`DIMOUSESTATE`) — `CExoInputInternal::UpdateMouseDelta 0x005E0110` →
   `CExoRawInputInternal::GetMouseState 0x005E4050` → `IDirectInputDevice8::GetDeviceState`
   (docs/re/frame-hook.md's "polling path", VERIFIED) — and turns `lX`/`lY` straight into the
   camera look axes (`CExoInputInternal+0x3A0/+0x3A4`, mouse-cursor-input.md §"CORRECTION to the
   working assumption"). This device is set up with `SetCooperativeLevel(hwnd, 0x0A)`
   (`CExoRawInputInternal::InitializeDirectInputMouse 0x005E3FA0`, VERIFIED), the same flag value
   the keyboard uses. **Before this pass the mouse device was never wrapped at all** — only the
   keyboard was (`DirectInputKeyboardHook`, built for the gamepad layer) — so any successful poll
   of it passed the real, physical mouse's relative motion straight through, unfiltered, with no
   regard for window focus, ghosting, click-through or z-order (DirectInput's device polling has
   nothing to do with Win32 window-message routing — it is a completely separate path).

**A concrete, in-repo candidate for HOW a background window's poll could still succeed (INFERRED,
not live-confirmed this pass — flagged NEEDS-LIVE below):** a background/unfocused window is not
normally able to keep this device acquired — the keyboard's own empirical behaviour under the
identical `0x0A` cooperative level is to fail every read with `DIERR_NOTACQUIRED 0x8007000C`
while unfocused (`DirectInputKeyboardHook`'s own doc comment, live-verified 2026-09-03). But
`CClientExoApp::SetInputActive 0x005EDA10` — read via disassembly 2026-09-03,
`Odyssey.Input.cs`'s own comment on that thunk — "writes the flag, **re-Acquires the keyboard
and mouse devices**". `Odyssey.EnsureInputActive()` calls that function DIRECTLY, in-process,
every single time the rig's own synthetic input needs to get past `WinMessageHandler`'s activity
gate (`InputInjectionController.EnsureUnfocusedDelivery`, `GamepadController.OnFrame`'s virtual-
pad branch) — a call that is not gated by the window's real OS foreground state the way a genuine
`WM_ACTIVATE` sequence would be. That re-Acquire is a plausible, concrete mechanism for the real
mouse device's `Acquire()` to succeed again even while the ghost window is never the OS foreground
window. The fix below does not depend on resolving this further: whatever the exact path that let
a real poll through, neutralising the state at the proxy makes it irrelevant.

### The fix

`isolate_real_input` (coop.ini, default `0` — never affects a real player):

* **Mouse — new wrap.** `src/KotorCoop.Plugin/DirectInputMouseHook.cs`: the same forwarding-
  proxy technique `DirectInputKeyboardHook` already uses (a patched vptr crashes the game —
  DirectInput validates the vptr it is handed — so a real `IDirectInputDevice8` is wrapped, never
  copied), reusing that class's own `BuildProxyTable`/`NewProxy` rather than duplicating them.
  `DirectInputKeyboardHook.CreateDeviceDetour` now recognises GUID_SysMouse (one byte off
  GUID_SysKeyboard: `...2B60` vs `...2B61`) alongside the keyboard's GUID_SysKeyboard and wraps
  whichever device the game just created. When isolating, `GetDeviceState` zeroes the entire
  16-byte `DIMOUSESTATE` (`lX`, `lY`, `lZ`, all 4 button bytes) it hands back, regardless of what
  the real device reported — "the real mouse did not move and no button is down", the only thing
  this device is ever polled for. Off (the default): a byte-for-byte passthrough, so a real
  player sees no change of any kind, only the cost of one extra indirection DirectInput itself
  already required.
* **Keyboard — extended, already-wrapped path.** `DirectInputKeyboardHook.GetDeviceStateDetour`/
  `GetDeviceDataDetour` now drop every REAL key transition when isolating (immediate-state bytes
  masked to the plugin's own held/synthetic state only; buffered real events hidden by reporting
  zero of them before the existing synthetic-append loop runs) while the plugin's OWN queued
  synthetic events — `input key ...`, the gamepad layer — are completely unaffected, since they
  were never read from the real device in the first place.
* **Pure logic, tested directly** (no engine pointer involved): `InputIsolationRule.cs` — what
  count of real buffered events survives, how a state byte is masked, whether a given
  `GetDeviceState` size is the plain `DIMOUSESTATE` this device ever actually uses. See
  `tests/KotorCoop.Tests/InputIsolationRuleTests.cs`.
* **No new window-message hook.** The cursor/click path (§"Mechanism" point 1 above) is already
  structurally protected by ghosting (`WS_EX_TRANSPARENT`), and building a new inline detour on
  `WinMessageHandler` to filter messages would be the kind of hook this repo has been burned by
  before when shipped without a live pass (`docs/research/*` "hook prologue = whole instructions"
  — a relocated prologue that splits an instruction crashed every computer-terminal reply for
  weeks before it was caught). Out of scope for a pass that cannot launch the game to verify it;
  left as a documented gap, not silently assumed safe. If a future isolated layout ever stops
  using a transparent window, this gap needs re-examining first.
* **Wiring:** `PluginHost.Startup.cs` reads `isolate_real_input` before any device gets wrapped
  (`DirectInputKeyboardHook.IsolateRealInput`, set once per process start) and logs when it is on.
  `Exports.DirectInput8Create`'s wrap gate now also fires on `isolate_real_input` alone (not only
  `gamepad`), so a rig install does not need `gamepad = 1` for isolation to take effect. Three
  config readers agree on the OFF default: the plugin's own `PluginConfig.ReadBool`, the digest-
  only key lists in both `PluginConfig.cs` and `KotorCoop.Session/CoopConfig.cs` (developer
  switch, not player-facing — docs/CONFIG.md's "Diagnostics (rig-only)" table), matching the
  pattern `docs/config-default-drift.md`'s own note insists on for every default.
* **`tools/demo.ps1`/`demo3.ps1 -Isolated`** now write `isolate_real_input = 1` into every
  instance's coop.ini for the run (same `Set-CoopIniKey` helper `join_mode`/`auto_start_agent`
  already use, written BEFORE the game process launches so the first `DirectInput8Create` already
  sees it) and restore `0` on `-Stop` — the rig must never leave a player's install silently
  eating their own input after the run ends, the same rule `auto_start_agent`/`join_mode` already
  follow.

### NEEDS-LIVE

Nobody could launch the game this pass (explicit instruction). Two things are unverified:

1. **The fix itself.** Launch `pwsh tools\demo.ps1 -Isolated`, then take two plugin screenshots
   of the HOST instance (`tools\shot.ps1`, or `blockpoke console 'screenshot <name>'`) about 10
   seconds apart while physically moving the real mouse around on the desk in between. The camera
   must be in the exact same orientation in both images — no drift, no swing. Before this fix the
   same recipe is what caught the bug in the first place. `blockpoke console 'input status'`
   (or `gamepad status`) should report `DI mouse wrapped` and `isolate real input ON`.
2. **Synthetic input still works while isolating.** `input click 686 17` (client px, the
   Messages icon per the demo's own click-test convention — mouse-cursor-input.md §5's runtime-
   formula caveat still applies at other resolutions) must still open the Messages screen with
   `isolate_real_input = 1` in effect — confirming the plugin's own `PostMessage`-based clicks and
   `DirectInputKeyboardHook.Enqueue`-based keys are genuinely unaffected by isolation, only the
   real hardware is. If the mouse device never gets wrapped (`DI mouse not wrapped` in `input
   status`) on a real machine, check whether `DirectInput8Create` actually created a
   `GUID_SysMouse` device before the plugin's `CreateDeviceDetour` ran, or capture the GUID it
   see instead — `IsSystemMouse`'s byte layout is decoded from the same, unverified-live pattern
   `IsSystemKeyboard` already uses successfully, so a mismatch would point at something else
   about this specific machine's DirectInput setup, not the decode itself.
3. **The exact re-acquire mechanism** in the "Mechanism" section above is INFERRED, not
   confirmed — the fix does not depend on it being right, but if a future session has spare rig
   time, watching `CClientExoApp::SetInputActive`/the mouse device's `Acquire()` return value
   around an `EnsureInputActive()` call while the window is genuinely backgrounded would close
   that gap for good.

## User-facing summary

* **Screenshots while occluded/off-screen/ghosted**: `tools\shot.ps1`, same interface as
  before (`-OutPath -ProcessName -ProcId -FullScreen`). Default method changed (§4,
  2026-09-25): a plugin-side GL readback rather than `PrintWindow`, since `PrintWindow` measured
  unreliable against this game's OpenGL surface — captured solid black once, against a window
  measured genuinely on screen and rendering. Needs the out-of-process agent attached to the
  target instance (same precondition `input`/`gamepad` already have); falls back to the old
  `PrintWindow`-then-screen-capture chain automatically if not. **NEEDS-LIVE.**
* **Clicks/keys while unfocused/off-screen**: works today (once `input`'s plumbing gets its
  first live pass), no setup — `tools\click.ps1` (default) or `blockpoke.ps1 console 'input
  ...'`.
* **Windows never appear on your monitor for the steady-state part of a run**:
  `pwsh tools\demo.ps1 -Isolated` (or `demo3.ps1 -Isolated`). No install needed. The games
  sit on your primary monitor as invisible, click-through windows, which is what lets
  screenshots keep working (verified live 2026-09-25). The windows
  will still flash onto the real foreground briefly during the automated save-load step at the
  start of a run (see the gap above) — after that, nothing.
* **If you want the windows to live on an actual (virtual) monitor instead of merely off the
  edge of the real desktop** (e.g. so a remote-desktop/VNC session into that monitor could watch
  them, or a recorder could target it): install a virtual/indirect display driver yourself (a
  signed one, or with Windows test-signing enabled — this project does not install drivers for
  you), then pass `-MonitorX`/`-MonitorY` at that display's origin.

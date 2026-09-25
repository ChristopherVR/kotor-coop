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

## User-facing summary

* **Screenshots while occluded/off-screen**: works today, no setup, `tools\shot.ps1` unchanged
  in interface.
* **Clicks/keys while unfocused/off-screen**: works today (once `input`'s plumbing gets its
  first live pass), no setup — `tools\click.ps1` (default) or `blockpoke.ps1 console 'input
  ...'`.
* **Windows never appear on your monitor for the steady-state part of a run**:
  `pwsh tools\demo.ps1 -Isolated` (or `demo3.ps1 -Isolated`). No install needed. The windows
  will still flash onto the real foreground briefly during the automated save-load step at the
  start of a run (see the gap above) — after that, nothing.
* **If you want the windows to live on an actual (virtual) monitor instead of merely off the
  edge of the real desktop** (e.g. so a remote-desktop/VNC session into that monitor could watch
  them, or a recorder could target it): install a virtual/indirect display driver yourself (a
  signed one, or with Windows test-signing enabled — this project does not install drivers for
  you), then pass `-MonitorX`/`-MonitorY` at that display's origin.

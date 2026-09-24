# Gamepad / controller support

Status (2026-09-03): **implemented, unit-tested, live-verified on the rig with the virtual pad**
(walking, Escape/panels, the cursor, and world clicks that select and act on objects — §6).
A physical XInput pad has not been on the rig yet.

Status (2026-09-24): **text entry added** — the address box, the password box, chat and the
console can now be typed into with the pad, via an on-screen character grid (§7). Unit-tested
(grid navigation, editing, confirm/cancel); NEEDS-LIVE the same way §6's click checks were
before they were run.

KOTOR's PC release has no controller support of its own: it reads its keyboard as buffered
DirectInput events and its cursor from ordinary window messages, and never asks XInput or
DirectInput for a joystick. The mod adds a controller by translating a pad into exactly those
two things — key events the engine's own keyboard device appears to have produced (honouring
the player's `swkotor.ini` re-bindings) and the same `WM_MOUSEMOVE`/`WM_LBUTTONDOWN` a mouse
would send — so the engine sees nothing it does not already understand.

Player-facing summary: [../../README.md](../../README.md) §"Playing with a controller";
config keys `gamepad` / `gamepad_cursor_speed` in [../CONFIG.md](../CONFIG.md); the
`gamepad` console verb in [../CONSOLE.md](../CONSOLE.md).

## 1. Why in-process, and why the keyboard

Two facts from earlier research fix the design:

- **Externally injected input never reaches the game.** `SendInput`/`keybd_event` from another
  process were tried for the unattended rig and the engine ignored them
  (docs/live-verification.md): the engine reads the keyboard through DirectInput's buffered
  `IDirectInputDevice8::GetDeviceData` (`CExoRawInputInternal::GetKeyboardBuffer 0x005E3AE0`,
  [../re/frame-hook.md](../re/frame-hook.md) §"Per frame"), not the window message queue.
- **This DLL *is* the game's `dinput8.dll`.** Every DirectInput object the game ever holds is
  handed out by `Exports.DirectInput8Create`, so the plugin can wrap the `IDirectInput8` it
  returns and every device it creates. The Steam overlay already wraps the same three
  interfaces the same way in this process ([../re/frame-hook.md](../re/frame-hook.md) §"Wrapping
  the returned interface provably works here"), so the technique is known not to upset this
  engine.

The cursor, by contrast, comes from the message pump: `Global::WinMessageHandler 0x00402800`
takes `WM_MOUSEMOVE`/`WM_L/RBUTTON*` with the client position in `lParam`
([mouse-cursor-input.md](mouse-cursor-input.md) §"Recipe A"), and the authoritative cursor the
GUI is re-fed every frame lives at `CClientExoAppInternal+0x3C8/+0x3CC`. `PostMessageA` from
inside the process lands on the game's own `PeekMessage` loop next frame, like real input.

So: buttons and sticks become **DIK scancodes** appended to the engine's own buffered keyboard
read; the cursor becomes **posted mouse messages** at a position the plugin advances itself.

## 2. The DirectInput wrap (`DirectInputKeyboardHook`)

1. `Exports.DirectInput8Create` calls the real export, then (when `gamepad` is on) hands the
   `void**` to `WrapDirectInput`, which replaces the returned `IDirectInput8` with a **proxy
   object**: 16 bytes of `VirtualAlloc`'d memory holding a vptr, the real interface pointer and
   the real methods it detours. The proxy's vtable (11 slots) forwards every slot to the real
   object through a five-instruction x86 thunk (`this = [this+4]; jmp [vtable+slot]`, so the
   arguments and the stdcall clean-up stay exactly as the caller made them) except slot 3
   (`CreateDevice`, +0x0C), which is the plugin's detour. The real object is never touched.
2. `CreateDeviceDetour` calls the real `CreateDevice` through the proxy's saved pointer, and
   when the GUID is `GUID_SysKeyboard` `{6F1D2B61-D5A0-11CF-BFC7-444553540000}` hands back a
   proxy for the returned `IDirectInputDevice8` the same way: 32 slots, slot 9
   (`GetDeviceState`, +0x24) and slot 10 (`GetDeviceData`, +0x28) detoured.
3. `GetDeviceDataDetour` captures the caller's capacity (`*pdwInOut` — DirectInput overwrites
   it with the returned count, so it is only knowable BEFORE the call), calls the real
   method, and — only for a consuming (`!DIGDD_PEEK`), non-null-buffer read with
   `cbObjectData == 0x14` — appends the plugin's pending `(DIK, down)` transitions as
   `DIDEVICEOBJECTDATA` entries (`dwOfs` = DIK, `dwData` = 0x80 for down, a tick timestamp, a
   sequence number starting at 0x40000000 so it can never collide with the device's own) up to
   the capacity, and writes the new count back. A failed read (the window lost focus:
   `DIERR_NOTACQUIRED`/`INPUTLOST`, about to be followed by a re-acquire) leaves the events
   queued for the next good read — except under the rig's virtual pad (`DeliverWhileUnacquired`,
   below), where the failed read is answered with the queued events alone.
4. `GetDeviceStateDetour` ORs the plugin's held keys into a 256-byte immediate-state read, for
   consistency; the engine reads the keyboard buffered, so this is belt-and-braces.

**Why a proxy and not a patched vptr (live 2026-09-03).** The first version copied the real
vtable and pointed the real object's vptr at the copy. DirectInput validates every object it is
handed by reading a header it keeps in front of its own vtables (`Common_hresPv`), and an
object whose vptr points at a plain copy fails that read with an access violation *inside*
`dinput8.dll` — the first `CreateDevice` after the swap crashed the game. Forwarding proxies
are what the Steam overlay does to the same three interfaces in this process, and they leave
the real objects byte-for-byte as DirectInput made them.

Everything runs on the game's main thread (device creation, the per-frame read and the frame
hook that queues events), so the 64-entry ring needs no locking. A second, different
`IDirectInput8`/keyboard implementation (another wrapper in a chain) is left alone with a
diagnostic rather than juggled.

The engine's keyboard is acquired **foreground-only** (`SetCooperativeLevel 0x0A`,
[../re/frame-hook.md](../re/frame-hook.md)): a background window's every read returns
`DIERR_NOTACQUIRED 0x8007000C`, which the wrap reports as `last read hr` in the full
`gamepad status` line. A real pad only queues events while the window IS foreground, so this
never bites a player; it bit the rig, whose windows never are (§6).

The engine's keyboard buffer is 0x100 entries deep and it is drained once per frame, so the
handful of transitions a pad produces per frame never approaches the capacity.

## 3. The pad (`XInputSource`) and the layout (`GamepadMapping.cs`)

`XInputGetState` is resolved at runtime from `xinput1_4.dll` → `xinput1_3.dll` →
`xinput9_1_0.dll`; a machine with none of them, or no pad, costs one failed probe per slot
every two seconds and nothing else. The first connected slot is the player's pad.

The pure half — tables and arithmetic, compiled into the test project — is
`GamepadMapping.cs`:

| Piece | What it does |
| --- | --- |
| `EngineKeyCodes.ToDik` | the engine's own key-code scheme (keymap.2da `character` / `[Keymapping]` values: A..Z = 51..76, 1..9 = 77..85, Space 87, Enter 88, Esc 31, Tab 30, F1..F6 39..44, arrows 9/10/7/8, …) → DIK |
| `GamepadKeymap.FromIni` | reads `swkotor.ini`'s `[Keymapping]` so a re-bound "move forward" (`Action280A`) still moves forward from the pad; unknown codes keep the default; `Overrides` lists what changed for the status line |
| `GamepadModeRule` | **Suspended** (pad off, window not foreground, or the `~` console open — a W would type a `w`), **Cursor** (main menu, a full-screen panel per `CGuiInGame+0x30`, a conversation, or the player's RS-click toggle), else **World** |
| `StickAxisLatch` | stick → held key with hysteresis (press at 0.5, release at 0.3) so a stick resting at the threshold does not chatter W down/up |
| `GamepadCursorRule` | radial dead zone 0.24 (XInput's own), squared response, 900 px/s at full deflection × `gamepad_cursor_speed`, frame time clamped to 0.1 s so a hitch cannot fling the cursor |
| `GamepadLayout` | the bindings below |
| `VirtualPadGrammar` | the `gamepad sim` argument grammar (§5) |

The layout follows the Xbox release of the game where it can:

| Pad | World (walking about) | Cursor (menus, panels, conversations) |
| --- | --- | --- |
| Left stick | walk (W/S) and turn (A/D), latched | cursor |
| Right stick | cursor | cursor (whichever stick is deflected further wins) |
| A | default action (R) | left click |
| B / Start | Escape (menu / close panel) | Escape |
| X | cancel combat (F) | right click |
| Y | switch character (Tab) | switch character |
| LB / RB | previous / next target (Q / E) | previous / next page (Q / E) |
| LT | pause (Space) | pause |
| RT | left click at the cursor | left click |
| D-pad | action slots 1-4 | up/down = scroll wheel (once per press), left/right = page |
| Back | map (M) | — |
| LS click | stealth (G) | — |
| RS click | force cursor mode on/off | same |

Every key above is the *engine action*, resolved through the player's keymap; the letters are
the shipped defaults.

## 4. The controller (`GamepadController`)

Runs from `PluginHost.OnFrame` before the epoch gate (so it works at the main menu too):

1. reads the pad (the virtual pad from `gamepad sim` when set, else XInput), decides the mode;
2. `GamepadLayout.Evaluate` fills a `GamepadIntent` (256 held-key flags, two mouse buttons,
   cursor axes, wheel notches);
3. diffs the held keys against last frame's and queues one `DirectInputKeyboardHook.Enqueue`
   per transition — a held key is ONE down event, not one per frame, exactly like a real key;
4. advances its own cursor by `GamepadCursorRule.Step`, clamps to `SCREEN_WIDTH/HEIGHT`
   (`0x0078D1D4/0x0078D1D8`), moves the real Windows cursor (`ClientToScreen` + `SetCursorPos`,
   so the mouse and the pad never fight over two positions) and posts `WM_MOUSEMOVE`;
5. posts `WM_LBUTTONDOWN/UP` / `WM_RBUTTONDOWN/UP` on button edges at the engine's own cursor
   (`CClientExoAppInternal+0x3C8/+0x3CC`), and `WM_MOUSEWHEEL` (screen coordinates, as that
   message takes) for a D-pad scroll.

Suspended releases everything the pad holds first (Escape/W are never left down when focus
goes), and a disconnect or `gamepad off` does the same. The window must be foreground for the
pad to do anything — the same `IsForeground` gate every other key reader in the plugin uses,
so two instances on one machine never both react. The virtual pad (§5) bypasses that gate and
the keyboard's acquire (it exists to be driven from outside the window), and re-activates the
engine's input if it finds it off (`CClientExoApp::SetInputActive 0x005EDA10`,
[mouse-cursor-input.md](mouse-cursor-input.md) §7) — on the rig the input was never off, so
that guard has not had to act yet.

**Clicks in the 3D view are two-step, like a mouse.** The world-click path
(`HandleMouseClickInWorld 0x00620350`, reached from `PerformLButtonUpAction 0x00620530`) works
on the object the engine has under the cursor (`CClientExoAppInternal+0x4A4`, re-picked every
frame from the GUI manager's copy of the cursor — so a *posted* cursor position is picked
exactly like a real one): a click on an object that is not the current target
(`+0x2B4`) **selects** it (`SetLastTarget 0x005F4A20`, the reticle and name appear); a click on
the selected object **runs its default action** (walk to it and open/talk/attack). Clicking
empty floor does nothing — KOTOR's PC build has no click-to-move — so from a pad the way to
"go there" is the left stick, and RT (or A in cursor mode) is for objects and buttons.

Text entry (the connect panel's address and password, chat, the console) needs a keyboard OR
the pad's own on-screen character grid (§7): the engine's edit boxes take characters through
`WM_CHAR`, a different path from everything above (which rides the buffered DirectInput read),
so it needed its own mechanism.

## 5. The `gamepad` console verb

Travels like every other verb: `ConsoleCommand.Parse` (`CoopCommandKind.Gamepad`, the
arguments as text, ≤ 31 characters), the agent's dispatcher, then the **cheat channel** (kind
74) back into the plugin, where `CheatController.Execute` hands the text to
`GamepadController.HandleConsole` *before* the no-player gate — so it works on the main menu
and with no world loaded, but it does need the agent (which the plugin starts itself). An
earlier plugin-local shortcut was dropped: `tools/blockpoke.ps1 console` feeds the agent's
parser, so a verb the agent did not know was unreachable from the rig.

The console reply is one cheat detail line — **63 characters** — so it carries the brief
form (`gamepad: on; virtual pad|XInput slot N|no pad; World|Cursor|Suspended; DI keyboard;
held N`); the full line (read counts, last read `hr` and shape, the cursor position and
whether the engine's input is active) goes to `kotorcoop-plugin.log` every time.

| Line | Effect |
| --- | --- |
| `gamepad status` | enabled/source/mode, held keys, mouse buttons, the DirectInput wrap (real reads / injected / pending / dropped / last diagnostic), cursor speed, keymap overrides |
| `gamepad on` / `off` | runtime toggle (off releases everything held) |
| `gamepad cursor` | toggle cursor mode, same as RS click |
| `gamepad sim lx= ly= rx= ry= lt= rt= a b x y lb rb start back ls rs up down left right` | a **virtual pad**: every field not named keeps its previous value, buttons bare or `=1`/`=0`. `gamepad sim ly=1` holds W; `gamepad sim ly=0` releases it |
| `gamepad sim off` | drop the virtual pad, back to XInput |

The virtual pad exists because the test rig has no physical controller and external input
never reaches the game: `tools/blockpoke.ps1 -ProcId <pid> console 'gamepad sim ly=1'`
followed by `where` twice is the unattended proof that the keyboard wrap works end to end.
While it is set the controller ignores the foreground gate and `DirectInputKeyboardHook`
answers the engine's failing (unacquired) keyboard reads with the queued events alone; both
switch back off once the pad is dropped and its releases have drained. Each `sim` line is one
agent round trip (~1.2 s), so a press and its release arrive on separate frames — fine for a
click, and the reason a stick deflection is timed with a `sleep` between two lines.

## 6. Live verification (2026-09-03)

Rig: host `C:/GOG Games/Star Wars KOTOR (GOG)`, save 1 (Endar Spire, `END_M01AA`),
`tools/demo.ps1 -Save 1`, every line through `tools/blockpoke.ps1 -ProcId <host> console`.
The host window was never foreground (the rig runs from a terminal), which is what the
virtual-pad bypasses in §5 are for.

| Check | Expected | Result |
| --- | --- | --- |
| Plugin log at start | `gamepad: enabled (cursor speed x1); XInput via xinput1_4.dll`, then `gamepad: IDirectInput8 proxied (real …, vtable …)` | **VERIFIED** |
| `gamepad status` | brief line in the console; the log's full line shows `DI: keyboard wrapped, N reads` with N climbing per frame | **VERIFIED** (`keyboard device proxied (real 0x5015694, vtable 0x6FDA65A0)`, 121 719 reads after ten minutes) |
| `gamepad sim ly=1` → `where` → `where` | the PC's position changes between the two `where`s (W is held through the engine's own keyboard read) | **VERIFIED** 20.0,22.2 → 11.4,18.2 |
| `gamepad sim off` | position stops changing; `status` shows 0 held keys | **VERIFIED** stopped at 10.5,15.6; `held 0` |
| Background window | with the virtual pad set, a queued event is delivered although every real read fails `DIERR_NOTACQUIRED` | **VERIFIED** (first attempt: 0 injected, 1 pending, `last read hr 0x8007000C` → `DeliverWhileUnacquired`) |
| `gamepad sim b` then `sim b=0` | Escape: a full-screen panel opens (mode flips to Cursor); repeat closes it (World) | **VERIFIED** |
| `sim rx=1` / `sim ry=…` | `CClientExoAppInternal+0x3C8/+0x3CC` follow the stick; small deflections (0.35 ≈ 45 px/s) position precisely; +ry moves the cursor UP | **VERIFIED** 400,299 → 799,0; then steered to 512,338 |
| Hover pick | with the cursor on the room's footlocker, `CClientExoAppInternal+0x4A4` holds its object id | **VERIFIED** `0x800000C6`, last target still `0x80000127` |
| `sim rt=1` / `rt=0` on the footlocker (first click) | selects it: `+0x2B4` becomes the hovered id, the name and reticle draw | **VERIFIED** `+0x2B4 = 0x800000C6`, "Footlocker" label on screen |
| second `sim rt=1` / `rt=0` | default action: the PC walks to it | **VERIFIED** `where` 20.0,22.2 → 12.6,22.0, standing at the locker |
| `sim rt` on empty floor / the PC itself | nothing (no click-to-move in this engine) | **VERIFIED** nothing, at four cursor positions — see §4 |
| `sim a` on a panel button in Cursor mode | the same posted `WM_LBUTTONDOWN/UP` pair the menu loader (`GameInput.ClickClient`) has driven the main menu with for months | NEEDS-LIVE as a pad check; the message path itself is VERIFIED ([mouse-cursor-input.md](mouse-cursor-input.md) §"The click dispatch chain") |
| Real keyboard and mouse | unaffected while the proxy is in place (the rig's own console typing kept working throughout) | **VERIFIED** |
| `gamepad = 0` | no proxy at all (no `IDirectInput8 proxied` line); `status` reports off | NEEDS-LIVE |
| A physical XInput pad | slot detection (`controller connected on XInput slot N`), the same `PadState` the virtual pad feeds | NEEDS-LIVE (none on the rig) |

Caveat carried over from [mouse-cursor-input.md](mouse-cursor-input.md): the engine's cursor
is in client pixels of the render window, which is only the same as the desktop when the
game runs at the desktop resolution or full-screen; `ClientToScreen` handles a window, and
the clamp uses the engine's own `SCREEN_WIDTH/HEIGHT`, so a mismatch only affects where the
Windows cursor is drawn, never where the engine thinks it is.

A physical XInput pad has not been plugged into the rig; the XInput path is the documented
struct layout (`XINPUT_STATE`: packet number, then `XINPUT_GAMEPAD` = buttons u16, two u8
triggers, four i16 thumbs) and the same `PadState` the virtual pad feeds, so everything
downstream of `TryRead` is covered by the virtual-pad checks above.

The first two hours of the click check were spent clicking the floor under the player and
concluding the click path was broken; it was the test that was wrong (§4: no click-to-move).
Read `+0x4A4` first — if the engine has nothing under the cursor, a click has nothing to do.

## 7. Text entry: the on-screen character grid (2026-09-24)

The one gap §4 called out: the address box (`docs/research/server-browser.md` §"The address
box"), the password box, chat (`Enter`, `Odyssey.OpenChatInput`) and the `~` console all take
typed characters through Win32 `WM_CHAR` —

- editbox: `app handler 0x004024CE → CSWGuiManager::HandleKeyPress 0x0040B2A0 → focused
  control vtbl +0x9C = CSWGuiEditbox::HandleKeyPress 0x004184F0`, appending via `AddNewChar
  0x004163A0` / backspacing via `RemoveLastChar 0x004162E0` (`docs/research/connect-flow.md`
  §"Real editable text control", VERIFIED disassembly);
- console: `HandleWMCharMessage 0x00402240 → DoConsole 0x00401F00`, `bl` = the typed char —
  `0x0D`/`0x0A` submit, `0x1B` toggles open/closed, `0x08` backspaces, else appends
  (`docs/research/console-open-safe.md` §"Typed chars reach the buffer", VERIFIED disassembly).

That is a **different path from everything else in this file**: movement, menu keys and clicks
all ride the buffered DirectInput read (`DirectInputKeyboardHook`) or posted mouse messages: a
fake DIK key-down never reaches either handler above, because neither is fed by DirectInput.
The pad therefore cannot "type" by pretending to be a keyboard key — it needs to drive the
engine through the SAME channel a real keystroke does.

### Design

**An on-screen character grid, driven entirely by posted `WM_CHAR`.** No new engine call
addresses at all: typing a character is `PostMessage(gameWindow, WM_CHAR, asciiChar, 1)` — the
same in-process, main-thread-landing `PostMessage` trick already proven for the mouse
(`docs/research/mouse-cursor-input.md` §"Recipe A"; `GamepadController.ApplyCursor` /
`ApplyMouseButtons`), aimed at a window message instead of `WM_MOUSEMOVE`/`WM_LBUTTONDOWN`.
`PostMessageA` (already imported) makes `wParam`'s low byte the ANSI character code, exactly
what `DoConsole`'s `bl` and the editbox's `AddNewChar` read — no encoding conversion needed.

Layout (`GamepadTextEntry.cs`, pure, compiled into the test project like `GamepadMapping.cs`):

| Piece | What it does |
| --- | --- |
| `GamepadTextGrid` | 5 rows: digits, `qwertyuiop`, `asdfghjkl:`, `zxcvbnm,.'`, `-_@?!/` (ragged — the last row is shorter). Letter rows have a lower/upper variant; digits and punctuation are shift-invariant. No Backspace/Space *cell* — those are dedicated buttons, same as a real keyboard. |
| `GamepadTextGridCursor` | Row/column, clamped vertically (never wraps past row 0 or the last row, and clamps the column when a shorter row is entered), wraps horizontally at the CURRENT row's own length. |
| `GamepadTextEntryStateMachine` | Open/Close, the cursor, the shift flag, and `Step(input) -> command`: one frame of edge-triggered intents in, one of `{None, Type(char), Backspace, Space, Submit, Cancel}` out. `Submit`/`Cancel` close it (`Active = false`); the controller re-opens it next frame if the box is still there (e.g. an address the grammar rejected — a fresh grid on re-open is the trade made for never having to track "is this the same box as last frame"). |
| `RenderRow(row)` | One display line: cells space-separated, the selected cell in `[brackets]` — the only "cursor" the plain-ASCII queued-text renderer can show (`docs/research/console-overlay.md`: no per-character colour, no newlines). Longest line (10 letters) is 29 characters, well under the queued-text budget. |

Pad mapping (`GamepadController.TextEntry.cs`, the impure half): D-pad presses are
edge-triggered like every other button here; the left stick ALSO navigates, through the same
`StickAxisLatch` hysteresis §3 uses for walking, compared frame-to-frame so a push moves the
selection once per threshold crossing rather than once per held frame.

| Pad | Action |
| --- | --- |
| D-pad / left stick | Move the grid selection |
| A | Type the selected character |
| B | Backspace |
| X | Space |
| Y or Start | Confirm (send) |
| Back | Cancel |
| LB | Toggle shift (upper case) |

**Confirm/Cancel do NOT both go through `WM_CHAR`.** The console's own submit (`0x0D`/`0x0A`)
and close (`0x1B`) are VERIFIED disassembly (`console-open-safe.md`), so a posted `WM_CHAR`
there runs exactly the engine's own path. The modal's Enter-in-editbox routing, by contrast, is
explicitly flagged `NEEDS-LIVE` and unrun in `connect-flow.md` ("Enter-in-editbox routing to
the ..."), so Confirm/Cancel on the address/password panel instead call
`PasswordEntryModal.ConfirmActive()` / `CancelActive()` — new static entry points that run
**the exact same code** the OK/Cancel buttons' own click handlers already run
(`OnOkClicked`/`OnCancelClicked` were refactored to call the same `SubmitTyped()`/
`CloseVisual()` instance methods), so a pad's "send" is provably identical to a mouse click on
OK, not a parallel, unverified path.

**Gating — why this cannot leak into normal play.** `GamepadController.OnFrame` only reaches
the grid when `foreground && (Odyssey.IsConsoleOpen || PasswordEntryModal.IsAnyOpen)` — the
SAME foreground gate every other pad action already uses, checked BEFORE `GamepadModeRule`
(which only ever forced `Suspended` for an open console; it has no idea about the modal). Both
conditions are the caller's own, already-live open flags, re-read every frame — nothing latches
"has focus" independently, so the grid can never survive past the box it was drawn for.
`PasswordEntryModal.IsAnyOpen` excludes an instance mid-`CloseVisual` (`_closing`) on purpose:
without that, Confirm/Cancel would set the grid inactive for one frame and then see the panel
"still open" (it is still registered for a frame or two while the sweep runs) and re-open it,
flashing the grid back on right as the panel is closing.

**Not a new input source, so no new double-drive risk.** The grid reads the SAME per-frame
`PadState` (XInput or the virtual pad) `GamepadController.OnFrame` already read this frame —
nothing new is polled, no new slot is opened. Whatever already stopped a background window or
a second instance from double-acting (the `IsForeground` gate, the single first-connected
slot, the virtual pad's exclusivity while set) applies unchanged; only the MEANING of a press
changes while a text box has focus, the same way World mode's buttons already mean something
else in Cursor mode.

**No new coop.ini key.** The existing `gamepad` toggle already gates the whole layer, including
text entry — `gamepad = 0` turns this off along with everything else, same as before.

### Live/rig verification — NEEDS-LIVE

No new console verb: `gamepad sim` already drives a virtual pad through the SAME
`GamepadController.OnFrame` path (§5), so it drives the grid too — the recipe below needs no
physical pad, same as §6's original checks.

```
tools/blockpoke.ps1 -ProcId <pid> console 'browse'                    # open the server browser
tools/blockpoke.ps1 -ProcId <pid> console 'gamepad sim down'          # ... etc to reach the address row
tools/blockpoke.ps1 -ProcId <pid> console 'gamepad sim a'             # Connect on it -> the address panel opens
tools/blockpoke.ps1 -ProcId <pid> console 'gamepad sim right'         # move the grid onto '2' (row0 col1)
tools/blockpoke.ps1 -ProcId <pid> console 'gamepad sim a'             # type '2'
tools/blockpoke.ps1 -ProcId <pid> console 'gamepad status'            # reply + log: "text entry: target address, row 0 col 1 ..."
tools/blockpoke.ps1 -ProcId <pid> console 'gamepad sim back'          # Cancel: panel closes, browser stays
```

| Check | Expected |
| --- | --- |
| `browse` then D-pad/stick to the address row, `sim a` | The address panel opens (as it already does for a mouse click on the row — proven in `server-browser.md`), and `kotorcoop-plugin.log` starts logging `text entry: target address, row 0 col 0 shift False, selected '1'` every `gamepad status`. |
| `sim right` × N then `sim a` | Each `sim a` posts one `WM_CHAR`; the panel's own live text (already rendered by the engine, NOT by this feature) grows by one correct character, and the caret stays after it — the SAME rendering `server-browser.md`'s address-box pass already watched work for a keyboard. |
| `sim b` | The panel's text shortens by one character (backspace). |
| `sim back` | The panel closes (Cancel), the browser stays open, exactly like a mouse click on Cancel — because `PasswordEntryModal.CancelActive()` runs the identical `CloseVisual()`. |
| Full address then `sim y` (or `sim start`) | `ConfirmActive()` runs `SubmitTyped()`: a valid address closes the panel and connects (`server-browser.md`'s existing NEEDS-LIVE checklist item 4); an address the grammar rejects re-captions the title and stays open, exactly as a mouse-clicked OK already does. |
| In a loaded module, `sim start` at the pad's own confirm mapping does NOT open chat | Confirm here means the GRID's confirm, not `Action280A`; opening chat is still `Enter` on a real keyboard or `ChatInputController`'s own edge-detect — the grid only exists once a box already has focus. |
| Press `Enter` for real (or `Odyssey.OpenChatInput`), then `sim a` on a letter | Posts `WM_CHAR` into the `say ` line; `sim start` posts `WM_CHAR 0x0D`, which is DoConsole's own VERIFIED submit — the line should reach the co-op layer exactly like a typed chat message. |
| `sim` a button while walking, no box open | Nothing happens to movement/menus (unchanged from before this feature); no grid is drawn (no `text entry:` lines in the log). |
| Alt-tab away while the grid is up | Same as the existing password-ask Alt-tab check (`connect-flow.md`): the panel/console closes on its own, and the NEXT frame's `IsAnyOpen`/`IsConsoleOpen` read false, so the grid closes with it (`_textEntry.Close()` in the "target vanished" branch). |

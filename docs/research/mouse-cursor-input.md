# The engine's mouse cursor: where it lives, how it moves, how to click (research 2026-07-31)

Static RE of retail **KOTOR 1 (GOG 1.0.3)** `swkotor.exe` — disk == memory, image base `0x400000`,
no ASLR, so every address below is both a file offset (via section map) and a live VA. Nothing was
modified; this is a read-only investigation of the binary plus `gui.bif`'s `mainmenu*.gui`.

Tag key: **VERIFIED** = read out of the binary here · **INFERRED** = deduced from verified code,
not yet run · **NEEDS-LIVE** = confirm against the running game before shipping.

---

## ⚠ CORRECTION to the working assumption

> "KOTOR does NOT use the OS pointer: it accumulates DirectInput RELATIVE deltas into an internal
> cursor clamped to the client area, with a sensitivity multiplier."

**That is wrong, and it is why the injected clicks were mis-diagnosed.** VERIFIED:

* The cursor position comes from **Windows mouse messages** (`WM_MOUSEMOVE`, `WM_L/R/MBUTTON*`),
  read straight out of `lParam` in the game's own window procedure
  `Global::WinMessageHandler 0x00402800`. There is no accumulator and no sensitivity multiply on
  the cursor path.
* The DirectInput device is acquired **non-exclusive / foreground**
  (`SetCooperativeLevel(hwnd, 0xA)` at `0x005E3FDE`), and its `DIMOUSESTATE` is polled by exactly
  one function, `CExoInputInternal::UpdateMouseDelta 0x005E0110`, whose only outputs are two
  **floats** at `CExoInputInternal+0x3A0/+0x3A4`. Those are `-lX/100.0f` and `-lY/100.0f` clamped
  to `[-1.0, +1.0]` (`0x007A2278` = `100.0f`, `0x0073D764` = `1.0f`, `0x00740CBC` = `-1.0f`) —
  **camera / look axes, not the cursor**. The wheel `lZ` is discarded here (the wheel arrives as
  `WM_MOUSEWHEEL` instead).
* The engine itself calls **`SetCursorPos`** when it wants to move the cursor
  (`CExoInputInternal::SetMousePos 0x005E0960` → `ClientToScreen` `[0x0073D3C4]` →
  `SetCursorPos` `[0x0073D3C8]`). It cooperates with the OS pointer; it does not fight it.

So `SendInput`-style injection *can* work in principle — but see
[§7 Why the current clicks do nothing](#7-why-the-current-clicks-do-nothing) for the two real gates
that were being tripped, and [§6](#6-recommended-click-at-xy-recipes) for two ways that do not
depend on the physical pointer at all.

---

## 1. Answer sheet (skip to §6 for the recipe)

| Thing | Address / chain | Type | Tag |
| --- | --- | --- | --- |
| **GUI cursor X** (what hit-tests use) | `*(int*)(GUI_MGR + 0x00)`, `GUI_MGR = *(void**)0x007A39F4` | `int`, client px, left-origin | VERIFIED |
| **GUI cursor Y** | `*(int*)(GUI_MGR + 0x04)` | `int`, client px, **top-down** | VERIFIED |
| **Authoritative cursor X** (re-pushed every frame) | `*(int*)(CLIENT + 0x3C8)` | `int`, client px | VERIFIED |
| **Authoritative cursor Y** | `*(int*)(CLIENT + 0x3CC)` | `int`, client px, **top-down** | VERIFIED |
| **Raw pending X** (this frame's WM value) | `*(int*)(EXOINPUT_INT + 0x37C)`, `-1` = none pending | `int`, client px | VERIFIED |
| **Raw pending Y** | `*(int*)(EXOINPUT_INT + 0x380)`, `-1` = none pending | `int`, **bottom-up** px | VERIFIED |
| **Left button down** | `*(int*)0x008338F4` | `int` 0/1 | VERIFIED |
| **Right button down** | `*(int*)0x008338F0` | `int` 0/1 | VERIFIED |
| **GUI's own "LMB is down"** | `*(int*)(GUI_MGR + 0x1C) & 1` | bit | VERIFIED |
| **Control under cursor** | `*(void**)(GUI_MGR + 0x08)` | `CSWGuiControl*` | VERIFIED |
| **Mouse-captured control** | `*(void**)(GUI_MGR + 0x10)`, button id at `+0x0C` (1 = left) | `CSWGuiControl*` | VERIFIED |
| **Game HWND** | `*(HWND*)0x007A39D8` | `HWND` | VERIFIED |
| Screen (client) size | `0x0078D1D4` / `0x0078D1D8` | `int` | VERIFIED |

Pointer chains (all VERIFIED):

```c
#define G_APP_MANAGER 0x007A39FC
#define G_GUI_MANAGER 0x007A39F4   /* CSWGuiManager*  (== CLIENT+0x274) */
#define G_EXO_INPUT   0x007A39E4   /* CExoInput*                        */
#define G_HWND        0x007A39D8

void* gui    = *(void**)G_GUI_MANAGER;
void* input  = *(void**)((char*)*(void**)G_EXO_INPUT + 4);          /* CExoInputInternal   */
void* app    = *(void**)((char*)*(void**)G_APP_MANAGER + 4);        /* CClientExoApp       */
void* client = *(void**)((char*)app + 4);                           /* CClientExoAppInternal */
```

The symbol DB agrees on the two that it knows: `CSWGuiManager +0x000 mouse_x / +0x004 mouse_y`,
`CClientExoAppInternal +0x3C8 mouse_x / +0x3CC mouse_y`.

---

## 2. Q1 — `CSWGuiControl::HitCheckMouse 0x004187C0`, and where its X/Y come from

Full disassembly (VERIFIED, `__thiscall`, `ret 8`):

```
0x004187C0  mov  eax, ecx                    ; eax = control
0x004187C2  mov  ecx, [eax+0x44]             ; flags
0x004187C5  test cl, 2       / je  miss      ; bit1 (0x2) must be SET   -> visible
0x004187CA  test cl, 0x20    / jne miss      ; bit5 (0x20) must be CLEAR -> not locked
0x004187CF  mov  ecx, [esp+4]                ; <-- ARG 1 = mouse X
0x004187D3  cmp  ecx, [eax+0x04] / jl  miss  ; x >= LEFT
0x004187D8  mov  edx, [eax+0x0C]
0x004187DB  add  edx, [eax+0x04]
0x004187DE  cmp  ecx, edx    / jg  miss      ; x <= LEFT+WIDTH   (inclusive!)
0x004187E2  mov  ecx, [esp+8]                ; <-- ARG 2 = mouse Y
0x004187E6  cmp  ecx, [eax+0x08] / jl  miss  ; y >= TOP
0x004187EB  mov  edx, [eax+0x10]
0x004187EE  add  edx, [eax+0x08]
0x004187F1  cmp  ecx, edx    / jle hit       ; y <= TOP+HEIGHT   (inclusive!)
      miss: xor eax, eax
       hit: ret 8                            ; returns `this` on hit, NULL on miss
```

**The mouse coordinates are plain `int` function arguments — no global is read here.** Extent is
four `int`s at `control+0x04 LEFT`, `+0x08 TOP`, `+0x0C WIDTH`, `+0x10 HEIGHT` (matches
`menu-button-runtime.md`). Note both upper bounds are **inclusive**.

### Following the arguments up to real storage

```
CClientExoAppInternal::ProcessInput 0x006227E0          (once per frame, main loop)
  └─ per input event: HandleInputEvent, then unconditionally:
     0x00622C8A  mov ecx, [esi+0x3CC]     ; CLIENT.mouse_y
     0x00622C90  mov edx, [esi+0x3C8]     ; CLIENT.mouse_x
     0x00622CA6  call CSWGuiManager::HandleMouseMove 0x0040C1E0 (x, y)
                    0x0040C1F6  mov [esi],   ebx   ; GUI_MGR+0x00 = x     <-- STORAGE
                    0x0040C1F8  mov [esi+4], edi   ; GUI_MGR+0x04 = y     <-- STORAGE
                    ... software-cursor position, tooltip reset ...
                    0x0040C274  call CSWGuiManager::HitCheckMouse 0x0040ABE0 (x, y, &panel, &ctl, 0)
                                  └─ panel->vtbl[0x40] = CSWGuiPanel::HitCheckMouse 0x0040B690 (x, y)
                                       └─ ctl->vtbl[0x44] = CSWGuiControl::HitCheckMouse 0x004187C0 (lx, ly)
```

So there are **three** copies, in dependency order:

1. `CExoInputInternal+0x37C/+0x380` — the raw value taken from the last window message
   (`+0x380` is bottom-up), consumed and reset to `-1` each `GetEvents`.
2. `CClientExoAppInternal+0x3C8/+0x3CC` — **the durable, authoritative cursor** (`int`, top-down).
   Only written when a mouse-position input event arrives.
3. `CSWGuiManager+0x00/+0x04` — the GUI's working copy, re-derived from (2) on every input event.

`CSWGuiPanel::GetLocalMouseCoords 0x0040BA20` is the engine's own public reader and it reads
`0x007A39F4 → +0x00 / +0x04` — confirming (3) is *the* "current mouse position" for GUI code.
**VERIFIED.**

---

## 3. Q2 — how the position is written each frame (no accumulation, no clamp on the hot path)

The whole chain, VERIFIED:

**(a) Window procedure.** `Global::WinMessageHandler 0x00402800` is the registered `lpfnWndProc`
(written into the `WNDCLASSA` at `0x00403755`, `RegisterClassA` at `0x00403769`; the resulting
`HWND` is stored to `0x007A39D8` at `0x004037E3`).

Message dispatch (byte table `0x0040355C`, jump table `0x00403538`, index = `msg - 0x112`):

| msg | handler | note |
| --- | --- | --- |
| `0x200` WM_MOUSEMOVE | `0x00402E44` | position only |
| `0x201` WM_LBUTTONDOWN | `0x00402E98` | position **+** button |
| `0x202` WM_LBUTTONUP | `0x00402F73` | |
| `0x203` WM_LBUTTONDBLCLK | `0x00402EF0` | |
| `0x204/0x205/0x206` RBUTTON | `0x0040306A` / `0x004030FA` / `0x00402FDB` | |
| `0x207` WM_MBUTTONDOWN | `0x004031AC` | |
| `0x20A` WM_MOUSEWHEEL | `0x00403244` | `ScreenToClient` first (wheel lParam is screen-space) |

Every one of them packs the position identically (this is `0x00402E5F`, the `WM_MOUSEMOVE` case;
`0x004030B9` in the LBUTTONDOWN case is byte-identical):

```
eax = [0x0078D1D8]            ; SCREEN_HEIGHT
edx = lParam >> 16            ; client Y, top-down
eax = SCREEN_HEIGHT - edx - 1 ; -> bottom-up Y
ecx = (word)eax << 16 | (word)lParam   ; packed
ev[+0x04] = message ; ev[+0x08] = wParam ; ev[+0x0C] = packed(x, bottomUpY)
CExoInput::BufferEvent 0x005DF590(&ev)
```

**(b) Buffering.** `CExoInputInternal::BufferEvent_2 0x005E12A0` switches on `ev[+0x04] - 0x200`
(jump table `0x005E170C`):

* `WM_MOUSEMOVE` → `0x005E12C2`: if the *self-move latch* `internal+0x384` is set, swallow the
  message and clear the latch; otherwise
  `internal+0x37C = (short)ev[0xC]` (X) and `internal+0x380 = (short)ev[0xE]` (bottom-up Y).
* `WM_LBUTTONDOWN` → `0x005E1302`: buffers **three** device events in order —
  `(device 1, control 3, value = x)`, `(device 1, control 4, value = bottomUpY)`, then
  `(device 1, control 0, value = 1)` — and sets `+0x37C/+0x380 = -1` so the tail-end emitter
  below does not duplicate them. Constants: `0x0074D3CC = 1` (mouse device),
  `0x0074D3E0/E4/E8 = 0/1/2` (L/R/M button), `0x0074D3EC/F0/F4 = 3/4/5` (X / Y / wheel).

**(c) Emission.** `CExoInputInternal::GetEvents 0x005E24E0`, after draining the device buffers:

```
0x005E2A6B  mov ebx, [esi+0x37C] ; cmp ebx,-1 ; je skip  -> emit (dev 1, ctl 3, value=x)
0x005E2AE6  mov ebp, [esi+0x380] ; cmp ebp,-1 ; je skip  -> emit (dev 1, ctl 4, value=y)
0x005E2C7C  or  eax, -1
0x005E2C7F  mov [esi+0x37C], eax
0x005E2C85  mov [esi+0x380], eax                          ; reset both to -1
```

**(d) Application — the only clamp in the whole path.**
`CClientExoAppInternal::HandleInputEvent 0x00621210` (jump tables `0x00622128`, byte table
`0x006221FC`, jump table `0x00622154`; index = `type - 0x19`) maps event types:

| event type | handler | meaning |
| --- | --- | --- |
| `0x3D` | `0x0062125B` → `SetMouseX 0x0061F7E0` | absolute mouse X |
| `0x3E` | `0x00621273` → `SetMouseY 0x0061F830` | absolute mouse Y (bottom-up input) |
| `0x3F` | `0x0062128B` → `PerformLButtonDownAction 0x0061F880` / `PerformLButtonUpAction 0x00620530` | left button |
| `0x40` | `0x006212C2` → `PerformRButtonDownAction 0x0061F940` / `…UpAction 0x0061F960` | right button |
| `0x41` | `0x006212EC` → `CSWGuiManager::HandleZMouse 0x0040C650` | wheel |

```
CClientExoAppInternal::SetMouseX 0x0061F7E0(int x):        ; __thiscall, ret 4
    if (this->0x3C8 == x) return 0;                        ; no-op if unchanged
    this->0x3C8 = x;
    if (x < 0) this->0x3C8 = 0;                            ; clamp low
    if (x >= SCREEN_WIDTH-1) this->0x3C8 = SCREEN_WIDTH-1; ; clamp high
    return 1;

CClientExoAppInternal::SetMouseY 0x0061F830(int yBottomUp): ; __thiscall, ret 4
    int y = SCREEN_HEIGHT - yBottomUp - 1;                 ; <-- FLIP to top-down
    if (this->0x3CC == y) return 0;
    this->0x3CC = clamp(y, 0, SCREEN_HEIGHT-1);
    return 1;
```

### Consequence for us — **the position is assignment, never increment**

Nothing anywhere increments `+0x3C8/+0x3CC`. A frame with no mouse message produces no `0x3D/0x3E`
event, so `SetMouseX/Y` are not called and the stored value simply persists. **Writing
`CLIENT+0x3C8/+0x3CC` therefore sticks indefinitely** — until the user physically moves the mouse
(or a `WM_*BUTTON*` message re-supplies a position). `ProcessInput` re-pushes those two ints into
`GUI_MGR+0x00/+0x04` after every input event, so the write also *propagates* on its own as soon as
any input happens. **VERIFIED.**

Caveat (VERIFIED): `ProcessInput` skips the `HandleMouseMove` call when
`RMB is held && CLIENT+0x9C != 2`, or when `CLIENT+0x9C == 4` (`0x00622C70`–`0x00622C88`).
`CLIENT+0x9C` is the mouse mode (`CClientExoAppInternal::SetMouseMode 0x005F49B0`). On the main
menu it is a GUI mode and the call runs.

Two engine helpers exist and are worth knowing:

* `CClientExoAppInternal::RestoreMousePositionTo 0x005F4EA0` — `__thiscall(int x, int y)`, `ret 8`:
  raw `this->0x3C8 = x; this->0x3CC = y;` with **no clamp and no flip**. Top-down px in, top-down
  px stored. This is the cleanest "teleport the authoritative cursor" call.
* `CSWGuiManager::MoveMouseToPosition 0x0040C790` — `__thiscall(int x, int y)`, `ret 8`:
  sets `GUI_MGR+0/+4`, calls `CExoInput::SetMousePos(x, SCREEN_HEIGHT-1-y)` (which does
  `ClientToScreen` + **`SetCursorPos`** and arms the self-move latch `internal+0x384` so the
  resulting `WM_MOUSEMOVE` is swallowed), then `HandleMouseMove(x, y)`. It does **not** touch
  `CLIENT+0x3C8/+0x3CC`, so on its own it is reverted by the next `ProcessInput`.

---

## 4. Q3 — button state and the click dispatch

### Where the button state lives (VERIFIED)

* `DIMOUSESTATE.rgbButtons` is **never read** — `GetDeviceState` is called only from
  `UpdateMouseDelta 0x005E0110` with `cbData = 0x10`, and that function reads only `lX/lY/lZ`.
  The button state is entirely `WM_*BUTTON*`-driven.
* `int` at **`0x008338F4`** — set to `1` at `0x0061F88B` (`PerformLButtonDownAction`), `0` at
  `0x0062053B` (`PerformLButtonUpAction`). **This is the left-button-down flag.**
* `int` at **`0x008338F0`** — same for the right button (`0x0061F946` / `0x0061F966`).
* `GUI_MGR+0x1C` bit0 — the GUI's own latch, set/cleared by
  `CSWGuiManager::HandleLMouseDown/Up`.
* `CExoInputInternal+0x3AC` (`CExoInput::GetLastSetMouseDownState 0x005DF730`) is the *cursor
  bitmap* down-state used by `SetMouseCursor 0x005E0000` — a rendering detail, not input.

### The click dispatch chain (VERIFIED, top to bottom)

```
event 0x3F, value != 0
  -> CClientExoAppInternal::PerformLButtonDownAction 0x0061F880(int arg)
       0x008338F4 = 1
       CExoInput::RefreshMouseCursor(1)
       CSWGuiManager::SetSoftwareMouseCursor(...)
       (mouse-mode / world-vs-GUI gates on CLIENT+0x9C)
       0x0061F929  call CSWGuiManager::HandleLMouseDown 0x0040C570(1)
                     GUI_MGR+0x1C bit0 = 1
                     if (GUI_MGR+0x10 && GUI_MGR+0x0C == 1) return 0     ; already captured
                     UpdateMouseOverControl 0x0040B300
                     ctl = GUI_MGR+0x08                                   ; hovered control
                     ctl->vtbl[0x18]()   -> CSWGuiControl::HandleLMouseDown 0x0041AB90
                                              CSWGuiManager::CaptureMouse 0x0040A1C0(ctl, 1)
                                                GUI_MGR+0x10 = ctl ; GUI_MGR+0x0C = 1

event 0x3F, value == 0
  -> CClientExoAppInternal::PerformLButtonUpAction 0x00620530()
       0x008338F4 = 0
       0x006205FF  call CSWGuiManager::HandleLMouseUp 0x0040A170()
                     ctl = GUI_MGR+0x10 ; if (!ctl || GUI_MGR+0x0C != 1) { clear bit0; return 0; }
                     ctl->vtbl[0x1C]()  -> CSWGuiControl::HandleLMouseUp 0x0041ABD0
                        if (GUI_MGR+0x1C & 1) ctl->vtbl[0x3C](0x1F9, 1)   ; "generic click" notify
                        if (ctl->0x44 & 0x8) ctl->vtbl[0x3C](0x27,  1)    ; <<< THE CLICK EVENT
                        CSWGuiManager::ReleaseMouse 0x0040A200(ctl)
                        tail-jmp ctl->vtbl[0x14]
                     GUI_MGR+0x1C &= ~1
```

**Event `0x27` is the click.** `CSWGuiButton::HandleInputEvent 0x0041AD40` gates on
`code == 0x27 && value != 0`, then runs the control's bound handler list via `control+0x34` and
plays the click sound (`CSWGuiManager::PlayGuiSound 0x0040A140`). This matches what
`connect-flow.md` / `menu-button-runtime.md` already use for `AddEvent 0x0041AB20(ctl, 0x27, …)`.

Vtable slots on `CSWGuiControl` used above (VERIFIED): `+0x0C` set-highlight, `+0x14` release,
`+0x18` `HandleLMouseDown`, `+0x1C` `HandleLMouseUp`, `+0x3C` `HandleInputEvent(int code,int val)`,
`+0x40` panel-level `HitCheckMouse(x,y)`, `+0x44` control-level `HitCheckMouse(x,y)`.

Note `control+0x44` bit3 (`0x8`) must be set for the `0x27` fire — the working main-menu buttons
have flags `0x0E` (bits 1,2,3), so that is satisfied.

### A free read-only probe

`CSWGuiManager::HitCheckMouse 0x0040ABE0` — `__thiscall`, `ret 0x14`:

```c
int __thiscall HitCheckMouse(void* gui, int x, int y,
                             CSWGuiPanel** outPanel, CSWGuiControl** outControl, int unused);
```

Returns non-zero on a hit and fills the two out-pointers (modal stack first, then the panel list
back-to-front). It walks exactly the same path the engine uses, so **call it to validate a
coordinate before clicking, and to identify what is under a point.** It writes nothing. This is the
cheapest possible sanity check and it is safe to call every frame.

---

## 5. Q5 — coordinate space (cross-checked against `mainmenu8x6.gui`)

**The stored cursor is in raw client pixels, top-down, origin top-left.** Not 640×480 virtual, not
normalised. VERIFIED by three independent facts:

1. `SetMouseX` clamps to `[0, SCREEN_WIDTH-1]` where `SCREEN_WIDTH 0x0078D1D4` is the client width.
2. `SetMousePos` treats the pair as client coords (`ClientToScreen` before `SetCursorPos`).
3. `WinMessageHandler` takes them verbatim from `lParam` of a client-relative mouse message.

The Y flip only exists on the *wire*: `WinMessageHandler` and `CExoInputInternal+0x380` carry
`SCREEN_HEIGHT-1-clientY` (bottom-up); `SetMouseY` flips it back, so everything from
`CLIENT+0x3CC` onward — including `GUI_MGR+0x04` and both `HitCheckMouse` levels — is top-down.

### How a panel maps that onto `.gui` extents

`CSWGuiPanel::HitCheckMouse 0x0040B690` branches on **panel flag `+0x44` bit0**:

* **bit0 CLEAR** (the ctor default — `CSWGuiPanel::Constructor 0x0040B570` does
  `flags = (flags & 0xFFFFF89C) | 0x84`): the incoming mouse is converted to a **640×480 canvas
  centred in the client area** —
  `x' = x - (SCREEN_WIDTH-640)/2`, `y' = y - (SCREEN_HEIGHT-480)/2` (`0x0040B6BD`–`0x0040B6EB`).
* **bit0 SET**: the panel extent is absolute; `GetExtentAccountingForPanelOffset 0x0040AA00`
  applies the anchor bits (`0x8` = centre panel on screen, `0x20` = centre horizontally against
  640, `0x40` = centre vertically against 480) and the **mouse is used raw**.

Then `local = mouse - panelExtent.LEFT/TOP` and each control is tested with `local`.
`CSWGuiPanel::GetLocalMouseCoords 0x0040BA20` implements the same two branches and is the function
to mirror if you ever need panel-local coordinates.

### The main menu specifically — **absolute, identity at 800×600**

* `CSWGuiMainMenu::LoadFromLayout 0x0067ACE0` calls `StartLoadFromLayout 0x0040A680` and then, at
  **`0x0067AD1C`, does `or dword ptr [esi+0x44], 1`** — it explicitly sets bit0. **VERIFIED.**
* `mainmenu8x6.gui`'s root panel `EXTENT` is `LEFT 0, TOP 0, WIDTH 800, HEIGHT 600`, and none of
  the anchor bits `0x8/0x20/0x40` are set by the ctor. So at an 800×600 client the panel origin is
  `(0,0)` and **control-local coordinates == client pixels == the numbers in the `.gui` file.**

Cross-check with the values in the brief:

| control (from `mainmenu8x6.gui`) | LEFT/TOP/W/H | hit region in client px @ 800×600 | click point |
| --- | --- | --- | --- |
| `BTN_NEWGAME` | 368, 280, 235, 24 | x ∈ [368, 603], y ∈ [280, 304] | (485, 292) |
| `BTN_LOADGAME` | 368, 310, 235, 24 | x ∈ [368, 603], y ∈ [310, 334] | **(485, 322)** |
| `BTN_MOVIES` | 367, 341, 235, 24 | x ∈ [367, 602], y ∈ [341, 365] | **(484, 353)** |
| `BTN_OPTIONS` | 368, 372, 235, 24 | x ∈ [368, 603], y ∈ [372, 396] | (485, 384) |
| `BTN_EXIT` | 368, 402, 235, 24 | x ∈ [368, 603], y ∈ [402, 426] | (485, 414) |
| `BTN_WARP` (our Coop button) | 368, 443, 235, 29 | x ∈ [368, 603], y ∈ [443, 472] | (485, 457) |

Bounds are inclusive on both ends (`jg` / `jle`), so the region is `[L, L+W]` × `[T, T+H]`.

**Resolution caveat (INFERRED / NEEDS-LIVE).** `mainmenu8x6`, `mainmenu10x7`, `mainmenu12x9` and
`mainmenu16x12` all ship the *same* 800×600 panel extent and the *same* control coordinates
(verified by reading all four out of `gui.bif`), and `CSWGuiManager::SetSize 0x0040BE70` is called
with the real `SCREEN_WIDTH/HEIGHT` (`0x005F19DE`, `0x005F93FA`). With bit0 set and no anchor bits,
that makes the main menu a top-left-anchored 800×600 block at higher resolutions. **Do not hardcode
the table above.** Compute the click point at runtime instead:

```c
/* panel = the CSWGuiMainMenu; ctl = the control (e.g. mainMenu+0x1248 for BTN_WARP) */
int ext[4];                                   /* {L, T, W, H} absolute panel extent */
((void(__thiscall*)(void*,int*))0x0040AA00)(panel, ext);   /* GetExtentAccountingForPanelOffset */
int cx = ext[0] + ((int*)ctl)[1] + ((int*)ctl)[3] / 2;     /* ctl+0x04 LEFT, +0x0C WIDTH  */
int cy = ext[1] + ((int*)ctl)[2] + ((int*)ctl)[4] / 2;     /* ctl+0x08 TOP,  +0x10 HEIGHT */
```
(That is only valid when the panel has bit0 set, which the main menu does. For a bit0-clear panel
add `(SCREEN_W-640)/2` and `(SCREEN_H-480)/2` to `cx/cy`.) Then **verify with
`CSWGuiManager::HitCheckMouse(cx, cy, &p, &c, 0)` and assert `c == ctl` before clicking.**

---

## 6. Q4 — Recommended "click at (x,y)" recipes

Ranked by crash risk, lowest first.

### Common preamble

```c
#define G_APP_MANAGER 0x007A39FC
#define G_GUI_MANAGER 0x007A39F4
#define G_EXO_INPUT   0x007A39E4
#define G_HWND        0x007A39D8
#define G_LMB_DOWN    0x008338F4
#define G_RMB_DOWN    0x008338F0

static void* CoopGuiManager(void) { return *(void**)G_GUI_MANAGER; }

static void* CoopClientInternal(void) {
    void* mgr = *(void**)G_APP_MANAGER;                if (!mgr) return NULL;
    void* app = *(void**)((char*)mgr + 4);             if (!app) return NULL;
    return *(void**)((char*)app + 4);
}
/* input is only accepted while the window is "active" -- see §7 */
static int CoopInputActive(void) {
    void* xi = *(void**)G_EXO_INPUT;                   if (!xi) return 0;
    void* in = *(void**)((char*)xi + 4);               if (!in) return 0;
    void* raw = *(void**)((char*)in + 0x140);          if (!raw) return 0;
    return *(int*)((char*)raw + 4);                    /* CExoInput::GetActive 0x005DF510 */
}
```

### ★ Recipe A — post the window messages (RECOMMENDED). Crash risk: **essentially zero**

```c
/* x, y are CLIENT pixels, top-down. Call from the per-frame main-thread callback. */
HWND  h  = *(HWND*)G_HWND;
LPARAM lp = (LPARAM)((y << 16) | (x & 0xFFFF));
PostMessageA(h, WM_LBUTTONDOWN, (WPARAM)MK_LBUTTON, lp);
PostMessageA(h, WM_LBUTTONUP,   (WPARAM)0,          lp);
```

Why this is the right answer:

* **We touch no engine memory and call no engine code.** Everything below the `PostMessage` is the
  game's own, unmodified path.
* **The position rides in `lParam`.** The `WM_LBUTTONDOWN` handler at `0x00402E98`/`0x004030B9`
  packs `(loword(lParam), SCREEN_HEIGHT-1-hiword(lParam))` into the buffered event, and
  `BufferEvent_2 0x005E1302` emits **mouse-X, then mouse-Y, then LMB-down, in that order**.
  `ProcessInput` runs `HandleMouseMove` after *every* event, so by the time the `0x3F` button event
  is dispatched the hover control is already correct for our coordinate. **The physical pointer is
  irrelevant** — this is exactly the property the `SendInput` approach lacked.
* It respects the player's "swap mouse buttons" option for free (`0x00402EE6` rewrites `0x201`
  to `0x204` when the client option at `[CClientExoApp::…+0x54]` is set) — i.e. it behaves like a
  real click, not a forged one.
* It is asynchronous (lands on the next `PeekMessage` pump, i.e. the same or next frame). If you
  need down and up separated by a frame (some list boxes drag), post the up on the following tick.

Preconditions (both cheap to assert): `*(HWND*)0x007A39D8 != NULL` and `CoopInputActive() != 0`.

### Recipe B — drive `CSWGuiManager` directly. Crash risk: **low**

Use when you need the click to be synchronous within your callback, or when the window is not the
foreground window and you deliberately want to bypass the activity gate.

```c
void* ci = CoopClientInternal();
void* gm = CoopGuiManager();
if (!ci || !gm) return;

/* 1. authoritative cursor (durable; survives until real mouse input) */
((void(__thiscall*)(void*,int,int))0x005F4EA0)(ci, x, y);        /* RestoreMousePositionTo */

/* 2. GUI working copy + hover/enter-leave + software cursor. Does NOT move the OS pointer. */
((void(__thiscall*)(void*,int,int))0x0040C1E0)(gm, x, y);        /* HandleMouseMove       */

/* 2b. optional sanity gate */
void *hitPanel = NULL, *hitCtl = NULL;
((int(__thiscall*)(void*,int,int,void**,void**,int))0x0040ABE0)(gm, x, y, &hitPanel, &hitCtl, 0);
if (hitCtl != expectedControl) return;                            /* bail, do not click */

/* 3. press + release */
((int(__thiscall*)(void*,int))0x0040C570)(gm, 1);                /* HandleLMouseDown      */
((int(__thiscall*)(void*))     0x0040A170)(gm);                  /* HandleLMouseUp -> 0x27 */
```

Notes / hazards:

* Main thread only, and only from a per-frame callback — never while `ProcessInput` is on the
  stack (re-entrancy into `HandleMouseMove` would corrupt hover state).
* Step 3's handler may **destroy the panel** (New Game, Exit…). Treat every cached control/panel
  pointer as dangling immediately after `HandleLMouseUp` returns.
* Substitute `CSWGuiManager::MoveMouseToPosition 0x0040C790(gm, x, y)` for steps 1–2 only if you
  *want* the OS pointer yanked (it calls `SetCursorPos`); note it does not write `CLIENT+0x3C8/3CC`,
  so pair it with step 1 anyway or the next `ProcessInput` reverts it.
* `0x008338F4` stays `0` with this recipe (it is set by `PerformLButtonDownAction`, which we skip).
  Anything that reads that global will not see the synthetic press. Set/clear it yourself if that
  matters.

### Recipe C — client-level button actions. Crash risk: **medium**

```c
((void(__thiscall*)(void*,int))0x0061F880)(ci, 1);   /* PerformLButtonDownAction */
((void(__thiscall*)(void*))    0x00620530)(ci);      /* PerformLButtonUpAction   */
```
Same as B but also runs the world-click path (`HandleMouseClickInWorld 0x00620350`), cursor refresh
and mouse-mode branching. More faithful in-game, more surface area to get wrong. Only reach for it
if a GUI-only click proves insufficient in the 3D view.

### Recipe D — fire the control's event directly. Crash risk: **medium-high in general**

```c
(*(void(__thiscall***)(void*,int,int))ctl)[0x3C/4](ctl, 0x27, 1);   /* HandleInputEvent(0x27,1) */
```
Bypasses hit-testing, hover and capture entirely. Perfectly fine for **our own** control (this is
effectively what `AddEvent(0x27, …)` already wires in `connect-flow.md`), but for stock controls it
can leave `GUI_MGR+0x08/+0x10` stale and any handler that pops a modal or expects a captured
control may misbehave. Do not use it as the general "click at (x,y)" primitive.

### Reading the cursor for our own overlay

```c
void* gm = *(void**)0x007A39F4;
int mx = ((int*)gm)[0];        /* client px, left-origin  */
int my = ((int*)gm)[1];        /* client px, TOP-down     */
int lmb = *(int*)0x008338F4;   /* 1 while left held       */
```
Hit-test your rows in the same client-pixel, top-down space you draw them in. Latch the
`0 -> 1` edge of `lmb` yourself; the engine has no "clicked this frame" flag for us to borrow. If
your overlay rows sit on top of a stock panel and you want first refusal on the click, check
`CSWGuiManager::HitCheckMouse` afterwards to see whether an engine control also claims the point.

---

## 7. Why the current clicks do nothing

Two VERIFIED gates, both consistent with "clicking Movies does nothing":

1. **The activity gate.** Every mouse branch in `WinMessageHandler` starts with
   `mov ecx,[0x007A39E4]; test ecx,ecx; je default` and `call CExoInput::GetActive 0x005DF510;
   test eax,eax; je default` (e.g. `0x00402E98`–`0x00402EAD`). `GetActive` returns
   `*(int*)(CExoInputInternal+0x140 → +4)`, which is driven from `WM_ACTIVATE`/`WM_ACTIVATEAPP`
   (`0x0040288D`–`0x004028DC` → `CClientExoApp::SetInputActive 0x005EDA10`). **If the game window
   is not the active window, every mouse message is dropped before it reaches the input layer.**
   `SendInput` from a second process, or from a plugin while a console/overlay window has focus,
   trips this.
2. **`SendInput` with `MOUSEEVENTF_ABSOLUTE` delivers to whatever window is under the *virtual
   desktop* point**, not to the game's client area. The moment the window is moved, DPI-scaled, or
   another window is on top, the message goes elsewhere. Recipe A removes this failure mode
   entirely because `PostMessageA` addresses the `HWND` directly and carries client coordinates.

Neither has anything to do with DirectInput deltas or a sensitivity multiplier.

---

## 8. Field reference (all VERIFIED unless noted)

**`CSWGuiManager`** (`*(void**)0x007A39F4`, ctor `0x0040BAD0`)

| off | meaning | evidence |
| --- | --- | --- |
| `+0x00` | `int` mouse X (client px) | `0x0040C1F6`, DB `mouse_x` |
| `+0x04` | `int` mouse Y (client px, top-down) | `0x0040C1F8`, DB `mouse_y` |
| `+0x08` | `CSWGuiControl*` under cursor | `0x0040C5E8`, `0x0041ABD8` |
| `+0x0C` | `int` captured button id (1 = left) | `0x0040A1DA` |
| `+0x10` | `CSWGuiControl*` mouse-captured | `0x0040A1D7` |
| `+0x1C` | `int` flags; bit0 = LMB down, bit3 = tooltip pending | `0x0040C587`, `0x0040A187` |
| `+0x24` | software-cursor object (`vtbl+0x5C` = set position) | `0x0040C24B` |
| `+0x58/+0x5C` | stored mouse X/Y (`StoreCurrentMousePosition 0x0040A3D0`) | DB |
| `+0x6C/+0x6E` | `short` viewport W/H (`SetSize 0x0040BE70`, fed real client size) | DB |
| `+0x88/+0x8C` | panel array / count | `0x0040AC50` |
| `+0x94/+0x98` | modal stack array / count | `0x0040ABEE` |

**`CClientExoAppInternal`** (`*(void**)(*(void**)(*(void**)0x007A39FC+4)+4)`)

| off | meaning |
| --- | --- |
| `+0x9C` | `int` mouse mode (2/3 = GUI-ish, 4 = suppressed) |
| `+0x274` | `CSWGuiManager*` (same object as `0x007A39F4`) |
| `+0x3C8` | `int` mouse X — **the authoritative cursor** |
| `+0x3CC` | `int` mouse Y (top-down) — **the authoritative cursor** |

**`CExoInputInternal`** (`*(void**)(*(void**)0x007A39E4 + 4)`)

| off | meaning |
| --- | --- |
| `+0x140` | `CExoRawInputInternal*`; its `+0x04` = "input active" |
| `+0x37C` | `int` pending X from last window message, `-1` = none |
| `+0x380` | `int` pending Y, **bottom-up**, `-1` = none |
| `+0x384` | `int` self-move latch; set by `SetMousePos`, swallows one `WM_MOUSEMOVE` |
| `+0x3A0/+0x3A4` | `float` camera axes from DirectInput `lX/lY`, `[-1,1]` — **not the cursor** |
| `+0x3AC` | `int` cursor-bitmap down state (`GetLastSetMouseDownState 0x005DF730`) |

**`CSWGuiControl`** — `+0x04 LEFT`, `+0x08 TOP`, `+0x0C WIDTH`, `+0x10 HEIGHT` (`int`);
`+0x34` handler/owner; `+0x38/+0x3C` bound-event array/count; `+0x44` flags
(bit1 `0x2` visible, bit3 `0x8` clickable, bit5 `0x20` locked — see `menu-button-runtime.md`).

**Functions worth having in the plugin's address table**

| address | signature | note |
| --- | --- | --- |
| `0x0040ABE0` | `int __thiscall(void* gui, int x, int y, void** outPanel, void** outCtl, int)` `ret 0x14` | read-only hit probe |
| `0x0040C1E0` | `void __thiscall(void* gui, int x, int y)` `ret 8` | `HandleMouseMove` |
| `0x0040C790` | `void __thiscall(void* gui, int x, int y)` `ret 8` | `MoveMouseToPosition` (also `SetCursorPos`) |
| `0x0040C570` | `int __thiscall(void* gui, int down)` `ret 4` | `HandleLMouseDown` |
| `0x0040A170` | `int __thiscall(void* gui)` `ret 0` | `HandleLMouseUp` |
| `0x0040AA00` | `void __thiscall(void* panel, int ext[4])` `ret 4` | absolute panel extent |
| `0x0040BA20` | `void __thiscall(void* panel, int* x, int* y)` `ret 8` | panel-local mouse |
| `0x005F4EA0` | `void __thiscall(void* client, int x, int y)` `ret 8` | `RestoreMousePositionTo` |
| `0x0061F7E0` / `0x0061F830` | `int __thiscall(void* client, int)` `ret 4` | `SetMouseX` / `SetMouseY` (Y is bottom-up in) |
| `0x0061F880` / `0x00620530` | `void __thiscall(void* client[, int])` | `PerformLButtonDown/UpAction` |
| `0x0041AB20` | `void __thiscall(void* ctl, int evtId, void* owner, void* fn)` `ret 0xC` | `AddEvent`; `0x27` click, `0x2D` accept |

---

## 9. What still needs a live pass

* **NEEDS-LIVE** — Recipe A end-to-end: post `WM_LBUTTONDOWN/UP` at `(484, 353)` on the main menu
  at 800×600 and confirm the Movies panel opens. Log `GUI_MGR+0x00/+0x04` and `0x008338F4`
  immediately before and after.
* **NEEDS-LIVE** — the resolution caveat in §5: at 1024×768, read the main-menu panel's
  `+0x04/+0x08` (or call `0x0040AA00`) and confirm whether the 800×600 block is top-left anchored
  or something else re-centres it. Use the runtime formula, not the hardcoded table, either way.
* **NEEDS-LIVE** — confirm `CoopInputActive()` is `1` in the situations where we want to inject
  (e.g. while our own overlay has drawn, while a second instance is running windowed).
* **INFERRED** — the `CLIENT+0x9C` mouse-mode values (`2`, `3`, `4`) are read off branch
  comparisons only; their names are guesses. Only matters if we ever inject clicks in the 3D view.

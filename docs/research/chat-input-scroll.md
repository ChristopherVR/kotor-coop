# In-game chat input & overlay scrolling (2026-07-31)

Reverse-engineered against `swkotor.exe` (GOG retail, 1.0.3; disk == memory, no ASLR, base
`0x400000`) via the sha256-matched symbol DB and static disassembly + PE data reads. Nothing in
the install was modified.

Read `docs/research/console-overlay.md` and `docs/research/text-render-queued.md` first. This
doc answers two questions: (1) how to give the player a **dedicated chat input** (type a message
without opening the dev console), and (2) how to capture a **scroll** signal to page the overlay
history. All frame-hook code below runs at `SwapBuffers` on the **main thread** — the same thread
the message pump and the engine's input poll run on, so the shared-global writes are race-free.

---

## Headline (VERIFIED)

- **Chat input: reuse the native console path (approach a).** The console's character editor
  `DoConsole 0x00401F00` is fed by the Win32 `WM_CHAR` handler `HandleWMCharMessage 0x00402240`,
  which routes a char to `DoConsole` **iff two flags are both set** — the `CExoInput`
  keyboard-mode flag *and* the console-open flag `[0x007A3A40]` — and **not** gated by
  `EnableCheats`. We can set both flags ourselves (by replaying the engine's own console-open
  sequence, all verified `__thiscall` no-arg calls on a verified global) and pre-fill the command
  buffer with `"say "`. The engine then does all keyboard-layout/shift/editing translation via
  Windows, and even renders the `> say …` line for us. **Enter** routes through the mod's existing
  buffer-capture (console-overlay.md); **Esc** cancels and restores movement. This reuses only
  known ABIs — no DirectInput scancode translation, no guessed callback.
- **Chat input: own-capture (approach b) is not recommended.** The engine's own key readers
  (`GetLastCapturedKeyboardKey 0x005DF630`, `GetKeyboardBuffer 0x005E3AE0`) return **DirectInput
  DIK scancodes** (`DIDEVICEOBJECTDATA`, 20-byte entries), not ASCII — you would have to
  re-implement DIK→ASCII + shift/caps translation, and `GetLastCapturedKeyboardKey`'s return ABI
  is murky. Higher effort, higher risk, worse text fidelity.
- **Scroll: use `GetAsyncKeyState(VK_PRIOR/VK_NEXT)`.** KOTOR **does not consume the mouse wheel
  at all** — `UpdateMouseDelta 0x005E0110` reads the DirectInput `DIMOUSESTATE.lZ` (wheel) field
  but **discards it**; only `lX`/`lY` are stored (`+0x3A0`/`+0x3A4`). There is no wheel
  accumulator global to read, and calling `GetDeviceState` ourselves would steal the relative-axis
  delta from the engine's per-frame poll and jitter mouse-look. Page-Up/Page-Down via
  `GetAsyncKeyState` (edge-detected, exactly like the existing `VK_RETURN` capture) is the
  reliable, read-only, zero-interference signal. A real wheel is possible only via a WndProc
  subclass (optional, §4).

---

## 1. The input globals (VERIFIED)

| Global | Type | Meaning | Proof |
| --- | --- | --- | --- |
| `[0x007A39E4]` | `CExoInput*` | the input singleton. 8-byte object `{vtable 0x74D5EC, CExoInputInternal* @ +4}`; internal is `0x3B0` bytes. | ctor `0x005DF3F0` (allocs `0x3B0`, stores at `+4`); init store at `0x00403BC7`; used as `this` for every `KeyboardMode*` call in `DoConsole`/`HandleWMCharMessage`. |
| `[0x007A39FC]` | app wrapper | `[+4]` = `CClientExoAppInternal*`, the `this` for `TurnOffKeyboardModal` and the WM-message dispatchers. | init store `0x004042DA`; used at `0x0040244B`, `0x00402263`, etc. |
| `[0x007A3A40]` | `int` | **console-open flag**. Nonzero ⇒ chars route to `DoConsole` **and** the engine renders `> `+buffer each frame (text-render-queued.md §2). | `DoConsole`/`HandleWMCharMessage`. |
| `[0x007A3A4C]` | `char[0x1F4]` | command buffer (null-terminated, cap 500 — `DoConsole` clamps at `0x1F4`). | `DoConsole 0x00401FF9`. |
| `[0x007A3A44]` | `int` | caret index into the buffer. | `DoConsole`. |
| `[0x007A3CB4]` | `int` | **"we own the keyboard grab"** flag. Set to `!prevKbMode` on open; when nonzero, Enter/Esc call `KeyboardModeOff` to release movement. | open path `0x00402431`; Esc `0x00401F39`; Enter `0x004021C5`. |
| `[0x007A3A48]` | `int` | set to `1` on Enter (command-pending marker). | `0x00402204`. |

Internal (`CExoInputInternal`, at `[0x007A39E4]+4`) fields seen: `+0x04` keyboard-mode flag
(1 = on), `+0x140` raw-input (`CExoRawInputInternal`) ptr, `+0x3A0`/`+0x3A4` mouse X/Y delta,
`+0x3A8` capture-keys value (`SetCaptureKeyboardKeys`).

## 2. Chat input — RECOMMENDED: reuse the native console (approach a)

### 2.1 Why it works (VERIFIED)

`HandleWMCharMessage 0x00402240` is the `WM_CHAR`/key WndProc handler. `wParam` (the char) is in
`si`. After the toggle-key branch it reaches the **char-dispatch** at `0x0040248A`:

```
0x0040248A  mov ecx,[0x7A39E4]        ; CExoInput*
0x00402490  call 0x5ED700            ; returns internal[+4] = keyboard-mode flag
0x00402495  test eax,eax ; je skip    ; require keyboard mode ON
0x00402499  mov eax,[0x7A3A40]        ; console-open flag
0x0040249E  test eax,eax ; je 0x4024C0 ; require console OPEN
0x004024A3  push esi ; call 0x401F00   ; DoConsole(char)  <-- edits the buffer
```

**Both conditions are ours to set**, and this path has **no `EnableCheats` check** (that gate lives
only in the grave/tilde *toggle* branch at `0x004023EE`, via `0x005E5630`). So forcing the two flags
routes typing into the buffer even with cheats disabled (NEEDS-LIVE — high-confidence INFERRED).
`DoConsole` then handles insert/backspace/caret/cap and, on `\r`/`\n` (`0x004021C5`), releases the
grab, closes the console, and posts the line — where the mod's existing Enter-edge capture
(console-overlay.md) reads `"say <msg>"` and routes it as chat.

### 2.2 The exact "force open" sequence (VERIFIED — replays the engine's own open path)

The engine's console-open code (`HandleWMCharMessage 0x0040241B–0x00402453`) is:

```
inp        = *(CExoInput**)0x007A39E4;
prevKbMode = 0x005ED700(inp);                 // = internal[+4]; normally 0 when closed
*(int*)0x007A3CB4 = (prevKbMode == 0) ? 1 : 0;// = 1: we now own the grab
0x005DF520(inp);                              // CExoInput::KeyboardModeOn  -> internal[+4]=1, grabs DI kbd
0x005DF4E0(inp);                              // CExoInput::ClearEvents     -> flush stale keys
appI = *(void**)(*(void**)0x007A39FC + 4);
0x005EDA20(appI);                             // CClientExoApp::TurnOffKeyboardModal
*(int*)0x007A3A40 = 1;                        // console open
```

Our chat-open is that sequence **plus a pre-fill**:

```c
// all __thiscall, ecx = this, no stack args, plain ret — VERIFIED ABI
typedef void (__thiscall *voidfn)(void* thisp);
void*  inp   = *(void**)0x007A39E4;
int    prevK = *(int*)(*(int*)((char*)inp + 4) + 4);      // == 0x5ED700(inp)
*(int*)0x007A3CB4 = (prevK == 0);
((voidfn)0x005DF520)(inp);                                // KeyboardModeOn
((voidfn)0x005DF4E0)(inp);                                // ClearEvents
void* app  = *(void**)0x007A39FC;
if (app && *(void**)((char*)app + 4))
    ((voidfn)0x005EDA20)(*(void**)((char*)app + 4));      // TurnOffKeyboardModal
strcpy((char*)0x007A3A4C, "say ");                        // pre-fill
*(int*)0x007A3A44 = 4;                                    // caret after "say "
*(int*)0x007A3A40 = 1;                                    // open last
```

Trigger this on the rising edge of your chosen chat key (`GetAsyncKeyState`, same edge pattern the
mod already uses for `VK_RETURN`). Do it **only when `[0x007A3A40]==0`** so you never double-open.

**ABI (all VERIFIED):**
- `CExoInput::KeyboardModeOn 0x005DF520` — thunk `mov ecx,[ecx+4]; jmp 0x5DFF60`. `this=CExoInput*`,
  no args, `ret`. Sets `internal[+4]=1` and, if a DI keyboard device exists (`internal[+0x140]!=0`),
  calls raw `0x005E3BE0` to acquire it. **Null-safe** (guards the device ptr).
- `CExoInput::KeyboardModeOff 0x005DF530` — mirror; used by Enter/Esc to release.
- `CExoInput::ClearEvents 0x005DF4E0` — `this=CExoInput*`, no args. Flushes queued events.
- `CClientExoApp::TurnOffKeyboardModal 0x005EDA20` — `this=CClientExoAppInternal* = *(0x007A39FC)+4`.
- `0x005ED700` — 3-byte accessor `mov eax,[ecx+4]; mov eax,[eax+4]; ret` = `internal[+4]`; equivalent
  to just reading `*(int*)(*(int*)(inp+4)+4)`, so the engine call is optional (read the field).

### 2.3 Safety & notes (approach a)

- **VERIFIED-safe from the hook.** Every call is a `__thiscall` no-arg on a verified global, i.e.
  the *same* calls the engine makes on the *same* thread when the user presses grave. The buffer/caret/
  flag writes are plain `.data` stores.
- **Rendering is free.** With `[0x007A3A40]=1`, `UpdateScreen` enqueues `> ` + buffer every frame
  (text-render-queued.md §2), so the typed line shows without us drawing it. If you want a custom
  chat box instead, you may render your own line with `AurPostString` and accept that the native
  `> ` prompt also shows (cosmetic).
- **Release is automatic.** Because we set `[0x007A3CB4]=1`, both Enter (`0x004021C5`) and Esc
  (`0x00401F39`) call `KeyboardModeOff` → movement resumes and the console closes. No manual cleanup.
- **1-frame latency (VERIFIED harmless).** We set the flags in the `SwapBuffers` hook; `WM_CHAR`
  is processed earlier in the *next* frame's pump. So the first keystroke after the trigger lands
  next frame — imperceptible.
- **NEEDS-LIVE:** (1) confirm char routing works with `EnableCheats=0` (the dispatch path has no
  cheat gate — expected yes). (2) Confirm the mod's Enter-edge capture still fires for a
  programmatically-opened console (it reads the same buffer/flag, so expected yes). (3) The user can
  backspace through the `"say "` prefix; re-assert it each frame while open if you want it locked
  (cosmetic). (4) Confirm `*(0x007A39FC)+4` is non-null in every context you allow chat (guarded
  above; if null, skip the `TurnOffKeyboardModal` call — it only clears a menu-modal state).

## 3. Chat input — NOT recommended: own capture (approach b)

For completeness, the engine's key readers and why they are worse here:

- **`GetKeyboardBuffer 0x005E3AE0`** (`this = internal[+0x140]`, arg = a list struct
  `{void* buf; int count}`). Allocates `0x1400` bytes = `0x100 × 0x14` and calls the DirectInput
  device's `GetDeviceData` (vtable `+0x28`). Each entry is a **`DIDEVICEOBJECTDATA` (20 bytes)**:
  `dwOfs` = **DIK scancode**, `dwData` bit `0x80` = key-down, then timestamp/sequence/appdata.
  Returns **scancodes, not characters** — you must translate DIK→ASCII and apply shift/caps
  yourself.
- **`GetLastCapturedKeyboardKey 0x005DF630`** (`this=CExoInput*`) → internal `0x005E0230`. Builds a
  local list, calls `GetKeyboardBuffer`, scans it against a capture table (`internal[+0x164…]`,
  count `[0x0074D3DC]`). Its return value is a DIK-based key, and the return ABI (it manipulates a
  local object under an SEH frame) is **not cleanly pinned** — invoking it blind is a crash risk.
- **`SetCaptureKeyboardKeys 0x005DF720`** just writes `internal[+0x3A8]` (a filter value); it does
  not give you characters. **`GetDisplayCharacterDirectInputValue 0x005E3D10`** does a DIK→display-
  char lookup (uses `[0x007A39E0]`, helper `0x005E6690`) but takes/returns via a `CExoString`-style
  path — again a guessed ABI.
- Detecting an edge yourself would still mean per-key `GetAsyncKeyState` for the whole keyboard plus
  layout handling. All of this re-implements what approach (a) gets for free from Windows `WM_CHAR`.

**Conclusion:** approach (b) buys nothing over (a) except a custom-drawn input box, at the cost of a
scancode-translation layer and a murky-ABI engine call. Use (a).

## 4. Scrolling the overlay history

### 4.1 There is no readable wheel delta (VERIFIED)

- No `WM_MOUSEWHEEL (0x020A)` handler exists — the 11 `0x020A` byte-matches in the image are all
  data/mid-instruction, not a message switch. KOTOR takes the mouse through **DirectInput**.
- `GetMouseState 0x005E4050` fills a **16-byte `DIMOUSESTATE`** (`lX@0, lY@4, lZ(wheel)@8,
  rgbButtons@0xC`) via `GetDeviceState` (vtable `+0x24`, size `0x10`). The wheel **is** in the
  struct…
- …but `UpdateMouseDelta 0x005E0110` (the per-frame consumer) reads only `lX` (`[esp+8]`) and `lY`
  (`[esp+0xC]`), scales them, and stores to `internal[+0x3A0]`/`[+0x3A4]`. **`lZ` is never touched.**
  `GetMouseDelta 0x005E00F0` returns only those two fields. **So there is no persistent wheel value
  to read.**
- Reading the wheel ourselves via `GetMouseState`/`GetDeviceState` is **unsafe**: DirectInput
  relative-axis immediate state returns the delta accumulated since the *last* `GetDeviceState` and
  resets it. The engine calls that every frame in `UpdateMouseDelta`; an extra call from our hook
  would race it and **steal mouse-look motion** → jitter. Do not.

### 4.2 RECOMMENDED: Page-Up / Page-Down via `GetAsyncKeyState` (VERIFIED-safe)

Pure read-only Win32, no engine interference, identical to the existing `VK_RETURN` edge capture:

```c
static SHORT prevPrior, prevNext;
SHORT p = GetAsyncKeyState(VK_PRIOR); // 0x21  Page-Up
SHORT n = GetAsyncKeyState(VK_NEXT);  // 0x22  Page-Down
if ((p & 0x8000) && !(prevPrior & 0x8000)) scrollHistory(+PAGE);  // rising edge
if ((n & 0x8000) && !(prevNext  & 0x8000)) scrollHistory(-PAGE);
prevPrior = p; prevNext = n;
```

Edge = "down this frame, up last frame" (bit `0x8000`). For key-repeat-style continuous paging,
drop the edge test and page while held (throttle by a frame counter). We keep the scrollback
ourselves and render a scrollbar with `AurPostString` (text-render-queued.md). `GetAsyncKeyState`
is already proven to work in this game (the mod reads `VK_RETURN` the same way), so it is not
blocked by DirectInput's keyboard grab.

### 4.3 OPTIONAL: a real mouse wheel via WndProc subclass (INFERRED / new infra)

If a wheel is wanted, subclass the game window (`SetWindowLongPtr(hwnd, GWLP_WNDPROC, …)`) and, in
`WM_MOUSEWHEEL (0x020A)`, accumulate `delta += (SHORT)HIWORD(wParam) / WHEEL_DELTA (120)`, then read
& zero `delta` from the frame hook. Windows still delivers `WM_MOUSEWHEEL` because the engine's DI
mouse is foreground/non-exclusive and never consumes the wheel (§4.1). Trade-off: this is **new
input infrastructure** (a WndProc detour) rather than a read-only poll — more surface area than the
Page-key path. NEEDS-LIVE if pursued; the Page-key path needs none. Recommend Page-keys unless a
wheel is a hard requirement.

---

## Recommendation

1. **Chat input → approach (a).** On a chosen key edge, replay the verified open sequence (§2.2) and
   pre-fill `"say "`; the engine translates keystrokes and renders the line; Enter routes through the
   mod's existing capture, Esc cancels. Lowest risk: only verified `__thiscall` no-arg calls on
   verified globals plus `.data` writes, on the main thread.
2. **Scroll → `GetAsyncKeyState(VK_PRIOR/VK_NEXT)`** with edge detection. The wheel is unavailable
   without new WndProc infra and unsafe to poll from DirectInput.

**Biggest risk:** the "force console open" path assumes the two dispatch flags are sufficient with
`EnableCheats=0` and that the mod's Enter capture fires for a programmatically-opened console —
both high-confidence INFERRED but unconfirmed live. Failure mode is benign (typing does nothing, or
the line isn't routed as chat — no crash), so it is safe to calibrate live. Guard the
`TurnOffKeyboardModal` `this` against null (done above) — that is the only pointer we deref that
could be null outside in-world play.

## Reference addresses (this build)

Globals: `CExoInput* [0x007A39E4]` (ctor `0x005DF3F0`, init `0x00403BC7`) · app `[0x007A39FC]`
(init `0x004042DA`, internal at `+4`) · console-open `[0x007A3A40]` · buffer `[0x007A3A4C]`
(`char[0x1F4]`) · caret `[0x007A3A44]` · grab-owned `[0x007A3CB4]` · cmd-pending `[0x007A3A48]`.
`CExoInputInternal` fields: `+0x04` kbd-mode flag · `+0x140` raw-input ptr · `+0x3A0/+0x3A4` mouse
X/Y delta · `+0x3A8` capture-keys filter.
Calls (`__thiscall`, ecx=this): `KeyboardModeOn 0x005DF520` · `KeyboardModeOff 0x005DF530` ·
`ClearEvents 0x005DF4E0` · `TurnOffKeyboardModal 0x005EDA20` · kbd-mode accessor `0x005ED700` ·
`GetLastCapturedKeyboardKey 0x005DF630`(→`0x005E0230`) · `GetKeyboardBuffer 0x005E3AE0` ·
`GetDisplayCharacterDirectInputValue 0x005E3D10` · `SetCaptureKeyboardKeys 0x005DF720` ·
`GetMouseState 0x005E4050` · `UpdateMouseDelta 0x005E0110` · `GetMouseDelta 0x005E00F0`.
Console pipeline: `HandleWMCharMessage 0x00402240` (toggle branch `0x004023EE`, EnableCheats compare
`0x005E5630`; open sequence `0x0040241B–0x00402453`; char-dispatch `0x0040248A`; DoConsole call
`0x004024A3`) · `DoConsole 0x00401F00` (Esc `0x00401F39`, Enter/execute `0x004021C5`, insert/cap
`0x00402116/0x00401FF9`). Wheel: no `WM_MOUSEWHEEL` handler; `DIMOUSESTATE.lZ@+8` discarded by
`UpdateMouseDelta`. Scroll fallback: `GetAsyncKeyState(VK_PRIOR 0x21 / VK_NEXT 0x22)`.
</content>
</invoke>

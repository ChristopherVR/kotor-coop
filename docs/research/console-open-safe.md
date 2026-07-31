# Programmatically opening the `~` console (2026-07-31)

Verified against `swkotor.exe` (GOG retail, 1.0.3; disk == memory, no ASLR, base
`0x400000`) via the sha256-matched symbol DB and static disassembly. Nothing in the install
was modified during research. Companion to `console-overlay.md` (buffer/render side) and
`console-register.md` (command table). This doc grounds the **open** action by disassembling
the engine's own toggle so we can pop the console pre-filled (e.g. `"say "`) without the crash
we hit.

Tags: **VERIFIED** = read from the disassembly on this build. **INFERRED** = follows from
verified code. **NEEDS-LIVE** = confirm at runtime before shipping.

## Headline

**Our sequence had exactly one wrong call: `TurnOffKeyboardModal`.** We passed
`this = mgr + 4` (the *address* of the client field inside `CAppManager`) where the engine
passes `this = *(mgr + 4)` (the client *pointer* itself). The one-level-too-shallow
dereference makes the method operate on the wrong object and fault. `KeyboardModeOn`,
`ClearEvents`, the input singleton `0x007A39E4`, and the buffer/caret/flag globals were all
correct.

## Ground truth — the engine's own open path (VERIFIED)

The native toggle lives inside `DoConsole 0x00401F00` (the WM_CHAR handler). When the console
is currently **closed** (`[0x007A3A40] == 0`) it runs this, with `esi == 1` (set at
`0x0040234E mov esi,1`):

```
; --- OPEN branch, 0x0040241B .. 0x00402459 ---
0040241B  mov ecx,[0x007A39E4]     ; CExoInput* singleton  (== our `input`)
00402421  call 0x005ED700          ; field accessor [[this+4]+4]; ret -> boolean below
00402426  mov ecx,[0x007A39E4]     ; ecx = CExoInput*   (this for next call)
0040242C  neg eax; sbb eax,eax; inc eax   ; eax = (ret==0) ? 1 : 0
00402431  mov [0x007A3CB4],eax     ; "keyboard-mode-was-engaged" latch  (NOT hard 1)
00402436  call 0x005DF520          ; CExoInput::KeyboardModeOn(this=CExoInput*)
0040243B  mov ecx,[0x007A39E4]
00402441  call 0x005DF4E0          ; CExoInput::ClearEvents(this=CExoInput*)
00402446  mov eax,[0x007A39FC]     ; CAppManager*
0040244B  mov ecx,[eax+4]          ; ecx = *(CAppManager+4) = appmgr->client (CClientExoApp*)
0040244E  call 0x005EDA20          ; CClientExoApp::TurnOffKeyboardModal(this=client)
00402453  mov [0x007A3A40],esi     ; console-open flag = 1
00402459  ...                      ; exit / stack cleanup
```

Close branch (for reference, `0x00402404`): `ecx=[0x007A39E4]; call 0x005DF530`
(`KeyboardModeOff`); `mov [0x007A3A40],0`.

## The three thiscalls — signatures and `this` (VERIFIED)

All three are thin thunks `mov ecx,[ecx+4]; jmp <internal>` — each takes `this` in `ecx`,
reads `this->internal` at `+4`, and tail-calls the impl. So a wrong `this` propagates one
level deeper before it faults.

| Fn | Addr | ABI | Correct `this` | Derivation |
| --- | --- | --- | --- | --- |
| `CExoInput::KeyboardModeOn` | `0x005DF520` | `__thiscall(this)` | `CExoInput*` | `*(int*)0x007A39E4` |
| `CExoInput::ClearEvents` | `0x005DF4E0` | `__thiscall(this)` | `CExoInput*` | `*(int*)0x007A39E4` |
| `CClientExoApp::TurnOffKeyboardModal` | `0x005EDA20` | `__thiscall(this)` | `CClientExoApp*` | `*(int*)( *(int*)0x007A39FC + 4 )` |

- **`0x007A39E4` IS the `CExoInput` singleton** (VERIFIED): the engine loads `ecx` from it
  directly for every input-mode call (`0x00402404/0x0040241B/0x00402426/0x0040243B`). It is
  *not* reached through `CAppManager`. Our `input = *(int*)0x007A39E4` was right.
- **`CClientExoApp` is `appmgr->client`** (VERIFIED): `CAppManager` layout is `+4 client`,
  `+8 server` (DB `offsets CAppManager`). Every CClientExoApp call in the key/console handlers
  loads `mov eax,[0x007A39FC]; mov ecx,[eax+4]` — a **dereference** of the `+4` field, not its
  address.

## Which call was wrong, and why it faulted (VERIFIED mechanism)

Our code:
```c
mgr = *(int*)0x007A39FC;          // CAppManager*
TurnOffKeyboardModal(mgr + 4);    // BUG: this = &appmgr->client, not appmgr->client
```
The `0x005EDA20` thunk then does `mov ecx,[ecx+4]` on `ecx = mgr+4`:
```
[ (mgr+4) + 4 ] = [ mgr + 8 ] = appmgr->server   (CServerExoApp*)
```
so it tail-calls `CClientExoAppInternal::TurnOffKeyboardModal 0x005F2980` with `this =`
the **server** pointer. That impl immediately does `mov esi,ecx; lea edi,[esi+0x100]; …`
and writes through `server+0x100` as if it were `CClientExoAppInternal` — wrong object,
wrong field layout → access-violation / corruption. **This single call is the crash.**
The other two calls (`KeyboardModeOn`, `ClearEvents`) received the correct `CExoInput*` and
were fine.

**Fix:** dereference once more —
```c
mgr    = *(int*)0x007A39FC;             // CAppManager*
client = *(int*)(mgr + 4);              // CClientExoApp* = appmgr->client
TurnOffKeyboardModal(client);           // this = client   (CORRECT)
```

## `0x007A3CB4` — set it, but note it's a latch not a constant (INFERRED)

The engine computes `[0x007A3CB4] = (ret_of_0x005ED700 == 0) ? 1 : 0` **before**
`KeyboardModeOn`, and reads it on the close/Enter paths (`cmp [0x007A3CB4],0; jne` →
`KeyboardModeOff`) to decide whether it still owes a `KeyboardModeOff`. Since we *do* call
`KeyboardModeOn`, hard-writing `1` is **safe** (guarantees the matching `KeyboardModeOff` on
close). Our `*(int*)0x007A3CB4 = 1` was fine; order relative to the mode calls doesn't matter.

## Minimal correct open sequence (for the plugin)

Replicate the engine, then optionally pre-fill the buffer:
```c
void* input = *(void**)0x007A39E4;                 // CExoInput*
void* mgr   = *(void**)0x007A39FC;                 // CAppManager*
void* client= *(void**)((char*)mgr + 4);           // appmgr->client (CClientExoApp*)

*(int*)0x007A3CB4 = 1;                              // owe a KeyboardModeOff on close
((void(__thiscall*)(void*))0x005DF520)(input);      // KeyboardModeOn
((void(__thiscall*)(void*))0x005DF4E0)(input);      // ClearEvents
((void(__thiscall*)(void*))0x005EDA20)(client);     // TurnOffKeyboardModal  <-- fixed this
*(int*)0x007A3A40 = 1;                              // console-open flag = 1

// optional pre-fill (matches engine's own edit-buffer contract):
strcpy((char*)0x007A3A4C, "say ");                  // command buffer, cap 0x1F4
*(int*)0x007A3A44 = 4;                              // caret index
```
Guard before running: only when in-world with the console subsystem live —
`*(int*)0x007A39E4`, `*(int*)0x007A39FC`, and `*(int*)(mgr+4)` all non-null, and the console
not already open (`*(int*)0x007A3A40 == 0`) to avoid double-toggling against a real keypress.

## Typed chars reach the buffer; Enter submits — routing (VERIFIED)

`DoConsole 0x00401F00` is the character (WM_CHAR) handler; `bl` = the typed char:

- **`0x1B` ESC** → the toggle block above (open when closed / close when open).
- **`0x0D` CR (Enter)** and **`0x0A` LF** → submit at `0x004021C5`: sets
  `[0x007A3A40]=0`, `KeyboardModeOff` if `[0x007A3CB4]`, and marks the line ready — the actual
  dispatch of `[0x007A3A4C]` happens at the pump `0x00404810`/`RunConsoleLine 0x0044C1F0`
  (see `console-register.md`). **Our existing Enter/buffer capture still fires** — we did not
  change the submit path.
- **`0x08` Backspace** → in-place edit of `[0x007A3A4C]`.
- **else** → append char to `[0x007A3A4C]` at caret `[0x007A3A44]`.

So once `[0x007A3A40]==1` and `KeyboardModeOn` is engaged, subsequent characters route into
`0x007A3A4C` exactly as when the player opens the console normally. Confirms buffer
`0x007A3A4C`, caret `0x007A3A44`, flag `0x007A3A40` (also proven by the close path at
`0x00401F56` zeroing byte `[0x007A3A4C]` and `[0x007A3A44]`).

## Thread-safety (INFERRED + NEEDS-LIVE)

The engine runs this open sequence from the **WndProc / message pump on the main thread**. Our
SwapBuffers frame-hook is also the **main thread**, so calling it there is the *same* thread as
the native path — **no cross-thread hazard**; the crash was purely the bad pointer, not
threading. **INFERRED safe** from the frame hook.

**NEEDS-LIVE:**
1. Confirm the fixed `TurnOffKeyboardModal(client)` runs clean in-world and the console opens
   with `"say "` pre-filled and the caret after it.
2. Confirm no re-entrancy if the player also hits the real toggle key in the same frame (guard
   on `[0x007A3A40]` as above).
3. Confirm `[0x007A3CB4]=1` leaves keyboard-mode balanced (console closes cleanly, character
   movement resumes) across open→type→Enter and open→ESC.

## Reference addresses

`DoConsole 0x00401F00` (open branch `0x0040241B`, close `0x00402404`, submit `0x004021C5`,
`esi=1` at `0x0040234E`) · `HandleWMKeyDownMessage 0x004024F0` · `DoConsoleHist 0x00401870`.
`CExoInput::KeyboardModeOn 0x005DF520` · `KeyboardModeOff 0x005DF530` · `ClearEvents 0x005DF4E0`
· `CClientExoApp::TurnOffKeyboardModal 0x005EDA20` (impl `CClientExoAppInternal 0x005F2980`) ·
field accessor `0x005ED700`.
Globals: `CExoInput* 0x007A39E4` · `CAppManager* 0x007A39FC` (`+4 client`, `+8 server`) ·
open-flag `0x007A3A40` · caret `0x007A3A44` · command buffer `0x007A3A4C` (cap `0x1F4`) ·
keyboard-mode latch `0x007A3CB4`.

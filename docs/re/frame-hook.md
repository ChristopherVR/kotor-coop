# Running code on KOTOR's own thread, once per frame

Every phase-2 task depends on this. Engine calls (spawn a creature, queue a movement
action) must happen on the thread that owns the simulation, at a point where engine state
is consistent. This document establishes where that point is, tests the cheap options, and
picks one.

Every claim below is labelled **VERIFIED** (observed in the live process or read out of the
symbol database) or **INFERRED** (reasoned from call structure and naming). Nothing here is
guessed.

Method: all runtime facts were obtained **read-only** against the running game
(pid 28524) with `OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ)` plus
`ReadProcessMemory`, disassembled with capstone. No game memory was written, no game
function was called. Addresses come from the `k1.db` Ghidra database, whose `game_version`
sha256 matches the installed executable; image base is `0x400000` and there is no ASLR, so
database addresses are live addresses.

---

## 1. Answer first

**Hook `GDI32!SwapBuffers` by rewriting one aligned dword in swkotor.exe's import address
table, at `0x0073D00C`.**

That gives a callback that is:

- on the game's main thread, by construction (the only two callers are `Global::WinMain`
  and `Global::UpdateScreen`, both of which run on the initial thread);
- once per frame, after the client update, after the render, and after the server tick;
- a plain `__stdcall BOOL(HDC)`, so it maps directly onto a C#
  `[UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]` method with no assembly
  stub and no calling-convention adapter;
- installed with a **single 4-byte aligned store**, which is atomic on x86, so there is no
  torn-instruction window and no need to suspend the game;
- not code patching: no bytes in `.text` change, so no signature scan, no instruction-length
  disassembly, no trampoline, and nothing for an anti-tamper check to notice in the code
  section;
- currently unoccupied: the slot still points at the real GDI32 export, so we are not
  stomping the Steam overlay (which *is* in this process and *has* hooked other things).

The DirectInput idea in the brief was tested against the live process and **refuted as a
primary frame hook**: the game stops polling DirectInput entirely whenever its window is not
the foreground window, while the frame loop keeps running at full speed. Details in
section 4.

The `opengl32.dll` proxy idea is also refuted: KOTOR does not call `wglSwapBuffers`. It
calls `GDI32!SwapBuffers`. Details in section 5.

---

## 2. The main loop (VERIFIED)

`Global::WinMain` at `0x004041F0` contains the one and only frame loop. Disassembled from
live memory:

| Address | What | Evidence |
| --- | --- | --- |
| `0x004042DA` | `mov [0x007A39FC], eax` | stores `CAppManager*`; confirms `APP_MANAGER_PTR` |
| `0x004044B0` | `call 0x005ED7B0` | `CClientExoApp::Initialize`, once, before the loop |
| **`0x00404570`** | **frame loop top** | back-edge target |
| `0x00404592` | `call [0x0073D3F4]` = `USER32!PeekMessageA` | if a message is pending: `GetMessageA` / `TranslateMessage` / `DispatchMessageA` then `jmp 0x00404A0B`, **skipping the whole game update for that iteration** |
| `0x00404663` | `call 0x00403800` `Global::ReInitAurora` | only when `[0x007A3A2C] != 0` (video mode change pending) |
| **`0x004046BD`** | **`call 0x005ED7D0` `CClientExoApp::MainLoop`** | `ecx = [APP_MANAGER_PTR]+4`; result OR-ed into the quit flag `[0x007A39B8]` |
| `0x0040472D` | `fstp [0x007A3C7C]` | float, ms spent in the client MainLoop, written **once per frame** |
| **`0x00404757`** | **`call 0x004AE860` `CServerExoApp::MainLoop`** | skipped when `[APP_MANAGER_PTR]+8 == 0` (no server); loop counter `esi < 1`, so **at most one call per frame** |
| `0x004047DA` | `fstp [0x007A3C80]` | float, ms spent in the server MainLoop, once per frame |
| `0x004048B8` | `call [0x0073D408]` = `USER32!GetDC` | |
| **`0x004048C1`** | **`call [0x0073D00C]` = `GDI32!SwapBuffers`** | guarded by `CClientExoApp::IsMoviePlaying == 0` and `[0x007A3A2C] == 0`; `[0x007A3C9C]` is set to 1 across the call |
| `0x004048CF` | `call [0x0073D404]` = `USER32!ReleaseDC` | |
| `0x004048FD` | frame limiter | `fld [0x007A3C64]`; if the cap is <= 0 the limiter is skipped entirely; otherwise busy-spins on `CExoTimers::GetHighResolutionTimer` until `1000/cap` ms have elapsed |
| **`0x00404A11`** | **`je 0x00404570`** | loop back-edge, taken while quit flag `[0x007A39B8] == 0` |

Both `MainLoop` entry points are one-instruction thunks (VERIFIED from live bytes):

```
0x005ED7D0  8b 49 04              mov ecx, [ecx+4]      ; CClientExoApp   -> Internal
0x005ED7D3  e9 d8 56 01 00        jmp 0x00602EB0        ; CClientExoAppInternal::MainLoop
0x005ED7D8  90 90 90 90 90 90 90 90                     ; 8 bytes of padding

0x004AE860  8b 49 04              mov ecx, [ecx+4]      ; CServerExoApp   -> Internal
0x004AE863  e9 48 c3 00 00        jmp 0x004BABB0        ; CServerExoAppInternal::MainLoop
0x004AE868  90 90 90 90 90 90 90 90
```

### Candidate table

| Class::Function | Address | Conv | Cadence | Notes |
| --- | --- | --- | --- | --- |
| `Global::WinMain` | `0x004041F0` | `__stdcall` | once per process | contains the loop |
| `CClientExoApp::MainLoop` | `0x005ED7D0` | `__thiscall` | **once per frame** | thunk to below |
| `CClientExoAppInternal::MainLoop` | `0x00602EB0` | `__thiscall` | **once per frame** | client update + render + input |
| `CServerExoApp::MainLoop` | `0x004AE860` | `__thiscall` | **once per frame** (skipped with no server) | thunk to below |
| `CServerExoAppInternal::MainLoop` | `0x004BABB0` | `__thiscall` | **once per frame** | the simulation tick |
| `CClientExoAppInternal::ProcessInput` | `0x006227E0` | `__thiscall` | **once per frame** | two call sites, mutually exclusive branches (below) |
| `Global::UpdateScreen` | `0x00401C10` | `__cdecl` | **not per frame** | re-entrant pump used during load screens and movies; itself calls `CServerExoApp::MainLoop` and `Global::messagepump` |
| `Global::messagepump` | `0x00401510` | `__stdcall` | irregular | also stored as a callback in `[0x0078E39C]`, read from `Scene::DoGobBuckets` / `Scene::DoBlurBuckets`, i.e. per-bucket inside the renderer, **not** per frame |
| `CServerAIMaster::UpdateState` | `0x004B0B70` | `__thiscall` | once per server tick | called from `CServerExoAppInternal::MainLoop` |
| `CClientAIMaster::UpdateState` | `0x006048C0` | `__thiscall` | once per client tick | |
| `CSWCCreature::AIUpdate` | `0x0061AD60` | `__thiscall` | **per object** | do not hook for framing |
| `CSWCArea::Update` | `0x006097F0` | `__thiscall` | per area | |
| `CExoResMan::Update` | `0x00408D40` | `__thiscall` | once per frame, but a resource-manager concern | |

`ProcessInput` has exactly two call sites, `0x00603323` and `0x006033AC`, and they are on
mutually exclusive branches (VERIFIED from the disassembly): the test at `0x006032C4`
either takes the load-screen path (`Global::UpdateScreen`, then `ProcessInput` at
`0x00603323`, then an early `ret` at `0x00603354`) or the normal path (`CExoResMan::Update`,
`CSWParty::Update`, `Global::AurMakeCurrent`, then `ProcessInput` at `0x006033AC`). So
`ProcessInput` runs once per client MainLoop, not twice.

### Which of these is the main thread (VERIFIED, with one inference)

`WinMain` runs on the process's initial thread by definition, and it calls both `MainLoop`s
directly, so both run on the initial thread. Confirmed against the live process: of the 37
threads, the earliest-created one (tid **24080**, started `19:20:14.938`, matching the
process start time) is the only one in `Running` state and has accumulated **4h10m** of CPU
time. Everything else is in `Wait` with near-zero CPU. INFERRED, but only barely: that is the
loop thread.

### Frame rate, measured (VERIFIED, and it is not 63 Hz)

Sampled `[0x007A3C7C]` (the once-per-frame client-MainLoop duration) at 442,000 reads/s for
3 seconds while a save was loaded (`CAppManager+0x08` non-null, `CServerExoApp*` =
`0x1BFAD050` with the expected vtable `0x00744184`, and 16 occurrences of the
`CSWSCreature` vtable `0x007470C8` in private memory, matching `object-model.md`):

```
transitions        : 950 in 3.0 s  =  317 Hz
inter-transition ms: min 2.819  p25 2.889  median 2.986  p75 3.306  max 6.607
client MainLoop    : ~3.0 ms      server MainLoop: 0.0 - 0.2 ms
frame cap [0x007A3C64] = 0.0      (limiter disabled, loop runs free)
```

So the loop iterates about **317 times a second**, not 63. The game window was visible but
**not** the foreground window during this measurement, and `SwapBuffers` was evidently not
blocking on vsync in that state. The 63 Hz in `ARCHITECTURE.md` was measured a different way
(counting rewrites of the position mirror) and under focus.

Consequence for the design: **do not assume a fixed frame cadence.** A per-frame callback
can fire 300+ times a second, so it must be cheap and must not do per-frame network I/O.
Rate-limit inside the callback off a wall clock, not off a frame counter.

---

## 3. Where in the frame is engine state safe to touch

Order within one iteration (VERIFIED):

```
PeekMessage -> (message pending? handle one and skip the rest of this iteration)
CClientExoApp::MainLoop        input, client objects, camera, GUI, scene render
CServerExoApp::MainLoop        world timer, CServerAIMaster::UpdateState,
                               UpdateClientGameObjects, party, autosave, scripts
GetDC / SwapBuffers / ReleaseDC
(optional frame limiter)
```

`CServerExoAppInternal::MainLoop` (`0x004BABB0`) is the simulation tick. Its call list
includes `CServerExoAppInternal::UpdateAutoSaveTimer`, `CWorldTimer::SetSnapshotTime`,
`CNetLayer::ProcessReceivedFrames`, `CServerExoAppInternal::TempDeathUpdate`,
`CServerExoAppInternal::UpdateMapData`, **`CServerAIMaster::UpdateState`**,
**`CServerExoAppInternal::UpdateClientGameObjects`**, `CSWPartyTable::UpdateMembers`,
`CServerExoAppInternal::StartNewModule`, and the save path. (VERIFIED.)

`SwapBuffers` therefore sits at the **end of the frame, immediately after the server tick
has completed and immediately before the next frame's client update**. INFERRED, but
strongly: this is the quietest point in the frame. Nothing is mid-iteration over the object
array, no AI action is half-applied, and anything we enqueue (an action node on
`CSWSCreature+0x0FC`, a new object in `CGameObjectArray`) will be picked up by the very next
`CServerAIMaster::UpdateState`, before anything is rendered again.

By contrast, a hook *inside* `CServerAIMaster::UpdateState` or `CSWCCreature::AIUpdate` would
run while the engine is walking its own containers, which is exactly where mutating the
object array causes iterator invalidation. Avoid.

---

## 4. The DirectInput hypothesis: tested, and it does not hold

The brief's suggestion was the cheapest possible option, so it was tested rather than
assumed. The static path is real, the runtime behaviour is not.

### The polling path exists (VERIFIED)

`CExoRawInputInternal` (size `0x34`) holds:

| Offset | Field |
| --- | --- |
| `+0x04` | active flag |
| `+0x1C` | `IDirectInput8*` |
| `+0x20` | keyboard `IDirectInputDevice8*` |
| `+0x24` | mouse `IDirectInputDevice8*` |

`CExoRawInputInternal::InitializeDirectInputMouse` (`0x005E3FA0`) calls, through the COM
vtables (offsets cross-checked against the real `IDirectInputDevice8` layout, and against the
three entries the database already labels: `SetProperty` `0x18`, `SetDataFormat` `0x2C`,
`SetCooperativeLevel` `0x34`):

```
[+0x0C] IDirectInput8::CreateDevice(GUID @0x0074D6A4, &mouse, NULL)
[+0x2C] SetDataFormat(c_dfDIMouse @0x0074DB04)
[+0x34] SetCooperativeLevel(hwnd @[0x007A39D8], 0x0A = FOREGROUND | NONEXCLUSIVE)
[+0x18] SetProperty(DIPROP_BUFFERSIZE, 0x100)
[+0x1C] Acquire()
```

Per frame, the chain is:

```
CClientExoAppInternal::ProcessInput  0x006227E0
  +0xC3 -> CExoInput::UpdateMouseDelta   0x005DF620 -> 0x005E0110
             -> CExoRawInputInternal::GetMouseState 0x005E4050
                  -> [vtbl+0x24] IDirectInputDevice8::GetDeviceState(0x10, &state)
                     (and [vtbl+0x1C] Acquire on DIERR_INPUTLOST / NOTACQUIRED)
  +0xF2 -> CExoInput::GetEvents          0x005DF6B0 -> 0x005E24E0
             -> CExoRawInputInternal::GetKeyboardBuffer 0x005E3AE0
                  -> [vtbl+0x28] IDirectInputDevice8::GetDeviceData(0x14, 0x100 items)
```

So `GetDeviceState` (mouse) and `GetDeviceData` (keyboard) are both real per-frame calls.
`CExoInputInternal::PollInput` / `PollInput_2` (`0x005DF4D0` / `0x005E23C0`) are *not* device
polls despite the name: they are per-event state queries, which is why there are 51 call
sites in `ProcessInput` and `UpdateCamera`.

### Wrapping the returned interface provably works here (VERIFIED)

Reading the live objects through `[0x007A39E4] -> +0x04 -> +0x140`:

```
CExoInput*            0x061AF6B8
CExoInputInternal*    0x0625D1E0   vtable 0x0074D624   (matches the database)
CExoRawInputInternal* 0x061D8210
  IDirectInput8*      0x098B3FB0   vtable 0x65FB92B4 -> gameoverlayrenderer.dll+0xF92B4
  keyboard device*    0x098EBCA0   vtable 0x65FB9578 -> gameoverlayrenderer.dll+0xF9578
  mouse device*       0x098B7968   vtable 0x65FB95FC -> gameoverlayrenderer.dll+0xF95FC
```

The Steam overlay has already replaced all three interfaces with its own proxy objects. The
technique is therefore proven to work in this exact process by an existing shipping product.
That is worth knowing, and it also means our `dinput8.dll` proxy would be the second wrapper
in the chain.

### Why it is still the wrong hook (VERIFIED)

`CExoRawInputInternal::GetMouseState` begins:

```
0x005E4066  mov ecx, [esi+4]        ; the active flag
0x005E406B  je  0x005E40A9          ; inactive -> return 0 WITHOUT calling GetDeviceState
```

and `CExoRawInputInternal::SetActive` (`0x005E3C20`) calls `[vtbl+0x20]`
**`Unacquire`** on both devices when passed 0, `[vtbl+0x1C]` `Acquire` when passed 1.

The chain that drives it: `Global::WinMessageHandler` (the window procedure) calls
`Global::DeactivateRenderWindow` (`0x00401D90`), which calls
`CClientExoApp::SetInputActive(0)` (`0x005EDA10`), which reaches
`CExoInputInternal::SetActive` (`0x005DFFB0`) and then
`CExoRawInputInternal::SetActive(0)`. `Global::ActivateRenderWindow` (`0x00401E00`) does the
reverse. `SetInputActive` is also called from `Global::toggleFullScreen`,
`Global::ReInitAurora`, `CSWSArea::SetTransitionPending` and `CSWSArea::EventHandler`.

Read live, right now, while the game had a save loaded and was running at 317 Hz but was
**not** the foreground window (foreground belonged to the terminal):

```
CExoRawInputInternal+0x04 = 0x00000000     <- input inactive, both devices Unacquired
```

**`GetDeviceState` was not being called at all**, while the simulation kept ticking 317
times a second. A DirectInput-derived frame callback would have been silent for the entire
measurement window.

That is disqualifying for a co-op mod. The remote-player pipeline has to keep running when
the window loses focus (alt-tab, second monitor, the other player's turn in a dialogue), and
it must not silently freeze the moment attention moves elsewhere. Secondary problems, both
smaller: the callback would also stop during area transitions and video-mode changes, and it
would fire twice per frame (mouse `GetDeviceState` plus keyboard `GetDeviceData`) needing
de-duplication.

Verdict: **rejected as the frame hook.** Wrapping the DirectInput device remains useful for
a different job later (reading local input, or suppressing input for a remote-controlled
character), and the plumbing to do it already exists in `Exports.DirectInput8Create`.

---

## 5. The other alternatives

### An `opengl32.dll` proxy hooking `wglSwapBuffers`: rejected (VERIFIED)

`OPENGL32.dll` is loaded (`0x71370000`), and the game imports from it (`glClear` at IAT
`0x0073D21C`, `glColor4f` at `0x0073D224`). But it does **not** import or call
`wglSwapBuffers`. The buffer swap resolves as:

```
IAT 0x0073D00C -> 0x75CA57F0   GDI32.dll!SwapBuffers
```

and there are exactly two references to that slot in the whole image, both once-per-frame
sites:

```
0x004048C3  dword  in Global::WinMain      +0x6D3   <- the real frame swap
0x00401CC4  dword  in Global::UpdateScreen +0xB4    <- the load-screen / movie pump swap
```

An `opengl32.dll` proxy would therefore give us `glClear` (many times per frame, useless as
a frame marker) and nothing resembling a present. It would also mean shipping a proxy for a
DLL whose export surface is large and whose real implementation is driver-dependent. Reject.

The GDI32 chain is clean, which matters: `GDI32!SwapBuffers` is itself a forwarder
`jmp [0x75CBE130]` into `gdi32full.dll+0x71660`, and that target begins with the ordinary
MSVC prologue `8B FF 55 8B EC 51 56 57` (VERIFIED). No inline detour, and the game's IAT
slot still holds the genuine export address, so the Steam overlay has not taken this path.
Our hook would be first and only.

### A 5-byte detour into a database-identified per-frame function: viable fallback

Two clean targets, both `__thiscall`, both verified from live bytes:

```
0x004046BD  e8 0e 91 1e 00   call CClientExoApp::MainLoop    (rel32 -> 0x005ED7D0)
0x00404757  e8 04 a1 0a 00   call CServerExoApp::MainLoop    (rel32 -> 0x004AE860)
```

Patching the **call site** rather than the function is much better here. A 5-byte `E8 rel32`
is exactly the size of the detour we want to write, so there is no instruction-length
analysis, no trampoline, and no chance of landing mid-instruction. It also leaves the callee
untouched, so any other caller (and `Global::UpdateScreen` genuinely is another caller of
`CServerExoApp::MainLoop`, at `0x00401CF3`) is unaffected, which is either a feature or a
trap depending on what you want.

Tradeoffs versus the IAT hook:

| | IAT `SwapBuffers` | call-site detour at `0x004046BD` |
| --- | --- | --- |
| Bytes changed in `.text` | none | 4 (the rel32 operand) |
| Write atomicity | single 4-byte **aligned** store, atomic | operand at `0x004046BE` straddles a dword boundary, not atomic |
| Calling convention | `__stdcall BOOL(HDC)`, direct C# match | `__thiscall`, `this` in `ecx`, return value in `eax` consumed by `or [0x007A39B8], eax`; needs a hand-written asm stub |
| Ordering | after client update, render **and** server tick | before or after the client update, but **before** the server tick |
| Fires during movies | no (`IsMoviePlaying` guards the swap) | yes |
| Fires during load screens | yes, and possibly twice per iteration | yes |
| Anti-tamper surface | data section only | executable section |

The one real advantage of the detour is that it fires during movie playback, when the swap
does not. That is unlikely to matter: during a movie `CClientExoAppInternal::MainLoop` calls
`CClientExoAppInternal::WaitForMovie` (`0x005F6F20`) and the simulation is not advancing, so
there is nothing for a co-op tick to do.

Recommendation: **ship the IAT hook. Keep the call-site detour documented as a fallback**,
to be used only if a callback is genuinely needed during movie playback.

### Rejected outright

- `Global::UpdateScreen` (`0x00401C10`): not per frame. It is the re-entrant pump used while
  loading, and it calls `CServerExoApp::MainLoop` itself, so hooking it would give a
  callback that sometimes nests inside a server tick.
- The callback pointer at `[0x0078E39C]` (written once in `WinMain` with
  `Global::messagepump`): read from `Scene::DoGobBuckets+0x87` and `Scene::DoBlurBuckets+0x17`,
  i.e. inside the renderer's per-bucket loops. Attractive because it is a data-only hook, but
  the cadence is per-bucket, not per-frame.
- `CSWCCreature::AIUpdate`, `CSWCArea::Update`, `CServerAIMaster::UpdateState`: per object,
  per area, or mid-iteration over the engine's own containers.
- A `SetWindowsHookEx`/`WM_TIMER` message hook: rides the message pump, and the
  `PeekMessageA` branch at `0x00404592` deliberately *skips* the game update on any iteration
  that has a pending message, so message-driven timing is anti-correlated with simulation
  ticks.

---

## 6. Implementation plan (C# NativeAOT, win-x86)

Fits the existing `src/KotorCoop.Plugin` shape: `Exports.DirectInput8Create` already runs on
the game's main thread during startup, which is exactly where the hook should be installed.

### 6.1 Why installing from `DirectInput8Create` is race-free

`DINPUT8_DLL::DirectInput8Create` (`0x005E50C0`) is reached from
`CExoRawInputInternal::Constructor` (`0x005E3E80`) <- `CExoInputInternal::Constructor`
(`0x005E1880`) <- `CExoInput::Constructor` (`0x005DF3F0`) <- `Global::ReInitAurora+0x3B4`
(`0x00403BB4`). (VERIFIED by xref.)

`Global::ReInitAurora` is called from `Global::WinMain+0x473` (`0x00404663`), which is inside
the loop body but **before** the `MainLoop` calls and before the swap in that same iteration,
gated on `[0x007A3A2C] != 0`. (VERIFIED.) The plugin's own log confirms the timing: process
start `19:20:14.938`, `DirectInput8Create` reached at `19:20:17.344`.

So at install time we are on the main thread, inside a call the main thread is making, and
no other thread calls `SwapBuffers` through the game's IAT. Nothing needs suspending.

One consequence: `ReInitAurora` runs again on a resolution change (`SetVideoMode`, the
graphics options panels, `ChangeResolutionForMovie`), reconstructing `CExoInput`, so
`DirectInput8Create` can be called more than once. `PluginHost.EnsureStarted` is already
idempotent; the hook install must be too.

### 6.2 New P/Invokes

```csharp
// NativeMethods.cs
public const uint PageReadWrite      = 0x04;   // already present
public const uint PageExecuteRead     = 0x20;
public const uint PageExecuteReadWrite = 0x40;

[LibraryImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
public static partial bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

[LibraryImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
public static partial bool FlushInstructionCache(nint process, nint address, nuint size);

[LibraryImport("kernel32.dll")]
public static partial nint GetCurrentProcess();

[LibraryImport("kernel32.dll")]
public static partial uint GetCurrentThreadId();
```

### 6.3 The hook

```csharp
internal static unsafe class FrameHook
{
    // VERIFIED: swkotor.exe v1.0.3, ImageBase 0x400000, no ASLR.
    private const nint SwapBuffersSlot = 0x0073D00C;   // IAT entry for GDI32!SwapBuffers
    private const nint AppManagerPtr   = 0x007A39FC;   // CAppManager**

    private static delegate* unmanaged[Stdcall]<nint, int> _realSwapBuffers;
    private static nint _originalSlotValue;
    private static bool _installed;
    private static volatile bool _detachRequested;
    public  static uint MainThreadId;

    public static bool Install()
    {
        if (_installed) { return true; }

        MainThreadId = NativeMethods.GetCurrentThreadId();

        // Refuse to stomp somebody else's hook. The slot must still hold the genuine
        // export; if it does not, another product got here first and blindly overwriting
        // it would break them and us.
        nint expected = NativeMethods.GetProcAddress(
            NativeMethods.GetModuleHandle("gdi32.dll"), "SwapBuffers");
        nint current = *(nint*)SwapBuffersSlot;
        if (expected == 0 || current != expected)
        {
            PluginHost.Log($"frame hook: IAT 0x{SwapBuffersSlot:X} holds 0x{current:X}, " +
                           $"expected 0x{expected:X}; refusing to patch");
            return false;
        }

        // The IAT page is PAGE_READONLY in this build (VERIFIED via VirtualQueryEx),
        // so it must be opened for writing first.
        if (!NativeMethods.VirtualProtect(SwapBuffersSlot, sizeof(nint),
                NativeMethods.PageReadWrite, out uint old))
        {
            PluginHost.Log("frame hook: VirtualProtect failed");
            return false;
        }

        _originalSlotValue = current;
        _realSwapBuffers = (delegate* unmanaged[Stdcall]<nint, int>)current;

        // Single 4-byte store to a 4-byte aligned address: atomic on x86, so the game's
        // thread can never observe a torn pointer. We are on that thread anyway.
        Volatile.Write(ref *(nint*)SwapBuffersSlot,
            (nint)(delegate* unmanaged[Stdcall]<nint, int>)&SwapBuffersHook);

        NativeMethods.VirtualProtect(SwapBuffersSlot, sizeof(nint), old, out _);
        _installed = true;
        PluginHost.Log($"frame hook installed: IAT 0x{SwapBuffersSlot:X} " +
                       $"0x{current:X} -> hook, main thread {MainThreadId}");
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int SwapBuffersHook(nint hdc)
    {
        // Present first. If our own work faults, the frame the user was looking at has
        // already reached the screen, which makes the failure far easier to attribute.
        int result = _realSwapBuffers(hdc);

        if (_detachRequested)
        {
            // Detach on the game's own thread, at the safest point in the frame.
            Uninstall();
            return result;
        }

        try
        {
            OnFrame();
        }
        catch
        {
            // Never let a managed exception unwind into WinMain.
        }

        return result;
    }

    private static void OnFrame()
    {
        // Runs ~317 times a second (VERIFIED). Budget accordingly:
        //   - no allocation, no logging, no string formatting on this path;
        //   - no locks the worker thread can hold for long;
        //   - rate-limit real work off a wall clock, not off a frame count.
        nint appManager = *(nint*)AppManagerPtr;
        if (appManager == 0) { return; }
        nint server = *(nint*)(appManager + 8);
        if (server == 0) { return; }   // main menu, no world: nothing to do

        // Phase 2 lands here: read the agent's remote peers out of the shared block and
        // drive them through the engine's own functions, on this thread.
    }

    public static void RequestDetach() => _detachRequested = true;

    private static void Uninstall()
    {
        if (!_installed) { return; }
        if (!NativeMethods.VirtualProtect(SwapBuffersSlot, sizeof(nint),
                NativeMethods.PageReadWrite, out uint old)) { return; }
        Volatile.Write(ref *(nint*)SwapBuffersSlot, _originalSlotValue);
        NativeMethods.VirtualProtect(SwapBuffersSlot, sizeof(nint), old, out _);
        _installed = false;
        _detachRequested = false;
    }
}
```

Wire it in beside the existing startup, in `PluginHost.EnsureStarted`, after
`AllocateBlock()` and before the worker starts. It must be called from
`Exports.DirectInput8Create` (which is where `EnsureStarted` is already called from), not
from `DllMain`: NativeAOT startup under the loader lock is a hazard, and the current design
already avoids it.

### 6.4 Points that will bite if ignored

- **`[UnmanagedCallersOnly]` cannot be a generic, instance, or closure-capturing method,
  and the function pointer must be taken with `&Method`.** `Marshal.GetFunctionPointerForDelegate`
  would also work but keeps a managed delegate alive that must be rooted for the whole
  process, and NativeAOT prefers the direct `&` form. `Marshal.GetDelegateForFunctionPointer`
  is not needed at all here: the `delegate* unmanaged[Stdcall]<nint, int>` cast of the
  original slot value is the whole forwarder.
- **Zero allocation in `OnFrame`.** A GC triggered from the game's thread suspends it. At
  ~317 calls a second, an allocating callback will produce visible stutter. Keep the
  callback to raw pointer reads/writes and `Interlocked` operations against the shared block.
- **Move all engine calls onto this thread.** The existing `WorkerLoop` must stop being the
  place where anything engine-facing happens. Its job becomes: bump the heartbeat, and move
  bytes between the shared block and the network. Hand off with a lock-free double buffer or
  `Interlocked` sequence counter, not a `lock`, so the frame callback can never block on it.
- **Re-entrancy during load screens.** `Global::UpdateScreen` swaps at `0x00401CC4` and
  `WinMain` swaps at `0x004048C1`, and during a load screen the client MainLoop takes the
  early-return path, so both can fire in one loop iteration. Harmless if `OnFrame` is
  idempotent. If exactly-once matters, de-duplicate on the once-per-frame float at
  `[0x007A3C7C]`: skip when it is unchanged since the previous callback. Note the collision
  case, two consecutive frames with a bit-identical duration, which would drop a tick.
- **No callback during movie playback.** The swap at `0x004048C1` is guarded by
  `CClientExoApp::IsMoviePlaying`. The simulation is not advancing then either, so this is
  acceptable; if it ever is not, switch to the call-site detour in section 6.5.
  **⚠ The call-site detour is not the escape hatch this implies — see
  `docs/research/cinematics.md`.** `CClientExoAppInternal::MainLoop` spins inside
  `WaitForMovie 0x005F6F20` until the movie ends, so the detour at `0x004046BD` fires *before*
  that block and then does not fire again either. Neither hook runs during a movie, and in a
  co-op session the simulation not advancing is only true of THIS process: the external agent
  and the other player keep going. What matters is not a co-op tick that has nothing to do, it
  is the pre-store flush and the epoch gate, which are delayed by the whole length of the movie.
- **`_realSwapBuffers` must be set before the slot is written.** It is, above. Reversing
  those two lines would produce a null-pointer call on the very next frame.
- **Verify, do not trust, the constants.** `SwapBuffersSlot` is validated against
  `GetProcAddress` before patching, which is the strongest available check and survives a
  future patch level that moves the IAT. `AppManagerPtr` should be sanity-checked the same
  way: `*(nint*)0x007A39FC` must be non-null and `*(nint*)(appManager + 4)` must have vtable
  `0x0074E6D8` (`CClientExoApp`) before anything is done with it.

### 6.5 The fallback detour, if it is ever needed

Patch the 4-byte rel32 operand at `0x004046BE` so the `call` at `0x004046BD` targets a stub
we allocate. Verify the 13-byte signature first (VERIFIED live bytes):

```
0x004046B5:  8B 4A 04 A3 C8 39 7A 00 E8 0E 91 1E 00
             mov ecx,[edx+4] | mov [0x007A39C8],eax | call 0x005ED7D0
```

The stub, 15 bytes, in a `VirtualAlloc` region flipped to `PAGE_EXECUTE_READ` after writing:

```
51                    push ecx                      ; preserve this
E8 rel32(0x005ED7D0)  call CClientExoApp::MainLoop  ; ecx = this; returns the quit flag
59                    pop  ecx
50                    push eax                      ; WinMain does or [0x007A39B8], eax
E8 rel32(callback)     call ManagedFrameCallback     ; __stdcall, no args, callee-clean
58                    pop  eax                      ; restore the return value
C3                    ret
```

`CClientExoApp::MainLoop` takes no stack arguments and its epilogue is a bare `ret` at
`0x00603354` (VERIFIED), so the `call`/`ret` pairing is clean and `esp` is balanced. The
stub may clobber `eax`, `ecx` and `edx` freely (a `__thiscall` callee already does) but must
leave `ebx`, `esi`, `edi` and `ebp` alone, which it does. Install it from
`DirectInput8Create` for the same reason as the IAT hook; the operand at `0x004046BE`
straddles a dword boundary, so patching it from any other thread would need the main thread
suspended first.

---

## 7. Constants established here

| Symbol | Address | Status |
| --- | --- | --- |
| `Global::WinMain` | `0x004041F0` | VERIFIED |
| frame loop top | `0x00404570` | VERIFIED |
| `call CClientExoApp::MainLoop` | `0x004046BD` | VERIFIED |
| `call CServerExoApp::MainLoop` | `0x00404757` | VERIFIED |
| `call [IAT] GDI32!SwapBuffers` | `0x004048C1` | VERIFIED |
| frame loop back-edge | `0x00404A11` -> `0x00404570` | VERIFIED |
| **IAT slot, `GDI32!SwapBuffers`** | **`0x0073D00C`** | **VERIFIED** (page is `PAGE_READONLY`, 4-byte aligned) |
| IAT slot, `USER32!PeekMessageA` | `0x0073D3F4` | VERIFIED |
| IAT slot, `USER32!GetDC` / `ReleaseDC` | `0x0073D408` / `0x0073D404` | VERIFIED |
| IAT slot, `OPENGL32!glClear` | `0x0073D21C` | VERIFIED |
| `CClientExoApp::MainLoop` | `0x005ED7D0` | VERIFIED |
| `CClientExoAppInternal::MainLoop` | `0x00602EB0` | VERIFIED |
| `CServerExoApp::MainLoop` | `0x004AE860` | VERIFIED |
| `CServerExoAppInternal::MainLoop` | `0x004BABB0` | VERIFIED |
| `CClientExoAppInternal::ProcessInput` | `0x006227E0` | VERIFIED |
| `CServerAIMaster::UpdateState` | `0x004B0B70` | VERIFIED |
| `CExoTimers::GetHighResolutionTimer` | `0x005E9260` | VERIFIED |
| `Global::UpdateScreen` | `0x00401C10` | VERIFIED |
| `Global::messagepump` | `0x00401510` | VERIFIED |
| `Global::WinMessageHandler` (WndProc) | `0x00402800`, `__stdcall` | VERIFIED |
| `Global::ActivateRenderWindow` / `Deactivate` | `0x00401E00` / `0x00401D90` | VERIFIED |
| `CClientExoApp::SetInputActive` | `0x005EDA10` | VERIFIED |
| `CExoRawInputInternal::GetMouseState` | `0x005E4050` | VERIFIED |
| `CExoRawInputInternal::GetKeyboardBuffer` | `0x005E3AE0` | VERIFIED |
| `CExoInput*` global | `0x007A39E4` | VERIFIED (not in the database's global list) |
| game `HWND` global | `0x007A39D8` | VERIFIED |
| quit flag | `0x007A39B8` | VERIFIED |
| client MainLoop ms, once per frame | `0x007A3C7C` | VERIFIED |
| server MainLoop ms, once per frame | `0x007A3C80` | VERIFIED |
| frame cap (0 = uncapped) | `0x007A3C64` | VERIFIED |
| video-mode-change pending | `0x007A3A2C` | INFERRED from use |
| `CAppManager+0x04 / +0x08 / +0x14` | client / server / reentrant stats | VERIFIED (live vtables match) |

`IDirectInputDevice8` vtable offsets used above: `Acquire` `0x1C`, `Unacquire` `0x20`,
`GetDeviceState` `0x24`, `GetDeviceData` `0x28`, `Poll` `0x64`. The database independently
labels `SetProperty` `0x18`, `SetDataFormat` `0x2C` and `SetCooperativeLevel` `0x34`, which
match the standard layout at indices 6, 11 and 13, and the struct size `0x80` matches 32
methods. VERIFIED by cross-check.

---

## 8. What is still open

- The 317 Hz measurement was taken with the window visible but unfocused. The rate while
  focused (and therefore possibly vsync-bound) has not been measured. The design must not
  depend on either number.
- Whether the render happens inside `CClientExoAppInternal::MainLoop` was not pinned to a
  specific call: `Camera::RenderScene` (`0x0045C540`) is referenced only from a vtable at
  `0x00741B28`, so the dispatch is virtual. The ordering argument in section 3 does not
  depend on locating it, since the swap follows everything either way.
- No hook has been installed. Nothing in this document has been executed against the game;
  it is all read-only observation plus a plan.

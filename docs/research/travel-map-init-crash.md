# Travel map-init crash — `CSWGuiMainInterface::InitializeMap` on a moduleless server (research 2026-08-01)

Why a module transition requested through `TravelController` kills the process one frame
later at `0x405F73`, what the client's per-frame GUI refresh assumes about the **server's**
module, and the one engine call that makes the transition safe. Verified against
`swkotor.exe` (GOG retail 1.0.3; disk == memory, no ASLR, base `0x400000`) by disassembly
(`tools/disasm.py`), caller scans (`tools/xrefs.py`), the jump tables read out of the image
by hand, and the sha256-matched symbol DB. Nothing was modified.

Builds on `docs/research/character-login-chain.md` (the in-process load replay) and
`docs/research/world-up-faction-crash.md` (house style, and the previous "our world is not
the engine's world" bug).

Tag key: **VERIFIED** = read from the binary here (or measured live already) · **INFERRED**
= deduced, not yet run · **NEEDS-LIVE** = must be confirmed on the running game.

## Headline

`CSWGuiMainInterface::InitializeMap 0x687E40` rebuilds the minimap image name
`"lbl_map%s"` from **the server's** module resref, every frame, from inside
`CClientExoAppInternal::MainLoop`. Its second instruction is
`CServerExoApp::GetModule 0x4AE6B0`, and it uses the result **without a null check**:
`lea ecx,[edi+0x30]; call CResRef::CopyToString 0x405F70`. A module transition destroys
the server's module inside `CServerExoAppInternal::UnloadModule 0x4B9240` (which sets
`[srvInt+0x10060] = 0x7F000000`, so `GetModule` cleanly returns **NULL**), and the
replacement `CSWSModule` is only constructed **on the next server-MainLoop frame**
(`0x4BB0BA`). In between, the outer game loop runs the **client** MainLoop — which is
where we die, with `ecx = 0 + 0x30` and `mov eax,[ecx]` faulting.

The engine's only protection is the client tearing its own world down first:
`CClientExoAppInternal::UnloadModule 0x5F8290` nulls `[clientInternal+0x18]` (the client
module) and resets `[clientInternal+0x20]` to `OBJECT_INVALID`, which closes two of the
five gates guarding that block. **Its sole in-play trigger is the queued network message
`Module_DumpPlayer` (`50 03 08`)** which the server sends from inside its own
`UnloadModule` (`0x4B93CC`) and the client handles at `HandleServerToPlayerModule 0x6684F0`
minor 8 → `CClientExoApp::UnloadModule 0x5ED430`. Our client had not processed it when
its MainLoop ran.

**`TravelController` is not at fault.** Disassembled side by side, the shipped console
cheat `Global::warp 0x60AF50` does *exactly* `SetMoveToModuleString` + 
`SetMoveToModulePending(1)` and nothing else — the same two calls
`Odyssey.TravelToModule` makes (VERIFIED). So the answer to Q4 is **(a)**: the transition
call is right; the client half of the world our load replay brought up does not complete
the teardown handshake in time.

**The fix is one call on the existing frame hook**, in the window the plugin already logs
(`epoch: world unloading`, one full frame before the crash): when
`CServerExoApp::GetModule()` is null and `CClientExoApp::GetModule()` is not, call
`CClientExoApp::UnloadModule 0x5ED430` — byte-for-byte the call the engine's own
`DumpPlayer` handler makes at `0x6685D3`.

## 1. The crash path, instruction by instruction (Q1)

### Fault site — VERIFIED

`CResRef::CopyToString 0x405F70` (thiscall, one arg — the out `CExoString*`):

```
0x405F70  sub  esp, 0x14
0x405F73  mov  eax, [ecx]        ; copy the 16-byte resref onto the stack -> FAULTS
0x405F75  mov  edx, [ecx+4]
...
0x405F9C  call 0x5E5140          ; CExoString::assign(char*, len)
```

`ecx` is the `CResRef*`. Reading `[ecx]` faults ⇒ `ecx` is not a mapped address.

### The caller — VERIFIED

`CSWGuiMainInterface::InitializeMap 0x687E40` (thiscall, 0 args):

```
0x687E40  push -1
0x687E42  push 0x72F420                ; SEH scope table  <-- appears on the crash stack
0x687E47  mov  eax, fs:[0]             ; ... SEH prologue
0x687E55  sub  esp, 0x24               ; 0x24 bytes of UNINITIALISED locals
0x687E58  mov  eax, [0x7A39FC]         ; CAppManager
0x687E60  mov  esi, ecx                ; this = CSWGuiMainInterface
0x687E62  mov  ecx, [eax+8]            ; CServerExoApp
0x687E6A  call 0x4AE6B0                ; CServerExoApp::GetModule()
0x687E73  mov  edi, eax                ; edi = CSWSModule*   ** NOT NULL-CHECKED **
0x687E75  call 0x5B3190                ; CExoString ctor (local at esp+0x1c)
0x687E82  call 0x5B3190                ; CExoString ctor (local at esp+0x14)
0x687E8B  push ecx                     ; &local string
0x687E8C  lea  ecx, [edi+0x30]         ; CSWSModule+0x30 = area_name (CResRef)  <-- 0x30 when edi==0
0x687E94  call 0x405F70                ; CResRef::CopyToString  ** CRASH **  (ret 0x687E99)
0x687E99  lea  ecx, [esp+0x1c]
0x687E9D  call 0x5E5670                ; CExoString::CStr
0x687EA7  push 0x747774                ; "lbl_map%s"
0x687EAD  call 0x5E5680                ; CExoString::Format
0x687EB2  mov  eax, [edi+0x218]        ; module's area array (would fault next anyway)
...
0x687EE5  call 0x414C00                ; CSWGuiBorderParams::SetFillImage on map_label+0x70
0x687F30  call [eax+4]                 ; resize CSWGuiMainInterface+0x5E00 (map_label)
```

String literal at `0x747774` read out of `.rdata`: **`"lbl_map%s"`** (VERIFIED). So the
function's whole job is *"name the minimap texture after the currently loaded module and
size the map label"* — and it asks the **server** for that name.

`edi == 0` ⇒ `ecx = 0x30` ⇒ AV at `0x405F73`. Matches the probe exactly.

### The stack, decoded — VERIFIED

| value | meaning |
| --- | --- |
| `0x687E99` | genuine: return into `InitializeMap` from `CopyToString` (`call` at `0x687E94`) |
| `0x40BD7A` | **stale** — `CSWGuiManager::PanelExists+0xA`, the return of its `call 0x40ABA0` (`GetPosInPanelList`) |
| `0x62AA6D` | **stale** — `CGuiInGame::SetSWGuiStatus+0x6D`, the return of its `call 0x40BD70` (`PanelExists`) |
| `0x72F420` | `InitializeMap`'s own SEH scope table (pushed at `0x687E42`) |
| `0x602FB8` | genuine: return into `CClientExoAppInternal::MainLoop` (`call 0x62B0A0` at `0x602FB3`) |
| `0x7273A8` | `CClientExoAppInternal::MainLoop`'s SEH scope table (pushed at `0x602EB8`) |
| `0x4046C2` | genuine: return into the outer game loop (`call CClientExoApp::MainLoop 0x5ED7D0` at `0x4046BD`) |

The two "middle frames" are an artefact, and the mechanism is exact rather than hand-wavy:
`CClientExoAppInternal::MainLoop` calls **`SetSWGuiStatus(1, 1)`** eight instructions
earlier, at `0x602FAB`, at the *same stack depth*; that call's frame (and its nested
`PanelExists` → `GetPosInPanelList`) left its two return addresses exactly where
`InitializeMap`'s `sub esp,0x24` block of uninitialised locals now sits. They are the
**previous** call at that depth, not the current chain. VERIFIED — `PanelExists 0x40BD70`
is 21 bytes long, calls only `0x40ABA0`, and cannot reach `InitializeMap`.

The genuine chain is a **tail-call thunk**:

```
0x602FB0  mov  ecx, [esi+0x40]          ; CClientExoAppInternal+0x40 = CGuiInGame
0x602FB3  call 0x62B0A0                 ; CGuiInGame::InitializeMap
--
0x62B0A0  mov  ecx, [ecx+0x90]          ; CGuiInGame+0x90 = main_interface
0x62B0A6  test ecx, ecx
0x62B0A8  je   0x62B0AF                 ; null main interface -> return (a real guard)
0x62B0AA  jmp  0x687E40                 ; CSWGuiMainInterface::InitializeMap  (TAIL JUMP)
0x62B0AF  ret
```

so `InitializeMap`'s return address is MainLoop's `0x602FB8`, and no `CGuiInGame` frame
exists on the stack at all.

### Where the null comes from — VERIFIED

`CServerExoApp::GetModule 0x4AE6B0` is a thunk (`mov ecx,[ecx+4]; jmp 0x4B14F0`).
`CServerExoAppInternal::GetModule 0x4B14F0`:

```
eax = [srvInt+0x10060]                     ; the module's object id
if (eax == [srvInt+0x1B918]) return [srvInt+0x1B91C];   ; one-entry cache
obj = CGameObjectArray::GetGameObject([srvInt+0x1005C], eax)
if (!obj) { [srvInt+0x1B918] = 0x7F000000; [srvInt+0x1B91C] = 0; return 0; }
[srvInt+0x1B918] = eax; [srvInt+0x1B91C] = obj->vtbl[0x20]();   ; as-module
```

and `CServerExoAppInternal::UnloadModule 0x4B9240` writes

```
0x4B94B0  mov dword [srvInt+0x10060], 0x7F000000   ; module id := OBJECT_INVALID
0x4B94CE/0x4B9501  [srvInt+0x10054] = new CFactionManager
0x4B954B           [srvInt+0x1005C] = new CGameObjectArray   ; the plugin's world epoch token
```

So after the unload `GetModule()` returns a **clean NULL**, not a dangling pointer
(VERIFIED — that is why the fault address is `0x30` and not something random).

### The five gates that let MainLoop get there — VERIFIED

`CClientExoAppInternal::MainLoop 0x602EB0` (`esi` = clientInternal, DB offsets confirm
every field):

| # | gate | address |
| --- | --- | --- |
| 1 | `[esi+0x288]` (`area_not_ready_to_display`) != 0 | `0x602F09 je 0x603254` |
| 2 | `[esi+0x18]` (**client** `CSWCModule*`) != 0 | `0x602F0F je 0x603254` |
| 3 | `GetCreatureByGameObjectID([esi+0x20])` != 0 (cache `[esi+0x3BC]/[esi+0x3C0]`) | `0x602F4B je 0x603254` |
| 4 | `CSWCModule::GetMiniGame 0x641980` == 0 (path B, the non-minigame path) | `0x602F82 je 0x602F8C` |
| 5 | `[esi+0x40]` (`CGuiInGame`) and `[CGuiInGame+0x90]` (`main_interface`) != 0 | `0x62B0A6` |

With all five open, MainLoop unconditionally runs `SetInputClass(0)`,
`CSWPlayerControl::SetEnabled(1)`, `SetSWGuiStatus(1,1)` and **`InitializeMap`** — every
frame. Gates 2 and 3 are exactly what `CClientExoAppInternal::UnloadModule 0x5F8290`
closes (`[esi+0x20] = [0x746644]` = `0x7F000000` at `0x5F82C6`; `[esi+0x18] = 0` at
`0x5F8374`).

## 2. `CGuiInGame::SetSWGuiStatus 0x62AA00 +0x6D` (Q2)

VERIFIED. Thiscall, 8 bytes of args; `arg1` = status (1..4), `arg2` = "also reassert the
pause panel". Jump table at `0x62AB18`, read out of the image:

| arg1 | effect on `[this+0x34]` (the status word) |
| --- | --- |
| 1 | leave it if it is 3, else set **1** |
| 2 | set **2** unconditionally |
| 3 | leave it if it is 2, else set 3 |
| 4 | leave it if it is 2, else set 1 |

Then, gated on `[this+0x108]` (`initialized`) and `[this+0x38]` (`manager`):

* status == 1 → if `CSWGuiManager::PanelExists(main_interface)` is false (`0x62AA68` — the
  call whose return address is the stale `0x62AA6D`), `AddPanel(main_interface, 4, 1)`
  (`0x40BC70`) then `SendPanelToBack 0x40BD20`. `+0x6D` is simply "the main interface was
  already registered".
* status != 1 → the mirror: if the panel *is* registered, `RemovePanel 0x40C830`.
* then the same for `[this+0x7C]` (`in_game_pause`) when `arg2 != 0`.

**When MainLoop calls it:** not on a transition — *every* in-world frame, at `0x602FA9`
(`push ebp; push ebp` with `ebp = 1` ⇒ `SetSWGuiStatus(1, 1)`), immediately before
`InitializeMap`. It is the "make sure the in-game HUD is the visible GUI" heartbeat. Its
presence in the crash dump is coincidence, not causation (§1).

## 3. What a real module transition sets up, in order (Q3)

All VERIFIED by disassembly. Entry point: the server's own MainLoop notices
`[srvInt+0x10080]` (`MoveToModulePending`) and calls
`CServerExoAppInternal::StartNewModule 0x4BA920` at `0x4BAD93`, then clears the flag
(`0x4BAD98`).

**Frame N, server MainLoop:**

1. `StartNewModule 0x4BA920(CExoString* module)` — refuses if `[srvInt+0x100B4]`
   (`loading`) is already 1 (`0x4BA947`). Client-side pre-roll:
   `GetNextLoadScreenHintSTRREF`/`SetLoadScreenHint`/`SetLoadScreenHintUpdateTimer`,
   `SetCanSendDialog(0)`, `CGuiInGame::SetGlobalDialogState(0) 0x62EC60`,
   `CGuiInGame::CloseDialog 0x6332B0`, `CExoSound::StopAllOneShots 0x5D5E70`,
   `CClientExoApp::PlayLoadModuleMovies 0x5EDB20`.
2. Existence check for `MODULES:<name>` as restype `0x7DB` (`.mod`) or `0xBBA` (`.rim`);
   on failure it returns 0 and **nothing happens at all** (`0x4BAA82`).
3. `[srvInt+0x100B4] = 1` (loading) and **`[srvInt+0x1007C] = 1`
   (`DoingStartNewModule`)** (`0x4BAAC3/0x4BAAC9`) — see the warning in §5.
4. `ForceRunAreaOnExitScripts 0x4B4F50` → **`StorePlayerCharacters 0x4B2BA0`** (writes
   `TEMP:pifo` from the live creatures; returns 0 if no player has a creature, in which
   case the whole transition is abandoned at `0x4BAADD`) → **`StoreCurrentModule 0x4B2E70`**
   (serialises the outgoing area; this is the call `TravelController`'s doc comment warns
   about, and it drives `Global::UpdateScreen` for the progress bar).
5. `DoModuleEnterSaveCleanup 0x4B2380`, `AutoSaveOnEnteringModule 0x4B5050` /
   `DoPCAutosave 0x4B8300`.
6. **`ExecuteLoadModule 0x4BA820`**: `SendServerToPlayerModule_StartStartNewModule
   0x56CBF0` (`50 03 0B` — the client handler `0x652840` only does
   `SetInStartNewModule(client,1)` = `[clientInternal+0x220] = 1`, nothing else),
   `CExoSound` stop, pause **both** world timers (`0x4ADFF0`),
   `CClientExoApp::DisableInput 0x5EDBE0`, then `LoadModule 0x4B95B0(name, 0)`.
7. `LoadModule` caches `GetModule()` first (`0x4B95E0`), re-affirms `loading = 1`, and if a
   module exists calls **`UnloadModule 0x4B9240`** (`0x4B9698`). `UnloadModule`:
   * per player in `[srvInt+0x10064]`: **`SendServerToPlayerModule_DumpPlayer 0x56CCB0`**
     (`50 03 08`) at `0x4B93CC`, destroy the player's creature, `[player+0x24] = 0` (login
     state), `[player+0x7C] = 0`, `[player+0x34] = OBJECT_INVALID`;
   * client party GUI teardown (`GetSWParty 0x5ED8B0` → `0x634A40`), party table reset;
   * **`[srvInt+0x10060] = OBJECT_INVALID`**, faction manager and game-object array
     replaced.
   `LoadModule` then does resource-directory plumbing and returns. **It never calls
   `Global::UpdateScreen`** (VERIFIED by the 49-site xref list for `0x401C10`: no caller
   lies between `0x4B95B0` and `0x4BA820`).
8. Back in MainLoop: `[srvInt+0x10080] = 0`, then `cmp [[0x7A39FC]+0x14], 0` → non-zero →
   `jmp 0x4BAF1A` → **MainLoop returns**. The new module has *not* been created.

**Frame N+1, server MainLoop** enters the app-state branch at `0x4BAF6A` and only there
does `operator new(0x224)` → `CSWSModule::Constructor 0x4C84A0` → **`[srvInt+0x10060] =
[module+0x20]`** (`0x4BB0BA`) → `CSWSModule::LoadModuleStart 0x4C9050` (`0x4BB0E8`).
Subsequent frames run `LoadModuleInProgress 0x4C5720`, then `LoadModuleFinish 0x4C5880`,
`[srvInt+0x10008] = 1` (mode) and `[srvInt+0x100B4] = 0` (`0x4BB193/0x4BB19A`), and from
there the login chain of `character-login-chain.md` resumes (RunModule → because
`DoingStartNewModule` is 1, `EndStartNewModule` → `LoginIFOCharacter` →
`LoadCharacterFromIFO(-1)` = the `TEMP:pifo` step 4 just wrote → client `ModuleLoaded` →
`InitiateModuleForPlayer` → area ack → mode 2).

**The client side of the transition is a single queued message.** The client's whole
teardown is `HandleServerToPlayerModule 0x6684F0` minor **8** → `CClientExoApp::UnloadModule
0x5ED430` → `CClientExoAppInternal::UnloadModule 0x5F8290`, which sets
`[clientInternal+0x20] = OBJECT_INVALID`, destroys the `CSWCModule` and nulls
`[clientInternal+0x18]`, resets `[clientInternal+0x14]` (client object array), pause state
and the pause exclusion list, and calls `SetAreaNotReadyToDisplay(1)`. The client rebuilds
its module later, from module message minor **1** (`Module_Info 0x567EF0`), which calls
`CClientExoApp::CreateModule 0x5ED440` when `GetModule()` is null (`0x668542–0x668554`) and
answers with `SendPlayerToServerModule_ModuleLoaded 0x677490`.

**The initialisation the replay may never have performed** is therefore *not* a missing
setup call — it is the delivery of that one message. `CNetLayer::SendMessageToPlayer
0x5D4CB0` only appends to the connection's `0x10000`-byte frame buffer
(`[[netlayer+0x2000C]+0x20000]`); nothing dispatches until
`CNetLayer::ProcessReceivedFrames 0x5D57D0` runs. VERIFIED: the six call sites of
`ProcessReceivedFrames` are `0x401D12`/`0x401D2C` (inside `Global::UpdateScreen`, and only
when its **second** argument is 1), `0x4BAC31`/`0x4BAF71` (server MainLoop), and
`0x6032A1`/`0x6034F2` (client MainLoop — **after** the crash site, and gated on
`[[0x7A39FC]+0x14] == 1` at `0x60328B`).

**Outer-loop order** (VERIFIED; back edge found at `0x404A11 → 0x404570`):
input/render → **`CClientExoApp::MainLoop` `0x4046BD`** → **`CServerExoApp::MainLoop`
`0x404757`** (up to twice) → present/`SwapBuffers` `0x4048B8` → repeat. So the server
destroys the module late in frame N and the *very next* thing that touches it is the client
MainLoop at the top of frame N+1 — **~16 ms later, which is precisely the measured 15 ms.**

## 4. Which is it — the replay, or a missing warp step? (Q4)

### The `warp` cheat does nothing we do not do — VERIFIED

`Global::warp 0x60AF50`, in full:

```
0x60AF6B  call 0x60A300                 ; Global::GetCheatsTurnedOn; bail if 0
0x60AF83  call 0x6FB5E0                 ; strchr(arg, ' ') -> truncate at the first space
0x60AFC3  call 0x5E5A90                 ; CExoString::CStrConstructor(name)
0x60AFE1  call 0x4AECD0                 ; CServerExoApp::SetMoveToModuleString(&str)
0x60AFF5  call 0x5E5C20                 ; ~CExoString
0x60B002  push 1
0x60B004  call 0x4AECC0                 ; CServerExoApp::SetMoveToModulePending(1)
0x60B00A  ret
```

That is the entire cheat. `Odyssey.TravelToModule` makes the same two engine calls, plus
`SetMoveToModuleStartWaypoint 0x4AED30` (which the debug menu
`CSWGuiLoadModuleDebugMenu::OnModulePicked 0x6CF9D0` also skips, and which only writes
`[srvInt+0x1008C]`, read by `RunModule` at `0x4B3D10` when placing creatures). **Nothing is
omitted.** The other four callers of `SetMoveToModulePending` (`0x50DA04` in
`CSWSArea::EventHandler`, `0x54457A` in `ExecuteCommandStartNewModule`, `0x58C192` in
`CSWSDoor::EventHandler`, `0x58F2CD` in `CSWSTrigger::EventHandler`, `0x6CFA3D` in the debug
menu) all reduce to the same pair; the trigger/door ones additionally set the waypoint.

So the answer is **(a)**, with one honest caveat below: the transition request is correct
and complete, and the defect is that on our world the client still owns a module (gates 2
and 3 open) when the client MainLoop next runs.

### The caveat, stated plainly — NEEDS-LIVE

Static analysis says the *same* one-frame window exists for a retail `warp`: I could not
find any `ProcessReceivedFrames` (or `UpdateScreen(…, 1, …)`) call site that executes
between the server's `UnloadModule` in frame N and the client's MainLoop at the top of
frame N+1. Either retail closes it through a path this pass did not locate, or retail has
the same latent window and normal transitions survive it for a reason our replayed world
does not reproduce (the obvious candidate: on a click-loaded world the client processes
`DumpPlayer` from an earlier queued batch, or one of gates 1/3/5 happens to be shut).

This does **not** block the fix: the fix does not depend on knowing which of the five gates
differs, because it closes gates 2 and 3 unconditionally, exactly the way the engine does.
The live probe in §6 answers the question for the record in one run.

## 5. THE FIX (Q5)

### Fix 1 — RECOMMENDED: tear the client's world down in the window we already observe

`PluginHost.OnFrame` runs on the `SwapBuffers` frame hook (IAT slot `0x73D00C`, called at
`0x4048B8`) — i.e. **after** the server MainLoop that unloads and **before** the client
MainLoop that crashes. The plugin already proves it runs there: it printed
`epoch: world unloading` on exactly that frame (`Odyssey.TryGetWorldEpoch` returns false
as soon as `[srvInt+0x100B4] != 0` or the mode word leaves 2).

Add a guard that runs **before the epoch gate** (like `MenuLoadController` and
`NativeBrowserController` already do — PluginHost.cs line ~249; the epoch gate returns
early during exactly this window):

```csharp
// CClientExoApp::UnloadModule() — thunk 0x5ED430: `mov ecx,[ecx+4]; jmp 0x5F8290`.
// thiscall on CClientExoApp, no arguments (0x5F8290 ends in a bare `ret`).
private static readonly delegate* unmanaged[Thiscall]<nint, void> ClientUnloadModule =
    (delegate* unmanaged[Thiscall]<nint, void>)0x005ED430;

// CClientExoApp::GetModule() -> CSWCModule*  (0x5ED530 -> [clientInternal+0x18])
private static readonly delegate* unmanaged[Thiscall]<nint, nint> ClientGetModule =
    (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED530;

/// MAIN THREAD ONLY. Returns true when it performed the teardown.
public static bool SyncClientToUnloadedModule()
{
    nint appManager = *(nint*)0x007A39FC;
    if (appManager == 0) return false;

    nint client = *(nint*)(appManager + 0x04);
    nint server = *(nint*)(appManager + 0x08);
    if (client == 0 || server == 0) return false;

    if (ClientGetModule(client) == 0) return false;   // client already torn down
    if (GetModuleFn(server) != 0) return false;       // server module alive: normal frame

    ClientUnloadModule(client);                       // == the DumpPlayer handler, 0x6685D3
    return true;
}
```

Why this is the right level:

* It is **the engine's own routine, called the engine's own way**. `HandleServerToPlayerModule
  0x6684F0` minor 8 does literally `mov ecx,[[0x7A39FC]+4]; call 0x5ED430` (`0x6685CA–
  0x6685D3`). We are not synthesising state, we are performing the step the queued message
  will perform a frame late.
* It closes gates 2 and 3 (`[clientInternal+0x18] = 0`, `[clientInternal+0x20] =
  OBJECT_INVALID`), so `CClientExoAppInternal::MainLoop` skips the whole in-world block at
  `0x602F0F`/`0x602F4B` and never reaches `InitializeMap`.
* It is **self-limiting**: after it runs, `ClientGetModule` is 0 and the guard never fires
  again for that transition.
* The engine puts the world back by itself: module message minor 1 (`Module_Info`) calls
  `CClientExoApp::CreateModule 0x5ED440` precisely when `GetModule()` is null
  (`0x668542–0x668554`), which is the state we just produced.

Placement: a small `ModuleTransitionGuard` (or a static method on `TravelController` called
from `PluginHost.OnFrame` before the epoch gate). It must **not** be gated on
`TravelController.HasPendingRequest` — script-, door- and trigger-driven transitions
(`0x50DA04`, `0x58C192`, `0x58F2CD`) reach the same window, and a peer-initiated warp will
too.

### Fix 2 — belt and braces, only if Fix 1 proves insufficient: entry guard on `InitializeMap`

House template `PartyFactionGuard.cs`. One patch at the callee covers every caller.

* **Site**: `0x687E40`, `CSWGuiMainInterface::InitializeMap`.
* **Original bytes (7)**: `6A FF 68 20 F4 72 00` = `push -1; push 0x72F420` (the SEH
  prologue). Overwrite with `E9 <rel32>` + `90 90`.
* **Resume target**: `0x687E47` (`mov eax, fs:[0]`), with the two pushes reproduced.
* **Stub** (must preserve `ecx`, the `this` that `0x687E60` consumes; `GetModule` clobbers
  eax/ecx/edx):

```
    push ecx                  ; save this
    mov  eax, [0x7A39FC]
    test eax, eax
    je   bail
    mov  ecx, [eax+8]         ; CServerExoApp
    test ecx, ecx
    je   bail
    call 0x4AE6B0             ; CServerExoApp::GetModule()  (thiscall, 0 args)
    test eax, eax
    je   bail
    pop  ecx                  ; restore this
    push -1
    push 0x72F420
    jmp  0x687E47
bail:
    pop  ecx
    ret                       ; thiscall, 0 args -> plain ret, no stack cleanup
```

Skipping the minimap refresh for the frames where there is no module is semantically
correct (there is no module to name), and `InitializeMap` is idempotent — it runs again
every frame once the new module exists. Not recommended as the *primary* fix because it
cures only this dereference while leaving the client running a full in-world frame against
a dead server world; Fix 1 restores the invariant instead.

### Fix 3 — pump the engine's own message. REJECTED as primary

`CNetLayer::ProcessReceivedFrames 0x5D57D0` on `GetNetLayer(client)` (client vtable `+0x10`,
already bound in `MenuLoadController`) with argument 1 — the same call the client MainLoop
makes at `0x6032A0`. This would deliver `DumpPlayer` and let the engine do everything.
Rejected as the primary because manual pumping has already cost this project one crash
(`MenuLoadController` phase 3: "pumping the server ourselves double-steps the module
finalisation"), and because it delivers *every* queued message a frame early, not just the
one we need. Keep it in the drawer.

### Do NOT clear `DoingStartNewModule` on a travel — VERIFIED, and easy to get wrong

`character-login-chain.md`'s fix (`SetDoingStartNewModule(server, 0)`) is **menu-load only**.
`StartNewModule` deliberately sets `[srvInt+0x1007C] = 1` at `0x4BAAC9` and
`StorePlayerCharacters 0x4B2BA0` writes a real `TEMP:pifo` from the live creatures right
after, so the broadcast flavour (`EndStartNewModule` → `LoginIFOCharacter` →
`LoadCharacterFromIFO(-1)`) is the *correct* path for an in-game transition. Clearing it
would send `RunModule` to `LoadPrimaryPlayer` against a freshly loaded module and lose the
carried-over party.

## Crash-risk register

| Risk | Assessment |
| --- | --- |
| Calling `CClientExoApp::UnloadModule` twice (ours, then the engine's `DumpPlayer` a frame later) | Safe. The second entry re-runs only the idempotent prologue (party leader → `OBJECT_INVALID`, `SetPauseState(2)/(1)`, `ClearPauseExclusionList`), the sound/fade block is one-shot guarded by bit 4 of `[clientInternal+0x37C]` (`0x5F8303`), and the module branch early-outs at `0x5F8361` (`[esi+0x18] == 0 → je 0x5F8459`). VERIFIED. |
| Tearing the client down "too early" | We do it ~16 ms before the engine would. Retail's own trigger is the same `UnloadModule`, in the same transition, on the same thread. The client's rebuild is driven by `Module_Info` (minor 1), which explicitly handles `GetModule() == null` by calling `CreateModule` (`0x668542`). VERIFIED. |
| Guard runs while there is no server or no client | Both null-checked before use; `GetModule` is never called on a null `CServerExoApp` (the thunk would fault on `mov ecx,[ecx+4]`). |
| Guard fires at the main menu | `[appManager+8]` (server) is null at the menu, so it returns immediately — same shape as `MenuLoadController`'s `Odyssey.HasServer` test. |
| Guard fires during the *from-menu* load (server exists, module not yet built) | It would call `ClientUnloadModule` while `[clientInternal+0x18]` is non-null — which at that point it is not (no client module exists before the first `Module_Info`). If a live run ever shows it firing during a menu load, gate it additionally on `[srvInt+0x100B4] != 0` (loading) so it only acts inside a real transition. INFERRED / NEEDS-LIVE. |
| `StorePlayerCharacters` returns 0 (no player has a creature) | `StartNewModule` clears `DoingStartNewModule` and returns 0 at `0x4BAADD–0x4BAAF2`: the travel silently does nothing, the pending flag has already been cleared by MainLoop, and `TravelController` will have reported success. Cosmetic, but worth logging (VERIFIED). |
| Destination module missing on disk | `StartNewModule` fails the `CExoResMan::Exists` check (`0x4BAA27`/`0x4BAA4D`) and returns 0 before touching any state — no crash, no transition. VERIFIED. |
| Byte-patching `InitializeMap` (Fix 2) | The original 7 bytes must verify before patching; the stub must `push ecx`/`pop ecx` around `GetModule` (it clobbers ecx) and must reproduce both SEH pushes before resuming at `0x687E47`, or the function's `fs:[0]` chain is corrupted. |
| Doing nothing | Deterministic: every module transition on a replay-loaded world faults at `0x405F73` one frame after the server unloads. Measured twice. |

## How to verify live

1. **One log line from the frame hook while a transition is in flight** (the frame that
   already prints `epoch: world unloading`), before any fix:
   `serverModule = CServerExoApp::GetModule 0x4AE6B0`, `clientModule =
   [clientInternal+0x18]`, `creatureId = [clientInternal+0x20]`, `areaFlag =
   [clientInternal+0x288]`, `guiInGame = [clientInternal+0x40]`, `mainInterface =
   [guiInGame+0x90]`, `loading = [srvInt+0x100B4]`. The expectation that predicts the
   crash is `serverModule == 0` with all five gates open.
2. **The control run**: do the same on a world loaded by clicking the save in the menu, and
   warp with the `~` console (`warp end_m01ab`). If retail's client shows `clientModule ==
   0` on that frame while ours shows non-zero, the `DumpPlayer` round-trip is the whole
   difference and §4's caveat is closed. If retail shows the same open gates, retail shares
   the latent window and Fix 1 is a strict improvement for both.
3. **After Fix 1**: the guard should log once per transition, `clientModule` should read 0
   on the next frame, the crash should not occur, and the world should come up in the new
   module with the party intact (`InitiateModuleForPlayer` takes the existing-object branch
   because `StorePlayerCharacters`/`LoadCharacterFromIFO(-1)` restored the PC).
4. **Regression watch**: confirm the minimap image is correct after the transition (it is
   rebuilt by `InitializeMap` on the first frame the new module exists) and that
   `Odyssey.TryGetWorldEpoch` starts returning true again with the new module name.

## Key addresses

| Address | Symbol / meaning |
| --- | --- |
| `0x405F70` | `CResRef::CopyToString(CExoString*)` — faults at `+3` (`mov eax,[ecx]`) on a null/garbage `CResRef` |
| `0x687E40` | `CSWGuiMainInterface::InitializeMap` — `GetModule` at `+0x2A`, **unchecked**; `CopyToString(module+0x30)` at `+0x54`; builds `"lbl_map%s"` (`0x747774`) |
| `0x62B0A0` | `CGuiInGame::InitializeMap` — thunk: `[this+0x90]` null-check then **tail-jmp** to `0x687E40` (why no `CGuiInGame` frame is on the stack) |
| `0x602EB0` | `CClientExoAppInternal::MainLoop` — gates `0x602F09` (`+0x288`), `0x602F0F` (`+0x18` client module), `0x602F4B` (player creature), `0x602F82` (minigame); `SetSWGuiStatus(1,1)` `0x602FAB`; `InitializeMap` `0x602FB3`; netlayer pump `0x6032A1` (too late) |
| `0x62AA00` | `CGuiInGame::SetSWGuiStatus(status, bPause)` — status table `0x62AB18`; `PanelExists` at `0x62AA68` (the stale `+0x6D`) |
| `0x40BD70` | `CSWGuiManager::PanelExists` — 21 bytes, calls only `GetPosInPanelList 0x40ABA0` (the stale `+0xA`) |
| `0x4AE6B0` / `0x4B14F0` | `CServerExoApp::GetModule` thunk / `CServerExoAppInternal::GetModule` — id `[srvInt+0x10060]`, cache `[srvInt+0x1B918]/[+0x1B91C]`, returns clean NULL when the id no longer resolves |
| `0x4B9240` | `CServerExoAppInternal::UnloadModule` — `DumpPlayer` send `0x4B93CC`; player state reset `0x4B9414`; `[srvInt+0x10060] = OBJECT_INVALID` `0x4B94B0`; new object array `0x4B954B` |
| `0x56CCB0` / `0x6684F0`+8 / `0x5ED430` | `SendServerToPlayerModule_DumpPlayer` (`50 03 08`) / client dispatch (jump table `0x668604`, minor 8 → `0x6685CA`) / **`CClientExoApp::UnloadModule` — the fix call** |
| `0x5F8290` | `CClientExoAppInternal::UnloadModule` — `[+0x20] = OBJECT_INVALID` `0x5F82C6`, one-shot bit `0x5F8303`, `[+0x18] = 0` `0x5F8374`, module-branch early-out `0x5F8361`, `SetAreaNotReadyToDisplay(1)` `0x5F8414` |
| `0x5ED530` / `0x644760` | `CClientExoApp::GetModule` → `[clientInternal+0x18]` |
| `0x4BA920` | `CServerExoAppInternal::StartNewModule` — loading/`DoingStartNewModule` set `0x4BAAC3/0x4BAAC9`; `StorePlayerCharacters 0x4B2BA0`; `StoreCurrentModule 0x4B2E70`; `ExecuteLoadModule 0x4BAB8F` |
| `0x4BA820` | `ExecuteLoadModule` — `StartStartNewModule 0x4BA86D`, pause timers, `DisableInput`, `LoadModule 0x4BA912` |
| `0x4B95B0` | `CServerExoAppInternal::LoadModule` — `UnloadModule 0x4B9698`; **no `UpdateScreen` anywhere in it** |
| `0x4BABB0` | `CServerExoAppInternal::MainLoop` — `StartNewModule 0x4BAD93`, pending clear `0x4BAD98`, app-state branch `0x4BAF6A`, **new module id `0x4BB0BA`**, `LoadModuleStart 0x4BB0E8`, `LoadModuleFinish 0x4BB167`, mode/loading `0x4BB193/0x4BB19A` |
| `0x60AF50` | `Global::warp` — the whole cheat: `SetMoveToModuleString 0x4AECD0` + `SetMoveToModulePending(1) 0x4AECC0`. Nothing else |
| `0x4AECC0` / `0x4AECD0` / `0x4AED30` | `SetMoveToModulePending` (`+0x10080`) / `SetMoveToModuleString` (`+0x10084`) / `SetMoveToModuleStartWaypoint` (`+0x1008C`) |
| `0x5D4CB0` / `0x5D57D0` | `CNetLayer::SendMessageToPlayer` — **queues only**, into `[[netlayer+0x2000C]+0x20000]` / `ProcessReceivedFrames` — the only dispatcher (`0x401D12`, `0x401D2C`, `0x4BAC31`, `0x4BAF71`, `0x6032A1`, `0x6034F2`) |
| `0x401C10` | `Global::UpdateScreen(dt, bServiceServer, bNoRender)` — runs the server MainLoop **and** both client pumps only when arg2 == 1 (`0x401CDB`) |
| `0x4046BD` / `0x404757` / `0x4048B8` | outer loop: client MainLoop → server MainLoop → `SwapBuffers` (the plugin's frame hook, IAT `0x73D00C`); back edge `0x404A11 → 0x404570` |
| `0x746644` | `OBJECT_INVALID` (`0x7F000000`) constant used by the client unload |

# In-game GUI transition — why the in-process load never leaves the main menu (research 2026-08-01)

What the CLIENT does, call for call, to go from "main menu + load screen" to "in-game world
view"; which of those steps `MenuLoadController` skips; and the exact ordered set of calls
that completes the transition. Verified against `swkotor.exe` (GOG retail 1.0.3; disk ==
memory, no ASLR, base `0x400000`) by disassembly (`tools/disasm.py`), caller scans
(`tools/xrefs.py`), a byte-pattern scan for field writes, and the sha256-matched symbol DB.
Nothing was modified.

Companion to `docs/research/character-login-chain.md` (the server side of the same load),
`docs/research/world-up-faction-crash.md` (what happens once the world is up),
`docs/research/travel-map-init-crash.md` (the `InitializeMap` null-module fault) and
`docs/research/native-browser-panel.md` (the panel-sweep idiom this doc reuses).

Tag key: **VERIFIED** = read from the binary here · **INFERRED** = deduced, not yet run ·
**NEEDS-LIVE** = must be confirmed on the running game.

## Headline

**The engine does almost all of the transition by itself, every frame, inside
`CClientExoAppInternal::MainLoop 0x602EB0`.** Once three client-side gates are true it calls
`SetSWGuiStatus(1,1)` (the HUD), `CGuiInGame::InitializeMap` (the minimap),
`EnableInput` and `HideLoadScreen(1,1)`, and on the first such frame it starts the fade-in
and clears the unpauseable state. There is no "show the world" function to call — there is a
state to reach.

**What our replay actually gets wrong is one line.** Retail's `CSWGuiMainMenu::OnLoadSaveGame
0x67B1A0` **marks the main menu `0x400` for the GUI manager's sweep** (`0x67B22C`), which
deletes it on the next `CSWGuiManager::Update`. `MenuLoadController` reproduces
`CSWGuiSaveLoad::LoadGame` but not `OnLoadSaveGame`, so **the main menu is never marked and
never leaves the panel list** — it keeps drawing, forever, over whatever else happens. The
Co-op flow makes this certain: `NativeBrowserController` marks the menu `0x400` when it opens
its panel, and the browser's close path calls `DisplayMainMenu 0x5ED420(0)` which builds a
**fresh** menu (`native-browser-panel.md` §"Back click"). That fresh menu is the one on the
screenshot, and nothing in `MenuLoadController` ever kills it.

The second, independent defect is that `MainLoop`'s in-world block is gated on client-side
state our replay may not reach — `[clientInternal+0x288]`, a client module, and a client
player creature. `DisableInput` is *not* a separate bug: `MainLoop` calls
`CClientExoAppInternal::EnableInput 0x61F7D0` unconditionally at `0x602FBA` inside that same
block. Input is stuck disabled for exactly the same reason the HUD is missing — the block
never runs. Fix the gates and input, the load screen, the HUD and the fade all come back
together.

## 1. The client-side presentation state machine (Q1)

### 1.1 `CGuiInGame::SetSWGuiStatus 0x62AA00` — VERIFIED

`__thiscall`, `ret 8` → **two** arguments: `SetSWGuiStatus(int status, int bDestroyPause)`.
Dispatch is a 4-entry jump table at `0x62AB18` on `status-1` (`dec eax; cmp eax,3;
ja 0x62AB12` at `0x62AA07–0x62AA0F`), writing the status field **`[gui+0x34]`**:

| `status` | effect on `[gui+0x34]` | site |
| --- | --- | --- |
| 1 | `= 1` unless it is already `3` | `0x62AA1C` |
| 2 | `= 2`, unconditionally | `0x62AA21` |
| 3 | `= 3` unless it is `2` | `0x62AA2A` |
| 4 | `= 1` unless it is `2` | `0x62AA35` |

Then two guards, both hard early-outs: **`[gui+0x108]` (`initialized`) must be non-zero**
(`0x62AA42–0x62AA4A`) and **`[gui+0x38]` (`manager`) must be non-zero** (`0x62AA50`).

* **`[gui+0x34] == 1` → bring the HUD up** (`0x62AA5B`): if
  `CSWGuiManager::PanelExists 0x40BD70(mgr, main_interface)` is false, then
  `AddPanel 0x40BC70(main_interface, 4, 1)` and `SendPanelToBack 0x40BD20(main_interface)`.
  `main_interface` is **`[gui+0x90]`** (`CSWGuiMainInterface*` — the HUD: minimap, party
  portraits, action bar). Then, if `[gui+0xB3C]` and `[gui+0x7C]` (`in_game_pause`) are both
  non-null, the same Add/SendToBack for the pause panel.
* **anything else → take the HUD down** (`0x62AAD1`): if the main interface is registered,
  `RemovePanel 0x40C830(main_interface)`; and if `bDestroyPause` is non-zero and the pause
  panel is registered, `CSWGuiPanel::setToDestroy 0x624A00(pause, 0)`.

So `SetSWGuiStatus(1, 1)` **is** the in-game GUI bring-up, and `SetSWGuiStatus(2, 1)` is the
teardown — the latter is exactly what the return-to-main-menu path runs at `0x6034B3`,
directly after `CAppManager::DestroyServer 0x401280`, `DisplayMainMenu 0x5FCA30` and
`CGuiInGame::ShutDown 0x62C310`. All 14 callers: `0x602FAB` (MainLoop, `(1,1)`), `0x6034B3`
(to-menu, `(2,1)`), `0x62CBDD` (`HideSWInGameGui`, `(4,1)`), `0x62CA1B` (`ShowSWInGameGui`,
`(3,1)`), plus dialog/store/galaxy-map/solo-mode screens.

`CGuiInGame::InitializeMap 0x62B0A0` is a null-guarded tail-call thunk —
`if ([gui+0x90]) jmp CSWGuiMainInterface::InitializeMap 0x687E40` — which is why `0g`'s stack
showed it as a stale frame.

### 1.2 The panel sweep — what actually removes a screen — VERIFIED

`CSWGuiManager::Draw 0x40CC50` draws every panel in `[mgr+0x88]` (count `[mgr+0x8C]`) whose
`[panel+0x44] & 4` is set and which is **not** in the modal stack (`GetPosInModalStack
0x40AB70 == -1`), then draws the modal stack (`[mgr+0x94]`) on top. A panel that is in the
list keeps drawing; there is no "hidden" state other than removal.

`CSWGuiManager::Update 0x40CE70` runs the sweep at its tail (`0x40CF09–0x40CF57`), back to
front over the panel list, on `flags = [panel+0x44]`:

```
if (flags & 0x100)            continue;             ; 0x40CF1E  test ah,1
sel = (flags >> 9) & 3;                             ; 0x40CF23-0x40CF28
if (sel == 2)   { RemovePanel(p); p->vtable[0](1); }; 0x40CF30-0x40CF3E  remove AND DELETE
else if (sel)   { p->[0x44] &= ~0x700; RemovePanel(p); } ; 0x40CF46-0x40CF51  remove, keep
```

So **`0x400` (without `0x200`) = "unregister me and destroy me"**, `0x200` = "unregister me,
keep the object". `Update` is called once per frame from `0x603C9B` on the client's
`gui_manager` (`[clientInternal+0x274]`) — this is the same sweep `NativeBrowserController`
already drives (`NativeBrowserController.cs:357`, `:527`).

### 1.3 What retail runs when you click Load Game — VERIFIED

**`CSWGuiMainMenu::OnLoadSaveGame 0x67B1A0`** (the click handler, which our replay does not
reproduce at all):

```
0x67B1B8  if ((menu->[0x44] & 0x600) == 0x400) return;      ; already closing
0x67B1C8  if (menu->[0x140C] == 0) return;                  ; menu not fully alive
0x67B1DD  panel = operator new(0x1160)
0x67B1FF  CSWGuiSaveLoad::Constructor(panel, menu->[0x18], 0, 1)
0x67B216  CSWGuiManager::AddPanel(mgr, panel, 2, 1)
0x67B22C  menu->[0x44] = (menu->[0x44] & ~0x300) | 0x400     ; ** DELETE THE MAIN MENU **
```

**`CSWGuiSaveLoad::LoadGame 0x6CB0E0`**, main-menu autosave branch (`0x6CB256–0x6CB3E8`, the
branch `MenuLoadController` replays): `ResetSessionStartTime 0x563CF0` ·
`SetGlobalUnpauseableState 0x62B410(inGameGui, 1)` · `PopModalPanel 0x40BE00(mgr)` ·
`HideSWInGameGui 0x62CBA0(inGameGui, 0)` · `SetLoadScreenByModuleName 0x5EDCF0` ·
`SetLoadMusic 0x5EDD30` · **`ShowLoadScreen 0x5EDA80(1,1,0)`** · `SetLoadBarProgress
0x5EDD40(0,1)` · `UpdateScreen 0x401C10` · `DestroyServer`/`UnloadModule` if needed ·
`CreateServer 0x401380(0)` · pause both world timers · `StartConnectToSession 0x5D5040` ·
`RequestStatus 0x6775E0` · `SetInputClass 0x5EDA60(0,1)` · `DisableInput 0x5EDBE0` ·
`LoadPCAutoSave 0x6CA250` · and finally, at `0x6CB3ED`:

```
this->[0x44] = (this->[0x44] & ~0x300) | 0x400      ; ** DELETE THE SAVE PANEL TOO **
```

Net effect by the frame after the click: **both the main menu and the save screen are gone
from the GUI manager**, and the only thing on screen is the modal load screen.

### 1.4 The load screen — VERIFIED

`CClientExoApp::ShowLoadScreen 0x5EDA80` → `CClientExoAppInternal::ShowLoadScreen 0x5F6C60`
pushes the load-screen panel (`[clientInternal+0x278]`) as a **modal** — which is why it
draws over everything, including a main menu that was never removed.

`CClientExoApp::HideLoadScreen 0x5EDA90` → `0x5F6E20`, `ret 8` → **two** arguments,
`HideLoadScreen(int bFadeIn, int bUpdateScreen)`:

```
0x5F6E28  ShowMouse(2)
0x5F6E2D  if (!load_screen[+0x278] || !gui_manager[+0x274]) return;
0x5F6E50  if (bFadeIn) CGuiInGame::StartGlobalFade 0x62ABF0(gui_in_game, 0,0,0, &v)
0x5F6E83  CSWGuiManager::PopModalPanel(mgr)
0x5F6E95  CSWGuiManager::RemovePanel(mgr, load_screen)
0x5F6EEC  ShowMouse(0) ; 0x5F6EF3 0x5F1B90
0x5F6EFD  if (!bFadeIn && bUpdateScreen) UpdateScreen(1/30, 0, 0)
```

It **removes but does not destroy** the load-screen panel, and it **pops one modal** — so it
must be called exactly once per `ShowLoadScreen`, or it starts popping other people's modals.

### 1.5 The whole bring-up lives in `CClientExoAppInternal::MainLoop 0x602EB0` — VERIFIED

`Global::WinMain 0x4041F0`'s frame loop calls `CClientExoApp::MainLoop 0x5ED7D0` at
`0x4046BD` and `CServerExoAppInternal::MainLoop 0x4AE860` at `0x404757` **every frame,
menu or world** — there is only one outer loop. `MainLoop`'s in-world block is
`0x602F0F–0x603254`, gated on three things:

| gate | site | meaning |
| --- | --- | --- |
| `[clientInternal+0x288] != 0` | `0x602EFC` | the "display work pending" flag (`Set/GetAreaNotReadyToDisplay 0x5EDAE0/0x5EDAD0`) |
| `[clientInternal+0x18] != 0` | `0x602F0F` | the **client's** `CSWCModule` |
| `GetCreatureByGameObjectID([clientInternal+0x20]) != 0` | `0x602F18` | the **client's** player creature (identical code to `CClientExoApp::GetPlayerCreature 0x5ED540`) |

Inside, with `dlg = 0x641980(module)` (`[module+0x48] ? [[module+0x48]+0x264] : 0`, the
dialog/cutscene flag):

```
0x602F85  if (dlg)  SetInputClass(1, 1)                       ; cutscene: no HUD
0x602F8D  else    { SetInputClass(0, 1)
0x602FAB            if (gui_in_game) SetSWGuiStatus(1, 1)     ; ** THE HUD **
0x602FB3            CGuiInGame::InitializeMap(gui_in_game) }  ; ** THE MINIMAP **
0x602FBA  CClientExoAppInternal::EnableInput()                ; [+0x2BC] = 0
0x602FF4  if (movie playing || render window active) HideLoadScreen(1, 1)
0x603055-0x603109  pause/resync/unpause all four world timers
0x6031BE  first frame only: CGuiInGame::StartGlobalFade(...)  ; the fade in from black
          (one-shot latch [inGameGui+0xB98], set at 0x6031D2)
0x6031EF  SetGlobalUnpauseableState(gui_in_game, 0)           ; undoes LoadGame's (…, 1)
```

`[clientInternal+0x288]` has exactly four writers (byte-pattern scan of `.text` for
`mov [reg+0x288], …`): the setter thunk `0x5EDAE7`, the client constructor `0x5FC4B5`,
`UnloadModule` at `0x5F8414` (sets 1), and — the interesting one —
**`MainLoop 0x6032DF`, `mov [esi+0x288], 1`**, in its own loading stall, taken when
`[[appManager+0x14]+0] != 0` and `[[appManager+0x14]+4]` is 1 or 3. So on a retail load the
engine arms its own display flag while the module loads.

## 2. What signals the client that the module is ready (Q2)

**The exact function the client runs when a load completes normally is
`CSWCMessage::HandleServerToPlayerModule 0x6684F0`, minor 1 (`Module_Info`)** — jump table at
`0x668604`, branch at `0x668519`. VERIFIED:

```
module = CClientExoApp::GetModule 0x5ED530(client)
if (*(int*)0x8328FC == 0) { CSWMessage::ClearReadMessage 0x4D6170(); return 1; }   ; 0x668528
if (!module) module = CClientExoApp::CreateModule 0x5ED440(client)                ; 0x66854F
if (CSWCModule::LoadModule 0x63F660(module)) {                                    ; 0x66855F
    CClientExoApp::InitializeInGameGUI 0x5ED730(client)                           ; 0x668575
    CSWCMessage::SendPlayerToServerModule_ModuleLoaded 0x677490()                 ; 0x668590
}
```

* `CClientExoApp::InitializeInGameGUI 0x5ED730` → `0x5F1410` = `mov ecx,[ecx+0x40]; jmp
  CGuiInGame::Initialize 0x632860`. `Initialize` early-outs if `[gui+0x108]` is already set,
  otherwise allocates **every** in-game panel (main interface, menu, equip, inventory,
  character, abilities, journal, map, options, dialog, bark bubble, container, store, …),
  driving the load bar via `SetLoadStep 0x5EDFD0` + `SetLoadBarProgress 0x5EDD40` +
  `UpdateScreen`, and finishes with `[gui+0x108] = 1` at `0x63328F`. **This is the only
  writer of the `initialized` flag `SetSWGuiStatus` demands.**
* `0x8328FC` is the gate, and it has exactly two references: the read above, and
  `mov dword [0x8328FC], 1` at `0x64EC8B` inside **`CSWCMessage::HandleServerToPlayerLogin
  0x64EC00`**. So **`Login_Confirm` must be processed before `Module_Info` will be acted
  on**; otherwise the message is dropped and retried.
* The player creature arrives separately:
  `CSWCMessage::HandleServerToPlayerCreatureUpdate_ControlObject 0x64CAF0` →
  `CClientExoApp::SetPlayerCreature 0x5ED780` → `[clientInternal+0x20]`.

Client-direction frames (`CNetLayer::ProcessReceivedFrames 0x5D57D0(0)`) are pumped from
`Global::UpdateScreen 0x401C10` **only when its second argument is 1** (`cmp [esp+0xC],1` at
`0x401CDB`; the pumps are at `0x401D12`/`0x401D2C`) and from the server MainLoop
(`0x4BAC31`, `0x4BAF71`). `MainLoop`'s own two calls (`0x6032A1`, `0x6034F2`) pass direction
1 (server). **NEEDS-LIVE**: whether our from-menu replay ever reaches a pump that services
the client's `Login_Confirm`/`Module_Info`. Every static `UpdateScreen` call site inspected
so far (`Global::UpdateLoadScreen 0x4B30D0`, the `0x4B32xx`/`0x4C44xx` module-load stalls,
`LoadPCAutoSave`'s own `0x6CA4D7`/`0x6CA674`) passes `arg2 = 0`, so the client is serviced by
the server MainLoop pumps, not by the load stalls — which is fine on the retail path and
should be fine on ours, but it is the one link static analysis cannot close.

## 3. What our replay leaves wrong (Q3)

| # | Broken state | Left by | The call that clears it |
| --- | --- | --- | --- |
| 1 | **The main menu panel is still registered and drawing.** Nothing in `MenuLoadController` marks it. | never marked — retail does it in `OnLoadSaveGame 0x67B22C`, not in `LoadGame` | `*(uint*)(menu+0x44) = (*(uint*)(menu+0x44) & ~0x300u) \| 0x400u;` then the next `CSWGuiManager::Update 0x40CE70` deletes it |
| 2 | **The load screen is still up, modal, on top of everything.** | `ShowLoadScreen 0x5EDA80(1,1,0)` in `FirePhase1` | `CClientExoApp::HideLoadScreen 0x5EDA90(client, 1, 1)` — retail leaves it to `MainLoop 0x602FF4` |
| 3 | **Input is disabled** (`[clientInternal+0x2BC] == 1`). | `DisableInput 0x5EDBE0` in `FirePhase2` (retail does this too) | `CClientExoApp::EnableInput 0x5EDBF0` (`[+0x2BC]=0`) — retail leaves it to `MainLoop 0x602FBA` |
| 4 | **No in-game GUI.** `[gui+0x108]` stays 0 unless `Module_Info` was serviced. | client never reached `InitializeInGameGUI` | `CClientExoApp::InitializeInGameGUI 0x5ED730(client)` → `CGuiInGame::Initialize 0x632860` |
| 5 | **HUD status never set.** `[gui+0x34]` never becomes 1. | `MainLoop`'s block never runs | `CGuiInGame::SetSWGuiStatus 0x62AA00(gui, 1, 1)` (+ `InitializeMap 0x62B0A0`) |
| 6 | **`GlobalUnpauseableState` left at 1.** | `SetGlobalUnpauseableState 0x62B410(gui, 1)` in `FirePhase1` | the same call with `0` — retail does it at `0x6031EF` |
| 7 | **`[clientInternal+0x288]` may be 0**, which alone disables every one of 2–6 above. | `MainLoop 0x6032DF` only sets it during its own stall | `CClientExoApp::SetAreaNotReadyToDisplay 0x5EDAE0(client, 1)` |
| 8 | The synthetic `CSWGuiSaveLoad` panel (`0x1160` bytes) is never freed. | deliberately never `AddPanel`ed, so the sweep cannot see it | none needed — a one-off leak per load; do **not** mark it `0x400` (the sweep only walks registered panels, and `RemovePanel` on an unregistered pointer is pointless) |
| 9 | `SetLoadStep 0x5EDFD0` residue | phase 1 / `LoadPCAutoSave` | **nothing** — `LoadPCAutoSave` itself resets all five bytes (`0x6CA9B4–0x6CA9FA`), as does the server's module load (`0x4BA6B9–0x4BA700`). The steps only feed the load bar, which disappears with the load screen. |

Note that 2, 3, 5 and 6 are all *self-healing* the moment 7 + a client module + a client
player creature are true — that is the whole in-world block. The fixes below therefore do
both: reach the state, and (idempotently) perform the work in case a gate is still missing.

## 4. THE RECIPE (Q4)

All addresses `__thiscall` unless noted; `client = *(nint*)(*(nint*)0x007A39FC + 4)`,
`server = *(nint*)(*(nint*)0x007A39FC + 8)`, `gui = GetInGameGui 0x5ED690(client)`.

### Phase 1 — `FirePhase1`, one new line

Immediately **after** `SaveLoadCtor(panel, guiManager, 0, 1)` (retail order: the panel is
constructed, then the menu is marked), and **after** every read of `menu` (there are none
later):

```csharp
// CSWGuiMainMenu::OnLoadSaveGame 0x67B22C-0x67B239 — the step the replay omits.
// The next CSWGuiManager::Update (0x40CE70, driven each frame from 0x603C9B) unregisters
// and deletes the menu. From here on [clientInternal+0x280] is a dangling pointer.
*(uint*)(menu + MenuStateBits) = (*(uint*)(menu + MenuStateBits) & ~0x300u) | 0x400u;
```

`MenuStateBits` is already `0x44` in the file. This is byte-for-byte the idiom
`NativeBrowserController.cs:357` already uses. Optionally copy retail's guards from
`0x67B1B8`/`0x67B1C8` first: skip if `(state & 0x600) == 0x400`, or if `[menu+0x140C] == 0`
(`NativeBrowserController.MenuAlive`).

Do **not** add `AddPanel`/`PopModalPanel` for our synthetic panel — the existing comment is
correct and unaffected by this change.

### Phase 2 — unchanged

`PauseWorldTimer` ×2, `SetInputClass(0,1)`, `DisableInput`, `LoadPCAutoSave`. All of this is
retail (`0x6CB33E–0x6CB3E8`). Phase 3 is what undoes the input lock, exactly as retail's
`MainLoop` does.

### Phase 3 — a second gate, then the presentation

Today phase 3 acks on `Odyssey.HasPlayer`, which is **server-side only**
(`Odyssey.cs:2243` → `CServerExoApp::GetPlayerCreature 0x4B2820`). Add a client-side wait
before the presentation work. New bindings needed:

```csharp
// CClientExoApp:: thunks — all take the CClientExoApp as `this`.
GetClientModule        0x005ED530 (nint) -> nint          // CSWCModule*
GetClientPlayerCreature0x005ED540 (nint) -> nint          // CSWCCreature*
SetAreaNotReadyToDisplay 0x005EDAE0 (nint, int) -> void
InitializeInGameGUI    0x005ED730 (nint) -> void
HideLoadScreen         0x005EDA90 (nint, int bFade, int bUpdateScreen) -> void
EnableInput            0x005EDBF0 (nint) -> void
// CGuiInGame::
SetSWGuiStatus         0x0062AA00 (nint, int status, int bDestroyPause) -> void
InitializeMap          0x0062B0A0 (nint) -> void
// CServerExoApp:: (already bound for the travel fix)
GetServerModule        0x004AE6B0 (nint) -> nint
```

Ordered, once per load, after the existing faction repair and **before** writing
`MenuLoadStatus`/`MenuLoadApplied`:

```
 0. (each frame while pumping, as now) clear DoingStartNewModule; wait for Odyssey.HasPlayer.

 1. WAIT — do not fire immediately:
        clientModule   = GetClientModule(client)
        clientCreature = GetClientPlayerCreature(client)
    if either is 0 -> return and retry next frame. (Log the pair every 3 s; if they never
    arrive, that IS the diagnosis — see §6.)

 2. SetAreaNotReadyToDisplay(client, 1)          // 0x5EDAE0 — byte-for-byte MainLoop 0x6032DF
    This alone re-arms MainLoop's in-world block; from the next frame the engine performs
    3-7 itself, exactly as retail. Everything below is idempotent belt-and-braces.

 3. if (*(int*)(gui + 0x108) == 0) InitializeInGameGUI(client);   // 0x5ED730 -> 0x632860
    (Only meaningful once clientModule != 0; this is what the Module_Info handler calls at
    0x668575, in the same state.)

 4. if (*(int*)(gui + 0x108) != 0) SetSWGuiStatus(gui, 1, 1);     // 0x62AA00, retail args

 5. if (GetServerModule(server) != 0) InitializeMap(gui);         // 0x62B0A0 — SEE §5
    MUST be gated on a live SERVER module: CSWGuiMainInterface::InitializeMap 0x687E40
    dereferences module+0x30 unchecked (travel-map-init-crash.md).

 6. SetInputClass(client, 0, 1);  EnableInput(client);            // 0x5EDA60, 0x5EDBF0

 7. HideLoadScreen(client, 1, 1);                                 // 0x5EDA90 — EXACTLY ONCE
    Same args as MainLoop 0x602FF0. bFade=1 gives retail's fade-in and suppresses the
    forced extra frame.

 8. SetGlobalUnpauseableState(gui, 0);                            // 0x62B410 — undoes phase 1

 9. block.MenuLoadStatus = 0; block.MenuLoadApplied = serial;
```

Steps 2–8 must be latched (a `bool _presented`) so 7 in particular runs once: `HideLoadScreen`
pops one modal panel per call.

**What must wait rather than fire immediately:** step 1's two client objects gate everything;
step 5 additionally needs a live server module; step 3 is a no-op-and-a-half before the
client module exists (`Initialize` would build the whole GUI against a world that is not
there). Steps 2, 4, 6, 7, 8 are safe once step 1 passes.

## 5. Crash-risk register

| Risk | Assessment |
| --- | --- |
| `InitializeMap 0x62B0A0` with a null/destroyed **server** module | **The known fault** — `CSWGuiMainInterface::InitializeMap 0x687E40+0x59` → `CResRef::CopyToString 0x405F70` on `module+0x30`, no null check. Gate on `CServerExoApp::GetModule 0x4AE6B0(server) != 0` (`travel-map-init-crash.md`). The thunk itself only null-checks `[gui+0x90]`. |
| `SetSWGuiStatus` before `CGuiInGame::Initialize` | Safe — hard early-out on `[gui+0x108] == 0` at `0x62AA4A` and on `[gui+0x38] == 0` at `0x62AA55`. Does nothing, does not fault. |
| `InitializeInGameGUI` called twice | Safe — `CGuiInGame::Initialize 0x632860` early-outs on `[gui+0x108] != 0` at `0x632883`. |
| `InitializeInGameGUI` before the client has a module | **NEEDS-LIVE.** `Initialize` allocates ~35 panels and calls `UpdateScreen` five times; retail only ever calls it from `Module_Info` with the client module already built (`0x66855F` succeeded). Keep the gate. |
| `HideLoadScreen` called twice / without `ShowLoadScreen` | Guarded against null `[client+0x278]`/`[client+0x274]` at `0x5F6E2D`/`0x5F6E3B`, but `PopModalPanel 0x40BE00` is unconditional — a second call pops **somebody else's** modal. Latch it. |
| Marking the main menu `0x400` twice | The first sweep frees the object; the second read/write is a use-after-free. Mark once, and never read `menu` again — `[clientInternal+0x280]` is **not** nulled by the destructor (the only writers of that field are the ctor `0x5FC222`, `DisplayMainMenu 0x5FCAF2` and `StopServices 0x604189`). |
| `NativeBrowserController.TryOpen` after the menu is marked | Its guard reads `[menu+0x44]` and `[menu+0x140C]` on a freed object. Retail has the same hazard and lives with it because `DisplayMainMenu 0x5FCA30` early-outs on `PanelExists` (a pointer-compare, never a deref) and rebuilds otherwise. Suppress the browser once `MenuLoadController` has fired, or reuse the existing `Odyssey.HasServer` check. |
| `SetAreaNotReadyToDisplay(client, 1)` while the client has no module | Harmless — gate 2 of the in-world block (`[client+0x18]`) still fails, so nothing runs. `UnloadModule 0x5F8414` sets exactly this value in exactly this situation. |
| `EnableInput` before the world is up | Only writes `[client+0x2BC] = 0`. Worst case the user can click into a world that is still assembling; retail's own `MainLoop` sets it on the very first in-world frame. |
| Marking the menu in phase 1 and then phase 1 failing | The menu is gone and the player is looking at a black screen with no way back. Retail has the same property (`OnLoadSaveGame` deletes the menu before `LoadGame` can refuse). Consider marking the menu at the **end** of `FirePhase1`, immediately before `return 0` — it is not order-sensitive against anything `LoadGame` does. |
| Phase-3 work firing before `LoadPCAutoSave`'s module load finishes | Prevented by the step-1 gate: neither a client module nor a client creature exists until the server has finished and its messages have been serviced. |

## 6. How to verify live

**On screen** (and only a screenshot proves this — see `0h`'s method note):
the main menu must be *gone*, not merely covered; the load screen must fade out rather than
sit black; the HUD (minimap top-left, party portraits, action bar) must be present; the
mouse cursor must respond and WASD/click-to-move must work.

**In memory**, with `client = [[0x7A39FC]+4]`, `ci = [client+4]`, `gui = [ci+0x40]`:

| Probe | Healthy value | Meaning |
| --- | --- | --- |
| `[ci+0x280]` | pointer **not** in `[[ci+0x274]+0x88]`'s panel array | the main menu is unregistered (dangling pointer is expected and normal) |
| `[ci+0x18]` | non-zero | the client has a `CSWCModule` — `Module_Info` was serviced |
| `[ci+0x20]` | not `0x7F000000` | the client has a player creature id |
| `[ci+0x288]` | `1` | the display gate is armed |
| `[ci+0x2BC]` | `0` | input enabled |
| `[ci+0x278]` registered? | **not** in the manager's panel list | the load screen was dismissed |
| `[gui+0x108]` | `1` | `CGuiInGame::Initialize` ran |
| **`[gui+0x34]`** | **`1`** | **the in-game GUI status — the single best "the transition completed" read** |
| `[gui+0x90]` | non-zero *and* registered in the manager | the HUD panel exists and is drawn |
| `[gui+0xB98]` | `1` | the one-shot world-reveal fade fired |
| `*(int*)0x8328FC` | `1` | `Login_Confirm` was processed (the `Module_Info` gate) |
| `[[0x7A39FC]+0x14][0]` / `[+4]` | `0` / `0` | the app-transition stall finished (`StallServerCode 0x602C90`) |

**The decisive diagnostic if it still fails:** log `[ci+0x18]`, `[ci+0x20]`, `*(int*)0x8328FC`
and `[gui+0x108]` every 3 s in phase 3. If `0x8328FC` is 0, the client never processed
`Login_Confirm` and the problem is the netlayer pump, not the GUI. If `0x8328FC` is 1 but
`[ci+0x18]` is 0, `CSWCModule::LoadModule 0x63F660` failed. If both are set but `[gui+0x34]`
stays 0, `[ci+0x288]` is the culprit and step 2 of the recipe is the whole fix.

## Key addresses

| Address | Symbol / meaning |
| --- | --- |
| `0x62AA00` | `CGuiInGame::SetSWGuiStatus(status, bDestroyPause)` — `ret 8`; status field `[gui+0x34]`; **`(1,1)` shows the HUD**, `(2,1)` hides it; guards `[gui+0x108]`, `[gui+0x38]` |
| `0x62AB18` | its 4-entry jump table (`0x62AA1C`/`0x62AA21`/`0x62AA2A`/`0x62AA35`) |
| `0x62B0A0` | `CGuiInGame::InitializeMap` — null-guarded tail call to `CSWGuiMainInterface::InitializeMap 0x687E40` |
| `0x632860` | `CGuiInGame::Initialize` — builds every in-game panel; sets `[gui+0x108]=1` at `0x63328F` |
| `0x5ED730` | `CClientExoApp::InitializeInGameGUI` → `0x5F1410` → `CGuiInGame::Initialize` |
| `0x602EB0` | `CClientExoAppInternal::MainLoop` — in-world block `0x602F0F–0x603254`; gates at `0x602EFC`/`0x602F0F`/`0x602F18` |
| `0x602FAB` / `0x602FB3` / `0x602FBA` / `0x602FF4` | MainLoop's `SetSWGuiStatus(1,1)` / `InitializeMap` / `EnableInput` / `HideLoadScreen(1,1)` |
| `0x6032DF` | MainLoop's own `[clientInternal+0x288] = 1` during the load stall |
| `0x6031BE` / `0x6031D2` / `0x6031EF` | one-shot world-reveal `StartGlobalFade` / its latch `[gui+0xB98]` / `SetGlobalUnpauseableState(0)` |
| `0x67B1A0` | `CSWGuiMainMenu::OnLoadSaveGame` — **`0x67B22C`: marks the main menu `0x400`** (the omitted step) |
| `0x6CB0E0` | `CSWGuiSaveLoad::LoadGame`; main-menu autosave branch `0x6CB256–0x6CB3E8`; **`0x6CB3ED`: marks the save panel `0x400`** |
| `0x40CE70` | `CSWGuiManager::Update` — the sweep at `0x40CF09`; `0x400` = remove+delete, `0x200` = remove, `0x100` = skip. Driven from `0x603C9B` each frame |
| `0x40CC50` | `CSWGuiManager::Draw` — draws panels with `[panel+0x44]&4` not in the modal stack, then the modal stack |
| `0x40BC70` / `0x40BD20` / `0x40BD70` / `0x40C830` / `0x40BE00` | `AddPanel` / `SendPanelToBack` / `PanelExists` / `RemovePanel` / `PopModalPanel` |
| `0x6684F0` | `CSWCMessage::HandleServerToPlayerModule`; **minor 1 = `Module_Info` at `0x668519`** → `CreateModule` → `LoadModule 0x63F660` → `InitializeInGameGUI` → `SendPlayerToServerModule_ModuleLoaded 0x677490` |
| `0x8328FC` | the `Module_Info` gate; set to 1 only at `0x64EC8B` inside `HandleServerToPlayerLogin 0x64EC00` |
| `0x64CAF0` | `HandleServerToPlayerCreatureUpdate_ControlObject` → `SetPlayerCreature 0x5ED780` → `[clientInternal+0x20]` |
| `0x5EDAD0` / `0x5EDAE0` | `Get/SetAreaNotReadyToDisplay` → `[clientInternal+0x288]` (the display gate) |
| `0x5EDA80` / `0x5EDA90` | `ShowLoadScreen` (pushes a modal) / `HideLoadScreen(bFade, bUpdateScreen)` → `0x5F6E20`, `ret 8` |
| `0x5EDBE0` / `0x5EDBF0` | `DisableInput` → `[clientInternal+0x2BC]=1` / `EnableInput` → `=0` |
| `0x5EDA60` | `CClientExoApp::SetInputClass(class, ?)` → `[clientInternal+0x9C]` |
| `0x5EDFD0` | `SetLoadStep(idx, byte)` → `[clientInternal+0x3E4+idx]`; reset by `LoadPCAutoSave 0x6CA9B4…` and server load `0x4BA6B9…` — nothing to clean up |
| `0x5ED530` / `0x5ED540` | `CClientExoApp::GetModule` / `GetPlayerCreature` — the two client-side readiness probes |
| `0x5ED420` / `0x5FCA30` | `DisplayMainMenu` — rebuilds the menu unless one is still registered; called by the browser panel's close path (`native-browser-panel.md`) |
| `0x401C10` | `Global::UpdateScreen(dt, bPumpServer, bClearScreen)` — pumps server MainLoop + client netlayer **only** when `arg2 == 1` (`0x401CDB`) |
| `0x4041F0` | `Global::WinMain` — the single outer loop; client MainLoop `0x4046BD`, server MainLoop `0x404757` |
| `0x602C90` | `CClientExoAppInternal::StallServerCode` — consumes `[appManager+0x14]` transition state; `[+0x14]==1` aborts to the main menu |
| panel `+0x44` | `CSWGuiPanel` state bits: `4` = visible/drawable, `0x100` = sweep-skip, `0x200` = unregister, `0x400` = unregister + delete |

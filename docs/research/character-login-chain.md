# Character login chain — why the in-process load brings up a DEFAULT PC (research 2026-08-01)

Why the plugin-driven main-menu load (`MenuLoadController`, the LoadPCAutoSave replay)
produces a blank level-0 player at the module start point instead of the saved character,
traced end to end through the retail login chain, with the one-call fix. Verified against
`swkotor.exe` (GOG retail 1.0.3; disk == memory, no ASLR, base `0x400000`) by disassembly
(`tools/disasm.py`), caller scans (`tools/xrefs.py`), the sha256-matched symbol DB, and —
decisively — the **actual on-disk state** the last live run left behind (`gameinprogress\`,
`Temp\`, both saves' `SAVEGAME.sav`). Nothing was modified.

Builds on `docs/research/world-up-faction-crash.md` (this doc **closes its NEEDS-LIVE
item 1**: which branch created the factionless creature) and on Handoff.md §0e / "the
engine-side load path".

Tag key: **VERIFIED** = read from the binary or measured on disk here · **INFERRED** =
deduced, not yet run · **NEEDS-LIVE** = must be confirmed on the running game.

## Headline

The replay's login chain runs exactly as designed — and that is the bug. `LoadPCAutoSave`
sets `DoingStartNewModule`, which routes the login through **`EndStartNewModule →
LoginIFOCharacter → LoadCharacterFromIFO(-1)`**, and index `-1` means *read the player from
`TEMP:pifo`* (the file `Temp\pifo.ifo`, restype `0x7DE` = "ifo"). **A manual save contains
no pifo** — measured: both real saves' `SAVEGAME.sav` hold only `AVAILNPC0.utc`,
`<module>.sav`, `INVENTORY.res`, `REPUTE.fac`, and after the bad run `gameinprogress\` has
those same four files and `Temp\` is empty. A manual save keeps its PC **inside the module
`.sav`, in `module.ifo`'s `Mod_PlayerList`** (written by `CSWSModule::SavePlayers
0x4C7870`); the pifo flavour exists only for true autosaves and chargen. So
`LoadCharacterFromIFO(-1)` quietly fails (it even fabricates an *empty* pifo GFF first),
the `0xF` login branch **never checks the return value**, `LoadCharacterFinish` confirms
the login anyway (state 1), and when the client then reports "module loaded",
`InitiateModuleForPlayer 0x4B3A90` finds a player with no game object and **manufactures a
blank `CSWSCreature` at the module start point** — hp 1/1, faction −1, never PostProcessed.
That is the measured default character, and also the true origin of the faction crash.

The menu click works because clicking a normal save never sets `DoingStartNewModule`:
`RunModule` then calls **`LoadPrimaryPlayer 0x4B5F50`** directly, which loads
`Mod_PlayerList[GetPrimaryPlayerIndex()]` from **`module.ifo`** — the saved PC, saved
position, PostProcess, faction, everything.

**The fix is one call.** After `LoadPCAutoSave` returns in `FirePhase2`, call
`CServerExoApp::SetDoingStartNewModule(server, 0)` (`0x4AF090`). That reroutes the login
onto the exact path the working menu click uses. Everything else is already in place.

## 1. The login chain, function by function (Q1)

All VERIFIED by disassembly unless tagged.

### The two flavours

The chain forks on `[serverInternal+0x1007C]` (`DoingStartNewModule`), read by `RunModule`.
Its **only** setter call site in the binary is `LoadPCAutoSave` at `0x6CAA09`
(`SetDoingStartNewModule 0x4AF090`; xrefs: one caller); `StartNewModule 0x4BA920` writes
the field directly (`0x4BAAC9`, and clears at `0x4BAAEA`) but is only reached from
`MainLoop 0x4BABB0` (`0x4BAD93`) for **in-game module transitions** — it is *not* on the
from-menu path. From the menu, `ExecuteLoadModule 0x4BA820` (thunk `0x4AEBF0`) calls
`LoadModule 0x4B95B0` **directly** at `0x4BA912`, so `StorePlayerCharacters` (and its
`[player+0x80] = -1` poison) never runs on our path either.

### `CServerExoAppInternal::RunModule 0x4B6270` — the dispatcher

Thiscall, no args. Runs from MainLoop when the server mode word `[srv+0x10008]` is 1;
sets it to 2 (`0x4B6294`/`0x4B62B0`), sends a server-status broadcast
(`SendServerToPlayerServerStatus_Status 0x568310` at `0x4B62BF`, to the global player id
`[0x74C52C]`), moves every **state ≠ 0** player's creature to the start waypoint
(`0x4B3D10`, reads `[srv+0x1008C]` = `MoveToModuleStartWaypoint`), then:

* `[module+0x1C8] != 0` (module carries a player list — set for save-loaded modules,
  INFERRED name) **and** `[srv+0x1007C] != 1` → **`LoadPrimaryPlayer(firstPlayer)`** at
  `0x4B635A` + `CSWPartyTable::CreateParty 0x565760` at `0x4B6365`. Note: the first player
  is taken from `player_list [srv+0x10064]` **without an empty check** (`0x4B6341`).
* `[srv+0x1007C] == 1` → `SendServerToPlayerModule_EndStartNewModule(1) 0x56CC50` at
  `0x4B6384` (message **50 03 0C** + BOOL, to `[0x74C52C]`), clear the flag (`0x4B6389`),
  **skip LoadPrimaryPlayer**.

### Broadcast flavour (what the replay currently gets)

1. Client `CSWCMessage::HandleServerToPlayerModule_EndStartNewModul 0x652860`: `ReadBOOL`,
   `CClientExoApp::SetInStartNewModule(client, 0) 0x5ED630`, and if the BOOL is 1 —
   **unconditionally** — `SendPlayerToServerLogin_LoginIFOCharacte 0x678030`, a 3-byte
   message **`70 02 0F`** (bytes written at `0x678044`). There is no client-side state
   branch here.
2. Server `CSWSMessage::HandlePlayerToServerLoginMessage 0x5244E0` (thiscall; stack:
   `CSWSPlayer*`, minor byte), minor `0xF` at `0x524526`: requires `[player+0x80] != -1`
   (`0x524551`; ctor default 0, so it passes on our path) → `LoadCharacterStart(0xF,
   player, CResRef(""), 0, 0)` via thunk `0x4AE700`. On `-1` it answers
   `SendServerToPlayerLogin_NeedCharacter 0x56C7F0` (**50 02 10**) — whose **client handler
   is a stub**: `HandleServerToPlayerLogin 0x64EC00` minor `0x10` → `0x5ED310` →
   `0x4015A0` = `ret`. A NeedCharacter reply kills the login silently.
3. `CServerExoAppInternal::LoadCharacterStart 0x4B7470` (thiscall, 0x20 bytes of args:
   byte type, `CSWSPlayer*`, 16-byte `CResRef`, two dwords), type `0xF` branch `0x4B7678`:
   `LoadCharacterFromIFO(-1, 1, 1)` at `0x4B7686` — **the return value is ignored** —
   then `CreateParty` (`0x4B7691`, party table at `srvInt+0x1B770`) and
   `LoadCharacterFinish(player, 0)` (`0x4B769B`). Other types: 2/`0x11` =
   `LoadServerCharacter 0x562380` (NWN server-vault, KOTOR-dormant), `0xE` = FromIFO by
   resref, default (1/`0x13`) = the `"TEMP:temp_char"` vault file (`0x74538C`) — all
   dormant in retail KOTOR.

### Direct flavour (menu click on a normal save; the fix's path)

The GUI sends a load-game request; `HandlePlayerToServerModuleMessage 0x524800` calls
`CServerExoApp::LoadGame 0x4AE6F0` at `0x524ACA` → `CServerExoAppInternal::LoadGame
0x4BA640`: build `"SAVES:" + dir`, `AddResourceDirectory 0x408800`,
`CSWPartyTable::LoadTableInfo 0x565D20`, `CSWGlobalVariableTable::Load 0x52ADE0`,
`RemoveResourceDirectory 0x4088D0`, `[srvInt+0x1B87C] = 1`, `LoadModule` at `0x4BA7C9`.
**`DoingStartNewModule` is never set**, so `RunModule` later takes `LoadPrimaryPlayer`:

* `CServerExoAppInternal::LoadPrimaryPlayer 0x4B5F50(player)`:
  `CSWSModule::GetPrimaryPlayerIndex 0x4C5D90` — walks the module's packed player list
  `[module+0x48]` for an entry with `[e+0x18] == 1` (the is-PC mark), returns its index or
  −1. On −1: return 0 (no login). Else `[player+0x44] = index`,
  `LoadCharacterFinish(player, 1)`.

### `CServerExoAppInternal::LoadCharacterFinish 0x4B5C50(player, bReload)`

* If `[player+0x24] != 1`: set **state 1** (`0x4B5CC8`), `SendServerToPlayerLogin_Confirm
  0x56FDE0` (**50 02 05**; client sets the global `[0x8328FC] = 1`),
  `SendServerToPlayerModule_Info 0x567EF0`.
* If `bReload` and `[player+0x44] != -1` (`0x4B5CEF`): destroy any existing game object
  (`0x4B5D00`) and **`LoadCharacterFromIFO([player+0x44], 1, 1)`** at `0x4B5D12`.
* Queue AI event `0xE` on the module (`GetServerAIMaster 0x4AED80` →
  `CServerAIMaster::AddEventDeltaTime 0x4B08D0`; `OBJECT_INVALID` if there is still no
  creature — the `0x4B5F32` tail), preload the head model (2DA `"NORMALHEAD"`, appearance
  word `[stats+0x10]` i.e. `[creature+0xA60]`).

### `CSWSPlayer::LoadCharacterFromIFO 0x561E30(index, bArg2, bPostProcess)` — the heart

Thiscall on the player, 12 bytes of args.

* `index == -1` (`0x561E8B`): `AddResourceDirectory("TEMP:")` (`0x561EAE` — so TEMP: is
  always registered here, independent of the panel ctor), list index := `[player+0x80]`
  (`0x561EC0`), `GetResObject(CResRef("pifo"), 0x7DE) 0x4074D0`. **If the resource is
  missing** (`0x561EF8`): construct a `CResGFF`, `SetResObject` a brand-new **empty** GFF
  with header `"IFO "` — the doomed fabrication.
* `index != -1` (`0x561F6A`): `GetResObject(CResRef("Module"), 0x7DE)` — **`module.ifo`
  of the currently loaded module**, which for a save-loaded module comes out of the
  save's `<module>.sav`. If missing → return 0.
* Common path: `CRes::Demand 0x409B10`, `GetTopLevelStruct 0x411240`,
  `GetList("Mod_PlayerList") 0x4118C0` — **fails on the empty fabrication → Release +
  return 0** (`0x561FDF`). `GetListElement(list, index) 0x411990` — out of range →
  return 0. Then: `ReadFieldDWORD("ObjectId", default 0x7F000000)`, **`CSWSCreature::
  Constructor 0x4F7A10(objid, 1)`**, `SetGameObject 0x561C40`, `[player+0x38] = creature
  id`, `ReadFieldDWORD("AreaId")`, `SetInParty(1,1) 0x4FDB20`, **`ReadStatsFromGff
  0x5AFCE0`** (hp, `FactionID`, everything; failure destroys the creature and returns 0),
  `LoadJournal 0x4F17D0`, DetectMode/StealthMode/CreatureSize fields, `ReadItemsFromGff
  0x4FFDA0`, equipped-item pass, **X/Y/Z position + orientation floats** → `0x4CD170`,
  then (`0x5622A0`): module-path and AreaId valid → **`[player+0x40] = 1`** ("position
  known"), TEMP-path → 0; `bPostProcess` → **`CSWSCreature::PostProcess 0x4F1C40` at
  `0x5622CC`** (→ player-faction `AddMember` — the faction doc's chain); finally
  module-path writes **`creature+0x310 = AreaId`, `+0x314/0x318/0x31C = saved position`**
  (`0x562335–0x562359`), TEMP-path does `RemoveResourceDirectory("TEMP:")`. Returns 1.

### `CServerExoAppInternal::InitiateModuleForPlayer 0x4B3A90` — the default-PC factory

Thunk `CServerExoApp::InitiateModuleForPlayer 0x4AE930`; its **only** caller is
`HandlePlayerToServerModuleMessage 0x524800` minor 2 (`0x52489A`) — the client's
**`SendPlayerToServerModule_ModuleLoaded 0x677490` (70 03 02)** ack — gated on
`[player+0x24] == 1 && GetServerMode() == 2`, which also sets `[player+0x7C] = 1` and then
sends `PlayerList_Add 0x568030` / `PlayerList_All 0x56FE40`.

* `GetGameObject() == null` (`0x4B3AB9`) → **`new CSWSCreature(0x7F000000, 1)`**
  (`0x4B3ABD–0x4B3AE3`) + `SetGameObject` (`0x4B3B05`). A creature built from nothing:
  constructor-default stats (hp 1/1, `faction_id = -1` per `0x5ACEDA`), **PostProcess is
  never called on it**.
* `[player+0x40] == 0` (`0x4B3B1C`) → bind the creature to the module's entry area
  (`creature+0x310 = [area+0x120]`) and place it at the **module start point** (module
  `ModEntryArea` at `[module+0x58]`, fields `+0x10/+0x14/+0x18`). With `[player+0x40] ==
  1` (set by a successful FromIFO) the saved position is left alone.

### `CSWSMessage::HandlePlayerToServerCharacterDownload 0x525350` (Q1's "and neighbours")

Minor must be 1; replies `CharacterDownloadReply 0x56B090` if `[player+0x64] == 1`, else
`CharacterDownloadFail 0x56B140`. It **creates nothing** — NWN server-vault machinery,
dormant in retail KOTOR. Not on any KOTOR login path.

## 2. Where TEMP:pifo comes from — and where it does NOT (Q2)

* `"TEMP:"` is a resource-directory alias for `<game>\Temp` (created by
  `CExoResMan::CreateDirectory2 0x4068C0` at `0x6CA521`; registered by
  `AddResourceDirectory 0x408800` — by the save-panel ctor and, decisively, inside
  `LoadCharacterFromIFO` itself at `0x561EAE`). `"pifo"` is a resref; restype `0x7DE` =
  2014 = **"ifo"** (kotorfiles restype table), so `TEMP:pifo` is the file
  **`Temp\pifo.ifo`** — a standalone one-player module-IFO carrying `Mod_PlayerList`.
  VERIFIED.
* Writers of `Temp\pifo.ifo`, exhaustive by string xref (`0x745318 "pifo"`, `0x74554C
  "TEMP:pifo"`): ~~**(a)** chargen — `CSWCCreature::SaveCharGenCreature 0x6123E0` (new
  game)~~ **⚠ (a) IS WRONG — RETRACTED 2026-08-29, see the second addendum at the foot of this
  file.** `SaveCharGenCreature` does not reference either literal; the five occurrences of
  `0x745318` are `0x4B2D61`, `0x4B908D`, `0x561EC7`, `0x561F1F`, `0x6CA558`, all inside (b),
  (c), `DoPCAutosave` and `LoadCharacterFromIFO`. **THE REAL LIST IS (b) AND (c) PLUS
  `DoPCAutosave 0x4B8300`**; **(b)** `CServerExoAppInternal::StorePlayerCharacters 0x4B2BA0` (in-game module
  transitions via `StartNewModule`, snapshots each **live** creature; writes nothing when
  no player has a creature — return is `stored_count > 0`, `0x4B2E36`); **(c)**
  `LoadPCAutoSave 0x6CA250`, which after exporting the save's `SAVEGAME.sav` into
  `GAMEINPROGRESS:` (`CERFFile::ExportFilesFromERF 0x5DD710` at `0x6CA50C`) copies
  `<export>\pifo` → `TEMP:pifo` (`CExoResMan::FileCopy 0x406990` at `0x6CA634`; argument
  order src→dst confirmed against the save-side call in `DoPCAutosave` at `0x4B90FC`,
  which copies `TEMP:pifo` → `<savedir>\pifo`). VERIFIED.
* **A manual save has no pifo to copy.** Measured on disk (the strongest evidence in this
  doc): `saves\000001 - AUTOSAVE\SAVEGAME.sav` (the agent-staged copy) and
  `saves\000002 - Game1\SAVEGAME.sav` (a genuine hand-made save) both contain exactly four
  resources — `AVAILNPC0.utc`, `END_M01AA` (restype 2057, the module `.sav`),
  `INVENTORY.res`, `REPUTE.fac` — **no `pifo`**; after the bad run `gameinprogress\`
  contains those same four files and `Temp\` is **empty**. The manual-save PC lives inside
  `END_M01AA.sav` → `module.ifo` → `Mod_PlayerList` (packed by `CSWSModule::SavePlayers
  0x4C7870`, which is also where `[e+0x18]`-marked PC entries for `GetPrimaryPlayerIndex`
  come from). VERIFIED (files) / INFERRED (SavePlayers is the packer — consistent with its
  `LoadCharacterFromIFO` xref at `0x4C7C9B` and the save's contents).
* True autosaves are the pifo flavour: `DoPCAutosave 0x4B8300` writes `TEMP:pifo` (via the
  live-creature snapshot) and packs it into the autosave's `SAVEGAME.sav`. `LoadPCAutoSave`
  is its mirror — the pifo expectation is an **autosave-format assumption**, violated by
  staging a manual save over the autosave slot. VERIFIED for the load side; save side
  INFERRED from `0x4B908C–0x4B90FC`.

## 3. What triggers the login (Q3)

Two triggers, one per flavour — both run on the game thread with nothing for a plugin to
pump:

* **Flag set** (`DoingStartNewModule == 1`): the trigger is a *message round-trip* rooted
  in server-side `RunModule` — broadcast `50 03 0C` → client answers `70 02 0F` → server
  login handler. Our replay's loopback delivers all of it (measured: state 1 was reached),
  so the missing-RequestStatus theory stays dead.
* **Flag clear** (every normal-save load): the trigger is a *direct call* — `RunModule` →
  `LoadPrimaryPlayer` — no client message involved. The client stays passive because
  `LoadPCAutoSave` set `CClientExoApp::SetLoadFromIFO(client, 1) 0x5EDD50`
  (`[clientInternal+0x310] = 1` at `0x6CA652`), whose only reader,
  `HandleServerToPlayerServerStatus 0x64F280` (`0x64F30E`), clears it and *refrains* from
  sending the (dormant) local-character login. VERIFIED.

A plugin therefore does not invoke the login directly at all — it **chooses the flavour**
by setting `[serverInternal+0x1007C]` before `RunModule` reads it, and the engine does the
rest in its own order.

## 4. Why the replay produced a default character — the full mechanism (Q4)

All links VERIFIED; the composite is INFERRED-strong (every measured symptom accounted
for):

1. `LoadPCAutoSave` exports the staged **manual** save → no pifo appears in
   `GAMEINPROGRESS:` → the `FileCopy` to `TEMP:pifo` has no source → `Temp\pifo.ifo`
   never exists (measured: `Temp\` empty after the run).
2. Module loads; `RunModule` sees `DoingStartNewModule == 1` → broadcasts
   `EndStartNewModule(1)`; client answers `LoginIFOCharacter`.
3. `LoadCharacterStart 0xF` → `LoadCharacterFromIFO(-1, 1, 1)` → fabricates an **empty**
   pifo GFF, `GetList("Mod_PlayerList")` fails, **returns 0 — and the 0xF branch ignores
   it**.
4. `LoadCharacterFinish(player, 0)`: no reload possible (`[player+0x44]` is the ctor's
   −1), but it still sets **state 1**, sends Login_Confirm and Module_Info, and queues AI
   event `0xE` with `OBJECT_INVALID`.
5. Client finishes loading and sends `ModuleLoaded (70 03 02)` →
   `InitiateModuleForPlayer`: no game object → **blank `CSWSCreature`**, `[player+0x40] ==
   0` → placed at the **module start point**. Constructor-default stats: **hp 1/1, faction
   −1, never PostProcessed** — precisely the measured PC (`(15.42, 20.12, -1.27)`, 1/1)
   and precisely the factionless creature of the crash doc (its NEEDS-LIVE item 1 is now
   answered: the creature was built by `InitiateModuleForPlayer`, a path with **no
   PostProcess call at all** — not a PostProcess that skipped its AddMember).

The menu click on the ORIGINAL slot took the direct flavour and read the PC from
`module.ifo` — right character, right position, right faction. Corollary (NEEDS-LIVE, low
value): clicking the **autosave** slot in the GUI with a staged *manual* save should
reproduce the same default-PC bug, since it is the same `LoadPCAutoSave` path.

## 5. THE RECIPE (Q5)

One new call, in `FirePhase2`, immediately after `LoadPCAutoSave` returns. No new
resources, no hand-built state — it flips the replay onto the retail normal-save path.

```
// existing:
LoadPCAutoSave(panel, _entryNative);                     // 0x6CA250 — unchanged

// NEW — reroute the login to the LoadPrimaryPlayer flavour:
nint appManager = *(nint*)0x7A39FC;
nint server     = *(nint*)(appManager + 0x8);            // CServerExoApp
// (optional but recommended gate — see risk register:)
nint srvInt     = *(nint*)(server + 0x4);
int  players    = *(int*)(srvInt + 0x10064 + 8);         // the count FirePhase already logs
if (players >= 1)
    SetDoingStartNewModule(server, 0);                   // 0x4AF090, thiscall(server, int)
```

* `SetDoingStartNewModule 0x4AF090`: thiscall on `CServerExoApp` (`[appMgr+8]`), one int
  arg; writes `[serverInternal+0x1007C]`. Binding is identical in shape to the plugin's
  existing thiscall bindings. VERIFIED.
* **Timing**: `LoadPCAutoSave` sets the flag at `0x6CAA09` and enters
  `ExecuteLoadModule`→`LoadModule` before returning; `RunModule` only reads the flag at
  the mode 1→2 transition, which MainLoop reaches frames later (the load takes seconds,
  measured). Clearing right after the call returns is strictly before the read. VERIFIED
  (order of writes/reads) / NEEDS-LIVE (that nothing else re-sets it — nothing does
  statically; the setter has exactly one caller).
* What then happens, in the engine's own order (§1): `RunModule` → `LoadPrimaryPlayer
  (firstPlayer)` → `GetPrimaryPlayerIndex` on the save's module (its `module.ifo` carries
  the PC entry) → `LoadCharacterFinish(player, 1)` → `LoadCharacterFromIFO(idx, 1, 1)`
  reads `module.ifo:Mod_PlayerList[idx]` → real creature, `ReadStatsFromGff` (hp 12/0,
  `FactionID`), items, journal, **saved position** into `creature+0x314`, `[player+0x40] =
  1`, **PostProcess → player-faction AddMember** → Login_Confirm/state 1 → `CreateParty`
  → client `ModuleLoaded` → `InitiateModuleForPlayer` sees a creature and `[player+0x40]
  == 1` → no relocation → area ack → state 2 → updates.
* Everything else in `MenuLoadController` stays as-is: the entry (flags `0x30`, module
  name, directory name) is read by nothing new — `LoadPCAutoSave` touches its argument
  only to pass it to `ExecuteLoadModule` (`[ebp+8]` read solely at `0x6CAA14`, VERIFIED).
  `EnsurePlayerFaction` stays as a canary; after this fix its repair branch should stop
  firing.
* Agent-side preflight (optional, zero-risk): before staging, open the save's
  `SAVEGAME.sav` → `<module>.sav` → `module.ifo` with the C# GFF reader and check
  `Mod_PlayerList` is non-empty; refuse the in-process route if not.

### Rejected alternatives

* **Synthesise `Temp\pifo.ifo` ourselves** (extract the `Mod_PlayerList` entry into a
  standalone IFO): duplicates data the engine already reads natively, hand-built GFF
  against the engine-write rules, and the broadcast flavour then still depends on message
  timing. Strictly worse than one flag write.
* **Repair at world-up** (call `LoadPrimaryPlayer(player)` after the blank PC exists — its
  reload branch would even destroy the blank creature at `0x4B5D00` and load the real
  one): touches a creature already bound into the area, party table and AI event queue;
  NEEDS-LIVE and risky. Keep only as a last-resort fallback.
* **Replay the normal-save GUI path instead of LoadPCAutoSave**: viable (`LoadGame
  0x4BA640` + client-side export), but a much larger rewrite of a controller that already
  works for the module part.

## 6. Crash-risk register and live verification (Q6)

| Risk | Assessment |
| --- | --- |
| `RunModule`'s LoadPrimaryPlayer block takes the **first player without an empty-list check** (`0x4B6341`); `LoadPrimaryPlayer` then writes `[player+0x44]` | With no player the walk yields garbage/null → possible fault. Mitigation: only clear the flag when `players >= 1` (the count the controller already logs). With the 1 s connect grace this is always true; if 0, leave the flag set — degraded old behaviour (default PC) instead of a crash. VERIFIED (no check) / policy INFERRED. |
| `[module+0x1C8] == 0` would skip LoadPrimaryPlayer → no login at all → world up with no PC, controller waits forever on `HasPlayer` | Non-crash stall; the same save loads correctly by menu click, which requires this gate to pass, so it passes for save-loaded modules. INFERRED / NEEDS-LIVE (log it once). |
| `GetPrimaryPlayerIndex == -1` (module.ifo without a PC entry) | LoadPrimaryPlayer returns 0 → same non-crash stall. Preflight the save's `Mod_PlayerList` agent-side. VERIFIED (return path). |
| Clearing the flag suppresses the `EndStartNewModule` broadcast — client never runs `SetInStartNewModule(client, 0)` | The client-side flag was never set to 1 on our path (chargen sets it); the retail menu-click flow also never receives the broadcast and is healthy. VERIFIED for our path's writers. |
| `SetLoadFromIFO(client, 1)` left at 1 | Its only reader clears it on the first server-status message and merely *suppresses* the dormant local-character login. Harmless. VERIFIED. |
| Double character creation (LoadCharacterFromIFO success then InitiateModuleForPlayer) | InitiateModuleForPlayer takes the existing-object branch (`0x4B3B0C`) — no second creature; `[player+0x40] == 1` skips the start-point move. VERIFIED. |
| Something re-sets `[srv+0x1007C]` between our clear and RunModule | `0x4AF090` has exactly one caller (LoadPCAutoSave, already returned); `StartNewModule` is not reachable without a running module. VERIFIED statically. |

**How to verify live** (phase-3 world-up probe, one log line):

* `[serverInternal+0x1007C]` — 0 after the clear, and still 0 when mode `[+0x10008]`
  reaches 2.
* The creature from `GetPlayerCreature 0x4B2820`: hp `creature+0xDC` (expect **12**, not
  1), max hp neighbour field, position `creature+0x314/0x318/0x31C` ≈ **(20.01, 22.15,
  −1.27)** (not the start point 15.42/20.12), area id `creature+0x310` valid,
  `[[creature+0xA74]+0x78]` (faction id) ≥ 0 **without** `EnsurePlayerFaction` having to
  repair (its log line should report healthy), first name via stats for the saved name.
* `[player+0x40] == 1`, `[player+0x24]` reaching 1 then 2, `[player+0x44] ==
  GetPrimaryPlayerIndex` result (log it).
* On failure: log `GetPrimaryPlayerIndex` and `[module+0x1C8]` — those two pin which
  residual gate (if any) is wrong.

## Key addresses

| Address | Symbol / meaning |
| --- | --- |
| `0x4AF090` | `CServerExoApp::SetDoingStartNewModule(int)` — **the fix call**, arg 0, `this = [[0x7A39FC]+8]`; writes `[srvInt+0x1007C]` |
| `0x1007C` | `CServerExoAppInternal` DoingStartNewModule — selects broadcast (1) vs LoadPrimaryPlayer (0) flavour in RunModule |
| `0x4B6270` | `RunModule` — flag read `0x4B633F`/`0x4B636A`; LoadPrimaryPlayer `0x4B635A`; broadcast `0x4B6384`; **no empty-player-list check** `0x4B6341` |
| `0x4B5F50` | `LoadPrimaryPlayer(player)` — `GetPrimaryPlayerIndex` → `[player+0x44]` → `LoadCharacterFinish(player, 1)` |
| `0x4C5D90` | `CSWSModule::GetPrimaryPlayerIndex` — walks `[module+0x48]`, PC mark `[e+0x18]==1` |
| `0x4B5C50` | `LoadCharacterFinish(player, bReload)` — state 1 `0x4B5CC8`, Confirm `50 02 05`, reload branch `0x4B5CEF`→FromIFO(`[player+0x44]`) |
| `0x561E30` | `CSWSPlayer::LoadCharacterFromIFO(index, bArg2, bPostProcess)` — `-1` = `TEMP:pifo` (`Temp\pifo.ifo`), else `module.ifo`; empty-pifo fabrication `0x561EF8`; PostProcess `0x5622CC`; saved pos `0x562335`; `[player+0x40]` write `0x5622A0` |
| `0x4B7470` | `LoadCharacterStart` — type `0xF` branch `0x4B7678` **ignores FromIFO's return** |
| `0x5244E0` | `HandlePlayerToServerLoginMessage` — minor `0xF` gate `[player+0x80]!=-1` at `0x524551`; else NeedCharacter `50 02 10` (client stub `0x4015A0`) |
| `0x652860` / `0x678030` | client EndStartNewModule handler / `LoginIFOCharacte` reply `70 02 0F` |
| `0x56CC50` | `SendServerToPlayerModule_EndStartNewModule` — `50 03 0C` to `[0x74C52C]` |
| `0x4B3A90` | `InitiateModuleForPlayer` — **the default-PC factory**: blank `CSWSCreature` `0x4B3ABD`, start-point placement when `[player+0x40]==0`; sole caller `0x52489A` (module msg minor 2, state==1 && mode==2) |

---

## Addendum (2026-08-29): writer (a) turned out to be a feature, not a footnote

§2's writer list has three entries, and (a) — chargen, `CSWCCreature::SaveCharGenCreature
0x6123E0` — was recorded only to make the list exhaustive. It is now load-bearing.

`coop.ini join_mode = newcharacter` (see
[world-adoption.md](world-adoption.md)'s 2026-08-29 section) lets a joiner who has never played
this game create a character through the retail New Game screens and be dropped into the
HOST's world with it. The whole mechanism is: notice the file writer (a) produces, copy it
aside before the adoption load's own `FileCopy` truncates it, and hand it to
`WorldAdoption` as the carried character. No new engine call, no chargen code of our own.

Two facts from this doc are what make that work, and both are worth restating here because
they are what a reader of §2 would want to know next:

* **The pifo shape is not chargen-specific.** Writer (a)'s output and `PlayerIfo.BuildPifo`'s
  emit the same thing — an `IFO ` GFF whose root holds a one-element `Mod_PlayerList` — so
  `LoadCharacterFromIFO(-1)`'s `[player+0x80] == 0` index is correct for either, by
  construction.
* **The flag flavour is already right.** A chargen game runs with `DoingStartNewModule` SET;
  that is the flavour §3 describes as reading `TEMP:pifo`, and it is exactly the flavour world
  adoption asks for (`block+0x3320`). The `SetDoingStartNewModule(server, 0)` reroute in
  `MenuLoadController` — §5's RECIPE — is the thing an adopted load deliberately SKIPS.

Newly disassembled this pass, filling in the one gap §2 left (where writer (a) is called from):

| Address | Symbol / meaning |
| --- | --- |
| `0x6DBDF0` | `CSWGuiClassSelection::BeginPlayGame` — the entire retail "start a new game" step. `CreateServer 0x401380` at `0x6DBE93`; **`SaveCharGenCreature` at `0x6DBE9B`** (the pifo write, `this = [classSel+0x68]`, a `CSWCCreature`); `StartConnectToSession 0x5D5040` at `0x6DBEDE`; `RequestStatus 0x6775E0` at `0x6DBF07`; the start module's name built from `[classSel+0x1558]`/`+0x155C` from `0x6DBF0C`. |
| `0x6EB320` | `CSWGuiMainCharGen::BeginPlayGame` — `RemoveSubPanels 0x6EA830`, then `mov ecx,[this+0x68]` and a tail `jmp 0x6DBDF0`. No stack arguments; `[mainCharGen+0x68]` is the `CSWGuiClassSelection`. |
| `0x6123E0` | `SaveCharGenCreature` has **exactly one** call site in the image (`tools/xrefs.py`: one direct call at `0x6DBE9B`, zero address-literal occurrences), so §2's "chargen" attribution is not just a string xref — nothing else can reach it. |

⚠ Note for anyone reading §2's writer (b): `StorePlayerCharacters 0x4B2BA0` writes nothing when
no player has a creature (`stored_count > 0` at `0x4B2E36`), which is why the chargen pifo
SURVIVES the chargen→module transition that immediately follows it. That is the window the
capture poll lives in.
| `0x677490` | client `SendPlayerToServerModule_ModuleLoaded` `70 03 02` — the trigger of InitiateModuleForPlayer |
| `0x6CA250` | `LoadPCAutoSave` — GAMEINPROGRESS export `0x6CA50C`, pifo FileCopy `0x6CA634`, `SetLoadFromIFO(1)` `0x6CA652`, flag set `0x6CAA09`, `ExecuteLoadModule(entry)` `0x6CAA1B` (entry read only there) |
| `0x406990` | `CExoResMan::FileCopy(src, dst, restype, 0xFFFF)` — direction proven by `DoPCAutosave 0x4B90FC` |
| `0x4BA640` | `CServerExoAppInternal::LoadGame` — the retail normal-save server path (never sets the flag) |
| `0x4B2BA0` | `StorePlayerCharacters` — transition-only TEMP:pifo writer; sets `[player+0x80]=-1` for creatureless players; **not on the from-menu path** (sole caller `StartNewModule 0x4BAAD6`) |
| `0x7DE` | restype 2014 = "ifo" — `TEMP:pifo` ⇒ `Temp\pifo.ifo`; `CResRef("Module")+0x7DE` ⇒ `module.ifo` |
| `0x525350` | `HandlePlayerToServerCharacterDownload` — vault reply machinery, creates nothing, dormant |

---

## Second addendum (2026-08-29): writer (a) is RETRACTED — chargen writes a BIC

The addendum above built `join_mode = newcharacter` on §2's writer (a), "chargen —
`CSWCCreature::SaveCharGenCreature 0x6123E0`". **That attribution is wrong**, it was caught by
the live round that tried to use it, and the correction is recorded here because §2 is the page
anyone building on `TEMP:pifo` reads first.

What the live run measured: the plugin hooked the sole call site (`0x006DBE9B`) and looked at
`Temp\pifo.ifo` the instant `SaveCharGenCreature` returned. The file was **0 bytes**, still 0
after a 400 ms spin on the game thread, and `Temp\` was empty by the time the Endar Spire was
up. That was diagnosed as a flush race. It was not a race — there was nothing being written.

What the disassembly says:

* **`SaveCharGenCreature` references neither literal.** `tools/xrefs.py 0x745318` (`"pifo"`)
  gives five address-literal sites — `0x4B2D61`, `0x4B908D`, `0x561EC7`, `0x561F1F`,
  `0x6CA558` — and `0x74554C` (`"TEMP:pifo"`) two more, `0x4B90CA` and `0x6CA605`. None is
  inside `0x6123E0..0x6134B9`. The function was attributed to the FEATURE ("this is the new-game
  path, so this must be the new-game writer") rather than to a hit on the string, and a
  string-xref list cannot catch that: the second step is not the mechanical one.
* **Its own tail names a different file and a different restype.** The GFF it builds is headed
  `"BIC "` / `"V2.0"` (`0x745384`/`0x745338`, at `0x61245D`/`0x612445`) and it ends at
  `0x00613434` with `CResGFF::WriteGFFFile("TEMP:" + "temp", 0x7DF)` — `0x745310` + `0x74F4F4`,
  restype 2015 = **"bic"**. It writes `Temp\temp.bic`, a character file, not a module IFO.

So the corrected §2 list has **three** writers, all of them server-side and none on the
from-menu new-game path: `StorePlayerCharacters 0x4B2BA0` (module transitions, via
`StartNewModule`), `DoPCAutosave 0x4B8300` (the autosave, which then copies `TEMP:pifo` into the
save directory at `0x4B90FC`), and `LoadPCAutoSave 0x6CA250` (the load side's copy, and §7's
truncation trap). A new game reaching its first module from the menu goes through
`ExecuteLoadModule → LoadModule` (§1), which is exactly why none of them runs and why `Temp\`
was empty.

`join_mode = newcharacter` therefore takes the new character out of a real SAVE — see
[world-adoption.md](world-adoption.md)'s "The capture point, answered" section. The pifo shape
statement in the first addendum stands unchanged; only its provenance was wrong.

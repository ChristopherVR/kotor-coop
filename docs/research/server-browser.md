# Main-menu server browser — a scrollable co-op server list (research 2026-07-31)

How to render, on KOTOR's main menu, a scrollable **list of discovered co-op servers** (name /
players / address), let the player pick one, and hand the chosen address back to the agent —
plus manual-IP and password entry. Verified against `swkotor.exe` (GOG retail 1.0.3; disk ==
memory, no ASLR, base `0x400000`) via the sha256-matched symbol DB. Nothing was modified.

Read alongside `connect-flow.md` (BTN_WARP rebind, `CSWGuiSaveNamePanel` editbox, `PushModalPanel`)
and `console-overlay.md` (the already-built `~` console text channel). This doc reuses both.

Tag key: **VERIFIED** = read from the binary here · **INFERRED** = deduced, not yet run ·
**NEEDS-LIVE** = must be confirmed on the running game before shipping.

## Headline

KOTOR has exactly the widget we want, still linked and used every time you open Load Game:
**`CSWGuiListBox`** — a scrollable control list with a scrollbar, per-page paging, keyboard
navigation, and a single selection index. The save/load screen (`CSWGuiSaveLoad`) embeds one
and fills it with clickable rows carrying arbitrary text. So the list, the scrollbar, the
click-to-select and the "read which row is selected" all exist as proven engine code.

The catch is **populating** it. KOTOR builds its rows by cloning a prototype control defined in
a `.gui` asset and setting each clone's text — a path that is safe when the *engine* drives it
(loading a `.gui`, cloning its proto item) and crash-prone when *we* hand-build controls and
call the add-item routine with a guessed ABI. The honest recommendation (§6) is therefore
layered: ship the list first through the already-working console text channel, and treat the
native `CSWGuiListBox` as a richer tier that needs one live verification pass.

## 1. The list widget: `CSWGuiListBox` — VERIFIED

`CSWGuiListBox` size `0x2E0`, vtable **`0x0073E840`**. It is a `CSWGuiControl`-derived
container. Layout (from the DB, cross-checked against the routines below):

| Offset | Field | Meaning |
| --- | --- | --- |
| `+0x06C` | `clicked_index` | int, scrollbar/hit result from last click |
| `+0x104` | `scrollbar` | `CSWGuiScrollBar` (size 0x188) — the engine's own scrollbar |
| `+0x28C..0x298` | `viewport_{x,y,width,height}` | int, the scrolling region |
| **`+0x29C`** | **`controls`** | `CExoArrayList<CSWGuiControl*>` — the rows. `{data@0, count@4, cap@8}` |
| **`+0x2A0`** | *(count of `controls`)* | int — number of rows |
| `+0x2A8` | `control_extents` | `CExoArrayList<CSWGuiExtent>` — per-row rectangles |
| `+0x2C2` | `scroll_position` | short |
| `+0x2C4` | `items_per_page` | short |
| **`+0x2C6`** | **`selection_index`** | short. **`0xFFFF` = nothing selected**; else index into `controls` |
| `+0x2C8/0x2CA` | `top_visible_index` / `bottom_visible_index` | short |
| **`+0x2CC`** | **`proto_item`** | `CSWGuiControl*` — the row prototype cloned per entry |

Rows are just `CSWGuiControl*` in the `controls` array; a "row" is typically a `CSWGuiButton`.

Selected members of the class (all `__thiscall`):

| Address | Routine | Notes |
| --- | --- | --- |
| `0x00419A90` | `ClearItems` | empties `controls`, resets count `+0x2A0=0`, `selection_index=0xFFFF`. **VERIFIED** |
| `0x0041C1D0` | `AddControls` | **replaces** all rows from a caller `CExoArrayList<CSWGuiControl*>`; calls `ClearItems` then appends + re-organizes. **ret `0x10` (4 args)**: `(arrayListPtr, flagA, flagB, flagC)`. **VERIFIED** |
| `0x00419B80` | `Initialize` | positions one row from the proto extent |
| `0x0041D5B0` | `Load` | builds the listbox from a `.gui` node |
| `0x0041D3E0` | `LoadProtoItem` | reads the proto row from the `.gui` |
| `0x0041B140` | `OrganizeControls` | recomputes row rectangles / paging |
| `0x0041A5A0` | `GetControl(i)` | bounds-checks `i` vs `+0x2A0`, returns `controls[i]`. **ret 4. VERIFIED** |
| `0x0041A5D0` | `GetSelectedControl` | returns `GetControl( *(short*)(this+0x2C6) )`. **VERIFIED** |
| `0x0041C040` | `SetSelectedControl(i,…)` | sets `+0x2C6`, toggles highlight vtbl `+0x3C` |
| `0x0041C4A0` | `HandleLMouseDown` | hit-tests the row under the cursor via `HitCheckMouseLocal 0x0041A5E0` and updates selection |

**Reading the selected row is a pure field read** — the single most important fact for
select→connect: `short idx = *(short*)(listbox + 0x2C6); if (idx == (short)0xFFFF) none;` and the
row control is `((void**)*(void**)(listbox + 0x29C))[idx]` (equivalently `GetControl(idx)`).

### Setting a row's text — VERIFIED path

Each row button carries its caption in a `CSWGuiText` sub-object:

- `CSWGuiButton.text` (`CSWGuiText`, size 0x70) at **button `+0x154`**.
- `CSWGuiText.text_params` (`CSWGuiTextParams`) at **`+0x18`**; the caption `CExoString` is
  `text_params +0x00`.
- **`CSWGuiTextParams::SetText 0x00415E00`** — `__thiscall void(this=textParams, CExoString* s)`,
  **ret 4**. Copies `s` into the params, sets `str_ref=-1` (so an inline literal wins over any
  TLK strref), and if the text object + its live `CAurGUIStringInternal` exist, rebuilds the
  on-screen string immediately. So the button caption at `button+0x154+0x18` is settable to any
  runtime string via `SetText(button+0x16C, &myCExoString)`. **VERIFIED routine; call ABI INFERRED**
  (simple, but not yet run here).

Conclusion for the task's question — *"can its rows be repopulated with arbitrary strings?"*
**Yes**: a `CSWGuiListBox` row is a button whose caption is a plain `CExoString` set through a
verified `SetText`. The list is not restricted to save names. What is *not* free is manufacturing
the rows themselves (below).

## 2. The closest analog: `CSWGuiSaveLoad` — VERIFIED

`CSWGuiSaveLoad` size `0x1160`, vtable `0x00757650`. It **embeds a `CSWGuiListBox` at
`+0x934`** (`games_listbox`) and three buttons: `saveload_button +0xC14`, `back_button +0xDD8`,
`delete_button +0xF9C`.

`CSWGuiSaveLoad::PopulateGameList 0x006CC160` is the reference "fill the list" routine. Traced:

1. Enumerates the `saves` directory (`CExoBase::GetDirectoryList 0x005E6640`) → count.
2. Per entry: `operator_new(0x248) 0x006FA7E6` → `CSWGuiSaveLoadEntry::Constructor 0x006CB940`
   (`CSWGuiSaveLoadEntry` size `0x248`, vtable `0x00757740` — a button at `+0x00` plus save
   metadata: `savegamename +0x1D8`, `areaname +0x1E8`, `lastmodule +0x1F0`, `screenshot +0x208`).
3. Clones the row from the listbox proto (`call [entry_vtbl+0x98]` passing
   `games_listbox.proto_item` = `this+0xC00`), loads its data (`LoadData 0x006C8E50`), and sets
   the visible caption (**`CSWGuiSaveLoadEntry::SetXboxTitle 0x006C9780`** — builds a date/time +
   name string into the row).
4. **Binds each row button's own events** with `CSWGuiControl::AddEvent 0x0041AB20` (event `0x27`
   click → `VerifyLoadGame 0x006CC0E0` or `HandleSaveButton 0x006CBB60`; `0x29` →
   `DeleteGame 0x006CAA90`; `0x00` → `ShowGame 0x006C89D0`). So in stock KOTOR the row identifies
   itself to its handler **by being the firing control** — the listbox selection index is used for
   keyboard highlight, not to route the click.
5. Accumulates the row pointers and installs them into the listbox, then re-lays-out.

Two mechanisms for "which server did the user pick", both usable:

- **(a) selection index** — the low-risk one. Put a single **Connect** button beside the list;
  when it fires, read `games_listbox + 0x2C6`. Because we build the rows in our server-list order,
  that short index *is* the index into the agent's server array. Pure field read.
- **(b) per-row click event** — mirror save/load: `AddEvent(rowButton, 0x27, ctx, OnServerClicked)`
  on each row; the firing-control pointer (see `connect-flow.md`'s callback ABI) identifies the row.
  More engine callbacks, same proven `AddEvent`.

## 3. Building / showing the panel — VERIFIED addresses, INFERRED assembly

The modal machinery is owned by **`CSWGuiManager`** (`GUI_MANAGER_PTR 0x007A39F4`; `this =
*(void**)0x007A39F4`):

| Address | Routine |
| --- | --- |
| `0x0040BC70` | `AddPanel(panel, bModalFlag, bShow)` |
| **`0x0040BD90`** | **`PushModalPanel(panel)`** — hides current focus, `AddPanel(panel,0,1)`, sets focus. ret 4. VERIFIED |
| `0x0040BE00` | `PopModalPanel` |
| `0x0040C830` | `RemovePanel` |
| `0x0040BA00` | `CSWGuiPanel::GetControl(i)` — returns `panel_controls[i]` (`panel+0x20` array, `+0x24` count). VERIFIED |

Two ways to obtain a panel that *contains* a listbox:

- **Reuse `CSWGuiSaveLoad`.** It already is a panel with a live, laid-out `CSWGuiListBox` and its
  proto row. Lowest widget-construction risk (the engine built every control). But its
  constructor/`PopulateGameList` read the saves directory and wire save/load handlers, and its OK
  path *saves the game* (see the `WriteGame 0x006C8790` crash warning in `connect-flow.md`), so we
  would have to `ClearItems` and re-drive it — non-trivial and off-label. **INFERRED / NEEDS-LIVE.**
- **Load our own `.gui`.** Ship `Override\coopbrowser.gui` defining a panel with a `LISTBOX`
  control (+ its proto item) and Connect/Cancel/manual-IP buttons, and let the engine's own
  `CSWGuiListBox::Load 0x0041D5B0` / `LoadProtoItem 0x0041D3E0` instantiate it. This is the *engine's*
  clone path, not ours, so it is the safe way to get real rows — but wiring a fresh panel object
  up to `PushModalPanel` from the plugin has not been exercised. **INFERRED / NEEDS-LIVE.**

Populate cadence: fill once on open (rebuild rows from the agent's current server list), and
optionally refresh on a timer as LAN discovery finds more servers — call `ClearItems 0x00419A90`
then re-add. Reading the shared block for the server list each open is enough for v1.

## 4. Manual IP + password — VERIFIED (from connect-flow.md)

Reuse the proven text-entry popup rather than inventing one:

- `CSWGuiSaveNamePanel` (vtable `0x007576D0`, size `0x690`, asset `savename.gui`) — one of only
  two live editbox instances in the game. The typed string is a `CExoString` at **`panel+0x548`**;
  the engine feeds keystrokes into it automatically once it has focus.
- Show it from the browser's "Manual IP" button, rebinding its OK/Cancel to plugin stubs (else the
  stock OK tries to save the game). On OK, read `panel+0x548` → the IP, hand it to the agent.
- **Password: no masking exists anywhere** (the edit format is `"%s_"`, text + caret). A typed
  password renders in cleartext. Keep the password in `coop.ini` and type only the IP — or, if a
  per-server password is required, accept that it shows on screen. **VERIFIED limitation.**

Selecting a row and clicking Connect passes `{ address, (optional) password }` to the agent
through the shared block, exactly like the existing `ConnectHost` / `ConnectSerial` fields.

## 5. Handing the pick back to the agent — INFERRED

On Connect: `idx = *(short*)(listbox+0x2C6)`; if `0xFFFF`, ignore. Else look up the agent's server
array at `idx` (same order we populated) and write its address (and chosen password) into the
shared-block connect fields the runtime command layer already consumes. No new engine call is
needed for the read — it is one 16-bit field load. This is the crash-free half of the feature.

## 6. Lowest-risk plan (layered)

**Tier 0 — ship now, zero GUI-construction risk (VERIFIED building blocks).** Do not build a
native list at all for v1. The `~` console overlay is already implemented and proven: print the
discovered servers as a numbered list with `PrintStringScreenPos 0x0044CCA0` (self-contained cdecl,
already used), and let the player type `connect <n>` or `connect <ip>` — the console capture path
already routes co-op verbs. For manual IP the `CSWGuiSaveNamePanel` editbox (§4) is proven. This
delivers "see the servers, pick one" with **no new engine callback ABI and no widget construction**.

**Tier 1 — native list, selection-by-index (INFERRED, needs a live pass).** Add the real
`CSWGuiListBox` on the main menu behind the existing BTN_WARP "Co-op" button (rebind per
`connect-flow.md`). Get the rows from the *engine's* clone path (load a `.gui`, or reuse
`CSWGuiSaveLoad`'s already-built listbox), set each row caption with `SetText 0x00415E00`, and
read the pick with the single field load `listbox+0x2C6` when a Connect button fires. This keeps
us to **field reads + two verified thiscalls** (`SetText`, and `AddEvent` which is already proven).

**Tier 2 — custom browser panel (INFERRED).** A bespoke `coopbrowser.gui` with list + Connect +
Manual-IP + Refresh, columns for name/players/address, per-row click events. Best UX, most new
surface; do only after Tier 1 is confirmed live.

### Crash-risk register (every guessed-ABI engine call)

- **`AddControls 0x0041C1D0` with a hand-built `CExoArrayList<CSWGuiControl*>`** — HIGHEST RISK.
  It `ClearItems`, then for each element dereferences the control's vtable and reads its extent
  (`+0x04`) to lay it out. Feeding it controls we allocated ourselves (wrong vtable, uninitialized
  extent) will fault inside `OrganizeControls`. **Prefer letting the engine clone `proto_item`;
  do not manufacture row controls by hand.**
- **`CSWGuiSaveLoad` reuse** — its stock handlers save/load the game; any row we leave stock-wired,
  or an OK we fail to intercept, calls `WriteGame 0x006C8790` / module-load and crashes. Must
  `ClearItems` and rebind every event before showing.
- **`SetText 0x00415E00`, `PushModalPanel 0x0040BD90`, `AddEvent 0x0041AB20`** — ABIs are simple
  and (for AddEvent) already proven in `connect-flow.md`, but the `this`/arg marshaling for a
  fresh panel is unrun. Verify with one live call before relying on it.
- **Building `CExoString` arguments** — `SetText` takes a `CExoString*`; construct it correctly
  (`{char*@0, len@4, ...}`) and keep it alive across the call.

### What must be verified live (NEEDS-LIVE)

1. That a `CSWGuiListBox` (whether from a loaded `.gui` or reused from `CSWGuiSaveLoad`) can be
   pushed modally from the **main menu** and renders (the present path is universal per
   `console-overlay.md`, but the menu-time GUI tree is unconfirmed for a listbox panel).
2. That `SetText 0x00415E00` on a cloned row button updates the visible caption without touching
   TLK, with our thiscall marshaling.
3. That `listbox+0x2C6` reads the row the user visibly highlighted (index origin, `0xFFFF`-when-none).
4. The engine clone path for rows (`proto_item` + the entry's `[vtbl+0x98]` init, or `.gui` `Load`)
   — confirm a row appears with our text before wiring Connect.

## Key addresses

`CSWGuiListBox` vtable `0x0073E840` · `ClearItems 0x00419A90` · `AddControls 0x0041C1D0` (ret 0x10) ·
`GetControl 0x0041A5A0` · `GetSelectedControl 0x0041A5D0` · `SetSelectedControl 0x0041C040` ·
`HandleLMouseDown 0x0041C4A0` · `Load 0x0041D5B0` · `LoadProtoItem 0x0041D3E0` · selection_index
`listbox+0x2C6` (0xFFFF=none) · controls array `listbox+0x29C` / count `+0x2A0` · proto_item `+0x2CC`.
Row text: `CSWGuiButton.text +0x154`, `CSWGuiText.text_params +0x18`, caption `CExoString @+0x00`;
`CSWGuiTextParams::SetText 0x00415E00` (ret 4).
`CSWGuiSaveLoad` vtable `0x00757650` size `0x1160`, `games_listbox +0x934`; `PopulateGameList
0x006CC160` · `CSWGuiSaveLoadEntry 0x00757740` size `0x248`, `SetXboxTitle 0x006C9780`,
`LoadData 0x006C8E50`, `ShowGame 0x006C89D0` · row-event handlers `VerifyLoadGame 0x006CC0E0` /
`HandleSaveButton 0x006CBB60`.
`CSWGuiManager` `GUI_MANAGER_PTR 0x007A39F4` · `AddPanel 0x0040BC70` · `PushModalPanel 0x0040BD90` ·
`PopModalPanel 0x0040BE00` · `RemovePanel 0x0040C830` · `CSWGuiPanel::GetControl 0x0040BA00`.
`AddEvent 0x0041AB20` (see connect-flow) · manual IP editbox `CSWGuiSaveNamePanel` typed string
`panel+0x548` · console print `PrintStringScreenPos 0x0044CCA0`.

---

## The listing itself (2026-08-13, live)

What the panel shows, after a pass driven by looking at it rather than at the block.

### Two rendering rules this GUI enforces, both learned by breaking them

1. **There is no newline.** An embedded `\n` in a caption draws as `?` — the row read
   `Chris's Co-op Game?10.5.0.2:27140`. Layout comes from the renderer's own word wrap and
   from keeping text short, never from control characters.
2. **Overflowing text shows its TAIL, not its head.** When a caption needs more lines than
   its box has, what survives is the bottom. That single fact explains both defects this pass
   fixed:
   - the row's original `"1. Name [module]  address  (players/max)"` padded the name to 18 and
     the address to 23 — ~50 characters in a box about twenty wide — so the browser displayed
     an IP and a player count with **the host's name pushed off the top**;
   - the whole world string in the narrow PLANET label displayed as `Module`, the last word of
     "Endar Spire - Command **Module**", and the extra wrapped line ran into the AREA label
     beneath it.

### The layout now

```
row (list, two lines)        details pane
---------------------        ------------
Chris's Co-op Game           ENDAR SPIRE        <- planet label
10.5.0.2:27140 (1/8)         COMMAND MODULE     <- area label
                             Password required  <- shot caption, only when locked
                             [face][face][face] <- the host party's portraits
```

⚠ The world is SPLIT ON `" - "` deliberately: the game's own area names are written
"Planet - Area" ("Taris - Lower City", "Unknown World - Temple Main Floor") and the panel's
two labels are the save screen's planet and area. Each half fits on one line; the whole
string does not.

### Where the names come from

`Mod_Name` in `module.ifo` is **not** it — measured on four base-game modules, every one of
them returns its own resref. The player-facing name is on the AREA, as a `Name` StrRef into
`dialog.tlk`: 10221 -> "Endar Spire - Command Module", 35894 -> "Taris - Lower City". Read
locally by the browsing agent (`GameFiles.ModuleNames`), because both installs have the same
files and a resref is 16 bytes on the wire where a sentence is not. Verified against the
install: 6/6 real modules resolve, longest 33 characters, an unknown resref falls back to
itself.

The host's display name is `coop.ini server_name`, distinct from `name` (the character's).

### The portraits, and why they were white — SOLVED

The data path is complete and verified — the host reads `portrait_id` off each party
creature, resolves it through `portraits.2da` (`baseresref`; row 21 -> `po_pmha4`), the
beacon carries up to three resrefs (v3 tail), the block's details array holds them, and the
panel's image labels take them. Live, two of the three boxes fill for a party of two. They
fill **white**, which is what an image label draws when its fill texture does not resolve.

**`CSWGuiBorderParams::SetFillImage` takes a `CResRef*`, not a `CExoString*`.** Its comparison
callee at 0x406DC0 is `repe cmpsd` with `ecx = 4` — sixteen raw bytes compared in place — and
the engine's own caller copies a 16-byte record out of the save entry and passes THAT
(`ShowGame`, 0x6C8B39 -> 0x6C8B43). Handing it a CExoString makes the engine read a POINTER
and a LENGTH as the texture's name, which resolves to nothing and draws a blank white fill.

Everything else was already right: `portrait_id` at `CSWSObject+0x30` reads 15, the save's own
`PortraitId` field agrees, portraits.2da row 15 is `po_pfhb3` (female, forpc, appearance 113
against the PC's 114), and `po_pfhb3` is present in the texture pack. The id was never the
problem — an early misreading of a hex dump made it look like 21 (`po_pmha4`, a male portrait)
and sent this off after the client/server split for a while.

⚠ The blanking calls that predated this had the same defect and hid it perfectly: garbage
blanks an image just as well as an empty name does. A wrong argument type is invisible for
as long as you only ever use it to clear something.

### The middle image: the module's load screen

The big pane — where the save panel draws a save's screenshot — draws
`load_<module>`, the game's own loading art for wherever the host is standing. The format
string `"load_%s"` is at file offset 0x345580 and the 97 textures live in
`TexturePacks\swpc_tex_gui.erf` (`load_end_m01aa`, `load_tar_m03aa`, ...). Composed by the
agent, so a convention change is one function rather than a plugin rebuild.

⚠ The area's own `LoadScreenID` is NOT the source: it reads 0 on every base-game module
measured, so it is dead in KOTOR 1.

The engine's portrait walk is `esi + 0x5E4` with stride `0x140` for three faces
(0x6C8B31..0x6C8B52), which is exactly the `PanelImageParams` this controller already had —
`[0x224, 0x5E4, 0x724, 0x864]`, screenshot then three portraits.

---

## The address box (2026-09-18) — built, NEEDS-LIVE

§4's "Manual IP" button, finally built — as a ROW, not a button, re-using the password panel
wholesale. Review-2026-09-18 §2 had the site and the package README describing an address
box that did not exist; the joiner typed `connect host:port` in the console.

### What was built

- **The row.** `PopulateRows` (`NativeBrowserController.Open.cs`) now appends a permanent last
  row, caption `Connect to an address...`, after the server rows (or the scanning placeholder).
  It is an engine-cloned row like every other (`BuildRow`: `OperatorNew` → `EntryCtor` →
  `Initialize_2(rect, proto)` → `SetText`), with the same accept binding; nothing is hand-built.
  Why a row and not a fourth button: `CSWGuiSaveLoad` has exactly three buttons (Load/Save →
  Connect, Back → stock close, Delete → Host), and manufacturing a control by hand is the crash
  register's #1 item (native-browser-panel.md). The details pane blanks for it and the caption
  slot reads `Press Enter to type an address`. The row survives a list refresh: the highlight
  restore keys on a marker string (`address-row`) that no published address can equal.
- **The click.** `OnConnectClicked` (Connect button, or Enter/A on the row) sees
  `selection == _addressRow`, records `_addressPromptPending` and returns — the modal opens on
  the NEXT plugin frame in `OnFrameWhileOpen`, never inside the engine's event dispatch (the
  same deferral as the locked-row password ask).
- **The panel.** `PasswordEntryModal.TryOpen(guiManager, Ask.Address, prefill)` — the same
  `CSWGuiSaveNamePanel` build, the same OK/Cancel rebinds (the stock OK would `WriteGame`), the
  same modal push over the browser (`AddPanel(…, 1, 1)`), the same `PanelExists` polling. Three
  differences: the title is `Host address (keyboard only)` (a pad cannot type into it — the
  title says so); the editbox is SEEDED with the last address through the same
  `CSWGuiEditText::SetText 0x00416490` the empty-box clear already used; and OK composes
  through `ConnectAddressRule` instead of `PasswordAnswer`.
- **The grammar** (`src/KotorCoop.Plugin/ConnectAddressRule.cs`, pure, compiled into the test
  host). Accepts `host`, `host:port`, `[v6]:port`, `[v6]`, a bare v6 literal, and a leading
  `connect ` typed by habit; ALWAYS emits an explicit port (`IPEndPoint.TryParse` reads a
  bracketed literal with no port as port 0). Rejects spaces, URLs/paths, bad ports, brackets
  around a hostname, non-DNS characters and over-long hosts, each with a short line that fits
  the title label. `tests/KotorCoop.Tests/ConnectAddressRuleTests.cs` also pins that the
  agent's `CoopCommand.TryResolveEndpoint`, `ConsoleCommand.Parse` and
  `SteamConnectStringRule.TryParse` read every emitted shape back to the same endpoint — the
  plugin cannot reference those assemblies, so agreement is a test, not a call.
- **Rejection stays open.** Text the rule refuses does not close the panel: the OK handler
  re-captions LBL_TITLE with the reason (the same guarded `TrySetTitle` as at open — label
  vtable at +0x550, params back-pointer at +0x688) and returns, the player's text still in
  the box. An empty OK is a cancel, as for the password.
- **The connect.** On `Outcome.Answered` for an address ask the browser TAKES the composed
  line out of the modal (`TakeAnswer`; the sweep bookkeeping clears the modal's buffer), marks
  `_addressConnectPending` and closes itself the stock way. Only in the "panel freed" branch
  (`PanelExists` false, fresh main menu built) is the line published through
  `PublishConsoleCommand`, byte-for-byte `connect host:port`. That is the row-connect ordering
  (act only after the sweep) kept for the same reason: nothing the agent does can race the GUI
  teardown. The agent then runs exactly the path a typed `connect` takes: `Join` →
  `TryResolveEndpoint` → `Dial(locked: false)` → on `E_PASSWORD`, `ReflectPhase` →
  `PromptForPassword` → `RequestPasswordPrompt` → `BrowserPasswordSerial` →
  `PasswordPromptController` floats the same modal (as a password ask) over the fresh menu.
  No wire, block or protocol change.
- **Memory.** The last address is kept in `kotorcoop-last-address.txt` beside the game (the
  console history's neighbour), written on the plugin frame after the publish, never in the
  OK handler. With no sidecar the box seeds from coop.ini `host` + `port`
  (`ConnectAddressRule.PrefillFromConfig`). coop.ini is NOT written: nothing in the plugin or
  the agent rewrites it after install (PackageInstaller writes it once; both readers only
  read), and a first writer for a hand-commented player file was not worth one prefill.
- **Fallbacks.** Modal latched off (`PasswordEntryModal.Available` false) before or during the
  ask → close the browser and `Odyssey.OpenConsoleWith("connect <last>")` after the sweep, the
  surface the password ask's fallback already verified at this menu. A transient open
  refusal → the browser stays up, the row stays highlighted, the next Connect retries.
- **Input class.** Not touched: both closes here are the retail sweep (modal) and the stock
  Back path (`DisplayMainMenu`), at the main menu, and no `Show*Gui` follows — the in-world
  `CGuiInGame+0x30` rule (input-class-after-gui-close) does not apply.

### NEEDS-LIVE checklist

1. Open the browser (Co-op / F9 at the menu): the last row reads `Connect to an address...`
   and the details pane blanks with the hint when it is highlighted; server rows unchanged.
2. Highlight it and press Connect (and, separately, Enter on the row): the text panel floats
   over the browser titled `Host address (keyboard only)`. **Prefill**: with a
   `kotorcoop-last-address.txt` (or coop.ini `host`) present, the box shows that address with
   the caret after it and Backspace edits it — the seeding call is the one the empty clear
   used, but a NON-EMPTY seed has never been watched.
3. Type `not an address`, OK: the title changes to `No spaces in an address`, the panel stays
   up, the text stays. Clear it, OK: the panel closes, the browser stays (cancel).
4. Type the host's `ip:port`, OK: the browser closes, the agent log shows the console line
   `connect ip:port` arriving AFTER `native browser: connecting to typed address …`, the
   session connects, the menu auto-load fires as for a row connect.
   `kotorcoop-last-address.txt` now holds the address; reopening the box shows it.
5. Against a password-protected host: after the connect the password modal appears over the
   main menu (the serial path), and the answer joins.
6. Alt-tab with the address box up: Dismissed, the browser stays (same as the password ask).
7. A list refresh (a second host appears) while the address row is highlighted keeps it
   highlighted (the marker restore).

### Key additions

`ConnectAddressRule` (plugin, pure) · `PasswordEntryModal.Ask` / `TryOpen(gui, ask, prefill)` /
`TakeAnswer` / `TrySetTitle` · `NativeBrowserController.Address.cs` (`_addressRow`,
`_addressPromptPending`, `_addressConnectPending`, `_addressConsoleFallbackPending`,
`ReadLastAddress`, `RememberAddress`) · sidecar `kotorcoop-last-address.txt`.

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

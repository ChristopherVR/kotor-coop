# Native server-browser panel — repurposing `CSWGuiSaveLoad` (research 2026-07-31)

How to take the engine's own save/load screen — whose constructor the plugin **already calls
live** (`MenuLoadController.cs` constructs it every plugin-driven load) — and turn it into the
co-op server browser: clear its save rows, fill its embedded `CSWGuiListBox` with one engine-built
row per discovered server, retitle it "CO-OP SERVERS", show it at the main menu, and read the
selected row on Connect. Verified against `swkotor.exe` (GOG retail 1.0.3; disk == memory, no
ASLR, base `0x400000`) by disassembly (`tools/disasm.py`) and the sha256-matched symbol DB.
Nothing was modified.

Builds on `server-browser.md` (listbox layout, field offsets), `connect-flow.md` (AddEvent ABI,
callback shape) and Handoff.md's engine-side load path. Everything those docs tagged INFERRED on
the row-construction and panel-show paths is resolved below.

Tag key: **VERIFIED** = read from the binary here (or already proven live) · **INFERRED** =
deduced, not yet run · **NEEDS-LIVE** = must be confirmed on the running game before shipping.

## Headline

Every ABI the browser needs exists and is now read end-to-end: the constructor **itself calls
`PopulateGameList`**, so a freshly built panel already contains engine-cloned rows; the row clone
is one small thiscall (`CSWGuiButton::Initialize_2 0x418D80(rect, proto)`); the caption is one
`SetText 0x415E00` on `entry+0x16C`; `AddControls` takes a caller-stack `{data,count,cap}` triple
and **transfers row ownership to the listbox** (ClearItems frees them); the retail Back button
already performs a perfect close (mark panel for deletion, rebuild the main menu) with **no
rebinding needed**. The two facts that were not guessable: the manager's per-frame Update contains
a **panel sweep that deletes any panel whose `+0x44` state bits read `0x400`** — this is how both
our panel AND the main menu itself get freed — and a listbox row click **fires the row's `0x27`
only after the selection index is already updated** (selection on mouse-down, click on mouse-up),
so selection-index reads are always current inside a click handler.

## 1. `CSWGuiSaveLoad::Constructor 0x006CC680` — VERIFIED (statically and live)

`__thiscall CSWGuiSaveLoad* Constructor(this, CSWGuiManager* guiManager, int bSaveMode,
int bFromMainMenu)`, **ret 0xC**, returns `this`. Memory from `operator_new(0x1160) 0x006FA7E6`.
The plugin already calls it live as `(panel, [menu+0x18], 0, 1)` — construction itself is proven.
Full trace:

1. `CSWGuiPanel::Constructor 0x0040B570(guiManager)` at `0x6CC6AB`; vtable set to `0x757650`
   (`0x6CC6BB`). The manager lands in `panel+0x18` (used throughout as `[this+0x18]`).
2. Member constructors: a `CExoString` at `+0x6C` (a resource-directory name used by
   ShowGame/dtor), `CSWGuiLabel::Constructor 0x41ACD0` at `+0x74`, `+0x1B4`, `+0x2F4`, `+0x434`,
   vector-ctor of 3×`0x140` labels at `+0x574`; `CSWGuiListBox::Constructor 0x41BE40` at `+0x934`;
   `CSWGuiButton::Constructor 0x41C0F0` at `+0xC14`, `+0xDD8`, `+0xF9C`.
3. `StartLoadFromLayout 0x0040A680(CResRef("saveload"))` at `0x6CC783` — loads **`saveload.gui`**
   — then `InitControl 0x0040B930(name, &control, 1)` for each named control (`0x6CC79C`–
   `0x6CC9B8`), then `StopLoadFromLayout 0x0040B8F0` (`0x6CC9CD`). The `.gui` control names and
   panel offsets (string constants read from the binary):

   | `.gui` control | offset | type |
   | --- | --- | --- |
   | `LBL_PANELNAME` | `+0x074` | label — **the title** |
   | `LBL_SCREENSHOT` | `+0x1B4` | label |
   | `LBL_PLANETNAME` | `+0x2F4` | label |
   | `LBL_AREANAME` | `+0x434` | label |
   | `LBL_PM1/2/3` | `+0x574/+0x6B4/+0x7F4` | labels (party portraits) |
   | `LB_GAMES` | `+0x934` | **the listbox** |
   | `BTN_SAVELOAD` | `+0xC14` | button (Load/Save → our **Connect**) |
   | `BTN_BACK` | `+0xDD8` | button |
   | `BTN_DELETE` | `+0xF9C` | button |

4. Listbox made selectable: `call [lb_vtbl+0xA4] = 0x41B120(1)` at `0x6CC9ED` sets
   `listbox+0x2BC` **bit0** — required for `+0x2C6` selection tracking (§5).
5. **Panel-level event binds** (`0x6CC9F3`–`0x6CCA6D`), all `AddEvent 0x41AB20(eventId=0x27,
   owner=panel, fn)`; each button also gets `flags+0x44 &= ~0x4` first:
   - `BTN_SAVELOAD`: first `Global::AcceptButtonCallback 0x624BA0` (a thunk:
     `jmp [owner_vtbl+0x50]`), then **replaced** at `0x6CCA69` by `VerifyLoadGame 0x6CC0E0`
     (load mode) / `HandleSaveButton 0x6CBB60` (save mode). AddEvent same-id **replaces** — read
     directly from `0x41AB20`: event entries are 12 bytes `{owner@0, fn@4, id@8}` in the array at
     `control+0x38` (count `+0x3C`); a matching id overwrites fn+owner in place; fn=0 removes;
     otherwise appends. `connect-flow.md`'s replace semantics CONFIRMED at the machine level.
   - `BTN_BACK`: `CSWGuiPanel::OnBButtonPressed_2 0x624BB0` — a thunk `jmp [panel_vtbl+0x54]`
     which (slot `0x40B650`) calls the panel's virtual `HandleInputEvent(0x28, 1)`. **This is the
     whole retail close path** (§2).
   - `BTN_DELETE`: `0x624BC0` (→ vtbl+0x58) then **replaced** by `DeleteGame 0x6CAA90`.
6. Proto-row color fixup (`0x6CCA6E`): `proto = [panel+0xC00]` (= `listbox+0x2CC`, the
   `.gui`-loaded proto item), dereferenced **unconditionally** — `saveload.gui` always defines a
   proto row (and live construction already works, so this holds on the GOG install). Writes
   global GUI colors `0x7A23B4..` into `proto+0x90..0x98` and `0x7A23C0..` into
   `proto+0x104..0x10C` (border / hilight border colors inside the params blocks that row clones
   copy), then `SetPulsingAlpha 0x414D00(proto+0xF4, 1, 1, 0)`.
7. Mode flags: `panel+0x64` bit0 = bSaveMode, bit1 = bFromMainMenu; `panel+0x68 = 2` (`0x6CCAE1`).
8. Stock titles by TLK strref `SetStrRef 0x415E50`: load mode → strref `0x635` on the
   **BTN_SAVELOAD text params at `panel+0xD80`** and strref `0x631` on the **title label text
   params at `panel+0x15C`** (`0x6CCB04`–`0x6CCB19`). These are the two captions we overwrite (§6).
9. **`PopulateGameList 0x6CC160` is called by the constructor** (`0x6CCB1E`). A freshly
   constructed panel already contains one engine-cloned, laid-out, event-bound row per directory
   under `saves\`, and the first row's details are already shown (ShowGame is invoked for row 0
   inside PopulateGameList, `0x6CC3BE`).
10. `AddResourceDirectory("TEMP:") 0x408800` (`0x6CCB43`), then
    `SetActiveControl 0x40A630(games_listbox, 0)` (`0x6CCB61`) — the listbox is the panel's
    active control for keyboard focus. Returns `this`.

**⚠ Zero-saves special case (VERIFIED, matters for co-op).** In load mode with an empty `saves\`
directory, PopulateGameList (`0x6CC5C3`) marks **the panel itself** `+0x44 = (…&~0x300)|0x400`
(born in the delete state, §2), calls `DisplayMainMenu(0)` and pushes a **modal** "no saved games"
message box (strref `0xA5FB`) via `AddPanel(msgbox, 1, 1)`. A browser opened with zero saves on
disk would self-destruct next frame behind a modal box. Mitigation in §8.

## 2. Show and close — VERIFIED

### Show: `CSWGuiMainMenu::OnLoadSaveGame 0x0067B1A0` (thiscall, arg = firing control)

1. Guards: `(menu+0x44 & 0x600) == 0x400` → abort (a child screen is already up);
   `menu+0x140C == 0` → abort (menu not fully alive); `firingControl+0x4C == 0` → abort (the
   event-arg slot the dispatcher fills, see §3f — nonzero for real events).
2. `operator_new(0x1160)` → `Constructor(panel, [menu+0x18], 0, 1)` (`0x6CB1FF`).
3. **`AddPanel 0x0040BC70([menu+0x18], panel, 2, 1)`** (`0x67B216`). AddPanel ABI (read from
   `0x40BC70`): `__thiscall(gm, CSWGuiPanel* panel, int flags2, int bPlayOpenSound)`, ret 0xC.
   `bPlayOpenSound` → `PlayGuiSound([panel+0x60])`. `flags2` bits 0–1 are shifted into panel
   `+0x44` bits 3–4: retail `2` sets **bit3** (increments the manager's blocking-panel counter
   `gm+0x70`, which turns on the full-screen background draw) and does **not** push the modal
   stack — only `flags2` bit0 triggers `PushModalPanel 0x40BD90`. Ends with panel
   `vtbl+0x48 = OnPanelAdded 0x40B870` (refreshes mouse-over, revalidates the manager's focused
   control) and `DetermineNeedToDraw 0x40ACC0`. No other focus work: keyboard focus rides the
   panel's active control set in ctor step 10.
4. If the new panel is NOT itself in the closing state (the zero-saves case), the **menu** is
   marked `menu+0x44 = (…&~0x300) | 0x400` (`0x67B22C`).

### The `+0x44` state machine and the manager sweep — the key lifetime fact

`CSWGuiManager::Update 0x0040CE70` ends with a backward sweep over all registered panels
(`0x40CF09`–`0x40CF58`). For each panel, `f = panel+0x44`:

- `f & 0x100` → protected, skip.
- `(f >> 9) & 3 == 2` (i.e. **0x400 set, 0x200 clear**) → `RemovePanel 0x40C830(panel)` then
  **`call [panel_vtbl+0x00](1)` — the panel is deleted and freed** (`0x40CF33`–`0x40CF3E`).
- `(f >> 9) & 3 == 1 or 3` (0x200 set) → clear `0x700`, `RemovePanel` only — **removed but kept
  alive** (`0x40CF46`–`0x40CF51`).
- else untouched.

`RemovePanel 0x40C830` itself never frees: it pops the modal stack if the panel is on top,
removes it from the array, calls `vtbl+0x4C OnPanelRemoved`, fixes the counters. VERIFIED.

Consequence, verified against the show path above: **opening the save screen marks the main menu
`0x400`, so the next Update REMOVES AND FREES the CSWGuiMainMenu object.** While the browser is
up, `[clientInternal+0x280]` is a dangling pointer. Nothing in our plugin may resolve/touch the
menu while the panel is open.

### Close: the Back button — VERIFIED, and it is free

`BTN_BACK` click → thunk `0x624BB0` → `CSWGuiSaveLoad::HandleInputEvent 0x6C86D0(0x28, 1)`
(panel vtbl+0x3C). For events `0x28`, `0x2D`, `0x2E` with arg≠0:

1. `PlayGuiSound(0)` (`0x6C86F5`).
2. `panel+0x44 = (…&~0x300) | 0x400` — our panel enters the delete state (`0x6C8702`).
3. Because `panel+0x64` bit1 (bFromMainMenu): **`CClientExoApp::DisplayMainMenu 0x5ED420(0)`**
   → `0x5FCA30`: if the old menu at `+0x280` is no longer registered (it was swept),
   `operator_new(0x1414)` → `CSWGuiMainMenu::Constructor 0x67C4C0(gm)` → stored at
   `[clientInternal+0x280]` → `AddPanel(gm, menu, 2, 1)` → restarts menu music (`0x5F9AF0`). A
   **brand-new main menu** replaces the freed one; the stale pointer heals here.
4. Falls into `CSWGuiPanel::HandleInputEvent 0x409E60` (base dispatch) and returns. Next Update:
   the sweep RemovePanel+deletes our panel; the listbox destructor inside frees every row (§5).

So: **leave BTN_BACK stock-wired and the entire clean-close problem is solved by retail code.**
(In-game, `+0x64` bit1 clear, the same handler calls `PopModalPanel 0x40BE00` instead — not our
path.)

## 3. Row creation — every ABI from `PopulateGameList 0x006CC160` — VERIFIED

PopulateGameList enumerates `GetDirectoryList 0x5E6640(out, &CExoString("SAVES:"), 0xFFFF, 1, 1)`
(directory entries are 8-byte `CExoString`s) and per save does exactly this (loop at `0x6CC290`):

### a. Entry constructor

`operator_new(0x248) 0x6FA7E6` → **`CSWGuiSaveLoadEntry::Constructor 0x6CB940` —
`__thiscall entry*(this)`, NO arguments, ret 0** (call site `0x6CC2AA` pushes nothing), returns
`this`. Gives the row the entry vtable `0x757740` (a `CSWGuiButton` at `+0x00` plus save metadata:
`+0x1C4` flags, `+0x1C8` save_number, `+0x1D8` savegamename, `+0x1E8` areaname, `+0x1F0`
lastmodule, `+0x200` display-title string, `+0x208` screenshot). Rows MUST come from the engine
heap — ClearItems frees them with the engine's scalar delete (§5).

### b. Clone-from-proto: `[entry_vtbl+0x98]` = `CSWGuiButton::Initialize_2 0x00418D80`

Call site `0x6CC2C6`: `push proto; push &rect; ecx=entry; call [vtbl+0x98]`, where
`proto = [panel+0xC00]` (listbox `proto_item`) and `rect` is a stack `CSWGuiExtent`
**`{x=0, y=0, w=0, h=0x2A}`** (0x2A = 42 px row height; locals set at `0x6CC1AB`–`0x6CC1B7`).

`Initialize_2` — `__thiscall(this=row, CSWGuiExtent* rect, CSWGuiControl* proto)`, **ret 8** —
is 10 instructions: it tail-dispatches `[this_vtbl+0x9C]` = `CSWGuiButton::Initialize 0x417970`
with `(rect, proto+0x16C, proto+0x80, proto+0xF4)` (ret 0x10), which:

- copies `rect` into the row's extent `this+0x04..0x10`;
- initializes the row's two borders `this+0x6C` and `this+0xE0` (`CSWGuiBorder::Initialize
  0x415190`) from the proto's border params (`proto+0x80`) and hilight params (`proto+0xF4`) —
  the blocks the panel ctor pre-colored in §1.6;
- computes the text extent (`0x40A4A0`) and initializes the row's `CSWGuiText` at `this+0x154`
  (`0x417310`) from the **proto's text params `proto+0x16C`** (font, colors) with alpha 1.0.

**Is it required?** Yes: without it the row has a zero extent, no borders, and no font —
`AddControls` copies the extent into the layout array and `OrganizeControls`/`Draw` consume all
three. It is the entire "clone" — there is no other copying step.

### c. `LoadData 0x6C8E50` — NOT needed for a clickable text row

`__thiscall(entry, CExoString* saveDirName)`, ret 4. It only parses the save's metadata (builds
`"SAVES:<dir>"` paths, fills `+0x1C4/+0x1C8/+0x1D8/+0x1E8/…`). Proof it is skippable: the
save-mode **"new slot" row** (`0x6CC423`–`0x6CC4AA`) is created by ctor → `Initialize_2` →
`SetStrRef(0x636)` → flags → `AddEvent` — **no LoadData** — and renders and clicks in retail. A
co-op row with empty/zeroed metadata is structurally identical to that retail row. Leave
`+0x1C4 = 0` (see §4 for why that is the safe value).

### d. The caption

`SetXboxTitle 0x6C9780(entry, CExoString* dirName)` builds the display string ("Game N — time —
name", AUTOSAVE via strref `0x639`, quicksave via `0xBB77`) and ends (`0x6CC9A44`) with
**`CSWGuiTextParams::SetText 0x00415E00(ecx = entry+0x16C, CExoString* s)`, ret 4** — the row's
text params live at **`entry+0x16C`** (= button `+0x154` CSWGuiText `+0x18` params; matches
`server-browser.md`). The new-slot row uses `SetStrRef 0x415E50` on the same object. So an
arbitrary server caption is exactly one call: `SetText 0x415E00(entry+0x16C, &myString)`, with the
`CExoString` built by `CStrConstructor 0x5E5A90` (already used by the plugin). Retail sets the
caption after the clone and **before** `AddControls`/first draw — same order for us, so the
"live string exists?" concern in server-browser.md §1 does not arise. SetText also writes
`str_ref = -1` so no TLK lookup competes.

### e. Handing rows to the listbox: `AddControls 0x0041C1D0`

Call site `0x6CC548`–`0x6CC557`: `AddControls(ecx = panel+0x934, &arr, 1, 0, 0)`, **ret 0x10**.
`arr` is **three dwords on the caller's stack** `{data@+0, count@+4, capacity@+8}` — the
`CExoArrayList` shape from server-browser.md confirmed at the call site. `data` is a plain
`operator_new`'d array of row pointers which **the caller frees itself right after the call**
(`0x6CC577`) — AddControls copies the pointers out, so any memory valid for the duration of the
call (even a pinned managed buffer) works for the triple and the array.

AddControls body (`0x41C1D0`):

1. **Calls `ClearItems 0x419A90` first** — old rows are freed per §5. Refresh = just call
   AddControls again with new rows.
2. Sets `listbox+0x2BC`: bit1 = arg2 (**ownership: listbox deletes rows in ClearItems**), bit2 =
   arg3 (restore-extents-on-clear variant), bit8 = arg4, bit3 always; bit0 (selectable, from ctor
   §1.4) is preserved. Retail `(1,0,0)` → owned rows. **Pass the same.**
3. Per control: skip if already present (`indexOf 0x6C2A40`); else `control+0x50 = rowIndex`,
   `control+0x34 = listbox` (parent backpointer — the click path and sounds use it), append
   pointer (`Add_2 0x671C00` into `+0x29C/+0x2A0`), append the control's extent `+0x04..0x10`
   into the extents array `+0x2A8` (`0x41B860`).
4. `OrganizeControls 0x41B140` — lays rows into the viewport. What it needs from each control is
   only what `Initialize_2` set: a valid extent (height) — x/y/width are assigned from the
   viewport. Engine-cloned rows therefore lay out with zero extra work.

### f. Events bound per retail row — the complete list to rebind/omit

At `0x6CC30E`–`0x6CC341`, each real save row gets (owner = panel in all cases):

| event | handler (load mode) | fires when |
| --- | --- | --- |
| `0x27` | `VerifyLoadGame 0x6CC0E0` | row clicked / activated |
| `0x29` | `DeleteGame 0x6CAA90` | delete chord (X button path, `0x40B660` thunk class) |
| `0x00` | `ShowGame 0x6C89D0` | **row becomes selected** (see below) |

(The save-mode new-slot row: only `0x27`→`HandleSaveButton` and `0x00`→`ShowGame`.)

**Nothing stock-wired survives our repopulation**: these bindings live in each row's own event
array (`row+0x38`), and ClearItems **frees the rows**, bindings and all. Our rows contain only
what we bind. The panel-level button bindings (§1.5) do survive and are handled in §6/§8.

**How the events actually fire — VERIFIED, and it guarantees selection-before-click:**

- Mouse **down** on a row: `CSWGuiListBox::HandleLMouseDown 0x41C4A0` hit-tests the row
  (`HitCheckMouseLocal 0x41A5E0`), and — listbox selectable (bit0) — if the index differs from
  `+0x2C6`, calls `SetSelectedControl 0x41C040(index, 1)` (`0x41C66D`). SetSelectedControl fires
  the OLD row's `HandleInputEvent(1, 1)` (deselect), writes **`+0x2C6 = index`**, fires the NEW
  row's `HandleInputEvent(0, 1)` — **event `0x00` = "selected"**, which is what routes to
  ShowGame in retail — and plays the row's sound byte (`row+0x55`).
- Mouse **up**: `HandleLMouseUp 0x41A700` fires the **listbox's** `HandleInputEvent(0x27, 1)`
  (`0x41A77A`); the listbox dispatcher `0x41CE20`'s default case (`0x41D2B2`) **forwards any
  unhandled event to the currently selected row's** `HandleInputEvent`, whose base dispatch
  `0x418750` looks up the row's event list and calls
  `fn(ecx = owner, stack = firingControl=row)` — after storing the event id in `row+0x48` and the
  arg in `row+0x4C` (this is the `+0x4C` OnLoadSaveGame/ShowGame test). So by the time any `0x27`
  handler runs, `listbox+0x2C6` already names the clicked row. Keyboard: events `0x31/0x32/0x3D`
  move the selection inside `0x41CE20`; everything else forwards to the selected row the same way.
  An event with no binding dispatches to nothing (events 0/1 additionally toggle the highlight
  flag via `vtbl+0x40` — cosmetic, safe).

## 4. The details pane — what reads entry metadata, and when — VERIFIED

`ShowGame 0x6C89D0(panel, entry)` is the **only** reader of entry metadata outside the load/save/
delete handlers; the panel's Draw is the generic `CSWGuiPanel::Draw 0x40B760` (iterates controls,
no entry access), so **no per-frame code dereferences entry fields**. ShowGame branches on
`entry+0x1C4`:

- **bit0=1, bit1=0** (a real save): enables the delete button (`vtbl+0x88 SetEnabled(1)` — a
  text-color toggle, `0x418DB0`), splits `areaname +0x1E8` on `" - "` into the planet
  (`SetText` on `panel+0x3DC`) and area (`panel+0x51C`) labels — an **empty** areaname takes the
  find-fails branch and writes empty strings, no deref hazard (`0x6C8A73`–`0x6C8AAE`) — then
  party portraits and the screenshot (mounts the save directory as a resource dir via
  `GetGameDirectory 0x6C8250` or uses the `+0x208` resref).
- **bit0=0 (or bit0&bit1)** — the path a `+0x1C4 = 0` co-op row takes (`0x6C8C9A`): writes empty
  strings to the planet/area labels, clears the three portrait images, clears the screenshot.
  **Completely safe with empty metadata.**

So: give co-op rows `+0x1C4 = 0` and even a stray ShowGame (we don't bind event `0x00`, but
keyboard/highlight paths cannot summon it anyway once unbound) merely blanks the pane. One
residue: after the ctor's stock PopulateGameList ran, the pane shows the **last real save's**
planet/area/screenshot. Blank it once after repopulating: `SetText 0x415E00` with `""` on
`panel+0x15C` (title — we overwrite anyway), `+0x3DC`, `+0x51C`, `+0x29C` (screenshot caption),
and `CSWGuiBorderParams::SetFillImage 0x414C00(label+0x70, &CExoString(""), 0)` for the image
labels at `+0x1B4` (→ `+0x224`) and `+0x574/+0x6B4/+0x7F4` (→ `+0x5E4/+0x724/+0x864`) — the exact
calls ShowGame's clear path makes (`0x6C8C9A`–`0x6C8D14`), so the ABI is retail-proven.

## 5. `ClearItems 0x419A90`, ownership, selection — VERIFIED

- If `listbox+0x2BC` **bit1** (set by retail/our `AddControls(…,1,0,0)`): for every non-null row,
  **`call [row_vtbl+0x00](1)`** — the scalar deleting destructor: rows are destructed AND freed
  (`0x419AD0`–`0x419AE1`). The listbox owns rows. Clearing the ctor-built save rows neither leaks
  nor double-frees — but every previously held row pointer is dead after any ClearItems or
  AddControls call.
- If bit1 clear: rows are only unparented (`row+0x34 = 0`, optional extent restore under bit2).
- Tail always: cached ptr `+0x2DC = 0`, count `+0x2A0 = 0`, extents count `+0x2AC = 0`,
  **`selection_index +0x2C6 = 0xFFFF`**, scroll `+0x2C2 = 1` (`0x419B4B`–`0x419B67`).
- Panel destruction reaches the same place: `CSWGuiSaveLoad::Destructor 0x6CA110` → listbox
  destructor `0x41B060` → deletes `proto_item +0x2CC` then **calls ClearItems** → owned rows
  freed. No action needed from us at teardown.
- Selection semantics: `0xFFFF` = none (VERIFIED in ClearItems and `SetSelectedControl 0x41C040`,
  which range-checks and resets to `0xFFFF` on invalid index). Mouse updates it on **mouse-down**,
  before the mouse-up click event (§3f); keyboard paths in `0x41CE20` move it before forwarding.
  A Connect handler may always trust `short idx = *(short*)(panel + 0xBFA)` (`0x934+0x2C6`).

## 6. Retitling and buttons — VERIFIED

All captions go through `CSWGuiTextParams::SetText 0x00415E00` (thiscall, `CExoString*`, ret 4 —
the exact call retail makes at `0x6C9A4B`):

| target | text params address | stock content |
| --- | --- | --- |
| title (`LBL_PANELNAME`) | `panel+0x15C` (label `+0x74` + `0xE8`) | strref 0x631 "Load Game" → **"CO-OP SERVERS"** |
| `BTN_SAVELOAD` | `panel+0xD80` (button `+0xC14` + `0x16C`) | strref 0x635 → **"Connect"** |
| `BTN_BACK` | `panel+0xF44` (`+0xDD8` + `0x16C`) | keep ("Back") |
| `BTN_DELETE` | `panel+0x1108` (`+0xF9C` + `0x16C`) | hidden instead |

Hiding the delete button: clear **bit1 (0x2) of `panel+0xFE0`** (`delete_button+0x44`).
Verified consumers of that bit: `CSWGuiPanel::Draw` draws a control only if
`control+0x44 & 2` (`0x40B841`), and `CSWGuiControl::HitCheckMouse 0x4187C0` returns null unless
bit1 set (and bit5 clear) — so clearing it removes both rendering **and** clicks.
(Note the ctor's own `&= ~0x4` on the buttons is a different bit — pulse/highlight — don't copy it.)

## 7. Lifetime, destruction, teardown — VERIFIED core, one NEEDS-LIVE

- Destructors: `Destructor 0x6CA110` (`__thiscall`, ret 0, non-deleting: removes the `TEMP:`
  resource dir and the `+0x6C` dir, destroys buttons/listbox(→rows)/labels, base
  `CSWGuiPanel::Destructor 0x40CF70`); vtable slot 0 `Destructor_2 0x6CB0C0`
  (`__thiscall(int freeFlag)`, ret 4) — the scalar deleting form the sweep calls with 1.
- **Construct per open; let the engine free on close.** The retail pattern is: every open is a
  fresh `new 0x1160` + ctor; every close is `+0x44 → 0x400` + next-frame sweep
  (RemovePanel + delete). Keeping one panel across open/close cycles is possible in principle by
  closing with `0x600` instead (state 3: RemovePanel, object kept — VERIFIED in the sweep) and
  re-`AddPanel`ing later, but no retail code exercises re-adding a previously removed
  `CSWGuiSaveLoad` (the AddPanel re-add branch at `0x40BCA0` exists but is exercised by other
  panels) — **NEEDS-LIVE** if ever wanted. There is no reason to want it: construction is cheap
  and already proven live.
- **The main menu is freed while the panel is open** (§2). Between open and close,
  `[clientInternal+0x280]` dangles; after the stock Back handler runs, DisplayMainMenu has
  replaced it with a fresh menu. Any plugin code that resolves the menu per-frame
  (`MenuLoadController.ResolveMainMenu` pattern) must not run while the browser is open, or must
  verify with `PanelExists 0x40BD70(gm, menuPtr)` first.
- **World-load teardown**: we WILL register our panel (AddPanel), so it must be gone before any
  load fires. The lever is already in hand: the Connect click closes the panel (stock-Back
  equivalent: set `panel+0x44 = (…&~0x300)|0x400` and call `DisplayMainMenu 0x5ED420(0)`), at
  least one frame passes (the sweep frees it), and only then does the agent's staged
  load request fire — the MenuLoadController flow is already multi-phase with wall-clock gaps, so
  ordering costs nothing. Handoff.md's world-up crash suspect ("registered panel left over at
  menu teardown") is thereby avoided by construction. What the load path itself would do to a
  still-registered panel remains unexercised — do not find out; close first. **NEEDS-LIVE only in
  the negative sense: we never ship the ordering that would test it.**

## 8. Recommended recipe for the plugin

Open (all on the game thread, at the main menu only, mirroring `OnLoadSaveGame`):

1. Guard: resolve the menu; require `(menu+0x44 & 0x600) != 0x400` and `menu+0x140C != 0`.
   Ensure at least one save directory exists (the agent's staging guarantees this in a session;
   create/keep a dummy `000001 - AUTOSAVE` otherwise) — else the zero-saves path (§1) births the
   panel dead behind a modal message box.
2. `panel = operator_new(0x1160); Constructor(panel, [menu+0x18], 0, 1)` — proven live.
3. Immediately check `(panel+0x44 & 0x600) == 0x400` → the zero-saves case slipped through:
   abandon (let the sweep free it), report. (NEEDS-LIVE if you instead want to rescue it.)
4. Repurpose, before showing:
   - Title/captions per §6; hide delete via `panel+0xFE0 &= ~2`.
   - `AddEvent 0x41AB20(BTN_SAVELOAD=panel+0xC14, 0x27, ctx, OnConnectClicked)` — replaces
     `VerifyLoadGame` (replace semantics VERIFIED). Same for event `0x2D` if desired. Leave
     BTN_BACK alone.
   - Build rows: per server `entry = operator_new(0x248); Constructor 0x6CB940(entry)`;
     `Initialize_2 0x418D80(entry, &rect{0,0,0,0x2A}, [panel+0xC00])`;
     `SetText 0x415E00(entry+0x16C, &caption)`; leave `+0x1C4 = 0`; optionally
     `AddEvent(entry, 0x27, ctx, OnRowClicked)` (mechanism b — not required if Connect reads the
     selection index). Do NOT bind 0x29/0x00.
   - `AddControls 0x41C1D0(panel+0x934, &{data,count,cap}, 1, 0, 0)` — frees the stock save rows,
     installs ours, lays out. Then blank the details pane (§4).
5. Show: `AddPanel 0x40BC70([menu+0x18], panel, 2, 1)`; then `menu+0x44 = (…&~0x300)|0x400`
   (retail order; the menu is reclaimed by the sweep). From this frame until close, treat
   `[clientInternal+0x280]` as poison.

Refresh (discovery found more servers): build a new row array, repeat step 4's row build +
`AddControls` (it clears/frees the old rows itself) + blank pane. Old entry pointers are dead.

Connect click (`OnConnectClicked(ctx, firingControl)`):

1. `idx = *(short*)(panel + 0xBFA)`; `0xFFFF` → ignore (or flash the title).
2. Map `idx` to the server list (same order as populated), stash address/password.
3. Close: `panel+0x44 = (panel+0x44 & ~0x300) | 0x400;` then
   `DisplayMainMenu 0x5ED420(client, 0)` — byte-for-byte what the stock handler does
   (`0x6C86FA`–`0x6C871B`), minus its PlayGuiSound if unwanted.
4. Hand the pick to the agent **at least one frame later** (the sweep must free the panel and
   DisplayMainMenu's fresh menu must exist before the MenuLoadController path resolves it). The
   existing serial/phase machinery already provides the delay.

Back click: nothing — stock handler closes and rebuilds the menu.

## Crash-risk register

- **Row not cloned before AddControls** — zero extent/no font → OrganizeControls/Draw faults.
  Always `Initialize_2` first. (VERIFIED consumers.)
- **Rows on the wrong heap** — ClearItems frees rows with the engine's deleting dtor; managed or
  stack memory = heap corruption. Engine `operator_new 0x6FA7E6` only. (VERIFIED ownership.)
- **Stale row/entry pointers after any AddControls/ClearItems** — freed; re-read from
  `listbox+0x29C` if needed.
- **Zero saves on disk at construction** — panel born in delete state + modal "no saves" box
  (§1). Guarantee one save dir, and check `panel+0x44` post-ctor.
- **Leaving `VerifyLoadGame` on BTN_SAVELOAD** — a click with a selected row starts a real save
  load (`LoadGame 0x6CB0E0`) against co-op row garbage. The rebind (replace) removes it; verify
  the replace took by not seeing loads. Row-level stock handlers die with the rows — no risk.
- **Touching the menu while the panel is open** — `[clientInternal+0x280]` dangles (§2). Gate
  every menu resolve on `PanelExists 0x40BD70`.
- **Firing the load with the panel still registered** — the Handoff world-up-crash suspect.
  Close, wait a frame, then load.
- **Double-marking `0x400` after the sweep already freed the panel** — keep exactly one owner of
  the close action; null the plugin's panel pointer the moment 0x400 is set.
- **CExoString args** — build with `CStrConstructor 0x5E5A90`, keep alive across the call,
  destruct with `0x5E5C20` (SetText copies; the argument may then be destroyed). (Retail order.)

## What still needs a live pass (NEEDS-LIVE)

1. The full repurpose sequence end-to-end (each individual ABI is verified; the composition — our
   thiscall marshaling for `Initialize_2`/`SetText`/`AddControls` in one pass — is not yet run).
2. Rescuing (rather than avoiding) the zero-saves birth state.
3. Re-adding a kept-alive panel (only if the construct-per-open pattern is ever abandoned).
4. Visual check that proto-cloned rows with our captions render with the stock font/colors (the
   clone copies proto params — expected identical to save rows).

## Key addresses (new in this doc; see server-browser.md for the listbox field map)

`CSWGuiSaveLoad::Constructor 0x006CC680` (thiscall, ret 0xC, `(gm, bSaveMode, bFromMainMenu)`;
calls PopulateGameList itself) · `Destructor 0x6CA110` / deleting `Destructor_2 0x6CB0C0` ·
`HandleInputEvent 0x6C86D0` (0x28/0x2D/0x2E = close; main-menu branch calls DisplayMainMenu) ·
panel title textparams `+0x15C` · btn textparams `+0xD80/+0xF44/+0x1108` · delete-btn flags
`+0xFE0` (bit1 = visible+hittable; Draw `0x40B841`, HitCheck `0x4187C5`) · details-pane textparams
`+0x3DC/+0x51C/+0x29C`, images `label+0x70` via `SetFillImage 0x414C00`.
Rows: `CSWGuiSaveLoadEntry::Constructor 0x6CB940` (no args) · clone `[vtbl+0x98]` =
`CSWGuiButton::Initialize_2 0x00418D80(rect, proto)` ret 8 → `Initialize 0x417970` ret 0x10 ·
row rect `{0,0,0,0x2A}` · proto = `[panel+0xC00]` · caption `SetText 0x415E00(entry+0x16C, s)` ·
`AddControls 0x41C1D0(&{data,count,cap}, 1, 0, 0)` ret 0x10 (owns rows; calls ClearItems; needs
each row's extent) · `ClearItems 0x419A90` (frees owned rows; `+0x2C6=0xFFFF`) · listbox dtor
`0x41B060` (frees proto + rows).
Events: `AddEvent 0x41AB20` entries `{owner,fn,id}@control+0x38`, same-id replaces · dispatch
`0x418750` (sets `+0x48/+0x4C`, calls `fn(owner, firingControl)`) · selection-before-click chain
`0x41C4A0` (down: `SetSelectedControl 0x41C040`, fires evt 1/0) → `0x41A700` (up: listbox evt
0x27) → `0x41CE20` default `0x41D2B2` (forwards to selected row) · stock row binds
0x27→`VerifyLoadGame 0x6CC0E0`, 0x29→`DeleteGame 0x6CAA90`, 0x00→`ShowGame 0x6C89D0`.
Show/close: `OnLoadSaveGame 0x0067B1A0` · `AddPanel 0x40BC70(gm, panel, flags2, bSound)`
(flags2=2 → `+0x44` bit3, no modal push; bit0 → PushModalPanel) · `PanelExists 0x40BD70` ·
`RemovePanel 0x40C830` (never frees) · **manager sweep in `Update 0x40CE70` @`0x40CF09`**:
`+0x44` state `(f>>9)&3`: 2 → RemovePanel+`[vtbl+0](1)` delete; 1/3 → RemovePanel keep; `0x100`
protected · `DisplayMainMenu 0x5ED420`→`0x5FCA30` (new menu `0x1414`, ctor `0x67C4C0`, stored
`[clientInternal+0x280]`) · listbox `SetSelectable` vtbl+0xA4 `0x41B120` (`+0x2BC` bit0).

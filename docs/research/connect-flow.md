# In-game Connect flow — feasible (research 2026-07-31)

Verdict: a real "click Co-op → panel appears → type the host IP → connect" flow is
achievable using only proven engine machinery. KOTOR keeps a working editable text control
(`CSWGuiEditbox`) and a reusable modal text-entry popup (`CSWGuiSaveNamePanel`,
`savename.gui`). The dead multiplayer GUI is fully stripped — nothing MP-specific survives.
Password masking does not exist, so the password stays in a config file and only the IP is
typed in-game.

## Menu button rebind

`CSWGuiControl::AddEvent 0x0041AB20` — `__thiscall void(this=control, int eventId, void*
owner, void* fn)`, ret 0xC. Same-eventId call REPLACES; null fn removes. Verified at the
main-menu constructor call sites (e.g. 0x0067C699): push order is (eventId, owner, fn).
Event 0x27 = click, 0x2D = A/Enter.

Callback the engine invokes (via `HandleInputEvent 0x00418750`): `__thiscall void(owner
/*ecx*/, CSWGuiControl* firingControl /*stack*/)`, ret 4. The engine never dereferences
`owner` — pass any non-null plugin context.

Design: rebind the dormant **BTN_WARP** (`mainMenu+0x1248`, TEXT.STRREF already blank):
`AddEvent(warpBtn, 0x27, ctx, OnCoopClicked)` + same for 0x2D. Optional merged
`Override\mainmenu.gui` sets that control's inline TEXT (STRREF 0xFFFFFFFF) to "Co-op" —
cosmetic; must work on the stock menu without it.

## Real editable text control

`CSWGuiEditbox` vtable 0x0073EAC8, size 0x160. `edit_text` (CSWGuiEditText) at +0xE0; its
`string` CExoString at +0x78. **So the typed string is a CExoString at editbox+0xE0+0x78 =
editbox+0x158.** Read `{char* @0, int len @4}` directly.

Keystroke capture is AUTOMATIC once the editbox has focus: app handler 0x004024CE →
`CSWGuiManager::HandleKeyPress 0x0040B2A0` → focused control (`[guiManager 0x007A39F4 +
0x18]`) vtable +0x9C = `CSWGuiEditbox::HandleKeyPress 0x004184F0`, which appends printables
via `AddNewChar 0x004163A0` and backspaces via `RemoveLastChar 0x004162E0`. `SetText
0x00416490` prefills.

⚠ No password masking anywhere (format constant 0x0073E3EC is "%s_" — text + caret). A
typed password shows cleartext. Keep the password in `coop.ini`; type only the IP.

## Reusable modal: CSWGuiSaveNamePanel

Vtable 0x007576D0, size 0x690, asset `savename.gui` (BTN_OK id0, BTN_CANCEL id1, EDITBOX
id2, LBL_TITLE id3). One of only two editbox instances in the game.
- BTN_OK control at +0x68 (or GetControl(0) via `CSWGuiPanel::GetControl 0x0040BA00`)
- BTN_CANCEL at +0x22C
- EDITBOX at +0x3F0; its typed CExoString at **panel+0x548** (0x3F0+0xE0+0x78)
- owner sink at +0x64

Show sequence (from CSWGuiSaveLoad 0x006CB8BC): `operator_new(0x690) 0x006FA7E6` →
`Ctor 0x006CAE70(ctxArg)` → `SetText 0x00416490` on panel+0x4D0 (prompt) → set panel+0x64 →
`AddPanel 0x0040BC70(panel,1,1)` modal (or `PushModalPanel 0x0040BD90`). Focus auto-set in
`OnPanelAdded 0x006C8410`. Dismiss: `PopModalPanel 0x0040BE00` + `RemovePanel 0x0040C830`.

⚠ **Must intercept OK.** The stock `HandleOkButton 0x006C9D50` reads panel+0x548 then calls
`CSWGuiSaveLoad::WriteGame 0x006C8790` on [panel+0x64] — it tries to SAVE THE GAME and
crashes if +0x64 isn't a real CSWGuiSaveLoad. So rebind BTN_OK/BTN_CANCEL to plugin stubs
(same AddEvent 0x27/0x2D) before showing.

## Recommended flow
1. Plugin rebinds BTN_WARP → "Co-op".
2. OnCoopClicked builds a SaveNamePanel, SetText prompt "Host IP:" (prefill last IP),
   rebinds its OK/Cancel to stubs, PushModalPanel.
3. Engine feeds typing into panel+0x548 automatically.
4. On OK, stub reads panel+0x548 → IP, dismiss, start the session (password from coop.ini).

Fallback (zero risk): BTN_WARP → immediate connect from coop.ini (no typing); optionally a
non-editable info panel saying "edit coop.ini then press Co-op".

## Key addresses
AddEvent 0x0041AB20 · HandleInputEvent 0x00418750 · HandleKeyPress 0x0040B2A0 ·
Editbox::HandleKeyPress 0x004184F0 · AddNewChar/RemoveLastChar/SetText
0x004163A0/0x004162E0/0x00416490 · SaveNamePanel Ctor/vtable/size 0x006CAE70/0x007576D0/0x690 ·
operator_new 0x006FA7E6 · AddPanel/PushModal/PopModal/RemovePanel
0x0040BC70/0x0040BD90/0x0040BE00/0x0040C830 · GetControl 0x0040BA00 · guiManager 0x007A39F4
(focused +0x18) · mainMenu at clientInternal+0x280, BTN_WARP +0x1248 · typed string
panel+0x548 · ⚠ stock OK 0x006C9D50 → WriteGame 0x006C8790.

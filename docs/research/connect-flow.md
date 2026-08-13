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

---

# Password prompt (implemented 2026-08-13)

## What was broken

Pressing **Connect** on a password-protected server in the in-game browser did two wrong
things at once, both confirmed live:

1. **Nothing ever asked for a password.** `SharedBlockView.RequestBrowserPassword()` existed
   with zero callers on either side, and it pointed the wrong way (plugin → agent). Only the
   AGENT learns a password is needed — the host is what rejects — and only the PLUGIN can take
   a keystroke from a player looking at the main menu. The field is now agent → plugin.
2. **A refused join loaded the world anyway.** The auto-load fired the moment the menu handed
   over a choice, in parallel with a handshake that had not happened. Measured: client logged
   `rejected by host: E_PASSWORD: wrong password`, host stayed at `peers 0`, and the game
   loaded END_M01AA regardless — the player stands in single-player believing they are in
   co-op, with the only evidence in a console window behind the game.

A third, quieter defect fell out of the same area: the browser's Connect went straight to
`SessionManager`, bypassing `CoopCommandDispatcher` — the only place that records what a join
IS (endpoint, password) and therefore the only place that could retry one.

## The prompt surface: the developer console, not a GUI panel

The `CSWGuiSaveNamePanel` route documented above was NOT taken. The console is already a
working, engine-owned text field with a caret, backspace and a keyboard grab the rest of the
game respects; it already renders at the main menu, which is exactly where this prompt
happens; and its submit path is already wired to the agent through `FuncInterpHook`. A GUI
panel would be a second implementation of all of that, for the one screen where the first one
is known to work — and it carries the `HandleOkButton` → `WriteGame` hazard above.

So `Odyssey.OpenChatInput()` was generalised to `OpenConsoleWith(prefill)` (it was already
force-opening the console pre-filled with `say ` for Enter-to-chat), and the prompt is the
same move with `password `. It returns false when the console is already open, so a prompt
can never stamp over a line the player is half way through typing.

## The wiring

- `SharedBlock.OffsetBrowserPasswordSerial` (0xE14) — **agent → plugin**, bumped to ask.
  No layout change, so no block version bump: nothing had ever read or written it.
- `KotorGameBridge.RequestPasswordPrompt()` bumps it, seeding from the block so a reattached
  agent does not re-ask on its first request.
- `PasswordPromptController` (plugin) runs on **MainLoop**, not the swap hook — the ask lands
  at the main menu, where SwapBuffers is dormant. It holds the request while
  `NativeBrowserController.IsOpen` (the panel owns the keyboard) and retries each frame while
  the console is busy.
- `password <pw>` / `pw <pw>` is a new console verb (added to `FuncInterpHook.Verbs` and
  `ConsoleCommand.Parse`). It carries **only the secret** — no address — because the player
  picked a row in a panel and never saw an IP. The whole rest of the line is the password, so
  a passphrase with spaces survives.
- `CoopCommandDispatcher.Dial()` is now the single place a join starts. It records the
  endpoint for a retry, and refuses to dial a beacon-locked server with no password in hand —
  prompting instead, because that attempt can only end in E_PASSWORD.
- `MenuAutoLoad.RunAsync()` gates the world load on `SessionPhase.Connected`, with **no
  deadline**, so answering a prompt on the second or third attempt still lands in the world.
  A host loads immediately: it has nobody to be admitted by.
- coop.ini's password now folds into the browser path too (it only reached `ApplyConfig`
  before), so a configured password is TRIED and the prompt is what happens when there isn't
  one or it is wrong — which is the actual contract.

## Verified live (GOG host + instance-2 joiner, host `--password hoth`)

| case | result |
| --- | --- |
| locked row, no password held | prompt opens (`> password _`), world does NOT load |
| typed password correct | `peers 1`, world loads |
| coop.ini password wrong | `E_PASSWORD` → `auto-load held: the join was refused; staying at the main menu.` → prompt |
| answering the retry prompt | `retrying 127.0.0.1:27140...` → `connected ... as peer 2` → `auto-load: loaded through the plugin` |

⚠ The prompt can be opened while the intro logo movies are still playing (MainLoop runs
there). That is harmless — it survives to the menu with the caret intact, which is what the
screenshots show — but keystrokes sent during the movies go to the movie, not the console.

⚠ Still no masking: the typed password is cleartext on screen, as the format constant above
dictates.

## Trap: a dead command tick takes the whole console with it

`CoopCommandDispatcher.RunAsync` caught only `OperationCanceledException`, so one throw
anywhere in a tick unwound the loop and the ~ console, the menu button and the browser all
went quiet for the rest of the session — with nothing on screen to say why, because the thing
that reports problems is what died. The tick body is now individually guarded.

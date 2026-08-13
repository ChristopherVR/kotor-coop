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

## Native panel upgrade (implemented 2026-08-13, NEEDS-LIVE)

The console prompt above is now the FALLBACK; the primary surface is the researched
`CSWGuiSaveNamePanel`, built by `PasswordPromptController`:

- **Show**: `operator_new(0x690)` → `Ctor 0x006CAE70(guiManager)` (the arg follows the
  pattern every panel ctor takes; vtable `0x007576D0` verified after, mismatch latches) →
  rebind BTN_OK (`+0x68`) and BTN_CANCEL (`+0x22C`) to plugin stubs via AddEvent 0x27+0x2D
  **before showing** (the stock OK's WriteGame trap above) → `SetText 0x00416490` on
  `panel+0x4D0` with an EMPTY string → `AddPanel 0x0040BC70(gm, panel, 1, 1)` (flags2 bit0
  = PushModalPanel). The MENU IS NOT MARKED `0x400`: marking it is an explicit act of
  OnLoadSaveGame (`0x67B22C`), not a side effect of AddPanel, and the stock flow floats
  this very popup over a live panel — so the menu stays valid and the other menu
  controllers keep running.
- **`panel+0x4D0` is the EDIT CONTENT, not a caption**: it is EDITBOX(`+0x3F0`) +
  edit_text(`+0xE0`), whose string is `+0x78` = the typed `+0x548` — the doc's own
  arithmetic. Stamping prompt text there would come straight back as the "password" on a
  bare OK, so the box is cleared instead and the title stays savename.gui's stock
  LBL_TITLE strref (its params offset is unresearched; do not invent it).
- **OK**: read the CExoString at `+0x548` (cut at the FIRST NULL — the length field
  includes the terminator), compose `password <text>` (`PasswordAnswer.Compose`, unit
  tested) and publish it through the existing console channel
  (`SharedBlockView.PublishConsoleCommand`) — but only AFTER the sweep confirms the panel
  freed (`PanelExists 0x0040BD70`), the browser's own teardown-race discipline. Zero new
  block fields; the agent's `password` verb path is unchanged.
- **Dismiss**: mark `panel+0x44 = (f & ~0x300) | 0x400` and let the manager sweep pop the
  modal stack, unregister and free it (RemovePanel pops the modal top; freeing from inside
  the click dispatch would be a use-after-free). Empty OK = cancel. A re-bumped serial
  reopens a fresh panel (construct-per-open, the retail pattern).
- Guard refusals latch the native path off (`_broken`) and every ask falls back to
  `OpenConsoleWith("password ")`; in-world asks use the console directly (no menu exists).

NEEDS-LIVE: the ctor argument, the modal-over-menu show, Enter-in-editbox routing to the
rebound OK, Escape behaviour, and what the stock LBL_TITLE reads.

## Trap: a dead command tick takes the whole console with it

`CoopCommandDispatcher.RunAsync` caught only `OperationCanceledException`, so one throw
anywhere in a tick unwound the loop and the ~ console, the menu button and the browser all
went quiet for the rest of the session — with nothing on screen to say why, because the thing
that reports problems is what died. The tick body is now individually guarded.

## Live verification of the native panel (2026-08-13, second pass)

The full product flow was run cold — plain `Start-Process swkotor.exe`, no scripts, hidden
auto-started agents on both sides:

| step | result |
| --- | --- |
| Cold launch, no `menu_button` key | Co-op button present (default-on verified) |
| Co-op click → browser → Connect on the locked row | **native SaveNamePanel opens** over the menu, editbox focused, caret live |
| Type `hoth`, click OK | **no save attempt** (the WriteGame rebind held), answer routed as `password hoth`, host admitted the client (`peers 1`), world auto-loaded |
| Console in-world | `where` and `who` reply; Up recalls history; `wa`+Tab completes to `warp` with the usage hint line drawn below the input |

Known gaps, deliberate or observed:
- The panel title still reads "Enter name for saved game:" (stock LBL_TITLE strref; its
  params offset is unresearched and was not guessed).
- The panel is swept by a focus loss (alt-tab while it is up dismisses it cleanly —
  "password prompt: dismissed", no crash). The player re-presses Connect; the ask does not
  auto-re-fire after an unanswered dismissal.
- The auto-started agent hosts on the CLI default port (27015); coop.ini `port=` is not
  folded into the hosting path. Harmless on LAN (the beacon carries the real port) but a
  direct-IP joiner typing the ini port would miss.
- ⚠ Injected-input trap for test scripts: an arrow key sent WITHOUT
  KEYEVENTF_EXTENDEDKEY is NUMPAD-8 and types '8' into the console; tools/sendkey.ps1 now
  sends 0xE0-prefixed scans for the arrow cluster.

## Prompt in place (implemented 2026-08-13, NEEDS-LIVE)

What was wrong with the verified flow above, as the player experiences it: Connect on a
locked row **closed the browser**, dumped them at the main menu, and only then floated the
password modal over the bare menu — a visual round-trip through a screen they never asked
for, because the ask travelled plugin → agent → plugin (close, connect serial, `Dial` sees
locked-with-nothing, bumps 0xE14, prompt controller reopens a panel over the rebuilt menu).

Now the ask happens BEFORE the browser closes, pushed over the browser itself — the retail
pattern exactly (the stock save screen pushes CSWGuiSaveNamePanel over ITSELF; show sequence
from 0x006CB8BC above). Our browser IS a CSWGuiSaveLoad, so this is the engine's own flow.

- The modal machinery moved into **`PasswordEntryModal`** (one class owning
  build/rebind/show/read/dismiss — everything in "Native panel upgrade" above, unchanged at
  the engine level), with two callers:
  1. **`NativeBrowserController`** — Connect on a locked row with no agent-held password
     records the pick and sets a flag; the NEXT plugin frame floats the modal over the
     browser using the browser's own gui manager (the main-menu object is freed while the
     browser is up and must not be resolved). Construction stays on the frame hook, never
     inside the engine's event dispatch. OK → the freed-panel poll publishes
     `password <typed>` through the console channel FIRST, then the normal
     close-and-connect handoff (selection, then the connect serial after the browser
     sweep) — so the agent holds the secret before the join starts. Cancel/empty-OK/alt-tab
     → the modal alone is swept; the player stays in the browser, row still selected.
  2. **`PasswordPromptController`** — the 0xE14 serial path, kept intact as the
     WRONG-PASSWORD RETRY: it fires after the browser is long closed, so its modal floats
     over the main menu (correct there). Same latch, same console fallback, same
     adopt-serial-at-construction.
- **`BrowserEntry+0x3B` = PasswordHeld** (agent → plugin, previously unused padding inside
  the 0x40 stride — verified nothing ever read or wrote it, so no signature bump). The
  agent stamps every row with "I already hold a password for the next join" (coop.ini,
  --password, or an answered prompt); the plugin prompts only on locked-and-NOT-held, so a
  configured player is never asked. The flag folds into the publish signature so gaining or
  losing a password rewrites the rows.
- **Agent: an unsolicited `password <pw>` is an offer, not an error.** With the new
  ordering the line arrives before any join is pending;
  `CoopCommandDispatcher.AnswerPasswordPrompt` stores it in `_password` and replies
  "password saved for the next join"; both `JoinListed` and a direct `connect` fold it in.
  The retry behaviour when an endpoint IS pending is unchanged (unit tested both ways in
  PasswordPromptTests).
- Every fallback keeps the OLD live-verified flow: a latched or failed modal open turns the
  click into the close-then-connect handoff, where the agent's serial prompt (or its
  console fallback) still asks — nothing can strand the player's click.

NEEDS-LIVE checklist (GOG host `--password hoth` + instance-2 joiner):

| # | step | expected |
| --- | --- | --- |
| 1 | Co-op → browser → Connect (or Enter) on the locked row, joiner holds no password | modal opens IN PLACE: the browser stays visible beneath it (title "CO-OP SERVERS", rows, details pane), NO main-menu flash |
| 2 | press Cancel | modal closes, browser still up, same row highlighted; nothing dialled (agent logs nothing) |
| 3 | Connect again, OK on an EMPTY box | same as Cancel — browser stays, nothing dialled |
| 4 | Connect again, type `hoth`, OK | browser closes once, `password saved for the next join` then `connecting to ...` in the agent, `peers 1`, world loads — menu appears only during the normal close-to-load handoff |
| 5 | joiner restarts with `--password hoth` (or coop.ini password), Connect on the locked row | NO prompt at all: dials straight away (held flag) |
| 6 | joiner configured with a WRONG password, Connect | dials, `E_PASSWORD`, auto-load held, retry modal opens over the MAIN MENU (acceptable — the browser is long closed), answering `hoth` connects |
| 7 | while the in-place modal is up: click browser rows/buttons beneath | dead (modal owns input); Escape/alt-tab behaviour = dismissed → case 2 |
| 8 | unlocked row, Connect | unchanged old flow, no prompt |

## LBL_TITLE researched (2026-08-13): the title is now captioned

The fourth embedded control of CSWGuiSaveNamePanel is LBL_TITLE at **panel+0x550**
(ctor 0x006CAEDE: `lea ecx, [esi+0x550]` → CSWGuiLabel::Constructor 0x0041ACD0; registered
against the "LBL_TITLE" literal at 0x006CAFA4..0x006CAFC7; 0x550 + sizeof(CSWGuiLabel)=0x140
= the 0x690 panel size). Its CSWGuiText is at label+0xD0 (panel+0x620) and the
CSWGuiTextParams at text+0x18 → **panel+0x638** — the same label+0xE8 shape as the save/load
title already shipping at +0x15C. Caption with **CSWGuiTextParams::SetText 0x00415E00**
(copies the string, sets str_ref = -1, re-wraps); 0x00416490 is CSWGuiEditText::SetText —
the editbox setter behind the +0x4D0 trap, never the label.

The stock "Enter name for saved game:" is a strref baked into savename.gui, resolved ONCE in
the ctor's Load pass (SetStrRef 0x415E50 from CSWGuiText::Load 0x00416050); exhaustive xref
scans of 0x415E00/0x415E50 found nothing that re-stamps it, and the per-frame draw path
(0x00416240) renders the cached string without touching str_ref — one SetText after the ctor
sticks. PasswordEntryModal writes "Enter the server password" guarded by two invariants
(label vtable 0x0073E5B8 at +0x550; params+0x50 back-pointer == panel+0x620), and on a
mismatch SKIPS the caption rather than latching the modal: a shifted label is a cosmetic
problem, a wrong-offset write is not.

## Live verification, third pass (2026-08-13): all three user reports fixed

Cold product flow again (no scripts, hidden auto-agents), after merging the in-place
prompt, the title caption, the journal idempotence fix, and the dispatcher unification:

| check | result |
| --- | --- |
| Host via `host` typed in the ~ console AT THE MENU | hosted + auto-loaded (the unified dispatcher's menu-time verbs, live) |
| Connect on the locked row | modal floats OVER the browser — row, details and portraits visible beneath; no menu round-trip |
| Modal title | **"Enter the server password"** (panel+0x638 write held; guards passed) |
| Cancel | back in the browser, row still selected, nothing dialled |
| Type password → OK | connected as peer 2, world loaded, no menu flash |
| Journal popup | **zero "Journal Entry Added" boxes on either side**, host and joiner |
| Peers | 1/1 both sides, END_M01AA, each sees the other's character |

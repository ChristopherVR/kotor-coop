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

## Seamless arrival + join/leave announcements (2026-08-13, live-verified)

Two user reports: the client loads in and THEN co-op "connects" around them, and the host
gets no on-screen word when a player joins.

**The pop-in's root cause was an innocent-looking cache.** `EnumerateCreatures` cached its
scan for 15 s regardless of content. The agent attaches at the menu, scans, finds ZERO
creatures (no world yet), caches the emptiness — and then every resolve attempt during the
save load returns that stale nothing until the cache happens to expire. Measured live: the
identical flow resolved 1.7 s after world-ready on one run and 15.7 s on the next, purely on
expiry luck. Empty results now age out in 750 ms (`EmptyCreatureCacheMs`); "no creatures"
means "no world yet", a state that flips exactly once.

**The seamless gate** (MenuLoadController.TryPresentWorld): a ready world is held behind the
load screen while the session has admitted peers and no phantom exists yet — released the
frame the phantom spawns, released early when the agent has resolved and filtered everyone
out (peer in another module), released at a 4 s deadline regardless. The signal is a new
agent-written field `OffsetSessionPeers` (0xE1C, written every dispatcher tick) — NOT
RemoteCount, which depends on the 1 Hz player resolve and arrives too late by definition.
With the cache fix the gate rarely needs to hold at all: final run's timeline —
`18:58:43.259 waiting for the client world` → `18:58:45.462 spawned phantom` (under the
load screen) → `18:58:45.880 presentation handed over`. The player arrives with their
partner already standing there.

Two supporting facts established: the SwapBuffers hook DOES fire under the load screen (the
world pipeline runs there; only movies gate it), and a stall-detecting fallback in
PluginHost.MenuFrame drives the pipeline at ~10 Hz anyway if that ever stops being true.

**Join/leave announcements** (`PeerAnnouncer` + dispatcher tick): the admitted roster is
diffed every tick and changes post through the standard reply trio — "Malak joined the
game" (host log + overlay, live) and "Malak left the game" (screenshot, on-screen, with the
phantom despawning alongside). Keyed by peer id, not name; `password` prompt refusals never
announce (they are never admitted).

**Agents die with their game now.** Killing the joiner's game left its hidden auto-started
agent connected — the host kept a live "peer" whose player no longer existed, frozen phantom
and all, exactly as the process-model review predicted. The agent now watches its game pid
and cancels itself on exit, so the Bye goes out and the host announces the leave within a
tick. (The live "left" proof above was produced by stopping the zombie by hand; the watch
ships with this change.)

## Liveness: no silent failures (implemented 2026-08-21, NEEDS-LIVE)

Four gaps shared one root cause: a co-op failure with nothing on screen to say so. A hung
or crashed agent left phantoms frozen with no explanation; a failed auto-start was a log
line nobody reads; a busy port threw an unlabelled exception; a dead host left the client
in "connecting..." forever. All four now report through the SAME existing surfaces —
`Odyssey.PostTextLine` (the queued-text path every overlay already uses, see
docs/research/text-render-queued.md) on the plugin side, and the dispatcher's `Reply()`
trio (stdout, console line, chat overlay) on the agent side. No new render surface.

### (a) Agent heartbeat

`SharedBlock.OffsetAgentHeartbeat` (0x3780, 8 bytes) — **agent → plugin**, new field,
**block version bumped 38 → 39** (`PluginHost._block->Version` and
`KotorGameBridge.PluginSignature` together, as the existing discipline requires). It sits
in the free tail after the browser-details array (`0x3400 + 8*0x70 = 0x3780`;
`TotalSize` is `0x3800`, so this is the last 8 bytes in the block) — nothing before this
change ever read or wrote it, but a genuinely new cross-process contract (not previously
padding either side silently ignored) earned the bump rather than reusing the
`BrowserEntryPasswordHeld`/`OffsetSessionPeers` "verified-dead-padding" exception.

The value is `Environment.TickCount64` (Windows' system-uptime counter), not a sequence
number: both processes read the *same* OS clock independently, so the plugin computes
staleness as a plain `now - lastBeat` with no handshake and no risk of two Stopwatches
drifting apart. 0 means "no agent has ever pumped this block" and doubles as the sentinel
for "nothing to warn about yet" — a real `TickCount64` stamp essentially never collides
with it. `CoopCommandDispatcher.RunAsync` writes it every tick (~150 ms), unconditionally,
whether or not a session exists — "the agent is pumping at all", not "connected".

`AgentLiveness.Evaluate(lastBeat, now)` (dependency-free, unit-tested,
`tests/KotorCoop.Tests/AgentLivenessTests.cs`, compiled into the test project the same way
`DownBlock`/`PasswordAnswer` are) classifies NeverLive / Live / Stale (≥5 s,
`StaleAfterMs`) / Dead (≥15 s, `DeadAfterMs`). `AgentLivenessController`, run every frame
from `PluginHost.MenuFrame` (menu **and** in-world — the SwapBuffers hook is dormant at
the menu, same reason `ConsoleAssistController` lives there):

- Stale or Dead → posts *"co-op agent stopped responding - phantoms frozen; type
  reconnect"* every frame (re-posted per the queued-text discipline, not a one-shot toast)
  at grid cell (3, 5) — clear of the console rows (0-2), `ConsoleAssistController`'s hint
  row (3), and `ChatOverlayController`'s shallowest anchor row (11) — and sets
  `AgentLivenessController.RemoteWritesFrozen = true`.
- `PhantomController.OnFrame` checks that flag right after its existing player/command
  gates and returns before touching a single remote slot: no spawn, no destroy, no drive.
  Existing phantoms simply stand where they last were — genuinely frozen, not "driven off
  stale data every frame".
- Live → clears the flag and stops drawing; recovery is automatic the moment a re-attached
  (or relaunched) agent's heartbeat starts advancing again, no separate "reconnect" logic
  needed on the plugin side.
- Every *transition* (not every frame) is logged once via `PluginHost.Log`.

### AgentLauncher: startup failure, and one relaunch

`AgentLauncher.Launch` (the two prior failure branches — no exe found, `CreateProcessW`
failed — refactored out of `EnsureRunning` so a relaunch runs the identical logic) now
also sets `AgentLauncher.StartupFailure`, a static string `AgentLivenessController` reads
and shows as *"co-op agent did not start: &lt;reason&gt;"* whenever the block is still
`NeverLive` — cleared the instant any launch (first or relaunch) actually succeeds, so a
stale banner cannot outlive the problem it described. An opted-out auto-start
(`auto_start_agent = 0`) is deliberately NOT a failure and never sets this.

`AgentLauncher.TryRelaunch(lastHeartbeatMs, nowMs)`, called every frame from
`AgentLivenessController` once the state reads Dead: relaunches through `Launch` at most
once a minute (`AgentLiveness.RelaunchCooldownMs`), and only for a process this launcher
itself started (`_startedGameDirectory`, set only on a real `CreateProcessW` success) — an
agent the player started by hand, or one whose auto-start coop.ini opted out, is never
touched. Logs only the ticks it actually attempts something on.

### (c) Port already in use

`UdpChannel`'s constructor now catches `SocketException` with
`SocketErrorCode == AddressAlreadyInUse` around its one `Bind` call and rethrows the typed
`KotorCoop.Session.PortInUseException(port, inner)` (disposing the half-built socket
first). `SessionManager.Connect` wraps `new CoopHost`/`new CoopClient` in a
try/catch: `PortInUseException` → *"port &lt;n&gt; is already in use - is another co-op
agent already running?"*; any other `SocketException` → a generic-but-labelled line. Both
land on **`SessionPhase.Idle`**, not `Failed` — nothing here ever half-started (the socket
that would have backed the session never came up), so there is nothing for `Disconnect` to
unwind, and Idle keeps this out of the same bucket as a wrong-password refusal.

**Regression caught while wiring this up, fixed in the same change:**
`CoopCommandDispatcher.FireMenuAutoLoadIfArmed` — called unconditionally right after
`Dispatch(Host)` returns — used to rely on the OLD behaviour where a bind failure threw
all the way out of `Dispatch` and never reached it. Now that `Connect` fails gracefully,
that call site would have auto-loaded a save into a game with **no session at all**. Fixed
by gating it on `manager.Current is not null` in addition to the existing
"not already in a world" check. Pinned by
`CoopCommandDispatcherTests.BareButtonPressHostingOnABusyPortNeverAutoLoads`.

### (d) Host unreachable (connect timeout)

`SessionSettings.ConnectTimeoutMs` (new, default `ProtocolConstants.DefaultConnectTimeoutMs`
= 15000; coop.ini `connect_timeout_ms`, threaded through `Program.cs`'s `SettingsFor`).
`SessionManager.Connect` arms a deadline (`SessionClock.NowMs + ConnectTimeoutMs`) only for
a **client** attempt — a host has nobody to be admitted by, so it never sets one.
`SessionManager.Poll` checks the deadline *before* reading `GetStats()` (a host that never
answers Hello produces no status change at all for the existing logic to notice) and, on
expiry, tears the session down through the same path `Disconnect()` uses (factored out as
`TearDownSession()`) and lands on `SessionPhase.Idle` with *"no reply from
&lt;endpoint&gt;"* — deliberately **not** `Failed`: the wrong-password path
(`CoopCommandDispatcher.ReflectPhase`'s existing `Failed` case, `manager.Detail` containing
"password") is a rejection the host DID answer, immediately; this is total silence. Both
now report through a dedicated `case SessionPhase.Idle:` in `ReflectPhase` — its own
branch, not folded into `Failed`, so the password-prompt retry logic there can never
mis-fire for either.

### Unit tests

`AgentLivenessTests` (pure `Evaluate`/`ShouldRelaunch` — thresholds, boundary values,
recovery, the cooldown, the "0 = never attempted" sentinel). `UdpChannelTests` (bind
collision throws the typed exception with the right port; an ephemeral bind still works).
`SessionManagerTests` (+3: port-in-use → Idle with the right message; a silent host times
out to Idle; a real handshake beats a short timeout). `CoopCommandDispatcherTests` (+1: the
auto-load regression above). All of `PasswordPromptTests`/`PasswordReconnectTests` (the
wrong-password path this must not disturb) still pass unchanged.

### NEEDS-LIVE

None of this has been run against the real game yet — everything above is unit-tested and
both builds are clean, but nobody has watched the on-screen line actually appear, watched a
frozen phantom actually stay put, or watched a relaunch actually happen. Specifically
unverified:

- The warning line's placement (grid cell 3,5) doesn't overlap the console/hint/chat rows
  **in code**, but the exact text-grid geometry is viewport-derived (per
  docs/research/text-render-queued.md's own NEEDS-LIVE) and has not been calibrated live
  for this new row.
- That killing (or suspending) a live agent process actually leaves phantoms visibly frozen
  in place rather than despawned or misbehaving, and that the warning appears within ~5 s.
- That `AgentLauncher.TryRelaunch` actually relaunches a killed agent after 15 s and that
  the new process re-attaches to the SAME plugin block and resumes driving the same
  phantoms cleanly.
- That a real port-in-use collision (two game instances against one `coop.ini` port, or a
  zombie agent from a crashed session) produces the "already in use" line in-game rather
  than only in the agent's own log.
- That a genuinely unreachable host (firewalled port, wrong LAN address) produces the
  15 s timeout and the "no reply from" line in-game, and that the main menu is left in a
  state the player can immediately retry from (Co-op button / `connect`).
- Whether 5 s / 15 s / 60 s are the right thresholds in practice — they are the task's
  specified defaults, not measured against a real network hiccup.
## A persistent connection indicator (2026-08-21, NOT live-verified)

Item 18, roadmap-seamless.md: before this pass the ONLY way to see connection state in-game
was typing `status` at the `~` console. Nothing was ever shown unasked, so a player who is not
actively typing has no idea their partner's link dropped until something else goes visibly
wrong — a phantom freezes, a door stops responding. `PeerAnnouncer` (above) already narrates
individual joins and leaves; nothing narrated the connection's own STATE — connected, how many
players, mid-outage and recovering, or gone.

**Surface chosen, and why not the others.** Read three candidates before picking: `PostTextLine`
(the minimap arrow hook and the console-assist hint line both draw this way, per-frame, from
inside the plugin), a new HUD widget, and the queued-text path `CompatibilityAnnouncer`
already uses for content-mismatch warnings (`Reply`, which reaches both the `~` console reply
line and the always-on chat overlay). The last one won on the same grounds
`CompatibilityAnnouncer` already established: reposted only on CHANGE rather than drawn every
frame, so an unobtrusive indicator costs nothing while the state is stable, and the surface
(the chat overlay) is already persistent and already configurable (`chat_overlay_anchor`,
`chat_overlay_opacity`) — nothing new to draw, position or theme.

**`KotorCoop.Agent.ConnectionStatusAnnouncer`** is `CompatibilityAnnouncer`'s and
`PeerAnnouncer`'s shape exactly: a pure static `Line(ConnectionSnapshot)` (state + peer count
→ the exact string shown — "co-op: connected, 2 players" / "co-op: reconnecting..." /
"co-op: connection failed" / "co-op: disconnected") separated from a thin stateful `Observe`
that reposts only when the snapshot actually differs from the last one seen — including a
peer-count change alone, with the state byte unmoved. Wired into
`CoopCommandDispatcher.AnnounceConnectionStatus`, called every dispatcher tick (150 ms)
alongside `AnnouncePeerChanges`/`AnnounceCompatibilityChanges`, gated by coop.ini's new
`status_toasts` (default on).

**The state that did not exist anywhere before this:** RECONNECTING mid-session. `ReflectPhase`
(existing, untouched) already announces the coarse `SessionManager.Phase` transitions —
Connecting → Connected, and Failed/Disconnected — but that phase is the MANAGED lifecycle and
stays `Connected` for the whole of a link outage described in disconnect-reconnect.md (a
blackout, a lossy link): nothing there ever told a player their partner's connection was
degraded. `AnnounceConnectionStatus` reads the LIVE `SessionStats.Connected` flag underneath
the phase instead, which is exactly what flips false the moment the client notices silence and
true again the moment it rejoins — so "co-op: reconnecting..." now brackets precisely the
window disconnect-reconnect.md's recovery-sequence table describes, and "co-op: connected, N
players" returns the instant it closes.

**The block field: widened, not added.** `SharedBlock.OffsetConnectStatus` (0x9EC) already
existed — the agent wrote it once, to `ConnectStatusConnecting`, on a menu Connect press, and
nothing on the plugin side ever read it back (confirmed: no consumer of
`SharedBlockView.ConnectStatus`'s getter anywhere in the plugin). Per the instruction to add no
new block field and not bump the block version (another agent was changing the block tail
concurrently), this pass repurposes it rather than adding beside it:
`ConnectionSnapshot.Pack()` puts the state in the LOW byte (reusing the four existing sentinels
0-3 unchanged, plus a new value 4 = Reconnecting) and the peer count, clamped to a byte, in the
next byte up. Every one of the four pre-existing sentinel values is entered with zero peers, so
the packed uint reproduces the exact bare value a hypothetical old reader would have compared
against — `PackReproducesTheOriginalFourSentinelsWhenAlone` pins this. The plugin does not
currently read the field for anything (this pass does not add a reader); it is carried so the
data is there, current, and correctly shaped for whichever surface reads it next, per the
instruction that it ride an existing field rather than nothing at all.

**Tests**, all pure — no session, no sockets, no clock:
`tests/KotorCoop.Tests/ConnectionStatusAnnouncerTests.cs` covers the "what changed → which
line" decision (`Line` for every state × peer count combination via `[Theory]`), the on-change
diffing (`Observe`: a stable snapshot announces once, a peer joining while connected is a
change even though the state byte does not move, a drop-and-recovery announces both edges,
`Reset` lets a fresh session announce its own state again), and the wire packing (state in the
low byte, peer count in the next, backward-reproduction of the four original sentinels, and
clamping an out-of-range count to a byte). `CoopConfigTests` covers `status_toasts` parsing and
its on-by-default rule, matching `menu_button` and `chat_overlay`.

**NEEDS-LIVE.** Nothing here was run against the game. In particular: whether the chat overlay
and `~` console reply line actually show the posted lines (the plugin-side rendering itself —
`ChatOverlayController`, `ConsoleAssistController`'s reply path — is untouched by this pass and
was already live-verified for other lines going through the exact same `Reply` call, but this
specific content has not been watched on screen); whether `netsim blackout` on the live rig now
shows "co-op: reconnecting..." during the outage and "co-op: connected, N players" the instant
it heals; and whether a second player joining an already-connected session visibly updates the
peer count on both screens without the player needing to type `who`.

**2026-08-27: the render side now exists too (NOT live-verified).** The paragraph above
("this pass does not add a reader") described the state through 2026-08-21; it no longer does.
`KotorCoop.Plugin.ConnectionIndicatorController` now reads `SharedBlockView.ConnectStatus`
every `MainLoopHook` tick (menu AND in-world, the same hook `AgentLivenessController` uses, for
the same reason — Connecting/Failed both happen before a session, and possibly before the
SwapBuffers hook has ever fired) and draws a PERSISTENT one-line summary at grid cell
(column 3, row 6) — directly under `AgentLivenessController`'s own warning line at row 5, and
clear of the console (rows 0-2), `ConsoleAssistController`'s hint (row 3), and
`ChatOverlayController`'s shallowest anchor (row 11) — through the same queued-text path
(`Odyssey.PostTextLine`, re-posted every frame; see text-render-queued.md). Unlike the toast
above, this line is CONTINUOUS while non-Idle rather than reposted only on change, so a player
who was not looking when the toast appeared still sees the current state. It shows nothing at
all while Idle (no session attempted).

The packed-uint decode is `KotorCoop.Plugin.ConnectionIndicatorRule.Text` — a second, by-value
duplicate of the same low-byte/next-byte layout `ConnectionSnapshot.Pack` writes (the plugin is
a dependency-free NativeAOT win-x86 DLL and cannot reference `KotorCoop.Agent`'s assembly, the
same reason `LivenessRule`/`AgentLiveness` duplicate their own Protocol/SharedBlock
counterparts rather than referencing them); `ConnectionIndicatorRuleTests` pins its wording
against `ConnectionStatusAnnouncer.Line` directly so the two surfaces cannot silently drift
apart. `ConnectionSnapshot` also gained an `Unpack` (the inverse of `Pack`, agent-side only —
the plugin has no use for it, since it decodes the wire bytes itself) with its own round-trip
tests in `ConnectionStatusAnnouncerTests`. New coop.ini key: `status_indicator` (plugin-side,
default on, independent of `status_toasts`) — `profiles/coop.ini.example` and
`docs/CONFIG.md` document it.

**NEEDS-LIVE (added by this pass):** whether the line actually renders at (3, 6) without
clipping or overlap on a real viewport; whether it visibly tracks a live `netsim blackout`
disconnect/recovery and a peer join/leave the same way the toast above needs verifying against;
and whether `status_indicator = 0` in `coop.ini` actually suppresses it (the read-once-at-
construction pattern is the same one `ConnectMenuController` uses for `menu_button`, which
already live-verified, but this specific key has not been toggled against a live game).

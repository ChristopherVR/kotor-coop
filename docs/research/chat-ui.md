# A better home for chat than the console (research 2026-09-25)

Question that started this: players currently see co-op chat two ways — a scrollback answer
typed into the `~` console (`docs/research/chat-input-scroll.md`), and the always-on,
fading overlay `ChatOverlayController` already draws through the engine's own queued-text path
(`docs/research/text-render-queued.md`). Is there a *better retail UI surface* for (1) showing
an incoming line, (2) a persistent inbox/history, and (3) typing one, that beats routing
everything through the console?

**Tooling note** (asked for explicitly): every address below was derived the way every prior
`docs/research/*.md` write-up derives one — `tools/q.py` (`class`/`offsets`/`func`/`sql`)
against `tools/kotor1_0_3.db`, the symbol database imported from another build and therefore
cross-checked wherever it matters (`tools/dbaudit.py`'s ret-immediate check, wired into `q.py
func`'s own `** DB says N bytes... TRUST THE BINARY` warning), plus `tools/disasm.py <addr>
<len>` for raw disassembly and `tools/xrefs.py <addr>` for call sites — all static, offline,
against the GOG `swkotor.exe` on disk. Nothing here launched the game or touched the shared
rig (per this task's own instruction).

## Answer up front

- **Persistent inbox: yes, a good one exists and was not previously used — the retail
  Messages screen (Escape → Messages) has a genuine dialogue-history tab, and the exact
  append call that feeds it is now verified (§2). A prototype ships this pass, OFF by default,
  behind `chat_messages_screen`.**
- **Showing an incoming line: keep the existing always-on overlay as the primary surface — it
  is already built, already live-tested for other content on the identical render path, and
  already configurable.** A bark bubble over the sender's phantom (§3) is a real, low-risk
  *addition*, not a replacement, using an already-shipped, already-default-ON function
  (`Odyssey.PlayBark`) — but wiring "whose bark bubble" needs a sender→phantom mapping the
  shared block does not carry today, which is new scope past what this pass's block-compatible
  budget covers. Documented as the natural next step, not prototyped this round.
- **Typing: keep Enter → console, prefilled with `say `.** A real retail editbox exists and is
  fully researched (`docs/research/connect-flow.md`), but every angle checked below argues
  against building a second one for chat specifically (§4).

## 1. What already ships, and why it is the right baseline

`ChatOverlayController` (`src/KotorCoop.Plugin/ChatOverlayController.cs`) is not new research —
it already draws incoming chat every frame through `Odyssey.PostTextLine`
(`docs/research/text-render-queued.md`'s `AurPostString 0x0044D490` enqueue, VERIFIED safe to
call from the `SwapBuffers` hook because it never touches the font). It already has scrollback
(Page Up/Down, `docs/research/chat-input-scroll.md`), a configurable anchor/opacity/fade
(`chat_overlay*` keys, `docs/CONFIG.md`), and the identical render path is independently
live-verified for other content (join/leave toasts, the connection indicator — both in
`docs/research/connect-flow.md`'s later sections). This is the strongest possible baseline: it
needed no new research to confirm, and nothing below argues for replacing it — only adding to
it.

`ChatInputController`/`Odyssey.OpenChatInput` (Enter opens the `~` console prefilled with
`say `) is the typing side, and is likewise already shipped and load-bearing — the gamepad's
on-screen character grid (`docs/research/gamepad.md` §7) already targets it as one of its four
text-entry boxes.

What was missing, and what this pass researched: a **persistent** place chat lives once it
scrolls off the overlay or the console history — something the retail GUI already has, that
nothing in this mod had pointed a write at yet.

## 2. The Messages screen IS the inbox — CGuiInGame has two separate ring buffers

`docs/research/dialogue-sync.md`/`Odyssey.Dialogue.Spectate.cs` already established that
`CGuiInGame+0xFC` is a 64-slot dialogue-history ring the client appends every displayed line
to, and reads it (read-only) to show a spectator the current line. What that research did not
chase: **what draws it**, and **whether the plugin can write to it itself.** Both are now
answered directly from the symbol database and fresh disassembly.

### 2.1 CGuiInGame owns two rings, not one

```
$ tools/q.py offsets CGuiInGame
  0x0F8  message_buffer        -- CGuiInGameMessage *
  0x0FC  dialog_messages       -- CGuiInGameDialogMessage *   (same slot as dialog_buffer)
  0x0FC  dialog_buffer         -- CGuiInGameDialogMessage *
  0x100  message_buffer_count  -- int
  0x104  dialog_buffer_count   -- int
```

- **`dialog_buffer` (+0xFC/+0x104)** — the one `Odyssey.Dialogue.Spectate.cs` already reads.
  Record type `CGuiInGameDialogMessage`, confirmed by the DB's own offsets:
  ```
  $ tools/q.py offsets CGuiInGameDialogMessage
    0x000  message   -- CExoString
    0x008  speaker   -- CExoString
  ```
  This is an exact, independent cross-check of the plugin's existing read-side comment
  (`{char* text; u32 len; char* speaker; u32 speakerLen}`) — the DB's class layout agrees with
  the reverse-engineered record shape byte for byte, from two unrelated sources.
- **`message_buffer` (+0xF8/+0x100)** — a SEPARATE ring, type `CGuiInGameMessage` (no DB
  offsets recorded, but disassembly of its own append function — below — shows a different
  shape: an 8-byte CExoString at +0, a 4-byte field at +8, and a single BYTE at +0xC, not a
  second CExoString). This is the **combat feedback** ring (`CSWSCreature::SendFeedbackMessage
  0x004EDE10`, `CTwoDimArrays::Load2DArrays_FeedBackText 0x005C4130` — the `feedbacktext.2da`
  driven "Miss", "Critical Hit", etc. lines), not dialogue.

### 2.2 The Messages screen panel: `CSWGuiInGameMessages`, and which listbox reads which ring

```
$ tools/q.py class InGameMessages
  CSWGuiInGameMessages   size=0xAF4   vtable=0x74FD18

$ tools/q.py offsets CSWGuiInGameMessages
  0x000  panel              -- CSWGuiPanel
  0x064  messages_listbox   -- CSWGuiListBox
  0x344  dialog_listbox     -- CSWGuiListBox
  0x624  messages_label     -- CSWGuiLabel
  0x76C  show_button        -- CSWGuiButton
  0x930  exit_button        -- CSWGuiButton
```

`CGuiInGame+0x1C = in_game_messages` (a `CSWGuiInGameMessages*`) — this class *is* the
pause-menu Messages screen. It owns two listboxes, and the function names make the pairing
unambiguous:

| Ring | Feeds | Populated by |
| --- | --- | --- |
| `dialog_buffer` (+0xFC) | `dialog_listbox` (+0x344) | `CSWGuiInGameMessages::ShowDialogMessages 0x624DF0` / `AddDialogMessages 0x626B10` |
| `message_buffer` (+0xF8) | `messages_listbox` (+0x64) | `CSWGuiInGameMessages::ShowFeedbackMessages 0x624F70` / `AddMessages 0x626920` |

So the retail **Messages screen has a Dialogue tab and a Feedback tab, backed by exactly the
two rings the task's own lead described** — not inferred, read straight off the class's own
member names and the panel's own function names in the symbol database. A chat line appended
to `dialog_buffer` is what a player sees on the Dialogue tab of a screen the game already ships,
reachable from the pause menu with no mod-drawn UI at all.

### 2.3 The append call, verified end to end

```
$ tools/q.py func AppendTo
  0x0062B680  CGuiInGame::AppendToDialogBuffer     [__thiscall]
  0x00630560  CGuiInGame::AppendToDialogBuffer_2   [__thiscall]
  0x0062B5C0  CGuiInGame::AppendToMsgBuffer        [__thiscall]
```

`tools/disasm.py 0x0062B680 0xA0` (full listing in the code comment of
`Odyssey.Dialogue.MessagesScreen.cs`) gives:

```c
// __thiscall, ret 8 (verified: the function's own `ret 8` — two stack dwords)
void CGuiInGame::AppendToDialogBuffer(CGuiInGame* this, CExoString* message, CExoString* speaker);
```

Reading it end to end:

1. **Guard** (`0x62B683`-`0x62B693`): `message` is compared against a global empty `CExoString`
   at `0x0073D71C` via `CExoString::operator!=_2` (`0x005E5390` — the DB's own name for that
   address, an independent confirmation this is exactly what it looks like). Equal (empty
   message) → the whole call is a no-op. A real chat line always passes.
2. **Ring shift** (`0x62B699`-`0x62B6E5`): if `dialog_buffer_count == 0x40` (64), every slot is
   copied one down from the next (`[i] = [i+1]`, oldest falls off the front) and the count is
   held at `0x3F`. **Confirms the read side's own comment about the 64-slot cap and
   shift-when-full behaviour, from the write side this time.**
3. **Append** (`0x62B6E6`-`0x62B71E`): `buffer[count].message = *message` and
   `buffer[count+0x8].speaker... ` — both via `CExoString::operator=_2 (0x005E5C50)`, a
   **copy-assignment**, not a pointer store — then `count++`.
4. **No allocation anywhere in the function** (no `operator_new` call in the body) — this
   writes into a fixed-size 64-record array `CGuiInGame` already owns; it never grows or moves
   anything. That, plus step 3's copy-assignment (rather than storing the caller's pointer), is
   the whole safety case: the call cannot outlive or alias anything the caller passed in.

This is the exact CExoString marshalling idiom `Odyssey.PlayBark` already uses for barks
(`docs/research/bark-replay.md`) and half a dozen other `Odyssey.*.cs` call sites (party
templates, save names, travel labels, movie names — `grep CExoStringCtorFn src/KotorCoop.Plugin`
turns up nine of them): build a temporary with the engine's own `CExoString(const char*)` ctor
(`0x005E5A90`, already declared as `CExoStringCtorFn`), call the engine function, free it with
the engine's own dtor (`0x005E5C20`, `CExoStringDtorFn`) — never touching the ctor/dtor
addresses themselves, since those were already researched and are already in production. This
call was NOT previously made by the plugin (only the read side existed); the two-argument
signature above, the empty-string guard, and the no-allocation ring-shift behaviour are new
findings from this session, cross-checked twice (DB class layout vs. disassembled field
offsets; DB function name vs. disassembled call target).

### 2.4 What it does NOT give — read this before treating it as a real inbox

- **Not cross-transition persistent.** `Odyssey.Dialogue.Spectate.cs`'s own `GetInGameGui`
  comment already establishes that the whole `CGuiInGame` chain is torn down and rebuilt on
  every module load. The ring lives inside that object, so a module change almost certainly
  empties it — this specific claim is UNVERIFIED live (nobody has watched the ring survive or
  not survive a transition), but it follows the same object lifetime every other
  transition-volatile `CGuiInGame` field in this codebase already has.
- **Not save-persistent.** `CGuiInGame` is GUI state, not game state serialized into a save —
  there is no reason to expect the ring round-trips a save/reload, and nothing was found that
  would flush or restore it. UNVERIFIED, same caveat.
- **64-line cap, oldest silently drops.** Fine for "what did I miss in the last while", not a
  full transcript.
- **No timestamps, no color, no separate tabs for chat vs. real dialogue** — a chat line and an
  NPC's actual line of dialogue land in the SAME ring, interleaved by arrival order. Cosmetic,
  not a safety issue, but worth setting expectations: this is "recent messages", not a
  dedicated chat log screen.

None of that makes it a bad choice — it is still strictly more than the overlay offers (it
survives the overlay's fade, in a real scrollable retail panel with a real title and a real
close button) — it is just not a permanent, cross-session inbox. Framed honestly to a player:
"chat you missed is still in Messages until you next change area."

## 3. Floating text over the sender — reuse, don't rebuild

The task's other lead, a bark bubble over the sender's in-world phantom, does not need new
research: `docs/research/bark-replay.md` and `Odyssey.Dialogue.Bark.cs`'s `Odyssey.PlayBark`
already give a **fully verified, already-shipped** (`bark_replay`/`bark_sync` both default ON,
`docs/CONFIG.md` "Always-on plumbing") call:

```c
bool Odyssey.PlayBark(uint speakerObjectId, ReadOnlySpan<byte> text,
                       ReadOnlySpan<byte> resRef, uint flags, bool suppressCapture = true);
```

— which rebuilds a real `CExoString`/`CResRef` locally (never forwards a peer's raw pointers,
the exact bug `bark-replay.md` documents killing the process twice) and calls
`CClientExoAppInternal::BarkString 0x005F2C90`, which resolves to
`CGuiInGame::ShowBarkBubble 0x0062AB30` and draws over whichever creature `speakerObjectId`
names **on THIS client** — precisely the "sender's phantom" semantics the task lead describes,
since a phantom is exactly this mod's local stand-in body for a remote player.

**Why this is not prototyped this round anyway.** `PlayBark` needs an object id for the
speaker. Chat today only carries a pre-formatted display string
(`CoopCommandDispatcher.DrainChat`: `Chat($"{message.Name}: {message.Text}")` →
`KotorGameBridge.PostChatLine` → `SharedBlock.OffsetChatLine`/`OffsetChatSerial`) — there is no
structured "which peer said this" field riding alongside it, and no existing lookup from
"chat sender" to "that sender's phantom object id" on the plugin side (`PhantomController`
tracks phantoms by peer slot for drive/combat purposes, not exposed as a public "peer → object
id" accessor for this use). Wiring that up is a real, boundable piece of work — a new
shared-block field (or reusing the existing peer-id fields chat already implicitly has on the
agent side) plus a plugin-side lookup — but it is NEW cross-process plumbing, not "call an
already-verified function with data already in hand" the way §2's append is. Recommended as the
natural next step, kept out of this pass's prototype so the one thing that ships stays cleanly
low-risk per the task's own bar.

## 4. Typing: leave it on Enter → console

`docs/research/connect-flow.md` already fully researched a real editable text control
(`CSWGuiEditbox`, vtable `0x0073EAC8`) and its one reusable modal wrapper
(`CSWGuiSaveNamePanel`) for the connect/password flow — and that same research **already tried
and rejected** the GUI-panel route for an in-game text prompt, for reasons that apply even more
strongly to chat:

- **Modal.** `CSWGuiSaveNamePanel` (and every other real editbox host in this game) is pushed
  as a modal panel — it owns input exclusively while open. A password prompt asked once, at a
  menu, tolerates that. A chat box a player might want to glance at *while still moving and
  fighting* does not — a modal editbox would freeze the world exactly when chat is likely to be
  about the fight in progress.
- **The `HandleOkButton` → `WriteGame` trap.** The one reusable modal panel's stock OK button
  tries to *save the game* unless its click handler is rebound first (`connect-flow.md`'s own
  "Must intercept OK" warning) — solved once for the password flow with real engine risk
  (`PasswordEntryModal`'s guard-and-fallback discipline), and would have to be solved again here
  for no functional gain over what already works.
- **The console is already the exact shape a chat box needs**, and already proven: an
  engine-owned text field with a caret and backspace, already open to real keystrokes and
  the gamepad's on-screen grid, already wired end-to-end (`FuncInterpHook` → `say` → the co-op
  layer), and — per `connect-flow.md`'s own conclusion for the password case — "a GUI panel
  would be a second implementation of all of that, for the one screen where the first one is
  known to work." That argument is about the main menu there; it is *stronger* in-world, where
  the console does not pause anything and Enter-to-chat already reads as a normal chat
  keybind rather than "opening a console" (the pre-fill with `say ` is what makes that true).
- **No fragmentation across input methods.** The gamepad's on-screen character grid
  (`docs/research/gamepad.md` §7) already targets `Odyssey.OpenChatInput`'s console box as one
  of its four supported fields. A second, parallel chat-only editbox would need its own grid
  wiring, its own focus-gating, and would leave mouse/keyboard and pad players typing chat
  through two different code paths for no player-visible benefit.

**Recommendation: no change.** The console-prefilled-with-`say` stays the typing surface. If a
genuinely persistent, non-modal on-screen input box is ever wanted for its own sake (not chat
specifically), `connect-flow.md`'s `CSWGuiEditbox` research is the starting point — but nothing
about chat argues for building it now.

## 5. Recommendation, stated as one line each

1. **Showing an incoming line:** keep `ChatOverlayController` (already shipped, already
   live-tested render path) as the primary surface. Add a bark bubble over the sender's
   phantom later, once a sender→phantom id path exists (§3) — the render call itself needs no
   further research, it already ships for barks.
2. **Persistent inbox:** append every chat line to the retail Messages screen's dialogue tab via
   the newly-verified `CGuiInGame::AppendToDialogBuffer` (§2) — prototyped this pass, OFF by
   default (`chat_messages_screen`), NEEDS-LIVE.
3. **Typing:** no change — Enter opens the console prefilled with `say `, exactly as today.

## 6. The prototype (`chat_messages_screen`; default ON since the §7 live run)

- `src/KotorCoop.Plugin/Odyssey.Dialogue.MessagesScreen.cs` — the write side:
  `Odyssey.AppendChatLineToMessagesScreen(speaker, text)`, the `AppendToDialogBufferFn`
  delegate at `0x0062B680`, and the CExoString marshalling described in §2.3. No-ops (returns
  `false`) when there is no loaded module (`GetInGameGui() == 0`) or the text is empty.
- `src/KotorCoop.Plugin/ChatMessagesScreenController.cs` — reads the SAME
  `SharedBlockView.ChatSerial`/`ChatLine` fields `ChatOverlayController` already reads (no new
  shared-block field, no new agent-side plumbing — every chat line that already reaches the
  overlay is mirrored here too). The line arrives pre-formatted as `"Name: text"`
  (`CoopCommandDispatcher.Chat`), so it is appended as the message half with an empty speaker —
  splitting the two would need a new block field this pass deliberately does not add.
- Wired into `PluginHost` (`_chatMessagesScreen` field, constructed in
  `PluginHost.Controllers.cs`, ticked in `PluginHost.Frame.cs` right next to `_chatOverlay`,
  before the epoch gate — harmless at the main menu since `GetInGameGui()` is 0 there and the
  call simply no-ops, but the chat serial is still consumed so a line that arrives before a
  module loads is not retried once one does; best-effort, matching the ring's own "recent
  history" nature).
- **Config, single reader by design.** `PluginConfig.ReadBool("chat_messages_screen", false)`
  only — no `CoopConfig`/`AgentComposition` reader, and this is a deliberate departure from the
  "three readers must agree" convention, not an oversight: that convention exists for keys that
  gate cross-peer *behaviour* (something the two instances need to agree on, or that the agent
  needs to know about to negotiate). This key gates a purely local rendering choice built
  entirely from data the plugin already receives (the same `ChatLine` the overlay already
  renders) — the exact same shape as existing plugin-only, single-reader dev switches
  (`gui_probe`, `frame_stats`, `delay_probe`) that also never touch `CoopConfig`. Registered in
  both `PluginConfig.cs`'s and `CoopConfig.cs`'s `DeveloperKnownKeys` lists (the latter purely
  so the shared "every key some reader is aware of" bookkeeping — and
  `CoopIniExampleDocSyncTests`, which scrapes `PluginConfig.ReadBool` call sites automatically —
  does not flag it as an unrecognised stray key), documented in `docs/CONFIG.md`'s new "Chat
  UI" developer-switches subsection. Not added to `profiles/coop.ini.example` (ships OFF by
  omission, same as other NEEDS-LIVE dev switches) and not touched in
  `tools/CoopIniDefaults.ps1` (that file's drift list is for a *default that changed* on an
  existing key; this is a brand-new key, which `Get-CoopIniDrift` already reports correctly as
  a `MissingKeys` entry with no code change needed).
- Build: `pwsh src/KotorCoop.Plugin/build.ps1 -WarnAsError` — clean, exit 0.
- Tests: `dotnet test tests/KotorCoop.Tests/KotorCoop.Tests.csproj --filter
  "FullyQualifiedName~CoopIniExampleDocSyncTests"` and `~CoopConfigTests` — both green (3/3 and
  56/56). No unit test exercises the unsafe engine call itself — the plugin is not compiled into
  the test project at all (`docs/research/README.md`'s own "slnx skips the plugin" note), the
  same reason `BarkController`/`PlayBark` have none either.

### NEEDS-LIVE checklist

None of this has been run against the real game (this task was explicitly static-RE-only, no
rig). In order of what would be checked first:

1. **The call itself does what the disassembly says.** With `chat_messages_screen=1` on both
   instances, send a chat line (`say hello`), then open the pause menu → Messages → the
   Dialogue tab on BOTH the sender's and receiver's screen. Expect the line to appear as
   `"PeerName: hello"` in the list, most recent at the bottom (or wherever the listbox default
   sorts — unverified which end `dialog_listbox` scrolls to by default).
2. **The 64-line ring shift.** Send 70+ chat lines in one module and confirm the oldest ones
   fall off the Messages screen (not a crash, not a wrap that overwrites the wrong slot) and
   that ordinary NPC dialogue lines that happen in between still interleave correctly (the ring
   is shared — a real conversation during a chat burst should not desync the two, since both
   go through the same guarded, count-incrementing append).
3. **Transition volatility.** Send a chat line, cross a loading transition, reopen Messages —
   confirm §2.4's prediction (the line is gone) rather than assume it; if it survives, that is
   a pleasant surprise worth documenting, not a regression.
4. **No interaction with an active conversation.** Chat while genuinely mid-dialogue (a real
   NPC conversation on screen) — confirm the appended chat line does not visually disturb the
   dialogue panel itself (it is a different UI element, `CSWGuiDialog`/letterbox, not
   `CSWGuiInGameMessages` — expected no interaction, but unverified).
5. **The config toggle itself.** `chat_messages_screen=0` (the default) — confirm chat still
   works exactly as today (overlay + console) and the Messages Dialogue tab shows only real
   game dialogue, nothing from chat.

## 7. Live run 2026-09-25 (now default ON)

Two GOG installs on the isolated rig. Players opened the Messages screen with the plugin's
`input click` on the Messages icon (686,17 in an 800x600 window).

- **Lines land in the Dialog tab.** Chat sent from each side appeared as `Lanik: …` and
  `Malak: …` on the receiver, and `you: …` on the sender, after the game's own lines.
- **History survives an area change.** After the host warped to `danm14aa` and the joiner
  followed, the earlier lines were still there, and new chat was appended.
- **Filtering was needed, and is now in.** The first pass mirrored every overlay line. Console
  replies (`module danm14aa at …`) and two save diagnostics leaked into the inbox. Chat lines
  now carry a leading 0x01 marker (`KotorGameBridge.PostInboxChatLine`, written by the agent's
  `Chat()`). The inbox keeps only marked lines, and the overlay strips the marker. After the
  change, the joiner's inbox showed only chat plus the game's own lines. The two diagnostics
  (`save identity differs …` and the character-capture outcome) moved to the log.
- **The ring is saved with the world.** After a full main-menu rejoin (world adoption), the
  joiner's inbox held `Malak: inbox filter check` from before the disconnect, carried in the
  host's save.
- **A conversation interleaved with chat** was not exercised.

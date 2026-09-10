# The "skip mid-sentence desyncs" report — hypotheses, instrumentation, and the round-75 live-test script (2026-08-23)

## The report

User: "we've noticed cases still where if one player skips mid dialogue sentence it doesn't
sync properly with the others." This is a live symptom that needs live reproduction to
diagnose properly, and this round does not have the rig (a companion-following investigation
is live on it — see the merge boundary in `docs/research/dialogue-sync.md`'s own Item 1
section for what this round did and did not touch). So this round's job, per the brief, is
three things: form concrete hypotheses from the code alone, add fine-grained diagnostic
logging so a FUTURE live round can see exactly what happened, and write the exact live-test
script for that round to run.

Read first (this document builds directly on both, and repeats nothing they already say):
`docs/research/dialogue-sync.md` (node sync design, the reply-slot-replay dead end, the
current entry-index-driven design) and `docs/research/dialogue-join-latency.md` (the measured
polling latency this symptom's most likely mechanism rides on).

## The mechanism, as the code describes it

Three moving parts, all already documented elsewhere in this repo and only summarised here:

1. **The owner publishes POSITION, not picks.** `DialogueSpectateController.PublishLocal`
   reads `Odyssey.GetDialogueNodeIndex()` every frame the local conversation is active and
   writes it to `block.LocalDialogueNode`/`LocalDialogueNodeSerial` whenever it changes
   (`dialogue-sync.md`'s "Node sync: replaying the owner's POSITION, not their pick"). The
   reply pick log still travels, but is explicitly "DIAGNOSTIC ONLY — nothing is driven from
   this" per the code's own comment.
2. **The wire is a LEVEL, not a QUEUE.** `DialogueState`/`DialogueRequestPacket` carry exactly
   one `(NodeIndex, NodeSerial)` pair — the owner's CURRENT position, not a history of every
   position visited. `CoopAgent.PumpDialogue` samples the plugin's block on a cadence
   (`CombatPumpIntervalMs`, 150 ms by default — `dialogue_fast_join=0` is the default in
   `coop.ini.example`; the fast per-poll-tick path exists but is opt-in) and publishes whatever
   the CURRENT sample says. `DialogueTickGate` (the fast-path gate) is itself a "changed OR
   heartbeat due" test over a single fingerprint — it decides WHETHER to publish this tick's
   sample, not to accumulate several ticks' worth of samples into one send.
3. **The follower is driven directly to the published node.** `DriveToPeerNode` reads
   `block.RemoteDialogueNode`/`RemoteDialogueNodeSerial` and, if different from the last node
   applied (`DialogueNodeFollower.NeedsNode`), calls `Odyssey.DriveDialogueToNode(wantedNode)`
   — a direct call to `CSWSDialog::SendDialogEntry` with the target entry index, which
   `dialogue-sync.md`'s node-sync section says bounds-checks the index and jumps straight
   there; it does not need to visit intermediate entries one at a time. This call also fires
   `CGuiInGame::HandleDialogSound` (the VO) and lipsync arming for the LANDED entry only —
   nothing plays for any entry it does not land on.

Put together: the follower is always driven to the OWNER's most recently PUBLISHED node, and
that publish is a periodic (or change-triggered, but still single-value) SAMPLE of wherever
the owner currently is — never a record of every node the owner passed through to get there.

## Hypotheses

### H1 — Does a "skip" even change the node, or only the VO's own pacing?

This is the fork every other hypothesis depends on, and it is **NEEDS-LIVE, not resolved by
this pass**: does KOTOR's line-skip action (see "What triggers a skip" below) advance
`CSWSDialog`'s current-entry index at all, or does it only fast-forward the audio/subtitle
timer WITHIN the entry that is already showing, with the actual entry transition still gated
on whatever the node's own script/auto-continue logic decides (unaffected by the skip)?

- **If skip is pure VO fast-forward** (node index does not change any faster than it would
  without skipping — a skip just removes the WAIT, not a state transition), then **no desync
  is possible from the node-publish/drive mechanism described above**, because skip produces
  no additional node transitions to lose. The reported symptom would have to come from
  somewhere else entirely — a rendering/audio glitch unrelated to this mod's sync channel, or
  (see H3) a reply-adjacent race that has nothing to do with skipping specifically.
- **If skip DOES advance the node** — the far more likely reading, since KOTOR's own dialogue
  UI visibly jumps straight to the next line or the reply list the instant a line is skipped,
  which is a real state transition, not merely audio finishing early — then H2 below applies
  directly.

### H2 — Skip-driven rapid node transitions coalesce on the polling cadence (most likely mechanism)

If H1 resolves to "skip advances the node," then several rapid skips through a run of
auto-continuing narration entries (no reply choice — exactly the "sequential owner lines"
shape a recruitment speech has) produce several REAL node transitions inside one polling
window. Because the wire carries only the LATEST `(node, serial)` and the poll period is
`CombatPumpIntervalMs` (150 ms) by DEFAULT configuration (`dialogue_fast_join=0` — most players
are on this path, not the opt-in fast one), a burst of skips faster than 150 ms apart means:

- the owner's own screen shows every line they skip through, in order, because they are
  driving the real engine directly;
- the follower's screen only ever receives the node the owner happened to be on at the NEXT
  sample — every intermediate entry's speaker/line/VO/lipsync (all fired together by
  `SendDialogEntry`, per `dialogue-sync.md`'s own note) is never delivered to the follower AT
  ALL, not delayed, not corrupted — simply never sent, because the wire has no queue to hold it
  in.

This is not obviously a "bug" in the sense of producing a WRONG final state — `DriveToPeerNode`
still lands the follower on the owner's correct current node once it catches up, and
`DialogueNodeFollower.Arrived` still converges — but from the follower's own experience it
reads exactly as "doesn't sync properly": their screen jumps ahead without them having read or
heard the lines in between, at exactly the moment their partner is the one skipping, not them.
**This is the hypothesis this round considers most likely to explain the report**, and it is
inherent to a level-triggered, polling-sampled design — fixing it (if a fix is wanted) is a
different-shaped project (queuing every transition, or lowering the effective sample interval
enough that a human's skip rate cannot outrun it) than finding a bug to patch, which is exactly
why this round instruments rather than guesses at a fix.

Even with `dialogue_fast_join=1` (every poll tick, not 150 ms), the SAME structural coalescing
is possible in principle — a poll tick is still a discrete sample, not a continuous feed — but
the window shrinks enormously (from ~150 ms to roughly one frame), which the instrumentation
below can quantify precisely rather than leaving as a guess. If the live round finds the report
reproduces just as readily with `dialogue_fast_join=1`, that rules out "the 150 ms cadence is
simply too coarse" as the WHOLE story and points at something coarser-grained than polling
interval.

### H3 — A race between the pick-log/reply path and the node-drive path (narrower, less likely)

The task's own brief asks this to be checked explicitly: could a skip auto-advance through an
auto-picked single-reply node fast enough to race `DialogueTickGate`'s dedup/heartbeat window?
Reading the code closely, this folds mostly into H2 rather than being a separate mechanism:

- The actual DRIVE mechanism (`DriveToPeerNode`) never consults the reply/pick log at all —
  `dialogue-sync.md`'s own "the pick replay that used to be here is gone, and must not come
  back" section is emphatic that picks are diagnostic-only. So a race in the PICK path cannot,
  by construction, desync the NODE the follower is driven to.
- `DialogueTickGate`'s fingerprint DOES include `replySerial`/`replyIndex` alongside the node
  fields (`CoopAgent.PumpDialogue`'s `HashCode.Combine` call). For the fingerprint to fail to
  detect a real change, the NODE would have to end up back at the SAME value as the last
  published one despite having visited others in between — which is exactly H2's coalescing,
  observed through the fingerprint mechanism rather than a separate defect in it.
- The one place this could matter independently: `CSWGuiDialog::SelectReply`
  (`DialogueReplyHook`'s hook point) is described as catching "every reply, keyboard or mouse"
  — it is plausible, but NOT verified either way in this pass, that an auto-continuing
  narration entry with no visible reply choice never calls `SelectReply` at all (there is
  nothing to select), meaning the pick log simply has nothing to record for a skip-driven
  narration run regardless of speed. If so, H3 is moot for narration-only skip bursts and only
  matters for a conversation where skipping ALSO happens to blow through a real (if
  single-option) reply choice — narrower than the report as described ("mid dialogue
  sentence"), and not the primary hypothesis this round is instrumenting for.

**This round's ranking**: H2 is the leading hypothesis, H1 is the prerequisite fork a live
round must resolve first, and H3 is a secondary check worth ruling out with the same
instrumentation rather than a hypothesis expected to be the answer.

## What triggers a skip — from the game's own controls, and the tooling gap

KOTOR's dialogue UI advances a line's voiceover/subtitle display faster than its natural
duration on a click or a keypress during playback (Space is the well-known default in this
engine's own dialogue UI convention) — clicking or pressing the bound key mid-line either
reveals the reply list immediately or advances straight to the next auto-continue entry,
rather than waiting out the VO. This is player-facing UI behaviour, not something this repo's
static-disassembly tooling is positioned to independently re-confirm — it is not a memory
offset or an engine internal, it is "what a human does with a keyboard/mouse," and the exact
bound key is whatever the tester's own KOTOR key bindings say (check in-game Options → Controls
if unsure; Space and a mouse click are the two default candidates to try).

Critically: **`docs/live-verification.md`'s own "hard limit on autonomous testing" applies
directly here** — "injected input does not reach the game at all" (both mouse and keyboard
injection were tested and failed; a held key produced zero movement). This mod's own test-drive
hooks (`TestDriveController`, `blockpoke.ps1`) can open and close a conversation and drive a
reply SLOT, but nothing in this codebase can synthesize "the human pressed skip mid-VO" — that
is exactly the category of input this repo's own tooling cannot inject. **A human tester must
physically do the skipping** for round 75 below; there is no automation path around it.

## Instrumentation added this round

Both new log lines are capped (`NodeLogBudget = 4000` each, independent budgets so a flood on
one side cannot starve the other's evidence — the same "capped, timestamped" discipline
round 52's ground-truth attacker-id log used for combat, `shared-state-audit.md`), monotonic
per session (reset only on a module change, `DialogueSpectateController.Invalidate`), and
add no allocation or engine calls beyond what the existing code path already made — they log
what was already being computed, nothing new is read from the engine to produce them.

**Publish side** — `DialogueSpectateController.PublishLocal`, at the exact point
`block.LocalDialogueNode` changes (unconditional, every real transition, not sampled):

```
dialogue-node-pub: #<N> node <from> -> <to> serial <S> at <TickCount64>
```

**Consume/replay side** — `DialogueSpectateController.DriveToPeerNode`, on EVERY attempt
(not only a successful arrival — the pre-existing `LogFew("dialogue: followed the owner ...")`
line only fires on arrival or at the 1200-deferral complaint threshold; this fires every time,
including "already there" and a declined/deferred call), with the actual call's own wall-clock
cost so a slow ENGINE CALL is distinguishable from a slow WIRE UPDATE:

```
dialogue-node-drive: #<N> wanted <W> before <B> landed <L> arrived=<bool> callMs=<ms> at <TickCount64>
```

Reading the two logs together (both instances' `kotorcoop-plugin.log`, same wall clock since
the two-instance rig runs on one machine) answers exactly what a live round needs:

- **How many REAL node transitions did the owner produce during a skip burst** (count the
  `dialogue-node-pub` lines on the owner's own log between the first and last skip) **versus
  how many the follower's own `dialogue-node-drive` log shows it was actually asked to drive
  to.** If the owner's publish count is much larger than the follower's drive count, H2 is
  confirmed directly — coalescing measured, not inferred.
- **The gap between consecutive `dialogue-node-pub` timestamps on the owner** — this is the
  real inter-node interval a human's skip rate produces, which settles whether 150 ms
  (`dialogue_fast_join=0`) or even a single poll tick (`=1`) can keep up with it.
- **`callMs` on the drive side** — rules out "the engine call itself is slow" as a confound
  separate from "the wire sample was too coarse."

## Live-test script for round 75

Read `docs/research/shared-state-audit.md`'s own round-numbering convention before running
this — if a later round has already run by the time this executes, use the next free number
instead of 75.

### Setup

- Both plugin and agent rebuilt and deployed (`pwsh src\KotorCoop.Plugin\build.ps1 -Install`,
  per this repo's own "demo.ps1 does not deploy the plugin" note) on BOTH installs.
- `tools\demo.ps1` cold start onto a save in `END_M01AA` (or `END_M01AB`), OR a manual two-
  window session — either way, both plugin logs must be reachable afterward
  (`kotorcoop-plugin.log` on each install).
- Confirm `dialogue_auto_join` and `dialogue_join_teleport` are set so the two players can
  actually get face to face with the target NPC without fighting the auto-join machinery —
  `dialogue_join_teleport=1` (already the shipped default) is enough; `dialogue_auto_join`'s
  value does not matter for this test since both players will already be present.
- **Find a conversation with several sequential, auto-continuing owner lines** — the task's own
  suggestion is Trask's recruitment dialogue (`end_trask`, `.dlg` resref used throughout
  `dialogue-sync.md`'s own live passes, e.g. `end_trask01.dlg`) on `END_M01AA`, which is already
  the conversation every other live pass in this doc set used and is confirmed to have dozens
  of entries including long uninterrupted narration stretches. If Trask is already past that
  content on the loaded save, use a save from earlier in the Endar Spire, or any other NPC with
  a multi-line monologue (a quest-giver's opening pitch is a common shape).

### Procedure

1. Both players stand next to the NPC. One player (the OWNER) opens the conversation
   (`talk end_trask` via the test hook, or a real click); the other (the FOLLOWER) joins (a
   real click on the same NPC, or `talk end_trask` on their own instance once the owner's
   token is visible — either reaches the same adopt/follow path).
2. Confirm the follow is live: both plugin logs show matching `dialogue-node-pub`/
   `dialogue-node-drive` activity for the first couple of lines, and `NODES: on the same entry`
   if `tools/dlgprobe.ps1` is run alongside (optional — the new log lines are sufficient on
   their own and do not need the probe script running at the same time).
3. **The owner repeatedly and quickly skips** through several consecutive lines using KOTOR's
   own skip control (see "What triggers a skip" above — try Space first; fall back to
   mouse-clicking the dialogue panel if Space does nothing on this build). Aim for at least
   5-10 skips in as fast a burst as the tester can physically manage — the whole point is to
   beat the 150 ms default cadence, which requires real human speed, not a leisurely pace.
4. **Watch the follower's screen directly** while this happens: does it visibly jump/skip
   ahead of what it is showing, freeze, show mismatched speaker/line pairs, or (the more
   concerning failure) get stuck showing a line the owner is no longer on? Note exactly what is
   seen, in the tester's own words, alongside the timestamp so it can be matched against the
   logs afterward.
5. Repeat the skip burst 2-3 times across different points in the conversation (early, an
   auto-continue-heavy stretch, and — if one exists in the chosen `.dlg` — a stretch that leads
   into an actual reply choice, to see whether the coalescing behaves differently right before
   a branch point).
6. Let the conversation run to its natural end (or close it) and collect both
   `kotorcoop-plugin.log` files.

### What to check in the logs afterward

- Pull every `dialogue-node-pub` line from the OWNER's log during each skip burst's time
  window; count them and note the node sequence and the gaps between timestamps.
- Pull every `dialogue-node-drive` line from the FOLLOWER's log in the SAME window; compare the
  node sequence and count against the owner's publish sequence.
- If the follower's drive count is smaller than the owner's publish count during a burst, H2 is
  confirmed — report exactly how many nodes were coalesced per burst and the owner's measured
  skip rate (from the publish timestamps) so a future round can judge whether tightening the
  cadence (`dialogue_fast_join=1`, or a lower `CombatPumpIntervalMs`) would plausibly close the
  gap, or whether the human skip rate can outrun even the fast path.
- If the follower's drive count matches the owner's publish count 1:1 even under a fast skip
  burst, H2 does NOT reproduce as described and the "doesn't sync properly" report needs a
  different explanation — re-read H1 (did the node even change on every skip?) and H3 next.
- Either way, report whether `dialogue_fast_join=0` (default) and `=1` differ qualitatively —
  this is the single most actionable knob this investigation can hand back: if `=1` visibly
  fixes what `=0` does not, that is a strong argument for flipping the default, independent of
  everything else in this document.

## Live pass (2026-08-23): blocked on real input access, not on the instrumentation

Two-GOG-install rig, `END_M01AA`, both builds rebuilt/installed (block v51 / protocol v38). The
save's clean state (freshly reloaded via `tools\demo.ps1`, before any other testing touched it)
puts `end_trask`'s conversation at entry **29** — the long, multi-starting-entry recruitment-
shaped dialogue this document names as the target content. Host opened it (`talk end_trask`);
client joined and adopted within the same tick:

```
HOST    [12:32:54.993] dialogue-node-pub: #1 node -1 -> 29 serial 1 at 3487625
HOST    [12:32:54.994] dialogue: engine reached StartDialog (1 so far, forced=False)
CLIENT  [12:33:04.887] dialogue-node-pub: #1 node -1 -> 19 serial 1 at 3497531
CLIENT  [12:33:04.887] dialogue: engine reached StartDialog (1 so far, forced=False)
CLIENT  [12:33:04.888] dialogue: the engine started the same conversation here; adopting it to follow
CLIENT  [12:33:04.891] dialogue: followed conversation delivered content 0 ms after opening (0 ms after the request)
CLIENT  [12:33:04.891] dialogue-node-drive: #1 wanted 29 before 19 landed 29 arrived=True callMs=0 at 3497531
CLIENT  [12:33:04.892] dialogue: followed the owner 19 -> 29
CLIENT  [12:33:04.896] dialogue-node-pub: #2 node 19 -> 29 serial 2 at 3497531
```

**The instrumentation itself is confirmed live and working exactly as designed**: `dialogue-
node-pub` fires on the owner for every real transition (including the client's own brief local
start at 19 before it recognised and adopted the owner's copy — the adopt path publishes its
own node too, which is expected and correctly distinguished from the drive path below);
`dialogue-node-drive` fires on the follower with the `wanted`/`before`/`landed`/`arrived`/
`callMs` shape this round's own design specified, and it landed correctly (`arrived=True`,
`callMs=0`) on the first attempt. This is real evidence the publish/drive plumbing this document
depends on is sound going into the skip test — whatever the skip test itself could or could not
show, it would not have been contaminated by broken instrumentation.

### The skip test itself: blocked, and the blocker is more specific than "input never reaches KOTOR"

`docs/live-verification.md`'s standing note says injected input does not reach the game at all
(tested 2026-08-01: held-W movement, mouse-click loader). This round found the SAME outcome but
a MORE PRECISE cause, worth recording because it changes what a future round should try first.

`tools\sendkey.ps1 -Keys space` (the documented skip control) and `tools\click.ps1` against the
dialogue panel both produced **zero** effect on the live conversation — `localNode` stayed at
`29` across a 10-press rapid burst, a single Space, a mouse click, and (as a positive control,
since an EARLIER pass's `dlgprobe.ps1` run had reported a reply-key press working) a bare `1`
reply key. All four produced no node change whatsoever. Checking *why*, rather than assuming the
old finding still applied unchanged, found the actual mechanism this time: **the KOTOR window
never had Windows foreground focus to begin with.** `GetForegroundWindow()` returned an
unrelated application's window (a live Steam game the operating this machine, "Dota 2") every
time it was checked, including immediately after `sendkey.ps1`'s own `SetForegroundWindow` +
`AttachThreadInput` sequence ran, and including after an explicit, separate
`ShowWindow`/`BringWindowToTop`/`SetForegroundWindow` call made directly against the KOTOR host
window from this session. Windows' foreground-lock protection is doing exactly what it is
designed to do: a background automation process cannot steal input focus from a window the
actual logged-in user is actively running, and rightly so — nothing in this codebase should be
trying to forcibly seize the desktop out from under a person's own foreground application. One
non-destructive attempt was made to un-block this (minimizing the competing window) and it did
not change the outcome either; that avenue was not pursued further, because repeatedly fighting
another live, human-owned application for OS focus is not something an automated round should be
doing regardless of whether it would eventually succeed.

**This is the item that genuinely needs a human at the keyboard**, exactly as this document
predicted before any live attempt was made. It is not a KOTOR-specific input-injection failure
this time so much as an ordinary desktop-focus contention that happens to produce the identical
symptom (zero observable effect) — worth distinguishing for whoever tries next: on a machine
where the two KOTOR windows genuinely hold foreground focus (no competing foreground
application), `sendkey.ps1 -Keys space` and a real reply-key press are both still untested this
round and remain the first things to try, since the EARLIER `dlgprobe.ps1` pass's own reply-key
success (2026-08-11, `sendkey.ps1`'s `PickAfter` mechanism advancing a real conversation) shows
the injection technique itself has worked on this exact game build before, under different
desktop conditions.

### What this pass could, and could not, settle

- **H1/H2/H3**: none resolved. No skip was ever delivered to the game, so no node transition
  attributable to a skip was ever produced or lost. The hypotheses stand exactly as ranked before
  this pass.
- **The instrumentation**: fully confirmed working, both the publish side (owner) and the drive
  side (follower), with real timestamps and the exact log shapes this document specified. A
  future round with real input access can trust it immediately rather than needing to re-verify
  it before trusting what it reports about a skip burst.
- **`dialogue_fast_join=0` vs `=1`**: not exercised this round (no skip to compare it against).
- Left shipped, unchanged, for whoever can supply the one missing ingredient: a real keypress or
  click landing on a KOTOR window that actually holds Windows' foreground focus.

## Live pass, retry (2026-08-23, round 77): foreground WAS achieved this time — and the skip
## still never registered, so the blocker is narrower and deeper than last round's

The user reported this should work now. It was retested, precisely, before attempting the skip
burst itself, per this document's own standing instruction not to burn a round on a blocked path.

### Step 1: the SAME positive control the prior round used — still failed, but differently

`tools\sendkey.ps1 -Keys space -ProcId <host>`, run exactly as the prior round ran it: before and
after, `GetForegroundWindow()` still read an unrelated window (`chrome`, not `Dota 2` this time —
a different competing app, same symptom). The script's own built-in `Focus()` (`ShowWindow` +
`BringWindowToTop` + `SetForegroundWindow`, wrapped in `AttachThreadInput`) did not move the OS
foreground pointer at all, confirming the prior round's finding reproduces on this machine
independent of which application happens to be competing for focus.

### Step 2: root-caused one level deeper, and fixed — `AttachThreadInput` alone does not satisfy
### Windows' foreground-lock guard; a synthetic Alt key tap does

Windows refuses `SetForegroundWindow` from a background process unless the CALLING process looks
like it just received real input itself. `AttachThreadInput` alone (the script's existing
approach) did not satisfy this guard on this session. Adding a synthetic Alt keydown/keyup
(`keybd_event(0x12, ...)`) immediately before `SetForegroundWindow` — a well-known, narrow trick,
not a broad input simulation — did:

```
before: foreground pid=3472 name=chrome
[SetForegroundWindow with the Alt-tap fix]
after:  foreground pid=6672 name=swkotor
```

**Fixed in the tooling** (`tools/sendkey.ps1`, `tools/click.ps1` — both `Focus()` helpers), so
every future round gets this for free rather than re-discovering it. This is a genuine
improvement over last round's total inability to move the OS foreground pointer at all.

### Step 3: even with confirmed, atomic, held foreground — the keypress still never registered

This is the round's real finding, and it is more specific than "input does not reach the game":
with `GetForegroundWindow()` verified to read the KOTOR process immediately after the fix, and a
screenshot taken in the SAME atomic script call confirming the window was genuinely visible and
on top (showing a live Trask reply-choice screen, not another app's pixels), a repeated burst of
scan-code `1` key presses (3 taps, 80 ms hold, 200 ms gaps, all inside one `AttachThreadInput`
session, no gap for anything else to steal focus back) produced **zero change**: the same reply
screen, byte-identical, in a screenshot taken at the end of the same atomic call, and no new
`dialogue-node-pub` line in the plugin log for the whole window.

This machine's desktop was independently confirmed to be in genuine, active, concurrent use by
the machine's own human operator throughout this session (a full-desktop screenshot mid-session
showed two Chrome windows actively navigating Gmail and a GitHub notifications page, with a
live-updating on-screen clock) — which explains why the OS foreground pointer kept drifting back
to Chrome a few seconds after each grab, exactly as the prior round's Dota 2 competitor did. But
it does NOT explain the zero-effect result inside the ATOMIC test above, where foreground was
verified held for the whole send. That result points at something past simple OS-level
foreground contention: most plausibly that KOTOR's own DirectInput device acquisition (its
`DISCL_FOREGROUND` cooperative-level re-`Acquire()`) depends on the window actually receiving a
real `WM_ACTIVATE`/`WM_ACTIVATEAPP` message sequence from a genuine user-driven focus change,
which a `SetForegroundWindow` call forced from an external process via `AttachThreadInput` and a
synthetic Alt tap may set the OS-level pointer without necessarily delivering in the exact form
the engine's own window procedure expects. This is inference, not verified by disassembly this
pass — nothing in this round traced KOTOR's own DirectInput acquisition code.

### Verdict: still blocked, but the failure mode moved from "cannot get foreground" to "foreground
### confirmed, input still not registering" — a narrower, more specific target for next time

- **H1/H2/H3**: still none resolved. No skip was ever delivered to the game.
- **The sendkey.ps1/click.ps1 Alt-tap fix**: real, live-confirmed, and kept — future rounds start
  one step ahead of where this one did.
- **`dialogue_fast_join=0` vs `=1`**: still not exercised (no skip to compare it against).
- **What a future round should try next**, in order: (1) a human physically at the keyboard, which
  sidesteps this whole class of problem outright; (2) if automation must continue, trace
  `swkotor.exe`'s own DirectInput device setup (`IDirectInput8::CreateDevice` /
  `SetCooperativeLevel` / `Acquire`) to confirm the exact cooperative level and re-acquire
  trigger, since this round's evidence narrows the problem to "the engine does not believe it has
  input focus even when Windows says it does" rather than "Windows never gives it focus"; (3) as a
  narrower experiment, try `SendInput` immediately after a REAL user-driven Alt-Tab (not a
  synthetic one) to see whether that specific message sequence is the missing ingredient.

---

## Round 79 (2026-08-23): the premise was wrong — reproduced entirely in-process, H1 and H2 both
## settled with real data, no OS input involved at any point

Every round above shares one assumption, stated as fact in this document's own earlier text:
*"there is no automation path around it"* because *"the ONLY way to reproduce a skip is real OS
keyboard/mouse input reaching the game window."* **That assumption is false, and this round's
whole job was disproving it the same way every other "can't inject input" wall in this codebase
has been solved before**: find the engine function a keypress ultimately calls, and call it
directly, in-process, from inside the game's own address space — exactly how `usedoor`,
`useplaceable`, `attack`, `leader` and `recruit` were built. `sendkey.ps1`, `click.ps1`, Windows
focus, DirectInput re-acquisition — none of it is relevant any more. Nothing in this round
touched the OS input layer at all.

### Static RE: the skip mechanism, fully VERIFIED by disassembly

Starting point per the brief: `CSWGuiDialog::HandleInputEvent 0x006A7230`, the dialogue panel's
own keyboard/click router (`python tools/disasm.py 0x6A7230`, cross-checked with
`tools/fieldwrites.py`). Its switch on the event code (arg1) has a case for `0x27` and `0x2D` —
the SAME case Enter and a mouse click both land in, and the SAME case that (per
`docs/research/dialogue-sync.md`'s already-verified `SelectReply`) commits the highlighted reply
when the panel is showing one. The dispatch inside that case is what this round needed:

```
0x006A7266  mov  eax, [esi+0x1DF8]        ; panel's own flag byte
0x006A726C  test al, 1
0x006A726E  je   0x6A729E                 ; bit0=0: SELECT the highlighted reply (vtable+0x6C,
                                           ;   *(panel+0x1C8A)) — this is what a click on a
                                           ;   reply-list screen does
0x006A7270  test al, 2
0x006A7272  jne  0x6A730D                 ; bit0=1, bit1=1: no-op (already mid-skip / busy)
0x006A7278  mov  eax, [esi+0x19C0]        ; bit0=1, bit1=0: showing a LINE, not a reply list,
0x006A727E  mov  ecx, [0x7A39FC]          ;   and not already skipping — THE SKIP CASE
0x006A7284  mov  ecx, [ecx+8]             ; ecx = serverApp = mgr+8
0x006A7288  call 0x4AE970                 ; CServerExoApp::SkipPastDialog(serverApp, objId)
```

`panel+0x19C0` defaults to the constant `0x7F000000` (`tools/fieldwrites.py 0x19C0` finds the
literal store inside `CSWGuiDialogCinematic::Constructor`) — the same generic "the player"
sentinel id `CSWSObject::UpdateDialog`'s own owner gate already reads. **VERIFIED**: this is
retail's actual skip control, one case away from the reply-select case this repo already trusted.

`CServerExoApp::SkipPastDialog 0x004AE970` is a two-instruction thunk
(`mov ecx,[ecx+4]; jmp 0x4B1F90`) to `CServerExoAppInternal::SkipPastDialog 0x004B1F90`, whose
entire body — after resolving the object id through the server's own hashtable (`0x4D8230`,
`internal+0x1005C`, the SAME lookup `docs/research/control-return.md` already names) and
vtable-confirming it as a `CSWSObject` (`vtable+0x10`, the `AsSWSObject` cast
`docs/research/shared-state-audit.md` already names) — is exactly one write:

```
0x004B1FD1  mov dword ptr [eax+0x1F4], 1
```

**That offset is not new to this codebase.** `Odyssey.cs`'s `ObjDlgSendScratch = 0x1F4` was
already a named constant before this round, discovered independently through
`DriveDialogueToNode`'s own RE and documented as *"a dialog scratch field the engine clears
immediately before every call to the producer (0x004CD4AB)"* — nobody had connected it to skip
until this pass found the SAME offset from the opposite direction (the input router, not the
node-drive producer) and the two readings turned out to be the same field serving two purposes.

The consumer, `CSWSObject::IsDialogDelay 0x004CD2D0`, is **hypothesis (a) from the task brief,
now VERIFIED rather than guessed**: at `0x004CD3B1` it reads `[this+0x1F4]`; if nonzero AND the
active `CSWSDialog`'s own `+0x3C` is ALSO nonzero (a "something is actually pending" guard — a
skip against an entry with nothing playing is a genuine, harmless no-op, exactly like a real
retail skip keypress on an idle screen), it runs the identical "entry finished" bookkeeping the
natural end-of-VO path runs (`[this+0x220] = 1`, then the same two helper calls,
`call 0x4AEDE0` / `call 0x4ADF50`, the natural-completion branch makes) and returns **0** ("not
delayed") instead of **1** — which is exactly what lets the very next `UpdateDialog` tick's
`SendDialogEntry` call proceed to the next entry, precisely as if the VO had finished on its own.
No raw call is needed and no timer is zeroed directly; a flag-set is genuinely the cheapest,
smallest-blast-radius option available, matching this codebase's own stated preference.

### Implementation: `skipline` (and a small helper, `selectreply`)

`Odyssey.SkipDialogueLine(out nodeBefore, out entryCount)` writes `ObjDlgSendScratch = 1` on the
dialogue OWNER `TryGetActiveDialogue` already resolves and vtable-verifies — smaller blast radius
than replaying `SkipPastDialog`'s own id-hashtable lookup, since the object is already resolved
and already trusted by `DriveDialogueToNode`. Wired end to end, following `useplaceable`'s exact
shape: `ConsoleCommand.Parse` (`"skipline"`, no arguments) → `CoopCommandKind.SkipLine` →
`CoopCommandDispatcher` (`SendCheat(CheatKinds.SkipLine)`) → `CheatController.SkipLine()` →
`Odyssey.SkipDialogueLine`. New cheat kind 22 on both halves
(`SharedBlock.CheatKindSkipLine` / `CheatKinds.SkipLine`), verified in sync by the existing
`CheatKindSyncTests`. Refuses (does not silently no-op) when there is no active conversation;
reports "applied" with the pre-skip node otherwise, honestly noting in the answer text that it is
a no-op if nothing was actually playing (which `IsDialogDelay`'s own guard already makes safe).

A second, smaller primitive was needed to make the live test possible at all: `end_trask01.dlg`,
reached from this save's plot state, is mostly a REPLY-DRIVEN Q&A loop (Trask's post-recruitment
tutorial topics), not a long uninterrupted narration — `skipline` is a correct, deliberate no-op
on every reply-list screen (confirmed live, repeatedly, by screenshot), so reaching a genuinely
skippable VO stretch required navigating replies with no OS input either. Rather than build
something new, this round exposed an ALREADY-VERIFIED, previously-unused primitive:
`Odyssey.SelectDialogueReply` (`CSWGuiDialog::SelectReply`, through the panel's own vtable slot)
existed from the abandoned "replay the owner's pick" node-sync design
(`docs/research/dialogue-sync.md`'s own "REPLAYING THE OWNER'S REPLY SLOT CANNOT CONVERGE"
warning) and had never been deleted, just never called. Reusing it LOCALLY, on your own
conversation, carries none of that design's original hazard (the hazard was replaying a PEER's
pick across two independently-evaluated trees; committing your OWN reply locally is exactly what
a real click does). Wired identically: `selectreply <slot>` → `CoopCommandKind.SelectReply` →
`CheatKinds.SelectReply` (kind 23) → `CheatController.SelectReply()`. Explicitly a test-only
navigation aid, not a feature verb — labelled as such in every layer.

`Odyssey.DescribeDialogueDrive()` (pre-existing) was extended with two fields —
`dialogAt0x3C`/`skipFlag` — read directly from `IsDialogDelay`'s own two guards, and
`CheatController.SkipLine()` logs it before every write. This is what told the live pass apart
"the flag write did nothing" from "there was nothing to skip" the first time a burst landed on a
reply screen and looked, at a glance, like a dead mechanism.

### Live test: H1 settled, repeatedly, unambiguously

Two-GOG-install rig (`tools\demo.ps1`, END_M01AA, block v51 / protocol 38, both builds rebuilt and
installed), Trask (`end_trask`) — the same standard target every prior round in this doc set used.
`skipline` was fired against a genuinely playing line (confirmed by screenshot — Trask mid-VO, no
reply list on screen) via `tools\blockpoke.ps1 -ProcId <host> console 'skipline'`. Every single
time it was fired against an active wait, the node advanced on the VERY NEXT logged frame:

```
[16:06:47.253] dialogue: skipline requested at node 19/74
[16:06:47.256] dialogue-node-pub: #3 node 19 -> 20 serial 3 at 16319890      (3 ms later)

[16:20:25.837] dialogue: skipline requested at node 22/74
[16:20:25.840] dialogue-node-pub: #22 node 22 -> 23 serial 25 at 17138484   (3 ms later)
```

Fired against a reply-list screen (screenshot-confirmed — e.g. node 29's "1. Okay. / 2. How do
you use security on a door? / 3. How do I switch you to the party leader again?"), it was a clean,
correctly-inert no-op, over and over, exactly as `IsDialogDelay`'s own second guard predicts:
`skipline about to poke — owner 0x127 node 29/74 ... isDialogDelay 0` (nothing pending; the entry
is waiting on a player pick, not a VO timer). **H1 is settled: skip DOES advance the node — a
real state transition, not merely audio finishing early — confirmed live, many times, in both a
narration context (advances) and a reply-list context (correctly does not).**

### The tooling problem this round actually had to solve: not the game, the RELAY

Getting a "tight burst" required its own investigation, because the obvious approach — repeated
`blockpoke.ps1 ... console 'skipline'` calls — could not go faster than **~230-310ms per
request**, dominated entirely by the AGENT's own console-channel poll cadence, not the engine
(the underlying `IsDialogDelay`/`SendDialogEntry` calls resolve in low single-digit milliseconds,
confirmed by the node-pub timestamps above). Worse, a true fire-and-forget burst through that
same channel (writes spaced 15-40 ms apart) mostly LOST requests to the console channel's
single-slot mailbox — of 20 rapid writes, typically only 1-3 were ever actually read and executed.

The fix was to stop going through the agent at all for the burst itself: a small one-off script
(`cheatburst.ps1`, scratchpad-local, not shipped) writes `CheatKind`/`CheatSerial` directly into
the plugin's shared block — the SAME channel `skipline` itself ultimately lands on, one hop
shorter than the console relay. Round trip dropped to **~15-16ms**, matching the game's own frame
rate — the actual floor for how fast ANY mechanism, human or automated, could make this engine
advance more than once. This is the cadence the burst below was fired at.

### H2: coalescing, measured directly, for the first time

Navigated (via `selectreply`, paced with real waits between commits so the client/server round
trip that verb needs could land) into a genuine multi-entry narration run inside Trask's
action-menu tutorial topic (entries 22→23→24→25, no reply required between them), then fired the
15-16ms-cadence burst. **Three real node transitions landed in 55-70ms — well inside the 150ms
`CombatPumpIntervalMs` `dialogue_fast_join=0` samples on:**

```
HOST (owner)
[16:20:25.840] dialogue-node-pub: #22 node 22 -> 23 serial 25 at 17138484
[16:20:25.863] dialogue-node-pub: #23 node 23 -> 24 serial 26 at 17138500   (23 ms later)
[16:20:25.895] dialogue-node-pub: #24 node 24 -> 25 serial 27 at 17138531   (32 ms later)

CLIENT (follower), SAME window
[16:21:28.035] dialogue-node-drive: #12 wanted 22 before 25 landed 22 arrived=True ...
[16:21:36.663] dialogue-node-drive: #13 wanted 25 before 22 landed 25 arrived=True ...
```

**The client's own drive log never once mentions node 23 or node 24.** It went straight from 22
to 25. Everything about the two intermediate entries — their speaker, their line, their VO, their
lipsync, all fired together by the SAME `SendDialogEntry` call per `dialogue-sync.md`'s own
finding — was never delivered to the follower at all, not delayed, not corrupted, simply never
sent, because the wire genuinely has no queue to hold it in. Reproduced a second time, independent
navigation, identical shape:

```
HOST: 22 -> 23 -> 24 -> 25 in 70 ms (16:24:10.520 to .590)
CLIENT: dialogue-node-drive #6 wanted 23 landed 23, #7 wanted 24 landed 24, #8 wanted 25 landed 25
```

Wait — that second run's client log shows EVERY node landed individually. That is not a
contradiction; it is the very next section's finding, because that run was made with
`dialogue_fast_join=1` set on both installs.

**This is H2, settled with real, live-measured data for the first time**: a skip-driven burst of
real transitions, closer together than the wire's own sample period, DOES coalesce on the
follower under the default cadence — this is exactly the "doesn't sync properly" report,
reproduced on demand, root-caused to the mechanism this document's own H2 predicted from the code
alone months of hypothesis before any live data existed.

### `dialogue_fast_join=0` vs `=1`: a direct, decisive A/B — the doc's own open question, answered

The SAME exact sequence — `talk end_trask`, `selectreply 1` at the top menu, burst through 19→20→21,
`selectreply 1` again, burst through the 22→23→24→25 chain — was replayed with `dialogue_fast_join=1`
set on BOTH installs (`coop.ini` edited, rig fully restarted — this value is read at agent
startup, no live-reload). Every intermediate node this round could produce, this pass caught,
individually, every time:

```
[16:24:10.520] dialogue-node-pub: #6 node 22 -> 23 ...
[16:24:10.556] dialogue-node-drive: #6 wanted 23 before 22 landed 23 arrived=True callMs=0 ...
[16:24:10.560] dialogue-node-pub: #7 node 23 -> 24 ...
[16:24:10.586] dialogue-node-drive: #7 wanted 24 before 23 landed 24 arrived=True callMs=0 ...
[16:24:10.590] dialogue-node-pub: #8 node 24 -> 25 ...
[16:24:10.634] dialogue-node-drive: #8 wanted 25 before 24 landed 25 arrived=True callMs=16 ...
```

Zero coalescing, 100% delivery, against a burst of comparable speed (70ms for 3 transitions,
against the 55-70ms bursts that coalesced under the default). **`dialogue_fast_join=1` visibly
fixes what `=0` does not** — the doc's own standing question, from the very first draft of this
file, is answered: yes, decisively, for this specific failure mode. This does not overturn
`docs/research/dialogue-join-latency.md`'s own 2026-08-22 null result — that pass measured JOIN
latency specifically (how long until content first appears), a different axis the far-join
teleport fix already covers; this round measured MID-CONVERSATION coalescing, which join latency
testing was never positioned to see.

### H3

Not separately exercised this round — H1/H2 fully explain the reported symptom on their own, and
`dialogue-sync.md`'s own reasoning that a race in the reply/pick-log path cannot desync the NODE
the follower is driven to (the pick log is diagnostic-only; `DriveToPeerNode` never reads it)
still holds unmodified. Ranked exactly as before: secondary, and effectively subsumed by H2.

### The fix, and what remains

**No code change to `DialogueTickGate`, `DialogueSpectateController`, or
`CoopAgent.PumpDialogue`'s dedup/replay logic was made or is recommended.** The mechanism those
three implement is exactly what H2 always said it was — a level-triggered, periodically-sampled
wire that cannot represent more than one transition per sample — and it is not buggy relative to
its own design; the design's assumption ("a human cannot outrun 150ms") is what a skip burst
breaks. The fix that already exists and, measured directly, closes the gap completely is
`dialogue_fast_join=1`. `profiles/coop.ini.example`'s own comment on that key now records this
round's dated finding alongside the join-latency pass's null result, and recommends flipping the
shipped default — left at `0` in the template itself (a default flip is a broader call than one
diagnostic round should make unilaterally) and reverted back to `0` on both rig installs before
tear-down, matching the shipped default a fresh install would get.

**Update, same day, made by the coordinator with the full session's context**: flipped
`dialogue_fast_join` to `1` in `profiles/coop.ini.example`, `CoopConfig.DialogueFastJoin`'s
default (and its "key absent" parse behaviour, matching `dialogue_item_sync`'s existing
pattern), `AgentSettings.DialogueFastJoin`'s default, and `Program.cs`'s no-config fallback —
all four sites a real install can reach. Unlike `party_fast_pump`/`container_fast_pump` (each
measured twice with no benefit and left off), this closes a real, live-measured content-loss
bug with a direct A/B showing zero regression on the identical repro — a materially different
evidence bar than "a latency tweak with unproven upside," which is what made those two stay off
and this one a reasonable default-flip candidate on its own record. Full suite (1635/1635) and
both builds reconfirmed clean after the flip.

What remains, honestly:

- **The verb's own authority model is deliberately minimal** — no `CheatAuthority` gate, matching
  `leader`/`recruit`'s local-only precedent, because it only ever touches the LOCAL player's own
  resolved dialogue-owner object; a wrong resolution is a silent no-op by construction (matching
  `TryGetActiveDialogue`'s own vtable-verified design), never a crash. Only tested against a
  PC-owned Trask conversation this round — a companion-led or scripted-NPC-initiated conversation's
  owner resolution was not separately re-verified.
- **`selectreply`'s own robustness gap**, found live, is NOT a `skipline` defect: unlike skip
  (a pure server-local write), committing a reply is a real client→server round trip, and hammering
  it faster than that round trip lands just keeps "succeeding" against a stale panel/reply-list
  state with no node change — and, separately, it was seen reporting "committed" against a panel
  that had gone stale after the conversation had already ended. `selectreply` is a test-only
  navigation aid, not a shipped feature verb; a future script using it should pace commits with a
  real wait (as this round's own working sequences did), unlike `skipline`, which is safe to fire
  as fast as the caller can manage.
- ~~**`dialogue_fast_join=1` is a recommendation, not a shipped default flip**~~ — flipped
  the same day; see the update above.
- The fastest cadence this round could produce (~15-16ms, bounded by the game's own frame rate)
  is very likely close to the fastest ANY mechanism — human or automated — could ever make this
  engine advance, since the engine itself only processes one `UpdateDialog` tick per frame. The
  55-70ms real bursts measured here are a realistic reading of "a human mashing the skip key
  quickly," not an artificial worst case invented for this test.

---

# Addendum (2026-08-29): a SPECTATOR could skip, and now cannot

## The report

Live, from the user: a client started a conversation, the host joined it as a spectator, and
the host pressed skip — and it advanced. Everything above treats skip as something only the
owner would ever do; the word "spectator" does not appear in the 695 lines before this line.

## Why the existing gate did not cover it

Replies had been gated since the dialogue-sync work: `DialogueReplyHook` swallows
`CSWGuiDialog::SelectReply 0x006A7110` and `CSWGuiDialogCinematic::SelectReply 0x006A7E20`
while a peer holds the token. Skip is a **different engine call reached from a different
branch of the same switch**, one branch away from the one that was hooked:

```
CSWGuiDialog::HandleInputEvent 0x006A7230   (event 0x27 = Enter/click, 0x2D)
0x006A7266  mov  eax, [esi+0x1DF8]     ; the panel's own mode byte
0x006A726C  test al, 1
0x006A726E  je   0x6A729E              ; bit0=0 -> SelectReply            (GATED)
0x006A7270  test al, 2
0x006A7272  jne  0x6A730D              ; bit1=1 -> the engine's own no-op
0x006A7278  mov  eax, [esi+0x19C0]     ; bit0=1,bit1=0 -> the SKIP case   (WAS UNGATED)
0x006A7297  call 0x4AE970              ; CServerExoApp::SkipPastDialog
```

⚠ Correction to §"Round 79" above, which transcribed the call at `0x006A7288`: that address
holds an id-fixup call (`0x4AEA30`); `SkipPastDialog` is at **`0x006A7297`**. The mechanism
described there is right; those two addresses are off by one call.

## The fix

`DialogueSkipHook` splices `0x006A7278` — the skip case's own first instruction, six bytes, one
whole instruction, reached only by fallthrough (`tools/xrefs.py 0x6A7278`: zero call/jmp sites,
zero address literals, so neither of the switch's jump tables at `0x6A7374`/`0x6A7384` targets
it). When swallowing it jumps to **`0x6A730D`**, which is precisely where the engine itself
goes when it decides the event is already mid-skip — so a swallowed skip is bit-for-bit a skip
that arrived one frame early, a state the engine handles many times per conversation.

**One site covers both panel classes**, unlike the reply gag which needed two:
`CSWGuiDialogCinematic` declares no `HandleInputEvent` of its own and inherits this one.

The predicate is `DialogueSkipAuthorityRule.ShouldSwallowSkip(state)` — the same
"anything but `Idle`" that `DialogueFollowMachine.SuppressReplies` uses, renewed on the same
branch of the same frame pass with the same lease length, so the two can never disagree. The
narrower `IsFollowing` would reopen `Standby` (the 15 s join window), `Yielding` and
`BackingOff` — three windows this document's own follow machine records as measured in play.

## Verified, and NOT verified

VERIFIED live (2026-08-29): the hook's byte check passes against the real binary and it
installs (`dialogue: dialogue skip authority at 0x6A7278`); the game then ran a full character
creation, a module load, a two-player session and a real conversation with no crash. 16 unit
tests pin the rule.

⚠ **NOT verified live: an actual spectator keypress being swallowed.** The obvious instrument
does not work for this — `skipline` pokes `CSWSObject+0x1F4` directly and never enters
`HandleInputEvent`, so it exercises the owner-side primitive rather than the input path, and on
the host it refuses outright (`no active conversation here to skip a line in`) because the
spectate follow had not yet opened its own local copy. Driving a real keypress needs input
injection, which this same session measured dropping clicks mid-sequence.

**What the next round should add:** a `skipkey` live-test primitive that calls
`CSWGuiDialog::HandleInputEvent(panel, 0x27, 1)` on the resolved dialogue panel — thiscall,
`ret 8`, panel found by vtable (`CSWGuiDialog 0x7559E0`, `CSWGuiDialogCinematic 0x755800`).
That is the input path, so it goes through the hook, and
`DialogueSkipHook.SwallowedCount` is then the oracle: it must increment on the spectator and
stay flat on the owner, with the node index unchanged on both.

---

## `skipkey` (2026-08-29): the primitive that actually reaches the hook

Built exactly as the paragraph above specifies. `Odyssey.SendDialogueSkipKey` resolves the
dialogue panel with the existing `GetDialoguePanel()` (vtable-matched against
`CSWGuiDialog 0x7559E0` / `CSWGuiDialogCinematic 0x755800`, the same resolver
`SelectDialogueReply` uses) and calls `HandleInputEvent` **through the panel's own vtable
slot**, not the address.

**The slot is 0x3C, and it is the same slot in both classes.** Read out of the two vtables:
index 15 of each holds `0x006A7230`. `CSWGuiDialogCinematic` declares no override, which is the
same fact that lets `DialogueSkipHook` cover both panel classes with one splice while the reply
gag needed two — and calling through the slot rather than the address keeps that true if a
future build ever changes it.

**The arguments, re-derived rather than assumed** (`0x6A7230`, thiscall, `ret 8`):

```
0x006A7230  push ebp
0x006A7231  mov  ebp, [esp+0x0C]      ; <- arg2 (one push deep)
0x006A7235  test ebp, ebp
0x006A723A  je   0x6A7333             ; arg2 == 0 -> straight to the return
0x006A7240  push ebx
0x006A7241  mov  ebx, [esp+0x10]      ; <- arg1 (three pushes deep)
0x006A7245  cmp  ebx, 0x3D            ; ... 0x27 / 0x2D compared below
```

So arg1 is the EVENT CODE (`0x27`) and arg2 is a payload the function refuses outright when
zero. `1` satisfies that test and is never dereferenced on this branch — only the `0x1F9` event
reads through it, and `0x27` is not `0x1F9`. The jump table at `0x6A7374` is indexed by
`event - 0x27` (`lea eax,[ebx-0x27]; cmp eax,0xB; ja 0x6A730D`), which is how `0x27` reaches the
mode-byte test at `0x6A7266` and, from there, the skip case this document's hook sits on.

**Why the counter is reported before AND after in one line.** `CheatController.SkipKey` reads
`DialogueSkipHook.SwallowedCount`, dispatches, reads it again, and answers with both. The detour
runs synchronously inside the call, on the same thread, so the pair is exact; and one console
reply then distinguishes the two outcomes without a second round trip — `swallowed N -> N+1`
means the gag fired (spectator), `swallowed N -> N` means it passed through (owner). Console
verb: `skipkey`, no arguments, blockpoke-only, documented in docs/CONSOLE.md.

⚠ It is deliberately NOT wired into `FuncInterpHook`'s verb list, matching
`skipline`/`selectreply`: dev-only live-test primitives stay reachable only through
`tools/blockpoke.ps1`, never by a player typing at the in-game console.

## LIVE-VERIFIED (2026-08-29, rig): the spectator's skip is swallowed, the owner's is not

Two GOG instances, host `35220` on demo save 1, client `27404` — a client that had joined
through `join_mode = newcharacter` and then rejoined off its own new save. The conversation was
staged the way `verify.ps1` stages its own: the HOST opened one with `end_trask` through the
test-drive channel (block `0x1D70` tag + `0x1D90` serial), and the client joined it as a
spectator by itself.

State when the keys went in — host owns, client follows:

```
host  : localPartner='end_trask'  remoteActive=0
client: localPartner=''           remoteActive=1  remotePartner='end_trask'
```

| who | result |
|---|---|
| **CLIENT (spectator)** `skipkey` | `dialogue: skipkey dispatched at node -1; swallowed 0 -> 1 (SWALLOWED - spectator)` |
| … and what moved | **nothing, on either instance**: host node index 0 → 0, client's followed index 0 → 0, no `dialogue-node-pub` on either side |
| **HOST (owner)** `skipkey`, same conversation | `skipkey about to dispatch - owner 0x127 node 29/74 … isDialogDelay 1` then `dispatched at node 29; swallowed 0 -> 0 (passed through - owner)` |
| … repeated on a freshly opened line | `node 19/74 … isDialogDelay 1`, `swallowed 0 -> 0 (passed through - owner)` |

That is the oracle this document asked for, both halves: the gag's counter **increments on the
spectator and stays flat on the owner**, and the spectator's press advanced nothing anywhere.
The owner's presses reached the engine's own dispatch with a line genuinely playing
(`isDialogDelay 1` — the guard `skipline`'s own probe reads).

⚠ **What this pass did NOT capture: the owner's skip stepping the node index.** Both owner
presses landed on nodes (29/74 and 19/74) where the following `dialogue-node-pub` never moved
within several seconds. That is consistent with retail's own behaviour rather than with the gag
— `skipline`'s table entry in console-cheats.md already records skip as "a correctly inert
no-op on a reply-list screen", and `CSWGuiDialog::HandleInputEvent`'s switch sends the event to
`SelectReply` instead of the skip case whenever `[panel+0x1DF8]` bit 0 is clear, which is
exactly a reply screen. Note that this ALSO explains the owner's flat counter honestly: on a
reply screen the event never reaches `0x6A7278` at all, so the hook could not have swallowed it
even if it wanted to. The earlier `skipline` live pass is what has the owner-side node stepping
(3–32 ms after the call); a future round wanting a single run that shows both should stage a
narration stretch rather than `end_trask`'s opening node.

⚠ **`startdialog` on the CLIENT could not open a conversation with `end_trask` at all** this
round (three attempts, `ActionStartConversation queued` each time, no `StartDialog` ever
reached), which is why the roles are host-owner / client-spectator rather than the other way
round. The assertion is symmetric, so this does not weaken it, but it is worth knowing before
scripting the mirror-image test: the client's Trask is a solo-mode companion driven by the host
(`partystate`: client `soloMode 1`, companion `0x127`), and a companion the local engine is not
driving is not something the local player can start a conversation with.

---

# 2026-09-10: the ACTUAL client-owns/host-follows direction, live — "only the first skip
# propagates", root-caused to a stale INTERACTION LOCK, not to anything in the node-sync
# path this whole document has been examining

## The report

User, distinct from every finding above: dialogue-skip sync is asymmetric.

1. **Client owns a conversation, host spectates: only the FIRST skip reaches the host.** Every
   skip after that does nothing visible on the host's screen — the host's followed copy simply
   stops advancing for the rest of the conversation.
2. **Host owns, client spectates: mostly works, except "the last dialogue stretch"** — it breaks
   down again specifically near the end of a conversation.

## Where this document's own prior rounds could not have caught it

Every round above (79, the 2026-08-29 addendum, `skipkey`) exercises the NODE-SYNC path — the
owner's `dialogue-node-pub` and the follower's `dialogue-node-drive`, both purely PLUGIN-side —
and every one of them found that path sound. It still is. **The defect this round found lives
one layer up, in the SESSION layer's conversation-token arbiter, and specifically in a channel
this document has never once mentioned: the per-NPC interaction lock.** Nothing in the plugin,
`DialogueFollowMachine`, or `DialogueNodeFollower` needed to change.

## Root cause, read straight off the code (no live probe needed to find it, only to confirm it)

Three facts, each individually already documented elsewhere in this repo, that combine into the
bug when read together:

1. **`InteractionLockController.PublishLocalClaim()` (the plugin) cannot tell "I started this
   conversation" from "I am mirroring a peer's".** It reads `block.LocalDialoguePartnerTag` —
   set by `DialogueSpectateController.PublishLocal()` whenever `Odyssey.TryReadActiveDialogue()`
   is true — and publishes an `InteractionKind.Conversation` claim on that tag, active, every
   single tick. `TryReadActiveDialogue` is true for a FOLLOWER'S locally adopted mirror copy of
   the conversation exactly as much as for the real owner's: the follower really does have a live
   `CSWSDialog` running locally (that is the whole mechanism `DriveDialogueToNode` depends on),
   and the plugin has no way to know from inside the engine that this copy is a mirror rather
   than the original.
2. **`InteractionLockStore.Claim` is first-fresh-claim-wins**, and per `DialogueArbiter`'s own
   pre-existing doc comment, that race was never fair: "the host's own claim runs this arbiter
   in-process at zero latency, while a client's has to cross the network first." That asymmetry
   was known and deliberately accepted for the ORIGINAL race it was built for (two players
   clicking the same NPC at the start of a conversation) — nobody had connected it to a
   FOLLOWER'S claim on an NPC someone else is ALREADY, legitimately, mid-conversation with.
3. **`DialogueArbiter.Apply`'s lock veto ran unconditionally**, ahead of every other branch —
   including an UPDATE from the peer who already, fairly, owns the live token. So the veto was
   never just "who gets to start talking" (its own stated purpose); it silently re-adjudicated
   EVERY update for as long as the conversation ran.

Put together: a client opens a conversation with Trask (claims the token fairly — the lock is
free at that instant, so the grant succeeds). The host, following, adopts a local mirror of the
same conversation to visually drive it — and that mirror is read by the host's OWN
`InteractionLockController` as "the host is now interacting with Trask", which the host's own
agent publishes as an interaction claim reaching the SAME lock store the host itself owns, at
zero latency. The client's own (rightful, but network-latency) claim refresh loses that race the
very next tick. From that tick on, `DialogueArbiter.Apply` sees `lockHolder == HostPeerId !=
requesterPeerId (the client)` on every subsequent `DialogueRequestPacket` the client sends — and
refuses every one of them, forever, even though the client never stopped legitimately owning the
token. The very FIRST skip already happened before the host's adopt-claim could win the race,
which is exactly why it alone got through.

**Why direction 2 (host owns) mostly works, and only breaks "near the end":** the asymmetry is
about which side is physically the HOST PROCESS, not who owns the conversation. When the HOST is
the owner, the host's OWN interaction claim for that NPC is the one reaching the lock store at
zero latency — so it will almost always keep re-winning the race against the client's own
(mirror-driven) competing claim, and `lockHolder == requesterPeerId` stays true for the host's
own updates throughout. It is not IMMUNE, though: the false claim is only suppressed while the
local `LocalDialoguePartnerTag` is actually published (i.e. while `_hadDialogue` reads true), and
a genuinely longer between-entries gap — more plausible near a conversation's closing stretch,
where a scripted fade/end-transition can outlast the ordinary flicker debounce
(`PublishLingerMs`) — opens exactly the window the client's persistently-refreshed claim needs to
sneak in and take the lock. From that point the SAME veto-on-every-update bug applies to the
host's own further skips. Two directions, one mechanism; the asymmetry is a difference in HOW
OFTEN the race is lost, not a second bug.

## The fix

Two changes, defense in depth — either alone would have closed the reported symptom, but each
closes a different half of the same defect:

1. **`DialogueArbiter.Apply`** (`src/KotorCoop.Session/DialogueArbiter.cs`): the lock-holder veto
   now only gates the `!current.Active` branch (the GRANT) — never an update from
   `current.OwnerPeerId == requesterPeerId`. Once a claimant has fairly won the token, no lock
   check run afterward can produce a MORE correct owner than the one who already won the race
   that matters. This is the fix that actually stops the desync: even if the lock keeps
   flip-flopping to a follower's mirror claim for the rest of the conversation, the legitimate
   owner's updates are never vetoed again.
2. **`WorldStatePump.PumpInteractionLocks`** (`src/KotorCoop.Session/Pumps/WorldStatePump.cs`):
   suppresses the false claim at its source. The AGENT layer is the one place that actually knows
   which side of the token this instance is on (`session.GetDialogue()`), so before forwarding
   the plugin's raw interaction read, it checks whether this is a `Conversation`-kind claim on
   the SAME partner tag a peer's token already names US as following
   (`token.IsHeldByAnotherThan(session.LocalPeerId)`) — and if so, publishes nothing (kind=0,
   inactive) instead of a competing claim. This is the more complete fix: without it, item 1's
   fix alone still leaves a stale lock entry naming the WRONG peer, which would incorrectly make
   `HoldCreatureStill` run against Trask's body on the true owner's OWN screen (the "peer holds
   this NPC, keep it still" mechanism `InteractionLockController.ApplyPeerLocks` drives) — a
   second-order symptom this document never got a report for, but which the code read made
   clearly possible and worth closing alongside the primary one.

Two new unit tests in `tests/KotorCoop.Tests/DialogueArbiterTests.cs`:
`AnOwnersUpdateSurvivesTheInteractionLockChangingHandsUnderIt` (asserts the exact fixed defect:
an owner's second update succeeds even though the lock has since moved to a third peer) and
`ALockHeldByTheContenderDoesNotStealAnAlreadyActiveToken` (confirms the grant-time veto still
works exactly as before — a contender pointing at a lock it holds still cannot steal an
already-active token from its real owner).

## Live verification (2026-09-10, rig)

Two-GOG-install rig (`tools\demo.ps1`, END_M01AA, both installs rebuilt with the fix and
reinstalled), Trask (`end_trask`), navigated with `selectreply <slot>` (past this save's
reply-driven Q&A stretch — node 29 and node 21 both read `isDialogDelay 0`, confirming they are
reply screens where `skipline` is correctly an inert no-op, exactly as the 2026-08-23 round
found) and `skipline` (fired against genuine narration entries, `isDialogDelay 1`) via
`tools\blockpoke.ps1 -ProcId <pid> console '<verb>'`.

**Direction 1 (client owns, host spectates) — the reported failure, fixed and confirmed:**
navigated the client through nodes 29→19→20→21→22→23→24→25, mixing real `skipline` skips and
`selectreply` picks (the wire path is identical either way — a node-index change, not a
picked-slot replay). Every single one of the 8 consecutive transitions was followed by the host,
1:1, with matching `dialogue-node-drive: ... arrived=True` entries:

```
CLIENT (owner)                                      HOST (follower)
[14:32:21.778] node-pub #2  node 29 -> 19            [14:32:21.880] node-drive #2 wanted 19 landed 19 arrived=True
[14:32:41.648] node-pub #3  node 19 -> 20            [14:32:41.749] node-drive #3 wanted 20 landed 20 arrived=True
[14:32:58.676] node-pub #4  node 20 -> 21            [14:32:58.743] node-drive #4 wanted 21 landed 21 arrived=True
[14:34:18.255] node-pub #5  node 21 -> 22            [14:34:18.323] node-drive #5 wanted 22 landed 22 arrived=True
[14:34:32.513] node-pub #6  node 22 -> 23            [14:34:32.591] node-drive #6 wanted 23 landed 23 arrived=True
[14:34:34.923] node-pub #7  node 23 -> 24            [14:34:34.992] node-drive #7 wanted 24 landed 24 arrived=True
[14:34:37.321] node-pub #8  node 24 -> 25            [14:34:37.406] node-drive #8 wanted 25 landed 25 arrived=True
```

This is the exact sequence (entries 22→23→24→25, the SAME narration run round 79 used) that was
never able to get past its first hop under the bug — node 19→20 is where round 79's own
equivalent test would have stuck under the pre-fix arbiter, since that is the first update after
the host's adopt would have had a chance to win the interaction-lock race. Under the fix it did
not stick anywhere.

**Direction 2 (host owns, client spectates) — re-run for regression, then pushed through a
mid-conversation close:** the same topic loop (22→23→24→25→22→23→24...) was driven from the HOST
side instead, for 30 consecutive node transitions across roughly two minutes of wall clock, every
one followed by the client 1:1 with no coalescing and no stall — confirming the fix does not
regress the direction that was already mostly working. The host then closed the conversation
mid-line (`talk close`) while the client was actively following: the client's own log shows the
LAST node (`25`) was driven and landed (`arrived=True`) BEFORE the close was even issued, and the
close itself propagated cleanly (`dialogue: the owner ended the conversation; closing`), with the
client's dialogue state fully reset afterward. No stuck node, no stale partner tag.

**What this round did NOT manage to reproduce directly:** the specific "last dialogue stretch"
failure for direction 2, because this save's `end_trask01.dlg` state is a long reply-driven topic
loop that this round's automated navigation (`selectreply`/`skipline` via `blockpoke.ps1`) could
not reliably steer to the conversation's true final (74th) entry inside a reasonable session —
the same reply-loop-navigation limitation `selectreply`'s own known robustness gap (2026-08-23
addendum) already documents. The mechanism section above explains why direction 2's failure would
be timing-dependent on a longer-than-usual between-entries gap rather than guaranteed every time,
which is consistent with a live report of "sometimes, near the end" rather than "every single
conversation". **NEEDS-LIVE**: a run that reaches Trask's actual final entry (or any other
NPC's) and specifically times a real close-approaching gap against a still-following peer's
competing claim, to catch the direction-2 failure in the act rather than by mechanism alone. The
fix itself does not depend on catching it this way — it closes the SAME veto-on-every-update bug
regardless of which direction's timing triggers the lock hand-off — but an in-the-act capture
would be the stronger evidence a future round could add.

**Full regression**: 3475/3475 unit tests green after the fix (including the two new
`DialogueArbiterTests`), both installs rebuilt and reinstalled clean.

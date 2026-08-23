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

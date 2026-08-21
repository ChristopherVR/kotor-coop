# Why a joiner's subtitles lag, and the fix (2026-08-21)

Read `docs/research/dialogue-sync.md` first — this builds on its node-sync design (position
replay, not reply-slot replay; `DialogueArbiter`'s single token; `CSWSDialog::SendDialogEntry
0x005A4010`) and its own measured numbers ("on the same entry 15/15", the 531 ms forced-join
figure, the "delivered content 0 ms after opening, 15 ms after the request" measurement from a
NEARBY pair of players). This document is about the case that measurement did not cover: a
joiner who is not already standing next to the owner when the conversation starts, and the
fixed, agent-side latency every join pays regardless of distance.

## The report

*One player starts a conversation, the other joins (click-to-join or adopt), and on the
joiner's screen the subtitles and reply options do not appear for a few seconds; the cinematic
letterbox bars creep in slowly.*

The letterbox itself is the engine's own dialogue-mode camera transition — it runs identically
for the owner and is not something this mod drives or can speed up. The bug is that the first
LINE takes longer to arrive than the transition takes to finish, so the player watches the bars
close over an empty box.

## The timeline, built from the code, with the actual numbers

Every hop between "the owner's node changes" and "the joiner's screen shows it", before this
session's fix:

| # | Hop | Where | Worst case |
|---|-----|-------|-----------:|
| 1 | Engine advances to a new entry | `swkotor.exe`, frame-driven | 0 ms (already happened) |
| 2 | Plugin's frame hook writes the node into the shared block | `DialogueSpectateController.PublishLocal` on `SwapBuffers`, ~300 Hz | ~3 ms |
| 3 | Owner's agent reads the block and stages it for the wire | `CoopAgent.PumpDialogue`, gated behind `CombatPumpIntervalMs` | **150 ms** |
| 4 | Owner's client sends the staged fields (client only; the host applies its own publish immediately) | `CoopClient.Tick → SendDialogueRequest`, `TickRate` = 30 Hz | 33 ms |
| 5 | Host rebroadcasts the token to every peer | `CoopHost.Tick → BroadcastDialogue`, `TickRate` = 30 Hz, **unconditional every tick** | 33 ms |
| 6 | Joiner's agent reads the token and writes it into its own shared block | `CoopAgent.PumpDialogue`, gated behind `CombatPumpIntervalMs` | **150 ms** |
| 7 | Joiner's plugin frame hook reads the block and drives the node | `DialogueSpectateController.FollowRemote → DriveToPeerNode`, ~300 Hz | ~3 ms |
| 8 | Engine's own one-second audio gag on the FIRST entry after a conversation opens (`CanSendDialog`, `CClientExoAppInternal+0x228`) | `swkotor.exe`, engine-internal, cannot be safely bypassed (`Odyssey.SetCanSendDialog`'s own doc: "DO NOT CALL — NOT EVEN WITH true") | up to 1000 ms, first line only |

Worst case for the controllable hops (2–7): `3 + 150 + 33 + 33 + 150 + 3 = 372 ms`. Add the
engine's own up-to-1000 ms audio gag on the very first line after `StartDialog`, which this mod
does not control and must not try to bypass (see `Odyssey.SetCanSendDialog`'s own warning), and
the worst case for the FIRST line a joiner sees is **up to ~1.37 s** — before any real network
latency, jitter, or a joiner who has to be walked into range at all (`FollowOpenTimeoutMs` /
`LocalOpenGraceMs` = 15 s, unrelated to this fix and already generous by design).

Lines #3 and #6 are the two hops entirely under this mod's control, and they are the SAME gate
(`CombatPumpIntervalMs`, 150 ms) hit twice — once on the owner's publish, once on the joiner's
read. `CoopAgent.cs`'s own prior comment on this code ("Dialogue rides here too... at this
cadence it is a few hundred milliseconds") already documents that this was tightened once,
from a full second (1 Hz) down to 150 ms, and correctly predicted the residual cost in exactly
those words.

## What was checked and found NOT to be a bottleneck

The task brief listed several suspects. Each was checked against the current code, not assumed:

* **"Node replay limited to one node per frame/tick."** False. `DialogueSpectateController`
  publishes only the owner's CURRENT node (never a history), and
  `Odyssey.DriveDialogueToNode(entryIndex)` calls `CSWSDialog::SendDialogEntry` directly with
  the TARGET index — there is no walk through intermediate entries to remove. The doc's own
  live measurement already proves this: `dialogue: followed the owner 19 -> 29` landed in one
  call. `DialogueNodeFollowerTests.ALargeCatchUpJumpLandsInOneCallJustLikeASmallOne` (new, this
  session) makes that a checked invariant rather than an observation in a log.
* **"Waiting for a block serial to round-trip before replaying."** Not found. `DriveToPeerNode`
  banks the owner's serial only after a RE-READ shows the engine actually landed on the node
  (`DialogueNodeFollower.Arrived`) — this is a correctness rule (a successful call is not an
  arrival — see that type's own doc), not an extra round trip. It runs every plugin frame
  (~300 Hz) regardless.
* **"The adopt handshake requiring several ticks."** `DialogueFollowMachine.StepFollow`
  resolves `Adopting → Opening/Following` on the very next `Step` call — two plugin frames,
  ~6 ms at ~300 Hz. Not worth budget against a 372 ms chain.
* **"The dialogue packet riding the slow world-snapshot cadence instead of an immediate
  send."** False on both ends. `CoopClient.Tick` sends `DialogueRequestPacket` every network
  tick (30 Hz) unconditionally, and `CoopHost.Tick → BroadcastDialogue` sends the token to every
  peer every tick too, deliberately unconditional — its own comment: *"Broadcasts the
  conversation token every tick, unconditionally (even when idle)... One tiny packet per tick
  is negligible."* Neither side was found riding a slower "heavy world state" cadence. The real
  bottleneck was one layer further in: the AGENT's own read/write of the dialogue channel
  (hops #3 and #6 above), not the network tick.
* **"The joiner's StartDialog issued only after N confirmations."** False. A click-to-join
  calls the engine's `StartDialog` directly, gated only by `DialogueGateHook`, which explicitly
  fails OPEN while the peer's partner tag is still unknown (`dialogue-sync.md`'s own "the gate
  only closes once we know who the peer is talking to" section) — so a click is never held up
  waiting for network confirmation. An "adopt" (both players trigger the same conversation
  independently) has its `StartDialog` fired by the ENGINE'S OWN script trigger at the same
  moment as the owner's, not by anything this mod schedules. In both cases `StartDialog` — and
  therefore the letterbox — already fires at the earliest possible moment given the current
  design; there was no artificial delay to remove here.

So the fix is narrow and precisely targeted: hops #3 and #6, the two places `CoopAgent`'s own
~150 ms cadence sits between an otherwise-fast chain.

## The fix

`coop.ini dialogue_fast_join=1` (default OFF, so a live A/B pass can compare it against the
existing cadence before it becomes the only path):

* `CoopAgent.PumpOnce` calls `PumpDialogue` on the real-time (every poll tick, `PollRate` —
  30 Hz by default, same as the position write) path instead of the `CombatPumpIntervalMs`
  path, on BOTH the owner's publish and the joiner's read — closing hops #3 and #6 above from
  150 ms to ~33 ms each, a worst-case saving of up to `(150 − 33) × 2 = 234 ms` off the
  controllable chain (372 ms → ~139 ms).
* `KotorCoop.Protocol.DialogueTickGate` (new) makes "every tick" not mean "republish the
  identical payload thirty times a second for the length of every conversation": a CHANGE (a
  new line, a new node, the conversation starting/ending, or the peer's token moving) fires the
  very next tick — event-driven, not only on cadence — while an unchanged read is deduped
  against a 150 ms heartbeat, which still forces a periodic resend so a late joiner or a lost
  UDP datagram converges in bounded time exactly the way `CoopHost.BroadcastDialogue`'s own
  "unconditional every tick" design already argues for one layer up. It is a small, pure,
  fully-tested type (`DialogueTickGateTests.cs`, 8 tests) that knows nothing about dialogue,
  sockets, or the game — only a fingerprint and two timestamps — so the decision is checked
  without a live game or a 150 ms sleep in a test.
* Turning the flag OFF is a literal rollback, not an approximation of one: `CoopAgent.PumpOnce`
  simply calls `PumpDialogue` from inside the original `CombatPumpIntervalMs`-gated block
  exactly as before, and `PumpDialogue` never constructs or consults the tick gate.

What did **not** change, deliberately: the node-sync design (position replay, one call per
drive, no walk), the network send cadence (already unconditional per-tick on both ends), the
adopt handshake, the click-to-join gate's fail-open behaviour, and the engine's own
`CanSendDialog` audio gag on the first line (touching that is explicitly documented elsewhere
as unsafe — see `Odyssey.SetCanSendDialog`).

### Files touched

* `src/KotorCoop.Protocol/DialogueTickGate.cs` — new. The pure gate.
* `src/KotorCoop.Session/CoopAgent.cs` — `AgentSettings.DialogueFastJoin`; `PumpOnce` calls
  `PumpDialogue` on the fast path when set; `PumpDialogue` builds a fingerprint (active state,
  speaker/line, held partner tag, local reply/node serials, whether following, and the peer
  token's own serials) and consults the gate before publishing, so a change on EITHER side
  (local or the peer's token) is acted on immediately.
* `src/KotorCoop.Session/CoopConfig.cs` — `DialogueFastJoin` property, `dialogue_fast_join` key.
* `src/KotorCoop.Agent/Program.cs` — maps the ini key into `AgentSettings`.
* `profiles/coop.ini.example` — documents the key, default off.
* `tests/KotorCoop.Tests/DialogueTickGateTests.cs` — new, 8 tests: first-call-always-fires,
  change fires immediately, unchanged does not fire early, heartbeat still fires, the heartbeat
  clock resets from the last FIRE not the last ask, a change also resets the heartbeat clock,
  `Reset()` behaves like a fresh instance, and rapid oscillation fires on every flip.
* `tests/KotorCoop.Tests/DialogueFastJoinPumpTests.cs` — new, 4 tests at the `CoopAgent` layer:
  flag off stays on the legacy 150 ms cadence (a change made inside that window does not reach
  `PublishDialogue`); flag on publishes a changed line on the very next `PumpOnce()`; flag on
  does not republish an unchanged line on repeated ticks; a node-only change (no text change)
  still counts as a change.
* `tests/KotorCoop.Tests/DialogueNodeFollowerTests.cs` — one new test,
  `ALargeCatchUpJumpLandsInOneCallJustLikeASmallOne`, documenting and checking the "no per-node
  walk" finding above.

All 1222 tests pass (`dotnet test tests/KotorCoop.Tests/KotorCoop.Tests.csproj`), up from the
~1209 baseline. Both `dotnet build KotorCoop.slnx` and `pwsh src\KotorCoop.Plugin\build.ps1`
succeed; no plugin (native x86) source changed, because the bottleneck found here was entirely
on the managed agent side — the plugin's own per-frame work (hops #2 and #7 above) was already
~300 Hz and never the problem.

## Live-test script for another agent

The live rig was in use by another agent during this session, so none of this was run live.
What follows is exact enough to run unattended.

### Setup

1. Two GOG installs, per `kotor-coop-gog-is-primary-target.md` — e.g.
   `C:\GOG Games\Star Wars KOTOR (GOG)` (host) and `D:\Games\swkotor-gog-p2` (client).
2. Build and deploy the plugin to BOTH installs (`demo.ps1` does not deploy the plugin by
   itself — `kotor-coop-demo-does-not-deploy-plugin.md`):
   ```
   pwsh src\KotorCoop.Plugin\build.ps1 -Install
   ```
3. On BOTH installs' `coop.ini`, set `dialogue_fast_join=0` for the baseline pass. (Leave
   everything else at its default — in particular `dialogue_exclusive=1`, the default, must
   stay on for the one-conversation-at-a-time invariant this whole feature depends on.)
4. Launch both instances via `tools\demo.ps1` (fully unattended —
   `kotor-coop-autonomous-live-testing.md`) and get both players into `END_M01AA` on the same
   save, standing near Trask (`end_trask` — this doc's own established standard NPC, chosen
   because its END_M01AA placement is what `dlgprobe.ps1` already targets by default).
5. Confirm the session is up with `Get-Process swkotor` (two processes) before proceeding —
   `kotor-coop-live-session-checks.md`.

### Baseline pass (`dialogue_fast_join=0`)

```
pwsh tools\dlgjoinlatency.ps1 -Tag end_trask -Seconds 20
```

Repeat 3–5 times (a fresh `talk close` / re-approach between runs if `dlgprobe.ps1`'s own
output shows the conversation did not end cleanly). Also run once with `-FromClient` to cover
the host-joins-a-client-owned-conversation direction (`kotor-coop-client-server-halves.md`'s
two state machines make this a genuinely different code path on the joining side).

Record, per run:
* `owner StartDialog -> joiner StartDialog (GUI open)` — the "letterbox begins" moment.
* `owner StartDialog -> joiner's first driven node` — the "a real line could be on screen"
  moment; this is the number the user's report is actually about.
* The self-reported `joiner content latency` line (`... ms after opening, ... ms after the
  request`) — the most clock-skew-robust of the three, since it is one instance timing itself.
* `dlgprobe.ps1`'s own printed verdicts: `serial movement over N s` (should read low churn,
  matching the existing "settled, not churning" bar this feature already had to clear) and
  `NODES: on the same entry for X of Y paired samples` — the closest thing to a "node lag
  histogram" this rig can produce without new instrumentation: at `dlgprobe.ps1`'s 1 Hz sample
  rate, a run that is NOT `X == Y` names the exact seconds (`t=...`) the two screens disagreed,
  which is the histogram's bucket boundaries even though the sampling itself is coarser than a
  millisecond.

### Fast-join pass (`dialogue_fast_join=1`)

Set `dialogue_fast_join=1` on BOTH installs' `coop.ini`, restart both instances (coop.ini is
read at agent startup — no live-reload), and repeat the exact same runs:

```
pwsh tools\dlgjoinlatency.ps1 -Tag end_trask -Seconds 20
pwsh tools\dlgjoinlatency.ps1 -Tag end_trask -Seconds 20 -FromClient
```

### What "pass" looks like

* The two `owner StartDialog -> ...` metrics and the self-reported content-latency metric
  should each be lower on the `dialogue_fast_join=1` runs than the `=0` runs — the predicted
  shape is roughly `234 ms` less on the controllable chain (see the timeline table above),
  though live network/scheduler jitter means "clearly and consistently lower across 3–5 runs"
  is the right bar, not an exact number.
* `dlgprobe.ps1`'s own verdicts must be UNCHANGED by the flag: `serial movement` should still
  read as settled (not churning), and `NODES: on the same entry for X of Y` should still be
  `X == Y` (or as close as the baseline already was) — `dialogue_fast_join` must not regress
  the one-conversation-at-a-time or node-agreement invariants this whole feature already
  depends on. A regression here is a stop-ship finding regardless of the latency numbers.
* If a run's `NOT OBSERVED` lines appear, it usually means the join did not happen inside
  `-Seconds` (increase it) or the players were not close enough for the server-side walk to
  finish (`FollowOpenTimeoutMs` = 15 s covers this normally; `dlgjoinlatency.ps1` inherits
  `dlgprobe.ps1`'s own `-Seconds` window, so raise both together).

### If the numbers do not move

The single most likely explanation, checked first: confirm `dialogue_fast_join=1` actually
reached `AgentSettings` — `CoopAgent`'s console/log output does not currently print the
dialogue-fast-join state the way it prints `tuning: N Hz, M ms interpolation (from coop.ini)`
for the tick rate and interpolation keys, so there is nothing to eyeball at startup today. The
fastest live check is `blockpoke.ps1`'s existing `dialogue` verb (reads the shared block's
dialogue fields directly) sampled a few times in quick succession during a held conversation:
under `=1` a changed node/line should show up on the very next sample; under `=0` it should lag
by up to ~150 ms, matching the two passes' predicted difference.

---

## Live A/B (2026-08-21)

Run for real, on the documented rig: two GOG installs
(`C:\GOG Games\Star Wars KOTOR (GOG)`, `D:\Games\swkotor-gog-p2`), both plugin and agent
rebuilt and deployed (`src\KotorCoop.Plugin\build.ps1 -Install` against both `-GameDir`s; block
version 47 / protocol 33 confirmed on both), `tools\demo.ps1` cold-launched onto `END_M01AA`
near `end_trask`. 12 `dlgjoinlatency.ps1` runs total (5 baseline + 2 failed-to-open baseline
retries, 5 fast-join), plus manual `blockpoke.ps1`/`sendkey.ps1` probes for the rough edges and
`shot.ps1` screenshots. Full row data in `tools\dlgjoinlatency.csv`.

**Tooling bug found and fixed first**: `dlgjoinlatency.ps1`'s `ParseTimestamp` regex assumed
log lines with no leading character (`^\s*(\d{2}):...`), but `kotorcoop-plugin.log` lines are
bracketed (`[22:31:43.714] dialogue: ...`) — every run before the fix printed `NOT OBSERVED` for
both millisecond metrics. Fixed by allowing an optional `\[` before the digits
(`tools\dlgjoinlatency.ps1`). This is a pure test-tool fix; nothing under `src/` changed for it.

### The numbers

| Metric | Baseline (`=0`, n=5) | Fast-join (`=1`, n=5) |
|---|---:|---:|
| owner StartDialog → joiner StartDialog (GUI open) | 5033–5103 ms, avg ~5065 ms | 5030–5061 ms, avg ~5046 ms |
| owner StartDialog → joiner's first driven node | 20179–21303 ms (4 of 5); 1 outlier at 5057 ms | 20023–20599 ms (4 of 5); 1 outlier at 5057 ms |
| joiner content latency (self-reported, `ReportContentLatency`) | **0 ms after opening, 0 ms after the request — 5/5 runs** | **0 ms after opening, 0 ms after the request — 5/5 runs** |
| `dlgprobe.ps1` node agreement | 7/7, 13/15, 15/15, 14/15, 14/15 | 7/7, 14/15, 15/15, 15/15, 14/15 |

Every disagreement in the node-agreement column landed at the same two samples (`t=18`,
`t=19`, the last two seconds of a 20 s window) under BOTH flag settings, and every one resolved
itself (the two screens were never left apart at the end of a run) — the shape is identical
between the two arms, not a regression introduced by either.

### Why the two headline metrics move in lock-step with the flag, and what that means

The **GUI-open** gap (~5.05 s both arms) is dominated by `dlgprobe.ps1`'s own `-JoinAfter 4`
scripted wait before it pokes the joiner's test-open, plus ~1 s of PowerShell subprocess
start-up on top — a harness constant, not a measurement of anything this mod controls. It
cannot move between arms because nothing about `dialogue_fast_join` touches it.

The **first driven node** gap (~20.1–20.6 s in 8 of 10 runs) is `dlgprobe.ps1`'s own
`-PickAfter 10` reply key, sent via `sendkey.ps1`'s `SendInput`, racing the joiner's own
just-opened dialogue window for OS foreground focus — both `swkotor` windows are on screen at
once in this rig. A follow-up manual probe (no joiner window contending for focus) sent the
same key directly: the host's own node advanced in **under 1 s** on one run and in **3.5 s**
on another, under the SAME `dialogue_fast_join` value — i.e. this number's own variance,
uncontended, is larger than anything separating the two arms' medians. This is the OWNER's own
local reply-to-next-entry latency (vanilla engine pacing against the line's audio,
`IsDialogDelay` in `dialogue-sync.md`'s own terms) — it has nothing to do with the
follower's read path `dialogue_fast_join` changes, and both arms pay it identically. The 1-in-5
outlier at 5057 ms in each arm (run where the engine happened to pick the owner's OWN starting
entry past the joiner's local one, so the adopt's own catch-up jump WAS the first "followed the
owner" line, with no reply-key wait in between) confirms the outlier is about which starting
entry the `.dlg`'s own conditional logic picked, not the flag either.

**The metric that actually isolates the fix — content latency, self-reported by the SAME
instance off its own clock — read 0 ms after opening in every single run, both arms, 10/10.**
That is because every join in this rig (players already standing next to Trask on both
installs) resolves through the **adopt** path (`dialogue-sync.md`'s "the engine started the
same conversation here; adopting it to follow") rather than a distance **follow**: the
joiner's own local engine independently reaches `StartDialog` for the same NPC (the same thing
a real click does), so the first line the joiner sees is rendered by their OWN local dialogue
data, not replicated from the owner over the wire at all. The 150 ms × 2 cadence hops
`dialogue_fast_join` closes (hops #3/#6 in this doc's own table) sit on the FOLLOW path — a
joiner who is not yet standing at the NPC and has to be spectate-walked in before their own
`StartDialog` fires — and this rig's design (both players placed next to the same NPC by the
save, joined via the `talk`/test-open hook that behaves exactly like a click) never exercises
that path. The static, code-read analysis earlier in this document remains the best evidence
for the FOLLOW path's savings; it was not, and could not be, independently confirmed or refuted
by this live pass, because reproducing a genuine distance-follow needs a joiner meaningfully far
from the NPC when the owner starts, which this session's fixed two-PC-adjacent-to-Trask setup
does not produce.

### Screenshots (the user's literal symptom)

Captured via `shot.ps1` on the joiner's window after firing the join. Actual elapsed time from
the join-trigger to each shot was dominated by `shot.ps1`'s own ~1.5–2.5 s per-call overhead
(window-activate wait + subprocess start), so the requested exact 0.5/1/2/3 s marks were not
achievable with sequential PowerShell calls — the earliest capture landed at **~2.4 s**
post-trigger, the second at **~17.2 s**. Saved under the session scratchpad
(`join_p0.5s.png`, `join_p2.png`; not committed — screenshots are ephemeral evidence, not repo
content). What they show:

* **~2.4 s post-join**: the letterbox bars are already in their final, steady thin position —
  not visibly "creeping" — and no subtitle text is on screen at that instant (a normal
  between-lines gap, not a stuck blank box).
* **~17.2 s post-join**: full subtitle text rendered normally (`"Look at the TARGET MENU and
  find the SECURITY icon..."`), letterbox unchanged from the first shot.

Combined with the 0 ms content-latency figure above, the user's reported symptom — subtitles
staying blank for several seconds while the bars visibly animate in — **did not reproduce in
this configuration** (adjacent players, adopt-path join), under either flag setting. This is
consistent with the join path exercised here (adopt) never having been the slow path to begin
with; it does not rule out the FOLLOW/distance-join path still showing the symptom, which this
rig's fixed NPC-adjacent starting position cannot exercise.

### Rough edges (`dialogue-sync.md`'s NEEDS-LIVE list)

* **Both players clicking the same NPC simultaneously.** Fired `talk end_trask` on both
  installs within ~50 ms of each other. No crash, no stuck state. The in-process arbiter
  resolved the tie (the token went to whichever side's write landed first — observed both
  ways across two attempts), the LOSING side logged `"the engine started the same conversation
  here; adopting it to follow"` and immediately converged onto the winner's exact current line
  (content latency 0 ms again). Both instances' `blockpoke dialogue` reads showed byte-identical
  line text afterward.
* **The joiner leaving mid-conversation (Esc).** Sent a literal `Esc` keypress
  (`sendkey.ps1 -Keys esc`) to the follower's window mid-conversation. **No observable effect**
  — the conversation continued unchanged on both instances and stayed in sync (both advanced to
  the next line together immediately after). This appears to be vanilla KOTOR1 behaviour (this
  engine build does not offer an Esc-to-leave affordance during dialogue at all), not a gap this
  mod introduces or needs to close.
* **The joiner explicitly leaving and rejoining** (`talk close`, which does exercise the real
  `Odyssey.CloseDialogue` give-up path, unlike Esc above): the owner's OWN conversation
  continued completely unaffected — identical line text read before and after the joiner's
  close. The joiner then re-issued `talk end_trask` and was readmitted as a follower
  (`remote: active 1` again), catching back up toward the owner's current node. Leave-then-
  rejoin works.
* **Owner ends the conversation — how fast does the joiner's UI close?** The joiner's
  `"the owner ended the conversation; closing"` line appeared within a few seconds of the
  close request in every observation, but this harness cannot isolate the true propagation
  time from the ~1–3 s of PowerShell subprocess start-up each triggering call itself costs —
  the same limitation as the node-advance metric above. Not independently measured to
  millisecond precision; qualitatively prompt, no stuck joiner UI observed in any run.
* **A conversation started by a trigger/cutscene on one side (END_M01AA's opening Trask
  scene).** **Not independently re-verified live this session.** The save both installs load
  already begins past that scripted opening, and reliably re-triggering a spent module-load
  cutscene would need restarting from a much earlier save — out of this session's scope.
  Structurally the code path is identical to the already-tested adopt mechanism
  (`BeginConversation → StartDialog` is the same entry point in `dialogue-sync.md`'s own
  verified chain regardless of whether a player click or a script action reached it), so no
  separate risk is expected — but that is an inference from the code, not a live observation,
  and is recorded honestly as a gap rather than dressed up as one.

### Verdict: default NOT flipped

`dialogue_fast_join` stays `0` by default. The task's own bar — flip only if the A/B shows it
*strictly* better with no regressions — was not met: no regression appeared (identical node-
agreement shape, identical 0 ms content latency, all rough edges clean in both arms), but no
measurable improvement appeared either, for the reason explained above: this rig's fixed
NPC-adjacent starting position only ever exercises the adopt path, which was never gated by the
150 ms × 2 cadence this flag closes. The fix is not shown to be wrong — the static analysis
behind it is untouched by this pass — it is shown to be **unverified for the path it targets**,
which is a live-testing gap, not a design defect. `dialogue_fast_join=1` remains available and
recommended for anyone who can reproduce a genuine distance follow (a joiner started well away
from the NPC) to re-run this same A/B against.

---

## Far-join live pass (2026-08-21)

The gap the previous pass left honest: its rig placed both players next to Trask, so every join
resolved through **adopt** and the **follow/distance** path — the one the user's report is
actually about — was never exercised. This pass placed the joiner 14 m from the NPC and drove
both directions for real. It reproduces, and it is worse than the reported symptom: a joiner
left far enough away does not just lag, they never open the conversation at all, and the
mechanism that leaves them stuck does not resolve on its own.

### Disassembly: where the distance check lives, and its exact threshold

Read `docs/research/dialogue-sync.md` first for the address database and verification
conventions this section follows.

`CSWSObject::StartDialog 0x004CF490` (`dialogue-sync.md`'s own headline function) itself takes
no distance-related parameter — disassembly of its body (`tools/disasm.py 0x004CF490 0x180`)
shows it only reads fields off `this` and constructs the `CSWSDialog`. **VERIFIED.** The walk
happens one level up, in the AI action that reaches `StartDialog`:
`CSWSObject::AIActionDialogObject 0x0057A470`, already named in this doc's own "the joiner
CAN start conversations — they THRASH" section as the function that re-arms
`gui+0xB4` "before its range test... re-queueing itself for every tick the server spends walking
the player into range." This pass disassembled that range test
(`tools/disasm.py 0x0057A470 0x500`):

```
0x0057A799  fld   dword ptr [0x747450]      ; the threshold, loaded twice and squared
0x0057A79F  fmul  dword ptr [0x747450]
0x0057A7A5  fcompp                          ; compare against dx²+dy²+dz² computed just above
0x0057A7A7  fnstsw ax
0x0057A7A9  test  ah, 5
0x0057A7AC  jp    0x57a9d7                  ; NaN guard, not the range branch
```

`[0x747450]` **VERIFIED LIVE** by reading it directly out of a running instance
(`ReadProcessMemory` at the fixed address — no ASLR, verified elsewhere in this repo): **10.0**.
So `ActionStartConversation`'s own engine-side range gate is a flat **10-unit** radius: a player
farther than that from the NPC does not reach `StartDialog` until something closes the gap.

Two flags gate this whole distance block, read a few lines earlier from the AI action struct
(`ebx = [esp+0xb4]`, the action parameter block; offsets below are relative to it):

```
0x0057A74A  mov  eax, [esp+0x24]     ; = [ebx+0x48]
0x0057A750  test eax, eax
0x0057A756... jne  0x57a9d7          ; skips the ENTIRE distance block, straight to dispatch
0x0057A756  mov  eax, [esp+0x28]     ; = [ebx+0x44]
0x0057A75C  test eax, eax
0x0057A75C... jne  0x57a8ff          ; a different, "close-range" dispatch (pushes 1.0f)
```

`ebx+0x48` is the fifth field the action struct carries after the object/resref pair — the
same position NWScript's own `ActionStartConversation(object, string, int bPrivateConversation,
int bPlayerDialog, int bIgnoreStartRange, ...)` puts `bIgnoreStartRange` in. **INFERRED** (the
field ordering matches the NWScript signature and the branch's own effect — skip the whole
range/walk block — matches the parameter's own documented name in every other NWN-heritage
engine), not independently named in the symbol DB. Following the jump at `0x57a74a` lands at
`0x57a9d7`, which falls straight into the SAME dispatch code the "already close enough" fast
path uses (`jmp 0x57a85c`, into the block that ends in the real `StartDialog`-reaching call) —
**VERIFIED** by disasm, confirming the flag really does bypass the walk entirely, not just relax
its threshold.

**This was not shipped as a native patch.** Setting `bIgnoreStartRange` would need either (a)
marshalling a full NWScript VM call to `ExecuteCommandActionStartConversation 0x0052D5B0` — an
unverified ABI, flagged HIGH risk by this doc's own house rules — or (b) a mid-function detour
inside `AIActionDialogObject` itself, after several vtable calls and floating-point setup have
already run, which is a much longer and riskier trampoline than the whole-function prologue
hooks this codebase ships elsewhere (`DialogueGateHook`'s own doc: refuse BEFORE the SEH
prologue runs, never mid-body). A third path was found instead: the server's own dialog-input
message handler (`CSWSMessage::HandlePlayerToServerInputMessage`'s dialog branch,
`0x00525F2E`) builds this SAME action from a batch of hardcoded, mostly-zeroed pushes at
`0x00525F99-0x00525FE3` through a generic type-tagged param-marshaling call
(`0x004CEA20`) — patching ONE of those pushes to source `bIgnoreStartRange` from a flag this
mod controls is possible in principle but was judged not safe to ship blind in this session: a
wrong type tag in that marshaling call is exactly the class of bug this codebase's own
"verify-bytes-or-refuse" discipline exists to keep out, and nothing here independently confirmed
the full 24-value push list's typing. Left as a follow-up, not attempted.

### Reproduction: the joiner never opens, and the failure does not resolve itself

Live rig: `C:\GOG Games\Star Wars KOTOR (GOG)` (host) and `D:\Games\swkotor-gog-p2` (client),
both plugin and agent rebuilt and deployed, block version 47 / protocol 33 confirmed on both,
`tools\demo.ps1` cold-launched onto `END_M01AA`. The joiner was placed 14 m from `end_trask`
(`console 'tp 20.0 8.0 -1.3'` against a save that starts both players at `20.0 22.2 -1.3`) before
the owner started the conversation and the joiner "clicked" it (`talk end_trask`, which sends the
same player-to-server dialogue request a click does — see `TestDriveController`'s own doc for why
this, not injected mouse input, is how this rig exercises a join).

Before this session's fix, on current HEAD (`38be4ba8`), with `dialogue_join_teleport` not yet
existing:

```
[23:18:15.283] dialogue: engine reached StartDialog (2 so far)      <- HOST, 4 ms after its own request
...
[23:18:19.200] test: opened a conversation with 'end_trask'          <- CLIENT (joiner), request sent
                                                                          NO "engine reached StartDialog" for
                                                                          the client anywhere in the log
[23:18:34.199] dialogue: a peer won the conversation token; closing our own to follow
[23:18:38.197] dialogue: our conversation would not close after the server's turns; forcing the UI shut
[23:18:48.199] ...forcing the UI shut          <- repeats every 2 s (LogFew's own dedupe window makes
[23:18:58.200] ...forcing the UI shut             it print every ~10 s; the underlying retry is 2 s)
[23:19:08.199] ...forcing the UI shut
   ... continues, unattended, past two minutes straight, with ZERO effect ...
```

The joiner's own `CSWSObject::StartDialog` is **never reached** — the engine's own
"conversation active" flag (`gui+0xB4`) goes up immediately (this is the letterbox-eligible
signal the user's report calls "the cinematic bars creep in"), but no real `CSWSDialog` exists
behind it, so no content ever arrives. Structurally this is the correction this doc already made
once ("A JOINER CANNOT START A CONVERSATION AT ALL" → "they THRASH"), reproduced from the ONE
angle that prior pass never tried: **too far**, not **too close together**. The player's own
position was polled throughout and never moved (`20.0, 8.0` at every sample) — this build's
player character is drive-controlled, not action-queue-walked (`TestDriveController.HandleWalk`'s
own established finding, now confirmed to apply here too), so the automatic-join path this rig
exercises (`force_dialogue_party`, or the `talk` test hook) has no way to close the gap on its
own the way a real mouse-driven walk would.

**A second, worse defect was found underneath the first**: the "forcing the UI shut" retry —
already the LAST resort in the state machine — does not stop the loop. `Odyssey.ForceCloseDialogueUi`
only closes the client's GUI panel; it does not touch the still-queued `AIActionDialogObject`
action, which re-arms `gui+0xB4` on the very next tick regardless. Measured: the loop ran for
**over two minutes straight**, unattended, with no change. `talk close`
(`Odyssey.CloseDialogue`) does not help either — it needs resolved participant ids, which never
exist for a half-started shell like this one. What DID stop it, every time it was tried: a
`CheatController`-style teleport (`Odyssey.SnapTo`, which clears the object's action queue
*before* writing its position — its own doc already explains why the clear is required, not
decoration). This is a real, live-verified defect independent of the join-latency feature this
session was asked to investigate, and it is fixed below.

### The fix

Two changes, both built entirely from already-verified, already-used engine primitives — no new
native detour, no mid-function patch:

**1. `dialogue_join_teleport` (default OFF, `coop.ini`).** `Odyssey.StartDialogueWith` — the one
chokepoint both the automatic `force_dialogue_party` join and the test hook use, per its own
existing doc comment about why it bypasses `ActionInitiateDialog` — now checks the target's
distance against the VERIFIED 10-unit start range before sending the request. Farther than that,
it snaps the local player 1.5 units short of the NPC (`Odyssey.SnapTo`, the same call
`CheatController`'s own `tp` uses) and remembers the origin; closer, it is a complete no-op.
`Odyssey.RestoreJoinTeleportIfPending()` puts the player back once the followed conversation
concludes (owner ended it, or the follow gave up) — wired into
`DialogueSpectateController.Say`'s existing `OwnerEnded` / `GaveUpNoContent` / `GaveUpNeverOpened`
handling, so nothing new decides WHEN to restore; the same three events that already end a follow
now also end the teleport.

This is deliberately scoped to the automatic-join chokepoint, not a real mouse click: a genuine
click on a distant NPC already walks the player there via local input (drive control), which this
change does not touch and did not need to — see "What was NOT re-verified" below.

**2. The stuck-loop fix (unconditional, not gated on the flag above).** The pure state machine
(`DialogueFollowMachine.StepUnjoined`'s `Yielding` branch, `KotorCoop.Protocol`) now escalates
past `ForceCloseUi` when it keeps failing: after `YieldClearActionsAttempts` cycles (5, i.e. 10 s
of retrying at the existing 2 s cadence) with the shell still open, the decision becomes the new
`ForceCloseUiAndClearActions` action, which the controller answers by calling BOTH
`Odyssey.ForceCloseDialogueUi()` and the new `Odyssey.ClearLocalPlayerStuckDialogueAction()` (a
`SnapTo` to the player's OWN current position — nothing about where they stand changes, only
that the stuck action is gone). This fixes the two-minutes-and-counting loop found above
regardless of whether `dialogue_join_teleport` is on; it is a correctness fix for an already-
shipped last-resort path that measured as not actually resorting to anything.

### Live verification of both fixes

Same rig, rebuilt plugin and agent, block version 47 / protocol 33 confirmed. All times from the
plugin logs, same machine so cross-process timestamps share a wall clock.

**Stuck-loop fix, `dialogue_join_teleport=0`** (so the underlying failure still happens, and only
the escalation is under test): joiner 14 m away, `talk end_trask`.

```
[23:33:45.173] test: opened a conversation with 'end_trask'
[23:34:00.166] dialogue: a peer won the conversation token; closing our own to follow      (+15.0 s: LocalOpenGraceMs)
[23:34:04.166] dialogue: our conversation would not close after the server's turns; forcing the UI shut
[23:34:10.168] dialogue: the UI-only close did not stick; clearing the stuck action too    (+25.0 s total)
                                                                                              NO further lines after this
```

Position after the clear: unchanged (`20.0, 8.0`, exactly where it was) — the clear does not
displace the player, only unsticks the action. Watched with two separate 30-40 s monitors after
the clear fired: zero further "forcing the UI shut" or "clearing the stuck action" lines. The
loop that ran unattended for over two minutes before this fix now self-resolves, unattended, in
25 seconds — one join attempt's worth of retries, not an indefinite one.

**`dialogue_join_teleport=1`, both directions, before/after:**

| Scenario | `engine reached StartDialog` after the request | content latency (self-reported) | position after conversation ends |
|---|---:|---:|---|
| Adjacent join (control, unaffected by the flag) | 4 ms | 0 ms after opening | unchanged (no teleport needed) |
| Far join (14 m), **before** the fix | never (2+ min observed) | never | never advanced |
| Far join (14 m), host owns / **client** joins, **after** | 4 ms | 15 ms after opening | restored to the pre-join spot exactly |
| Far join (14 m), **client** owns / host joins, **after** (roles swapped) | 5 ms | 0 ms after opening | restored to the pre-join spot exactly |
| Far join (14 m) **with `dialogue_fast_join=1` too** | 4 ms | 0 ms after opening | restored to the pre-join spot exactly |

Every "after" row matches the adjacent-join control to within measurement noise — a far join now
costs nothing beyond the request round-trip, in both ownership directions, and composes cleanly
with `dialogue_fast_join`. Logs, one representative run (client joins host 14 m away):

```
[23:37:17.602] client (14m away): talk end_trask                                   <- request issued
[23:37:18.519] test: opened a conversation with 'end_trask'                        <- +0.9 s (PowerShell/serial-poll overhead)
[23:37:18.523] dialogue: engine reached StartDialog (1 so far)                     <- +4 ms
[23:37:18.523] dialogue: the engine started the same conversation here; adopting it to follow
[23:37:18.526] dialogue: followed conversation delivered content 15 ms after opening (15 ms after the request)
...
[23:37:57.210] host: talk close                                                    <- owner ends it
[23:38:00.103] dialogue: the owner ended the conversation; closing
                                                                                     client position: 20.0, 8.0, 0.0
                                                                                     (exactly the pre-join spot)
```

### Screenshots: not usable in this environment, and why block/log telemetry replaced them

The task asked for `shot.ps1` captures at fixed offsets. Both game windows rendered solid black
in every capture attempted (including a full-desktop capture confirming it was not a
capture-tool artifact — the windows themselves show black on screen), regardless of
refocus/retry. The two console windows and the desktop icons behind them captured cleanly, so
this is specific to the D3D9 swap chain not presenting in this session's display environment, not
a general screenshot failure — the PREVIOUS pass's own screenshots (real subtitle text, real
letterbox bars) prove this rig CAN produce them under different conditions. Rather than spend
further budget chasing a display issue, this pass leaned on the block reads and log timestamps
already used throughout — `blockpoke.ps1 dialogue`, `console 'where'`, and the plugin's own
timestamped log lines — which are more precise than a screenshot could be anyway (millisecond
timestamps vs. a ~2 s per-capture floor) and were the actual evidence base for every claim above.

### What remains

* **A real mouse-driven click on a distant NPC** was NOT re-verified this session — this rig has
  no reliable way to inject one (`docs/live-verification.md`'s own "traps 22 and 25"), so
  everything above exercises the automatic-join chokepoint (`force_dialogue_party` / the test
  hook), not a literal click. Vanilla KOTOR's own click-and-walk behaviour for a distant NPC is
  untouched by `dialogue_join_teleport` (it is not in the call path at all), so it is expected to
  behave exactly as it always has — but that expectation is an inference from the code, not an
  observation, same honesty bar the rest of this doc holds itself to.
* **Adjacent room / no line-of-sight** was not separately tested. `dialogue_join_teleport`'s own
  mechanism (`SetPosition`, not a walk) does not consult the walkmesh or line of sight at all —
  the same `NEEDS-LIVE` caveat `CheatController.Teleport`'s own doc already carries for hand-typed
  coordinates — so a joiner on the far side of a wall teleports exactly as cleanly as one across
  an open room. Not independently measured, but not expected to differ, for a documented reason
  rather than an assumption.
* **A trigger/cutscene-started conversation** (END_M01AA's opening Trask scene) remains
  NEEDS-LIVE from the previous pass — this session's save is also already past that scripted
  opening. Unchanged from before: `BeginConversation → StartDialog` is the same entry point
  regardless of trigger vs. click, so no separate risk is expected from either fix here, but it
  is still an inference.
* **The `bIgnoreStartRange` native patch** (see disassembly above) remains unshipped. The
  teleport fallback fully covers the automatic-join case this session could reproduce and measure
  live; the flag would additionally help a genuine walked click reach the NPC faster, which
  nothing here could exercise to confirm is even worth the added native-patch risk.

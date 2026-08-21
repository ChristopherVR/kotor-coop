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

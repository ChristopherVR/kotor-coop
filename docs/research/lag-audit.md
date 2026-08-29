# Lag audit: "sometimes quite a bit of lag even on localhost" (2026-08-22)

A static audit — no live run — of every per-frame plugin cost, every agent-side scan cadence,
and the end-to-end latency budget for a phantom's position, done to separate two genuinely
different complaints the one word "lag" covers: (i) a peer's avatar or a mirrored enemy visibly
lagging BEHIND where it really is, and (ii) the game itself hitching — a frame that takes far
longer than its neighbours, felt as a stutter regardless of what anyone is doing. New
instrumentation (`frame_stats`, `lagstats`) is added so the next live round can MEASURE which of
the two — or both — is actually happening, rather than guess.

## Table 1 — per-frame plugin controllers (`PluginHost.OnFrame`)

Frame order: `_death → MinigameController → _spectate → _controller → _items → _combat →
_liveness → _enemyPresence → _partyPresence → _partyRoster → _incomingDamage → _peerEffects →
_encounters → _triggerRelay → _movieRelay → _peerDamage → _doors → _placeables → _loot →
_pause → _combatState → _pauseKey → _journal → _mapMarkers → _mapReveal → _portraits → _barks →
_interactionLocks → _controlReturn → _dialogueSpectate → _testDrive → _cheats`
(`src/KotorCoop.Plugin/PluginHost.cs:810-876`).

| Controller | Gate | Steady-state cost | Cost class |
|---|---|---|---|
| `EnemyPresenceController` | **none — every frame** | scan ≤32 enemy slots; per-enemy `ClaimCreature` (raw write) + gait-edge-triggered `+0x12C` drive call | scan + edge-triggered engine call |
| `PartyPresenceController` | none, but `PartyFreezeRule`-gated | scan ≤4 companion slots; skips the currently-controlled body; edge-triggered faction/health writes, `+0x12C` drive every pass | scan + engine call |
| `PhantomController` | internal wall-clock gate, `StepIntervalSeconds = 1/60 s` (`PhantomController.cs:39`) | drive step over ≤8 phantoms at ~60 Hz; spawn rate-limited to 1/30 frames | drive: engine call; spawn (rare): "by far the most expensive thing here" per its own comment |
| `LivenessReconcileController` | `IntervalFrames = 40` (~0.66 s @ 60 fps) | cheap when nothing diverges | flag compare |
| `TriggerRelayController` | `WatchIntervalFrames = 12` | cheap | flag compare |
| `InteractionLockController` | `IntervalFrames = 10` | `ClearAllActions` on locked creatures — raw write, not VM | raw writes |
| `MapRevealController` | none, `block.Command` gate only | scan 8 remotes, float distance compares; engine reveal call only on a real edge | scan (cheap) |
| `PeerDamageController` | `IntervalFrames = 10` | cheap | flag compare |
| `IncomingDamageController` | `IntervalFrames = 10`, tightens to `3` (`LocalCombatIntervalFrames`) while in local combat | 3× tighter mid-fight | flag compare |
| `CombatController` | `IntervalFrames = 40` | cheap | flag compare |
| `PeerEffectController` | `IntervalFrames = 5` | cheap | flag compare |
| `AgentLivenessController` | every frame | one heartbeat-age compare | cheap |
| `CheatController` | every frame, unconditionally, for 3 of its watches (`ApplyPendingHitPoints`/`RetryPendingDoorWalk`/`WatchDoorWalk`) | each is a single-int early-return in steady state; actual cheat dispatch is serial-gated | cheap despite the file's size |
| `MovieRelayQueue` / `MidModuleMovieController` | serial compare | cheap | cheap |
| `JournalController` | serial compare + `block.Command` gate | cheap | cheap |
| `PortraitController` | `IntervalFrames = 15` | cheap | cheap |
| `DoorUse` / `ContainerUseHook` / `ContainerCloseHook` / `TriggerFireHook` | native detours, not polled from the controller list | zero continuous per-frame cost | event-driven |

**No frame-time histogram existed before this pass** — grepped `PauseController`/`FrameHook` for
timing code, found none. `FrameHook.cs`'s own doc comment: the main loop runs at ~317 Hz, but
`OnFrame` (the SwapBuffers callback) fires once per RENDERED frame — the cadence a hitch is
actually felt at, and the one `FrameTimeHistogram` (below) now samples.

## Table 2 — agent-side scans (`KotorCoop.Memory/KotorGameBridge.cs`)

| Scan | Cadence | Iterates | Notes |
|---|---|---|---|
| `EnumerateCreatures()` | address list cached 15 s (`CreatureCacheMs = 15_000`); 750 ms if the cache came back empty (`EmptyCreatureCacheMs`); each address's liveness re-`Confirm`ed every 250 ms (`LiveConfirmMs`) | full committed-memory pattern scan for the address list only; `Confirm` is a handful of field reads per address | The file's own doc comment records the ORIGINAL bug this fixed: uncached, "a peer took 3-7 seconds to move, and the pump ran at ~4 Hz instead of 60" — already fixed, this is the steady state now |
| `CachedScan` (doors/placeables/encounters) | `ObjectScanCacheMs = 30_000` (30 s) | up to 256/512/128 addresses depending on the object type | Same historical class of bug, already fixed: "a dozen full scans a second... consumed a whole CPU core" |

**Locking / `SharedBlock` discipline**: grepped `SharedBlock.cs` and `SharedBlockView.cs` for
`Mutex`, `Semaphore`, `lock (`, `Monitor.`, `MemoryMappedFile` — **zero hits**. There is no
synchronization primitive guarding the shared block at all; the agent reads and writes it via
cross-process memory operations (`ReadProcessMemory`/`WriteProcessMemory`, or the in-process
pointer the plugin itself uses), and the plugin's frame thread never waits on anything the agent
holds. **A slow agent scan cannot stall a plugin frame through a lock, because there is no lock
to stall on.** This directly answers suspect (ii)'s stated worry — it is not one of the three
below, because there is nothing there to audit.

## Latency budget: a phantom's position, host to client

Both `CoopHost`/`CoopClient`'s own tick loop and `CoopAgent`'s poll loop default to the SAME
30 Hz (`ProtocolConstants.DefaultTickRate = 30`, `src/KotorCoop.Protocol/ProtocolConstants.cs:380`;
`CoopAgent.RunAsync`'s `PeriodicTimer` at `settings.PollRate`, `CoopAgent.cs:248`; `CoopHost`/
`CoopClient.RunAsync`'s `PeriodicTimer` at `settings.TickRate`, `CoopHost.cs:1598`) — ~33 ms
between hops, independently, on both ends.

| # | Hop | Cadence | File:line |
|---|---|---|---|
| 1 | Host plugin writes the local PC's position into its shared block | every rendered frame (~16.7 ms @ 60 fps) | `EnemyPresenceController.cs` / phantom write path |
| 2 | Host agent reads local state, publishes it to the session | every `PumpOnce` (~33 ms, `PollRate`) — NOT gated by the 150 ms combat pump; that gate covers enemy census and party mirroring only | `CoopAgent.cs:322` (`PumpOnce`), `CoopAgent.cs:395` (`TryWriteRemotePeers`, unconditional) |
| 3 | Host session broadcasts `WorldSnapshotPacket` to every peer | every `Tick()` (~33 ms, `TickRate`) | `CoopHost.cs:1614` (`Tick`), `CoopHost.cs:1701` (the broadcast) |
| 4 | Network transit | ~0-1 ms on localhost UDP | — |
| 5 | Client session receives, buffers into `SnapshotBuffer` at the DELIBERATE playback delay | `InterpolationDelayMs`, default **80 ms** (`ProtocolConstants.DefaultInterpolationDelayMs = 80`, `ProtocolConstants.cs:391`) | `SnapshotBuffer.cs:14`, `CoopClient.cs:176` |
| 6 | Client agent reads the interpolated position, writes its own shared block | every `PumpOnce` (~33 ms) | `CoopAgent.cs:395` |
| 7 | Client plugin drives the phantom body, visually eased by its own smoother | `PartyPresenceController`'s `mirror_smooth_ms`, default **150 ms** (`PartyPresenceController.cs:88`) for companions; `PhantomController`'s own `smooth_ms`, default **60 ms** (`PhantomController.cs:1097`) for peer avatars/enemies; a repath is cooled down for `RepathCooldownSteps = 5` steps @ ~60 Hz ≈ **80 ms** before a new direction is honoured (`PhantomController.cs:39,55`) | see above |

**Rough total, typical (peer avatar / phantom path)**: ~33 ms (hop 2) + ~33 ms (hop 3) + 80 ms
(hop 5, deliberate) + ~33 ms (hop 6) + up to 60 ms (hop 7, phantom smoother) ≈ **~240 ms**, most
of which — 80 ms of it — is a DELIBERATE design choice trading a fixed, bounded delay for
smooth interpolation instead of a visible step every network tick. **Worst case**: a position
that lands right after hop 2's sample waits a full extra ~33 ms before the next one, and the
same for hops 3/6, plus the smoother's own window: ≈ **~330-400 ms**. The companion-mirror path
(`mirror_smooth_ms = 150 ms`) runs meaningfully longer than the phantom path (`smooth_ms =
60 ms`) for the same reason `PartyPresenceController`'s own doc comment gives: companions arrive
on the ~150 ms combat pump, not the 30 Hz tick, so its smoothing window is sized to match.

## Top 3 suspects, (i) phantom/enemy MOVEMENT lag

1. **The 80 ms `InterpolationDelayMs` itself, misread as lag.** It is working as designed — a
   fixed buffer that trades a bounded delay for smoothness — but it is also the single largest
   deliberate number in the whole chain, and nothing before this pass surfaced it to a live
   round. `lagstats` now prints it explicitly labelled "deliberate," specifically so it stops
   being confused with an actual stall.
2. **`mirror_smooth_ms` (150 ms) stacking with the 150 ms combat pump on the COMPANION path.**
   Two independently-configured 150 ms windows appear back to back in the companion-mirror
   chain (the combat pump that reads a companion's state, and the smoother that eases it into
   view) — nothing forces them to be different values, and their sum is the single biggest
   contributor to companion mirroring reading "laggier" than a peer's own avatar, which only
   goes through the 60 ms phantom smoother.
3. **The `RepathCooldownSteps` 80 ms floor on a direction change.** A phantom that reverses
   course cannot be re-issued a new move action for 5 steps (~80 ms) after the last one — a
   deliberate anti-churn guard, but it means a sharp real-world direction change (someone
   spinning around) reads as a beat of unresponsiveness that is not attributable to any network
   hop at all.

## Top 3 suspects, (ii) FRAME-TIME hitching

1. **`EnemyPresenceController`'s unconditional, ungated, every-frame scan.** It is the one
   controller in the whole per-frame list with NO countdown gate at all — every other scanning
   controller (`PartyPresenceController`, `MapRevealController`) is either gated or bounded to a
   handful of slots; this one runs its full ≤32-slot scan every single frame regardless of
   whether anything changed. With `FrameHook` confirming `OnFrame` fires once per rendered
   frame, this is the single largest UNCONDITIONAL per-frame cost in the list.
2. **`PhantomController`'s spawn path**, by its own doc comment "by far the most expensive thing
   here" — rate-limited to 1 per 30 frames, but when it fires (a peer's phantom needs to be
   (re)created — connect, module load, revive) it is a real engine object-creation call on the
   game thread, not a memory poke. A burst of connects/revives could visibly hitch even though
   the steady state is cheap.
3. **`CheatController`'s size, not its steady-state cost.** 2267 lines, three watches running
   every frame unconditionally — each one confirmed to be a single-int early-return in steady
   state, so it is NOT actually expensive per frame, but it is the largest single controller in
   the codebase and the easiest place a future change accidentally adds real per-frame work
   without anyone noticing, precisely because its current cost is invisible. Flagged as a watch
   item, not a live suspect today.

Ruled OUT as a suspect for (ii): the agent's SharedBlock discipline (no lock exists at all, so a
slow scan cannot stall a frame that way) and the two heavy agent-side scans (`EnumerateCreatures`,
`CachedScan`), both of which are already cached (15 s / 30 s) specifically because an earlier,
now-fixed bug made them expensive — their own doc comments record the fixed defect and the
measured cost it used to have.

## New instrumentation

### `frame_stats` — the plugin-side histogram

`coop.ini frame_stats=1` (default 0). `FrameTimeHistogram` (`src/KotorCoop.Plugin/
FrameTimeHistogram.cs`) is a pure, allocation-free accumulator — no EWMA, deliberately (see its
own doc comment: a smoothed running average would blur a burst of hitches into an innocuous
long-session mean, which is exactly the failure mode this exists to catch). `PluginHost.OnFrame`
feeds it one wall-clock delta (`Stopwatch.GetTimestamp()`) per SwapBuffers callback, before
anything else in the frame runs, and flushes a snapshot to the plugin log every ~10 s:

```
frame stats: 601 frames, 16.84 ms average, 212.3 ms worst, 3 over 33 ms
```

`SampleCount` / `AverageMs` / `MaxMs` / `OverThresholdCount` (frames slower than 33 ms — one
dropped frame at 30 fps, two at 60 fps) reset after every flush, so each line is exactly what
happened in the last ~10 s, not a running total.

### `lagstats` — the console verb

Typed at the in-game `~` console (or via `tools/blockpoke.ps1 console lagstats`), prints three
independent lines — deliberately not one combined verdict, because the whole point is telling
"the deliberate interpolation delay" apart from "the real send/recv cadence this round is
actually achieving" apart from "how much of the agent's own frame budget the pump is spending":

```
lagstats: interpolation delay 80 ms (deliberate - trades a fixed buffer for smoothness, not a stall)
  send intervals (ms, oldest first): 33, 34, 32, 33, 35, 33, 34, 33
  recv intervals (ms, oldest first): 33, 33, 41, 33, 32, 34, 33, 33
  agent pump: 2.1 ms average, 8.4 ms peak (budget 33 ms)
```

- **Interpolation delay**: `SessionStats.PhantomInterpolationDelayMs`, the actual
  `settings.InterpolationDelayMs` the running session is using (not the compiled-in default —
  `coop.ini interp_ms` can change it).
- **Send/recv intervals**: `IntervalTracker` (`src/KotorCoop.Session/IntervalTracker.cs`), a
  small ring buffer of the last 8 REAL gaps between successive events — recorded at
  `SendStateUpdate` (client) / the world-snapshot broadcast (host), and at the liveness-bearing
  inbound-packet site on both — surfaced through new `SessionStats` fields
  (`RecentSendIntervalsMs`/`RecentRecvIntervalsMs`). Pure and clock-free, unit-tested.
- **Agent pump cost**: already existed (`CoopAgent.PumpAverageMs`/`PumpPeakMs`/`PumpBudgetMs`,
  already surfaced in `AgentDashboard.cs`) — `lagstats` is the first thing to surface it
  IN-GAME rather than only on the agent's own console window.

## Tests

- `tests/KotorCoop.Tests/FrameTimeHistogramTests.cs` — no samples reports a zeroed snapshot; a
  single sample is its own average and max; the average is the true mean of every sample in the
  period (not smoothed); samples strictly over the threshold count as hitches, the threshold
  itself does not; one huge stall dominates the period's average and max rather than vanishing
  into a long-session smoothing; negative deltas are refused rather than corrupting the average;
  reset starts a clean period.
- `tests/KotorCoop.Tests/IntervalTrackerTests.cs` — the first event reports no interval yet; two
  events produce one interval; the snapshot orders oldest first; the buffer wraps at capacity and
  keeps the LAST N, not the first; a clock going backwards clamps to zero rather than going
  negative; the average matches the recorded gaps.

## How to read `lagstats` on the next live round

1. Run it on BOTH instances once connected and moving. `interpolation delay` should read the
   configured value (80 unless `interp_ms` was changed) on every read — if it does not, that is
   a bug in its own right, not lag.
2. Compare the printed send/recv intervals to the configured tick rate (30 Hz ⇒ ~33 ms). Intervals
   consistently and significantly above that — not a single outlier, a sustained shift — point at
   suspect (ii): something is stealing time from the tick loop itself (GC, a stalled `PumpOnce`,
   thread-pool starvation), not the network.
3. Compare `agent pump: average/peak` against `budget`. A peak repeatedly close to or over budget
   means the agent's own per-tick work — not the plugin, not the network — is the bottleneck; the
   PLUGIN's own `frame_stats` output (run with `frame_stats=1`, watch the plugin log) is the
   companion reading for whether the GAME thread is hitching independently of the agent.
4. A `frame_stats` line with a nonzero `OverThresholdCount` but a normal `lagstats` average/peak
   points at suspect (ii)'s list above (`EnemyPresenceController`'s unconditional scan first, the
   phantom spawn path second); a normal `frame_stats` line with elevated `lagstats` intervals
   points at suspect (i)'s list instead (the interpolation/smoothing chain, or an actual
   network/thread-pool stall upstream of the plugin entirely).

## Verification tags

- VERIFIED (source, this pass): every cadence/gate constant cited above, by file:line; the
  absence of any synchronization primitive in `SharedBlock.cs`/`SharedBlockView.cs`; the frame
  order in `PluginHost.OnFrame`; `TickRate`/`PollRate` both defaulting to 30 Hz independently.
- INFERRED: the "top 3 suspects" rankings are reasoned from the numbers above, not measured live.
- NEEDS-LIVE, closed for frame-time hitching and the send/recv/interpolation numbers — see
  "Measurements" below. Still open for the direct host-write-to-client-apply phantom latency
  correlation.

## Measurements (2026-08-22)

`frame_stats=1` enabled on both `coop.ini`; a ~190 s connected session on END_M01AA (repeated
`tp` hops for both players, one `usedoor` transition, one staged fight via the `attack` engine
action against `end_weld`); `lagstats` read on both instances afterward, and the plugin's own
`frame stats` lines (auto-flushed every ~10 s) pulled for the whole window.

### Correction to this doc's own premise: `interp_ms` already IS the exposed knob

The "act on it" instruction assumed `interp_delay_ms` needed adding to `coop.ini`. It does not —
`coop.ini interp_ms` (`CoopConfig.cs:333`, wired to `AgentSettings.InterpolationDelayMs` since
before this pass) already does exactly that, and **both install directories already had it set
to `interp_ms = 0`** — more aggressive than the 40 ms this doc's own instruction proposed
testing, and confirmed live: `lagstats` on both instances read `interpolation delay 0 ms` for
the whole session. No code change was needed here; this section corrects the audit's own
premise rather than shipping a redundant knob.

Whether 0 ms specifically is stable under a REAL walk/run (not a `tp` hop) remains
`phantomgait.ps1`'s own long-standing tooling gap: `tools/blockpoke.ps1 walk` was tried as the
drive primitive and confirmed, live, to be a no-op for the local PC (position never changed
across 15 s of samples, animation parked at idle) — the same "PC is drive-controlled" finding
`docs/live-verification.md`/round 52 already recorded. The `tp`-hop stress case in this
session's send/recv/frame-time numbers below shows no instability at 0 ms, but that is a
different claim from "the interpolation/smoothing pipeline reads clean during continuous
movement," which still needs a real human walking.

### Frame-time histogram (suspect ii)

`FrameTimeHistogram` reports mean/worst/over-threshold-count per ~10 s flush, not true
percentiles (deliberately — see its own doc comment above) — so "p50/p95/max" below is the
closest honest reading of that shape: the per-window MEAN as the p50 proxy (the distribution is
tight and unimodal around 2-3 ms on both instances), the largest per-window WORST as max, and
the summed over-threshold count as the hitch tally.

| | host (Game1) | client (Game2) |
|---|---|---|
| per-window mean, typical | 2.3-2.7 ms | 1.9-2.7 ms |
| worst single frame (whole session) | 183.5 ms | 154.3 ms |
| frames > 33 ms (whole session) | 7 | 5 |
| total frames sampled | ~101,000 (25 windows) | ~119,000 (25 windows) |

Both far below the 33 ms (30 fps) / 16.7 ms (60 fps) thresholds in the steady state — this rig,
in this session, is not frame-time-constrained. The 7 and 5 over-threshold frames are not
spread evenly:

- A cluster of 2+2+1 (host) and 1+3 (client) over-threshold frames lands in the ~24-second
  window around the scripted `usedoor` transition (elapsed ~64 s into the session, timestamps
  21:58:17-21:58:41) — a real module-load cost, expected and not a per-frame-controller defect.
- The remaining single large spikes (177.8 ms, 183.5 ms host; 154.3 ms client) are isolated,
  roughly one every 2-3 minutes, and do NOT correlate with the `tp` hops, the `attack` order, or
  any console command this session's driver issued at the same timestamp. Not chased further
  this pass — candidates are a GC pause or a periodic housekeeping boundary (the 15 s creature-
  cache refresh is the nearest fixed-period candidate on the AGENT side, though the docs already
  rule the agent out as a source of PLUGIN frame stalls since there is no lock between them).

**Suspect (ii) verdict: not confirmed.** `EnemyPresenceController`'s unconditional scan was the
top suspect on paper; this session's own hitch pattern (tied to a module load, not a steady
elevated cost or a repeating small-overage signature) does not point at it, so it was NOT gated
behind a coop.ini switch this pass — the audit's own instruction was conditional ("if the audit
says it clusters"), and it did not. Left as a documented non-finding rather than a speculative
change.

### Send/recv/agent-pump (suspect i)

```
lagstats: interpolation delay 0 ms (deliberate - trades a fixed buffer for smoothness, not a stall)
  send intervals (ms, oldest first): 16, 15, 16, 16, 16, 16, 15, 16
  recv intervals (ms, oldest first): 16, 0, 16, 0, 16, 0, 15, 0
  agent pump: 0.5 ms average, 1.3 ms peak (budget 17 ms)
```

`tick_rate = 60` is set explicitly in both `coop.ini` (not the 30 Hz default this doc's earlier
pass assumed), so ~16.7 ms send intervals are correct and healthy — confirmed on both host and
client. `recv` intervals alternate a real gap with 0 ms (packets arriving in the same interval
as the previous sample) rather than a clean 16 ms cadence — bursty, not stalled, and not
investigated further: localhost UDP coalescing readily produces exactly this pattern, and
nothing downstream showed jitter from it. Agent pump cost is negligible (0.3-0.5 ms average,
≤3.1 ms peak against a 17 ms budget) on both instances throughout — the agent is not the
bottleneck.

**Not measured this pass:** the direct host-write-timestamp → client-apply-timestamp
correlation for the same phantom sample (needs new serial-tagged instrumentation this pass did
not add). The reasoned budget, updated for what WAS measured — `interp_ms=0` instead of the
80 ms default — drops the single largest deliberate contributor entirely: rough total for a
peer-avatar phantom is now ~16 ms (publish) + ~16 ms (broadcast) + ~16 ms (client pump) + up to
60 ms (phantom `smooth_ms`) ≈ **~108 ms typical**, not the ~240 ms this doc computed against the
80 ms default.

### Companion-mirror latency: `party_fast_pump` (suspect i, #2)

Suspect #2 (two independently-configured 150 ms windows stacking on the companion path — the
combat pump that reads a companion's state, then `PartyPresenceController`'s own
`mirror_smooth_ms` smoother) is real and structural, independent of `interp_ms` (companions
never go through the interpolation buffer at all — `PartyPresenceController`'s own doc comment:
"companions arrive on the ~150 ms combat pump, not the 30 Hz tick").

**Change made:** `coop.ini party_fast_pump` (default OFF), mirroring `dialogue_fast_join`'s own
rollout shape exactly. When on, `PumpParty`/`PumpPartyRoster` (`CoopAgent.cs`) move from the
150 ms combat-pump gate to the same real-time (every poll tick) path dialogue already uses,
removing the first of the two stacked 150 ms windows. `PumpParty`/`PumpPartyRoster` are cheap
block reads/writes (no world scan), the same argument that justified moving dialogue.

- Unit-tested: `tests/KotorCoop.Tests/PartyFastPumpTests.cs`, mirroring
  `DialogueFastJoinPumpTests`'s own technique (an effectively-infinite combat-pump interval, so
  a write on every `PumpOnce()` can only be the fast path firing).
- Live-verified (smoke test, ~50 s connected, both instances): `party_fast_pump=1` on both
  installs; `partystate.ps1` on the client still read `companions 1 soloMode 1 objectId 0x127`
  correctly reconciled throughout; `lagstats`' agent-pump reading was unchanged (0.5 ms
  average); `frame stats` showed zero frames over 33 ms across the whole smoke window on both
  instances. No regression observed. NOT run through the FULL 3-minute session this pass (the
  measurement session above ran with the flag off, to keep the baseline numbers clean); a
  longer combat-heavy A/B against the flag is the natural next round.
- Left OFF by default, same reasoning as `dialogue_fast_join`: real, but a timing change to a
  channel real players will notice, so it ships behind a flag for a live A/B rather than being
  trusted on this pass's smoke test alone.

### What was NOT changed

- `EnemyPresenceController`'s scan gating — measured hitch pattern did not implicate it (see
  above).
- `interp_ms` / `interp_delay_ms` — already exposed, already tuned to 0 in both installs; no
  code change needed, only this doc's premise corrected.
- Default value of `party_fast_pump` — shipped OFF, matching `dialogue_fast_join`'s precedent.

## Verification tags (this pass)

- LIVE-VERIFIED 2026-08-22: the frame-time table and hitch clustering above; the
  send/recv/agent-pump `lagstats` reading; `interp_ms=0` already active in both installs;
  `party_fast_pump=1` smoke-tested stable for ~50 s (party reconciliation intact, no frame-time
  regression, agent pump unchanged).
- VERIFIED (source, this pass): `party_fast_pump` wiring end to end
  (`CoopConfig.cs`/`Program.cs`/`CoopAgent.cs`), mirroring `dialogue_fast_join`'s existing,
  shipped pattern exactly.
- NEEDS-LIVE, still open: the direct host-write → client-apply phantom-latency correlation (no
  serial-tagged instrumentation added this pass); a real (non-`tp`-hop) walk/run test of
  `interp_ms=0`/40 via `phantomgait.ps1`, blocked by the same "no input injection" tooling gap
  as always; a longer, combat-heavy `party_fast_pump` A/B against the full 3-minute-class
  session shape.

## 2026-08-23 — `party_fast_pump` A/B, real combat this time: LEFT OFF

Round 71 (shared-state-audit.md §2). The smoke test above never put the flag through a real
fight; this pass did, on both instances at once.

**Rig**: `tar_m10aa`'s Vulkar pool, host and client both given a working `attack` order
(shared-state-audit.md round 71's own range-closing fix) against separate Vulkars at the same
time, `frame_stats=1` throughout. Two full rig cycles, ~45 s of real combat sampled per leg —
shorter than the 3-minute target this task asked for (session time ran out before a longer pass
was possible); the numbers below are directional, not a claim of statistical rigor.

| | `party_fast_pump=1` | `party_fast_pump=0` |
| --- | --- | --- |
| Host mean frame time (combat windows) | 9.63 ms | 6.30 ms |
| Client mean frame time (combat windows) | 7.15 ms | 5.52 ms |
| Host frames over 33 ms budget | 197 | 138 |
| Client frames over 33 ms budget | 217 | 123 |

`=0` read better on all four numbers — lower mean frame time on both instances, and roughly 40%
fewer over-budget frames on both. Neither leg showed a qualitatively different worst-case spike
shape (both had one ~200-430 ms outlier per instance, most plausibly tied to a kill/death event
rather than the flag); this pass did not isolate that further. No companion-position-lag or
missed-hit metric was separately instrumented this pass — the comparison rests on `frame_stats`
alone.

**Verdict: default stays OFF.** The task's own rule ("flip default ON only if strictly better
with no regression") is not met — if anything this measurement points the other way. `coop.ini`
in both install directories had inherited `party_fast_pump=1` live from round 69's own smoke
test (never in source, `CoopConfig.cs`'s own default was already `false`); both were returned to
`=0` after this pass so the installs match the shipped default again.

NEEDS-LIVE, still open: a full 3-minute-per-leg version of this same test; a companion-position-
lag metric distinct from raw frame time; isolating whether the ~200-430 ms per-leg spikes are
kill-event-shaped or something `party_fast_pump` itself can still affect at a longer sample size.

## 2026-08-23 — `party_fast_pump` A/B, non-combat this time (round 75): still LEFT OFF, and the
position-lag metric this pass wanted turned out to be unmeasurable for a concrete, disassembly-
relevant reason

The user's own report ("lag even on localhost") reads more like companion-mirror latency during
ordinary walking than a combat-specific effect, which is exactly what this section's suspect #2
targets and what the round above never tested. This pass: `docs/research/party-reconciliation
.md`'s round 75, `tar/END_M01AA`, both instances, `frame_stats=1`, 120 s per leg, no combat.

### What was measured: frame time — no meaningful difference either way

| | `party_fast_pump=1` | `party_fast_pump=0` |
| --- | --- | --- |
| Host mean frame time | 1.91 ms | 2.01 ms |
| Client mean frame time | 1.88 ms | 1.96 ms |
| Host worst single 10 s bucket | 17.9 ms | 7.4 ms |
| Client worst single 10 s bucket | 179.1 ms | 133.0 ms |
| Client frames over 33 ms budget (of ~79,000 sampled) | 1 | 1 |

Both legs sit in the same ~1.9-2.0 ms band on both instances — an order of magnitude below the
combat legs above, as expected outside a fight — with one single-frame spike apiece on the
client (not the host), most plausibly ordinary per-module housekeeping rather than anything tied
to the flag, since it appears in both legs at a similar magnitude. **No frame-time regression
either way; no case for flipping the default.**

### What could NOT be measured: the companion position-lag metric this pass was asked for

The task's own ask was host-write-timestamp vs. client-apply-timestamp for the SAME sample. Two
independent attempts, both blocked:

1. **The natural signal doesn't exist.** Trask's real follow-AI does not walk — the exact bug
   `party-reconciliation.md`'s rounds 71-75 spend their whole length on — so there is no organic
   position change on the host to time in the first place. Sampling his real position
   (`tools/phantomgait.ps1 -ObjectId 0x127`) on the host for the whole 120 s window in EITHER leg
   showed exactly one distinct `(x, y)` value: he never moves.
2. **A manufactured signal gets reverted before it propagates.** `party-reconciliation.md`
   round 73's own "test staging only" technique — a raw `WriteProcessMemory` poke to the resolved
   creature's `+0x90` position field, cycling through a small patrol every 3 s — was tried as a
   substitute (`console 'resolve 0x127'` first, to get the real engine-resolved address rather
   than a vtable-scanned guess, which the first attempt got wrong and which explains why an
   EARLIER attempt at this exact test found zero propagation at all). With the correct address,
   the poked value DOES occasionally reach the client — but only as a single 250 ms sample before
   snapping back to the pre-poke value, on both `party_fast_pump=1` and `=0` equally (client CSV:
   `19.01311,21.150425` steady, one `32,22`-shaped flash roughly every 30-45 s despite pokes
   every 3 s, i.e. most individual pokes never survive long enough on the HOST to be read at all).
   Since the reversion happens at the same rate and magnitude under both settings, it is not
   something `party_fast_pump` itself causes — it is the host's own per-frame system re-asserting
   a stationary companion's cached position faster than either the external poke or the publish
   channel underneath it, independently confirming `party-reconciliation.md` round 75's own
   `CSWCCreature::SetPrecalculatedPosition -> CSWPartyFollowDataArray::ProcessPoint` disassembly
   finding from the same round's pathfinder-cursor work (a live creature's position is not a bare
   field a plugin can just overwrite and expect to stick, even briefly, for a body the engine's
   own movement system considers "not moving").

**Verdict: default stays OFF**, per the combat round's own finding, since this pass supplies no
evidence to override it — a null result on frame time, and an inability to produce the requested
lag metric at all, are not "strictly better with no regression." `coop.ini` in both installs
restored to `party_fast_pump=0` (the shipped default) after this pass.

NEEDS-LIVE, still open: the companion-position-lag metric, blocked on the same "the companion
does not genuinely move" root cause `party-reconciliation.md` itself is chasing — this is likely
only cleanly measurable once that bug is fixed, or against a companion driven by real, continuous
engine movement (a genuine walk, not a teleport or a poke) rather than any staging technique tried
so far.

## 2026-08-24 — `party_fast_pump` A/B round 3: the follow-AI confound is gone, the
position-lag metric finally measures cleanly, and it is decisive — default flipped ON

`party-reconciliation.md`'s rounds 80/84/88 (this same session, earlier) fixed the companion
follow-AI's permanent stall: Trask now genuinely walks and closes real distance behind a moving
leader, down to her last 1-3 recorded path nodes. That is exactly the organic-motion signal the
two rounds above (2026-08-23) needed and never had — this round re-runs the exact experiment they
describe (host-write vs. client-apply position, same technique, `tools/phantomgait.ps1 -ObjectId
0x127`) now that there is real motion to sample.

### Method

Recipe confirmed reliable by `party-reconciliation.md` round 80/84/88: `tp 20 22 -1.3` (a known-
good origin — other origins are flaky, see round 80's own note on `ActionMoveToLocation` no-
opping for a drive-controlled PC) then `walk 10 15`, a genuine ~10.8-unit leg. Repeating this
cycle (tp, 500 ms settle, walk, ~5-6 s gap) for the length of a leg produces several seconds of
real per-frame companion motion per cycle, cleanly separated by idle gaps.

`tools/phantomgait.ps1` was run **simultaneously on the host process and the client process**,
sampling Trask's position (`objectId 0x127`) every 100 ms into a CSV each, for the whole leg.
Because both instances share the same OS clock (one PC, two `swkotor.exe` processes), the two
CSVs' timestamps are directly comparable with no cross-process clock-sync needed — the exact gap
the two 2026-08-23 rounds identified but could not fill for lack of a moving companion to time.

Analysis (`analyze_lag.ps1`, scratch tooling, not shipped): find contiguous "active bursts" in
the HOST trajectory (Trask genuinely moving, not frozen between cycles); for each burst, search
candidate lags 0-900 ms in 10 ms steps and pick the one that minimizes mean-squared distance
between the CLIENT's position at time *t* and the HOST's (linearly-interpolated) position at time
*t − lag*, evaluated across the burst's own window. This directly answers "how many ms behind is
the client's rendered companion position, relative to the host's real one," per burst; residual
RMSE at the best lag (mostly 0.2-1.0 game units, small relative to the ~10-unit legs) confirms
the fit is meaningful, not noise.

### Result: reproduced twice, ~45-55% lower median lag with the flag on

`END_M01AA`, save slot 1, Trask recruited. Two independent ~55-65 s legs per setting (fresh rig
relaunch between settings, since `party_fast_pump` is read once at agent startup):

| | `party_fast_pump=0` (old default) | `party_fast_pump=1` |
| --- | --- | --- |
| Run 1 — bursts used | 13 | 14 |
| Run 1 — median lag | **350 ms** | **160 ms** |
| Run 1 — mean lag | 396.9 ms | 199.3 ms |
| Run 1 — min/max | 210 / 810 ms | 20 / 540 ms |
| Run 2 — bursts used | 8 | 9 |
| Run 2 — median lag | **320 ms** | **160 ms** |
| Run 2 — mean lag | 382.5 ms | 180.0 ms |
| Run 2 — min/max | 260 / 870 ms | 160 / 290 ms |

Consistent both times: `=1` reads **160 ms** median in both independent runs; `=0` reads
320-350 ms. A third smoke leg run AFTER the code default was flipped (no explicit `coop.ini`
override, confirming the shipped default actually behaves this way) read **190 ms** median,
7 bursts — same band as the two `=1` measurement runs.

**This lines up with the mechanism exactly.** Companions never go through the interpolation
buffer (`PartyPresenceController`'s own doc comment: "companions arrive on the ~150 ms combat
pump, not the 30 Hz tick"). Old cadence: up to a ~150 ms wait for the combat-pump gate to open,
plus ordinary network/pump hops, plus the ~150 ms `mirror_smooth_ms` smoother on the far side —
a worst case in the 300-400 ms range, matching the measured `=0` median almost exactly. New
cadence removes the combat-pump wait entirely (party rides the same real-time per-poll-tick path
dialogue already uses): what is left is essentially just the smoother's own ~150 ms window plus
network/pump hops — matching the measured `=1` median almost exactly. Two independently-derived
numbers (one from reading the code's own cadence constants, one from a live host-vs-client
position trace) land in the same place.

### Regression check: frame time and agent pump, clean in every leg

`frame_stats=1` throughout. Every leg, both settings, both instances: ~1.8-2.1 ms mean frame
time, 0 frames over the 33 ms budget. `lagstats`' agent-pump reading stayed negligible (<1 ms
average) throughout. No frame-time cost from moving `PumpParty`/`PumpPartyRoster` onto the fast
path — consistent with `CoopConfig`'s own reasoning that these are cheap block reads/writes, not
a world scan.

A light combat spot-check (`END_M01AA`'s own `end_weld`, id `0x35`, staged via `tp` + the
`attack` console verb — a single low-HP enemy, not `party-reconciliation.md` round 71's heavier
`tar_m10aa` Vulkar-pool fight) showed no regression either: `=0` combat window read 2.44-3.00 ms
host / 2.05-2.22 ms client mean frame time; `=1` read 2.27-2.40 ms host / 2.00-2.14 ms client (one
162/214 ms spike apiece at the moment of the `tp`+engagement event itself, not sustained — 0
further over-threshold frames on either side). This does **not** fully re-run round 71's own
heavier, multi-enemy, sustained-combat scenario (out of scope this pass, time-limited) — round
71's own 53%-worse combat frame-time finding was measured against a companion that could not move
at all (this session's own follow-AI fix postdates it) and is left as a documented, not-fully-
retested caution for a future heavier-combat pass, rather than either overridden or ignored.

### Verdict: **default flipped ON**

This clears the project's own established bar (`dialogue_fast_join`, `client_load_rejoin`/
`host_load_readopt`): a direct, decisive improvement on the metric the flag exists for
(companion-position lag, ~45-55% lower median, reproduced twice independently plus one
post-flip smoke confirmation), with no regression observed in frame time, agent pump cost, or a
light combat check. `CoopConfig.PartyFastPump`/`AgentSettings.PartyFastPump` now default to
`true`; `party_fast_pump=0` still reverts to the old cadence. This closes the "does moving the
party channel off the 150 ms combat-pump cadence reduce companion-position lag" question
definitively — three A/Bs across three confound-states (combat/frozen, non-combat/frozen,
non-combat/genuinely-moving) — the first two null/negative because there was no real motion to
measure a benefit against, the third decisive once there was.

NEEDS-LIVE, still open: a full re-run of round 71's own heavier, sustained multi-enemy combat
scenario now that the follow-AI fix is in place (this pass's own combat check was intentionally
light, time-limited); a genuinely human-driven (non-scripted) walk/run test, since every A/B this
project has run for this flag has driven the leader through the `tp`+`walk` console/test-harness
path rather than real OS input.

## 2026-08-24 — a real per-tick redundant-work source found and fixed by static analysis (no rig
access this pass); heavier-combat re-check still NEEDS-LIVE, not closed by this change

Task: re-examine `party_fast_pump`'s heavier-combat regression (round 71, above — 53% worse mean
frame time, ~40% more over-budget frames, `tar_m10aa`'s Vulkar pool) now that the follow-AI fix
(rounds 80/84/88) has landed, and look specifically for redundant per-companion-per-frame work
that scales worse under a heavier fight. No live rig access this round — static code analysis and
a targeted fix only; the actual heavier-combat re-measurement this task exists to eventually
answer is explicitly **not** attempted here.

### What `party_fast_pump=1` actually changes, precisely

`CoopAgent.PumpParty`/`PumpPartyRoster` (`src/KotorCoop.Session/CoopAgent.cs:571-575`) move off the
~150 ms `CombatPumpIntervalMs` gate onto every `PumpOnce()` — i.e. every poll tick, which both
installs' `coop.ini` set to `tick_rate = 60` (~16.7 ms), per this doc's own 2026-08-22 measurement
section. That is roughly a 9x increase in HOW OFTEN `KotorGameBridge.TryWritePartyMembers` runs on
the client — the only side that calls it (`PumpParty`'s own doc comment: "Host-authoritative...
exactly one instance's AI is ever in charge"; the host calls `ReadPartyMembers`/`PublishParty`
instead, never this path). Each call: one solo-mode byte write (always, correctly — see below),
then for each of the 4 party-presence slots, up to 9 separate `WriteProcessMemory`-backed field
writes (`src/KotorCoop.Memory/KotorGameBridge.cs`, `TryWritePartyMembers`'s plugin-block branch) —
**unconditionally, every call, regardless of whether that slot's data had changed since the
previous call.**

### Why this is the mechanism, not just "more calls, but each one is cheap"

Round 71's own note (carried into this doc's 2026-08-24 A/B section, "light combat spot-check")
already says the plain part: *"round 71's own 53%-worse combat frame-time finding was measured
against a companion that could not move at all (this session's own follow-AI fix postdates it)."*
Read together with the unconditional-write finding above, this is not a coincidence — it is the
whole story. A companion whose follow-AI cannot move has an UNCHANGING `PartyMemberState` for the
entire session: same position, same facing, same animation, same health, same one-shot serial,
every single pump. At 60 Hz that is up to ~36 field writes/second of **byte-identical data**,
9x more often than the pre-`party_fast_pump` 150 ms cadence sent the same unchanging bytes. None
of that work was ever necessary — the block already held the correct value from the write before
it — and it is pure, avoidable cross-process write traffic landing on the SAME agent thread that
also runs the enemy census, creature-liveness and phantom-damage scans on the 150 ms combat-pump
path (this doc's own Table 1/Table 2), sharing CPU and, on Windows, kernel-transition cost with
whatever the game process itself is doing that frame. A heavier fight is exactly the condition
with the least spare budget to absorb 9x the necessary write volume: more enemies for
`EnemyPresenceController`'s own unconditional per-frame scan (this doc's Table 1) to chew through
on the GAME side, at the same moment the AGENT side is burning cycles re-sending data nothing
downstream needed re-sent. This reframes round 71's regression from "`party_fast_pump` itself is
expensive" to "`party_fast_pump` moved a write nobody had made cheap yet onto a 9x faster
cadence, and the old 150 ms gate was accidentally hiding that cost."

This does **not** mean the regression is fully explained — round 71's fight was staged specifically
because companions could not move, which is no longer true after rounds 80/84/88, so a companion
in a REAL heavier fight now genuinely changes position most passes and would not benefit from a
dirty check nearly as much as round 71's frozen companion did. The fix below removes a real,
confirmed-redundant cost; it does not by itself prove the heavier-combat case is now clean, which
is exactly why the re-check below is NEEDS-LIVE, not closed.

### The fix: `PartyPresenceDirtyCheck` — skip a slot's write when nothing changed, never skip
across a module boundary

`src/KotorCoop.Memory/PartyPresenceDirtyCheck.cs` (new, pure, unit-tested) + the corresponding
per-slot cache in `KotorGameBridge.TryWritePartyMembers`
(`_lastWrittenPartyPresence`/`_lastWrittenPartyPresenceCount`/`_partyPresenceModule`): before
writing a party-presence slot, compare the candidate `PartyMemberState?` (or "empty") against what
was actually written to that slot last time, and skip the write when they match exactly (position,
facing, animation, health AND one-shot serial all equal — a fresh swing/parry one-shot always
forces a write even if everything else is static). The `BlockPartyPresenceCount` scalar gets the
same treatment — skipped only when both the count and every slot are unchanged.

**The one case content-equality must not be trusted alone**: `PluginHost.Invalidate()` clears the
shared block's party-presence slots IN-PROCESS on every module unload/epoch change
(`src/KotorCoop.Plugin/PluginHost.cs`), independent of anything this agent wrote or cached. A
companion whose reported position happens not to change across a transition (the common case, not
a rare one) would otherwise leave the block silently at its post-`Invalidate` empty state forever,
because the agent's own cache would see "unchanged" and skip re-populating it. The fix reads the
current module every call (`TryReadCurrentModule`, already a cheap, already-used-elsewhere engine
read — the same pattern `_doorModule`/`_placeableModule`/`_lootModule`/`_encounterModule` already
use for the identical hazard) and forces an unconditional full rewrite of every slot, plus a reset
of the whole cache, whenever the module differs from the last call (including "could not be read
at all," which biases toward an extra write rather than a wrongly-skipped one). The solo-mode byte
write itself is untouched — still unconditional every call, exactly as before; it was never the
cost source and re-asserting it constantly is deliberate, not redundant.

### Test suite and build

`dotnet test tests/KotorCoop.Tests` (Release): see this pass's own build log below. New:
`tests/KotorCoop.Tests/PartyPresenceDirtyCheckTests.cs` — first write is never skipped; identical
candidate is skipped; both-empty is skipped; a changed position/health/one-shot-serial/slot-index
is never skipped; identical content still forces a write when `moduleChanged` is true (both the
occupied and the both-empty case) — the exact hazard the module-boundary override exists to
prevent. `PartyFastPumpTests` (the existing end-to-end coverage of `party_fast_pump`'s own
CoopAgent-level cadence) is unaffected: it mocks `IGameBridge` directly, one layer above
`KotorGameBridge`, so it still asserts one `TryWritePartyMembers` CALL per tick — this change only
makes what happens INSIDE that call cheaper when nothing changed, never how often the call itself
happens.

### What this pass explicitly did NOT do

- **Did not re-run round 71's heavier-combat A/B.** No rig access this round. The dirty-check fix
  above is a real, reasoned, unit-tested optimization targeting a confirmed redundant-work source,
  but whether it is SUFFICIENT to close the 53%-worse gap under a real, heavier, sustained,
  multi-enemy fight with companions now genuinely moving (not frozen, unlike round 71's own
  staging) is unmeasured. A companion that is moving most passes during a real fight will dirty-
  check as "changed" most passes too, so the win this pass can most confidently claim is for the
  IDLE/slow-moving stretches of a fight (positioning, waiting between swings), not the swings
  themselves.
- **Did not add an enemy-count threshold or any other binary/graceful-degrade throttle.** Considered
  and rejected: a threshold would need live tuning this pass cannot do blind, and the dirty check
  above is strictly better where it applies (it costs nothing extra when data IS changing, unlike a
  cadence throttle that would reintroduce latency exactly when a companion is fighting hardest) —
  see docs/research/party-reconciliation.md's own "if unsure, do not flip it blind" precedent for
  why an untuned threshold was not shipped speculatively.
- **Did not touch the fallback (non-plugin) branch of `TryWritePartyMembers`.** That path writes
  directly onto resolved creature objects rather than the shared block and is not the one this
  doc's frame-time measurements (plugin-attached, `frame_stats=1`) exercise; changing it would add
  risk with no evidence it needs the same fix.
- **Did not change `party_fast_pump`'s default.** It stays ON, unchanged by this pass — this is an
  optimization underneath an already-shipped, already-decided flag, not a new A/B result.

### Verification tags (this pass)

- VERIFIED (source, this pass): `PumpParty`/`PumpPartyRoster`'s fast-path cadence and the
  client-only call site (`CoopAgent.cs`); `TryWritePartyMembers`'s unconditional per-field writes
  before this fix (`KotorGameBridge.cs`); `PluginHost.Invalidate()` clearing party-presence state
  in-process on a module change (`PluginHost.cs`); the existing `_doorModule`-style module-cache
  pattern this fix's own invalidation reuses.
- VERIFIED (unit test, this pass): `PartyPresenceDirtyCheck`'s decision table, including the
  module-boundary override.
- INFERRED, not measured: that round 71's regression is substantially explained by the frozen-
  companion confound plus this redundant-write mechanism — plausible and consistent with every
  fact on record, but not proven by a live measurement this pass.
- NEEDS-LIVE, still open and NOT closed by this pass: round 71's own heavier, sustained multi-
  enemy combat scenario, re-run with (a) the follow-AI fix in place, as the 2026-08-24 A/B round 3
  entry's light spot-check already partially did, and (b) this dirty-check fix in place, specifically
  measuring whether the previously-measured 53%-worse mean frame time / ~40%-more over-budget-frame
  gap narrows, closes, or persists. `frame_stats=1` on both installs, the same `tar_m10aa` Vulkar-
  pool recipe round 71 used (or an equivalent heavier multi-enemy fight), long enough to exceed the
  3-minute target every prior round in this file has fallen short of.

## Live-verified (2026-08-24) — round 71's heavier-combat regression re-checked with the follow-AI
fix AND the dirty-check fix both in place: the regression is GONE, not just narrowed

Rig access this round (plugin rebuilt/reinstalled fresh on both GOG installs first — the dirty-check
fix above was already merged onto this branch). `party_fast_pump=1` (shipped default,
unchanged in `coop.ini`), `frame_stats=1` on both installs, round 71's own `tar_m10aa` Vulkar-pool
recipe: host and client both given a real `attack 0x7FFFFFFF <id>` order against separate,
live Vulkars (`tools/blockpoke.ps1 attack`), god mode armed on both first so neither instance's
own PC dying interrupts the window. ~2 minutes of sustained real combat, re-arming fresh attack
orders against new Vulkars as each one died, to keep the fight going the whole window (three real
kills recorded — `combat: killed 0x36`/`0x38`/`0x2F on the owner's authority` — real damage
applied both directions throughout, `combat: applied N of a peer's damage to 0x24`/`0x33`).

### Result: dramatically better than even round 71's own OLD (`=0`) baseline, not just its `=1`

| | Round 71 `=1` (old, pre-fix) | Round 71 `=0` (old, pre-fix) | This round `=1` (both fixes in place) |
| --- | --- | --- | --- |
| Host mean frame time (combat) | 9.63 ms | 6.30 ms | **3.62 ms** |
| Client mean frame time (combat) | 7.15 ms | 5.52 ms | **2.55 ms** |
| Frames over 33 ms budget (combat window) | 197 host / 217 client | 138 host / 123 client | **0 / 0** |

Twelve consecutive 10-second `frame_stats` samples per instance, spanning the entire sustained
combat window (damage/kills recorded throughout), averaged directly from the plugin log's own
periodic lines — the same measurement method round 71 used. Zero frames exceeded the 33 ms budget
anywhere in the combat window on either instance; the only over-budget frames seen all session
(one ~162 ms spike on the host, one ~155 ms spike apiece on the client, twice) landed BEFORE combat
started, during the `travel` warp's own world-load settle — the same "kill/death-event-shaped,
not combat-shaped" spike pattern round 71 itself already flagged as unrelated to the flag, now
confirmed to sit outside the fight entirely rather than during it.

This is not merely "the 53%-worse gap closed" — mean frame time during real, sustained,
multi-Vulkar combat with `party_fast_pump=1` and both fixes in place (3.62/2.55 ms) is now
noticeably BETTER than round 71's own OLD `=0` numbers (6.30/5.52 ms), which were themselves the
"good" side of that original regression. Consistent with both fixes' own reasoning: rounds
80/84/88 gave companions real, continuous follow-AI motion (so a companion's `PartyMemberState`
genuinely dirties most passes it should, rather than sitting frozen and unable to benefit from
anything), and the per-tick dirty-check (this same document's own 2026-08-24 entry above) removes
the specific unconditional-rewrite cost round 71's own frozen-companion staging could not have
exposed as the real driver. Both explanations were inferred from static reasoning before this
round; this is the first live measurement that confirms them together, on the exact scenario round
71 itself used.

### Verdict

Round 71's 53%-worse mean frame time / ~40%-more over-budget-frame regression is **closed**, not
merely narrowed, once both the follow-AI fix and the dirty-check fix are in place — the combined
fix set outperforms even the pre-`party_fast_pump` baseline on this exact recipe. `party_fast_pump`
stays default ON (`e7663c2`/round-90-adjacent flip, unchanged by this pass); nothing here argues
for reverting it. Per this task's own framing, `party_fast_pump`'s "still needs work" status can be
considered resolved on the heavier-combat axis specifically — the one open question this whole
document's `party_fast_pump` thread had left outstanding since round 71.

Not re-run this pass, still open in principle: an even longer (3+ minute) window than the ~2
minutes this pass used, and a genuinely human-driven (non-scripted) combat session — every A/B for
this flag, this pass included, has driven combat through the `attack` console/test-harness path
rather than real OS input, per this document's own long-standing caveat.

### Test suite and build

`dotnet test`: no production code changed this pass (a live-verification run only).

## P0-2: `WorldSnapshotPacket` graceful degradation — paged like `CreatureLiveness` (2026-08-27)

A protocol audit finding, not a live-verification round: `WorldSnapshotPacket` was all-or-
nothing per tick. `CoopHost.Tick()` built ONE packet for the whole peer roster and, if
`PacketCodec.Write` returned 0 (the aggregate entry size overran
`ProtocolConstants.MaxPacketSize`), it logged once and simply did not send it — for as long as
the lobby stayed that full, EVERY peer's position sync stopped, silently, every tick, with only
a single log line marking the moment it started. `MaxPacketSize` had already been raised once
before (1200 → 1400, see its own doc comment) for exactly this failure shape; raising it again
only ever buys back one more growth cycle before the next field on `PlayerState`, a longer
`MaxNameLength`, or a larger `MaxPeers` reopens the identical bug.

The fix generalises the pattern `CreatureLivenessPacket` already uses for its own wider publish:
page the per-peer entries across multiple datagrams per tick instead of dropping the tick
whole. `WorldSnapshotPacket` gains `PageIndex`/`PageCount`, protocol version 43. Two differences
from `CreatureLiveness`'s pagination, both deliberate:

- **The tick id is `ServerTimeMs` itself, not a separate `Generation` counter.** `CoopHost.Tick()`
  already computes `now` once and stamps it on the packet; `CreatureLiveness`'s broadcast is
  throttled to one cycle every ~2 s and needed its own counter, but `WorldSnapshotPacket` is sent
  every tick at up to 30 Hz, so the existing per-tick timestamp is already a free, adequate
  grouping key — no extra wire bytes spent on a redundant counter.
- **Pages apply independently, not accumulate-then-replace.** `CreatureLiveness`'s receiver
  replaces its whole accumulated set only once a cycle looks complete; `WorldSnapshotPacket`'s
  pages are read every tick and carry AVATAR POSITIONS, so a peer named only on page 2 must not
  wait on page 1 — `WorldSnapshotAssembler.Apply` hands back that page's own entries
  unconditionally, and `CoopClient.HandleWorldSnapshot` pushes them into `SnapshotBuffer`
  immediately. Only the roster-wide decisions (pruning a peer who is genuinely gone via
  `SnapshotBuffer.RetainOnly`; trusting the flag/item/journal/loot version+digest tripwires) wait
  for `WorldSnapshotAssembler.TickComplete` — and even those just skip a tick rather than block,
  since the very next tick starts an independent cycle regardless of whether the last one ever
  completed.

The four world-store tripwires (`FlagVersion`/`ItemVersion`/`JournalVersion`/`LootVersion` and
their four digests) ride on page 0 only: `PacketCodec.Write(WorldSnapshotPacket)` does not even
put them on the wire for a later page, and `ReadWorldSnapshot` reports them as 0/default off
anything but page 0 — 48 bytes saved per non-zero page, and a receiver simply cannot read a
stale or duplicated tripwire off a page it should not trust.

`ProtocolConstants.MaxSnapshotEntriesPerPage = 5`, sized against page 0's own tighter per-page
budget (it additionally carries the 48 bytes of tripwire fields a later page does not), with the
fit asserted by `WorldSnapshotPagingTests.AFullEightPeerLobbyAtWorstCaseSizeSpansPagesWithNoneDropped`
rather than trusted by arithmetic alone — the same discipline `FullPeerSnapshotFitsInOneDatagram`'s
own doc comment warns is easy to get wrong by accident. A full 8-peer lobby now needs two pages
per tick; `WorldSnapshotPacket.MaxPages = 4` bounds it well beyond that, and a tick that somehow
still overflows drops the excess entries and logs once (latched, like the old whole-snapshot
guard), rather than growing the send unboundedly. `SessionStats.WorldSnapshotPagesSent` (host
only) makes the actual per-tick page count visible alongside `lagstats`'s other counters, so a
live round can confirm pagination is doing real work rather than sitting dormant.

The single-page case (a lobby of `MaxSnapshotEntriesPerPage` or fewer peers) is two bytes larger
on the wire than before this change (`PageIndex`/`PageCount`) but otherwise unchanged — the
pinning tests were updated deliberately for the new constructor shape rather than left byte-
identical, since adding the paging fields is itself the wire-format change protocol version 43
exists to mark.

### Verification tags (this pass)

- VERIFIED (unit test, this pass): `WorldSnapshotPagingTests` (host-side pagination arithmetic,
  including the full 8-peer worst-case-size scenario and the `MaxPages` overflow/drop/log path);
  `WorldSnapshotAssemblerTests` (client-side reassembly, including
  `ALostPageDoesNotStallTheOthers` and the fresh-cycle-per-tick-id reset);
  `PacketCodecTests.WorldSnapshotTripwireFieldsAreDroppedOnANonZeroPage` (the exact 48-byte
  saving and the 0/default read-back on a non-zero page).
- VERIFIED (loopback integration test, this pass): `EightPeerSessionTests
  .AllEightPeersSeeEachOthersPositionsAcrossSeveralPages` — a real host and seven real clients
  over loopback UDP, each peer's own distinct position, asserting every one of the 8 peers sees
  every other peer's real (not origin, not swapped) position, and that
  `SessionStats.WorldSnapshotPagesSent` actually climbs across several ticks rather than staying
  at the single-page-per-tick figure a coincidental fit would produce.
- NEEDS-LIVE, not yet observed running: an actual live 8-peer (or even 3+ peer) session against
  real `swkotor.exe` instances confirming positions still converge smoothly under real network
  conditions with pagination active — this pass is a protocol-layer audit finding plus loopback
  UDP verification, not a live-rig round.

### Test suite and build

`dotnet build KotorCoop.slnx -warnaserror`: clean. `dotnet test tests/KotorCoop.Tests --no-build`:
full suite, see this task's own report for the pass/fail tally.

## 2026-08-28 — cross-thread pending-apply queue audit: `LootPump`/`StorePump` had the identical
## unguarded `Queue<T>` root cause 5 (docs/research/party-pool-sync.md §24) fixed in
## `PartyPoolPump`; both now fixed, and — unlike root cause 5's own test — the new regression
## tests reliably reproduce the pre-fix corruption

Task: party-pool-sync.md §24 fixed root cause 5 (`PartyPoolPump._pendingApplies`, a plain
`Queue<T>` written by `CoopAgent.RunAsync`'s tick loop and drained by
`CoopCommandDispatcher.RunAsync`'s independently-scheduled loop, with no lock) and flagged
`LootPump._pendingLoot` as carrying the identical shape, out of scope for that pass. This pass
audits every producer/consumer queue crossing that same agent-tick-loop/dispatcher-loop boundary,
fixes whatever else carries the same defect, and adds a concurrency stress test per queue.

### Audit method and scope

`grep -rn "TryDequeuePending" src/KotorCoop.Session src/KotorCoop.Agent` finds the full set of
consumer entry points `CoopCommandDispatcher` calls into `CoopAgent`: `TryDequeuePendingLoot`
(`CoopCommandDispatcher.Items.cs`), `TryDequeuePendingPartyPoolApply`
(`CoopCommandDispatcher.PartyPool.cs`), `TryDequeuePendingStoreApply`
(`CoopCommandDispatcher.Store.cs`) — each a thin forward from `CoopAgent` into one pump
(`_lootPump`/`_partyPoolPump`/`_storePump`). A broader `grep -rn "Queue<"` / `"Dequeue"` across
all of `src/` turns up every other `Queue<T>` in the codebase (`RelayQueue<T>`'s inbox/outbox,
`CoopHost`/`CoopClient`'s per-channel inbox/outbox queues for barks/chat/dialogue-grants/movies/
materialised-loot/store-op-drain, and several `(Origin, Sequence)` op-history queues in
`ContainerLootStore`/`EventFlagStore`/`GroundItemStore`/`PartyPoolStore`/`StoreStore`/
`DialogueGrantSender`) — none of these cross the agent-tick-loop/dispatcher-loop boundary this
task is about: they are either already `Lock`-guarded at their own read/write sites (the
`_storeOpDrainQueue`/`_chatOutbox`/etc. queues written from the socket-receive path and drained
from the session's own `RunAsync`, all inside `CoopHost`/`CoopClient`, all already gated by a
`Lock` field — confirmed by reading each file) or are touched exclusively from a single thread
(a pump's own per-tick bookkeeping, e.g. `PartyPoolPump._publishHistory`'s per-key `Queue<long>`,
written and read only from `Pump()` itself, never from `TryDequeuePendingApply`). "Cheat replies"
(the task brief's third named category) resolve to `CoopCommandDispatcher`'s own
`_pendingCheatSerial`/`_afterCheat` fields (`CoopCommandDispatcher.cs`) — not a queue at all, and
not cross-thread: both are written and read exclusively from methods the dispatcher's own
`RunAsync` loop calls (`SendCheat`, and whatever polls the shared-block answer), never touched
from `CoopAgent`'s tick loop. Confirmed out of scope by reading the file, not merely by absence
from the `TryDequeuePending*` grep.

That leaves exactly three queues sharing the party-pool root-cause-5 shape — a plain `Queue<T>`
written by a pump's own `Pump()`/producer method (called from `CoopAgent.RunAsync`'s tick loop)
and drained by a `TryDequeuePending*` method (called from `CoopCommandDispatcher.RunAsync`'s
independently-scheduled loop, confirmed via `AgentComposition.cs` starting both as separate tasks
with no shared synchronization context — same confirmation §24 already did):

| Queue | Producer | Consumer | State before this pass |
|---|---|---|---|
| `PartyPoolPump._pendingApplies` | `Pump()` step 2 | `TryDequeuePendingApply` | Already fixed, §24 (root cause 5) |
| `LootPump._pendingLoot` | `PumpMaterialisedLoot` | `TryDequeuePendingLoot` | Unguarded — flagged by §24, not fixed |
| `StorePump._pendingApplies` | `Pump()` step 2 | `TryDequeuePendingApply` | Unguarded — not previously flagged; found this pass |

### Fix: `Lock`, not `ConcurrentQueue<T>`

Kept the `Lock` discipline `PartyPoolPump` already established rather than switching either queue
to `ConcurrentQueue<T>`: both producers perform a compound "check the bound, then enqueue"
operation (`if (_pendingX.Count >= MaxPendingX) { log/drop; } else { _pendingX.Enqueue(...); }`)
that has to be atomic — `ConcurrentQueue<T>.Count` racing its own `Enqueue` from another thread
can no more be trusted as an accurate bound check than the two independent `Interlocked` ops a
naive `ConcurrentQueue` conversion would still need to fake the same atomicity. A `Lock` around
both the check and the enqueue is simpler and exactly matches the sole other instance of this
pattern in the codebase, so a future reader of any of the three pumps sees the identical idiom.

`LootPump.cs`: `_pendingGate` (`Lock`) added, guarding both enqueue sites in
`PumpMaterialisedLoot` (materialised-loot gives and dialogue-grant gives both feed the same
queue — see the class's own remarks) and `TryDequeuePendingLoot`.

`StorePump.cs`: `_pendingGate` (`Lock`) added, guarding both enqueue sites in `Pump()`'s step 2
(the `StoreOpKind.Set` branch and the `Add`/`Remove` branch each have their own
count-check-and-enqueue) and `TryDequeuePendingApply`.

### Regression tests — and a stronger result than root cause 5's own test got

Both `MaterialisedLootQueueTests.ThePendingLootQueueSurvivesConcurrentEnqueueAndDequeueWithoutLosingOrDuplicatingEntries`
and `StorePendingApplyQueueConcurrencyTests.ThePendingApplyQueueSurvivesConcurrentEnqueueAndDequeueWithoutLosingOrDuplicatingEntries`
mirror `PartyPoolLoopbackTests`'s own stress shape (§24.1): one thread races 8,000
distinct-keyed `CoopAgent.PumpOnce()` producer calls against a separate, genuinely independent OS
`Thread` spinning the matching `TryDequeuePending*` consumer with no poll delay, to maximise
exposure to the dangerous internal-array-resize window.

The Loot queue's own test cannot assume zero drops the way the party-pool test does — that test's
producer tick does far more unrelated work per call (`HeavyPumpIntervalMs = 0` pulls in the whole
world-state pass), giving its consumer thread time to keep up with an effectively-unbounded
`MaxPendingApplies`. `LootPump.PumpMaterialisedLoot`'s own tick is comparatively cheap, so the
producer genuinely can outrun the consumer and legitimately trip the 256-entry cap a handful of
times over 8,000 iterations — `MaxPendingLoot` doing its job, not a bug. The test accounts for
this explicitly instead of asserting no drops: every produced item must be either delivered
exactly once or logged dropped exactly once, never both, never neither (verified by intersecting
the observed-resref set against a set parsed from the `"dropping materialised loot '...'"` log
lines). The Store queue's test uses `HeavyPumpIntervalMs = 0` like the party-pool test and did not
need the same accounting — it asserts zero drops directly, matching `PartyPoolLoopbackTests`'s own
assumption, and that assumption held.

Both tests were then run against a temporarily-reverted fix (`lock (_pendingGate)` mechanically
replaced with `if (true)` in both files, leaving every other line — including the count-check —
byte-identical, so the ONLY behavioural change is the missing lock) to check whether they are
mechanically-confirmed regression pins, the way §24.3 honestly recorded its own inability to force
root cause 5's `PartyPoolPump` test to fail pre-fix. **Both failed reliably, 3 out of 3 runs each,
against the unguarded code** — the Loot test caught a duplicate dequeue (a torn read handing back
the same, sometimes even a stale/default `default(PendingMaterialisedLootGive)`, entry twice — one
run's failure was literally `''` dequeued twice, the empty-string resref of a zeroed-out struct);
the Store test caught outright loss (7,996–7,998 observed out of 8,000 produced, several runs).
Reverted back afterward (confirmed via the file's `if (true)` count returning to zero and `lock
(_pendingGate)`'s count returning to three in each file) and the full targeted filter plus the
complete suite re-run clean against the real fix — see below. Unlike root cause 5's own test, both
of these ARE mechanically-confirmed failing-before/passing-after regression pins, not merely a
real hazard fixed on principle.

### Files changed

`src/KotorCoop.Session/Pumps/LootPump.cs`: `_pendingGate` (`Lock`) added; both
`_pendingLoot.Enqueue` sites and `TryDequeuePendingLoot` now guarded.

`src/KotorCoop.Session/Pumps/StorePump.cs`: `_pendingGate` (`Lock`) added; both
`_pendingApplies.Enqueue` sites (the `Set` branch and the `Add`/`Remove` branch) and
`TryDequeuePendingApply` now guarded.

`tests/KotorCoop.Tests/MaterialisedLootQueueTests.cs`: added
`ThePendingLootQueueSurvivesConcurrentEnqueueAndDequeueWithoutLosingOrDuplicatingEntries`.

`tests/KotorCoop.Tests/StorePendingApplyQueueConcurrencyTests.cs` (new file): a minimal
`ICoopSession`/`IGameBridge` stub pair (no real host/client/sockets needed — `StorePump` only
needs `DrainStoreOps()` under this pump's own control to exercise the race) plus
`ThePendingApplyQueueSurvivesConcurrentEnqueueAndDequeueWithoutLosingOrDuplicatingEntries`.

No plugin-side source touched this pass — `PartyPoolPump`'s own fix (§24) already covers the one
plugin-adjacent instance of this shape, and `party_pool_sync`/`store_sync` are both still off by
default, so this pass has no live-verification obligation of its own beyond the bench-level
mechanical confirmation above.

### Build and test results

`dotnet build KotorCoop.slnx -warnaserror`: clean (0 warnings, 0 errors).

`dotnet test tests/KotorCoop.Tests --no-build --filter "FullyQualifiedName~Loot|FullyQualifiedName~Store|FullyQualifiedName~PartyPool"`:
318/318 passed (317 pre-existing + the 1 new Loot test; the new Store test file's single test is
also `~Store`-matched, so 318 total includes both new tests).

`dotnet test tests/KotorCoop.Tests --no-build` (full suite): **2,475/2,475 passed**, 0 failed, 0
skipped.

## 2026-08-29 — "the other player catches up in bursts": the cause was a full 180 MiB memory
## scan running on the position thread once a second, and it is now off that thread entirely

Player report: on the local rig (two instances, one machine, loopback) the other player's
movement arrives in bursts rather than smoothly, and animations such as opening a container play
slowly and stutter. This section is the measurement that found the cause and the numbers before
and after.

The premise every previous round of this document worked from — that the agent pump is cheap
(0.3-0.5 ms average, ≤3.1 ms peak, measured 2026-08-22) and the game's frame time is fine
(1.8-2.7 ms mean) — was true and also beside the point. Both instruments average. `lagstats`
reports the pump's mean and peak over the last 100 ticks and `frame_stats` reports frame time,
and NEITHER reports the thing a player actually watches: how often the position written into the
game changes, and by how much when it does. That gap is what `perf_trace` was built to close.

### New instrument: `coop.ini perf_trace`

`PerfTrace` (`src/KotorCoop.Session/PerfTrace.cs`) writes one line a second into the agent log:
ticks per second, tick cost split into the real-time path and the heavy world pass, a per-phase
table of `PumpOnce`'s slices (`PumpPhase`: position, spawnring, combat, dialogue, party, loot,
scriptitem, heavy) in both milliseconds and cross-process reads, packets and bytes both ways,
and — the metric nothing had — the GAP and DISTANCE of each position change actually applied to
a phantom. `MemoryTelemetry` (`src/KotorCoop.Memory/MemoryTelemetry.cs`) counts the
`ReadProcessMemory`/`WriteProcessMemory` calls and bytes behind that, gated on a static bool so
it costs one relaxed read per call when off.

A stream that updates every 33 ms by 0.13 units and one that updates every 66 ms by 0.26 units
have identical averages, identical pump costs and identical packet counts. The second is the
bug. `phantom gap ... avg/peak` is what tells them apart.

### What the first measured line said

Rig, END_M01AA, both instances connected, nobody moving, `perf_trace=1`:

```
perf: 12.8 ticks/s, tick 49.57/636.4 ms avg/peak, position path 0.24 ms, heavy 1x 0.5/0.5 ms
  | rpm 8466 reads/s (185994.0 KiB/s, 1 failed) | ...
  | phases position 3.1ms/s; combat 2.1ms/s 1608r/s; party 2.2ms/s 1403r/s;
    scriptitem 625.9ms/s 2500r/s 181.6MiB/s; heavy 0.5ms/s;
```

The pump was running at **12.8 Hz against a 30 Hz timer**, its average tick cost **49.6 ms
against a 33 ms budget**, with individual ticks of **636 ms** (2,409 ms was seen). The real-time
path itself cost **0.24 ms** and the heavy pass **0.5 ms** — so neither was the defendant. The
per-phase table named it outright: `ScriptItemGrantPump` was consuming **626 ms of every second**
and issuing **181.6 MiB/s** of `ReadProcessMemory` against the live game.

### Root cause: `ListItems()` never honoured the rate limit every other item path did

`ScriptItemGrantPump` samples the local PC's inventory once a second via
`IItemBridge.ReadPossessedItems`, which was implemented as "sweep the whole world, then filter to
one creature": `ReadPossessedItems` → `ListItems()` → `RebuildItemTagCache()` →
`PatternScanner.ScanAll(ItemVtable, PrivateCommitted, 4096)`. That sweep reads roughly 180 MiB —
every private committed region of the game — looking for a 4-byte vtable constant, and costs
300-660 ms.

`EnsureItemKnown` has guarded exactly this since it was written, and says why in its own doc: *"a
sweep of the game's committed memory per miss per tick once dropped the pump to about 4 Hz and
measured as peers lagging by seconds."* `ListItems()` simply did not go through that guard — it
called `RebuildItemTagCache()` unconditionally. It was written for the interactive `items`
console verb, where a fresh sweep is the right answer and a human is waiting; nothing noticed
that a pump-thread caller had been routed through it.

So once a second, between two position writes, the agent stopped for half a second. That is
precisely "the other player freezes, then catches up in a burst" — and 180 MiB/s of
`ReadProcessMemory` aimed at the game process is also the most plausible reason its own
animations felt slow, since that traffic is kernel work charged against the target.

### Three fixes, in the order they were measured

**(1) `ListItems(bool force)`.** The interactive verb forces; `ReadPossessedItems` does not, and
gets the same at-most-`ItemTagRebuildMs` (15 s) view every other pump-thread item lookup has
always had.

| | before | after |
| --- | --- | --- |
| pump rate | 12.8 ticks/s | **30.0-31.5 ticks/s** |
| tick cost, mean | 49.6 ms | **0.65-0.91 ms** |
| tick cost, worst | 636 ms | **1.4-3.5 ms** |
| pump-thread read traffic | 186,000 KiB/s | **70 KiB/s** |

The residual was a single ~660 ms tick every 15 s when the limit expired — and it exposed a
second sweeper the first one had been masking: `party 415.9ms/s 242.9MiB/s` on that same tick.

**(2) `ItemScanWorker` — sweeps run on a background thread.**
(`src/KotorCoop.Memory/ItemScanWorker.cs`.) A sweep is pure reads: `ReadProcessMemory` on a
handle is safe from several threads and `PatternScanner` holds no mutable state. The worker
returns a plain list of addresses; the caller, on its own thread, does every follow-up read and
every dictionary write that turns those into a cache. The expensive half is the half that is safe
to move; the half that mutates bridge state never left the pump thread. One sweep at a time
across all vtables — two concurrent 180 MiB sweeps would cost the game more than the inline
version being replaced.

Three caches now serve stale while refreshing: the item tag/address cache
(`ServiceItemTagCache`), the world-object address cache (`CachedScan` — doors, placeables,
encounters, 30 s TTL), and the creature address cache (`EnumerateCreatures`, 15 s TTL). Each
still blocks on its FIRST build, deliberately, because every consumer reads an empty result as
"there are none of these in the world" — and for the creature cache the empty case additionally
keeps its blocking `EmptyCreatureCacheMs` path, because "no world yet" flips exactly once at load
and the seamless-arrival hold is watching for it. `ScanCreatures` was split into the sweep and a
new `ReadCreatures(List<nint>)` to make that division possible.

**(3) `coop.ini dejitter` — shipped OFF.** A separate, real defect found while reading the
receive path: every sender runs two free-running timers at the same nominal rate (`PumpOnce`
publishes, `Tick` sends) and nothing synchronises them, so the stream alternates between
repeating a sample and skipping one; the receiver stamped each arrival with its own wall clock,
so a duplicate read as "stood still for a tick" and the next sample as "covered two ticks in
one", which `SnapshotBuffer` faithfully interpolated into a pause and a double-speed dash.
`SnapshotTimingRule` (`src/KotorCoop.Protocol/SnapshotTimingRule.cs`) quantises inter-arrival
gaps to the sender's send period and declines to sample a byte-identical repeat, with a resync
escape past `ResyncGapMs` (400 ms) and a drift bound of `MaxDriftMs` (120 ms).

It is **unit-proven and not live-confirmed**, and is off by default for that reason.
`SnapshotDejitterTests` carries both the fix and a CONTROL test asserting that the
arrival-stamped path still bursts on the same stream, so the fix test cannot quietly stop
measuring anything. The live A/B could not settle it: the only mover an unattended session has is
a `tp` hop, which is genuinely discontinuous motion — the one shape this rule is not about. See
"what could not be measured" below.

### Before and after, 120 s walk per leg, both instances moving

Both PCs driven along perpendicular legs by evenly-spaced `tp` hops (768 hops per instance per
leg, `scratchpad/perfwalk.ps1`), `perf_trace=1`, `dejitter=0` in both legs so this table isolates
the scan work alone.

| | baseline (before) HOST | final HOST | baseline CLIENT | final CLIENT |
| --- | --- | --- | --- | --- |
| pump rate, mean | 12.8 ticks/s | **30.29** | 16.6 ticks/s | **30.29** |
| pump rate, worst second | 3.3 ticks/s | **30.0** | 12.6 ticks/s | **29.5** |
| tick cost, mean | 49.6 ms | **0.67 ms** | 30.1 ms | **0.89 ms** |
| tick cost, worst peak | 2,409 ms | **8.4 ms** | 605.7 ms | **29.2 ms** |
| position path, mean | 0.24 ms | 0.25 ms | 0.24 ms | 0.24 ms |
| pump-thread read traffic | 186,000 KiB/s | **70 KiB/s** | 162,000 KiB/s | **~34 KiB/s** |

The headline is the worst tick: **2,409 ms → 8.4 ms**, and a pump that never drops below 29.5 Hz
where it previously spent whole seconds at 3.3 Hz. There is no longer any point in the tick at
which the avatar stream stops.

`rpm ... KiB/s` in the per-second lines still reads tens of MiB/s on some seconds after the fix.
That is correct and is the background thread: `MemoryTelemetry` counts process-wide, deliberately,
so the traffic is still reported honestly — it simply no longer lands between two position writes.

### What could NOT be measured this pass

- **The `dejitter` A/B.** With the `tp`-hop mover the host's mean applied gap improved
  (102.8 → 83.2 ms) and both instances' mean applied step got smaller (host 0.60 → 0.39 u, client
  0.54 → 0.34 u), but the client's mean gap regressed (111.1 → 192.2 ms). On a hop-driven stream a
  longer hold between hops is arguably the CORRECT behaviour — the peer genuinely is standing
  still between teleports, and declining to sample the repeats is the rule working as designed —
  but that cannot be distinguished from a fault without continuous motion. **NEEDS-LIVE: a round
  with a human actually walking**, which is still blocked on `docs/live-verification.md`'s
  standing keystroke-injection gap.
- **The burst-ratio metric is dominated by the mover, not the network.** Worst applied step held
  at ~4.5 u across every leg, which is the mover's own leg reversal (8 hops × 0.5 u), not a
  network burst. Recorded so nobody reads the ~8x ratio in these tables as a remaining defect.
- **The container animation was not reproduced.** `useplaceable` on the demo save refused —
  *"the sweep found 64 placeable(s) but none close enough"* — so the reported slow/stuttery
  container open was never watched directly. The mirror path itself was read and is already
  correctly latched per edge (`InteractionLockController.ReconcileContainerAnimations` diffs
  through the pure `ContainerAnimationEdges`, and `CSWSObject::SetAnimation 0x004CCF60` has its
  own dirty-on-change latch), so a per-tick re-trigger is ruled out by construction. The
  remaining hypothesis, untested, is that it was the same starvation: 180 MiB/s of
  `ReadProcessMemory` aimed at the game is kernel work charged against it, and an animation is
  the most visible thing in a frame that arrives late.
- **No plugin-side change was made and none was needed.** `frame_stats` was on throughout; the
  plugin's per-frame cost was never the suspect and was not measured to have changed.
- **`ItemTagRebuildMs`, `ObjectScanCacheMs`, `CreatureCacheMs` and `ScriptItemGrantSampleIntervalMs`
  were all left exactly as they were.** The fix is about which THREAD the work happens on, not
  how often; changing a TTL as well would have made the measurement unattributable.

### A consequence worth stating plainly

`ScriptItemGrantPump`'s detection latency is now bounded by `ItemTagRebuildMs` (15 s) rather than
by its own 1 s sample interval, because a script-created item body cannot appear in a listing
until a sweep has found its address. It was never really 1 s — the sweep it was forcing every
second is exactly what made the session unplayable — so this is the honest version of a number
that was previously being paid for out of everyone's frame rate.

### Verification tags (this pass)

- **LIVE-VERIFIED 2026-08-29**: the before/after table above; two 120 s walking legs plus a
  20 s and a 45 s idle leg, both instances, on the demo save (END_M01AA).
- **LIVE-VERIFIED 2026-08-29**: `tools/verify.ps1` after the change — **43 passed, 6 failed**,
  against this document's own stated baseline of ~40-42 pass / 7-9 fail. No check regressed.
- **VERIFIED (unit test, this pass)**: `SnapshotTimingRuleTests` (11 tests) and
  `SnapshotDejitterTests` (6 tests, one of them the control).
- **VERIFIED (source, this pass)**: `ListItems()`'s unconditional `RebuildItemTagCache()` and
  `EnsureItemKnown`'s pre-existing rate limit on the same cache.
- **NEEDS-LIVE, still open**: `dejitter=1` with a human walker; the container-animation report.

### Test suite and build

`pwsh src/KotorCoop.Plugin/build.ps1 -Install` (both game dirs, `dinput8.dll` byte-identical to
the publish output in each), `dotnet build` clean with 0 warnings.

`dotnet test tests/KotorCoop.Tests` (full suite): **2,756/2,757 passed**. The single failure,
`SharedInventoryV2LoopbackTests.ClientBuyBackAppliesToTheHostExactlyOnceAndStaysThatWay`, passes
on its own (16/16 in that class) — a fixed-port loopback flake of the kind this document's
sibling notes already list, untouched by anything in this pass.

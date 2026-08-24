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

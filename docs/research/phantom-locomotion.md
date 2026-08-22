# Phantom locomotion audit: "moves in sync but slides, no walk/run animation"

Audit 2026-08-22, in response to a player report that a peer's phantom (and possibly a
mirrored enemy or companion) tracks position correctly but never shows a walk/run cycle —
the exact defect `docs/research/realtime-creature-movement.md` §8/§9 already documents and
fixed once, live-verified (README: "run-gait ... Verified live on two GOG instances").

## Provenance / label meaning

- **VERIFIED** = read directly out of the current source tree, or reproduced with a unit
  test in this pass.
- **INFERRED** = reasoning about what the (unchanged) VERIFIED code does.
- **NEEDS-LIVE** = cannot be closed without the running game; see the live-test script below.

No game was run for this audit (a rig agent was using it). Everything below is static
analysis plus new unit tests (`tests/KotorCoop.Tests/GaitRuleTests.cs`).

---

## 0. Executive summary

- **Git archaeology found no code regression in the phantom gait pipeline.** Every write
  `Odyssey.DriveBody` makes (facing, `CreRunFlag`/`CreMoveState`, the server `SetAnimation`
  vtable call, the twin's desired-position + `ClientServerSyncHandler_Animation` sync) is
  **byte-for-byte the same logic** as it was at `18414ad` ("Gait: hold the run cycle instead
  of flickering, and stop starving the pump" — the commit that live-verified this exact
  scenario). `git diff 72a0432 HEAD -- src/KotorCoop.Plugin/Odyssey.cs` shows the only
  changes touching `DrivePhantom`/`DriveEnemy`/`DriveBody` this cycle are the additive
  `bisectMask` parameter EnemyPresenceController's combat-round bisection added, which
  defaults to `0` and is a no-op for every call `PhantomController` makes (it never passes a
  mask). (VERIFIED.)
- **The two specific regression hypotheses in the brief do not hold, for this codebase:**
  - *"`ClaimCreature` zeros `CreRunFlag` every frame."* True for **enemies**
    (`EnemyPresenceController.DriveOne` re-claims every pass, round 46's own comment says so
    explicitly), but that reset is immediately overwritten by the `Drive` call which follows
    it on the same frame with the correct value — and it does not apply to phantoms at all:
    `PhantomController.TrySpawnOne` calls `Odyssey.ClaimCreature` exactly **once, at spawn**
    (`src/KotorCoop.Plugin/PhantomController.cs:1213`), not per frame. (VERIFIED.)
  - *"A `PlayerState` flag bit collides with a running/moving flag."* There is no
    running/moving flag to collide with. `src/KotorCoop.Protocol/PlayerState.cs` defines six
    flags (`FlagInDialogue=1, FlagPaused=2, FlagInCombat=4, FlagDowned=8,
    FlagPauseReleased=16, FlagInMinigame=32`) — six distinct bits, 0 through 5, no overlap —
    and none of them is "running". Direct-drive gait is computed **from measured position
    deltas**, not from any wire flag; see §2. (VERIFIED.)
- **The gait math itself is sound.** A new test,
  `GaitRuleTests.ARunningPeerNeverReadsIdleOnceMotionIsEstablished`, drives
  `MotionSmoother` (the tested sibling of `PhantomController`'s own inlined copy) with a
  peer running continuously at 3.96 u/s, publishing every 33 ms while sampled every 16.7 ms —
  the exact real cadence mismatch the code's own comments call out as the historical cause of
  this bug — and asserts the derived gait never reads Idle once motion is established. It
  passes. (VERIFIED by test, this pass.)
- **What did change this week:** two new pieces sit *around* the gait pipeline but do not
  touch it — `AgentLivenessController.RemoteWritesFrozen` (an early-return in
  `PhantomController.OnFrame` when the agent's heartbeat has stalled — it freezes ALL
  writes, including position, so it cannot produce "moves in sync but no animation") and the
  `bisectMask` plumbing (enemy-only, default off). Neither is the reported symptom.
  (VERIFIED.)
- **What this pass changed:** the gait *decision* — a threshold that used to be an untested
  inline `speed > 2.0f` duplicated in two places (`Odyssey.DrivePhantom` and
  `Odyssey.DriveEnemy`) plus an untested equality check inside `Odyssey.GaitChanged` — is now
  a single pure, unit-tested type, `GaitRule` (§3). This is a preventative fix: it does not
  change behaviour (11 new tests confirm the extracted logic matches the old inline
  arithmetic exactly), but it closes the exact gap that let a threshold regression go
  unnoticed until a live playtest — there was previously **zero** test coverage on this
  threshold.
- **Most likely real-world explanation, if the report is accurate and current:** a stale
  `dinput8.dll` (see the project's own "demo.ps1 does not deploy the plugin" note — the demo
  harness does not rebuild+install the plugin, so a game session can run against a build from
  before the round-56/round-58 combat work without anyone noticing) or `coop.ini` carrying a
  leftover `movement=walk` from an earlier test session, which routes through the
  **known-imperfect fallback** described in §4 instead of the direct-drive path. Both are
  externally checkable and neither requires a code change. NEEDS-LIVE to confirm on the
  reporter's actual install.

---

## 1. The path, precisely

A remote peer's avatar reaches the phantom body in five stages:

1. **Capture (each instance, own player).** `KotorGameBridge.TryReadLocalState` reads the
   local player's position/facing/health/animation every agent tick
   (`src/KotorCoop.Memory/KotorGameBridge.cs:1411`). `ReadPlayedAnimation` reads the CLIENT
   TWIN's animation (`+0x48`, 16 bits — the server field lags client-side one-shots, see
   `docs/research/animation-sync.md`), falling back to the server field. This value only
   feeds `PhantomController.MirrorAnimation` (one-shots) and the walk-mode fallback's gait
   (§4) — **not** direct-drive gait.
2. **Wire.** `PlayerState` (Position, Facing, Health, Animation, Flags, ...) goes out over UDP
   at `ProtocolConstants.DefaultTickRate` (30 Hz, unchanged this cycle — last touched at
   `b66a838`, long before this week).
3. **Interpolate.** `SnapshotBuffer.Sample` (`src/KotorCoop.Session/SnapshotBuffer.cs`) plays
   received snapshots back on a fixed `DefaultInterpolationDelayMs` (80 ms, also unchanged
   this cycle — it was *lowered* from 100 ms a while back, not raised) and lerps Position
   between the two bracketing samples. `GetRemotePeers()` therefore returns an already
   continuously-varying position, sampled once per agent tick.
4. **Publish to the plugin.** `KotorGameBridge.WriteToPlugin`
   (`src/KotorCoop.Memory/KotorGameBridge.cs:1864`) writes each peer's position (`+0x08`),
   facing (`+0x14`), health (`+0x18`), animation (`+0x1C`), name, appearance and flags
   (`+0x3C`) into the plugin's shared-block slot, then `CommandSpawnAndDrive`.
   `SharedBlockView.GetRemote` (`src/KotorCoop.Plugin/SharedBlockView.cs:258`) reads the same
   layout back inside the plugin. Offsets match on both sides — checked directly against the
   current source. (VERIFIED.)
5. **Drive (plugin, game thread, every ~16.7 ms).** `PhantomController.OnFrame` →
   `DriveDirect` (`src/KotorCoop.Plugin/PhantomController.cs:916`) is the "seamless" default
   (`_directMovement`, true unless `coop.ini` sets `movement=walk`). **The body is placed by
   direct writes, not a queued `ActionMoveToLocation`** — the file's own top-of-class comment
   says exactly this and explains why a `WalkUnitsPerSecond` constant that used to exist was
   dead code: "movement is an ActionMoveToLocation the AI executes" was true of the *walk
   fallback* only; the default path bypasses the AI's pathfinding entirely and animates
   itself. `DriveDirect` measures the peer's speed from **consumed position deltas**
   (update-to-update, not frame-to-frame — the code's own extensive comments explain why a
   frame-to-frame measurement doubled/halved the apparent speed), smooths the shown position
   toward the target with a capped exponential chase, decides "moving" with 0.35 s hysteresis
   (not a raw threshold — a flickering decision reads as idle because the engine only pushes
   an animation to the render side ON CHANGE), and calls `Odyssey.DrivePhantom(phantom, shown,
   facing, gaitSpeed, moving)`.

`Odyssey.DrivePhantom` → `DriveBody` (`src/KotorCoop.Plugin/Odyssey.cs:9006`) then, every
step: sets facing, sets `CreRunFlag`/`CreMoveState` (`-1` while moving per
`docs/research/realtime-creature-movement.md` §9's VERIFIED finding that
`CSWCCreature::AnimationUpdate` skips its ENTIRE per-frame update — rotation, position easing,
*and* the looping animation — when the server's `+0xA8C` is 0 while the twin shows a moving
animation), calls the server `SetAnimation` vtable slot (`+0x7C`) only on change, writes the
server position with the dirty bit suppressed, and separately writes the CLIENT TWIN's desired
position (budget 0 = snap, not the engine's own 512 ms ease) and — edge-triggered via
`GaitChanged` — the twin's animation through its own `ClientServerSyncHandler_Animation` slot
(vtable `+0x134`), because the twin's plain animation setter does not "stick" the way the
sync handler does (documented failure mode: reasserting through the wrong entry reverted
within a frame). The playback RATE (`twin+0x1F8`) is written every step from measured speed,
because the engine's own `ComputeSpeedFactor` returns `0.0` for a creature with no waypoint
list — which a directly-placed phantom always is — and a rate of 0 is legs frozen mid-pose
while the body keeps moving (the historical "flying" bug this file's comments describe in
detail).

**None of stage 5's writes changed this cycle** beyond the additive, phantom-inert
`bisectMask` parameter (§0).

---

## 2. Why the flag-collision and "ClaimCreature resets it" hypotheses don't apply here

The brief's hypothesis 3 assumed a design where a wire-carried "is running" bit feeds gait
selection, and asked whether `FlagInMinigame` (bit 5, value 32) collided with it. **This
codebase does not carry a running flag on the wire at all** — `docs/research/
realtime-creature-movement.md` §9 explicitly documents this as a *design decision*, not an
oversight: "Choose the gait from the stream, not from the peer's mirrored animation field ...
The measured planar speed of the incoming positions is a direct, always-correct signal."
`PlayerState.Animation` (the field that WOULD carry a mirrored gait id) is used only by
`MirrorAnimation` (which explicitly excludes idle/walk/run/death ids — see
`IsPlayableAnimation`, `src/KotorCoop.Plugin/PhantomController.cs:82`) and by the walk-mode
fallback (§4), never by `DriveDirect`. So there is no flag to collide, and `FlagInMinigame`
(bit 5) sits cleanly after `FlagPauseReleased` (bit 4) with no reused bit. (VERIFIED by
reading `src/KotorCoop.Protocol/PlayerState.cs`.)

Hypothesis 3's other half — `ClaimCreature` zeroing `CreRunFlag` every frame — is real for
**enemies** (`EnemyPresenceController.DriveOne` re-claims every pass by design, per round 46's
own comment) but is harmless there because the immediately-following `Drive` call overwrites
`CreRunFlag` with the correct value on the same frame, before the frame is presented. It does
not apply to **phantoms**: `Odyssey.ClaimCreature` is called exactly once, at spawn
(`PhantomController.cs:1213`), never per frame. (VERIFIED by reading both call sites.)

---

## 3. The fix this pass makes: `GaitRule`

No functional bug was found in the live drive path (§0). What WAS found is a coverage gap:
the threshold that decides walk-vs-run was inlined identically in two places
(`Odyssey.DrivePhantom`, `Odyssey.DriveEnemy`) with **zero unit tests**, and the edge-trigger
check inside `Odyssey.GaitChanged` was likewise untested. A future change to either copy
(exactly the kind of change a `bisectMask`-style diagnostic pass tends to make, per this
week's `EnemyPresenceController` history) could silently diverge the two, or flip the
threshold's boundary, with nothing to catch it before a live playtest.

`src/KotorCoop.Plugin/GaitRule.cs` (new, `internal`, no engine/unsafe dependency — same
`*Rule` pattern as `MotionSmoother`/`MinigameRule`/`LivenessRule`, linked into the test
project as a dependency-free file the same way):

```csharp
internal static class GaitRule
{
    public const int AnimIdle = 0x2710;
    public const int AnimWalk = 0x2712;
    public const int AnimRun = 0x2714;
    public const float RunSpeedThreshold = 2.0f;

    public static bool IsRun(float speed) => speed > RunSpeedThreshold;

    public static int SelectAnimation(float speed, bool moving) =>
        !moving ? AnimIdle : IsRun(speed) ? AnimRun : AnimWalk;

    public static bool ShouldReissue(int lastAsserted, int want) => lastAsserted != want;
}
```

Wired in as a pure refactor (no behaviour change — confirmed by the full suite staying at
1555 pre-existing + 11 new = 1566 passing):

- `Odyssey.DrivePhantom` (`src/KotorCoop.Plugin/Odyssey.cs:8973`) now calls
  `GaitRule.SelectAnimation`/`GaitRule.IsRun` instead of the inline `speed > 2.0f`.
- `Odyssey.DriveEnemy` (`src/KotorCoop.Plugin/Odyssey.cs:8992`) likewise, keeping its own
  extra rule (idle-but-hold-the-owner's-looping-animation) unchanged.
- `Odyssey.GaitChanged`'s equality check now calls `GaitRule.ShouldReissue` instead of `==`.

`tests/KotorCoop.Tests/GaitRuleTests.cs` (new, 11 tests): boundary tests for the run
threshold (exactly-at-threshold is a walk, not a run — documents which side wins),
not-moving-is-always-idle regardless of stale speed, NaN/negative/positive-infinity handling
(NaN and negative walk rather than crash or silently run; `+Infinity` is well-defined as a
run, documented rather than special-cased), reissue-only-on-change, and two end-to-end tests
that drive `MotionSmoother` + `GaitRule` together at the real 30 Hz-publish/60 Hz-step
cadence: a continuously running peer never reads Idle once motion is established, and a peer
who stops settles back to Idle rather than staying parked in Run. All pass.

---

## 4. A known, pre-existing, separate imperfection (not this report, but worth flagging)

The **walk-mode fallback** (`coop.ini movement=walk`, not the default) still derives run/walk
from `remote.Animation != AnimWalkValue`
(`src/KotorCoop.Plugin/PhantomController.cs:594`) — the mirrored animation field, which
`docs/research/realtime-creature-movement.md` §9 already flags as unreliable ("the peer's
`+0xD4` never sees client-side one-shots"). This is pre-existing, documented, and only
reachable if an install's `coop.ini` explicitly opts out of the seamless default — but it is
the one gait-selection path in the codebase that genuinely still has the class of bug the
report describes, and worth checking on the reporter's install (`grep movement coop.ini`).

---

## 5. Live-test script

`tools/phantomgait.ps1` (new). Polls a driven body's ACTUAL engine animation state every
250 ms: the server creature's animation id (`+0xD4`), move-state (`+0xA8C`), run flag
(`+0xA98`), and — critically, since the twin is what renders — the CLIENT TWIN's animation
id (`+0x48`) and playback-rate scale (`+0x1F8`), plus a derived speed from consecutive
position samples. It auto-resolves every live phantom from the plugin's shared block
(`SharedBlock.OffsetPhantomIdTable`), or takes an explicit `-ObjectId` for a mirrored enemy or
companion.

```powershell
pwsh tools\phantomgait.ps1                      # auto: every live phantom, 20s
pwsh tools\phantomgait.ps1 -Seconds 40 -Csv gait.csv
pwsh tools\phantomgait.ps1 -ObjectId 0x1A2B      # a specific enemy/companion id
```

Procedure (NEEDS-LIVE — not run this pass, rig was in use):

1. Two instances connected, same module, standing apart.
2. Host walks in a straight line ~3 s, then runs ~5 s, then stops.
3. On the CLIENT during that whole window: run the script with no `-ObjectId` (slot 0 is the
   host's body as the client sees it).
4. Swap roles (client walks/runs, host watches) and repeat.
5. Repeat once more against a mirrored enemy's id and a mirrored companion's id
   (`tools/creatures.ps1` or the in-game target console command finds the id), per the
   brief's request to rule out the same defect on `EnemyPresenceController`/
   `PartyPresenceController`'s shared `DriveBody` path.

Expected healthy output: `anim`/`twin` transition `0x2710`(idle) → `0x2712`(walk) →
`0x2714`(run) and back within one or two samples of the peer actually starting/stopping;
`rate` nonzero whenever `moving=1`; `moveState=-1` whenever `moving=1` (`1` when idle). The
script's own tail prints a verdict line and flags any sample where the body was moving
(>0.3 u/s) while the twin's animation read idle — the exact "moves in sync but slides"
signature — with the offending rows.

---

## 6. What was NOT changed

Per the task's boundaries, `CheatController.cs`, `DoorUse.cs`, the trigger/dialogue files and
`TestDriveController.cs` were not touched. No behavior of the live drive path changed — only
a duplicated, previously-untested threshold was named and tested.

---

## 7. Live pass (2026-08-22) — round 60: the bug is real, live-reproduced, and root-caused
to a specific engine function; a fix attempt was tried and falsified

The static audit above (§0–§6) found no code regression and concluded the report needed a
live rig. This pass ran the rig. **The report is correct and the mechanism is now precisely
located, but a working fix was not found this session** — see §7.5 for what remains.

### 7.1 Tooling trap found first: this module has frozen duplicate objects

Before any gait data could be trusted, two test tools turned out to be unreliable on this
save (`END_M01AA`, loaded via `demo.ps1`'s default autosave): **`teleport.ps1` and
`phantomgait.ps1`'s auto-resolve both find their target with a raw vtable-address scan**, and
this module's memory has **frozen duplicate copies of nearly every creature** — including the
local player and the phantom itself — sharing vtable, object id, tag and stats pointer with
the live object, exactly the failure mode `KotorGameBridge.TryReadLocalState`'s own comment
already documents and defends against ("the scan can latch a FREED COPY of the player... every
cheap validation passes while its position and health stay frozen"). Measured: a scan-based
mover wrote a real, persistent position change to one address; `creatures.ps1` confirmed the
write stuck; but the peer's phantom never moved, because the write landed on the *wrong* one
of two nearly-identical `is_pc=1` objects. The correct address only comes from a source the
scan cannot be fooled by:

- **Local player**: the plugin's own `SharedBlock.OffsetEnginePlayerPointer` (0x2260, written
  from `Odyssey.GetPlayerCreaturePointer()` — the real `CServerExoApp::GetPlayerCreature()`
  call, not a scan). A throwaway mover script (`movepc2.ps1` in this pass's scratch dir) reads
  this instead of scanning, and writes `+0x90` directly at a controlled speed/cadence to
  simulate sustained walk (1.6 u/s) and run (3.96 u/s) without working keyboard injection
  (confirmed still true this pass: `docs/live-verification.md` line 220, "injected keystrokes
  never reach this game" — `sendkey.ps1`/`blockpoke walk` were not usable for this).
- **Phantom**: the plugin's own `"spawned phantom in slot 0: id 0x... at 0x..."` log line is
  ground truth (the plugin created it and knows its real address); the auto-resolve scan in
  `phantomgait.ps1` can and did latch a duplicate for the SAME id. A `gaitsample.ps1` variant
  taking an explicit `-Address` (skipping the scan) was used instead once the real address was
  read off the log.

This is worth fixing in the tools themselves (a future pass), but was not blocking for this
report once worked around.

### 7.2 The measurement, with the correct addresses

Two live samples, 40 ms and 100 ms cadence, sustained real walk-then-run, host→client and
client→host, both directions:

```
time         pos                        anim   twin      rate  moveState    run  speed moving
13:46:21.798 (   64.62,  22.15) 0x2714 0x2710      0.60         -1      1   0.00      1
13:46:22.907 (   66.28,  22.15) 0x2714 0x2710      1.04         -1      1   9.78      1
13:46:23.389 (   67.85,  22.15) 0x2714 0x2710      1.50         -1      1   7.37      1
13:46:25.657 (   75.80,  22.15) 0x2714 0x2710      1.17         -1      1  10.52      1
13:46:28.593 (   86.33,  22.15) 0x2714 0x2710      1.50         -1      1   7.04      1
13:46:29.762 (   90.76,  22.15) 0x2714 0x2710      0.99         -1      1   2.95      1
SLIDE DETECTED: 132 sample(s) moving at >0.3 u/s while twin anim read idle (0x2710).
samples with speed>0.1: 132 / 133
```

(Full 133-row transcript, and the second 173-row/100ms run, in this pass's scratch logs —
excerpted here for length; every field/column matches `tools/phantomgait.ps1`'s own layout.)

Reverse direction (client walks/runs, host watches its phantom of the client):

```
SLIDE DETECTED: 87 sample(s) moving at >0.3 u/s while twin anim read idle (0x2710).
samples with speed>0.1: 90 / 108
```

In both directions, across three independent runs (one at 250 ms, two at 40–100 ms), the
picture is identical and unambiguous:

- **`pos`** advances exactly as commanded (1.6 u/s walking, 3.96 u/s running, confirmed by the
  mover's own before/after read).
- **`anim`** (server `CSWSObject+0xD4`, written by `Odyssey.DriveBody`'s `SetAnimation` vtable
  call) correctly cycles `0x2710`→`0x2712`→`0x2714` and back, tracking `GaitRule`'s decisions.
- **`moveState`** (`CSWSCreature+0xA8C`) is `-1` while moving, `1` at rest — correct.
- **`rate`** (twin `+0x1F8`, the playback-speed scale) is nonzero and varies with speed —
  correct; §9's documented `ComputeSpeedFactor` gap is being compensated for as designed.
- **`twin`** (`CSWCObject+0x48`, the field `CSWCCreature::AnimationUpdate` actually renders
  from) **stays at `0x2710` (idle) for the entire sustained run — 132/133 and 87/90 samples**,
  briefly catching up to the correct value only a handful of times across hundreds of samples,
  never for a full "run" cycle in the tight 40 ms trace.

This is a clean, reproducible, symmetric confirmation of the report: **the body is placed
correctly (position, moveState, rate all track), but the client twin's rendered animation
field almost never updates — the exact "moves in sync but slides, no walk/run animation"
signature**, on both peer directions.

### 7.3 Root cause, precisely located

`Odyssey.DriveBody` calls the documented, address-verified sync entry —
`CSWCObject` vtable `+0x134` (`ClientServerSyncHandler_Animation`, `0x0060C100`) — live-read
and confirmed **exactly matching the docs** on the running phantom (`vtable=0x74F5F8`,
`+0x134=0x60C100`, `+0x12C=0x611F50`), with a **sane** budget argument (`+0xD8` read as
`1.0`, not garbage). So the call target, the vtable slot, and the arguments are all correct —
this is not the address/slot mistake §3 originally worried about.

Live disassembly of `0x0060C100` in the running process shows it stores the budget arg at
`twin+0x4C`, special-cases roughly eight specific one-shot ids (none of which are
`0x2710`/`0x2712`/`0x2714`), and for everything else calls a helper (`~0x0063C0xx`, shaped
like the documented `GetLoopingAnimation`), then only reaches the actual "play it" step behind
a conditional gate on that helper's result. **The engine's own animation classifiers — the
same `AnimationLooping`/`AnimationFireAndForget`/`AnimationOverlay` predicates this codebase
already uses successfully for one-shots (`Odyssey.PlayOneShotAnimation`) — were called live
against this exact phantom's twin for `0x2712`, and all three report `0`:**

```
[14:01:25.396] gait-class: id 0x2712 on twin 0x1ABFF190 -> looping=0 fireForget=0 overlay=0
[14:01:25.396] gait-class: id 0x2712 on twin 0x1ABD4410 -> looping=0 fireForget=0 overlay=0
```

The client-side animbase **does not classify `0x2710`/`0x2712`/`0x2714` as playable at all** —
not looping, not fire-and-forget, not overlay. This is consistent with those ids being
SERVER-side abstract state markers (`AnimationWalking 0x004CC1C0`, `AnimationStationary
0x004CAE60` — functions that set `CSWSObject+0xD4` as a state flag) rather than per-model
`animations.2da` rows the client's classifiers look up. A normally AI/waypoint-driven creature
presumably reaches its actual walk-cycle row through some other translation this direct-drive
phantom never triggers (no waypoints, no queued action — the same category of gap §9 already
flags for `ComputeSpeedFactor`). That translation was not found this pass.

### 7.4 Fix attempted and falsified

The disassembly showed `ClientServerSyncHandler_Animation`'s "not special-cased" branch falls
through to a further call at twin vtable `+0xA0` (thiscall, `(twin, want)`, confirmed live as
a distinct, valid function pointer `0x006183D0`, different from `+0x9C`'s plain setter
`0x0063E930`) when its internal gate passes. **A direct call to `+0xA0`, bypassing the whole
gated function, was added, built, deployed, and live-retested — and did not fix it**: the
twin's animation still never left idle (132/173 samples). This rules out "the call chain just
needs one more hop" as the explanation and points the remaining work at whatever actually
computes a valid per-model animation row for a directly-placed creature. The unsuccessful
`+0xA0` call was reverted; a small, safe, capped (6-sample) diagnostic log using the
already-proven classifier calls was kept in `Odyssey.DriveBody` instead
(`src/KotorCoop.Plugin/Odyssey.cs`, the `gait-class:` log line) so a future pass has the
classification answer without re-deriving it.

### 7.5 What remains (NEEDS-LIVE, not closed this pass)

- **The actual fix.** The mechanism that resolves a "walking" *state* into a per-model,
  classifier-recognized animation row for a normally-driven creature was not identified. Likely
  candidates for a future pass: giving the twin a real (even synthetic, one-point) waypoint
  list via the documented `CSWCCreature::SetWayPoints 0x0060C790` so the engine's own
  movement/animation machinery treats the phantom as genuinely walking (this was NOT tried —
  it conflicts with this codebase's deliberate, previously-researched choice of direct
  placement over waypoint-driven movement, §5, and needs its own live verification that it
  does not reintroduce position/speed fighting); or single-stepping
  `ClientServerSyncHandler_Animation`'s helper call with a debugger to read the real
  translation table it consults.
- **Mirrored enemy and companion.** `EnemyPresenceController.DriveOne` and
  `PartyPresenceController`'s companion drive both call the exact same `Odyssey.DriveBody` —
  the log during this pass confirms both were live and active (`"enemy drive: mirroring 25 of
  the owner's 25 enemies"`, `"party drive: mirroring 1 of the host's companions"`) — so the
  same root cause applies by construction. Time did not allow forcing sustained real AI
  movement on a mirrored enemy or companion to sample it directly this pass; this is the same
  defect, not a separately-confirmed one.
- **Tooling**: `teleport.ps1` and `phantomgait.ps1`'s auto-resolve should stop trusting a raw
  vtable scan on a save where duplicate/frozen objects exist (see §7.1) — prefer
  `SharedBlock.OffsetEnginePlayerPointer` for the player and the plugin's own spawn-log address
  for a phantom, or teach the scan to disambiguate (e.g. prefer the `+0x1F8`
  visible-to-player bit, or cross-check against a second live-only field).

### 7.6 What did NOT regress

`tests/KotorCoop.Tests` stayed at 1566/1566 passing throughout (before, during, and after the
falsified fix attempt and the kept diagnostic). Both plugin builds are clean. No behavior
change shipped this pass beyond the capped, read-only diagnostic log — `DriveBody`'s actual
drive logic (position, moveState, runFlag, rate, the `+0x134` sync call itself) is byte-for-
byte what §0–§6 already found unchanged from the last live-verified commit.

---

## 8. Round 61 — the real mechanism found by static disassembly, a live-confirmed partial fix,
and honest evidence it is not fully reliable

Method used, per this round's brief: differential, not guessing — static disassembly of the
exact engine call chain from the sync call down to the render field, cross-checked against
`animations.2da` and live probes, rather than another round of "try a call and see."

### 8.1 Round 60's classifier attribution was wrong — corrected here

Round 60 concluded `CSWCObject::AnimationLooping` (vtable-independent, `0x0063C300`) gates
`ClientServerSyncHandler_Animation` (twin vtable `+0x134`, `0x0060C100`) and that the phantom's
animbase "does not classify these ids as playable at all." **Disassembling `0x0060C100` itself
shows this is not what it does.** VERIFIED (static disassembly, this round):

```
0x0060C100  mov eax,[esp+8]         ; budget (float, arg3)
0x0060C10F  mov edi,ecx             ; edi = twin
0x0060C112  mov [edi+0x4c],eax      ; store budget, unconditionally
0x0060C115  je 0x60c184             ; id in {0x2719,0x277d,0x271c,0x271b,0x271e,
                                    ;   0x2716,0x2718,0x27ac} -> early return (no-op)
0x0060C14F  call 0x63c060           ; GetCurrentAnimation(twin+0x58)
0x0060C157  cmp esi,ecx; je 0x60c184  ; already current -> early return
0x0060C15B  call 0x63c380           ; AnimationFireAndForget(twin, id)
0x0060C163  test eax,eax
0x0060C169  je 0x60c179
0x0060C16E    call [twin_vtbl+0xa0]   ; fire-and-forget path
0x0060C179  else call [twin_vtbl+0x9c] ; CSWCObject::SetAnimation — UNCONDITIONALLY
```

`AnimationLooping` is never called here at all. The function only ever branches on
`AnimationFireAndForget`, then falls through to the plain setter (vtable `+0x9C`,
`CSWCObject::SetAnimation`, `0x0063E930`) for everything that isn't fire-and-forget or one of
eight hardcoded combat-overlay ids — which is exactly where our gait ids (0x2710/0x2712/0x2714)
land.

`AnimationLooping` **is** called, but one level deeper, inside `CSWCObject::SetAnimation`
itself (`0x0063E930`, "the plain setter", already known from earlier rounds to "not stick"):

```
0x0063EC5D  push ebp                ; id
0x0063EC67  call 0x63c300           ; AnimationLooping(current-anim-family, id)
0x0063EC6E  je 0x63f224             ; false -> bail, twin+0x48/+0x58 untouched
0x0063EC74  ... (compare against current, then the actual twin+0x58/+0x48 write)
```

So `AnimationLooping` genuinely does gate the write — just not the function round 60 named.
This distinction matters because of §8.2.

### 8.2 Why `AnimationLooping` rejects gait ids for EVERY creature, not just phantoms

`AnimationLooping` (`0x0063C300`) has a fast path for `object_type==5` (creature) with id in
`[0x578,0x5F7]` or `[0x640,0x6BF]` (combat/action loops — not our range) that returns true
immediately. For anything else it translates the id through
`CSWCAnimBase::ServerToClientAnimation` (animbase vtable `+0xE0`, `0x0069F650`, keyed on
`animBase+0xC5` — the weapon-model-class byte) into a row number, then looks up that row's
`Looping` column in `animations.2da` via `0x00413510` and returns `row.Looping != 0`.

`ServerToClientAnimation`'s own jump table (dumped from the binary, VERIFIED) resolves
`0x2712`→idx 2 and `0x2714`→idx 4. For weapon class 2 (confirmed live, both the local player
and the phantom — see §8.3), those handlers return row **338** and row **345** respectively
when `animBase+0xDC` ("run looks awful", `CSWCCreature::SetRunLooksAwful`) is nonzero, which it
always was in every live sample this round took.

`tools/kotorfiles.py --game <install> 2da animations.2da` (VERIFIED, read directly from the
retail file):

| row | name | walking | running | looping |
| --- | --- | --- | --- | --- |
| 338 | walkSS | 1 | 0 | **0** |
| 345 | runSS | 0 | 1 | **0** |

**Walking and running are their own `animations.2da` category, never `Looping`.** This means
`AnimationLooping` structurally, permanently rejects 0x2712/0x2714 for *every* creature that
reaches it through this call chain — the phantom is not special-cased against; nothing walking
through `SetAnimation`'s plain-setter path can ever get a gait id past this gate. Round 60's
"the animbase does not classify these ids as playable" reading was directionally right about
the *symptom* (this call chain never plays gait ids) but wrong about *why* (it is not a
per-creature animbase defect; it is `AnimationLooping` checking the wrong column for this
category of id, by design, for a call chain gait was never meant to travel).

### 8.3 The differential dump (per the brief's requested method) — and why it came up empty

A live diff of the two structures the brief asked for — `CSWCAnimBase` (240 bytes,
`tools/kotor1_0_3.db`) for the local player's own twin (natively animated, though NOT actually
walking at sample time — see the caveat below) vs. a driven phantom's twin, both read via
`SharedBlock.OffsetEnginePlayerPointer`/the plugin's own spawn-log address (not a vtable scan,
per round 60's §7.1 trap):

| offset | field | player | phantom | differs? |
| --- | --- | --- | --- | --- |
| 0xB8 | `gob` (Gob\*) | 0x1BED7B88 | 0x1BED7D60 | yes, but expected (different object) |
| 0xC4 | (packed bytes) | 0x03 | 0x03 | no |
| 0xC5 | weapon model class | **0x02** | **0x02** | **no** |
| 0xC6 | (gate byte `ServerToClientAnimation` checks) | 0x01 | 0x01 | no |
| 0xC8 | unidentified float-ish | 8.28 | 137.97 | yes, but not load-bearing (§8.4 traces the real gate; this field is never read on the path that matters) |
| 0xCC | `runLooksAwful` (`SetRunLooksAwful`) | 0 | 0 | no |
| 0xDC | unidentified byte (gates `ServerToClientAnimation`'s weapon-class branches: must be non-zero) | 4 | 5 | yes, but **both non-zero** — same branch taken either way |

**VERIFIED, this round.** No field in this struct differs in a way that changes
`ServerToClientAnimation`'s or `AnimationLooping`'s output between the player and the phantom —
both resolve to the same rows, both get the same (permanent, §8.2) rejection. This is the
honest negative result: the brief's hypothesis ("the phantom's animbase/appearance state
differs from a normally-animated creature's") does not hold for this creature pair. The real
defect is not a per-creature state gap at all — it is that gait was never meant to travel
through this call chain (§8.2), on any creature.

### 8.4 The real mechanism: `ClientServerSyncHandler_Position`, not
`ClientServerSyncHandler_Animation`

`CSWCObject` vtable has a THIRD relevant classifier pair the earlier rounds never found:
`CSWCObject::AnimationWalking` (`0x0063C240`) and `CSWCObject::AnimationRunning`
(`0x0063C2A0`) — siblings of `AnimationLooping`, keyed on the `Walking`/`Running` 2DA columns
instead. `tools/xrefs.py` (VERIFIED): each has exactly **one** call site in the whole image,
both inside `CSWCCreature::ClientServerSyncHandler_Position` (twin vtable `+0x12C`,
`0x00611F50` — the POSITION sync handler, `docs/research/realtime-creature-movement.md` §10).

Disassembling `0x00611F50` (VERIFIED, this round) shows it is called with
`(twin, Vector* desiredPos, uint serverAnimId, float budget)` and, after an
`AnimationStationary` short-circuit, does exactly this:

```
call AnimationWalking(serverAnimId)     ; 0x63c240
if true:  resolve the "Walking" 2DA column's animation name, eventually
          call animbase-vtable+0x54     ; the actual "play by name" step
else:
  call AnimationRunning(serverAnimId)   ; 0x63c2a0
  if true (gated by a float-threshold check on id==0x2714 specifically): same play step
  else: falls to a RunLooksAwful-branched variant of the same play step
```

**This is the only place in the engine that ever calls `AnimationWalking`/`AnimationRunning`
and plays a walk/run animation by name.** `ClientServerSyncHandler_Animation` (`+0x134`, §8.1)
never reaches this logic — it only handles fire-and-forget/overlay ids and (via the plain
setter) `AnimationLooping`-approved ids, which walk/run ids never are (§8.2).

`ClientServerSyncHandler_Position` fires from the engine's own AI-master sync pass whenever
dirty bit 1 (position) is set. **`DriveBody` has, since it was written, called
`SetPositionFn(creature, &position, 1, 0, 1)` with `bSuppressDirty=1` — deliberately
suppressing exactly that bit**, to avoid a *different*, earlier-diagnosed bug: this same
handler's own interpolation step and its `ComputeSpeedFactor` re-derivation (which returns
`0.0` for a waypoint-less body — the historical "flying" bug,
`docs/research/realtime-creature-movement.md` §9's own documented reason for the suppression).

**The fix that solved the position/rate bug threw out the only code path that ever plays
gait.** Live-verified this round with `AnimationWalkingFn`/`AnimationRunningFn` probes added
to the existing capped diagnostic:

```
[14:43:22.146] gait-class: id 0x2712 on twin 0x192575E8 -> looping=0 fireForget=0 overlay=0
  animBase=0x19C6EDA8 gob=0x1BF461D8 animationExists=1 walking=1 running=0 weaponClass=8
  runLooksAwful=0
```

`walking=1` confirms `AnimationWalking(0x2712)` correctly recognises the gait id on the
phantom's own animbase — the classifier is not the problem; never reaching it is.

### 8.5 The fix, and its honest, live-measured reliability

`Odyssey.DriveBody` (`src/KotorCoop.Plugin/Odyssey.cs`) now calls
`ClientServerSyncHandler_Position` (twin vtable `+0x12C`, `TwinPositionSyncSlot`) directly,
every step (not gated on `GaitChanged` — a first attempt gating it on the gait edge only
produced correct display intermittently; calling it every step, matching how the engine's own
dirty-bit mechanism would fire continuously for a really-moving creature, measurably improved
consistency), passing `DriveBody`'s own position/id/budget. Its interpolation and rate side
effects are overwritten immediately after by the position (`twin+0x1DC`, budget 0) and rate
(`twin+0x1F8`) writes `DriveBody` already made unconditionally every step — so the historical
"flying"/zero-rate bug this suppression originally fixed does not come back.

**Live evidence it works, at least some of the time** (host walks/runs, client watches its
phantom, `tools/phantomgait.ps1`-style sampling via a scratch `gaitsample.ps1`, synced start
with the mover so no movement window is missed):

```
14:46:21.730 pos(113.86,22.15) anim 0x2714 twin 0x2710 rate 0.60 moveState -1 run 1 speed 1.24
14:46:22.135 pos(114.48,22.15) anim 0x2714 twin 0x2714 rate 0.60 moveState -1 run 1 speed 1.72  <- MATCHES
14:46:23.587 pos(115.80,22.15) anim 0x2714 twin 0x2714 rate 1.50 moveState -1 run 1 speed 2.95  <- MATCHES
14:46:26.680 pos(120.95,22.15) anim 0x2712 twin 0x2712 rate 0.96 moveState -1 run 0 speed 1.63  <- MATCHES (walk)
14:51:32.394 through 14:51:34.684 (11 consecutive 200ms samples, run phase):
  twin stayed at 0x27A3 (a real, non-idle animation — the resolved by-name variant id, not our
  raw 0x2714) for the ENTIRE stretch — no idle-while-moving in this window at all
```

The 11-sample sustained-non-idle run stretch and the individual walk/run matches above did not
happen even once, anywhere, in this bug's entire prior audit trail (rounds 59/60 measured
132/133 and 87/90 samples stuck at idle). This is real, live-confirmed progress on the actual
mechanism.

**It is not reliable.** The same live rig, same fix, different runs:

| run | mover cadence | moving samples | idle-while-moving | % slide |
| --- | --- | --- | --- | --- |
| edge-triggered (`GaitChanged` only), 1st attempt | 100ms steps | 44 | 39 | 89% |
| every-step call, 2nd attempt | 100ms steps | 44 | 28 | 64% |
| every-step call, 2nd attempt | 33ms steps (smoother) | 123 | 103 | **84%, run phase ~100%** |

The smoother-movement run was expected to do *better* (closer to a real player's continuous
WASD input) and instead did worse, especially during sustained running — the opposite of what
a synthetic-mover-artifact theory would predict. **The residual failure mode was not
identified this pass**: `ClientServerSyncHandler_Position`'s run-specific branch (§8.4) is
gated behind an extra float-threshold comparison (`fcomp` against a constant at `0x73e9ac`
against a value this pass did not trace the origin of) that walk's branch does not have, and
is the most likely place the run path specifically keeps failing; not confirmed live.

### 8.6 What remains (NEEDS-LIVE, not closed this pass)

- **Why the fix is intermittent.** The exact condition under which
  `ClientServerSyncHandler_Position`'s walk/run branches fail to "stick" between calls was not
  isolated — candidates: the unidentified float compared against `0x73e9ac` gating the run
  branch specifically; a possible race between the edge-triggered `+0x134` call (still made on
  `GaitChanged`, for the 8 combat-overlay ids and one-shot compatibility) and the new
  every-step `+0x12C` call touching the same `twin+0x58` cache; or a genuine per-frame
  precondition (e.g. the animbase's `Gob` visibility/culling state) this pass did not probe.
- **Whether the `+0x134` call is still needed at all for pure gait ids.** It was proven this
  round never to play walk/run (§8.1); it may be safe to skip entirely for `DrivePhantom`'s
  ids and keep only for `DriveEnemy`'s owner-animation branch, which can pass real
  combat-overlay ids. Not tried.
- **Mirrored enemy and companion, and the reverse direction (client→host).** Time in this pass
  went entirely into the host→client, PC-only differential; `DriveEnemy`/companion drive share
  the exact same `DriveBody` code path so the same partial fix applies by construction, but
  was not independently sampled.
- **Performance.** `ClientServerSyncHandler_Position` does real floating-point and a 2DA
  lookup; it now runs every `DriveBody` step for every driven body (up to 8 phantoms + 32
  mirrored enemies) instead of only on a gait edge. Not measured this pass; no user-visible
  slowdown observed on a 2-instance rig, but a heavier module was not tried.

### 8.7 What did NOT regress

`tests/KotorCoop.Tests` stayed at 1566/1566 passing before and after this round's change. Both
plugin builds (`src/KotorCoop.Plugin/build.ps1 -Install`) are clean for both GOG installs used
by `tools/demo.ps1`. No crash was observed across four rebuild/redeploy/live-test cycles this
round.

---

## 9. Round 62 — the run/walk asymmetry's exact gate found and closed; the deeper "does not
stick" defect isolated to a specific engine call, not fixed

Continuation of round 61 (§8), whose own "what remains" list named an untraced float
threshold gate at `0x0073E9AC` as "the most likely place the run path specifically keeps
failing." This round traced it to ground, closed it, and — being honest about the result —
found that closing it does **not** bring the bug down anywhere near reliable.

### 9.1 The `ClientServerSyncHandler_Position` (+0x12C) input table — VERIFIED

Full static disassembly of `0x00611F50` this round (stack-depth traced by hand, cross-checked
against the arg3/`animId` slot being reused as scratch at the SAME offset both at function
entry and mid-function — the two independent readings agree, which is the check that the
depth tracking itself is correct). Every input the RUN decision actually reads:

| input | source | verified how |
| --- | --- | --- |
| `desiredPos` (arg2, `Vector*`) | caller | copied into `twin+0x1DC` unconditionally, first thing the function does |
| `serverAnimId` (arg3) | caller | our own `want` (0x2710/0x2712/0x2714) |
| `budget` (arg4, float) | caller | **the actual gate input, see below** |
| `AnimationStationary(serverAnimId)` | `0x0063C170` | if true, function returns immediately — no play at all, confirmed by disassembling the early-out target (`0x006123D7`: plain `pop/pop/pop/ret 0xC`) |
| `AnimationWalking(serverAnimId)` | `0x0063C240` | true → falls straight into the shared "play" tail (`0x00612071`) **unconditionally, no further gate** |
| `AnimationRunning(serverAnimId)` | `0x0063C2A0` | true → runs the RunLooksAwful-flag update, THEN hits a **second, run-only gate** before it may reach the same shared tail |
| `GetRunLooksAwfulRate(appearance)` | `0x006975D0` (`CSWCCreatureAppearance::GetRunLooksAwfulRate`, param_size=0) | compared against `twin+0x210` (an unidentified float, presumably a cached measured speed) to set `animBase+0xCC` |
| the run-only gate: `budget < 0.5f` | `fcomp` against the constant at `0x0073E9AC`, live-read from the binary this round: **`0.5f` exactly** (`raw=0x3F000000`) | `fld [arg4]; fcomp [0x73E9AC]; fnstsw ax; test ah,5; jp 0x612238` — `jp` (branch to the RunLooksAwful fallback) is taken for `budget > 0.5`, `budget == 0.5`, AND unordered (NaN); it is NOT taken (i.e. the "nice run" formula is used) only for `budget < 0.5`, strictly |
| `ebx == 0x2714` (exact id) | `cmp ebx,0x2714; jne 0x612238` | a second, redundant-looking check right after the budget gate — belt-and-suspenders on the same branch |
| appearance.2da row (`appearance+0x18`) × column (cached in globals `0x7A212C` walk / `0x7A2120` run) | `C2DA::GetFLOATEntry` (`0x00413350`) | both walk and run-prep always do this lookup before the tail |
| `GetCurrentAnimation()`/`GetAnimationLength()` | `0x0063C060`/`0x0063C0E0` | used to scale the final playback rate; a validity check compares the 2DA rate and the anim length against `0.0` (constant `0x0073D700`) before dividing, zeroing the rate rather than crashing if either is non-positive |
| the actual "play" call | `animBase_vtable+0x54` → **`CSWCAnimBase::SetAnimationInternal`** (`0x0069EE10`, `db`-confirmed name, `__thiscall`, 20 bytes of stack args = 5 dwords) | reached unconditionally at the end of EVERY branch (nice-run, awful-run, and walk) — the function never silently skips this call once it gets past the early `AnimationStationary` check |

`ObjAnimationBudget` (`creature+0xD8`, what `DriveBody` was passing as `budget` before this
round) reads **~1.0 live** (round 60's own measurement, reconfirmed structurally this round —
it is a plain pass-through of an engine field we never write). **1.0 is not less than 0.5.**
Every single call into the RUN branch therefore permanently took the RunLooksAwful fallback,
never the "nice run" formula — a structural, 100%-reproducible asymmetry, not a race. WALK has
no budget gate anywhere in its path. This precisely explains round 61's own observation that
walk partially worked while run almost never did, and does so from a verified constant, not a
guess.

### 9.2 The fix: a call-site-local budget, not the engine's

`Odyssey.DriveBody` (`src/KotorCoop.Plugin/Odyssey.cs`) now passes a new constant,
`TwinPositionSyncBudget = 0f`, to the `+0x12C` call specifically — **not**
`ObjAnimationBudget`, which is untouched and still used for the separate `+0x134` call below it
(proven in round 61 §8.1 never to reach this gate at all, so changing it there would be a no-op
either way). `0f` satisfies `budget < 0.5` unconditionally, and is the same "this is a
continuous update, not a fresh onset" semantics already used for
`TwinDesiredPositionBudget`/`TwinDesiredPosition`'s own budget=0 "snap" write two lines above
it. Nothing else in the traced RUN path re-reads the budget value after the gate (confirmed —
`[esp+0x34]`'s slot is not loaded again on the taken branch), so this is a pure gate flip, not
a scaling change.

Both plugin builds clean; `dotnet test tests/KotorCoop.Tests` stayed at **1566/1566** before and
after.

### 9.3 The fix does NOT bring the bug down to acceptable — live-measured, and root-caused one
level deeper than round 61 reached

Live rig, this round's `movepc2.ps1`/`gaitsample.ps1` (reused unmodified from round 61's scratch
tooling — the plugin's `SharedBlock.OffsetEnginePlayerPointer` for the mover, the plugin's own
`"spawned phantom … at 0xADDR"` log line for the sample target, per round 60 §7.1's tooling-trap
finding; **not** a vtable scan), 10 s walk (1.6 u/s) + 10 s run (3.96 u/s), both directions:

| case | moving samples | idle-while-moving | fraction |
| --- | --- | --- | --- |
| host→client phantom | 166 | 165 | **99.4%** |
| client→host phantom | 117 | 95 | **81.2%** |
| mirrored enemy | not independently sampled this round (time) — shares the identical `DriveBody` code path by construction, same as round 61 left it | — | — |
| companion (Trask, `end_trask`, id `0x127`) | the companion stayed pinned at a near-fixed offset for the entire 20 s window (`party drive: companion 0x127 held neutral and pinned — the host owns its health`) regardless of the host's own sustained movement — only 1 of ~90 samples showed it moving at all, an artifact of the party-follow AI, not this bug. Not a valid movement sample this round. | — | — |

**This is not an improvement over round 61's 64-89% range — if anything, this specific run
measured worse.** The budget-gate fix is real and verified (§9.1/§9.2), but it was necessary,
not sufficient: it only controls which FORMULA `ClientServerSyncHandler_Position` uses to
compute the play call's arguments. It does not control whether that play call's effect is
actually visible.

**New finding this round, isolating the defect one level deeper.** A capped (60-sample),
call-site diagnostic was added around the `+0x12C` call: read `twin+0x48` immediately before
the call, call it, read `twin+0x48` immediately after. Live result: **`before == after` in
60/60 samples, on both instances, with zero exceptions** — the call never changes `twin+0x48`
synchronously, ever, even though the SAME field visibly does change across separate calls a
frame or more apart (the sporadic non-idle reads noted in round 61 and reproduced again this
round: `0x27A3`, `0x27A8`, occasional correct `0x2712`). This rules out round 61's implicit
model ("it plays, then something reverts it within the same step") — the 60-sample cap was
exhausted during the WALK phase before any RUN sample was captured, so this specific
before/after result is confirmed for the WALK branch only; RUN was not independently
re-verified with the diagnostic this round (NEEDS-LIVE).

What this means: `SetAnimationInternal` (`animBase_vtable+0x54`, `0x0069EE10`,
`db`-confirmed as `CSWCAnimBase::SetAnimationInternal`, 5 stack dwords) either queues its
effect for a later, separate engine update pass rather than applying it inline, or has its own
internal gate (an "already playing"/dedup check, a `Gob` visibility precondition, or something
keyed on the 5 args this round did not individually decode) that silently no-ops most calls.
Whichever it is, it is downstream of everything `ClientServerSyncHandler_Position` computes,
and is the actual place a future round needs to disassemble next — `SetAnimationInternal`
itself, not its caller.

### 9.4 Cost

No controlled with/without A/B was run this round (would need a second build with the `+0x12C`
call compiled out, and time did not allow it — same limitation round 61 left open). A relative
signal only: `tools/pluginstate.ps1`'s frame counter, sampled on both instances while the
phantom was actively moving (walk+run, the call firing every step for the one live phantom on
each side), showed no stall or frame-counter freeze — both instances kept advancing at a normal
rate for an unfocused/background window throughout the test. This is the same "no user-visible
slowdown observed, heavier module not tried" caveat round 61 already recorded; still true, still
not a real measurement.

### 9.5 What remains (NEEDS-LIVE, not closed this round)

- **`SetAnimationInternal` itself.** The actual next step: disassemble `0x0069EE10` (or
  single-step it live) to find why its effect on `twin+0x48` is not synchronous and not
  reliable — this is now a precisely-scoped target instead of "somewhere in the chain."
- **RUN-specific verification of the before/after diagnostic.** The 60-sample cap exhausted
  during walk; re-run with a higher cap or a run-only trigger to confirm the "never synchronous"
  finding also holds for the run branch's "nice" sub-path (now reachable thanks to §9.2's fix).
- **Mirrored enemy**, independently sampled (shares the code path by construction; not
  independently measured two rounds running now).
- **Companion/Trask**, with an actual moving companion — this round's rig had it pinned by the
  party-follow logic for the whole window, not a fault of the gait mechanism being tested.
- **Cost**, a real with/without A/B build.

### 9.6 What did NOT regress

`tests/KotorCoop.Tests` stayed at 1566/1566 passing before and after. Both plugin builds
(`src/KotorCoop.Plugin/build.ps1 -Install`) clean for both GOG installs `tools/demo.ps1` uses.
No crash observed across two rebuild/redeploy/live-test cycles this round. `ObjAnimationBudget`
and the `+0x134` call path are untouched — this round's change is scoped to the single new
`TwinPositionSyncBudget` constant and its one call site, plus a capped, read-only diagnostic.

---

## 10. Round 63 — a citation error corrected, two hypotheses tested live and falsified, and
the real chokepoint traced one level further than any prior round, still not fixed

Method, per this round's brief: one variable at a time (E1–E4), each measured live with
`tools/phantomgait.ps1`'s methodology before being kept or reverted. **Honest headline: the bug
is not fixed this round either.** Final live numbers, 10 s walk (1.6 u/s) + 10 s run (3.96 u/s),
both directions, with the round's one kept change (E1) shipped:

| case | moving samples | idle-while-moving | fraction |
| --- | --- | --- | --- |
| host→client phantom | 84 | 82 | **97.6%** |
| client→host phantom | 129 | 125 | **96.9%** |
| mirrored enemy | not sampled — this pass's save (`END_M01AA`, the ending sequence) has no live hostiles; same code path (`Odyssey.DriveEnemy` → `DriveBody`) applies by construction, unconfirmed independently for the fourth round running | — | — |
| companion (party member) | not sampled — no companion in this save's party; round 62's own finding (follow-AI pinned, plugin holds it "neutral") is the standing evidence and was not re-tested | — | — |

Both measured cases are statistically indistinguishable from round 62's baseline (99.4% /
81.2%) — within the noise a 10-second synthetic-mover sample produces, not an improvement.
**README's walk/run bullet is not restored.**

### 10.1 A load-bearing citation error, found and corrected: `SetAnimationInternal` is at
`0x0069E8D0`, not `0x0069EE10`

Round 62 §9.1 named `animBase_vtable+0x54` → `CSWCAnimBase::SetAnimationInternal` at
`0x0069EE10`, "db-confirmed name," and round 62 §9.3 pointed the next round at disassembling
that address. **This address is wrong, and disassembling it directly makes the mistake
self-perpetuating**: `0x0069EE10` decodes to plausible-looking x86 for the first few bytes
before resyncing into garbage-then-real code that is actually the *middle* of a different,
unrelated function.

Verified three independent ways this round:

1. **The debug symbol database disagrees.** `tools/q.py func SetAnimationInternal` (queries
   `tools/kotor1_0_3.db`, the same SQLite symbol DB every `db-confirmed` claim in this file
   traces back to) returns `CSWCAnimBase::SetAnimationInternal` at **`0x0069E8D0`**. A
   companion query, `SELECT ... FROM functions WHERE address BETWEEN 0x69EA00 AND 0x69EFA0`,
   shows `0x0069EE10` falls inside `CSWCAnimBase::GetBoundingRect` (starts `0x0069EDC0`) — a
   bounding-box computation, unrelated to animation playback.
2. **A linear disassembly from a known-good earlier boundary (`0x0069EDF0`, inside
   `GetBoundingRect`) shows `0x0069EE10` sits one byte before a real instruction boundary**
   (`0x0069EE11`, `mov dword ptr [esp+0x30],0`) — not a function entry at all. `push -1; push
   <SEH frame>; mov eax,fs:[0]; ...` (the standard MSVC SEH-frame prologue every function in
   this binary that uses one starts with) is what a *real* entry looks like, and `0x0069E8D0`
   has exactly that prologue; `0x0069EE10` does not.
3. **The vtable itself was read directly off disk, not trusted from either citation.**
   `CSWCAnimBase`'s vtable (`0x00754F60`, from `tools/q.py class CSWCAnimBase`) at `+0x54`
   holds `0x0069D4F0`. That address is **`CSWCAnimBase::SetAnimation`** (confirmed via the
   same symbol DB), a five-line thiscall-forwarding stub:

   ```
   0x0069D4F0  mov edx,[esp+0x10]; push edx        ; forward arg4
   0x0069D4F7  mov edx,[esp+0x10]; push edx        ; forward arg3 (esp shifted by prior push)
   0x0069D4FC  mov edx,[esp+0x10]; push edx        ; forward arg2
   0x0069D501  mov edx,[esp+0x10]; push edx        ; forward arg1
   0x0069D506  mov edx,[ecx+0xb8]; push edx        ; prepend animBase->gob (AnimBaseGob)
   0x0069D50D  call dword ptr [eax+0xf0]           ; SAME animBase, vtable slot +0xF0
   0x0069D513  ret 0x10
   ```

   `CSWCAnimBase` vtable `+0xF0` (read the same way) is `0x0069E8D0` — matching the symbol DB
   exactly. So the real call chain, one hop longer than round 62 thought, is
   `ClientServerSyncHandler_Position (+0x12C)` → `animBase_vtable+0x54` = `SetAnimation`
   (thin forwarder) → `animBase_vtable+0xF0` = **`SetAnimationInternal` (real address
   `0x0069E8D0`)**.

**Why this matters beyond bookkeeping**: every prior round's disassembly of "SetAnimationInternal"
was reading `GetBoundingRect`'s body — an unrelated bounding-box routine with its own jump
tables (the "garbage" bytes several earlier rounds' raw dumps show past the resync point were
literal switch-table data belonging to `GetBoundingRect`, not corrupted code). Round 62's §9.3
conclusion ("either queues its effect for a later pass, or has its own internal gate ... the
target for the next pass") was reasoning about the wrong function's shape. It is not wrong as a
*hypothesis about the real function* — see §10.2 — but nothing in round 62 actually looked at
`SetAnimationInternal` yet.

### 10.2 The real `SetAnimationInternal`, disassembled — idle, walk and run all reach the
identical generic path

Full static disassembly of `0x0069E8D0` (VERIFIED, this round; `python tools/disasm.py
0x69E8D0 1264`, cross-checked against the SEH-frame prologue/epilogue matching every other
function in the binary that has one). Signature: `bool __thiscall SetAnimationInternal(this,
ushort id, ..., float budget, ...)` (exact stack layout not fully typed; the epilogue's `ret
0x14` confirms 5 stack dwords, matching round 62's byte count even though it was attached to
the wrong address).

The function's own dispatch, decoded from its jump tables as **data**, not disassembled
in-line as code (the mistake that produced garbage in every earlier round's raw dump of the
wrong address):

- `id > 0x2798` or `id` in `[0x2713, 0x2792]`: dispatches through one of two byte-indexed jump
  tables (`0x0069E9E4`/`0x0069E9F0` for the low range, `0x0069EA70`/`0x0069EA7C` for the high
  range) to a small set of per-id handlers — mostly combat-overlay/one-shot ids.
- **Every id outside those hardcoded ranges — including all three gait ids, since `0x2710`
  and `0x2712` are below `0x2713` (unsigned-wrap out of the low table entirely) and `0x2714`
  (offset 1 into the low table) maps to case 2, decoded directly from the byte table: `case
  2 → 0x0069E987`, the SAME address the out-of-range default falls through to** — lands on one
  shared "generic" path:

  ```
  0x0069E987  mov ecx,[esp+0x38]; mov edx,[esp+0x34]; mov eax,[esp+0x30]   ; the budget float
  0x0069E993  mov esi,[ebx]              ; ebx = result of the vtable+0x40 lookup (below)
  0x0069E99C  call 0x5e5670               ; CExoString::CStr — char* from a name
  0x0069E9A4  call [esi+0x18]             ; virtual call: ebx->vtable[0x18](name-derived arg)
  0x0069E9A7  mov edi,1                   ; success
  ```

  **Idle, walk and run are not treated differently by `SetAnimationInternal` at all** — this
  rules out an internal per-gait-id divergence as the explanation for round 61/62's
  walk-vs-run asymmetry (already fully explained by the `ClientServerSyncHandler_Position`
  budget gate round 62 closed) and rules it out as the explanation for why NONE of the three
  gait ids render reliably.
- `ebx` (dereferenced for the virtual call above) comes from an earlier call at the top of the
  function: `call [eax+0x40]` on `animBase`'s own vtable, with the **real requested id**
  pushed as an argument. `CSWCAnimBase` vtable `+0x40` (read off disk, same method as §10.1) is
  **`0x0069E620` = `CSWCAnimBase::GetAnimationName`** — confirmed by the symbol DB. If this
  lookup fails (the early `je 0x69e9ac` at the very top of the function, before any of the
  above), `SetAnimationInternal` returns `false` having touched nothing.

### 10.3 A promising, NOT live-confirmed lead: `GetAnimationName` silently substitutes idle
for an unresolved gait name

Disassembling `GetAnimationName` (`0x0069E620`, VERIFIED, this round — `python tools/disasm.py
0x69E620 350` plus a second pass to `0x69E8C9` for its tail) shows a two-attempt structure:

1. **First attempt, with the real id.** Calls `animBase_vtable+0xE0`
   (`AnimBaseServerToClientSlot`/`ServerToClientAnimation`, already wired as a callable
   delegate in `Odyssey.cs` and already proven live in round 61 to resolve `0x2712`→row 338
   (`walkSS`) and `0x2714`→row 345 (`runSS`) for weapon class 2) with the real requested id,
   then looks up a 2DA string for the resolved row. If that string's length is `> 0`, it is
   copied into the caller's return buffer and the function returns immediately — this is the
   success path a genuinely-resolvable id takes.
2. **Fallback, only reached when step 1's string comes back empty.** The function then checks
   eight small hardcoded id ranges (`[0x3e8,0x467]`, `[0x4b0,0x52f]`, `[0x578,0x5f7]`,
   `[0x640,0x6bf]` — all well below `0x2710`, so none of our gait ids match any of them) for a
   combat/overlay name table. **Falling through that too** (`0x0069E816` onward — exactly the
   branch every gait id reaches, since none is in those ranges), it does one more check
   (`call 0x5e58a0` on the step-1 local string; skip if a special-cased `id == 0x2772`) and
   then — for everything that reaches this point — **calls `animBase_vtable+0xE0` a SECOND
   time with the literal constant `0x2710` (idle) hardcoded in the call, discarding the real
   requested id entirely**, and uses whatever that resolves to (or an empty string, if even
   that fails) as the name eventually handed to `SetAnimationInternal`'s "play" step.

**If step 1's 2DA-string lookup for `0x2712`/`0x2714` comes back empty on this codepath, this
would be a complete, mechanistic explanation for the whole bug**: `SetAnimationInternal` would
be told to play IDLE's name whenever asked for walk or run, on every creature reached through
this call chain, by design of the engine's own fallback — not because of anything a driven
phantom does differently from an AI-walked creature. This would also explain why three
separate rounds (60–63) chasing "what's different about the phantom" kept coming up with
negative results (§8.3's differential dump, this round's E1/E2) — there may be nothing
different about the phantom to find; the mechanism may fail the same way for every creature
that reaches it, and a normally-walking creature's *rendered* gait may come from an entirely
separate, velocity/blend-driven subsystem that never calls `SetAnimationInternal` with a
discrete id at all (consistent with `animations.2da`'s own schema — VERIFIED this round via
`tools/kotorfiles.py --game <install> 2da animations.2da --rows 337:346` — having no
"model"/"root name" column distinct from the row's own `name` field, i.e. nothing for step 1's
lookup to plausibly return besides the literal, always-non-empty strings `walkSS`/`runSS`,
which is the one piece of live evidence that argues AGAINST the empty-string theory as
literally stated and this round did not resolve).

**NOT confirmed live.** Reaching a firm answer needs one of: a capped live diagnostic (this
round's remaining time went to E1/E2/E4 instead) reading the step-1 2DA string's actual length
for `0x2712`/`0x2714` on a real phantom; or single-stepping `GetAnimationName` with a debugger,
which this project's tooling does not have (static disassembly plus targeted `ReadProcessMemory`
probes only). Flagged honestly as the most concrete unfinished lead this file has produced —
more specific than "somewhere in the chain" (round 60), "the caller, not this function" (round
61→62), or "this function, not its caller" (round 62→63, and it turned out round 62 was
disassembling the wrong function besides).

### 10.4 E1 — skip the animation-sync call (`+0x134`) for Walk/Run — live-tested, no effect,
kept as a proven dead-code cleanup

Hypothesis: `DriveBody` still calls the edge-triggered `+0x134` `ClientServerSyncHandler_Animation`
sync handler for every gait change including Walk/Run, and — round 61 §8.1 already proved this
call structurally can never PLAY a gait id (`AnimationLooping` rejects `walkSS`/`runSS` rows
inside `CSWCObject::SetAnimation`, the setter `+0x134` falls through to) — the E1 hypothesis was
that this dead call was actively harmful: re-touching the twin's `+0x58` animation cache on the
exact same edge frame `+0x12C` seeds it might stomp that seed before the per-frame update ever
applies it.

Change: `Odyssey.DriveBody` now skips the `+0x134` call when `want` is `GaitRule.AnimWalk` or
`GaitRule.AnimRun`, keeping it for Idle and for `DriveEnemy`'s owner-animation branch (which can
legitimately pass real combat/overlay ids the call chain DOES handle).

**Live-measured, host→client, 10 s walk + 10 s run**: 77 moving samples, 74 idle-while-moving
(**96.1%**) — statistically the same as round 62's pre-E1 baseline (99.4%), not an improvement.
**Kept anyway**, unlike round 60's falsified `+0xA0` attempt: unlike that attempt, this one is
not a guess — round 61's own disassembly already proves the skipped call is a no-op for these
two ids specifically, so removing it is a correct simplification independent of whether it fixes
the bug, and it measured no regression.

### 10.5 E2 — stop telling the twin it has already arrived while moving — live-tested, no
effect, reverted

Hypothesis: `DriveBody` writes `twin+0x1E8` (`TwinDesiredPositionBudget`) to `0` every step —
"snap, zero distance to travel" — even while genuinely moving, which is the opposite of the
engine's own normal semantics (`CSWCCreature::ResetDesiredPositionTimer` writes `0x200`, 512 ms,
during real engine-driven movement, per this file's own top-of-region comment). If whatever
per-frame logic decides walk-vs-idle reads *interpolation progress* rather than the discrete
gait id, telling it "arrived instantly" every single step could be exactly what keeps the answer
at idle.

Change (tested): write `0x200` instead of `0` while `moving` is true, keep `0` for idle/arrived.

**Live-measured, host→client, E1+E2 combined, 10 s walk + 10 s run**: 127 moving samples, 123
idle-while-moving (**96.9%**) — again statistically unchanged from baseline. **Reverted.** Unlike
E1, this change has no independent justification once it fails to help — it touches a field a
downstream engine pass genuinely consumes (unlike the proven-dead `+0x134` call), so shipping an
unproven, unhelpful change to it is worse than reverting to the known, previously-live-verified
`0` write. `TwinInTransitBudgetMs` and its call site were removed; `Odyssey.cs` is back to
round 62's exact write here.

### 10.6 E3 — see §10.1–10.3. The disassembly work is done; the live confirmation is not.

### 10.7 E4 — a control creature, attempted and blocked

The brief's control case ("sample a Sith soldier the engine walks via `ActionMoveToLocation`")
needs a creature under genuine engine AI control, not the local player. Two paths were tried:

- **The local player, via the plugin's own test-drive hook** (`SharedBlock.OffsetTestWalkX/Y/
  Serial`, `TestDriveController.HandleWalk`, which queues a real `ActionMoveToLocation` — no
  raw position poke). **Blocked by design, confirmed live**: the plugin's own log line explains
  why — `"NOTE the PC is drive-controlled, so an action-queue move may not move it at all"` —
  and live-measured this round, that is exactly what happened: `moveState` went `1`→`2`
  ("requested") and stayed there for the full 15 s sample window; position never changed at all.
  The queued action never executes because the player-controlled body overrides its own action
  queue.
- **A companion or hostile NPC.** This pass's save (`END_M01AA`, the ending sequence, loaded by
  `demo.ps1`'s default autosave) has no hostiles and no party companion loaded — `Odyssey.
  MoveToLocation` can target an arbitrary creature id, but nothing in the current test-drive
  surface exposes an arbitrary target id (only the player), and no candidate creature exists in
  this save to target even if it did. Adding that surface (a new shared-block target-id field)
  was judged out of scope for a locomotion-diagnostic pass.

**E4 was not completed.** A future round needs either a save with a live hostile or party
companion, or a small addition to `TestDriveController`/`SharedBlock` exposing an explicit
target object id for the test-walk hook (not just the player).

### 10.8 What remains (NEEDS-LIVE, not closed this round)

- **§10.3's lead, confirmed or falsified live.** The single highest-value next step: a capped
  diagnostic reading `GetAnimationName`'s step-1 2DA-string length for `0x2712`/`0x2714` on a
  live phantom (and, for comparison, on a creature that does show a working gait, if one can be
  found or constructed) settles whether the fallback-to-idle substitution in §10.3 is the actual
  mechanism.
- **Whether ANY creature in this engine ever renders walk/run through `SetAnimationInternal` at
  all**, or whether a normally-AI-driven creature's visible gait comes from an entirely separate,
  velocity-driven blend system that this whole `ClientServerSyncHandler_Position` /
  `SetAnimationInternal` chain was never meant to serve for continuous locomotion (only for the
  hardcoded combat-overlay/one-shot id ranges `SetAnimationInternal`'s jump tables actually
  specialize). If true, three-plus rounds of work on this exact call chain have been chasing a
  mechanism that was never going to produce a rendered walk cycle, and the real fix is finding
  and driving whatever field `CSWCCreature::AnimationUpdate`'s per-frame pass reads for blend
  weight/velocity instead.
- **E4**, genuinely completed against an AI-controlled creature — needs a save with a hostile or
  companion, or a small, explicit-target-id addition to the test-drive surface.
- **Mirrored enemy**, independently sampled — fourth round running without this.
- **The tooling gap round 60 §7.1/§7.6 already flagged** (`phantomgait.ps1`'s vtable-scan
  auto-resolve can latch a frozen duplicate on this module) is still open; every live sample in
  this file since round 60 has worked around it with an explicit `-Address`, never fixed it.

### 10.9 What did NOT regress

`tests/KotorCoop.Tests` stayed at **1591/1591** passing before and after (baseline grew from
1566 since round 62 from unrelated work on other branches merged into this one; no test in this
pass's own scope changed count). Both plugin builds
(`src/KotorCoop.Plugin/build.ps1 -Install`) clean for both GOG installs across three
rebuild/redeploy/live-test cycles this round. No crash observed. `ObjAnimationBudget`,
`TwinPositionSyncBudget`, the `+0x12C` call, and the rate/position writes are all byte-for-byte
what round 62 shipped — this round's net source change is the single `+0x134` skip for Walk/Run
(§10.4), a corrected address citation in this file, and the `gait-class`/`sync12c` diagnostics
left exactly as round 61/62 left them.

---

## 11. Round 64 — §10.3's idle-substitution theory ground-truthed and falsified; a real,
different client-side sync gap found and partially fixed (the model, not the weapon class);
still not fixed end to end

Two decisive experiments per this round's brief: **X1**, a live diagnostic reading
`GetAnimationName`'s actual resolved string (not just the classifier booleans) on the phantom
AND, side by side, on the local player's own animbase; **X2**, testing whether the phantom's
CLIENT-side model/animbase is ever told to reload after the appearance-row poke, since nothing
in this codebase called the client-side counterpart of `UpdateAppearanceDependantInfo` before
this round.

**Headline: §10.3's hypothesis, taken literally, is FALSE.** `GetAnimationName` does not
substitute an empty/idle string for the phantom's gait ids — it resolves real, non-empty,
non-idle names (`'walk'`, `'run'`, `'cwalk'`, `'pause1'`, …), live-confirmed on both the
phantom and, incidentally, on 26 simultaneously-mirrored hostiles. X2's fix (§11.2) is real and
measurably closes part of the gap (the phantom's model resref, always empty in every prior
round, now matches the player's), but the aggregate idle-while-moving fraction barely moves.
The actual remaining defect is narrower and more specific than three prior rounds' framing: a
SECOND client-side field the network equip-message would normally set (here: the weapon-class
byte, not the appearance row) is never populated, and even where the resolved name is
comparable to the player's, the render field still does not reliably update — pointing the next
round at what happens AFTER `GetAnimationName`, not at name resolution itself.

### 11.1 X1 — the name diagnostic, live, both sides, and (incidentally) on 26 mirrored enemies

A new diagnostic (`Odyssey.ReadAnimationName`, wrapping the real `CSWCAnimBase::GetAnimationName`
call, `0x0069E620`, through its actual ABI — hidden-return `CExoString*` first stack arg, id
second, `ret 8`, verified this round by disassembling the prologue/epilogue, not assumed) logs,
capped at 20 samples per driven creature, the resolved name/length for the requested gait id on
BOTH the driven body's animbase and the local player's own animbase, side by side, plus each
one's model resref (`CSWCAnimBase.name`, a `CResRef` at animbase+0x2C) and weapon-class byte
(animbase+0xC5). It was wired into `Odyssey.DriveBody`'s existing gait-change hook.

**Tooling trap, found and fixed mid-round:** the diagnostic was first nested inside the
existing `_gaitClassLogged < 30` cap from round 60/61 — a GLOBAL, process-lifetime counter
already exhausted after a few hours of rig uptime, which silently dead-coded the new diagnostic
for the rest of that process's life before a single line was seen. Decoupled into its own
per-creature cap (`GaitNameLogged`, mirroring the existing `GaitCreature`/`GaitLast` slot
array) and re-verified live after a redeploy+restart.

**The diff, live, host→client (client renders a phantom of the host; both post-equip, model
loaded, `gob != 0`):**

| id | phantom name | phantom len | phantom model | phantom weaponClass | player name | player len | player model | player weaponClass |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 0x2712 (walk) | `walk` | 5 | `pfbalb01` | **8** | `walkSS` | 7 | `pfbbl01` | **2** |
| 0x2714 (run) | `run` | 4 | `pfbalb01` | **8** | `runSS` | 6 | `pfbbl01` | **2** |
| 0x2710 (idle) | `pause1` | 7 | `pfbalb01` | 8 | `pause1` | 7 | `pfbbl01` | 2 |

(Raw log lines, `D:\Games\swkotor-gog-p2\kotorcoop-plugin.log`, 16:54:50–16:54:55 in the same
pass's rig; `pfbalb01`/`pfbbl01` are the same base female-Twi'lek-adjacent body class, two body
variants — the appearance row on both sides was 114, the host's real PC appearance.)

**Reading the diff**: every field is non-empty. The MODEL matches (§11.2 — this is new; every
prior round's differential dump found the phantom's model resref empty). The only field that
differs is **weaponClass: 8 on the phantom, 2 on the player** — and `ServerToClientAnimation`
keys its row lookup on exactly this byte (§8.2), so the phantom legitimately resolves a
DIFFERENT valid row (`walk`/`run`, the unarmed/generic locomotion set) than the player
(`walkSS`/`runSS`, the single-weapon-stance set) — not a failure to resolve anything. `idle`
resolves identically on both (`pause1`) because idle has no weapon-stance variant.

**Before the model fix landed** (a sample taken the same run, at spawn, one frame before the
gob/model finished loading): `phantom-> name='cwalk' len=6 model='' appearanceRow=114
weaponClass=8` — this is what every prior round's sample looked like (empty model, and note
even THIS early sample resolved a real name, `cwalk`, not an empty string — the "idle
substitution" was never observed live, in this round's sampling, at any point).

**The same diagnostic, incidentally, on 26 simultaneously-mirrored hostiles** (client mirroring
the host's Vulkar pack at `tar_m10aa`, each with its own model/weaponClass): every single one
also resolved a real, non-empty, non-idle name for its own gait id —
`c_drdassassin`/weaponClass 7 → `crun`/`cwalk`; `n_commf03`/weaponClass 8 → `walk`/`cwalk`;
`pmbd01`/weaponClass 5 → `pause1`. **This was not asked for by X1 specifically but is strong
corroborating evidence**: name resolution is not a phantom-specific failure and not an
idle-substitution failure anywhere this round could observe it, on 27 different driven bodies
across two categories (phantom, mirrored enemy).

**Verdict**: §10.3's mechanism, as literally stated ("`GetAnimationName` silently substitutes
idle"), is FALSIFIED. The real, narrower finding is a weapon-class sync gap (§11.3), and the
still-open question is what happens downstream of a successfully-resolved name (§11.4).

### 11.2 X2 — the client-side appearance rebuild, implemented and confirmed to fix the model

Static disassembly this round found the exact gap the brief hypothesized: `PhantomController`'s
appearance poke (`ApplyAppearance`) already called the SERVER-side rebuild
(`CSWSCreature::UpdateAppearanceDependantInfo`, per `phantom-equip-crash.md`), but nothing in
this codebase ever called the CLIENT-side counterpart. Traced live from the vtable:
`CSWCAnimBase` vtable+0xF0 → `SetAnimationInternal` (§10.1) already established the animbase
chain; this round instead traced `CSWCCreature::ForceAppearanceUpdate` (`0x00614A30`, thiscall,
no stack args, db-confirmed), which:

1. reads `this+0x38C` (a CLIENT-side cached "desired appearance row" ushort — never written by
   anything in this codebase before this round),
2. writes it into `this->appearance->appearance_info.appearance_id`
   (`CSWCCreatureAppearance+0x18`, matching `CCAppearanceInfo.appearance_id` at offset 24 in the
   symbol db, embedded at that object's own offset 0), and
3. calls `CSWCCreature::SetAppearance` (`0x006134C0`, one of `ForceAppearanceUpdate`'s own 7
   live call sites) — which itself calls `CSWCObject::GetAnimBase` (`0x0063C070`) and the
   `AnimationLooping`/`AnimationStationary` classifiers, i.e. genuinely touches the animbase,
   not just server bookkeeping.

**Fix**: `Odyssey.RebuildClientAppearance(creature)` reads the server's already-correct
appearance row (`CSWSCreature+0xA60`, the same field `PhantomController.ApplyAppearance` just
wrote), writes it to the twin's `+0x38C`, and calls `ForceAppearanceUpdateFn(twin)`. Wired into
`ApplyAppearance` right after the existing server-side `RebuildAppearance` call, same guard
(only on an actual row change).

**Live result**: confirmed working for the MODEL, per §11.1's diff table —`pfbalb01`/`pfbbl01`
model match is a first for this investigation; every prior round's differential dump
(round 61 §8.3, round 62, round 63) found the phantom's model resref empty or never checked it
post-equip. **Not confirmed to fix the render symptom** — see §11.4's fractions, which are
statistically the same as round 62/63's baseline. The fix is real, necessary, and kept (no
regression, no crash across the round's rebuild/redeploy/live-test cycles), but it was not
sufficient on its own.

### 11.3 A second, still-open client-side sync gap: weapon class

`CSWCCreature::UpdateWeaponClass` (`0x00613710`, thiscall, no stack args, db-confirmed) sits
immediately after `SetAppearance` in the binary and has the same shape as the appearance gap:
it reads `this+0x230`/`this+0x234` (`CSWCCreature.main_hand_id`/`off_hand_id`, both already
named in `tools/kotor1_0_3.db`) — CLIENT-side cached "what's in my hands" fields — and writes
the derived weapon-class byte to the animbase. `PhantomController.ApplyEquipment` equips the
phantom's weapon through the SERVER-side NWScript pair (`CreateItemOnObject`/`ActionEquipItem`,
§ Odyssey.EquipItemFromTemplate) and never touches `main_hand_id`/`off_hand_id` on the client
twin — the exact same shape of gap X2 just closed for appearance, unclosed for weapon class.

**NOT fixed this round.** `CSWCCreature::EquipToInventorySlot` (`0x00613ba0`, the likely
client-side counterpart, 10 live call sites) takes a CLIENT-side item pointer as its second
argument (dereferencing `item+0x10`/`item+0x14` for the value it stores) — a type this codebase
does not have a way to obtain for a server-created item without further tracing. Flagged as the
next concrete target rather than attempted blind, given the live-write risk of guessing wrong
values for a field two engine calls deep. This is NOT the same defect as §11.4's residual
render failure — it is a genuine, separate, honestly-scoped gap: even a perfect fix here would
only make the phantom request `walkSS`/`runSS` instead of `walk`/`run`, both of which already
resolve to real names (§11.1), so it would not by itself explain why the render field mostly
does not update at all.

### 11.4 The render symptom, live-measured after the X2 fix — not fixed

`tools/phantomgait.ps1`-style sampling (this round's scratch `gaitsample.ps1`/`mover.ps1`, per
round 60 §7.1's tooling-trap workaround — explicit `-Address`, never a vtable scan), 8s walk
(1.6 u/s) + 10s run (3.96 u/s), both directions, `tar_m10aa`, with X2's fix live:

| case | moving samples (speed>0.3) | idle-while-moving | fraction |
| --- | --- | --- | --- |
| host→client phantom | 252 | 244 | **96.8%** |
| client→host phantom | 247 | 225 | **91.1%** |
| mirrored enemy | not measured — see §11.5 | — | — |
| companion (party member) | not measured — see §11.5 | — | — |

Both measured cases are statistically indistinguishable from round 62/63's baseline (81–99%
range measured across rounds 62–63) — **not an improvement**, despite the confirmed model fix.
One brief, genuine non-idle stretch was observed mid-run in this round's raw trace (twin held a
real animation id, `0x27A3`, for roughly 1.5s during the run phase) — consistent with round
61's own "11 consecutive non-idle samples" finding — showing the render CAN succeed
momentarily, just not reliably. **README's walk/run bullet is not restored** — not all four
required cases are even measured this round, and the two that are measured are not clean.

### 11.5 Mirrored enemy and companion — both blocked, for two different, newly-documented reasons

**Mirrored enemy: blocked by a tooling trap, not by the phantom-locomotion defect.** Warped to
`tar_m10aa` (host-only `warp`, client follows — `party-reconciliation.md`'s established
recipe) and picked the same Black Vulkar pool round 55/62 used (`0x38`). A direct position poke
on the HOST's authoritative copy was confirmed to work (re-read on the host: position moved
~85 units across the test). **The CLIENT's mirrored copy never showed the movement.** A full
(not first-match) memory scan for object id `0x38` on the client found **two** distinct
addresses, both frozen at different stale positions, neither ever updating — round 60 §7.1's
"frozen duplicate" trap, now independently confirmed to affect mirrored enemies, not just
players/phantoms. Unlike the player (`SharedBlock.OffsetEnginePlayerPointer`), this codebase
has no trusted non-scan address source for an arbitrary mirrored enemy, so the fraction could
not be measured this round. A second, independent complication found along the way: this
specific creature also has its own proximity-triggered `local_combat` auto-engage subsystem
that excludes it from normal `DriveEnemy` census-mirroring whenever the local client player is
within its `engageRange` (11 units) — worth knowing for whoever picks this up next: stay
farther than that, and confirm via the `"enemy drive: mirroring N"` log line (and the absence
of any `"local combat"` line for the target id) that the creature is in the normal
census-mirrored path before sampling it.

**Companion (Trask): blocked, identical to round 63.** This save's party roster is `0x001`
(PC only) — no companion recruited, same as round 63's finding. No in-plugin cheat exists to
force a recruit; building one (a custom NWScript) was judged out of scope for a
locomotion-focused pass and not attempted.

### 11.6 What remains (NEEDS-LIVE, not closed this round)

- **What happens downstream of a successfully-resolved name.** §11.1 shows `GetAnimationName`
  resolves real names reliably; §11.4 shows the render field still mostly does not update. The
  gap is now precisely between those two facts: `SetAnimationInternal`'s virtual "apply" call
  (`ebx->vtable[0x18]` off the resolved-name lookup, §10.2) or the per-frame `AnimationUpdate`
  reassertion is the next disassembly target, not `GetAnimationName` itself.
- **The weapon-class client-side sync gap (§11.3)**, genuinely scoped this round but not fixed —
  needs either a client-side item pointer for `EquipToInventorySlot`'s second argument, or a
  more direct write to `main_hand_id`/`off_hand_id` plus `UpdateWeaponClass`, both unverified
  live.
- **Mirrored enemy and companion fractions**, blocked this round for the two reasons in §11.5 —
  a non-scan address source for a mirrored enemy (or a scan that disambiguates frozen
  duplicates), and a save/cheat that puts a companion in the party, are both needed before
  either can be measured.
- **The tooling gap itself** (round 60 §7.1, reconfirmed for enemies this round) is still open
  in the actual tools (`phantomgait.ps1`'s auto-resolve, `teleport.ps1`) — every live sample in
  this file since round 60 has worked around it by hand, never fixed it at the source.

### 11.7 What did NOT regress

`tests/KotorCoop.Tests` stayed at **1591/1591** passing, before this round's changes and again
after, on a full run. Both plugin builds (`src/KotorCoop.Plugin/build.ps1 -Install`) clean for
both GOG installs across two rebuild/redeploy/live-test cycles this round (one to fix the
capped-diagnostic tooling trap found mid-round). No crash observed across the whole session —
warps, equips, two direct-position-poke movers, a companion-search dead end, and the mirrored-
enemy tooling investigation. `TwinPositionSyncBudget`, `TwinPositionSyncSlot`, the `+0x134`
skip for Walk/Run, and every other round 61–63 write are byte-for-byte unchanged; this round's
net source change is `Odyssey.RebuildClientAppearance` (+ its 4 supporting consts/delegates),
`Odyssey.ReadAnimationName`/`ReadResRef` (+ supporting consts/delegates), the `gait-name`
diagnostic and its per-creature cap array, and the one call site in
`PhantomController.ApplyAppearance`.

---

## 12. Round 65 — the re-asserter found statically, fixed with a one-field write, and
live-verified at 0% idle-while-moving in both phantom directions

Method per this round's brief: find the re-asserter STATICALLY first (disassembly, not
another live guess), probe it live to confirm, then fix by satisfying the input the decider
actually reads. All three steps completed; the phantom case is fully closed, live-measured.

### 12.1 The caller table — VERIFIED (static disassembly + `tools/kotor1_0_3.db`)

`CSWCCreature::AIUpdate` (0x0061AD60) is the per-frame dispatcher every driven creature's
engine-side update passes through. Full disassembly this round (VERIFIED) shows it chooses
between exactly three animation-decision paths, and a directly-driven body (a phantom or a
mirrored enemy — no drive flag, no follow target) always lands on the third:

| caller | dispatch condition (from `AIUpdate`, 0x0061AD60) | what it does |
| --- | --- | --- |
| `CSWCCreature::DriveAnimationUpdate` (0x00615960) | `[creature+0x3A8]==1` (a "drive mode" flag — the local player's own PC, per `IsDriveAnimationBlocked`/`0x60F0F0` and `CGuiInGame::IsInDialog`/`0x62DBD0` gating it further) | derives the gait from real client-side movement; not reached by a network twin |
| `CSWCCreature::FollowAnimationUpdate` (0x00615E80) | `[creature+0x3A4]` (a follow-target pointer) resolves to a valid leader via `CSWSCreature::GetCurrentlyFollowingLeader` (0x4F7750) | derives the gait from the followed leader's own state; not reached by a phantom (no follow target) |
| `CSWCCreature::AnimationUpdate` (0x0061A980) | the default, taken whenever neither of the above applies — **exactly a phantom or a mirrored enemy** | **the re-asserter** — see §12.2 |

(`tools/q.py func '' CSWCCreature`, cross-checked address-by-address against a live
disassembly of `AIUpdate`'s own dispatch logic at 0x61AF04-0x61AFDD.)

### 12.2 `CSWCCreature::AnimationUpdate` (0x0061A980), the re-asserter — VERIFIED

Full disassembly this round. The load-bearing block, 0x61AA27-0x61AA52:

```
0x61AA27  xor edx,edx
0x61AA29  mov dx,[esi+0x48]        ; esi = this (the twin); +0x48 = TwinCurrentAnimation,
                                    ;   the RENDERED field every prior round has sampled
0x61AA30  call AnimationStationary(id)     ; 0x0063C170
0x61AA35  test eax,eax
0x61AA37  jne 0x61aa52             ; id IS stationary -> skip the block below entirely
0x61AA39  call GetLoopingAnimation()       ; 0x0063C100, ecx=esi, NO stack args
0x61AA40  mov edx,[esi]            ; esi's own CSWCObject vtable
0x61AA44  movzx eax,ax             ; eax = GetLoopingAnimation()'s return, low 16 bits
0x61AA49  push eax
0x61AA4C  call [edx+0x9c]          ; CSWCObject::SetAnimation (0x0063E930, round 61 sec8.1's
                                    ;   "the plain setter"), called with id=that return value
```

`GetLoopingAnimation` (0x0063C100) is a one-line getter, VERIFIED:

```
0x0063C100  mov ax,[ecx+0x58]
0x0063C104  ret
```

`tools/kotor1_0_3.db`'s own offset table names `CSWCObject+0x058` **`looping_animation`**
(ushort) — not inferred, read directly from the symbol db. `AnimationUpdate` reads this field
and nothing else to decide what to reassert. Xref of `GetLoopingAnimation`'s 8 call sites
(`tools/xrefs.py 0x63C100`, VERIFIED) confirms the other 7 (`SetAppearance`,
`EquipToInventorySlot`, `AnimateFireAndForget`, two network handlers, and the identical
`AnimationUpdate` sibling on `CSWCPlaceable`/`CSWCDoor`) are one-shot appearance/equip/network
paths, not a second per-frame decider — this is the only per-frame consumer.

**The mechanism, put together**: every `AIUpdate` tick that reaches this default path,
whenever `twin+0x48` is not currently `AnimationStationary` (i.e. it is showing a real
walk/run id — the rare case where `DriveBody`'s own `ClientServerSyncHandler_Position`
(`+0x12C`, round 61's fix) or `SetAnimationInternal` (round 63 sec10.2) actually landed one),
`AnimationUpdate` unconditionally calls `SetAnimation(twin, looping_animation, 0, 0)`.
`SetAnimation` (0x0063E930, already disassembled round 61 sec8.1) gates on
`AnimationLooping(id)` and, on a pass, overwrites `twin+0x48`/`twin+0x58` together; on a
fail, "bail, twin+0x48/+0x58 untouched." `animations.2da` (VERIFIED,
`tools/kotorfiles.py 2da animations.2da`):

| row | name | looping |
| --- | --- | --- |
| 6 | pause1 (idle, `0x2710`) | **1** |
| 338 | walkSS (`0x2712` for weapon class 2) | **0** |
| 345 | runSS (`0x2714` for weapon class 2) | **0** |

Nothing in this codebase (or, per round 61 sec8.1's own disassembly of the ONE call that ever
touched `+0x134` for these ids, anything reachable at all before this round) ever wrote
`looping_animation` to a gait id — the `+0x134` edge-triggered call was proven dead for
Walk/Run in round 63 sec10.4 and is skipped for them. So `looping_animation` always held a
stale idle id. The reassertion is therefore `SetAnimation(twin, 0x2710, 0, 0)` —
`AnimationLooping(0x2710)` passes (row 6, `Looping=1`) — and the write proceeds, stomping the
correct walk/run render back to idle. This is precisely the "something re-decides idle every
frame after our request" the brief predicted, mechanistically closed: not a movement/velocity
field, an animation-id CACHE that nothing had ever pointed at the right value.

### 12.3 The live probe — VERIFIED

`Odyssey.DriveBody`'s existing capped diagnostics (rounds 60-64) already established that
`twin+0x48` briefly shows a real walk/run/resolved-name id after a successful `+0x12C` call,
then reverts almost immediately — consistent with a per-frame reassertion running right after.
This round's fix (§12.4) IS the live probe: rather than instrumenting the read side further
(which six prior rounds already did exhaustively), the fastest confirmation was to write the
predicted fix and measure whether the predicted mechanism (a structural
`AnimationLooping` rejection once `looping_animation` holds a non-looping id) actually holds —
see §12.5's live fractions, which move from the baseline's 91-99% idle-while-moving to 0%,
exactly as the static analysis predicted, with the SAME `+0x12C`/rate/position code left
byte-for-byte unchanged from round 63.

### 12.4 The fix

`src/KotorCoop.Plugin/Odyssey.cs`: a new constant `TwinLoopingAnimation = 0x58`, and, in
`DriveBody`, inside the `twin != 0 && *(nint*)twin == ClientCreatureVtable` block (immediately
before the existing `TwinPositionSyncSlot` call), one line:

```csharp
*(ushort*)(twin + TwinLoopingAnimation) = (ushort)want;
```

Every step, not gated on `GaitChanged` — the same "every step, not gated" discipline
`TwinPositionSyncSlot` already needed (round 61 sec8.5: edge-triggering measurably
underperformed). `want` is `DriveBody`'s own already-computed gait id (`GaitRule.AnimIdle`/
`AnimWalk`/`AnimRun`), so this costs one `ushort` memory write per driven body per frame — no
new engine call, no new indirection, nothing to guard beyond the existing `twin != 0` and
vtable checks already in place for every other write in this block.

**Why this doesn't just re-break in reverse**: writing `looping_animation` to `0x2710` (idle)
when `want==Idle` is exactly what the existing, previously-live-verified idle path already
produced (via the kept `+0x134` call), so idle behaviour is unchanged. Writing it to
`0x2712`/`0x2714` when moving means `AnimationUpdate`'s reassertion call becomes
`SetAnimation(twin, walkSS/runSS, ...)`, which `AnimationLooping` rejects structurally (same
table as §12.2) — the call bails without touching `twin+0x48`, so whatever `+0x12C`/
`SetAnimationInternal` most recently painted there survives untouched. The reassertion does
not stop firing; it just stops being able to do anything, which is exactly the "no-op instead
of a fresh poke" a purely additive fix should aim for — no removed call, no changed call
order, one new memory write.

### 12.5 Live measurement — VERIFIED, both phantom directions clean

Rig: `tools/demo.ps1` cold start (`END_M01AA` autosave), both GOG installs rebuilt/redeployed
with the fix (`src/KotorCoop.Plugin/build.ps1 -Install`, both `-GameDir`s, clean). Mover: a
direct write to the engine player's position via `SharedBlock.OffsetEnginePlayerPointer`
(round 60 sec7.1's non-scan address source), 1.6 u/s walk for 10s then 3.96 u/s run for 10s,
150ms step. Sampler: an explicit-address variant of `tools/phantomgait.ps1` reading the same
fields plus the new `twin+0x58`, 100ms cadence, addresses taken from the plugin's own
`"spawned phantom in slot 0: id 0x... at 0xADDR"` log line (never a vtable scan, per round 60
sec7.1's own trap finding).

| case | moving samples (speed>0.3) | idle-while-moving | fraction |
| --- | --- | --- | --- |
| host→client phantom | 132 | 0 | **0%** |
| client→host phantom | 122 | 0 | **0%** |
| mirrored enemy | not independently measured — see §12.6 | — | — |
| companion | not measured — this save's roster carries no companion (0 party-presence entries, matches rounds 63-64) | — | — |

Both measured cases show `anim`/`twin`/`loop` moving in lockstep on every single sampled
frame throughout both the walk and run phases (`twin+0x58` tracking the requested id exactly
as written, `twin+0x48` tracking `twin+0x58`) — not a single idle-while-moving sample in
either 20-second run, against a round 62-64 baseline of 91.1%-99.4% on the same test shape.
Full transcripts: this pass's scratch `gaitsample65.ps1` output, CSV export in the session's
scratch dir (`r65-h2c.csv`, 200 rows; `r65-c2h.csv`, 198 rows).

### 12.6 Mirrored enemy and companion — attempted live, blocked by this save/module, not by
the fix

A host-only `console 'warp tar_m10aa'` (party-reconciliation.md's established recipe) put
both instances in the Black Vulkar pool room round 55/62/64 used, with the client's own enemy
census confirming active mirroring (`"enemy drive: mirroring 25 of the owner's 25 enemies"`).
A `[W_SAVE] different save` warning appeared once immediately after the transition but proved
transient — the census count and subsequent samples show mirroring was live throughout,
contradicting the warning's own text.

The enemy address was resolved without a raw vtable scan, per this round's own instruction:
a scan for the `CSWSCreature` vtable (`0x007470C8`) matched against the target id (`0x33`)
returned exactly one candidate, cross-checked against the position already known from the
client's own `EnemyPresenceSlots` table (`SharedBlock.OffsetEnemyPresenceSlots`, agent-fed
ground truth, not a scan) — the two agreed exactly, disambiguating the round 60 sec7.1 "frozen
duplicate" trap the way sec7.5 suggested for a future pass (cross-check a scan hit against a
second live-only field) rather than trusting the first hit blind.

With that address confirmed, the host was moved point-blank to the pool (native `tp` cheat,
`ConsoleVerbCatalog`'s `tp <x> <y> [z]`) to trigger genuine engine perception/aggro — it did,
immediately and repeatedly (`"combat ground-truth: local PC took N damage, attacker=0x33"`
and six other distinct attacker ids, real forwarded hits, matching round 55's own recipe
exactly). The host was then retreated, twice, once by a direct position write and once by the
native `tp` verb, to try to provoke a real chase (the thing this section needs: genuine
AI-driven locomotion to sample). **Neither retreat produced any movement**: re-reading the
full 25-entry enemy presence table before and after several minutes of live combat shows every
single entry's position byte-for-byte unchanged from the first read, including `0x33`, despite
it (and five others) having visibly, repeatedly hit the host during that window. This specific
pack are stationary point-defence guards around the pool — they attack whatever is in range
without pursuing — not patrollers, and this save/location has no other hostile or companion to
substitute (`OffsetPartyPresenceCount` read `0`, matching rounds 63-64's own finding that this
save's roster is PC-only).

This is the same category of gap round 64 sec11.5's E4 left open: this codebase's test-drive
surface exposes no way to force an arbitrary NPC to walk a path, and building one was judged
out of scope for a locomotion-focused pass, same call round 64 made. **The fix itself is not
in question for these two cases** — `EnemyPresenceController.DriveOne` and
`PartyPresenceController`'s companion drive both call the exact same `Odyssey.DriveEnemy` ->
`DriveBody` this round changed (confirmed by reading both call sites this round,
`src/KotorCoop.Plugin/EnemyPresenceController.cs:1297`,
`src/KotorCoop.Plugin/PartyPresenceController.cs:434`), and the re-asserter this round found
(`CSWCCreature::AnimationUpdate`) is a property of the RENDERING side — it does not
distinguish a phantom's twin from a mirrored enemy's twin; both are plain `CSWCCreature`
objects reached through the identical default `AIUpdate` dispatch path (§12.1). The only thing
missing is a live sample of a genuinely-moving instance of each, which this round's save could
not supply.

### 12.7 What remains (NEEDS-LIVE, not closed this round)

- **Mirrored enemy and companion, independently sampled.** Needs either a save/module with a
  patrolling (not stationary-guard) hostile, or a small, explicit-target-id addition to
  `TestDriveController`/`SharedBlock` so an arbitrary NPC can be driven along a path for
  testing — round 64 sec11.5/sec10.7 (E4) already scoped this same gap and it remains
  unaddressed by design (out of scope for a locomotion pass).
- **The `+0x134` call's remaining purpose.** Round 63 sec10.4 proved it dead for Walk/Run and
  kept it only for Idle and `DriveEnemy`'s owner-animation (combat-overlay) branch. This
  round's fix makes `looping_animation` a second, independent writer of the same idle
  steady-state the `+0x134` call already produces; whether the `+0x134` call is now fully
  redundant for Idle too (i.e. could be dropped entirely, leaving only combat-overlay ids on
  that path) was not investigated — low priority given it is proven harmless, not proven
  necessary either way.
- **The tooling gap** (round 60 sec7.1, reconfirmed working-around-able every round since) is
  still open at the source; this round again worked around it by hand
  (`SharedBlock.OffsetEnginePlayerPointer` for the mover, the plugin's own spawn-log line for
  the phantom, a scan-then-cross-check-against-presence-table for the enemy) rather than fixing
  `phantomgait.ps1`'s auto-resolve or `teleport.ps1`.

### 12.8 What did NOT regress

`dotnet test tests/KotorCoop.Tests` stayed at **1591/1591** passing, run in full before this
round's change and again after, on the working tree with the fix applied. Both plugin builds
(`src/KotorCoop.Plugin/build.ps1 -Install`) clean for both GOG installs `tools/demo.ps1` uses.
No crash observed across the round's rebuild/redeploy/live-test cycle, a module transition, a
sustained multi-attacker combat encounter at 1 HP (the plugin's own min-1-hp clamp held, per
`docs/research/live-verification.md`'s own documented behaviour — no death), and two direction
reversals of the mover. Every write round 61-64 shipped (`TwinPositionSyncBudget`,
`TwinPositionSyncSlot`, the `+0x134` skip for Walk/Run, `RebuildClientAppearance`, the
`gait-class`/`gait-name`/`sync12c` diagnostics) is untouched — this round's net source change
is the single `TwinLoopingAnimation` constant and its one write site in `DriveBody`.

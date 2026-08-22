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

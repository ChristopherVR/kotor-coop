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

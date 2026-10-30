using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The gait DECISION (idle/walk/run + when to reissue), extracted from Odyssey.DrivePhantom/
/// DriveEnemy so the threshold that used to be an untested inline "speed &gt; 2.0f" in two
/// places has one tested definition. See docs/research/phantom-locomotion.md.
///
/// ROUND 68 (phantom-locomotion.md §14) added a two-threshold walk/run hysteresis band and an
/// idle-dwell for low speed, replacing the bare "speed &gt; 2.0f" §13 flagged as a possible
/// flicker source under measurement jitter. Both are covered here.
/// </summary>
public class GaitRuleTests
{
    [Theory]
    [InlineData(0f, false, GaitRule.AnimIdle, GaitRule.AnimInjuredIdle)]
    [InlineData(1f, true, GaitRule.AnimWalk, GaitRule.AnimInjuredWalk)]
    [InlineData(4f, true, GaitRule.AnimRun, GaitRule.AnimInjuredRun)]
    public void InjuryAndHealingChangeThePoseWithoutChangingSpeed(float speed, bool moving, int healthy, int injured)
    {
        int last = GaitRule.SelectAnimation(speed, moving);
        Assert.Equal(healthy, last);
        foreach (bool hurt in new[] { true, false })
        {
            int want = GaitRule.ApplyInjury(GaitRule.SelectAnimation(speed, moving), hurt);
            Assert.Equal(hurt ? injured : healthy, want);
            Assert.True(GaitRule.ShouldReissue(last, want));
            last = want;
            // Holding the same health state must leave one-shots free to finish.
            Assert.False(GaitRule.ShouldReissue(last,
                GaitRule.ApplyInjury(GaitRule.SelectAnimation(speed, moving), hurt)));
        }
    }

    [Fact]
    public void InjuredRunKeepsHysteresisThroughHealingInTheSpeedDeadBand()
    {
        int last = GaitRule.AnimInjuredRun;
        foreach (bool hurt in new[] { true, false, true })
        {
            int gait = GaitRule.SelectAnimation(2f, true, GaitRule.IsRunningAnimation(last));
            last = GaitRule.ApplyInjury(gait, hurt);
            Assert.Equal(hurt ? GaitRule.AnimInjuredRun : GaitRule.AnimRun, last);
        }
        int slow = GaitRule.SelectAnimation(1f, true, GaitRule.IsRunningAnimation(last));
        Assert.Equal(GaitRule.AnimInjuredWalk, GaitRule.ApplyInjury(slow, true));
    }

    [Fact]
    public void InjuryDoesNotReplaceAuthoredAnimations()
    {
        Assert.Equal(0x2716, GaitRule.ApplyInjury(0x2716, true)); // death
        Assert.Equal(0x27AE, GaitRule.ApplyInjury(0x27AE, true)); // flourish
    }

    [Fact]
    public void NotMovingIsAlwaysIdleRegardlessOfSpeed()
    {
        Assert.Equal(GaitRule.AnimIdle, GaitRule.SelectAnimation(0f, moving: false));

        // A stale high speed reading (a chase-speed leftover, or a bad sample) must not leak
        // a run/walk id onto a body the caller has already decided is not moving.
        Assert.Equal(GaitRule.AnimIdle, GaitRule.SelectAnimation(50f, moving: false));
    }

    [Fact]
    public void MovingBelowRunEnterThresholdWithNoHistoryIsWalk()
    {
        // wasRunning defaults to false: with no memory of a previous decision, the body needs
        // to CROSS the enter threshold, not merely equal it, to read as a run.
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(0f, moving: true));
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(1.6f, moving: true));
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(GaitRule.RunEnterThreshold, moving: true));
    }

    [Fact]
    public void MovingAboveRunEnterThresholdWithNoHistoryIsRun()
    {
        Assert.Equal(GaitRule.AnimRun, GaitRule.SelectAnimation(GaitRule.RunEnterThreshold + 0.01f, moving: true));
        Assert.Equal(GaitRule.AnimRun, GaitRule.SelectAnimation(3.96f, moving: true));
        Assert.Equal(GaitRule.AnimRun, GaitRule.SelectAnimation(12f, moving: true));
    }

    [Fact]
    public void ExactlyAtTheEnterThresholdIsAWalkNotARun()
    {
        // Documents which side of the boundary wins entering Run: "> threshold", not ">=".
        Assert.False(GaitRule.IsRun(GaitRule.RunEnterThreshold, wasRunning: false));
        Assert.True(GaitRule.IsRun(GaitRule.RunEnterThreshold + 0.001f, wasRunning: false));
    }

    [Fact]
    public void ExactlyAtTheExitThresholdStaysARunNotAWalk()
    {
        // The exit side of the band is the mirror boundary: a running body needs to drop
        // STRICTLY UNDER the exit threshold to fall back to walk -- exactly at it, it is still
        // classified as running (">=", not ">").
        Assert.True(GaitRule.IsRun(GaitRule.RunExitThreshold, wasRunning: true));
        Assert.False(GaitRule.IsRun(GaitRule.RunExitThreshold - 0.001f, wasRunning: true));
    }

    [Fact]
    public void TheDeadBandBetweenTheTwoThresholdsKeepsWhicheverGaitWasAlreadyShowing()
    {
        // The old bare 2.0 u/s cutoff (docs/research/phantom-locomotion.md §13's flagged
        // risk) sits inside the new dead band (1.6 .. 2.4). A speed sampled there must not
        // flip the gait either way -- that is the entire point of hysteresis.
        const float oldBareThreshold = 2.0f;
        Assert.True(oldBareThreshold > GaitRule.RunExitThreshold && oldBareThreshold < GaitRule.RunEnterThreshold);

        Assert.False(GaitRule.IsRun(oldBareThreshold, wasRunning: false)); // already walking: stays walk
        Assert.True(GaitRule.IsRun(oldBareThreshold, wasRunning: true));   // already running: stays run
    }

    [Fact]
    public void HoveringNearTheOldBareThresholdNeverFlickersOnceAGaitIsEstablished()
    {
        // The concrete flicker scenario: a slow jog whose measured speed jitters between 1.8
        // and 2.2 -- straddling the old single 2.0 threshold every other sample. Under
        // hysteresis, once the gait settles it must not change again while the jitter stays
        // inside the dead band, no matter how many samples arrive.
        float[] jitter = { 2.0f, 1.8f, 2.2f, 1.9f, 2.1f, 1.8f, 2.2f, 2.0f, 1.9f, 2.1f };

        bool wasRunning = false; // starts walking
        int transitions = 0;
        int previousAnim = GaitRule.AnimWalk;

        foreach (float speed in jitter)
        {
            int anim = GaitRule.SelectAnimation(speed, moving: true, wasRunning);
            if (anim != previousAnim)
            {
                transitions++;
            }

            previousAnim = anim;
            wasRunning = GaitRule.IsRun(speed, wasRunning);
        }

        Assert.Equal(0, transitions);
        Assert.Equal(GaitRule.AnimWalk, previousAnim);
    }

    [Fact]
    public void ARealAccelerationStillCrossesBothThresholdsExactlyOnce()
    {
        // Hysteresis must not prevent a genuine speed-up/slow-down from registering -- only
        // suppress jitter inside the dead band. A monotonic ramp from a stop to a full sprint
        // and back down should show exactly one Walk->Run and one Run->Walk transition.
        float[] rampUpThenDown =
        {
            0f, 0.5f, 1.0f, 1.5f, 2.0f, 2.5f, 3.0f, 3.96f, 3.96f, 3.0f, 2.5f, 2.0f, 1.5f, 1.0f, 0.5f,
        };

        bool wasRunning = false;
        int walkToRun = 0;
        int runToWalk = 0;

        foreach (float speed in rampUpThenDown)
        {
            bool nowRunning = GaitRule.IsRun(speed, wasRunning);
            if (nowRunning && !wasRunning)
            {
                walkToRun++;
            }
            else if (!nowRunning && wasRunning)
            {
                runToWalk++;
            }

            wasRunning = nowRunning;
        }

        Assert.Equal(1, walkToRun);
        Assert.Equal(1, runToWalk);
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void ANonsenseSpeedWhileMovingStillWalksRatherThanRunning(float speed)
    {
        // A negative speed is never a run; NaN must not throw or silently select run through
        // a stray comparison — every relational operator on NaN is false, so IsRun(NaN) is
        // false and the result stays Walk, which is the safe substitute. True for a body with
        // no run history AND for one that was already (implausibly) classified as running.
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(speed, moving: true, wasRunning: false));
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(speed, moving: true, wasRunning: true));
    }

    [Fact]
    public void PositiveInfinityIsWellDefinedAsARun()
    {
        // Unlike NaN, IEEE 754 comparisons against +Infinity are well-defined (Infinity is
        // greater than any finite threshold), so this legitimately reads as a run rather
        // than needing special-casing — documented so a future change to IsRun's comparison
        // does not silently flip it without a test noticing.
        Assert.True(GaitRule.IsRun(float.PositiveInfinity));
        Assert.Equal(GaitRule.AnimRun, GaitRule.SelectAnimation(float.PositiveInfinity, moving: true));
    }

    // ---- idle-speed dwell (round 68) ---------------------------------------------------

    [Fact]
    public void AMovingBodyBelowIdleSpeedStaysWalkUntilTheDwellElapses()
    {
        Assert.Equal(
            GaitRule.AnimWalk,
            GaitRule.SelectAnimation(0.1f, moving: true, wasRunning: false, belowIdleSeconds: 0f));
        Assert.Equal(
            GaitRule.AnimWalk,
            GaitRule.SelectAnimation(
                0.1f, moving: true, wasRunning: false, belowIdleSeconds: GaitRule.IdleDwellSeconds - 0.01f));
    }

    [Fact]
    public void AMovingBodyBelowIdleSpeedDropsToIdleOnceTheDwellElapses()
    {
        Assert.Equal(
            GaitRule.AnimIdle,
            GaitRule.SelectAnimation(
                0.1f, moving: true, wasRunning: false, belowIdleSeconds: GaitRule.IdleDwellSeconds));
        Assert.Equal(
            GaitRule.AnimIdle,
            GaitRule.SelectAnimation(0.1f, moving: true, wasRunning: false, belowIdleSeconds: 1f));
    }

    [Fact]
    public void ADwellLongEnoughButASpeedAtOrAboveTheIdleThresholdNeverGoesIdle()
    {
        // The dwell only matters once the speed itself is under the threshold -- a long
        // accumulated value from an earlier slow patch must not condemn a body to idle the
        // instant it merely touches the threshold again.
        Assert.Equal(
            GaitRule.AnimWalk,
            GaitRule.SelectAnimation(
                GaitRule.IdleSpeedThreshold, moving: true, wasRunning: false, belowIdleSeconds: 5f));
    }

    [Fact]
    public void AdvanceBelowIdleAccumulatesWhileUnderThresholdAndResetsAboveIt()
    {
        float accum = 0f;
        accum = GaitRule.AdvanceBelowIdle(accum, speed: 0.1f, dt: 0.05f);
        Assert.Equal(0.05f, accum, precision: 4);

        accum = GaitRule.AdvanceBelowIdle(accum, speed: 0.1f, dt: 0.05f);
        Assert.Equal(0.10f, accum, precision: 4);

        // A single sample back above the threshold resets it completely, not decays it --
        // a real recovery, not more jitter.
        accum = GaitRule.AdvanceBelowIdle(accum, speed: 1.0f, dt: 0.05f);
        Assert.Equal(0f, accum);
    }

    [Fact]
    public void AdvanceBelowIdleNeverGoesNegativeOnABackwardsClock()
    {
        float accum = GaitRule.AdvanceBelowIdle(0.2f, speed: 0.1f, dt: -0.05f);
        Assert.True(accum >= 0.2f);
    }

    [Fact]
    public void ADwellDrivenIdleTransitionSimulatedStepByStepFiresExactlyOnce()
    {
        // End-to-end: a body decelerating under the idle-speed threshold and staying there,
        // stepped at PhantomController's 60 Hz cadence, should walk for a short stretch and
        // then settle into idle -- and never flicker back once it gets there.
        const float stepSeconds = 1f / 60f;
        float belowIdleAccum = 0f;
        int idleCount = 0;
        int walkCount = 0;
        int lastAnim = GaitRule.AnimWalk;
        int transitionsAfterFirstIdle = 0;
        bool sawIdle = false;

        for (int step = 0; step < 30; step++)
        {
            const float speed = 0.1f; // under IdleSpeedThreshold the whole time
            belowIdleAccum = GaitRule.AdvanceBelowIdle(belowIdleAccum, speed, stepSeconds);
            int anim = GaitRule.SelectAnimation(speed, moving: true, wasRunning: false, belowIdleAccum);

            if (anim == GaitRule.AnimIdle)
            {
                idleCount++;
                sawIdle = true;
            }
            else
            {
                walkCount++;
            }

            if (sawIdle && anim != lastAnim && anim != GaitRule.AnimIdle)
            {
                transitionsAfterFirstIdle++;
            }

            lastAnim = anim;
        }

        Assert.True(walkCount > 0); // the dwell window itself is still Walk
        Assert.True(idleCount > 0); // and it does eventually settle to Idle
        Assert.Equal(0, transitionsAfterFirstIdle); // never flickers back out of Idle
    }

    [Fact]
    public void ReissueOnlyFiresOnAChange()
    {
        Assert.False(GaitRule.ShouldReissue(GaitRule.AnimRun, GaitRule.AnimRun));
        Assert.True(GaitRule.ShouldReissue(GaitRule.AnimRun, GaitRule.AnimWalk));
        Assert.True(GaitRule.ShouldReissue(GaitRule.AnimIdle, GaitRule.AnimRun));
    }

    [Fact]
    public void ReissueFiresOnceFromNeverAsserted()
    {
        // 0 is not a valid animation id (docs/research/realtime-creature-movement.md §10:
        // idle/walk/run start at 0x2710), so "nothing asserted yet" naturally reissues the
        // first real gait without a separate sentinel.
        Assert.True(GaitRule.ShouldReissue(lastAsserted: 0, want: GaitRule.AnimIdle));
    }

    // ---- end-to-end: the exact scenario the user reported -----------------------------
    //
    // A peer runs in a straight line at 3.96 u/s (appearance row 114's run speed), publishing
    // a new position every ~33 ms (the agent's 30 Hz tick) while the phantom is driven every
    // ~16.7 ms (PhantomController's 60 Hz step) -- MotionSmoother.Step is PhantomController's
    // shared-algorithm sibling (the type doc: "PhantomController carries its own tuned copy")
    // and the two are exercised identically here. If gait selection ever regresses -- a
    // dropped hysteresis edge, a threshold that stops matching Odyssey.DriveBody's -- this is
    // where it would show up as Idle while genuinely, continuously moving: exactly "moves in
    // sync but slides, no run cycle."

    private const float RunSpeed = 3.96f;
    private const double NetworkTickSeconds = 1.0 / 30.0;
    private const double StepSeconds = 1.0 / 60.0;

    [Fact]
    public void ARunningPeerNeverReadsIdleOnceMotionIsEstablished()
    {
        MotionSmoother smoother = new(1, smoothingTauSeconds: 0.06f);
        double now = 0;
        double nextNetworkUpdate = NetworkTickSeconds;
        Vec3 networkPosition = default;

        bool sawIdleAfterWarmup = false;
        bool sawRun = false;
        int stepsAfterWarmup = 0;
        bool wasRunning = false;

        // Two seconds of continuous straight-line running, stepped at 60 Hz.
        for (int step = 0; step < 120; step++)
        {
            now += StepSeconds;

            if (now >= nextNetworkUpdate)
            {
                networkPosition = new Vec3 { X = (float)(RunSpeed * now), Y = 0f, Z = 0f };
                nextNetworkUpdate += NetworkTickSeconds;
            }

            (_, float speed, bool moving) = smoother.Step(0, networkPosition, now);
            int anim = GaitRule.SelectAnimation(speed, moving, wasRunning);
            wasRunning = GaitRule.IsRun(speed, wasRunning);

            // Skip the first few steps: the very first sample has no prior displacement to
            // measure a speed from (MotionSmoother's own "first movement never has a dt"
            // case), so a brief Walk before the estimate settles is expected and harmless --
            // PhantomController's own comment on _peerSpeed documents the same warmup.
            if (step < 6)
            {
                continue;
            }

            stepsAfterWarmup++;
            if (anim == GaitRule.AnimIdle)
            {
                sawIdleAfterWarmup = true;
            }

            if (anim == GaitRule.AnimRun)
            {
                sawRun = true;
            }
        }

        Assert.True(stepsAfterWarmup > 0);
        Assert.False(sawIdleAfterWarmup);
        Assert.True(sawRun);
    }

    [Fact]
    public void AStoppedPeerSettlesBackToIdleRatherThanStayingParkedInRun()
    {
        MotionSmoother smoother = new(1, smoothingTauSeconds: 0.06f);
        double now = 0;

        // Run for half a second, then stop dead.
        for (int step = 0; step < 30; step++)
        {
            now += StepSeconds;
            Vec3 target = new() { X = (float)(RunSpeed * now), Y = 0f, Z = 0f };
            smoother.Step(0, target, now);
        }

        Vec3 stoppedAt = new() { X = (float)(RunSpeed * now), Y = 0f, Z = 0f };

        int lastAnim = GaitRule.AnimRun;
        bool wasRunning = true;
        for (int step = 0; step < 60; step++)
        {
            now += StepSeconds;
            (_, float speed, bool moving) = smoother.Step(0, stoppedAt, now);
            lastAnim = GaitRule.SelectAnimation(speed, moving, wasRunning);
            wasRunning = GaitRule.IsRun(speed, wasRunning);
        }

        Assert.Equal(GaitRule.AnimIdle, lastAnim);
    }
}

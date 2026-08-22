using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The gait DECISION (idle/walk/run + when to reissue), extracted from Odyssey.DrivePhantom/
/// DriveEnemy so the threshold that used to be an untested inline "speed &gt; 2.0f" in two
/// places has one tested definition. See docs/research/phantom-locomotion.md.
/// </summary>
public class GaitRuleTests
{
    [Fact]
    public void NotMovingIsAlwaysIdleRegardlessOfSpeed()
    {
        Assert.Equal(GaitRule.AnimIdle, GaitRule.SelectAnimation(0f, moving: false));

        // A stale high speed reading (a chase-speed leftover, or a bad sample) must not leak
        // a run/walk id onto a body the caller has already decided is not moving.
        Assert.Equal(GaitRule.AnimIdle, GaitRule.SelectAnimation(50f, moving: false));
    }

    [Fact]
    public void MovingBelowThresholdIsWalk()
    {
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(0f, moving: true));
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(1.6f, moving: true));
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(GaitRule.RunSpeedThreshold, moving: true));
    }

    [Fact]
    public void MovingAboveThresholdIsRun()
    {
        Assert.Equal(GaitRule.AnimRun, GaitRule.SelectAnimation(2.01f, moving: true));
        Assert.Equal(GaitRule.AnimRun, GaitRule.SelectAnimation(3.96f, moving: true));
        Assert.Equal(GaitRule.AnimRun, GaitRule.SelectAnimation(12f, moving: true));
    }

    [Fact]
    public void ExactlyAtTheThresholdIsAWalkNotARun()
    {
        // Documents which side of the boundary wins: "> threshold", not ">=".
        Assert.False(GaitRule.IsRun(GaitRule.RunSpeedThreshold));
        Assert.True(GaitRule.IsRun(GaitRule.RunSpeedThreshold + 0.001f));
    }

    [Theory]
    [InlineData(-1f)]
    [InlineData(float.NaN)]
    public void ANonsenseSpeedWhileMovingStillWalksRatherThanRunning(float speed)
    {
        // A negative speed is never a run; NaN must not throw or silently select run through
        // a stray comparison — every relational operator on NaN is false, so IsRun(NaN) is
        // false and the result stays Walk, which is the safe substitute.
        Assert.Equal(GaitRule.AnimWalk, GaitRule.SelectAnimation(speed, moving: true));
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
            int anim = GaitRule.SelectAnimation(speed, moving);

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
        for (int step = 0; step < 60; step++)
        {
            now += StepSeconds;
            (_, float speed, bool moving) = smoother.Step(0, stoppedAt, now);
            lastAnim = GaitRule.SelectAnimation(speed, moving);
        }

        Assert.Equal(GaitRule.AnimIdle, lastAnim);
    }
}

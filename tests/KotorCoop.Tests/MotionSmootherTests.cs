using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The measure-smooth-decide arithmetic behind every mirrored body (phantoms carry a tuned
/// copy of the same logic). The cap cases are the "flying" bug: an exponential chase with a
/// short tau closes a stall's gap at gap/tau — tens of units per second — unless bounded.
/// </summary>
public class MotionSmootherTests
{
    private const float Tau = 0.06f;
    private const double FrameDt = 1.0 / 60.0;

    private static Vec3 At(float x, float y = 0f) => new() { X = x, Y = y, Z = 0f };

    [Fact]
    public void SteadyTrackingSettlesAtTheStreamsOwnSpeed()
    {
        MotionSmoother smoother = new(1, Tau);
        double now = 0;

        // A peer running at 3.96 u/s, updates every 33 ms, stepped at 60 Hz.
        Vec3 previousShown = default;
        float worstStepSpeed = 0f;
        for (int update = 0; update < 60; update++)
        {
            Vec3 target = At(3.96f * update * 0.033f);
            for (int frame = 0; frame < 2; frame++)
            {
                now += FrameDt;
                (Vec3 shown, _, bool moving) = smoother.Step(0, target, now);

                // Skip the priming step, where shown snaps to the first target.
                if (update > 2)
                {
                    float step = shown.X - previousShown.X;
                    worstStepSpeed = MathF.Max(worstStepSpeed, step / (float)FrameDt);
                    Assert.True(moving);
                }

                previousShown = shown;
            }
        }

        // The chase never needs to exceed anything near the cap while simply following.
        Assert.InRange(worstStepSpeed, 1f, 8f);

        // And it trails truth by well under a unit at this speed and tau.
        Assert.InRange(3.96f * 59 * 0.033f - previousShown.X, 0f, 1f);
    }

    [Fact]
    public void AStallsBacklogIsChasedNoFasterThanTheCap()
    {
        MotionSmoother smoother = new(1, Tau);
        double now = FrameDt;
        smoother.Step(0, At(0f), now);

        // The stream resumes 6 units ahead — under the 8-unit snap, so it must glide.
        Vec3 target = At(6f);
        Vec3 previousShown = At(0f);
        for (int frame = 0; frame < 120; frame++)
        {
            now += FrameDt;
            (Vec3 shown, _, _) = smoother.Step(0, target, now);

            float stepSpeed = (shown.X - previousShown.X) / (float)FrameDt;
            Assert.InRange(stepSpeed, 0f, 12.01f);
            previousShown = shown;
        }

        // Capped is not stuck: two seconds later the backlog is fully paid off.
        Assert.InRange(previousShown.X, 5.99f, 6.01f);
    }

    [Fact]
    public void AGapPastTheSnapThresholdCutsInsteadOfGliding()
    {
        MotionSmoother smoother = new(1, Tau);
        double now = FrameDt;
        smoother.Step(0, At(0f), now);

        (Vec3 shown, _, _) = smoother.Step(0, At(10f), now + FrameDt);

        Assert.Equal(10f, shown.X);
    }

    [Fact]
    public void TheGaitOutlivesJitterButNotAGenuineStop()
    {
        MotionSmoother smoother = new(1, Tau);
        double now = FrameDt;
        smoother.Step(0, At(0f), now);

        now += FrameDt;
        (_, _, bool movingWhileMoving) = smoother.Step(0, At(0.5f), now);
        Assert.True(movingWhileMoving);

        // 200 ms with no stream movement — a late packet, not a stop: the gait holds.
        now += 0.2;
        (_, _, bool movingAcrossGap) = smoother.Step(0, At(0.5f), now);
        Assert.True(movingAcrossGap);

        // Genuinely still for longer than the hysteresis window: the gait drops.
        now += 0.4;
        (_, _, bool movingAfterStop) = smoother.Step(0, At(0.5f), now);
        Assert.False(movingAfterStop);
    }
}

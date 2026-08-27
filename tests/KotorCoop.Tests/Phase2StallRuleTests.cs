using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class Phase2StallRuleTests
{
    private const double StallThresholdSeconds = 5.0;
    private const float KickRangeSquared = 3.0f * 3.0f;

    [Fact]
    public void ShouldKick_WrongPhase_NeverFires()
    {
        Assert.False(Phase2StallRule.ShouldKick(
            phase: 7,
            phase2StallSeconds: 100.0,
            leaderDistanceSquared: 100.0f,
            stallThresholdSeconds: StallThresholdSeconds,
            kickRangeSquared: KickRangeSquared));
    }

    [Fact]
    public void ShouldKick_RightPhase_NotStalledLongEnough_DoesNotFire()
    {
        Assert.False(Phase2StallRule.ShouldKick(
            phase: Phase2StallRule.MovingToPathPhase,
            phase2StallSeconds: StallThresholdSeconds - 0.01,
            leaderDistanceSquared: 100.0f,
            stallThresholdSeconds: StallThresholdSeconds,
            kickRangeSquared: KickRangeSquared));
    }

    [Fact]
    public void ShouldKick_RightPhase_StalledLongEnough_ButWithinKickRange_DoesNotFire()
    {
        Assert.False(Phase2StallRule.ShouldKick(
            phase: Phase2StallRule.MovingToPathPhase,
            phase2StallSeconds: StallThresholdSeconds + 10.0,
            leaderDistanceSquared: KickRangeSquared - 0.01f,
            stallThresholdSeconds: StallThresholdSeconds,
            kickRangeSquared: KickRangeSquared));
    }

    [Fact]
    public void ShouldKick_RightPhase_StalledLongEnough_AndFarAway_Fires()
    {
        Assert.True(Phase2StallRule.ShouldKick(
            phase: Phase2StallRule.MovingToPathPhase,
            phase2StallSeconds: StallThresholdSeconds + 10.0,
            leaderDistanceSquared: KickRangeSquared + 0.01f,
            stallThresholdSeconds: StallThresholdSeconds,
            kickRangeSquared: KickRangeSquared));
    }

    [Fact]
    public void ShouldKick_ExactlyAtStallThreshold_Fires()
    {
        // The rule uses >= for the stall-duration comparison — reaching the threshold exactly is
        // enough, matching the doc comment ("phase2StallSeconds < stallThresholdSeconds" rejects,
        // so equality falls through to the distance check).
        Assert.True(Phase2StallRule.ShouldKick(
            phase: Phase2StallRule.MovingToPathPhase,
            phase2StallSeconds: StallThresholdSeconds,
            leaderDistanceSquared: KickRangeSquared + 1.0f,
            stallThresholdSeconds: StallThresholdSeconds,
            kickRangeSquared: KickRangeSquared));
    }

    [Fact]
    public void ShouldKick_ExactlyAtKickRange_DoesNotFire()
    {
        // Distance test is strict ">", matching FollowKickRangeSquared's own "close enough to
        // its leader that standing still is just caught up" semantics elsewhere in this
        // controller (an exact-equal distance is still "caught up," not "far away").
        Assert.False(Phase2StallRule.ShouldKick(
            phase: Phase2StallRule.MovingToPathPhase,
            phase2StallSeconds: StallThresholdSeconds + 10.0,
            leaderDistanceSquared: KickRangeSquared,
            stallThresholdSeconds: StallThresholdSeconds,
            kickRangeSquared: KickRangeSquared));
    }

    [Fact]
    public void ShouldKick_ZeroStallSeconds_NeverFires_RegardlessOfDistance()
    {
        Assert.False(Phase2StallRule.ShouldKick(
            phase: Phase2StallRule.MovingToPathPhase,
            phase2StallSeconds: 0.0,
            leaderDistanceSquared: 10000.0f,
            stallThresholdSeconds: StallThresholdSeconds,
            kickRangeSquared: KickRangeSquared));
    }

    [Fact]
    public void MovingToPathPhase_And_TeleportForwardPhase_AreDistinctConstants()
    {
        Assert.Equal(2, Phase2StallRule.MovingToPathPhase);
        Assert.Equal(10, Phase2StallRule.TeleportForwardPhase);
    }
}

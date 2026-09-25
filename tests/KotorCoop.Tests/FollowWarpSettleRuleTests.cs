using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class FollowWarpSettleRuleTests
{
    [Fact]
    public void TheDantooineToHawkCrashTimingIsHeld()
    {
        // 2026-09-25: the second follow landed 0.8 s after world-up, before the body was built.
        Assert.False(FollowWarpSettleRule.MayStart(hasRenderedBody: false, worldUpAtMs: 10_000, nowMs: 10_800));
    }

    [Fact]
    public void ABuiltBodyStillWaitsForTheMinimumSettle()
    {
        Assert.False(FollowWarpSettleRule.MayStart(true, 10_000, 10_000 + FollowWarpSettleRule.MinSettleMs - 1));
        Assert.True(FollowWarpSettleRule.MayStart(true, 10_000, 10_000 + FollowWarpSettleRule.MinSettleMs));
    }

    [Fact]
    public void AWorldThatNeverBuildsABodyIsReleasedAtTheCeiling()
    {
        Assert.False(FollowWarpSettleRule.MayStart(false, 10_000, 10_000 + FollowWarpSettleRule.MaxWaitMs - 1));
        Assert.True(FollowWarpSettleRule.MayStart(false, 10_000, 10_000 + FollowWarpSettleRule.MaxWaitMs));
    }

    [Fact]
    public void NoRecordedWorldUpNeverHolds()
    {
        Assert.True(FollowWarpSettleRule.MayStart(false, 0, 5));
    }
}

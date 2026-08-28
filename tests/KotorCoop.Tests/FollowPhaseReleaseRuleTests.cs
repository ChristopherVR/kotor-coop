using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class FollowPhaseReleaseRuleTests
{
    [Fact]
    public void ShouldRelease_AlreadyAtEngineResetSentinel_DoesNotFire()
    {
        Assert.False(FollowPhaseReleaseRule.ShouldRelease(FollowPhaseReleaseRule.EngineWatchdogResetPhase));
    }

    [Theory]
    [InlineData(0)]  // FindPath
    [InlineData(1)]  // Offset
    [InlineData(2)]  // Moving_To_Path
    [InlineData(3)]  // Following_Path
    [InlineData(4)]  // Moving_To_Offset
    [InlineData(5)]  // WaitingForPath
    [InlineData(6)]  // OffsetWaitDest
    [InlineData(7)]  // PathWaitDest
    [InlineData(8)]  // ToOffsetWaitDest
    [InlineData(9)]  // ToPathWaitDest
    [InlineData(10)] // TeleportForward
    public void ShouldRelease_AnyDispatchedPhase_Fires(int phase)
    {
        Assert.True(FollowPhaseReleaseRule.ShouldRelease(phase));
    }

    [Fact]
    public void ShouldRelease_UnexpectedGarbagePhase_StillFires()
    {
        // A frozen record could in principle hold anything other than -1; the rule only ever
        // special-cases the one sentinel value it is safe not to rewrite.
        Assert.True(FollowPhaseReleaseRule.ShouldRelease(int.MaxValue));
        Assert.True(FollowPhaseReleaseRule.ShouldRelease(-2));
    }

    [Fact]
    public void EngineWatchdogResetPhase_IsNegativeOne()
    {
        // The exact sentinel WalkUpdateLocation_QuickWalk_FollowLeader's own 50-attempt watchdog
        // writes (0x0051C964) and TryResetStuckFollowPhase already recognizes.
        Assert.Equal(-1, FollowPhaseReleaseRule.EngineWatchdogResetPhase);
    }
}

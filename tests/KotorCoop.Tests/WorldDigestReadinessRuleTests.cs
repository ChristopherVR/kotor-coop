using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class WorldDigestReadinessRuleTests
{
    [Fact]
    public void InitialReadableSampleEstablishesBaselineAndCanBeReady()
    {
        (WorldDigestReadinessState next, bool ready) = WorldDigestReadinessRule.Observe(
            WorldDigestReadinessState.Initial, loadSerial: 5, localReadSucceeded: true);

        Assert.True(ready);
        Assert.Equal(new WorldDigestReadinessState(true, 5), next);
    }

    [Fact]
    public void ChangedSerialWithReadableOldRowIsSuppressedOnceThenReady()
    {
        WorldDigestReadinessState baseline = new(true, 5);

        (WorldDigestReadinessState next, bool ready) = WorldDigestReadinessRule.Observe(
            baseline, loadSerial: 6, localReadSucceeded: true);
        Assert.False(ready);
        Assert.Equal(new WorldDigestReadinessState(true, 6), next);

        (WorldDigestReadinessState after, ready) = WorldDigestReadinessRule.Observe(
            next, loadSerial: 6, localReadSucceeded: true);
        Assert.True(ready);
        Assert.Equal(next, after);
    }

    [Fact]
    public void UnreadableSamplesRemainUnreadyAfterSerialEdgeUntilAReadSucceeds()
    {
        (WorldDigestReadinessState next, bool ready) = WorldDigestReadinessRule.Observe(
            new WorldDigestReadinessState(true, 5), loadSerial: 6, localReadSucceeded: false);
        Assert.False(ready);

        (WorldDigestReadinessState afterFailure, ready) = WorldDigestReadinessRule.Observe(
            next, loadSerial: 6, localReadSucceeded: false);
        Assert.False(ready);

        (_, ready) = WorldDigestReadinessRule.Observe(
            afterFailure, loadSerial: 6, localReadSucceeded: true);
        Assert.True(ready);
    }
    [Theory]
    [InlineData("end_m01aa", true, "tar_m10aa", 2u, false)]
    [InlineData("tar_m10aa", true, "TAR_M10AA", 2u, true)]
    [InlineData("end_m01aa", false, "", 2u, false)]
    [InlineData("end_m01aa", true, "", 2u, false)]
    public void FreshNativeWorldCannotUseRetainedPlayerLabel(string playerModule, bool readable, string nativeModule, uint epoch, bool expected)
        => Assert.Equal(expected, WorldDigestCoherenceRule.ModuleMatches(playerModule, readable, nativeModule, epoch));
}

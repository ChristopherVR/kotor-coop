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
}

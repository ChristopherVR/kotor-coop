using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ContainerCloseReleaseTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CloseAndReopenBeforeNextFrameCannotPublishTheOldCloseForTheNewRequest(bool closeObservedFirst)
    {
        ContainerCloseRelease release = new();
        // Both native hooks fired before the controller polled. Either observation order
        // must retire close 7 while request 8 is pending, including the first frame's race.
        if (closeObservedFirst) release.ObserveClose(0xD1, 7);
        release.BeginDeferred(0xD1, lastAllowedAttempt: 7);
        if (!closeObservedFirst) release.ObserveClose(0xD1, 7);
        Assert.Equal(0u, release.TargetId);
        release.ObserveClose(0xD1, 7); // duplicate old observation
        Assert.Equal(0u, release.TargetId);

        // The granted open increments the allowed-attempt serial. Its real close survives.
        release.ClearRelease();
        release.ObserveClose(0xD1, 8);
        Assert.Equal(0xD1u, release.TargetId);
    }

    [Fact]
    public void AnotherContainerCloseAndAnUnarbitratedCloseStillPublish()
    {
        ContainerCloseRelease release = new();
        release.ObserveClose(0xC6, 1);
        Assert.Equal(0xC6u, release.TargetId);
        release.BeginDeferred(0xD1, 1);
        Assert.Equal(0xC6u, release.TargetId);
        release.ObserveClose(0xC6, 1);
        Assert.Equal(0xC6u, release.TargetId);
        release.Reset();
        release.ObserveClose(0xD1, 1);
        Assert.Equal(0xD1u, release.TargetId);
    }
}

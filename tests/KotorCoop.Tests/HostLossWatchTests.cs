using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The client's give-up ladder for a host that never comes back.</summary>
public class HostLossWatchTests
{
    [Fact]
    public void WarnsOnceAfterAMinuteThenGivesUpAfterFive()
    {
        HostLossWatch watch = new();
        long t = 1_000_000;

        Assert.Equal(HostLossAction.None, watch.Observe(true, t));
        Assert.Equal(HostLossAction.None, watch.Observe(true, t + HostLossWatch.WarnAfterMs - 1));
        Assert.Equal(HostLossAction.Warn, watch.Observe(true, t + HostLossWatch.WarnAfterMs));
        Assert.Equal(HostLossAction.None, watch.Observe(true, t + HostLossWatch.WarnAfterMs + 150));
        Assert.Equal(HostLossAction.None, watch.Observe(true, t + HostLossWatch.GiveUpAfterMs - 1));
        Assert.Equal(HostLossAction.GiveUp, watch.Observe(true, t + HostLossWatch.GiveUpAfterMs));
    }

    [Fact]
    public void AQuickRejoinNeverSpeaksAndResetsTheCountdown()
    {
        HostLossWatch watch = new();
        long t = 0;

        // Host reloads a save: client rejoins in ~20 s.
        Assert.Equal(HostLossAction.None, watch.Observe(true, t));
        Assert.Equal(HostLossAction.None, watch.Observe(true, t + 20_000));
        Assert.Equal(HostLossAction.None, watch.Observe(false, t + 21_000));

        // The next outage gets a full fresh minute.
        Assert.Equal(HostLossAction.None, watch.Observe(true, t + 100_000));
        Assert.Equal(HostLossAction.None, watch.Observe(true, t + 100_000 + HostLossWatch.WarnAfterMs - 1));
        Assert.Equal(HostLossAction.Warn, watch.Observe(true, t + 100_000 + HostLossWatch.WarnAfterMs));
    }

    [Fact]
    public void AfterGivingUpAManualReconnectStartsAFreshWatch()
    {
        HostLossWatch watch = new();
        Assert.Equal(HostLossAction.None, watch.Observe(true, 0));
        Assert.Equal(HostLossAction.GiveUp, watch.Observe(true, HostLossWatch.GiveUpAfterMs));
        Assert.Equal(HostLossAction.None, watch.Observe(false, HostLossWatch.GiveUpAfterMs + 150));
        Assert.Equal(HostLossAction.None, watch.Observe(true, HostLossWatch.GiveUpAfterMs + 10_000));
        Assert.Equal(HostLossAction.None, watch.Observe(true, HostLossWatch.GiveUpAfterMs + 50_000));
    }

    [Fact]
    public void TheAnnouncementsAreThePlayerFacingTexts()
    {
        Assert.Equal("The host has stopped responding. Still trying - type `disconnect` to play on alone.", HostLossWatch.WarnLine);
        Assert.Contains("reconnect", HostLossWatch.GiveUpLine, StringComparison.Ordinal);
    }
}

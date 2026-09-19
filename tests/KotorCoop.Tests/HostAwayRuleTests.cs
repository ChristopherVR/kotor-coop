using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="HostAwayRule"/> — the third host-loss trigger (a host parked at the main menu,
/// still sending, flagged away). The pure half pins the stamp bookkeeping, the strict
/// timeout, the enabled/timeout-zero/already-lost gates, and that only an away-declared loss
/// is ever restored by the flag clearing. The loopback half proves <see cref="CoopClient"/>
/// wires it: a host publishing <see cref="PlayerState.FlagAway"/> past the timeout raises
/// <see cref="CoopClient.HostLost"/> WITHOUT the link dropping, and the flag clearing lowers it
/// again with no reconnect — the same edge pair <c>CoopAgent.PumpOnce</c> already acts on.
/// </summary>
public sealed class HostAwayRuleTests
{
    [Fact]
    public void TrackStampsTheFirstAwayTickHoldsItAndClearsOnReturn()
    {
        Assert.Equal(0, HostAwayRule.Track(awaySinceMs: 0, hostAwayNow: false, nowMs: 1000));
        Assert.Equal(1000, HostAwayRule.Track(awaySinceMs: 0, hostAwayNow: true, nowMs: 1000));
        Assert.Equal(1000, HostAwayRule.Track(awaySinceMs: 1000, hostAwayNow: true, nowMs: 5000));
        Assert.Equal(0, HostAwayRule.Track(awaySinceMs: 1000, hostAwayNow: false, nowMs: 5000));
    }

    [Fact]
    public void DeclaresOnlyOnceStrictlyPastTheTimeout()
    {
        Assert.False(HostAwayRule.ShouldDeclare(enabled: true, timeoutMs: 20_000, alreadyLost: false, awaySinceMs: 1000, nowMs: 21_000));
        Assert.True(HostAwayRule.ShouldDeclare(enabled: true, timeoutMs: 20_000, alreadyLost: false, awaySinceMs: 1000, nowMs: 21_001));
    }

    [Fact]
    public void NeverDeclaresWhileTheHostIsInTheWorld()
    {
        Assert.False(HostAwayRule.ShouldDeclare(enabled: true, timeoutMs: 20_000, alreadyLost: false, awaySinceMs: 0, nowMs: 999_999));
    }

    [Fact]
    public void GatesOnTheReleaseToggleAPositiveTimeoutAndNotAlreadyLost()
    {
        Assert.False(HostAwayRule.ShouldDeclare(enabled: false, timeoutMs: 20_000, alreadyLost: false, awaySinceMs: 1, nowMs: 999_999));
        Assert.False(HostAwayRule.ShouldDeclare(enabled: true, timeoutMs: 0, alreadyLost: false, awaySinceMs: 1, nowMs: 999_999));
        Assert.False(HostAwayRule.ShouldDeclare(enabled: true, timeoutMs: 20_000, alreadyLost: true, awaySinceMs: 1, nowMs: 999_999));
    }

    [Fact]
    public void OnlyAnAwayDeclaredLossIsRestoredByTheFlagClearing()
    {
        Assert.True(HostAwayRule.ShouldRestore(lostByAway: true, hostAwayNow: false));
        Assert.False(HostAwayRule.ShouldRestore(lostByAway: true, hostAwayNow: true));
        // Declared by silence or HostBye: a fresh Welcome clears that, never a flag reading.
        Assert.False(HostAwayRule.ShouldRestore(lostByAway: false, hostAwayNow: false));
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, Action each)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            each();
            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private static PlayerState Somewhere => new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);

    [Fact]
    public async Task AHostFlaggedAwayPastTheTimeoutIsLostWithoutTheLinkDroppingAndRestoredWhenItReturns()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30, HostAwayTimeoutMs = 500 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        bool hostAway = false;
        void Publish()
        {
            host.PublishLocalState(hostAway ? Somewhere with { Flags = PlayerState.FlagAway } : Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, TimeSpan.FromSeconds(25), Publish));
        Assert.False(client.HostLost);

        hostAway = true;

        Assert.True(
            await WaitForAsync(() => client.HostLost && client.HostAway, TimeSpan.FromSeconds(10), Publish),
            "HostLost never latched for a host that stayed flagged away");
        Assert.True(client.IsConnected, "the link itself must stay up - the host never went silent");

        hostAway = false;

        Assert.True(
            await WaitForAsync(() => !client.HostLost, TimeSpan.FromSeconds(10), Publish),
            "HostLost never cleared after the host came back into the world");
        Assert.False(client.HostAway);
        Assert.True(client.IsConnected);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task ABriefAwayNeverTripsIt()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30, HostAwayTimeoutMs = 5000 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        bool hostAway = false;
        void Publish()
        {
            host.PublishLocalState(hostAway ? Somewhere with { Flags = PlayerState.FlagAway } : Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, TimeSpan.FromSeconds(25), Publish));

        // A load screen's worth of away, then back: well under the timeout.
        hostAway = true;
        Assert.False(await WaitForAsync(() => client.HostLost, TimeSpan.FromMilliseconds(800), Publish));
        hostAway = false;
        Assert.False(await WaitForAsync(() => client.HostLost, TimeSpan.FromMilliseconds(500), Publish));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}

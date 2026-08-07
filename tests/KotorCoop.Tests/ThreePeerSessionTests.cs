using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A real host and TWO real clients over loopback UDP.
///
/// Everything in this project had only ever been exercised with two players, and the bugs
/// that hid there are not subtle — they are "the wrong player takes the damage" and "the
/// second attacker's hits are discarded". Neither is reachable with one remote peer, because
/// every list has one element and every collision needs two. So the two-peer battery could
/// not have caught them and a three-peer one is the only thing that can.
/// </summary>
public class ThreePeerSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    private static async Task<bool> WaitForAsync(Func<bool> condition, TimeSpan timeout, Action? each = null)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            each?.Invoke();

            if (condition())
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    [Fact]
    public async Task ThreePlayersAllSeeEachOther()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        using CoopClient malak = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 }, hostEndPoint);
        using CoopClient bastila = new(new SessionSettings { PlayerName = "Bastila", TickRate = 30 }, hostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task malakTask = malak.RunAsync(cts.Token);
        Task bastilaTask = bastila.RunAsync(cts.Token);

        PlayerState hostState = new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);
        PlayerState malakState = new(new Vector3f(30f, 40f, 0f), 2f, "end_m01aa", 90, 0);
        PlayerState bastilaState = new(new Vector3f(50f, 60f, 0f), 3f, "end_m01aa", 80, 0);

        void Publish()
        {
            host.PublishLocalState(hostState);
            malak.PublishLocalState(malakState);
            bastila.PublishLocalState(bastilaState);
        }

        Assert.True(
            await WaitForAsync(() => malak.IsConnected && bastila.IsConnected, Timeout, Publish),
            "both clients must be admitted");

        // Distinct ids, dealt monotonically. A collision here would make every per-peer
        // dictionary in the session alias two players onto one entry.
        Assert.NotEqual(malak.LocalPeerId, bastila.LocalPeerId);
        Assert.NotEqual(CoopHost.HostPeerId, malak.LocalPeerId);
        Assert.NotEqual(CoopHost.HostPeerId, bastila.LocalPeerId);

        Assert.True(
            await WaitForAsync(() => host.GetRemotePeers().Count == 2, Timeout, Publish),
            "the host must see both clients");

        // The relay is what makes three players a session rather than two parallel duets:
        // each client has to see the OTHER client, whose packets it never receives directly.
        Assert.True(
            await WaitForAsync(
                () => malak.GetRemotePeers().Any(p => p.PeerId == bastila.LocalPeerId)
                    && bastila.GetRemotePeers().Any(p => p.PeerId == malak.LocalPeerId),
                Timeout,
                Publish),
            "each client must see the other client through the host's relay");

        // Position is interpolated between two received snapshots, so it reads as the origin
        // until a second one lands — wait for the value rather than sampling the first sight.
        Assert.True(
            await WaitForAsync(
                () => malak.GetRemotePeers()
                    .First(p => p.PeerId == bastila.LocalPeerId).State.Position == bastilaState.Position,
                Timeout,
                Publish),
            "the relayed position must be the third player's own, not the origin");

        RemotePeer bastilaAsSeenByMalak = malak.GetRemotePeers().First(p => p.PeerId == bastila.LocalPeerId);
        Assert.Equal("Bastila", bastilaAsSeenByMalak.Name);

        RemotePeer hostAsSeenByBastila = bastila.GetRemotePeers().First(p => p.PeerId == CoopHost.HostPeerId);
        Assert.Equal("Revan", hostAsSeenByBastila.Name);

        await cts.CancelAsync();
        await Task.WhenAll(SwallowCancel(hostTask), SwallowCancel(malakTask), SwallowCancel(bastilaTask));
    }

    /// <summary>
    /// Two different players hit the same third player's phantom. Their reports are separate
    /// RUNNING TOTALS, so they must be added.
    ///
    /// ⚠ The old code kept one number and took the larger: once the host had dealt 40, every
    /// report the other client sent (5, then 10, then 15...) was smaller and was thrown away,
    /// permanently. That player's damage never landed at all. With two players there is only
    /// ever one source, so the max and the sum are the same number and the bug was invisible.
    /// </summary>
    [Fact]
    public async Task DamageFromTwoAttackersIsSummedNotMaxed()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        using CoopClient attacker = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 }, hostEndPoint);
        using CoopClient victim = new(new SessionSettings { PlayerName = "Bastila", TickRate = 30 }, hostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task attackerTask = attacker.RunAsync(cts.Token);
        Task victimTask = victim.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(1f, 2f, 0f), 0f, "end_m01aa", 100, 0);

        void Publish()
        {
            host.PublishLocalState(state);
            attacker.PublishLocalState(state);
            victim.PublishLocalState(state);
        }

        Assert.True(
            await WaitForAsync(() => attacker.IsConnected && victim.IsConnected, Timeout, Publish),
            "both clients must be admitted");

        uint victimId = victim.LocalPeerId;

        // The host's own game deals 40 to the victim's phantom; the other client's deals 10.
        // The larger one is sent first on purpose - that is the ordering that used to make
        // the smaller one unreachable forever.
        void Damage()
        {
            Publish();
            host.PublishPhantomDamage(victimId, 40, 111);
            attacker.PublishPhantomDamage(victimId, 10, 222);
        }

        Assert.True(
            await WaitForAsync(() => victim.GetIncomingPhantomDamage().CumulativeDamage >= 50, Timeout, Damage),
            $"expected 40 + 10 = 50, got {victim.GetIncomingPhantomDamage().CumulativeDamage}");

        Assert.Equal(50u, victim.GetIncomingPhantomDamage().CumulativeDamage);

        // Totals advance per source and the sum stays monotonic.
        void More()
        {
            Publish();
            host.PublishPhantomDamage(victimId, 40, 111);
            attacker.PublishPhantomDamage(victimId, 25, 222);
        }

        Assert.True(
            await WaitForAsync(() => victim.GetIncomingPhantomDamage().CumulativeDamage >= 65, Timeout, More),
            $"expected 40 + 25 = 65, got {victim.GetIncomingPhantomDamage().CumulativeDamage}");

        Assert.Equal(65u, victim.GetIncomingPhantomDamage().CumulativeDamage);

        await cts.CancelAsync();
        await Task.WhenAll(SwallowCancel(hostTask), SwallowCancel(attackerTask), SwallowCancel(victimTask));
    }

    /// <summary>
    /// The host is a damage target too, and both clients can be hitting it. Same rule, other
    /// direction — this one exercises CoopHost's inbound table rather than CoopClient's.
    /// </summary>
    [Fact]
    public async Task HostSumsDamageFromBothClients()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        IPEndPoint hostEndPoint = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        using CoopClient one = new(new SessionSettings { PlayerName = "Malak", TickRate = 30 }, hostEndPoint);
        using CoopClient two = new(new SessionSettings { PlayerName = "Bastila", TickRate = 30 }, hostEndPoint);

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(15));
        Task hostTask = host.RunAsync(cts.Token);
        Task oneTask = one.RunAsync(cts.Token);
        Task twoTask = two.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(1f, 2f, 0f), 0f, "end_m01aa", 100, 0);

        void Publish()
        {
            host.PublishLocalState(state);
            one.PublishLocalState(state);
            two.PublishLocalState(state);
        }

        Assert.True(
            await WaitForAsync(() => one.IsConnected && two.IsConnected, Timeout, Publish),
            "both clients must be admitted");

        void Damage()
        {
            Publish();
            one.PublishPhantomDamage(CoopHost.HostPeerId, 7, 333);
            two.PublishPhantomDamage(CoopHost.HostPeerId, 3, 444);
        }

        Assert.True(
            await WaitForAsync(() => host.GetIncomingPhantomDamage().CumulativeDamage >= 10, Timeout, Damage),
            $"expected 7 + 3 = 10, got {host.GetIncomingPhantomDamage().CumulativeDamage}");

        Assert.Equal(10u, host.GetIncomingPhantomDamage().CumulativeDamage);

        await cts.CancelAsync();
        await Task.WhenAll(SwallowCancel(hostTask), SwallowCancel(oneTask), SwallowCancel(twoTask));
    }

    private static async Task SwallowCancel(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
    }
}

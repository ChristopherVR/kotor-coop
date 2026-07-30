using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Runs a real host and a real client over loopback UDP. No fakes: actual sockets, actual
/// packets, actual tick loops.
/// </summary>
public class SessionIntegrationTests
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
    public async Task ClientConnectsAndBothEndsSeeEachOther()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState hostState = new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);
        PlayerState clientState = new(new Vector3f(30f, 40f, 0f), 2f, "end_m01aa", 90, 0);

        void Publish()
        {
            host.PublishLocalState(hostState);
            client.PublishLocalState(clientState);
        }

        bool connected = await WaitForAsync(() => client.IsConnected, Timeout, Publish);
        Assert.True(connected, "client never received a Welcome");
        Assert.Equal(2u, client.LocalPeerId);

        bool clientSeesHost = await WaitForAsync(
            () => client.GetRemotePeers().Any(p => p.PeerId == CoopHost.HostPeerId),
            Timeout,
            Publish);
        Assert.True(clientSeesHost, "client never saw the host's avatar");

        bool hostSeesClient = await WaitForAsync(
            () => host.GetRemotePeers().Any(p => p.PeerId == 2),
            Timeout,
            Publish);
        Assert.True(hostSeesClient, "host never saw the client's avatar");

        RemotePeer hostAsSeenByClient = client.GetRemotePeers().First(p => p.PeerId == CoopHost.HostPeerId);
        Assert.Equal("Revan", hostAsSeenByClient.Name);
        Assert.Equal(hostState.Position, hostAsSeenByClient.State.Position);
        Assert.Equal("end_m01aa", hostAsSeenByClient.State.Module);

        RemotePeer clientAsSeenByHost = host.GetRemotePeers().First(p => p.PeerId == 2);
        Assert.Equal("Malak", clientAsSeenByHost.Name);
        Assert.Equal(clientState.Position, clientAsSeenByHost.State.Position);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task MovementPropagates()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        float x = 0f;
        void Publish()
        {
            host.PublishLocalState(new PlayerState(new Vector3f(x, 0f, 0f), 0f, "end_m01aa", 100, 0));
            client.PublishLocalState(new PlayerState(Vector3f.Zero, 0f, "end_m01aa", 100, 0));
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Walk the host avatar and confirm the client's view follows it.
        Assert.True(await WaitForAsync(
            () =>
            {
                x += 1f;
                return client.GetRemotePeers()
                    .Any(p => p.PeerId == CoopHost.HostPeerId && p.State.Position.X > 20f);
            },
            Timeout,
            Publish));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task WorldFlagsSyncInBothDirections()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(20));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Client opens a door: the reliable op must reach the authoritative host.
        client.SetFlag(1001, 1);
        Assert.True(
            await WaitForAsync(() => host.Flags.TryGet(1001, out sbyte v) && v == 1, Timeout, Publish),
            "flag set by the client never reached the host");

        // Host kills a boss: the client detects the version change and resyncs.
        host.SetFlag(2002, 1);
        Assert.True(
            await WaitForAsync(() => client.Flags.TryGet(2002, out sbyte v) && v == 1, Timeout, Publish),
            "flag set by the host never reached the client");

        Assert.True(
            await WaitForAsync(() => client.Flags.Version == host.Flags.Version, Timeout, Publish),
            "flag versions never converged");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task JournalEntriesMergeInBothDirections()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(20));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // The client advances a quest: the reliable op must reach the host.
        client.SetJournalEntry("k_swg_bastila", 20);
        Assert.True(
            await WaitForAsync(
                () => host.Journal.Snapshot().Any(e => e.Key == "k_swg_bastila" && e.Value == 20),
                Timeout,
                Publish),
            "journal entry set by the client never reached the host");

        // The host advances further: the client resyncs via the version tripwire, and
        // the merge must never lower what the client already has.
        host.SetJournalEntry("k_swg_bastila", 30);
        host.SetJournalEntry("tat_duelling", 10);
        Assert.True(
            await WaitForAsync(
                () => client.Journal.Snapshot().Any(e => e.Key == "k_swg_bastila" && e.Value == 30)
                    && client.Journal.Snapshot().Any(e => e.Key == "tat_duelling" && e.Value == 10),
                Timeout,
                Publish),
            "journal entries set by the host never reached the client");

        Assert.True(
            await WaitForAsync(() => client.Journal.Version == host.Journal.Version, Timeout, Publish),
            "journal versions never converged");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task ClientEnemyReportsReachTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        List<EnemyState> report = [new(0x14, 3, 15, false)];

        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
            client.ReportEnemies("end_m01aa", report);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // The host must see the client's floor for its own module...
        Assert.True(
            await WaitForAsync(
                () => host.GetEnemyReports("end_m01aa").Any(e => e.ObjectId == 0x14 && e.CurrentHealth == 3),
                Timeout,
                Publish),
            "the client's enemy report never reached the host");

        // ...and never for a different module, where the same ids name strangers.
        Assert.DoesNotContain(host.GetEnemyReports("tar_m02aa"), e => e.ObjectId == 0x14);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    [Fact]
    public async Task TwoClientsSeeEachOtherThroughTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        using CoopClient bastila = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task malakTask = malak.RunAsync(cts.Token);
        Task bastilaTask = bastila.RunAsync(cts.Token);

        PlayerState state = new(new Vector3f(5f, 5f, 0f), 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            malak.PublishLocalState(state);
            bastila.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected && bastila.IsConnected, Timeout, Publish));
        Assert.NotEqual(malak.LocalPeerId, bastila.LocalPeerId);

        // The star topology means these two never talk directly; the host relays.
        Assert.True(
            await WaitForAsync(
                () => malak.GetRemotePeers().Any(p => p.PeerId == bastila.LocalPeerId),
                Timeout,
                Publish),
            "clients did not see each other through the relay");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, malakTask, bastilaTask);
    }

    [Fact]
    public async Task GarbageTrafficDoesNotDisturbTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(Timeout + TimeSpan.FromSeconds(10));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        PlayerState state = new(Vector3f.Zero, 0f, "end_m01aa", 100, 0);
        void Publish()
        {
            host.PublishLocalState(state);
            client.PublishLocalState(state);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Anything can arrive on a UDP port. None of it may take the session down.
        // The host binds 0.0.0.0, which is not a valid destination, so target loopback.
        IPEndPoint hostTarget = new(IPAddress.Loopback, host.ListenEndPoint.Port);

        using System.Net.Sockets.UdpClient noise = new();
        for (int i = 0; i < 40; i++)
        {
            byte[] junk = new byte[Random.Shared.Next(1, 400)];
            Random.Shared.NextBytes(junk);
            await noise.SendAsync(junk, junk.Length, hostTarget);
        }

        Assert.True(
            await WaitForAsync(
                () => client.GetRemotePeers().Any(p => p.PeerId == CoopHost.HostPeerId),
                Timeout,
                Publish),
            "session did not survive malformed traffic");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}

using System.Net;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class ServerDiscoveryTests
{
    [Fact]
    public void BeaconRoundTrips()
    {
        ServerBeacon beacon = new(1, 27140, 4, 8, "Endar Spire");
        Span<byte> buffer = stackalloc byte[64];
        int length = beacon.Write(buffer);
        Assert.True(length > 0);
        Assert.Equal(beacon, ServerBeacon.Read(buffer[..length]));
    }

    [Fact]
    public void GarbageBeaconIsRejected()
    {
        Assert.Null(ServerBeacon.Read([1, 2, 3]));           // too short
        Assert.Null(ServerBeacon.Read(new byte[16]));        // wrong magic (all zero)
    }

    [Fact]
    public void LockedFlagAndModuleRideTheBeacon()
    {
        ServerBeacon beacon = new(2, 27140, 4, 8, "Endar Spire", Locked: true, Module: "end_m01aa");
        Span<byte> buffer = stackalloc byte[ServerBeacon.MaxBytes];
        int length = beacon.Write(buffer);
        Assert.True(length > 0);
        Assert.Equal(beacon, ServerBeacon.Read(buffer[..length]));
    }

    [Fact]
    public void AVersionOneBeaconWithoutTheTailReadsAsUnlockedUnknownModule()
    {
        // A v1 sender stops after the name. The v2 reader must accept the datagram and
        // default the fields it is missing rather than reject the host from the list.
        ServerBeacon v1 = new(1, 27140, 2, 8, "Old Host");
        Span<byte> buffer = stackalloc byte[ServerBeacon.MaxBytes];
        int full = v1.Write(buffer);

        int nameBytes = System.Text.Encoding.UTF8.GetByteCount("Old Host");
        int v1Length = 8 + nameBytes; // cut the v2 tail off

        Assert.True(full >= v1Length);
        ServerBeacon? read = ServerBeacon.Read(buffer[..v1Length]);
        Assert.NotNull(read);
        Assert.False(read.Value.Locked);
        Assert.Equal(string.Empty, read.Value.Module);
        Assert.Equal("Old Host", read.Value.Name);
    }

    [Fact]
    public void ObservedServerUsesTheGamePortNotTheSourcePort()
    {
        ServerList list = new();
        // Beacon arrives from an ephemeral source port, but the join address must be the game port.
        list.Observe(new IPEndPoint(IPAddress.Parse("192.168.1.10"), 55123),
            new ServerBeacon(1, 27140, 2, 8, "Revan's Game"), 1000);

        IReadOnlyList<ServerInfo> servers = list.Snapshot(1500);
        Assert.Single(servers);
        Assert.Equal("192.168.1.10:27140", servers[0].Address);
        Assert.Equal("Revan's Game", servers[0].Name);
        Assert.Equal(2, servers[0].PlayerCount);
    }

    [Fact]
    public void StaleServersExpire()
    {
        ServerList list = new();
        IPEndPoint from = new(IPAddress.Parse("10.0.0.5"), 40000);
        list.Observe(from, new ServerBeacon(1, 27140, 1, 8, "x"), 1000);

        Assert.Single(list.Snapshot(1000 + ServerList.ExpiryMs));       // still fresh at the boundary
        Assert.Empty(list.Snapshot(1000 + ServerList.ExpiryMs + 1));    // dropped past it
    }

    [Fact]
    public void ReHearingAServerRefreshesItAndUpdatesTheCount()
    {
        ServerList list = new();
        IPEndPoint from = new(IPAddress.Parse("10.0.0.5"), 40000);
        list.Observe(from, new ServerBeacon(1, 27140, 1, 8, "x"), 1000);
        list.Observe(from, new ServerBeacon(1, 27140, 3, 8, "x"), 5000);

        IReadOnlyList<ServerInfo> servers = list.Snapshot(6000);
        Assert.Single(servers);
        Assert.Equal(3, servers[0].PlayerCount);
    }

    [Fact]
    public async Task AHostBeaconReachesAListeningBrowserOverLoopback()
    {
        // Isolated high port so a real discovery service (or a parallel test run) does not clash.
        const int port = 47016;
        using ServerDiscovery browser = new(port);
        using ServerDiscovery host = new(port);

        browser.StartListening();
        await Task.Delay(200); // let the listener bind before the first beacon
        host.StartAdvertising("Revan", 27140, 8, () => 2);

        bool seen = false;
        for (int i = 0; i < 60 && !seen; i++)
        {
            await Task.Delay(100);
            seen = browser.Servers.Any(s => s.Name == "Revan" && s.EndPoint.Port == 27140 && s.PlayerCount == 2);
        }

        Assert.True(seen, "the browsing client never heard the host's LAN beacon");
    }
}

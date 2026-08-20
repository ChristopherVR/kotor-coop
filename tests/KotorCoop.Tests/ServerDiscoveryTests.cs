using System.Linq;
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
    public void PartyPortraitsRideTheBeacon()
    {
        // The faces a browsing player sees before joining. Compared by CONTENT: the record's
        // generated equality compares the list by reference, so a round trip can never be
        // asserted with Assert.Equal on the whole beacon once a list is on it.
        ServerBeacon beacon = new(
            3, 27140, 3, 8, "Chris's Game", Locked: true, Module: "tar_m03aa",
            Portraits: ["po_pcarth", "po_pbastilla", "po_pmission"]);

        Span<byte> buffer = stackalloc byte[ServerBeacon.MaxBytes];
        int length = beacon.Write(buffer);
        Assert.True(length > 0);

        ServerBeacon? read = ServerBeacon.Read(buffer[..length]);

        Assert.NotNull(read);
        Assert.Equal("Chris's Game", read.Value.Name);
        Assert.Equal("tar_m03aa", read.Value.Module);
        Assert.True(read.Value.Locked);
        Assert.Equal<string[]>(["po_pcarth", "po_pbastilla", "po_pmission"], [.. read.Value.Faces]);
    }

    [Fact]
    public void AVersionTwoBeaconReadsWithNoPortraits()
    {
        // A v2 host stops after the module. Its listing must appear with no faces rather
        // than being rejected, exactly as v1 does for the locked flag.
        ServerBeacon v2 = new(2, 27140, 1, 8, "Old Host", Locked: false, Module: "end_m01aa");
        Span<byte> buffer = stackalloc byte[ServerBeacon.MaxBytes];
        int full = v2.Write(buffer);

        // Cut the v3 tail off: everything the v2 writer would not have produced.
        int nameBytes = System.Text.Encoding.UTF8.GetByteCount("Old Host");
        int v2Length = 8 + nameBytes + 2 + System.Text.Encoding.UTF8.GetByteCount("end_m01aa");
        Assert.True(v2Length < full);

        ServerBeacon? read = ServerBeacon.Read(buffer[..v2Length]);

        Assert.NotNull(read);
        Assert.Equal("end_m01aa", read.Value.Module);
        Assert.Empty(read.Value.Faces);
    }

    [Fact]
    public void APortraitCountTheDatagramCannotBackIsIgnored()
    {
        // The count is a remote byte that sizes a loop; a sender claiming three portraits it
        // did not send must cost the faces, not an exception.
        ServerBeacon beacon = new(3, 27140, 1, 8, "Host", Module: "end_m01aa", Portraits: ["po_pcarth"]);
        Span<byte> buffer = stackalloc byte[ServerBeacon.MaxBytes];
        int length = beacon.Write(buffer);

        // The count byte sits immediately before the single 16-byte resref.
        buffer[length - ServerBeacon.ResRefLength - 1] = 3;

        ServerBeacon? read = ServerBeacon.Read(buffer[..length]);

        Assert.NotNull(read);
        Assert.Empty(read.Value.Faces);
    }

    [Fact]
    public void TheListCarriesAHostsPortraitsThrough()
    {
        // The gap this closes: the beacon carried the faces and the list dropped them on the
        // floor, so the browser drew three empty boxes with the data sitting one field away.
        ServerList list = new();
        list.Observe(
            new IPEndPoint(IPAddress.Parse("10.5.0.2"), 27140),
            new ServerBeacon(3, 27140, 2, 8, "Chris's Game", Module: "end_m01aa",
                Portraits: ["po_pmha4", "po_ptrask"]),
            nowMs: 1000);

        ServerInfo server = Assert.Single(list.Snapshot(1000));

        Assert.Equal<string[]>(["po_pmha4", "po_ptrask"], [.. server.Faces]);
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

    /// <summary>
    /// Snapshot() orders newest-seen first, and two independently-beaconing hosts have no
    /// coordinated timing, so their relative row order can flip on every single call with no
    /// player action involved — a re-beacon 1ms apart is enough. A consumer that remembers "the
    /// player picked row 0" instead of "the player picked server X" will silently point at a
    /// different server the moment that happens (the native browser panel's session-swap bug).
    /// <see cref="ServerInfo.Address"/> is the stable identity that survives the reorder; this
    /// pins that contract so a future change to Snapshot's ordering can't quietly break it.
    /// </summary>
    [Fact]
    public void RowOrderFlipsOnBeaconTimingButEachServersAddressStaysStable()
    {
        ServerList list = new();
        IPEndPoint serverA = new(IPAddress.Parse("10.0.0.1"), 27140);
        IPEndPoint serverB = new(IPAddress.Parse("10.0.0.2"), 27140);

        list.Observe(serverA, new ServerBeacon(1, 27140, 1, 8, "Alpha"), 1000);
        list.Observe(serverB, new ServerBeacon(1, 27140, 1, 8, "Bravo"), 1001);

        IReadOnlyList<ServerInfo> before = list.Snapshot(2000);
        Assert.Equal(2, before.Count);
        Assert.Equal("Bravo", before[0].Name);  // newest-seen first: B (1001) ahead of A (1000)
        Assert.Equal("Alpha", before[1].Name);

        // A re-beacon from A, with no player action at all, is enough to flip the row order.
        list.Observe(serverA, new ServerBeacon(1, 27140, 1, 8, "Alpha"), 2500);

        IReadOnlyList<ServerInfo> after = list.Snapshot(3000);
        Assert.Equal(2, after.Count);
        Assert.Equal("Alpha", after[0].Name);   // order DID flip
        Assert.Equal("Bravo", after[1].Name);

        // What must NOT change: each server's own address, which a consumer resolves by rather
        // than by row index.
        Assert.Equal("10.0.0.1:27140", after.Single(s => s.Name == "Alpha").Address);
        Assert.Equal("10.0.0.2:27140", after.Single(s => s.Name == "Bravo").Address);
        Assert.Equal(
            before.Single(s => s.Name == "Alpha").Address,
            after.Single(s => s.Name == "Alpha").Address);
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

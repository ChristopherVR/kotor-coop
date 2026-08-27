using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Real host, real client, real sockets — and a real link failure, produced by the transport
/// fault injector rather than by killing a process.
///
/// ⚠ THE DISTINCTION IS THE WHOLE POINT OF THIS FILE. The only disconnect ever verified on the
/// rig was killing the host AGENT and restarting it, which closes sockets: both ends get a
/// clean edge and a fresh session. A dropped internet link gives no edge at all. The sockets
/// stay open, packets simply vanish, both ends keep transmitting into nothing, the host's peer
/// table expires the client while the client is still convinced it is connected — and then it
/// all comes back. Every bug these tests cover lives in that second shape and none of them in
/// the first.
/// </summary>
public class DisconnectRecoveryTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(25);

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

    private static PlayerState Somewhere => new(new Vector3f(10f, 20f, 0f), 1f, "end_m01aa", 100, 0);

    /// <summary>
    /// The headline case: ten seconds of nothing, in both directions, which is twice the peer
    /// timeout — so the host really does expire the slot and this really is a rejoin, not a
    /// blip that the existing keepalives cover.
    ///
    /// The assertion that matters is the PEER ID. Everything cumulative in this session is
    /// keyed on it (the damage ledger, the incoming phantom-damage table, the progression
    /// ledger, the plugin's phantom slot), so a returning player dealt a new id is not a
    /// cosmetic annoyance — it is a second phantom on every other screen and a second helping
    /// of every running total.
    /// </summary>
    [Fact]
    public async Task ATenSecondBlackoutHealsAndThePlayerComesBackAsTheSamePeer()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        uint originalId = client.LocalPeerId;
        Assert.True(await WaitForAsync(() => host.GetRemotePeers().Count == 1, Timeout, Publish));

        // Pull the cable on the client: nothing it sends leaves, nothing addressed to it
        // arrives. The host keeps broadcasting into the hole, exactly as a real host does.
        client.Network.StartBlackout(10_000);

        // It must notice on its own — nothing tells it.
        Assert.True(
            await WaitForAsync(() => !client.IsConnected, TimeSpan.FromSeconds(10), Publish),
            "the client never noticed the link had gone");

        // And the host must let it go, or the roster grows a ghost.
        Assert.True(
            await WaitForAsync(() => host.GetRemotePeers().Count == 0, TimeSpan.FromSeconds(10), Publish),
            "the host kept a peer that had stopped talking");

        // The link comes back. Nobody intervenes.
        Assert.True(
            await WaitForAsync(() => client.IsConnected, Timeout, Publish),
            "the client never rejoined after the blackout ended");

        Assert.Equal(originalId, client.LocalPeerId);

        // Exactly one of it, on both ends, once the world snapshot has caught up.
        Assert.True(
            await WaitForAsync(
                () => host.GetRemotePeers().Count == 1 && client.GetRemotePeers().Count == 1,
                Timeout,
                Publish),
            "a duplicate phantom survived the reconnection");

        Assert.Equal(originalId, host.GetRemotePeers()[0].PeerId);
        Assert.Equal("Malak", host.GetRemotePeers()[0].Name);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// Everything reliable that happened on either side while the link was down has to be
    /// there afterwards: flags and journal in both directions, a ground item and a container
    /// removal from the host. The container removal is the one with no version tripwire — it
    /// is announced once and never mentioned again — so it is the one a blackout could lose
    /// silently and forever (and did: the host retired unsent ops the moment its recipient
    /// list went empty).
    /// </summary>
    [Fact]
    public async Task NoReliableWorldStateIsLostAcrossTheBlackout()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Something both ends already agree on, so a lost resync is visible as a REGRESSION
        // and not merely as a gap.
        client.SetFlag(1000, 1);
        Assert.True(await WaitForAsync(() => host.Flags.TryGet(1000, out sbyte v) && v == 1, Timeout, Publish));

        client.Network.StartBlackout(8_000);
        Assert.True(await WaitForAsync(() => !client.IsConnected, TimeSpan.FromSeconds(10), Publish));

        // Both worlds move on, separately, while neither can hear the other.
        host.SetFlag(2001, 1);              // the host opened a door
        host.SetJournalEntry("k_swg_bastila", 30);
        host.DropItem("g_w_blstrpstl001", "end_m01aa", new Vector3f(1f, 2f, 3f));
        host.PublishContainerLoot(containerId: 0x53, itemId: 0xC9);

        client.SetFlag(3001, 1);            // the client opened a different one
        client.SetJournalEntry("tat_duelling", 10);

        Assert.True(
            await WaitForAsync(() => client.IsConnected, Timeout, Publish),
            "the client never rejoined");

        // Client -> host: the reliable senders held these through the whole outage.
        Assert.True(
            await WaitForAsync(
                () => host.Flags.TryGet(3001, out sbyte v) && v == 1
                    && host.Journal.Snapshot().Any(e => e.Key == "tat_duelling" && e.Value == 10),
                Timeout,
                Publish),
            "world state the client changed during the blackout never reached the host");

        // Host -> client: the version tripwire and the forced reconnect resync.
        Assert.True(
            await WaitForAsync(
                () => client.Flags.TryGet(2001, out sbyte v) && v == 1
                    && client.Journal.Snapshot().Any(e => e.Key == "k_swg_bastila" && e.Value == 30)
                    && client.Items.Count == 1,
                Timeout,
                Publish),
            "world state the host changed during the blackout never reached the client");

        // The channel with no tripwire of its own.
        Assert.True(
            await WaitForAsync(
                () => client.ContainerLootRemovals.Any(r => r.ContainerId == 0x53 && r.ItemId == 0xC9),
                Timeout,
                Publish),
            "a container the host emptied during the blackout was never announced to the client");

        // Nothing regressed on the way through.
        Assert.True(client.Flags.TryGet(1000, out sbyte survived) && survived == 1);
        Assert.True(host.Flags.TryGet(1000, out sbyte stillThere) && stillThere == 1);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// The world may not walk backwards through a resync. A client that advanced a flag and a
    /// quest during the outage must still hold those values after the host's snapshot lands —
    /// the snapshot is authoritative about what the HOST knows, not a licence to delete what
    /// it has not been told yet.
    ///
    /// This is the failure the old ReplaceAll produced: the resync fires precisely when the
    /// versions disagree, which is precisely when the client is holding something the host has
    /// not applied — and wiping the set to adopt the host's erased it. Nothing recovered it,
    /// because the bridge only ever reports globals that CHANGED.
    /// </summary>
    [Fact]
    public async Task TheWorldNeverWalksBackwardsThroughAResync()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        client.Network.StartBlackout(7_000);
        Assert.True(await WaitForAsync(() => !client.IsConnected, TimeSpan.FromSeconds(10), Publish));

        // The host raises a flag to 1 and a quest to 10; the client independently reaches
        // higher values for the same two, which is the ordinary co-op case (both engines run
        // the same scripts) and the one a destructive resync loses.
        host.SetFlag(4242, 1);
        host.SetJournalEntry("k_swg_endar", 10);

        // Since protocol 31 the merge is last-writer-wins by a per-store Lamport clock, not a
        // bare max: both stores are fresh, so the host's changes just above and a client
        // change made at the same "logical instant" would mint the SAME serial and collide as
        // a genuine tie — which "ties favour the host" (deliberately) resolves in the host's
        // favour regardless of which value is numerically higher. That coincidence is not
        // what this test is about (it is about a client's advance surviving a resync), so a
        // few disconnected dummy changes give the client's clock a running start — cheap and
        // legitimate here because the client is disconnected, so none of this reaches the
        // host and advances ITS clock to match.
        for (uint i = 0; i < 5; i++)
        {
            client.SetFlag(9000 + i, 1);
            client.SetJournalEntry($"warmup_{i}", 1);
        }

        // The client's real changes are now UNAMBIGUOUSLY the newer ones (strictly higher
        // serial than the host's, not merely a tie broken in its favour) — see the class
        // remarks for why "newer", not "higher", is what protocol 31 actually promises.
        client.SetFlag(4242, 3);
        client.SetJournalEntry("k_swg_endar", 40);

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Give the resync every chance to do damage.
        Assert.True(
            await WaitForAsync(
                () => host.Flags.TryGet(4242, out sbyte v) && v == 3
                    && host.Journal.Snapshot().Any(e => e.Key == "k_swg_endar" && e.Value == 40),
                Timeout,
                Publish),
            "the client's advance never reached the host");

        for (int i = 0; i < 40; i++)
        {
            Publish();
            await Task.Delay(25);
        }

        Assert.True(client.Flags.TryGet(4242, out sbyte flag));
        Assert.Equal(3, flag);
        Assert.Equal(40, client.Journal.Snapshot().Single(e => e.Key == "k_swg_endar").Value);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// A cumulative channel must not re-bank a peer's whole running total when that peer
    /// reconnects. The ledger's own defences cannot catch this one: the total does not go
    /// backwards (so rule 2 does not fire) and the generation is unchanged (so rule 3 does
    /// not fire) — the only thing that moved was the peer's IDENTITY, which is the key the
    /// ledger remembers "already banked" under.
    ///
    /// A client that has dealt 40 damage to a Sith soldier and reconnects mid-fight would
    /// otherwise have that soldier take 80.
    /// </summary>
    [Fact]
    public async Task ADamageLedgerDoesNotReBankAPeersTotalAfterAReconnect()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        // The host's side of the channel, exactly as CoopAgent runs it.
        PeerDamageLedger ledger = new();
        List<EnemyDamageEntry> report = [new(0x14, 40)];

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            client.ReportEnemyDamage("end_m01aa", generation: 7, report);
            ledger.Merge("end_m01aa", host.GetEnemyDamage("end_m01aa"));
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        Assert.True(
            await WaitForAsync(() => ledger.OwedFor(0x14) == 40, Timeout, Publish),
            "the client's damage never reached the host's ledger");

        client.Network.StartBlackout(8_000);
        Assert.True(await WaitForAsync(() => !client.IsConnected, TimeSpan.FromSeconds(10), Publish));
        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // The client keeps reporting the same running total — its own counter did not restart,
        // because its game did not.
        for (int i = 0; i < 60; i++)
        {
            Publish();
            await Task.Delay(20);
        }

        Assert.Equal(40u, ledger.OwedFor(0x14));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// The same arithmetic, on the channel where getting it wrong hurts a player directly:
    /// incoming phantom damage is summed across senders and is deliberately never removed
    /// (removing a sender would walk the monotonic total backwards). A reconnecting peer
    /// arriving under a new id therefore ADDS a second running total on top of the first, and
    /// the plugin spends the difference as real damage — a player taking every hit they have
    /// ever taken again, in one tick, the moment their partner's router blinks.
    /// </summary>
    [Fact]
    public async Task IncomingPhantomDamageIsNotDoubledByAReconnect()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            client.PublishPhantomDamage(CoopHost.HostPeerId, cumulativeDamage: 40, attackerId: 0x14);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        Assert.True(
            await WaitForAsync(() => host.GetIncomingPhantomDamage().CumulativeDamage == 40, Timeout, Publish),
            "the client's phantom damage never reached the host");

        client.Network.StartBlackout(8_000);
        Assert.True(await WaitForAsync(() => !client.IsConnected, TimeSpan.FromSeconds(10), Publish));
        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        for (int i = 0; i < 60; i++)
        {
            Publish();
            await Task.Delay(20);
        }

        Assert.Equal(40u, host.GetIncomingPhantomDamage().CumulativeDamage);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// A THIRD player must not end up watching two copies of the one who reconnected. The
    /// relay is what makes this a separate case: the host cleans its own buffer the instant it
    /// expires a peer, but every other client's copy of that peer was being refreshed by the
    /// relay right up to that moment, so it aged out a further full timeout later — and a
    /// rejoin inside that window put two avatars on the screen.
    /// </summary>
    [Fact]
    public async Task AThirdPlayerNeverSeesTwoCopiesOfTheReconnectingPeer()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient malak = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        using CoopClient bastila = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task malakTask = malak.RunAsync(cts.Token);
        Task bastilaTask = bastila.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            malak.PublishLocalState(Somewhere);
            bastila.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => malak.IsConnected && bastila.IsConnected, Timeout, Publish));
        uint malakId = malak.LocalPeerId;

        // Bastila watches throughout; her view is the one under test.
        Assert.True(
            await WaitForAsync(() => bastila.GetRemotePeers().Count == 2, Timeout, Publish),
            "the third player never saw the full roster");

        malak.Network.StartBlackout(8_000);

        // While Malak is away Bastila must see exactly the host, and then exactly one Malak
        // again — never three peers, which is what a stale track plus a new id produces.
        Assert.True(
            await WaitForAsync(() => bastila.GetRemotePeers().Count == 1, TimeSpan.FromSeconds(15), Publish),
            "the third player kept a phantom for a peer the host had dropped");

        Assert.True(await WaitForAsync(() => malak.IsConnected, Timeout, Publish));
        Assert.Equal(malakId, malak.LocalPeerId);

        Assert.True(
            await WaitForAsync(
                () => bastila.GetRemotePeers().Count == 2
                    && bastila.GetRemotePeers().Any(p => p.PeerId == malakId),
                Timeout,
                Publish),
            "the reconnected peer never reappeared for the third player");

        // Watch a while: no duplicate is allowed to creep in afterwards either.
        for (int i = 0; i < 40; i++)
        {
            Publish();
            await Task.Delay(25);
            Assert.True(
                bastila.GetRemotePeers().Count <= 2,
                $"third player saw {bastila.GetRemotePeers().Count} peers after a reconnection");
        }

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, malakTask, bastilaTask);
    }

    /// <summary>
    /// The HALF-OPEN link: only the client's upstream fails. It keeps receiving the host's
    /// broadcasts perfectly, so silence — its only disconnect signal — never happens, while
    /// the host expires it after five seconds of hearing nothing. Before the roster check,
    /// this client stayed "connected" forever, sending state updates under a peer id the host
    /// had forgotten and which it therefore dropped: fully rendered world, completely
    /// invisible to everyone in it, with nothing in its own state saying so.
    /// </summary>
    [Fact]
    public async Task AOneWayOutageIsNoticedAndRepaired()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));
        uint originalId = client.LocalPeerId;
        Assert.True(
            await WaitForAsync(() => host.GetRemotePeers().Count == 1, Timeout, Publish),
            "the host never saw the client before the link was cut");

        // Upstream only. Everything the host sends still arrives here.
        client.Network.StartBlackout(8_000, NetSimDirection.Outbound);

        Assert.True(
            await WaitForAsync(() => host.GetRemotePeers().Count == 0, TimeSpan.FromSeconds(12), Publish),
            "the host never expired a peer it had stopped hearing from");

        // The client is still receiving, so it must work out from the ROSTER that it has been
        // dropped, and re-handshake.
        Assert.True(
            await WaitForAsync(
                () => host.GetRemotePeers().Count == 1 && client.IsConnected,
                Timeout,
                Publish),
            $"the client never noticed the host had forgotten it: client={client.GetStats().Status} "
            + $"peers={host.GetRemotePeers().Count} sim={client.Network.Describe()}");

        Assert.Equal(originalId, client.LocalPeerId);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// Thirty percent loss, in both directions, on both ends — a genuinely bad link that never
    /// fully drops. Nothing here should reconnect at all; everything reliable should simply
    /// take a few more attempts and arrive.
    /// </summary>
    [Fact]
    public async Task HeavyPacketLossStillConverges()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        host.Network.SetLoss(30);
        client.Network.SetLoss(30);

        Assert.True(
            await WaitForAsync(() => client.IsConnected, Timeout, Publish),
            "the handshake did not survive 30% loss");

        client.SetFlag(7001, 1);
        client.SetJournalEntry("k_swg_taris", 20);
        host.SetFlag(7002, 1);
        host.SetJournalEntry("k_swg_dantooine", 30);
        host.PublishContainerLoot(0x60, 0x61);

        Assert.True(
            await WaitForAsync(
                () => host.Flags.TryGet(7001, out sbyte a) && a == 1
                    && host.Journal.Snapshot().Any(e => e.Key == "k_swg_taris" && e.Value == 20)
                    && client.Flags.TryGet(7002, out sbyte b) && b == 1
                    && client.Journal.Snapshot().Any(e => e.Key == "k_swg_dantooine" && e.Value == 30)
                    && client.ContainerLootRemovals.Any(r => r.ContainerId == 0x60),
                TimeSpan.FromSeconds(40),
                Publish),
            "world state did not converge over a 30% loss link: "
            + $"hostFlag7001={host.Flags.TryGet(7001, out sbyte h1)}/{h1} "
            + $"hostQuest={host.Journal.Snapshot().Count} "
            + $"clientFlag7002={client.Flags.TryGet(7002, out sbyte c1)}/{c1} "
            + $"clientQuest={client.Journal.Snapshot().Count} "
            + $"clientLoot={client.ContainerLootRemovals.Count}");

        // A bad link is not a disconnect: the session should never have dropped.
        Assert.True(client.IsConnected);
        Assert.True(client.Network.DroppedOutbound > 0 && client.Network.DroppedInbound > 0);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// Container loot's own version+digest tripwire (this pass — see
    /// docs/research/disconnect-reconnect.md §4.8), exercised across a real link failure.
    ///
    /// Deliberately a BLACKOUT rather than <see cref="NetworkSim.SetLoss"/>: §4.8 measured
    /// <see cref="HeavyPacketLossStillConverges"/> as flaky under load — it fails on slow
    /// suite runs and passes on fast ones, because a percentage-loss budget is wall-clock but
    /// the attempts inside it are ticks, so a loaded machine buys fewer of them. A blackout's
    /// deadline is absolute against <see cref="SessionClock"/>, so this proves the same
    /// property — several container-loot removals made while the client cannot hear anything
    /// at all still converge once the link returns — without depending on machine load.
    ///
    /// Before this pass <see cref="ContainerLootStore"/> had no <c>Version</c> at all, so
    /// there was nothing on the store to assert convergence against beyond content equality;
    /// this is the first test that checks the wire tripwire fields (<c>Loot.Version</c>,
    /// <c>Loot.Digest</c>) themselves, the same way the flag and journal tests already could.
    /// </summary>
    [Fact]
    public async Task ContainerLootConvergesByVersionAndDigestAcrossADeterministicBlackout()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Agree on one removal before the outage, so a lost resync shows up as a REGRESSION
        // and not merely as a gap.
        host.PublishContainerLoot(0x10, 0x11);
        Assert.True(
            await WaitForAsync(
                () => client.Loot.Version == host.Loot.Version && client.Loot.Digest == host.Loot.Digest,
                Timeout,
                Publish),
            "the client never converged on the removal made before the blackout");

        client.Network.StartBlackout(8_000);
        Assert.True(await WaitForAsync(() => !client.IsConnected, TimeSpan.FromSeconds(10), Publish));

        // Three more removals while the client cannot hear anything at all — the shape §4.8
        // describes: reliable ops with nobody, for a while, to retransmit to.
        host.PublishContainerLoot(0x20, 0x21);
        host.PublishContainerLoot(0x30, 0x31);
        host.PublishContainerLoot(0x40, 0x41);

        Assert.True(
            await WaitForAsync(() => client.IsConnected, Timeout, Publish), "the client never rejoined");

        Assert.True(
            await WaitForAsync(
                () => client.Loot.Version == host.Loot.Version && client.Loot.Digest == host.Loot.Digest,
                Timeout,
                Publish),
            $"container loot never converged: host v{host.Loot.Version}/{host.Loot.Digest:x16} "
            + $"client v{client.Loot.Version}/{client.Loot.Digest:x16}");

        // Content, not just the tripwire fields: every removal the host made is on both sides.
        ContainerLootRemoval[] expected =
        [
            new(0x10, 0x11), new(0x20, 0x21), new(0x30, 0x31), new(0x40, 0x41),
        ];
        foreach (ContainerLootRemoval removal in expected)
        {
            Assert.Contains(removal, client.ContainerLootRemovals);
        }

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }
}

// The fault injector's own direct unit tests (armed/disarmed, loss, latency/jitter, blackout,
// direction, Clear) moved to the dedicated NetworkSimTests.cs — "file per class" — and grew a
// good deal more thorough there (an injectable Random pins loss/jitter boundary draws exactly
// instead of only bounding them). This file keeps only what genuinely needs a real host/client
// pair talking over it.

/// <summary>Parsing the runtime verb. A typo in a command that deliberately breaks the
/// network must never be read as something else that also breaks it.</summary>
public class NetSimCommandTests
{
    [Theory]
    [InlineData("coop netsim blackout 10", "blackout", "10")]
    [InlineData("netsim blackout 10 out", "blackout", "10 out")]
    [InlineData("coop netsim loss 30", "loss", "30")]
    [InlineData("netsim latency 120 40", "latency", "120 40")]
    [InlineData("coop netsim off", "off", null)]
    [InlineData("netsim", "status", null)]
    public void ValidLinesParse(string line, string sub, string? args)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.NetSim, command.Kind);
        Assert.Equal(sub, command.Address);
        Assert.Equal(args, command.Password);
    }

    [Theory]
    [InlineData("netsim blackout")]
    [InlineData("netsim blackout soon")]
    [InlineData("netsim blackout 10 sideways")]
    [InlineData("netsim loss 300")]
    [InlineData("netsim loss")]
    [InlineData("netsim latency -5")]
    [InlineData("netsim wobble 3")]
    public void MalformedLinesAreRefusedWithAReason(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.NotEmpty(command.Error);
    }
}

using System.Net;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Item 11(b), docs/research/enemy-materialisation.md §6: a peer that reconnects AFTER a
/// materialised-loot op fully retired (every then-connected peer had acked it) used to never
/// receive it — <see cref="MaterialisedLootSender"/> only tracks ops still in flight and forgets
/// one the moment it retires. <see cref="MaterialisedLootHistory"/> is the bounded permanent
/// record that fixes this, replayed to a (re)joining peer through
/// <c>CoopHost.ResyncMaterialisedLoot</c> — the same door container loot's own resync already
/// uses (<c>CoopHost.ResyncContainerLoot</c>).
///
/// The first two tests exercise the host directly, with no socket involved — the history's cap
/// and its module-change clear are pure host-state transitions and do not need a live peer to
/// observe. The rest are real host/client pairs over loopback, matching
/// <see cref="DisconnectRecoveryTests"/>'s style, because the actual resync only fires out of
/// <c>HandleHello</c> on a genuine (re)admission.
/// </summary>
[Trait("Category", "Slow")]
public class MaterialisedLootResyncTests
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

    private static MaterialisedLootItem Item(string resref) =>
        new(resref, new ItemIdentity(1, Charges: 0, MaxCharges: 0, Upgrades: 0, Flags: 0));

    // ---- host-only: the history's own cap and module-change clear -----------------------

    /// <summary>Publishing past the default cap must not grow the resync history without
    /// bound — the same discipline <see cref="ContainerLootStore"/> already applies to its own
    /// retained window.</summary>
    [Fact]
    public void PublishingMoreThanTheDefaultCapEvictsTheOldestFromTheHost()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0 });

        for (uint ownerEnemyId = 1; ownerEnemyId <= 300; ownerEnemyId++)
        {
            host.PublishMaterialisedLoot(ownerEnemyId, [Item($"item{ownerEnemyId}")]);
        }

        Assert.Equal(256, host.MaterialisedLootHistoryCount);
    }

    /// <summary>Owner enemy ids are reassigned per module load, so a resync history built for
    /// one module must not survive into the next — see <c>CoopHost.PublishEnemies</c>.</summary>
    [Fact]
    public void AHostModuleChangeClearsTheResyncHistory()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0 });

        host.PublishEnemies("end_m01aa", []); // arrive at the first module
        host.PublishMaterialisedLoot(0x10, [Item("g_w_blstrpstl001")]);
        Assert.Equal(1, host.MaterialisedLootHistoryCount);

        // Still the same module: the history must survive an ordinary re-publish of the census.
        host.PublishEnemies("end_m01aa", []);
        Assert.Equal(1, host.MaterialisedLootHistoryCount);

        // A genuine module change clears it.
        host.PublishEnemies("tar_m03aa", []);
        Assert.Equal(0, host.MaterialisedLootHistoryCount);
    }

    // ---- real host/client pairs: the resync actually firing on (re)admission -------------

    /// <summary>
    /// The headline case item 11(b) exists for: a peer (B) is connected, but disconnects before
    /// it ever received a death's loot; a SECOND peer (A) stays online and acks it, which
    /// retires the op on the host (B is not a current recipient while it is away, so its
    /// absence does not block retirement — see <c>MaterialisedLootSender.CollectDue</c>'s
    /// empty-recipients rule for why that alone is not the bug). Before this pass B's stand-in
    /// would stay empty forever; now the resync on B's reconnection replays it.
    /// </summary>
    [Fact]
    public async Task APeerDisconnectedBeforeAnOpRetiresReceivesItThroughTheResyncOnReconnect()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient clientA = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        using CoopClient clientB = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task aTask = clientA.RunAsync(cts.Token);
        Task bTask = clientB.RunAsync(cts.Token);

        List<MaterialisedLootPacket> receivedByB = [];

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            clientA.PublishLocalState(Somewhere);
            clientB.PublishLocalState(Somewhere);
            foreach (MaterialisedLootPacket packet in clientA.DrainMaterialisedLoot())
            {
                clientA.AcknowledgeMaterialisedLoot(packet);
            }
            foreach (MaterialisedLootPacket packet in clientB.DrainMaterialisedLoot())
            {
                receivedByB.Add(packet);
                clientB.AcknowledgeMaterialisedLoot(packet);
            }
        }

        Assert.True(await WaitForAsync(() => clientA.IsConnected && clientB.IsConnected, Timeout, Publish));
        Assert.True(await WaitForAsync(() => host.GetRemotePeers().Count == 2, Timeout, Publish));

        // B goes away first, so it is never a recipient of the op below at all.
        clientB.Network.StartBlackout(9_000);
        Assert.True(
            await WaitForAsync(() => host.GetRemotePeers().Count == 1, TimeSpan.FromSeconds(10), Publish),
            "the host never expired the disconnected peer");

        host.PublishMaterialisedLoot(0xB5, [Item("g_a_class4001"), Item("ptar_shockstick")]);

        // Only A is a recipient now, so A alone acking retires the op.
        Assert.True(
            await WaitForAsync(() => host.MaterialisedLootPendingCount == 0, Timeout, Publish),
            "the op never retired against the one peer still connected");

        // The history remembers it regardless.
        Assert.Equal(1, host.MaterialisedLootHistoryCount);

        // B's link returns; the host must recognise it as the SAME peer reconnecting.
        Assert.True(
            await WaitForAsync(() => clientB.IsConnected, Timeout, Publish),
            "B never rejoined after the blackout ended");
        Assert.True(
            await WaitForAsync(() => host.GetRemotePeers().Count == 2, Timeout, Publish),
            "the host never re-admitted B");

        // The resync on B's (re)admission is what B was missing before this pass.
        Assert.True(
            await WaitForAsync(
                () => receivedByB.Any(p => p.OwnerEnemyId == 0xB5),
                Timeout,
                Publish),
            "B never received the death's loot through the reconnect resync");

        MaterialisedLootPacket packet = receivedByB.First(p => p.OwnerEnemyId == 0xB5);
        Assert.Equal(2, packet.Items.Count);
        Assert.Contains(packet.Items, i => i.Resref == "g_a_class4001");
        Assert.Contains(packet.Items, i => i.Resref == "ptar_shockstick");

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, aTask, bTask);
    }

    /// <summary>
    /// A peer that already applied an op before it dropped must NOT be given it a second time
    /// after reconnecting — the client's dedup-by-owner-id has to survive the reconnect for
    /// that, which is exactly why <c>CoopClient.ResetMaterialisedLootDedup</c> is called only on
    /// a genuine module change (see <c>CoopAgent.PumpMaterialisedLoot</c>) and nowhere on the
    /// reconnect path.
    /// </summary>
    [Fact]
    public async Task ADedupedOwnerIsNotHandedToTheSamePeerAgainAfterItReconnects()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient client = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task clientTask = client.RunAsync(cts.Token);

        List<MaterialisedLootPacket> received = [];

        void Publish()
        {
            host.PublishLocalState(Somewhere);
            client.PublishLocalState(Somewhere);
            foreach (MaterialisedLootPacket packet in client.DrainMaterialisedLoot())
            {
                received.Add(packet);
                client.AcknowledgeMaterialisedLoot(packet);
            }
        }

        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        host.PublishMaterialisedLoot(0x77, [Item("g_w_qtrstaff01")]);

        Assert.True(
            await WaitForAsync(() => received.Any(p => p.OwnerEnemyId == 0x77), Timeout, Publish),
            "the client never received the op the first time");
        Assert.True(
            await WaitForAsync(() => host.MaterialisedLootPendingCount == 0, Timeout, Publish),
            "the op never fully retired while the client was connected and acking");

        int countBeforeReconnect = received.Count(p => p.OwnerEnemyId == 0x77);

        client.Network.StartBlackout(8_000);
        Assert.True(await WaitForAsync(() => !client.IsConnected, TimeSpan.FromSeconds(10), Publish));
        Assert.True(await WaitForAsync(() => client.IsConnected, Timeout, Publish));

        // Give the resync every chance to (wrongly) re-deliver.
        for (int i = 0; i < 40; i++)
        {
            Publish();
            await Task.Delay(25);
        }

        int countAfterReconnect = received.Count(p => p.OwnerEnemyId == 0x77);
        Assert.Equal(countBeforeReconnect, countAfterReconnect);

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, clientTask);
    }

    /// <summary>
    /// A peer joining for the FIRST time, in the same module the host has been publishing
    /// deaths in the whole time, must get the entire remembered history — not just whatever was
    /// still in flight the moment it said hello. And an already-caught-up peer must not be
    /// handed a duplicate as a side effect of that other peer's resync — the resync is there to
    /// bring the LATE joiner current, not to re-broadcast old news to everyone all over again.
    /// </summary>
    [Fact]
    public async Task ALateJoinerOnTheSameModuleReceivesTheWholeHistory()
    {
        using CoopHost host = new(new SessionSettings { PlayerName = "Revan", Port = 0, TickRate = 30 });
        using CoopClient clientA = new(
            new SessionSettings { PlayerName = "Malak", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(60));
        Task hostTask = host.RunAsync(cts.Token);
        Task aTask = clientA.RunAsync(cts.Token);

        List<MaterialisedLootPacket> receivedByA = [];

        void PublishA()
        {
            host.PublishLocalState(Somewhere);
            clientA.PublishLocalState(Somewhere);
            foreach (MaterialisedLootPacket packet in clientA.DrainMaterialisedLoot())
            {
                receivedByA.Add(packet);
                clientA.AcknowledgeMaterialisedLoot(packet);
            }
        }

        Assert.True(await WaitForAsync(() => clientA.IsConnected, Timeout, PublishA));

        host.PublishMaterialisedLoot(0x10, [Item("g_w_blstrpstl001")]);
        host.PublishMaterialisedLoot(0x20, [Item("g_a_class4001")]);
        host.PublishMaterialisedLoot(0x30, [Item("ptar_shockstick")]);

        Assert.True(
            await WaitForAsync(() => host.MaterialisedLootPendingCount == 0, Timeout, PublishA),
            "the three deaths never fully retired against the one peer already present");
        Assert.Equal(3, host.MaterialisedLootHistoryCount);

        int aCountBeforeLateJoin =
            receivedByA.Select(p => p.OwnerEnemyId).Distinct().Count();
        Assert.Equal(3, aCountBeforeLateJoin);

        // B joins fresh, after all three ops already retired.
        using CoopClient clientB = new(
            new SessionSettings { PlayerName = "Bastila", TickRate = 30 },
            new IPEndPoint(IPAddress.Loopback, host.ListenEndPoint.Port));
        Task bTask = clientB.RunAsync(cts.Token);

        List<MaterialisedLootPacket> receivedByB = [];

        void PublishBoth()
        {
            PublishA();
            clientB.PublishLocalState(Somewhere);
            foreach (MaterialisedLootPacket packet in clientB.DrainMaterialisedLoot())
            {
                receivedByB.Add(packet);
                clientB.AcknowledgeMaterialisedLoot(packet);
            }
        }

        Assert.True(await WaitForAsync(() => clientB.IsConnected, Timeout, PublishBoth));

        Assert.True(
            await WaitForAsync(
                () => receivedByB.Select(p => p.OwnerEnemyId).Distinct().Count() == 3,
                Timeout,
                PublishBoth),
            $"the late joiner only received {receivedByB.Select(p => p.OwnerEnemyId).Distinct().Count()} "
            + "of 3 historical deaths");

        Assert.Contains(receivedByB, p => p.OwnerEnemyId == 0x10);
        Assert.Contains(receivedByB, p => p.OwnerEnemyId == 0x20);
        Assert.Contains(receivedByB, p => p.OwnerEnemyId == 0x30);

        // A watches a while longer: it must never see a duplicate of loot it already applied.
        for (int i = 0; i < 20; i++)
        {
            PublishBoth();
            await Task.Delay(25);
        }

        int aCountAfterLateJoin =
            receivedByA.Select(p => p.OwnerEnemyId).Distinct().Count();
        Assert.Equal(3, aCountAfterLateJoin);
        Assert.All(
            [0x10u, 0x20u, 0x30u],
            ownerEnemyId => Assert.Equal(1, receivedByA.Count(p => p.OwnerEnemyId == ownerEnemyId)));

        await cts.CancelAsync();
        await Task.WhenAll(hostTask, aTask, bTask);
    }
}

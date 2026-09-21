using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class ContainerLootTests
{
    // ---- store ----------------------------------------------------------------

    [Fact]
    public void RetransmittedOpAppliesExactlyOnce()
    {
        ContainerLootStore store = new();
        ContainerLootPacket op = new(1, 5, 0x53, 0xC9);

        Assert.True(store.ApplyOp(op));
        Assert.False(store.ApplyOp(op));
        Assert.Equal(1, store.Count);
        Assert.Equal([new ContainerLootRemoval(0x53, 0xC9)], store.Snapshot());
    }

    [Fact]
    public void DistinctOpsForTheSameRemovalCollapseToOneEntry()
    {
        ContainerLootStore store = new();

        // Two different ops (e.g. a rescan re-announced it) naming the same (container, item).
        Assert.True(store.ApplyOp(new ContainerLootPacket(1, 5, 0x53, 0xC9)));
        Assert.False(store.ApplyOp(new ContainerLootPacket(1, 6, 0x53, 0xC9)));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void SnapshotAccumulatesEveryDistinctRemoval()
    {
        ContainerLootStore store = new();
        store.ApplyOp(new ContainerLootPacket(1, 1, 0x53, 0xC9));
        store.ApplyOp(new ContainerLootPacket(1, 2, 0x53, 0xCA));
        store.ApplyOp(new ContainerLootPacket(1, 3, 0x60, 0xD0));

        Assert.Equal(3, store.Count);
        Assert.Contains(new ContainerLootRemoval(0x60, 0xD0), store.Snapshot());
    }

    // ---- version + digest -------------------------------------------------------

    /// <summary>
    /// Before this pass this channel had no version at all — see
    /// docs/research/disconnect-reconnect.md §4.8. It now bumps once per genuinely new
    /// removal, exactly like the other three world stores, and a duplicate — whether the
    /// same op replayed or a different op naming the same pair — must not move it.
    /// </summary>
    [Fact]
    public void VersionBumpsOnceForEachDistinctRemoval()
    {
        ContainerLootStore store = new();
        Assert.Equal(0u, store.Version);

        Assert.True(store.ApplyOp(new ContainerLootPacket(1, 1, 0x53, 0xC9)));
        Assert.Equal(1u, store.Version);

        // Retransmit of the same op: no bump.
        Assert.False(store.ApplyOp(new ContainerLootPacket(1, 1, 0x53, 0xC9)));
        Assert.Equal(1u, store.Version);

        // A different op naming the same pair: still no bump, the removal is not new.
        Assert.False(store.ApplyOp(new ContainerLootPacket(1, 2, 0x53, 0xC9)));
        Assert.Equal(1u, store.Version);

        Assert.True(store.ApplyOp(new ContainerLootPacket(1, 3, 0x60, 0xD0)));
        Assert.Equal(2u, store.Version);
    }

    /// <summary>
    /// The digest must not care what order the removals arrived in — two peers that both
    /// end up holding the same set never agree on delivery order, and the tripwire has to
    /// compare equal anyway.
    /// </summary>
    [Fact]
    public void DigestIsInvariantToInsertionOrder()
    {
        ContainerLootStore forward = new();
        forward.ApplyOp(new ContainerLootPacket(1, 1, 0x53, 0xC9));
        forward.ApplyOp(new ContainerLootPacket(1, 2, 0x60, 0xD0));
        forward.ApplyOp(new ContainerLootPacket(1, 3, 0x70, 0xE1));

        ContainerLootStore backward = new();
        backward.ApplyOp(new ContainerLootPacket(1, 1, 0x70, 0xE1));
        backward.ApplyOp(new ContainerLootPacket(1, 2, 0x60, 0xD0));
        backward.ApplyOp(new ContainerLootPacket(1, 3, 0x53, 0xC9));

        Assert.Equal(forward.Version, backward.Version);
        Assert.Equal(forward.Digest, backward.Digest);
    }

    /// <summary>
    /// The whole point of carrying a digest beside the counter: two stores that reached the
    /// same VERSION by making the same number of changes, but different ones, must not
    /// compare equal on the digest — that tie is exactly what a bare counter cannot see
    /// (docs/research/disconnect-reconnect.md §4.1).
    /// </summary>
    [Fact]
    public void EqualVersionWithDifferentContentProducesADifferentDigest()
    {
        ContainerLootStore a = new();
        a.ApplyOp(new ContainerLootPacket(1, 1, 0x53, 0xC9));
        a.ApplyOp(new ContainerLootPacket(1, 2, 0x60, 0xD0));

        ContainerLootStore b = new();
        b.ApplyOp(new ContainerLootPacket(1, 1, 0x70, 0xE1));
        b.ApplyOp(new ContainerLootPacket(1, 2, 0x81, 0xF2));

        Assert.Equal(a.Version, b.Version);
        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Fact]
    public void AnEmptyStoreHasTheEmptyDigest()
    {
        ContainerLootStore store = new();
        Assert.Equal(EntryDigest.Empty, store.Digest);
        Assert.Equal(0u, store.Version);
    }

    [Fact]
    public void ClearResetsVersionAndDigest()
    {
        ContainerLootStore store = new();
        store.ApplyOp(new ContainerLootPacket(1, 1, 0x53, 0xC9));

        store.Clear();

        Assert.Equal(0u, store.Version);
        Assert.Equal(EntryDigest.Empty, store.Digest);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ModuleIdentityQueuesStaleOpsWithoutApplyingThemToTheActiveModule()
    {
        ContainerLootStore store = new();
        store.SetModule("end_m01aa");

        Assert.True(store.ApplyOp(new ContainerLootPacket(1, 1, 0x10, 0x20, "end_m01aa")));
        Assert.True(store.ApplyOp(new ContainerLootPacket(1, 2, 0x10, 0x20, "tar_m03aa")));
        Assert.Single(store.HistorySnapshot());

        store.SetModule("tar_m03aa");
        Assert.True(store.ApplyOp(new ContainerLootPacket(1, 3, 0x10, 0x21, "tar_m03aa")));
        Assert.Equal(2, store.HistorySnapshot().Count);

        store.SetModule("end_m01aa");
        Assert.Single(store.HistorySnapshot());
    }

    [Fact]
    public void HistorySurvivesTheBoundedGameWindow()
    {
        ContainerLootStore store = new();
        for (uint i = 0; i < 600; i++)
            Assert.True(store.Add(new ContainerLootRemoval(0x10, i + 1)));

        Assert.Equal(512, store.Snapshot().Count);
        Assert.Equal(600, store.HistorySnapshot().Count);
        Assert.Equal(600u, store.Version);
        Assert.False(store.Add(new ContainerLootRemoval(0x10, 1)));
    }

    // ---- sender ---------------------------------------------------------------

    [Fact]
    public void EnqueueMintsAnOpAndCollectDueEmitsIt()
    {
        ReliableLootSender sender = new();
        ContainerLootPacket? op = sender.Enqueue(CoopHost.HostPeerId, 0x53, 0xC9, 0);

        Assert.NotNull(op);
        Assert.Equal(0x53u, op.ContainerId);
        Assert.Equal(0xC9u, op.ItemId);

        List<ContainerLootPacket> due = sender.CollectDue([2u], 0);
        Assert.Single(due);
        Assert.Equal(op, due[0]);
    }

    [Fact]
    public void DuplicateEnqueueForTheSameRemovalIsRefused()
    {
        ReliableLootSender sender = new();
        Assert.NotNull(sender.Enqueue(CoopHost.HostPeerId, 0x53, 0xC9, 0));
        Assert.Null(sender.Enqueue(CoopHost.HostPeerId, 0x53, 0xC9, 0));
        Assert.Equal(1, sender.PendingCount);
    }

    [Fact]
    public void OpIsRetiredOnceEveryRecipientHasAcked()
    {
        ReliableLootSender sender = new();
        ContainerLootPacket op = sender.Enqueue(CoopHost.HostPeerId, 0x53, 0xC9, 0)!;

        // Two clients; both must ack before the op is done.
        uint[] recipients = [2u, 3u];
        Assert.Single(sender.CollectDue(recipients, 0));

        sender.Acknowledge(2, op.OpSequence);
        Assert.Equal(1, sender.PendingCount);

        sender.Acknowledge(3, op.OpSequence);

        // Fully acked: the next collection retires it and emits nothing.
        Assert.Empty(sender.CollectDue(recipients, 1000));
        Assert.Equal(0, sender.PendingCount);
    }

    /// <summary>
    /// ⚠ THE OPPOSITE OF WHAT THIS TEST USED TO ASSERT. It proved that an op with no
    /// recipients is "dropped rather than queued forever" — which is the same thing as
    /// throwing away every chest the host emptied while a client's link was down, because
    /// the host removes a silent peer from its table after five seconds and the recipient
    /// list then goes empty. The client came back, was never told, and could take the same
    /// loot a second time. World truth now waits for somebody to tell.
    /// </summary>
    [Fact]
    public void AnOpWithNoRecipientsWaitsForOneInsteadOfBeingDropped()
    {
        ReliableLootSender sender = new();
        sender.Enqueue(CoopHost.HostPeerId, 0x53, 0xC9, 0);

        // Nobody connected: nothing goes out, but nothing is lost either.
        Assert.Empty(sender.CollectDue([], 0));
        Assert.Empty(sender.CollectDue([], 10_000));
        Assert.Equal(1, sender.PendingCount);

        // The client comes back and is told at once.
        Assert.Single(sender.CollectDue([2u], 10_000));
    }

    [Fact]
    public void TheSenderIsBoundedRatherThanUnlimited()
    {
        ReliableLootSender sender = new(maxPending: 3);

        for (uint item = 0; item < 20; item++)
        {
            sender.Enqueue(CoopHost.HostPeerId, 0x53, item, 0);
        }

        Assert.Equal(3, sender.PendingCount);
    }

    [Fact]
    public void RetransmitWaitsForTheInterval()
    {
        ReliableLootSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(CoopHost.HostPeerId, 0x53, 0xC9, 0);

        uint[] recipients = [2u];
        Assert.Single(sender.CollectDue(recipients, 0));   // first send
        Assert.Empty(sender.CollectDue(recipients, 100));  // too soon
        Assert.Single(sender.CollectDue(recipients, 300)); // interval elapsed
    }

    [Fact]
    public void AckFromAnotherModuleCannotRetireAReusedSequence()
    {
        ReliableLootSender sender = new(retransmitIntervalMs: 10);
        ContainerLootPacket op = sender.Enqueue(CoopHost.HostPeerId, 0x53, 0xC9, 0, "end_m01aa")!;

        Assert.Single(sender.CollectDue([2u], 0));
        sender.Acknowledge(2, op.OpSequence, "tar_m03aa");
        Assert.Single(sender.CollectDue([2u], 20));

        sender.Acknowledge(2, op.OpSequence, "end_m01aa");
        Assert.Empty(sender.CollectDue([2u], 40));
    }
}

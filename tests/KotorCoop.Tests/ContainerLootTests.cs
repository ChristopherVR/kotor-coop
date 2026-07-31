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

    [Fact]
    public void AnOpWithNoRecipientsIsDroppedRatherThanQueuedForever()
    {
        ReliableLootSender sender = new();
        sender.Enqueue(CoopHost.HostPeerId, 0x53, 0xC9, 0);

        Assert.Empty(sender.CollectDue([], 0));
        Assert.Equal(0, sender.PendingCount);
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
}

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

    // ---- past the re-declare window ----------------------------------------------
    //
    // ⚠ THE LONG-SESSION CASE, AND THE ONE THAT DOES NOT SHOW UP IN A SHORT TEST. The store
    // keeps a bounded WINDOW of removals to re-declare to the game, ordered by arrival. Arrival
    // order is not a shared fact — UDP reorders, retransmits and resyncs — so once a session
    // passes the window size, the host and each client are holding different slices of the same
    // set. The version and the digest must therefore describe the SET, not the slice, or the
    // loot tripwire disagrees for the rest of the session and the client asks for a resync
    // twice a second forever, each ask re-offering hundreds of reliable ops that nothing can
    // settle.

    private static ContainerLootStore FilledInOrder(IEnumerable<int> order)
    {
        ContainerLootStore store = new();
        uint sequence = 1;

        foreach (int i in order)
        {
            store.ApplyOp(new ContainerLootPacket(1, sequence++, (uint)(0x1000 + (i / 8)), (uint)(0x2000 + i)));
        }

        return store;
    }

    [Fact]
    public void TwoStoresTaughtTheSameRemovalsInDifferentOrdersAgree()
    {
        // Comfortably past the window on both sides.
        int[] forwards = [.. Enumerable.Range(0, 900)];
        int[] backwards = [.. forwards.Reverse()];
        int[] shuffled = [.. forwards.OrderBy(i => (i * 2654435761u) % 4099)];

        ContainerLootStore host = FilledInOrder(forwards);
        ContainerLootStore reversed = FilledInOrder(backwards);
        ContainerLootStore jumbled = FilledInOrder(shuffled);

        Assert.Equal(host.Digest, reversed.Digest);
        Assert.Equal(host.Digest, jumbled.Digest);
        Assert.Equal(host.Version, reversed.Version);
        Assert.Equal(host.Version, jumbled.Version);
    }

    [Fact]
    public void AnEvictedRemovalIsStillKnownAndIsNotCountedTwiceIfReAnnounced()
    {
        ContainerLootStore store = FilledInOrder(Enumerable.Range(0, 900));

        ulong digest = store.Digest;
        uint version = store.Version;

        // The very first removal has long since left the re-declare window...
        ContainerLootRemoval first = new(0x1000, 0x2000);
        Assert.DoesNotContain(first, store.Snapshot());

        // ...but the store still knows it happened, so a retransmit or a host resync offering
        // it again is not news and must not move either tripwire field.
        Assert.True(store.Contains(first));
        Assert.False(store.ApplyOp(new ContainerLootPacket(1, 9999, first.ContainerId, first.ItemId)));
        Assert.Equal(digest, store.Digest);
        Assert.Equal(version, store.Version);
    }

    [Fact]
    public void TheReDeclareWindowIsStillBoundedAndHoldsTheNEWESTRemovals()
    {
        ContainerLootStore store = FilledInOrder(Enumerable.Range(0, 900));

        List<ContainerLootRemoval> window = store.Snapshot();

        Assert.Equal(512, window.Count);
        Assert.Equal(new ContainerLootRemoval(0x1000 + (899 / 8), 0x2000 + 899), window[^1]);
        Assert.DoesNotContain(new ContainerLootRemoval(0x1000, 0x2000), window);
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
}

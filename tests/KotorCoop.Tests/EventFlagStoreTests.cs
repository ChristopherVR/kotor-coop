using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class EventFlagStoreTests
{
    [Fact]
    public void SettingAFlagBumpsTheVersion()
    {
        EventFlagStore store = new();
        Assert.Equal(0u, store.Version);

        Assert.True(store.Set(1001, 1));
        Assert.Equal(1u, store.Version);
    }

    [Fact]
    public void RedundantSetDoesNotBumpTheVersion()
    {
        EventFlagStore store = new();
        store.Set(1001, 1);
        uint version = store.Version;

        // Otherwise the version would drift every tick and clients would resync forever.
        Assert.False(store.Set(1001, 1));
        Assert.Equal(version, store.Version);
    }

    [Fact]
    public void DuplicateOpsAreAppliedOnce()
    {
        EventFlagStore store = new();
        FlagOpPacket op = new(OriginPeerId: 2, OpSequence: 7, FlagId: 1001, Value: 1);

        Assert.True(store.ApplyOp(op));
        uint version = store.Version;

        // Retransmission is normal on UDP; applying twice must be a no-op.
        Assert.False(store.ApplyOp(op));
        Assert.Equal(version, store.Version);
    }

    [Fact]
    public void SameSequenceFromDifferentPeersAreDistinctOps()
    {
        EventFlagStore store = new();

        Assert.True(store.ApplyOp(new FlagOpPacket(2, 1, 100, 1)));
        Assert.True(store.ApplyOp(new FlagOpPacket(3, 1, 200, 1)));

        Assert.True(store.TryGet(100, out sbyte a) && a == 1);
        Assert.True(store.TryGet(200, out sbyte b) && b == 1);
    }

    [Fact]
    public void ReplaceAllAdoptsHostState()
    {
        EventFlagStore store = new();
        store.Set(1, 1);
        store.Set(2, 1);

        store.ReplaceAll([new KeyValuePair<uint, sbyte>(3, 1)], version: 42);

        Assert.Equal(42u, store.Version);
        Assert.Equal(1, store.Count);
        Assert.False(store.TryGet(1, out _));
        Assert.True(store.TryGet(3, out sbyte value) && value == 1);
    }

    [Fact]
    public void SetCanLowerAValueButMergeCannot()
    {
        EventFlagStore store = new();
        store.Set(1001, 1);

        // Set is the local, deliberate path and stays unconstrained.
        Assert.True(store.Set(1001, 0));
        Assert.True(store.TryGet(1001, out sbyte lowered) && lowered == 0);

        // Merge is the network path: world state only advances, so a peer that is behind
        // cannot walk this instance backwards through a quest.
        store.Set(2002, 5);
        Assert.False(store.Merge(2002, 3));
        Assert.True(store.TryGet(2002, out sbyte kept) && kept == 5);

        Assert.True(store.Merge(2002, 9));
        Assert.True(store.TryGet(2002, out sbyte raised) && raised == 9);
    }

    [Fact]
    public void ReplayedOpCannotUndoABeat()
    {
        EventFlagStore store = new();

        // A quest advances to stage 3, then a stale packet carrying stage 1 arrives late.
        Assert.True(store.ApplyOp(new FlagOpPacket(2, 1, 500, 3)));
        Assert.False(store.ApplyOp(new FlagOpPacket(2, 2, 500, 1)));

        Assert.True(store.TryGet(500, out sbyte value) && value == 3);
    }

    [Fact]
    public void SeedDoesNotChurnTheVersion()
    {
        EventFlagStore store = new();

        // A KOTOR catalogue is well over a thousand entries; seeding through Set would
        // bump the version once each and make every client demand an immediate resync.
        store.Seed(Enumerable.Range(1, 1000).Select(i => new KeyValuePair<uint, sbyte>((uint)i, 1)));

        Assert.Equal(0u, store.Version);
        Assert.Equal(1000, store.Count);
    }

    [Fact]
    public void SnapshotContainsEveryFlag()
    {
        EventFlagStore store = new();
        store.Set(1, 1);
        store.Set(2, 0);
        store.Set(3, 1);

        List<KeyValuePair<uint, sbyte>> snapshot = store.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Contains(new KeyValuePair<uint, sbyte>(2, 0), snapshot);
    }
}

public class ReliableFlagSenderTests
{
    [Fact]
    public void EnqueuedOpIsPendingUntilAcked()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 100);

        FlagOpPacket op = sender.Enqueue(originPeerId: 2, flagId: 1001, value: 1, nowMs: 0);
        Assert.Equal(1, sender.PendingCount);

        sender.Acknowledge(op.OpSequence);
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void OpIsNotRetransmittedBeforeTheInterval()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(2, 1001, 1, nowMs: 0);

        Assert.Empty(sender.CollectDue(nowMs: 100));
        Assert.Single(sender.CollectDue(nowMs: 300));
    }

    [Fact]
    public void RetransmitsStopAfterMaxAttempts()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 10, maxAttempts: 3);
        sender.Enqueue(2, 1001, 1, nowMs: 0);

        long now = 0;
        for (int i = 0; i < 10; i++)
        {
            now += 50;
            sender.CollectDue(now);
        }

        // Retransmitting forever into a dead link would leak memory and bandwidth.
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void SequenceNumbersAreUnique()
    {
        ReliableFlagSender sender = new();

        uint first = sender.Enqueue(2, 1, 1, 0).OpSequence;
        uint second = sender.Enqueue(2, 2, 1, 0).OpSequence;

        Assert.NotEqual(first, second);
    }
}

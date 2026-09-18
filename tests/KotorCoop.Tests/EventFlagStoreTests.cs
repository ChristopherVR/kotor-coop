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
        FlagOpPacket op = new(OriginPeerId: 2, OpSequence: 7, FlagId: 1001, Value: 1, Serial: 1);

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

        Assert.True(store.ApplyOp(new FlagOpPacket(2, 1, 100, 1, 1)));
        Assert.True(store.ApplyOp(new FlagOpPacket(3, 1, 200, 1, 1)));

        Assert.True(store.TryGet(100, out sbyte a) && a == 1);
        Assert.True(store.TryGet(200, out sbyte b) && b == 1);
    }

    [Fact]
    public void ReplaceAllAdoptsHostState()
    {
        EventFlagStore store = new();
        store.Set(1, 1);
        store.Set(2, 1);

        store.ReplaceAll([new KeyValuePair<ulong, sbyte>(3, 1)], version: 42);

        Assert.Equal(42u, store.Version);
        Assert.Equal(1, store.Count);
        Assert.False(store.TryGet(1, out _));
        Assert.True(store.TryGet(3, out sbyte value) && value == 1);
    }

    [Fact]
    public void SetAlwaysWinsLocallyEvenAgainstAHigherRemoteValue()
    {
        // Set is the local, deliberate path: this instance's own engine (or player) just
        // made this so, and it must win regardless of what a peer announced earlier —
        // including lowering a value, which protocol 31 makes legal (a kill counter ticking
        // down, a boolean cleared back to false).
        EventFlagStore store = new();
        store.Set(1001, 1);
        Assert.True(store.Set(1001, 0));
        Assert.True(store.TryGet(1001, out sbyte lowered) && lowered == 0);

        // Even against a value this store already holds via a REMOTE op with a serial that
        // would otherwise look "ahead": a genuine local change always mints a fresh serial
        // that outranks anything already observed (see EventFlagStore's remarks).
        store.ApplyOp(new FlagOpPacket(OriginPeerId: 9, OpSequence: 1, FlagId: 2002, Value: 99, Serial: 500));
        Assert.True(store.Set(2002, 3));
        Assert.True(store.TryGet(2002, out sbyte local) && local == 3);
    }

    [Fact]
    public void ANonMonotonicRemoteDecreaseIsAcceptedWhenItIsGenuinelyNewer()
    {
        // The core fix: a peer's remote op carrying a LOWER value must still win when its
        // serial is newer — the old value-only merge refused this forever.
        EventFlagStore store = new();
        Assert.True(store.ApplyOp(new FlagOpPacket(2, 1, 2002, 5, Serial: 1)));
        Assert.True(store.ApplyOp(new FlagOpPacket(2, 2, 2002, 2, Serial: 2)));

        Assert.True(store.TryGet(2002, out sbyte value) && value == 2);
    }

    [Fact]
    public void AStaleReplayWithAFreshSequenceNumberNeverRegressesANewerValue()
    {
        // The safety property the old scheme leaned on the value-only max for: a
        // reconnecting peer re-announcing a value it knew before it dropped must not be able
        // to undo a change that happened while it was gone — even when the replay arrives
        // under a BRAND NEW op sequence number, which the (origin, sequence) dedup set alone
        // cannot catch (it has never seen this sequence before).
        EventFlagStore store = new();

        // The quest advances to 3 (serial 1), and while the peer was disconnected it drops
        // further to 1 (serial 2) — both genuine, in order.
        Assert.True(store.ApplyOp(new FlagOpPacket(2, 1, 500, 3, Serial: 1)));
        Assert.True(store.ApplyOp(new FlagOpPacket(2, 2, 500, 1, Serial: 2)));

        // The peer reconnects and replays its OWN old belief (value 3) — a fresh sequence
        // number (10), stale serial (1). Must be refused.
        Assert.False(store.ApplyOp(new FlagOpPacket(2, 10, 500, 3, Serial: 1)));

        Assert.True(store.TryGet(500, out sbyte value) && value == 1);
    }

    [Fact]
    public void ATieIsBrokenByTheLowerPeerIdIeTheHost()
    {
        // Two instances mint the same serial independently for their Nth change, before
        // either has heard from the other — a genuine tie. The host (peer 1) wins
        // deterministically on both sides, so two stores that each apply both ops in
        // whichever order they arrive still converge on the same value.
        EventFlagStore fromHostFirst = new();
        Assert.True(fromHostFirst.ApplyOp(new FlagOpPacket(OriginPeerId: 1, OpSequence: 1, FlagId: 9, Value: 10, Serial: 5)));
        Assert.False(fromHostFirst.ApplyOp(new FlagOpPacket(OriginPeerId: 2, OpSequence: 1, FlagId: 9, Value: 20, Serial: 5)));

        EventFlagStore fromClientFirst = new();
        Assert.True(fromClientFirst.ApplyOp(new FlagOpPacket(OriginPeerId: 2, OpSequence: 1, FlagId: 9, Value: 20, Serial: 5)));
        Assert.True(fromClientFirst.ApplyOp(new FlagOpPacket(OriginPeerId: 1, OpSequence: 1, FlagId: 9, Value: 10, Serial: 5)));

        Assert.True(fromHostFirst.TryGet(9, out sbyte a) && a == 10);
        Assert.True(fromClientFirst.TryGet(9, out sbyte b) && b == 10);
    }

    [Fact]
    public void ObservingARemoteSerialAdvancesTheClockEvenWhenTheValueLoses()
    {
        // Causality: a rejected remote op must still push this store's own clock forward, so
        // a SUBSEQUENT genuine local change (on an unrelated key) is guaranteed to outrank
        // everything this instance has seen so far, not just everything it has originated.
        EventFlagStore store = new();
        store.ApplyOp(new FlagOpPacket(2, 1, 1, 1, Serial: 1));

        // A stale op for the SAME key, refused — but its serial (100) still moves the clock.
        store.ApplyOp(new FlagOpPacket(2, 1, 1, 1, Serial: 1)); // no-op, dedup
        store.ApplyOp(new FlagOpPacket(2, 2, 1, 1, Serial: 100));
        Assert.Equal(100u, store.Version);

        // A fresh local change on a different key mints clock+1 = 101, which the next
        // MergeSnapshot/ApplyOp comparison can only ever be beaten by something newer still.
        Assert.True(store.Set(2, 1));
        Assert.True(store.TryGetStamp(2, out uint serial, out _));
        Assert.Equal(101u, serial);
    }

    [Fact]
    public void MergeSnapshotAppliesPerEntryLastWriterWinsAndNeverRegressesALocalChange()
    {
        EventFlagStore client = new();

        // The client made its own local advance (mints serial 1) before the host's snapshot
        // — a resync must not stomp it if the host has not heard of it yet.
        client.Set(42, 7, originPeerId: 2);

        FlagSnapshotEntry stale = new(FlagId: 42, Value: 3, Serial: 0, OriginPeerId: 1);
        client.MergeSnapshot([stale], version: 5);

        Assert.True(client.TryGet(42, out sbyte kept) && kept == 7);

        // A genuinely newer host entry (higher serial) DOES win, including a decrease.
        FlagSnapshotEntry newer = new(FlagId: 42, Value: 1, Serial: 99, OriginPeerId: 1);
        client.MergeSnapshot([newer], version: 99);

        Assert.True(client.TryGet(42, out sbyte overridden) && overridden == 1);
    }

    [Fact]
    public void SeedDoesNotChurnTheVersion()
    {
        EventFlagStore store = new();

        // A KOTOR catalogue is well over a thousand entries; seeding through Set would
        // bump the version once each and make every client immediately demand a resync.
        store.Seed(Enumerable.Range(1, 1000).Select(i => new KeyValuePair<ulong, sbyte>((uint)i, 1)));

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

        List<KeyValuePair<ulong, sbyte>> snapshot = store.Snapshot();

        Assert.Equal(3, snapshot.Count);
        Assert.Contains(new KeyValuePair<ulong, sbyte>(2, 0), snapshot);
    }

    // ---- digest -----------------------------------------------------------------

    [Fact]
    public void DigestIsInvariantToInsertionOrder()
    {
        EventFlagStore forward = new();
        forward.Set(1, 1);
        forward.Set(2, 0);
        forward.Set(3, -5);

        EventFlagStore backward = new();
        backward.Set(3, -5);
        backward.Set(2, 0);
        backward.Set(1, 1);

        Assert.Equal(forward.Version, backward.Version);
        Assert.Equal(forward.Digest, backward.Digest);
    }

    /// <summary>
    /// The reason the digest exists at all: two stores that reached the same VERSION by
    /// making the same number of changes, but different ones, must disagree on the digest —
    /// the tie a bare counter cannot see (docs/research/disconnect-reconnect.md §4.1). Still
    /// true under the Lamport-clock scheme: the digest depends only on VALUE, never on
    /// Serial/OriginPeerId, so this property survives the redesign unchanged.
    /// </summary>
    [Fact]
    public void EqualVersionWithDifferentContentProducesADifferentDigest()
    {
        EventFlagStore a = new();
        a.Set(1, 1);
        a.Set(2, 1);

        EventFlagStore b = new();
        b.Set(1, 1);
        b.Set(2, 2);

        Assert.Equal(a.Version, b.Version);
        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Fact]
    public void DigestReturnsToAPriorValueWhenSetBackToIt()
    {
        // XOR-summing means the digest is its own inverse: toggling a flag away from a value
        // and then back (via the unconstrained Set path) restores the exact digest the store
        // had at that value, even though Version keeps counting forward the whole time.
        EventFlagStore store = new();
        store.Set(1, 1);
        ulong atOne = store.Digest;

        store.Set(1, 2);
        Assert.NotEqual(atOne, store.Digest);

        store.Set(1, 1);
        Assert.Equal(atOne, store.Digest);
        Assert.Equal(3u, store.Version);
    }

    [Fact]
    public void DigestAgreesForTwoStoresThatDivergedByDifferentPathsToTheSameContent()
    {
        // One store reached (flag=5) locally; the other reached it by adopting a remote op.
        // The digest depends only on content, so two peers that genuinely agree must digest
        // identically regardless of which path got them there.
        EventFlagStore local = new();
        local.Set(7, 5, originPeerId: 1);

        EventFlagStore remote = new();
        remote.ApplyOp(new FlagOpPacket(OriginPeerId: 1, OpSequence: 1, FlagId: 7, Value: 5, Serial: 1));

        Assert.Equal(local.Digest, remote.Digest);
    }

    [Fact]
    public void ReplaceAllRecomputesTheDigestRatherThanCarryingStaleContributions()
    {
        EventFlagStore store = new();
        store.Set(1, 1);
        store.Set(2, 1);
        ulong beforeDigest = store.Digest;

        store.ReplaceAll([new KeyValuePair<ulong, sbyte>(1, 1)], version: 9);

        EventFlagStore direct = new();
        direct.Seed([new KeyValuePair<ulong, sbyte>(1, 1)]);

        Assert.Equal(direct.Digest, store.Digest);
        Assert.NotEqual(beforeDigest, store.Digest);
    }

    [Fact]
    public void SeedUpdatesTheDigestEvenThoughItDoesNotBumpTheVersion()
    {
        // The digest is a content fingerprint, not a change count, so it has to reflect
        // whatever the store actually holds regardless of how that content arrived.
        EventFlagStore store = new();
        ulong empty = store.Digest;

        store.Seed([new KeyValuePair<ulong, sbyte>(1, 1), new KeyValuePair<ulong, sbyte>(2, 2)]);

        Assert.Equal(0u, store.Version);
        Assert.NotEqual(empty, store.Digest);
    }
}

public class ReliableFlagSenderTests
{
    [Fact]
    public void EnqueuedOpIsPendingUntilAcked()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 100);

        FlagOpPacket op = sender.Enqueue(originPeerId: 2, flagId: 1001, value: 1, nowMs: 0)!;
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

    /// <summary>
    /// ⚠ THE OPPOSITE OF WHAT THIS TEST USED TO ASSERT, deliberately. It used to prove that
    /// retransmits stop after a fixed attempt count "rather than leaking into a dead link";
    /// what that actually did was throw away a world change nothing would ever re-announce
    /// (the bridge only reports globals that CHANGED), so a door opened during a blackout
    /// longer than fifteen seconds stayed shut for the other player permanently. The op is
    /// now kept and the cost is bounded by backing off instead.
    /// </summary>
    [Fact]
    public void AnUnackedOpSurvivesALongBlackoutAndBacksOff()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 10, maxIntervalMs: 100);
        sender.Enqueue(2, 1001, 1, nowMs: 0);

        int attempts = 0;
        for (long now = 0; now <= 60_000; now += 10)
        {
            attempts += sender.CollectDue(now).Count;
        }

        Assert.Equal(1, sender.PendingCount);

        // A minute of silence at a flat 10 ms would have been ~6000 datagrams; the cap makes
        // it ~600, and the op is still there to deliver when the link returns.
        Assert.InRange(attempts, 1, 700);
    }

    [Fact]
    public void ReconnectingMakesEveryPendingOpDueAtOnce()
    {
        ReliableFlagSender sender = new(retransmitIntervalMs: 250, maxIntervalMs: 4000);
        sender.Enqueue(2, 1001, 1, nowMs: 0);
        sender.Enqueue(2, 1002, 1, nowMs: 0);

        // Burn enough attempts that both ops are deep into the backoff.
        for (long now = 0; now <= 30_000; now += 250)
        {
            sender.CollectDue(now);
        }

        Assert.Empty(sender.CollectDue(30_100));

        sender.ResetBackoff();
        Assert.Equal(2, sender.CollectDue(30_100).Count);
    }

    [Fact]
    public void ThePendingQueueIsBounded()
    {
        ReliableFlagSender sender = new(maxPending: 4);

        for (uint flag = 0; flag < 50; flag++)
        {
            sender.Enqueue(2, flag, 1, nowMs: 0);
        }

        Assert.Equal(4, sender.PendingCount);
    }

    [Fact]
    public void SequenceNumbersAreUnique()
    {
        ReliableFlagSender sender = new();

        uint first = sender.Enqueue(2, 1, 1, 0)!.OpSequence;
        uint second = sender.Enqueue(2, 2, 1, 0)!.OpSequence;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void TheEnqueuedPacketCarriesItsSerial()
    {
        ReliableFlagSender sender = new();

        FlagOpPacket op = sender.Enqueue(2, 1001, 1, nowMs: 0, serial: 77)!;

        Assert.Equal(77u, op.Serial);
    }
}

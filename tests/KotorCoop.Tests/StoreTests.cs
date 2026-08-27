using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class StoreTests
{
    private const uint KeblaYurt = 0x1000;
    private const uint AnotherStore = 0x2000;

    private static readonly StoreStackKey Blaster =
        new(KeblaYurt, "g_w_blstrpstl001", 0, 0, 0, ItemIdentity.FlagKnown);

    private static readonly StoreStackKey UpgradedSaber =
        new(KeblaYurt, "g_w_dblsbr001", 0, 0, 0x5, (byte)(ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified));

    // ---- store ------------------------------------------------------------------

    [Fact]
    public void AddAccumulatesQuantity()
    {
        StoreStore store = new();

        Assert.True(store.Record(StoreOpKind.Add, Blaster, 5));
        Assert.Equal(5, store.Get(Blaster));

        Assert.True(store.Record(StoreOpKind.Add, Blaster, 3));
        Assert.Equal(8, store.Get(Blaster));
    }

    [Fact]
    public void RemoveFloorsAtZeroAndDropsTheKey()
    {
        StoreStore store = new();
        store.Record(StoreOpKind.Add, Blaster, 5);

        Assert.True(store.Record(StoreOpKind.Remove, Blaster, 3));
        Assert.Equal(2, store.Get(Blaster));

        // Removing more than exists floors at zero rather than going negative.
        Assert.True(store.Record(StoreOpKind.Remove, Blaster, 100));
        Assert.Equal(0, store.Get(Blaster));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void SetIsAbsoluteNotAdditive()
    {
        StoreStore store = new();
        store.Record(StoreOpKind.Add, Blaster, 5);

        // A resync Set to a lower value than the store already holds must REPLACE, not
        // subtract from, the existing total — see StoreOpKind.Set's own doc.
        Assert.True(store.Record(StoreOpKind.Set, Blaster, 2));
        Assert.Equal(2, store.Get(Blaster));

        // Re-setting to the SAME value is a genuine no-op (no version bump).
        uint versionBefore = store.Version;
        Assert.False(store.Record(StoreOpKind.Set, Blaster, 2));
        Assert.Equal(versionBefore, store.Version);
    }

    [Fact]
    public void DistinctStackIdentitiesOfTheSameResrefNeverMerge()
    {
        StoreStore store = new();
        StoreStackKey damaged = Blaster with { Charges = 10 };

        store.Record(StoreOpKind.Add, Blaster, 5);
        store.Record(StoreOpKind.Add, damaged, 2);

        Assert.Equal(5, store.Get(Blaster));
        Assert.Equal(2, store.Get(damaged));
        Assert.Equal(2, store.Count);
    }

    /// <summary>
    /// The property that distinguishes this channel from the party pool's:
    /// <see cref="StoreStackKey"/> carries a store object id, so the SAME resref+identity in TWO
    /// different stores must never be confused for one aggregate total — buying out Kebla
    /// Yurt's blasters must not touch a different store's identical-looking stock.
    /// </summary>
    [Fact]
    public void TheSameStackInTwoDifferentStoresNeverCollide()
    {
        StoreStore store = new();
        StoreStackKey sameStackElsewhere = Blaster with { StoreObjectId = AnotherStore };

        store.Record(StoreOpKind.Add, Blaster, 5);
        store.Record(StoreOpKind.Add, sameStackElsewhere, 9);

        Assert.Equal(5, store.Get(Blaster));
        Assert.Equal(9, store.Get(sameStackElsewhere));
        Assert.Equal(2, store.Count);

        Assert.True(store.Record(StoreOpKind.Remove, Blaster, 5));
        Assert.Equal(0, store.Get(Blaster));
        Assert.Equal(9, store.Get(sameStackElsewhere));
    }

    [Fact]
    public void RetransmittedOpAppliesExactlyOnce()
    {
        StoreStore store = new();
        StoreOpPacket op = new(1, 5, StoreOpKind.Add, Blaster, 5);

        Assert.True(store.ApplyOp(op));
        Assert.False(store.ApplyOp(op));
        Assert.Equal(5, store.Get(Blaster));
    }

    [Fact]
    public void ApplyOpIsAdditiveAcrossDistinctSequences()
    {
        StoreStore store = new();

        Assert.True(store.ApplyOp(new StoreOpPacket(1, 1, StoreOpKind.Add, Blaster, 5)));
        Assert.True(store.ApplyOp(new StoreOpPacket(1, 2, StoreOpKind.Add, Blaster, 3)));

        Assert.Equal(8, store.Get(Blaster));
    }

    [Fact]
    public void ANewKeyIsRefusedPastTheCapButAnExistingKeyStillUpdates()
    {
        StoreStore store = new();

        for (int i = 0; i < ProtocolConstants.MaxStoreStacks; i++)
        {
            StoreStackKey key = Blaster with { Charges = (byte)i };
            Assert.True(store.Record(StoreOpKind.Add, key, 1));
        }

        Assert.Equal(ProtocolConstants.MaxStoreStacks, store.Count);

        StoreStackKey overflow = Blaster with { Upgrades = 1 };
        Assert.False(store.Record(StoreOpKind.Add, overflow, 1));
        Assert.Equal(ProtocolConstants.MaxStoreStacks, store.Count);

        StoreStackKey existing = Blaster with { Charges = 0 };
        Assert.True(store.Record(StoreOpKind.Remove, existing, 1));
        Assert.Equal(ProtocolConstants.MaxStoreStacks - 1, store.Count);
        Assert.True(store.Record(StoreOpKind.Add, overflow, 1));
    }

    [Fact]
    public void DigestIsInvariantToInsertionOrder()
    {
        StoreStore forward = new();
        forward.Record(StoreOpKind.Add, Blaster, 5);
        forward.Record(StoreOpKind.Add, UpgradedSaber, 1);

        StoreStore backward = new();
        backward.Record(StoreOpKind.Add, UpgradedSaber, 1);
        backward.Record(StoreOpKind.Add, Blaster, 5);

        Assert.Equal(forward.Version, backward.Version);
        Assert.Equal(forward.Digest, backward.Digest);
    }

    [Fact]
    public void ClearResetsEverything()
    {
        StoreStore store = new();
        store.Record(StoreOpKind.Add, Blaster, 5);

        store.Clear();

        Assert.Equal(0u, store.Version);
        Assert.Equal(EntryDigest.Empty, store.Digest);
        Assert.Equal(0, store.Count);
        Assert.Equal(0, store.Get(Blaster));
    }

    // ---- sender -------------------------------------------------------------------

    [Fact]
    public void EnqueueMintsAnOpAndCollectDueEmitsIt()
    {
        ReliableStoreSender sender = new();
        StoreOpPacket? op = sender.Enqueue(CoopHost.HostPeerId, StoreOpKind.Add, Blaster, 5, 0);

        Assert.NotNull(op);
        Assert.Equal(Blaster, op.Key);
        Assert.Equal(5, op.CountDelta);

        List<StoreOpPacket> due = sender.CollectDue([2u], 0);
        Assert.Single(due);
        Assert.Equal(op, due[0]);
    }

    /// <summary>Two sells of the SAME key are two DIFFERENT, cumulative facts — the sender must
    /// never collapse them, mirroring PartyPoolTests's identical test.</summary>
    [Fact]
    public void TwoOpsForTheSameKeyAreBothQueuedIndependently()
    {
        ReliableStoreSender sender = new();

        Assert.NotNull(sender.Enqueue(CoopHost.HostPeerId, StoreOpKind.Add, Blaster, 5, 0));
        Assert.NotNull(sender.Enqueue(CoopHost.HostPeerId, StoreOpKind.Add, Blaster, 3, 0));

        Assert.Equal(2, sender.PendingCount);
    }

    [Fact]
    public void OpIsRetiredOnceEveryRecipientHasAcked()
    {
        ReliableStoreSender sender = new();
        StoreOpPacket op = sender.Enqueue(CoopHost.HostPeerId, StoreOpKind.Add, Blaster, 5, 0)!;

        uint[] recipients = [2u, 3u];
        Assert.Single(sender.CollectDue(recipients, 0));

        sender.Acknowledge(2, op.OpSequence);
        Assert.Equal(1, sender.PendingCount);

        sender.Acknowledge(3, op.OpSequence);
        Assert.Empty(sender.CollectDue(recipients, 1000));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void AnOpWithNoRecipientsWaitsForOneInsteadOfBeingDropped()
    {
        ReliableStoreSender sender = new();
        sender.Enqueue(CoopHost.HostPeerId, StoreOpKind.Add, Blaster, 5, 0);

        Assert.Empty(sender.CollectDue([], 0));
        Assert.Empty(sender.CollectDue([], 10_000));
        Assert.Equal(1, sender.PendingCount);

        Assert.Single(sender.CollectDue([2u], 10_000));
    }

    [Fact]
    public void TheSenderIsBoundedRatherThanUnlimited()
    {
        ReliableStoreSender sender = new(maxPending: 3);

        for (byte i = 0; i < 20; i++)
        {
            sender.Enqueue(CoopHost.HostPeerId, StoreOpKind.Add, Blaster with { Charges = i }, 1, 0);
        }

        Assert.Equal(3, sender.PendingCount);
    }

    [Fact]
    public void RetransmitWaitsForTheInterval()
    {
        ReliableStoreSender sender = new(retransmitIntervalMs: 250);
        sender.Enqueue(CoopHost.HostPeerId, StoreOpKind.Add, Blaster, 5, 0);

        uint[] recipients = [2u];
        Assert.Single(sender.CollectDue(recipients, 0));
        Assert.Empty(sender.CollectDue(recipients, 100));
        Assert.Single(sender.CollectDue(recipients, 300));
    }

    // ---- diff rule (the pure add/remove/echo decision) -----------------------------

    [Fact]
    public void NoChangeProducesNoDeltas()
    {
        Dictionary<StoreStackKey, int> same = new() { [Blaster] = 5 };
        Assert.Empty(StoreDiffRule.Diff(same, new Dictionary<StoreStackKey, int>(same)));
    }

    [Fact]
    public void ANewKeyProducesAnAddForItsFullQuantity()
    {
        Dictionary<StoreStackKey, int> expected = [];
        Dictionary<StoreStackKey, int> actual = new() { [Blaster] = 5 };

        List<StoreDiffRule.Delta> deltas = StoreDiffRule.Diff(expected, actual);

        Assert.Equal(new StoreDiffRule.Delta(Blaster, StoreOpKind.Add, 5), Assert.Single(deltas));
    }

    [Fact]
    public void AVanishedKeyProducesARemoveForItsFullQuantity()
    {
        Dictionary<StoreStackKey, int> expected = new() { [Blaster] = 5 };
        Dictionary<StoreStackKey, int> actual = [];

        List<StoreDiffRule.Delta> deltas = StoreDiffRule.Diff(expected, actual);

        Assert.Equal(new StoreDiffRule.Delta(Blaster, StoreOpKind.Remove, 5), Assert.Single(deltas));
    }

    [Fact]
    public void AStockIncreaseProducesAnAddForOnlyTheDelta()
    {
        // A stock increase is a SELL (the player sold this stack into the store).
        Dictionary<StoreStackKey, int> expected = new() { [Blaster] = 5 };
        Dictionary<StoreStackKey, int> actual = new() { [Blaster] = 8 };

        List<StoreDiffRule.Delta> deltas = StoreDiffRule.Diff(expected, actual);

        Assert.Equal(new StoreDiffRule.Delta(Blaster, StoreOpKind.Add, 3), Assert.Single(deltas));
    }

    [Fact]
    public void AStockDecreaseProducesARemoveForOnlyTheDelta()
    {
        // A stock decrease is a BUY (the player bought this stack out of the store).
        Dictionary<StoreStackKey, int> expected = new() { [Blaster] = 8 };
        Dictionary<StoreStackKey, int> actual = new() { [Blaster] = 5 };

        List<StoreDiffRule.Delta> deltas = StoreDiffRule.Diff(expected, actual);

        Assert.Equal(new StoreDiffRule.Delta(Blaster, StoreOpKind.Remove, 3), Assert.Single(deltas));
    }

    /// <summary>The echo-suppression property this channel depends on — mirrors
    /// PartyPoolTests's identical test.</summary>
    [Fact]
    public void EchoSuppressionFeedingTheSameMapTwiceProducesNothing()
    {
        Dictionary<StoreStackKey, int> totals = new() { [Blaster] = 5, [UpgradedSaber] = 1 };
        Assert.Empty(StoreDiffRule.Diff(totals, new Dictionary<StoreStackKey, int>(totals)));
    }

    [Fact]
    public void MultipleIndependentKeysEachProduceTheirOwnDelta()
    {
        Dictionary<StoreStackKey, int> expected = new() { [Blaster] = 5 };
        Dictionary<StoreStackKey, int> actual = new() { [Blaster] = 5, [UpgradedSaber] = 1 };

        List<StoreDiffRule.Delta> deltas = StoreDiffRule.Diff(expected, actual);

        Assert.Equal(new StoreDiffRule.Delta(UpgradedSaber, StoreOpKind.Add, 1), Assert.Single(deltas));
    }

    /// <summary>
    /// The diff rule needs no store-aware partitioning of its own — a <see cref="StoreStackKey"/>
    /// already carries the store id, so two different stores' entries in the SAME merged map
    /// never collide as keys (docs/research/store-sync.md §2's remark on the diff rule).
    /// </summary>
    [Fact]
    public void EntriesFromDifferentStoresInOneMergedDiffNeverCollide()
    {
        StoreStackKey elsewhere = Blaster with { StoreObjectId = AnotherStore };
        Dictionary<StoreStackKey, int> expected = new() { [Blaster] = 5, [elsewhere] = 2 };
        Dictionary<StoreStackKey, int> actual = new() { [Blaster] = 5, [elsewhere] = 9 };

        List<StoreDiffRule.Delta> deltas = StoreDiffRule.Diff(expected, actual);

        Assert.Equal(new StoreDiffRule.Delta(elsewhere, StoreOpKind.Add, 7), Assert.Single(deltas));
    }
}

using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class GroundItemStoreTests
{
    private static GroundItem Item(uint id, string resref = "g_w_lghtsbr01") =>
        new(id, resref, "end_m01aa", new Vector3f(10f, 20f, 1f));

    /// <summary>A stack of twenty, upgraded, identified — the state a blueprint cannot carry.</summary>
    private static ItemIdentity Upgraded =>
        new(20, 7, 50, 0b101, ItemIdentity.FlagKnown | ItemIdentity.FlagIdentified);

    private static GroundItem KnownItem(uint id) => Item(id) with { Identity = Upgraded };

    [Fact]
    public void DropAndRemoveBumpTheVersion()
    {
        GroundItemStore store = new();
        Assert.Equal(0u, store.Version);

        Assert.True(store.Drop(Item(1)));
        Assert.Equal(1u, store.Version);
        Assert.Equal(1, store.Count);

        Assert.True(store.Remove(1));
        Assert.Equal(2u, store.Version);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void DuplicateDropAndMissingRemoveDoNothing()
    {
        GroundItemStore store = new();
        store.Drop(Item(1));

        Assert.False(store.Drop(Item(1)));
        Assert.False(store.Remove(99));
        Assert.Equal(1u, store.Version);
    }

    [Fact]
    public void RetransmittedOpAppliesExactlyOnce()
    {
        GroundItemStore store = new();
        ItemOpPacket op = new(2, 5, ItemOpKind.Drop, Item(0x20001));

        Assert.True(store.ApplyOp(op));
        Assert.False(store.ApplyOp(op));
        Assert.Equal(1, store.Count);
        Assert.Equal(1u, store.Version);
    }

    [Fact]
    public void RemoveOpTakesTheItemOffTheGround()
    {
        GroundItemStore store = new();
        store.ApplyOp(new ItemOpPacket(2, 1, ItemOpKind.Drop, Item(0x20001)));

        GroundItem tombstone = new(0x20001, string.Empty, string.Empty, Vector3f.Zero);
        Assert.True(store.ApplyOp(new ItemOpPacket(3, 1, ItemOpKind.Remove, tombstone)));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void ReplaceAllAdoptsTheAuthoritativeSet()
    {
        GroundItemStore store = new();
        store.Drop(Item(1));

        store.ReplaceAll([Item(7), Item(8)], version: 12);

        Assert.Equal(12u, store.Version);
        Assert.Equal(2, store.Count);
        Assert.False(store.TryGet(1, out _));
        Assert.True(store.TryGet(7, out _));
    }

    /// <summary>
    /// Identity merges UPWARD and only upward. An instance that heard about the item before
    /// anyone could read it (a harness drop, a snapshot) is repaired when the real reading
    /// arrives; a stock re-announcement afterwards can never strip the upgrades back off.
    /// </summary>
    [Fact]
    public void AKnownIdentityUpgradesAnUnknownOneAndNeverTheReverse()
    {
        GroundItemStore store = new();

        Assert.True(store.Drop(Item(1)));
        Assert.True(store.Drop(KnownItem(1)), "learning the identity is a real change");
        Assert.Equal(2u, store.Version);
        Assert.Equal(1, store.Count);

        store.TryGet(1, out GroundItem? learned);
        Assert.Equal(Upgraded, learned!.Identity);

        // The blueprint-only announcement arrives again (retransmit, or a peer that could
        // not read the item). It must change nothing.
        Assert.False(store.Drop(Item(1)));
        Assert.False(store.Drop(KnownItem(1)), "the identity is already known");
        Assert.Equal(2u, store.Version);

        store.TryGet(1, out GroundItem? still);
        Assert.Equal(Upgraded, still!.Identity);
    }

    /// <summary>
    /// The host decides which items are on the ground; it does not get to FORGET an identity
    /// it never knew. Without this the drop op and the snapshot that follows it would take
    /// the upgrades straight back off.
    /// </summary>
    [Fact]
    public void AHostSnapshotCannotForgetAKnownIdentity()
    {
        GroundItemStore store = new();
        store.Drop(KnownItem(7));

        store.ReplaceAll([Item(7), Item(8)], version: 12);

        Assert.Equal(12u, store.Version);
        store.TryGet(7, out GroundItem? kept);
        Assert.Equal(Upgraded, kept!.Identity);

        store.TryGet(8, out GroundItem? fresh);
        Assert.False(fresh!.Identity.IsKnown);
    }

    /// <summary>An authoritative snapshot that DOES carry an identity replaces whatever was
    /// held locally — the merge is "unknown loses", not "first wins".</summary>
    [Fact]
    public void AHostSnapshotWithAnIdentityWins()
    {
        GroundItemStore store = new();
        store.Drop(Item(7));

        store.ReplaceAll([KnownItem(7)], version: 3);

        store.TryGet(7, out GroundItem? item);
        Assert.Equal(Upgraded, item!.Identity);
    }

    /// <summary>Learning an identity must not smuggle in a new position or resref: the
    /// op that already placed the item is the one that decided where it is.</summary>
    [Fact]
    public void LearningAnIdentityKeepsTheOriginalPlacement()
    {
        GroundItemStore store = new();
        store.Drop(Item(1, "g_w_lghtsbr01"));

        store.Drop(new GroundItem(
            1, "g_i_medeqpmnt01", "danm13", new Vector3f(-99f, -99f, -99f), Upgraded));

        store.TryGet(1, out GroundItem? item);
        Assert.Equal("g_w_lghtsbr01", item!.Resref);
        Assert.Equal("end_m01aa", item.Module);
        Assert.Equal(10f, item.Position.X);
        Assert.Equal(Upgraded, item.Identity);
    }

    [Fact]
    public void TheGroundHasACapacityLimit()
    {
        GroundItemStore store = new();

        for (uint i = 1; i <= ProtocolConstants.MaxGroundItems; i++)
        {
            Assert.True(store.Drop(Item(i)));
        }

        Assert.False(store.Drop(Item(1000)));
        Assert.Equal(ProtocolConstants.MaxGroundItems, store.Count);
    }

    // ---- digest -----------------------------------------------------------------

    [Fact]
    public void DigestIsInvariantToInsertionOrder()
    {
        GroundItemStore forward = new();
        forward.Drop(Item(1));
        forward.Drop(KnownItem(2));
        forward.Drop(Item(3));

        GroundItemStore backward = new();
        backward.Drop(Item(3));
        backward.Drop(KnownItem(2));
        backward.Drop(Item(1));

        Assert.Equal(forward.Version, backward.Version);
        Assert.Equal(forward.Digest, backward.Digest);
    }

    /// <summary>
    /// The reason the digest exists at all: two stores that reached the same VERSION by
    /// making the same number of changes, but different ones, must disagree on the digest —
    /// the tie a bare counter cannot see (docs/research/disconnect-reconnect.md §4.1).
    /// </summary>
    [Fact]
    public void EqualVersionWithDifferentContentProducesADifferentDigest()
    {
        GroundItemStore a = new();
        a.Drop(Item(1));
        a.Drop(Item(2));

        GroundItemStore b = new();
        b.Drop(Item(1));
        b.Drop(Item(3));

        Assert.Equal(a.Version, b.Version);
        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Fact]
    public void LearningAnIdentityChangesTheDigest()
    {
        // The digest hashes the identity fields, not just the item id, so upgrading a stock
        // item's identity (see AKnownIdentityUpgradesAnUnknownOneAndNeverTheReverse) is
        // visible here too.
        GroundItemStore store = new();
        store.Drop(Item(1));
        ulong beforeLearning = store.Digest;

        store.Drop(KnownItem(1));

        Assert.NotEqual(beforeLearning, store.Digest);
    }

    [Fact]
    public void RemoveXorsTheDigestBackToWhatItWasBefore()
    {
        GroundItemStore store = new();
        ulong empty = store.Digest;

        store.Drop(Item(1));
        Assert.NotEqual(empty, store.Digest);

        store.Remove(1);
        Assert.Equal(empty, store.Digest);
    }

    [Fact]
    public void ReplaceAllRecomputesTheDigest()
    {
        GroundItemStore store = new();
        store.Drop(Item(1));
        store.Drop(Item(2));

        store.ReplaceAll([Item(7)], version: 5);

        GroundItemStore direct = new();
        direct.Drop(Item(7));

        Assert.Equal(direct.Digest, store.Digest);
    }
    [Fact]
    public void PickedUpItemCannotBeResurrectedAfterOperationCacheEviction()
    {
        GroundItemStore store = new();
        ItemOpPacket oldDrop = new(2, 1, ItemOpKind.Drop, Item(0x20001));
        Assert.True(store.ApplyOp(oldDrop));
        Assert.True(store.ApplyOp(new(3, 1, ItemOpKind.Remove, Item(0x20001))));
        // Evict the old drop's de-duplication key without dropping any real item again.
        for (uint sequence=2; sequence<4100; sequence++)
            store.ApplyOp(new(4, sequence, ItemOpKind.Remove, Item(0x40000 + sequence)));
        uint before = store.Version;
        Assert.False(store.ApplyOp(oldDrop));
        Assert.False(store.TryGet(oldDrop.Item.ItemId, out _));
        Assert.Equal(before, store.Version);
        Assert.True(store.ApplyOp(new(2, 2, ItemOpKind.Drop, Item(0x20002))));
    }

    [Fact]
    public void PickupArrivingBeforeItsOriginalDropStillWins()
    {
        GroundItemStore store = new();
        Assert.False(store.ApplyOp(new(3, 1, ItemOpKind.Remove, Item(0x20001))));
        Assert.False(store.ApplyOp(new(2, 1, ItemOpKind.Drop, Item(0x20001))));
        Assert.Empty(store.Snapshot());
    }

    [Fact]
    public void StaleSnapshotCannotRecreateLocallyPickedUpItem()
    {
        GroundItemStore store = new();
        Assert.True(store.Drop(Item(0x20001)));
        Assert.True(store.Remove(0x20001));
        store.ReplaceAll([Item(0x20001), Item(0x20002)], 10);
        Assert.False(store.TryGet(0x20001, out _));
        Assert.True(store.TryGet(0x20002, out _));
    }

    [Fact]
    public void ReplacementHostNamespaceCanReuseIdsAndOperations()
    {
        GroundItemStore store = new();
        ItemOpPacket drop = new(2, 1, ItemOpKind.Drop, Item(0x20001));
        Assert.True(store.ApplyOp(drop));
        Assert.True(store.Remove(0x20001));
        store.ResetNamespace();
        Assert.Equal(0u, store.Version);
        Assert.True(store.ApplyOp(drop));
        Assert.True(store.TryGet(0x20001, out _));
    }
}

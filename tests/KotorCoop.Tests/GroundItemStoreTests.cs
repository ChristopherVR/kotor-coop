using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class GroundItemStoreTests
{
    private static GroundItem Item(uint id, string resref = "g_w_lghtsbr01") =>
        new(id, resref, "end_m01aa", new Vector3f(10f, 20f, 1f));

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
}

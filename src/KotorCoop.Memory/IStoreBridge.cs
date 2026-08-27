// The merchant/store stock channel: reading whichever CSWSStore a local player currently has
// open, through the CGuiInGame+0x84 GUI-open chain. See docs/research/store-sync.md §1.3/§2.3.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IStoreBridge
{
    /// <summary>False when this bridge cannot see the in-game GUI (no external memory access,
    /// or the game profile has no offsets for it).</summary>
    bool SupportsStore => false;

    /// <summary>
    /// A fresh read of the CURRENTLY OPEN store's contents, if any local player has one open,
    /// aggregated by <see cref="StoreStackKey"/> (which already names the store — see that
    /// type's own remarks). False when no store GUI is open, there is no world loaded, or the
    /// read otherwise failed — the caller must not treat that as "the store is empty"; a caller
    /// wanting to know "which store, if any" should check <paramref name="storeObjectId"/> only
    /// when this returns true.
    ///
    /// Items whose Infinite bit is set (docs/research/store-sync.md §1.5 — item flags
    /// <c>+0x288</c> bit 2) are EXCLUDED from the totals: the engine's own <c>SellItem</c>/
    /// <c>AcquireItem</c> never change an infinite entry's stock in either direction, so there is
    /// never a delta to replicate for one.
    ///
    /// Pure read, throttled internally the same way <c>PartyPool</c>'s own sweep is — a full walk
    /// of every stock item is not free, and the GUI is not going anywhere between two ticks a
    /// human is looking at it.
    /// </summary>
    bool TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        storeObjectId = 0;
        totals = new Dictionary<StoreStackKey, int>();
        return false;
    }
}

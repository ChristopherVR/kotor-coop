// The shared party item pool: reading CSWPartyTable+0x118's CItemRepository. See
// docs/research/party-pool-sync.md.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface IPartyPoolBridge
{
    /// <summary>False when this bridge cannot see the party table (no external memory access,
    /// or the game profile has no offsets for it).</summary>
    bool SupportsPartyPool => false;

    /// <summary>
    /// A fresh read of the party pool's contents, aggregated by <see cref="PartyPoolStackKey"/>
    /// (everything about a stack except its quantity). False when there is no world loaded yet
    /// (main menu, loading screen) or the read otherwise failed — the caller must not treat
    /// that as "the pool is empty".
    ///
    /// Pure read, throttled internally the same way <c>CollectContainerLootRemovals</c> is (a
    /// full sweep of every live item is not free) — a caller on a fast pump path gets the last
    /// good scan rather than a fresh one every tick.
    /// </summary>
    bool TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        totals = new Dictionary<PartyPoolStackKey, int>();
        return false;
    }
}

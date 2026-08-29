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

    /// <summary>
    /// Forces the NEXT <see cref="TryScanPartyPoolTotals"/> call to perform a genuine engine
    /// read rather than serving whatever the internal throttle cached — called by
    /// <c>Pumps.PartyPoolPump</c> the moment it either applies (queues a reconcile) or detects
    /// (publishes a step-1 local change) anything on this channel, so a second rapid change to
    /// the SAME key is never masked behind the REST of an already-stale scan window (see
    /// docs/research/party-pool-sync.md §14). A scan this cheap (a linked-list walk over a few
    /// dozen items) can afford to run more often than its throttle nominally allows whenever the
    /// pump has a concrete reason to believe something just moved.
    ///
    /// Default no-op: a bridge with no scan cache of its own (a test fake with no throttling, or
    /// one that has not implemented party-pool support at all) has nothing to invalidate.
    /// </summary>
    void InvalidatePartyPoolScanCache()
    {
    }

    /// <summary>
    /// A counter this bridge bumps whenever it drops per-object bookkeeping the reported totals
    /// DEPEND on — today exactly one thing does that: a module change clearing the ambient-noise
    /// baseline (<c>KotorGameBridge.PartyPool.cs</c>'s <c>ClearPartyPoolSuppressionCache</c>),
    /// after which the same unchanged pool can legitimately report LARGER totals than it did the
    /// tick before.
    ///
    /// A consumer that reads totals as ABSOLUTE values can ignore this entirely. A consumer that
    /// reads them as a DELTA against its own previous observation (<c>Pumps.PartyPoolPumpV2</c>'s
    /// belief-delta detect step) must not read that step as engine movement — it re-seeds
    /// instead. Constant for a bridge with no such bookkeeping (every test fake, and any bridge
    /// with no plugin attached, where nothing is ever suppressed in the first place).
    /// </summary>
    int PartyPoolScanEpoch => 0;

    /// <summary>
    /// True when the totals the most recent <see cref="TryScanPartyPoolTotals"/> handed back
    /// were a REPEAT of an older sweep rather than anything read this tick — the "stale but
    /// honest" answer every failure path gives (no world loaded, an unreadable pointer chain,
    /// or a walk that could not resolve every id and so is not evidence about the whole
    /// repository — see <c>ItemAggregateScanRule</c>).
    ///
    /// ⚠ It returned <c>true</c> alongside those totals, and for a DELTA consumer that is
    /// harmless in itself: an unchanged value moves nothing and nothing is published. What it is
    /// NOT harmless for is any clock that measures "how long has an apply I queued gone
    /// unobserved?" — because during a blind window nothing CAN be observed, so such a clock is
    /// timing the blindness, not the apply. That is not hypothetical: a freshly created item's
    /// id cannot be resolved to a resref until the item-tag cache is rebuilt, which
    /// <c>EnsureItemKnown</c> refuses to do for 15s after the last rebuild, while
    /// <c>PartyPoolPumpV2.DefaultExpectedTimeoutMs</c> is 10s. A reconcile apply that landed
    /// correctly was therefore "presumed resolved", re-queued, and applied a SECOND time —
    /// watched happen 20 seconds into a live session (docs/research/shared-inventory-v2.md §15).
    ///
    /// A consumer that reads totals as absolute values may ignore this. A consumer holding
    /// expiry timers must slide them, and must not reconcile against a value nobody read.
    /// False for a bridge that never serves stale totals (every test fake).
    /// </summary>
    bool LastPartyPoolScanWasStale => false;
}

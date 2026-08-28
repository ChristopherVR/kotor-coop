// The merchant/store stock channel (docs/research/store-sync.md).
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public interface IStoreSession
{
    /// <summary>
    /// Announces that <paramref name="countDelta"/> units of <paramref name="key"/> (which
    /// names the store) were added to (a sell) or removed from (a buy) that store's stock,
    /// delivered reliably to every OTHER peer so their copy of that store converges. Symmetric:
    /// a client calls this exactly like the host does — see <see cref="IPartyPoolSession
    /// .PublishPartyPoolOp"/> for the identical wire-layer echo-suppression this mirrors.
    ///
    /// <paramref name="localBaselineIfUnknown"/> (2026-08-28 fix, docs/research/store-sync.md):
    /// the local engine total THIS delta was computed against — <c>Pumps.StorePump</c>'s own step
    /// 1 always knows this (it is the "before" side of the very diff that produced
    /// <paramref name="countDelta"/>). Threaded straight through to
    /// <see cref="StoreStore.Record"/>'s identically-named parameter — see that member's own
    /// remarks for why a store's pristine, never-transacted stock needs this the first time any
    /// key in it is ever recorded.
    /// </summary>
    void PublishStoreOp(StoreOpKind kind, StoreStackKey key, uint countDelta, int localBaselineIfUnknown = 0) { }

    /// <summary>Every (store, stack) key this instance currently believes holds a NONZERO
    /// quantity, across every store this session has ever learned about — this peer's own
    /// changes plus every other peer's, merged. A key this session KNOWS about but that is
    /// currently zero (a legitimate full depletion) is absent here by design; see
    /// <see cref="StoreKnownKeys"/> for the set that also covers that case. NOT consulted by
    /// <c>Pumps.StorePump</c> for applying anything any more (2026-08-28 redesign,
    /// docs/research/store-sync.md — applies are now driven by <see cref="DrainStoreOps"/>
    /// directly); kept as a read-only "what does this session currently believe" surface for
    /// tests and any future display/debug use.</summary>
    IReadOnlyDictionary<StoreStackKey, int> StoreTotals => EmptyStoreTotals.Instance;

    /// <summary>Every (store, stack) key this session has EVER recorded a real Add/Remove/Set op
    /// for — from this instance's own local detections, from a peer's ops, or from a resync —
    /// including one currently at zero (absent from <see cref="StoreTotals"/>). A key absent from
    /// THIS set is unknown, not zero — never inferred to be zero from silence. See
    /// <see cref="StoreStore.KnownKeys"/> for the single source of truth both
    /// <c>CoopHost</c>/<c>CoopClient</c> mirror this from, and <see cref="StoreStore.SnapshotKnown"/>
    /// for the resync payload this set's own quantities feed.</summary>
    IReadOnlySet<StoreStackKey> StoreKnownKeys => EmptyStoreKnownKeys.Instance;

    /// <summary>Every store op this instance's OWN <c>ApplyOp</c> call has genuinely accepted
    /// since the last drain, oldest first — i.e. every REMOTE fact (a peer's buy/sell, or a
    /// host's resync <see cref="StoreOpKind.Set"/>) this session's wire layer has taken as true.
    /// Deliberately EXCLUDES anything this instance itself published: <c>CoopHost</c>/
    /// <c>CoopClient</c> only enqueue here from inside their own <c>ApplyOp</c> success path,
    /// which — via the origin check in <c>CoopClient.HandleStoreOp</c> and the simple fact that a
    /// host never re-sends a client's own op back as a fresh <c>StoreOp</c> — never runs for a
    /// self-originated change (see those types' own remarks). <c>Pumps.StorePump</c> drains this
    /// every tick and applies each op DIRECTLY to the local engine — see that type's own class doc
    /// for the full design (docs/research/store-sync.md's 2026-08-28 redesign).</summary>
    IReadOnlyList<StoreOpPacket> DrainStoreOps() => [];

    /// <summary>
    /// Requests an authoritative resync of this channel from scratch. Used exactly once by
    /// <c>Pumps.StorePump</c>'s own loop breaker (docs/research/store-sync.md's 2026-08-29
    /// "engineBelief/wireTarget" redesign): when a single key's op rate exceeds the runaway
    /// threshold, the pump freezes that key rather than keep reacting to what might be a
    /// feedback loop, and asks for the merged truth instead of trying to reason its way out
    /// incrementally. A client sends a fresh sync request immediately (bypassing the periodic
    /// digest tripwire); the host, already authoritative, just re-broadcasts its own current
    /// knowledge, since there is nobody else for it to ask.
    /// </summary>
    void RequestStoreResync() { }

    // ---- v2: host-authoritative absolute snapshot + client intents (store_sync=2) -----------
    // See docs/research/shared-inventory-v2.md. Mirrors IPartyPoolSession's own v2 members
    // exactly, widened by the store's own object id (carried inside StoreStackKey, unchanged).

    /// <summary>Host only: merges a fresh scan of ONE store (<paramref name="storeObjectId"/>,
    /// carried explicitly rather than inferred from <paramref name="totals"/>'s keys so a store
    /// that scanned genuinely EMPTY still clears its own stale belief — an empty dictionary alone
    /// cannot say which store it was empty FOR) into the host's running cross-store belief. See
    /// docs/research/shared-inventory-v2.md §2.4/§2.6. A client's own call is a no-op.</summary>
    void PublishStoreSnapshotV2(uint storeObjectId, IReadOnlyDictionary<StoreStackKey, int> totals) { }

    /// <summary>The latest FULLY-gathered publish cycle's absolute totals; false until the first
    /// complete cycle has arrived — see <see cref="IPartyPoolSession.TryGetPartyPoolSnapshotV2"/>'s
    /// identical remarks. Always false on the host.</summary>
    bool TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        totals = EmptyStoreTotals.Instance;
        return false;
    }

    /// <summary>Client only: publishes one bounded REQUEST ("I bought/sold this many in this
    /// store"), returning the serial minted for it, or 0 if refused outright. See
    /// <see cref="IPartyPoolSession.PublishPartyPoolIntentV2"/>'s identical remarks.</summary>
    uint PublishStoreIntentV2(StoreOpKind kind, StoreStackKey key, ushort count) => 0;

    /// <summary>
    /// Client only: has this session ever received, from the host's own absolute snapshot, at
    /// least one entry naming <paramref name="storeObjectId"/> — i.e. has the HOST actually
    /// scanned this store at least once this session (docs/research/shared-inventory-v2.md §2.7's
    /// "unknown vs. zero" distinction, closed 2026-08-29 §7: a store the host has NEVER opened is
    /// absent from every snapshot entirely, indistinguishable from "scanned and currently empty"
    /// without this). A store never named by the host is UNKNOWN, not zero, and
    /// <c>Pumps.StorePumpV2</c>'s client branch must never treat a local scan of an unknown
    /// store's stock as evidence of anything — see that type's own gate. Always false on the
    /// host (there is nothing for it to ask itself — <see cref="CoopHost.IsStoreKnownV2"/>
    /// overrides with its own real answer purely for interface symmetry and tests). Once true for
    /// a given id, stays true for the rest of the session (mirrors §2.7's own "once known, stays
    /// known" precedent) — never reset by a later empty cycle, since the host's OWN snapshot
    /// dropping every one of a store's entries is a legitimate, ordinary depletion, not proof the
    /// store became unknown again.
    /// </summary>
    bool IsStoreKnownV2(uint storeObjectId) => false;

    /// <summary>Host only: every intent this instance's wire layer has accepted since the last
    /// drain, oldest first — see <see cref="IPartyPoolSession.DrainPartyPoolIntentsV2"/>'s
    /// identical remarks.</summary>
    IReadOnlyList<StoreIntentPacketV2> DrainStoreIntentsV2() => [];

    /// <summary>Client only: every intent serial acked OR refused since the last drain, oldest
    /// first — see <see cref="IPartyPoolSession.DrainAckedPartyPoolIntentsV2"/>'s identical
    /// remarks.</summary>
    IReadOnlyList<uint> DrainAckedStoreIntentsV2() => [];
}

/// <summary>A reusable empty dictionary singleton for the default interface member above, so a
/// bridge/session that never overrides <see cref="IStoreSession.StoreTotals"/> does not allocate
/// one on every call.</summary>
internal static class EmptyStoreTotals
{
    public static readonly Dictionary<StoreStackKey, int> Instance = [];
}

/// <summary>A reusable empty set singleton for <see cref="IStoreSession.StoreKnownKeys"/>'s
/// default member, matching <see cref="EmptyStoreTotals"/>'s own reasoning.</summary>
internal static class EmptyStoreKnownKeys
{
    public static readonly HashSet<StoreStackKey> Instance = [];
}

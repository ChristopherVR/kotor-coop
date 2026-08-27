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

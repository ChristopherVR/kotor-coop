using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The merchant/store stock channel (<c>CSWSStore+0x230</c>). Same two-job, detect-then-
/// reconcile shape as <see cref="PartyPoolPump"/>, driven by <see cref="StoreDiffRule.Diff"/>,
/// with one structural difference: the party pool is reachable by a single fixed pointer chain
/// at any time, but a store's stock can only be READ while a local player has that store's GUI
/// open (docs/research/store-sync.md §2.3/§2.6 — no per-area store enumeration was found). So:
///
/// 1. <b>Detect.</b> Only possible for whichever store is open THIS tick. Diffs a fresh scan of
///    that one store against the LAST scan this pump took of it (skipping any key with an apply
///    already in flight) to find a genuine local buy/sell and publish it. When no store is open,
///    or a DIFFERENT store is open than last tick, step 1 does nothing for the store that just
///    closed — its last scan already reflected every change made while it was open, so there is
///    nothing left to detect on close (see the type's own remarks in docs/research/store-sync.md
///    §2.6 for why "diff on close" needs no separate step).
/// 2. <b>Reconcile.</b> NOT gated on any store being open — <c>Odyssey.Store.cs</c>'s apply path
///    resolves a store by object id directly (docs/research/store-sync.md §1.2/§2.4), so a peer's
///    buy/sell can land on this instance's engine at any time, for ANY store this instance has
///    ever learned an id for (from its own opens, or from a peer's op naming it on the wire).
///    Reconciles every key in <see cref="ICoopSession.StoreTotals"/> against this pump's own
///    per-store "last known engine total" bookkeeping (§ below), which is populated the moment a
///    store is first opened locally OR the moment a peer names it and stays populated afterward —
///    never cleared just because the GUI closed.
///
/// Echo suppression: identical two layers to <see cref="PartyPoolPump"/> — wire layer in
/// <c>CoopClient.HandleStoreOp</c>'s origin check, engine layer here via
/// <c>_pendingApplyTarget</c>, tracking the engine total a queued apply should PRODUCE so the
/// very next scan does not misread the pump's own application as a fresh local change.
/// </summary>
internal sealed class StorePump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const int MaxPendingApplies = 256;

    private readonly Queue<CoopAgent.PendingStoreApply> _pendingApplies = new();

    /// <summary>This pump's own belief of every store's stock it has ever learned about — seeded
    /// on a store's first local open (baseline, no diff) and kept up to date afterward by every
    /// subsequent open's scans. This is the "engine" side of BOTH steps: step 1 diffs a fresh
    /// open-store scan against the slice of this dictionary for that one store; step 2 diffs the
    /// WHOLE dictionary against <see cref="ICoopSession.StoreTotals"/>.</summary>
    private readonly Dictionary<StoreStackKey, int> _knownEngineTotals = [];

    /// <summary>Which store id each key in <see cref="_knownEngineTotals"/> belongs to — needed
    /// only to scope step 1's diff to the currently-open store without re-deriving it from every
    /// key (a <see cref="StoreStackKey"/> already carries its store id, but a dictionary keyed on
    /// the whole struct cannot be sliced by a leading field alone).</summary>
    private uint _openStoreId;

    private bool _openStoreBaselined;

    /// <summary>Keys with an apply already queued, mapped to the engine total that apply is
    /// expected to produce once it lands. Excluded from both steps until a scan confirms it.</summary>
    private readonly Dictionary<StoreStackKey, int> _pendingApplyTarget = [];

    public void Pump(SessionCapabilities caps)
    {
        if (!bridge.SupportsStore || (caps & SessionCapabilities.Store) == 0)
        {
            return;
        }

        // Resolve applies that have landed, exactly like PartyPoolPump.
        if (_pendingApplyTarget.Count > 0)
        {
            List<StoreStackKey>? landed = null;
            foreach ((StoreStackKey key, int target) in _pendingApplyTarget)
            {
                if (_knownEngineTotals.GetValueOrDefault(key) == target)
                {
                    (landed ??= []).Add(key);
                }
            }

            if (landed is not null)
            {
                foreach (StoreStackKey key in landed)
                {
                    _pendingApplyTarget.Remove(key);
                }
            }
        }

        // ---- step 1: detect a genuine local buy/sell, only for the store open THIS tick -----
        if (bridge.TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> openStoreScan))
        {
            if (storeObjectId != _openStoreId)
            {
                // A different store opened (or the first one this session) — this pump has no
                // fresh scan of it yet to diff against, so seed one silently. If this store was
                // seen before (a peer already named it, or we opened it earlier this session),
                // _knownEngineTotals may already hold stale entries for it; TrySeed below folds
                // the fresh scan in as the new truth for exactly this store's keys.
                _openStoreId = storeObjectId;
                _openStoreBaselined = false;
            }

            Dictionary<StoreStackKey, int> priorForThisStore = SliceByStore(_knownEngineTotals, storeObjectId);

            if (!_openStoreBaselined)
            {
                ReplaceStore(_knownEngineTotals, storeObjectId, openStoreScan);
                _openStoreBaselined = true;
            }
            else
            {
                Dictionary<StoreStackKey, int> priorForDetect = Without(priorForThisStore, _pendingApplyTarget.Keys);
                Dictionary<StoreStackKey, int> scanForDetect = Without(openStoreScan, _pendingApplyTarget.Keys);

                foreach (StoreDiffRule.Delta delta in StoreDiffRule.Diff(priorForDetect, scanForDetect))
                {
                    log($"store: local {delta.Kind.ToString().ToLowerInvariant()} of {delta.Count}x "
                        + $"'{delta.Key.Resref}' detected in store 0x{storeObjectId:X}, publishing");
                    session.PublishStoreOp(delta.Kind, delta.Key, delta.Count);
                }

                ReplaceStore(_knownEngineTotals, storeObjectId, openStoreScan);
            }
        }
        else if (_openStoreId != 0)
        {
            // No store open now, but one was last tick — nothing further to detect for it (its
            // last scan already captured everything; see the type's own remarks). Just stop
            // tracking "the open store" — _knownEngineTotals keeps whatever that store's last
            // scan said, for step 2 to keep reconciling against.
            _openStoreId = 0;
            _openStoreBaselined = false;
        }

        // ---- step 2: reconcile every known store toward the merged truth, unconditionally ---
        IReadOnlyDictionary<StoreStackKey, int> desired = session.StoreTotals;
        Dictionary<StoreStackKey, int> desiredForReconcile = Without(desired, _pendingApplyTarget.Keys);
        Dictionary<StoreStackKey, int> engineForReconcile = Without(_knownEngineTotals, _pendingApplyTarget.Keys);

        // Diff(expected: engine, actual: desired) — an Add delta means desired > engine (this
        // instance owes that store that many more units), a Remove delta means desired < engine.
        foreach (StoreDiffRule.Delta delta in StoreDiffRule.Diff(engineForReconcile, desiredForReconcile))
        {
            if (_pendingApplies.Count >= MaxPendingApplies)
            {
                log($"store: dropping apply for '{delta.Key.Resref}' in store 0x{delta.Key.StoreObjectId:X}"
                    + $" — already holding {MaxPendingApplies} queued, the drain has fallen behind");
                continue;
            }

            _pendingApplies.Enqueue(
                new CoopAgent.PendingStoreApply(delta.Kind, delta.Key, (ushort)delta.Count));
            int target = desired.GetValueOrDefault(delta.Key);
            _pendingApplyTarget[delta.Key] = target;

            // This is also this pump's own best guess of what the store will hold once the
            // apply lands — updated optimistically so step 1 does not report a stale total for
            // this key against a LATER open of the same store before the apply has actually run
            // (the pending-apply-target skip already excludes it from step 1's diff either way).
            if (target > 0)
            {
                _knownEngineTotals[delta.Key] = target;
            }
            else
            {
                _knownEngineTotals.Remove(delta.Key);
            }
        }
    }

    /// <summary>Called by whatever already arbitrates the single-slot staging-cheat channel, at
    /// most once per ITS tick — mirrors <c>PartyPoolPump.TryDequeuePendingApply</c> exactly.</summary>
    public bool TryDequeuePendingApply(out CoopAgent.PendingStoreApply next) =>
        _pendingApplies.TryDequeue(out next);

    private static Dictionary<StoreStackKey, int> SliceByStore(
        IReadOnlyDictionary<StoreStackKey, int> source, uint storeObjectId)
    {
        Dictionary<StoreStackKey, int> result = [];
        foreach ((StoreStackKey key, int value) in source)
        {
            if (key.StoreObjectId == storeObjectId)
            {
                result[key] = value;
            }
        }

        return result;
    }

    /// <summary>Replaces every entry for <paramref name="storeObjectId"/> in
    /// <paramref name="target"/> with <paramref name="freshScan"/> — a store's own scan is
    /// always a complete snapshot of that store, so a key present before but absent now
    /// genuinely means "no longer in this store's stock".</summary>
    private static void ReplaceStore(
        Dictionary<StoreStackKey, int> target, uint storeObjectId,
        IReadOnlyDictionary<StoreStackKey, int> freshScan)
    {
        List<StoreStackKey>? stale = null;
        foreach (StoreStackKey key in target.Keys)
        {
            if (key.StoreObjectId == storeObjectId && !freshScan.ContainsKey(key))
            {
                (stale ??= []).Add(key);
            }
        }

        if (stale is not null)
        {
            foreach (StoreStackKey key in stale)
            {
                target.Remove(key);
            }
        }

        foreach ((StoreStackKey key, int value) in freshScan)
        {
            target[key] = value;
        }
    }

    private static Dictionary<StoreStackKey, int> Without(
        IReadOnlyDictionary<StoreStackKey, int> source, IReadOnlyCollection<StoreStackKey> excluded)
    {
        if (excluded.Count == 0)
        {
            return new Dictionary<StoreStackKey, int>(source);
        }

        Dictionary<StoreStackKey, int> result = [];
        foreach ((StoreStackKey key, int value) in source)
        {
            if (!excluded.Contains(key))
            {
                result[key] = value;
            }
        }

        return result;
    }
}

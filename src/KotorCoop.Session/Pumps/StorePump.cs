using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The merchant/store stock channel (<c>CSWSStore+0x230</c>). Same two-job, detect/apply shape as
/// <see cref="PartyPoolPump"/>, with two structural differences documented at length in
/// docs/research/store-sync.md (the 2026-08-28 "Bug 4" write-up and its follow-up fix):
///
/// 1. <b>Detect.</b> Only possible for whichever store is open THIS tick (a store's stock can only
///    be READ while a local player has that store's GUI open — §2.3/§2.6, no per-area store
///    enumeration was found). Diffs a fresh scan of that one store against the LAST scan this pump
///    took of it (skipping any key with an apply already in flight) to find a genuine local
///    buy/sell and publish it, ALONG WITH the local baseline value the delta was computed against
///    (<see cref="ICoopSession.PublishStoreOp"/>'s <c>localBaselineIfUnknown</c> — see that
///    member's own remarks for why: a store's PRISTINE, never-transacted stock is a real, nonzero
///    number nobody ever put on the wire, so the very first op ever recorded for a key needs this
///    hint to avoid computing against a false zero baseline). When no store is open, or a
///    DIFFERENT store is open than last tick, step 1 does nothing for the store that just closed —
///    its last scan already reflected every change made while it was open.
/// 2. <b>Apply.</b> Drains <see cref="ICoopSession.DrainStoreOps"/> — every REMOTE op this
///    session's wire layer has genuinely accepted since the last drain (never one this instance
///    itself published; <c>CoopHost</c>/<c>CoopClient</c> only enqueue there from their own
///    <c>ApplyOp</c> success, which is itself already origin-filtered — see those types' own
///    remarks) — and applies each one DIRECTLY:
///    - <see cref="StoreOpKind.Add"/>/<see cref="StoreOpKind.Remove"/>: the op's raw
///      <c>CountDelta</c> is applied AS A DELTA to whatever this instance's own engine currently
///      holds — never "set the engine to the wire's total" (docs/research/store-sync.md's design
///      rule (c)). This needs NO knowledge of the store's true pristine baseline at all: a pure
///      relative delta is correct regardless of whether this instance has ever locally scanned
///      the affected store.
///    - <see cref="StoreOpKind.Set"/> (resync only, docs/research/store-sync.md's design rule
///      (c)'s carve-out): an ABSOLUTE target. Applied ONLY if this pump already has an engine
///      belief for the key (from its own prior scan or a prior apply) — a Set for a key this
///      instance has NEVER locally observed is left alone rather than guessed at (forcing an
///      "Add[target]" out of nowhere would very likely be WRONG, since this instance's own true
///      pristine baseline for that key is unknown here — see docs/research/store-sync.md's own
///      "known limitation" note for this narrow case). Once THIS instance eventually scans that
///      store itself, its own first-open baseline adoption (§ below) picks up the truth directly.
///
/// Echo suppression, engine layer: <c>_pendingApplyTarget</c>, tracking the engine total a queued
/// apply should PRODUCE so the very next scan does not misread the pump's own application as a
/// fresh local change — unchanged in shape from before this pass, just fed by the apply path
/// above instead of a snapshot diff.
///
/// ⚠ BUG 4, THE VIRGIN-STORE WIPE, AND THE FLOORED-FIRST-REMOVE, BOTH FIXED THIS PASS
/// (2026-08-28, docs/research/store-sync.md's own write-up — read it for the full story). The
/// PRIOR shape of step 2 diffed this pump's ENTIRE local engine belief (including a store's
/// pristine, never-transacted stock) against <c>ICoopSession.StoreTotals</c> as if that
/// dictionary's silence always meant "the session desires zero of this key". It does not:
/// <c>StoreTotals</c> only ever holds a key someone has actually transacted — a virgin store's
/// stock is simply ABSENT, indistinguishable from "explicitly depleted to zero" by
/// <see cref="StoreDiffRule.Diff"/>'s own logic. Merely opening the same virgin store on two
/// <c>store_sync=1</c> instances, with zero buy/sell activity, was enough to have the old step 2
/// read every stocked key's absence from the (necessarily empty) wire truth as "remove the entire
/// quantity" — live-confirmed to empty a real store's stock on both sides. Investigating the fix
/// surfaced a SECOND, independent defect the snapshot-diffing shape had been masking: a Remove
/// (buy) as the very FIRST op ever recorded for a key floored against an unseeded zero baseline
/// (<c>before=0, after=max(0, 0-delta)=0</c>) and was silently swallowed — <c>StoreStore.Record</c>
/// reported "no change" and the op never even reached the wire. Both are closed by the redesign
/// above: detection now threads the true local baseline through to
/// <see cref="StoreStore.Record"/> (closing the floor), and application is driven by the RAW OPS
/// that actually reached the wire rather than by re-diffing two possibly-incomparable absolute
/// snapshots (closing the wipe, structurally — a store nobody has transacted in never produces an
/// op, so there is nothing for step 2 to ever apply).
/// </summary>
internal sealed class StorePump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const int MaxPendingApplies = 256;

    private readonly Queue<CoopAgent.PendingStoreApply> _pendingApplies = new();

    /// <summary>This pump's own belief of every store's stock it has ever learned about — seeded
    /// on a store's first local open (baseline, no diff) and kept up to date afterward by every
    /// subsequent open's scans, PLUS by every apply this pump itself has queued (updated
    /// optimistically the moment the apply is queued, not when it lands — see the echo-suppression
    /// remarks above). Step 1 diffs a fresh open-store scan against the slice of this dictionary
    /// for that one store; the apply step (above) consults it ONLY to decide whether a Set resync
    /// is safe to convert into a delta (a key absent here has no engine belief to compute against).</summary>
    private readonly Dictionary<StoreStackKey, int> _knownEngineTotals = [];

    private uint _openStoreId;

    private bool _openStoreBaselined;

    /// <summary>Keys with an apply already queued, mapped to the engine total that apply is
    /// expected to produce once it lands. Excluded from step 1's detection until a scan confirms
    /// it.</summary>
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
                // _knownEngineTotals may already hold stale entries for it; ReplaceStore below
                // folds the fresh scan in as the new truth for exactly this store's keys.
                _openStoreId = storeObjectId;
                _openStoreBaselined = false;
            }

            Dictionary<StoreStackKey, int> priorForThisStore = SliceByStore(_knownEngineTotals, storeObjectId);

            if (!_openStoreBaselined)
            {
                // Design rule (a): a store's first LOCAL read, with nothing to diff against yet,
                // publishes NOTHING — it simply becomes this pump's own baseline going forward.
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

                    // The true local baseline THIS delta was computed against — see
                    // PublishStoreOp's own remarks for why StoreStore.Record needs this the first
                    // time a key is ever recorded (design rule (a)/(c) — closes the "floored first
                    // Remove is silently swallowed" defect this pass also found).
                    int localBaseline = priorForDetect.GetValueOrDefault(delta.Key);
                    session.PublishStoreOp(delta.Kind, delta.Key, delta.Count, localBaseline);
                }

                ReplaceStore(_knownEngineTotals, storeObjectId, openStoreScan);
            }
        }
        else if (_openStoreId != 0)
        {
            // No store open now, but one was last tick — nothing further to detect for it (its
            // last scan already captured everything; see the type's own remarks). Just stop
            // tracking "the open store" — _knownEngineTotals keeps whatever that store's last
            // scan said.
            _openStoreId = 0;
            _openStoreBaselined = false;
        }

        // ---- step 2: apply every REMOTE op the wire layer has genuinely accepted -------------
        foreach (StoreOpPacket op in session.DrainStoreOps())
        {
            if (_pendingApplies.Count >= MaxPendingApplies)
            {
                log($"store: dropping apply for '{op.Key.Resref}' in store 0x{op.Key.StoreObjectId:X}"
                    + $" — already holding {MaxPendingApplies} queued, the drain has fallen behind");
                continue;
            }

            if (op.Kind == StoreOpKind.Set)
            {
                // Absolute resync target — only safe to convert into a delta against an engine
                // belief THIS pump already holds (own prior scan, or a prior apply's optimistic
                // update). A key with no entry here has never been locally observed by this
                // instance at all; guessing "Add[target] from nothing" would very likely be wrong
                // (this instance's own true pristine baseline is unknown to this Set) — left
                // alone, matching docs/research/store-sync.md's own documented open limitation for
                // this narrow case.
                if (!_knownEngineTotals.TryGetValue(op.Key, out int currentBelief))
                {
                    continue;
                }

                int target = op.CountDelta;
                int diff = target - currentBelief;
                if (diff == 0)
                {
                    continue;
                }

                StoreOpKind applyKind = diff > 0 ? StoreOpKind.Add : StoreOpKind.Remove;
                ushort applyCount = (ushort)Math.Min(Math.Abs(diff), ushort.MaxValue);

                _pendingApplies.Enqueue(new CoopAgent.PendingStoreApply(applyKind, op.Key, applyCount));
                SetEngineBelief(op.Key, target);
            }
            else
            {
                // Add/Remove: apply the RAW DELTA directly — never "set to wire total" (design
                // rule (c)). Correct regardless of whether this pump has ever scanned the
                // affected store: a relative delta needs no baseline knowledge at all.
                _pendingApplies.Enqueue(new CoopAgent.PendingStoreApply(op.Kind, op.Key, op.CountDelta));

                // Only update this pump's own engine-belief bookkeeping (and therefore only
                // arm the echo-suppression guard) for a key it already has SOME belief about —
                // see the class doc's own remarks: a store never locally scanned cannot be
                // mis-detected by step 1 anyway (step 1 only ever looks at the currently open
                // store), so there is nothing to guard for a key with no prior entry here.
                if (_knownEngineTotals.TryGetValue(op.Key, out int before))
                {
                    int after = op.Kind == StoreOpKind.Add
                        ? before + op.CountDelta
                        : Math.Max(0, before - op.CountDelta);
                    SetEngineBelief(op.Key, after);
                }
            }
        }
    }

    /// <summary>Called by whatever already arbitrates the single-slot staging-cheat channel, at
    /// most once per ITS tick — mirrors <c>PartyPoolPump.TryDequeuePendingApply</c> exactly.</summary>
    public bool TryDequeuePendingApply(out CoopAgent.PendingStoreApply next) =>
        _pendingApplies.TryDequeue(out next);

    /// <summary>Updates this pump's own optimistic belief of a key's engine total (after an apply
    /// has been QUEUED, not yet landed) and arms <see cref="_pendingApplyTarget"/> so step 1 does
    /// not misread the pump's own application as a fresh local change once it lands.</summary>
    private void SetEngineBelief(StoreStackKey key, int value)
    {
        if (value > 0)
        {
            _knownEngineTotals[key] = value;
        }
        else
        {
            _knownEngineTotals.Remove(key);
        }

        _pendingApplyTarget[key] = value;
    }

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

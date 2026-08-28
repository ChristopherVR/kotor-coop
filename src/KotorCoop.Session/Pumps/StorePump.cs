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
///    enumeration was found). Diffs a fresh scan of that one store against <see cref="_engineBelief"/>
///    (this pump's OWN belief of the store's contents, updated ONLY from real scans — see that
///    field's own remarks) to find a genuine local buy/sell and publish it, ALONG WITH the local
///    baseline value the delta was computed against (<see cref="ICoopSession.PublishStoreOp"/>'s
///    <c>localBaselineIfUnknown</c> — see that member's own remarks for why: a store's PRISTINE,
///    never-transacted stock is a real, nonzero number nobody ever put on the wire, so the very
///    first op ever recorded for a key needs this hint to avoid computing against a false zero
///    baseline). When no store is open, or a DIFFERENT store is open than last tick, step 1 does
///    nothing for the store that just closed — its last scan already reflected every change made
///    while it was open.
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
/// ⚠ ROOT CAUSE (2026-08-29, docs/research/store-sync.md's own write-up): the PRIOR shape of this
/// pump tracked a SINGLE dictionary (<c>_knownEngineTotals</c>) for two different jobs at once —
/// "what does a real scan say this key is" AND "what does this pump's own optimistic post-apply
/// belief say" — and set BOTH to the exact same value, at the exact same instant, the moment an
/// apply was QUEUED (not when it actually landed). A separate "has this apply landed?" check then
/// compared that SAME dictionary against the pending target it had JUST been set to match,
/// trivially true on the very next tick regardless of whether the queued apply had genuinely
/// executed in the real engine yet (that happens later, through the single-slot arbitrated cheat
/// channel, which can lag by more than one tick). This is the SAME over-exclusion class the party
/// pool's own root cause 3 fixed (docs/research/party-pool-sync.md §11-§12): a pending key's own
/// echo-suppression window closed before the thing it was protecting against had actually
/// happened, so a genuine local change to the SAME key that landed in the gap — the buy-back case,
/// docs/research/store-sync.md's own 2026-08-28 rig-round finding — either produced a spurious,
/// self-contradicting Remove/Add pair (if the real apply lagged behind the premature "landed"
/// clear) or, worse, got silently absorbed into what looked like the apply's own delayed landing
/// (if the local change and the real apply's landing were ever observed by the SAME fresh scan),
/// never separately detected or published.
///
/// <b>The fix: two dictionaries, one authority each.</b> <see cref="_engineBelief"/> is written
/// ONLY from a real fresh scan (<see cref="ReplaceStore"/>) — never optimistically. <see
/// cref="_wireTarget"/> is the value the WIRE currently expects a pending key to reach; it is
/// consulted, never trusted on its own, and is resolved ONLY by comparing it against the next real
/// scan of that key (exactly <see cref="PartyPoolPump"/>'s own step 1 loop): no movement since the
/// last real scan means the apply simply has not landed yet — keep waiting. Movement that matches
/// the target exactly is the apply landing cleanly — an echo, nothing to report, pending cleared.
/// Movement that does NOT match the target is treated as movement all the way to the target (the
/// apply's own already-accounted-for contribution) PLUS whatever is left over — publish exactly
/// the EXCESS beyond the target, then clear the pending entry either way: if the apply turns out
/// not to have actually landed yet, the very next tick's comparison (against the fresh
/// <see cref="_engineBelief"/> this tick's scan just wrote) starts clean, and step 2's own next
/// drain (if any) re-arms a fresh target from there. Nothing is ever permanently excluded: a
/// pending entry is resolved by the FIRST scan that disagrees with the pre-apply baseline, not by
/// a self-referential check that could never fail.
///
/// <b>Loop breaker.</b> A key that racks up more than <see cref="LoopBreakerMaxOpsPerWindow"/> ops
/// (published OR applied, either counts) within <see cref="LoopBreakerWindowMs"/> is presumed to be
/// caught in a feedback loop rather than genuinely being bought and sold that fast by a human —
/// this pump freezes it (skips publishing/applying anything further for it) for one more window,
/// logs once, and asks the session for an authoritative resync
/// (<see cref="ICoopSession.RequestStoreResync"/>) instead of continuing to reason about it
/// incrementally. <see cref="_engineBelief"/> keeps tracking the key's real scanned value the
/// whole time it is frozen (so the freeze does not desync THIS instance's own local read) — only
/// the wire-facing publish/apply actions are suppressed.
/// </summary>
internal sealed class StorePump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const int MaxPendingApplies = 256;

    private readonly Queue<CoopAgent.PendingStoreApply> _pendingApplies = new();

    /// <summary>This pump's own belief of every store's stock it has ever learned about — seeded
    /// on a store's first local open (baseline, no diff) and kept up to date afterward by every
    /// subsequent open's REAL scans only (<see cref="ReplaceStore"/>). Never written
    /// optimistically by the apply path any more — see the class remarks' root-cause section for
    /// why that used to defeat <see cref="_wireTarget"/>'s own purpose.</summary>
    private readonly Dictionary<StoreStackKey, int> _engineBelief = [];

    private uint _openStoreId;

    private bool _openStoreBaselined;

    /// <summary>Keys with an apply already queued (or in flight through the cheat channel),
    /// mapped to the engine total the WIRE currently expects that key to reach. Resolved only by
    /// comparing it against the next REAL scan of the key — see the class remarks.</summary>
    private readonly Dictionary<StoreStackKey, int> _wireTarget = [];

    /// <summary>A stack total above this threshold within one rolling window is presumed to be a
    /// feedback loop, not a human buying/selling that fast — see the class remarks' "loop
    /// breaker" section. Deliberately generous: this pump's own heavy-pump cadence is ~1 Hz, so 10
    /// genuine, distinct transactions on the SAME key inside one 10s window is already an unusual
    /// amount of real play to rule out before assuming a bug.</summary>
    private const int LoopBreakerMaxOpsPerWindow = 10;

    private const long LoopBreakerWindowMs = 10_000;

    private readonly Dictionary<StoreStackKey, (long WindowStartMs, int Count)> _opRate = [];

    private readonly Dictionary<StoreStackKey, long> _frozenUntilMs = [];

    /// <summary>The local module this pump last cleared its per-module state for. A store's
    /// object id is only meaningful within the module it was loaded from — <see cref="_engineBelief"/>/
    /// <see cref="_wireTarget"/> hold ids, not (module, id) pairs, so carrying either across a
    /// module change risks judging a NEW module's object id (which the engine is free to reuse
    /// the numeric value of once the OLD module's GIT is gone — the same id-reuse hazard
    /// <c>docs/research/enemy-presence-sync.md</c> already documents for creatures) against a
    /// belief that was never about it. Clearing on every module change is defence in depth: the
    /// live symptom this class's own root-cause section was fixed for (a client buy on a
    /// mirrored key never detected) turned out to be <see cref="KotorGameBridge"/>'s own resolve,
    /// not this — but a store id genuinely IS only ever resolved correctly by
    /// <c>GetStoreByGameObjectID</c>'s type-checked, per-app registry lookup, which itself is
    /// scoped to whatever module is currently loaded, so this pump should not pretend to know
    /// more about a stale id than the engine itself would.</summary>
    private string _lastModule = string.Empty;

    public void Pump(string module, SessionCapabilities caps)
    {
        if (!bridge.SupportsStore || (caps & SessionCapabilities.Store) == 0)
        {
            return;
        }

        // Gated on a clean, non-fallback module read (LootPump.PumpMaterialisedLoot's own
        // established convention) so a brief mid-transition fallback label ("area1F"-style,
        // ModuleNameGuard.IsFallbackModuleName) never fires a spurious reset — the module truly
        // changing is what matters, not a transient unreadable read.
        if (module.Length > 0 && !ModuleNameGuard.IsFallbackModuleName(module)
            && !string.Equals(module, _lastModule, StringComparison.OrdinalIgnoreCase))
        {
            if (_lastModule.Length > 0 && (_engineBelief.Count > 0 || _wireTarget.Count > 0))
            {
                log($"store: module changed ({_lastModule} -> {module}) — clearing "
                    + $"{_engineBelief.Count} known key(s) and {_wireTarget.Count} pending apply/ies "
                    + "rather than carrying a stale store id's belief into the new module");
            }

            _lastModule = module;
            _engineBelief.Clear();
            _wireTarget.Clear();
            _openStoreId = 0;
            _openStoreBaselined = false;
            _opRate.Clear();
            _frozenUntilMs.Clear();

            // Anything already queued for the OLD module's store ids must not land in the new
            // one — CoopAgent's own single-slot cheat-channel drain (TryDequeuePendingApply)
            // could otherwise apply a stale insert/remove against whatever id the engine now
            // reuses. Wire-side ops already accepted (session.DrainStoreOps) are re-read fresh
            // below, so nothing here is lost — only this pump's own optimistic queue is dropped.
            _pendingApplies.Clear();
        }

        long nowMs = SessionClock.NowMs;

        // ---- step 1: detect a genuine local buy/sell, only for the store open THIS tick -----
        if (bridge.TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> openStoreScan))
        {
            if (storeObjectId != _openStoreId)
            {
                // A different store opened (or the first one this session) — this pump has no
                // fresh scan of it yet to diff against, so seed one silently. If this store was
                // seen before (a peer already named it, or we opened it earlier this session),
                // _engineBelief may already hold stale entries for it; ReplaceStore below folds
                // the fresh scan in as the new truth for exactly this store's keys.
                _openStoreId = storeObjectId;
                _openStoreBaselined = false;
            }

            Dictionary<StoreStackKey, int> priorForThisStore = SliceByStore(_engineBelief, storeObjectId);

            if (!_openStoreBaselined)
            {
                // Design rule (a): a store's first LOCAL read, with nothing to diff against yet,
                // publishes NOTHING — it simply becomes this pump's own baseline going forward.
                ReplaceStore(_engineBelief, storeObjectId, openStoreScan);
                _openStoreBaselined = true;
            }
            else
            {
                // Every key any side knows about, once — matching StoreDiffRule's own two
                // passes, but resolved key by key here (not via StoreDiffRule.Diff) so a pending
                // entry's echo/excess logic can run inline, exactly like PartyPoolPump's own step
                // 1 loop. A key pending an apply is unioned in even when it appears in NEITHER
                // the prior belief NOR this fresh scan — the exact buy-back race
                // (docs/research/store-sync.md's own 2026-08-28 finding): a mirrored insert
                // immediately bought back can net the engine straight back to its PRE-INSERT
                // value within a single scan interval, so the key would otherwise never even be
                // visited here at all.
                HashSet<StoreStackKey> keys = new(priorForThisStore.Keys);
                keys.UnionWith(openStoreScan.Keys);
                foreach (StoreStackKey pendingKey in _wireTarget.Keys)
                {
                    if (pendingKey.StoreObjectId == storeObjectId)
                    {
                        keys.Add(pendingKey);
                    }
                }

                foreach (StoreStackKey key in keys)
                {
                    int prior = priorForThisStore.GetValueOrDefault(key);
                    int actual = openStoreScan.GetValueOrDefault(key);

                    if (_wireTarget.TryGetValue(key, out int target))
                    {
                        // A queued apply is (or was) in flight for this key. Resolved against the
                        // WIRE'S OWN target, deliberately NOT gated behind "did actual move from
                        // prior" — the buy-back race above is exactly the case where it did not
                        // (the insert and the buy-back can net back to the SAME value the pre-tick
                        // scan already held). Movement that lands EXACTLY on target is the apply
                        // landing cleanly — nothing new to report. Anything else is a genuine local
                        // change entangled with the apply's own landing; publish only the EXCESS
                        // beyond the target, then resolve the pending entry either way — if the
                        // apply had not actually landed yet, the next drain (step 2) re-arms a
                        // fresh target from this tick's now-current belief.
                        _wireTarget.Remove(key);

                        int excess = actual - target;
                        if (excess != 0 && !NoteOpAndCheckFrozen(key, nowMs))
                        {
                            PublishDetected(key, excess, prior);
                        }

                        continue;
                    }

                    if (actual == prior)
                    {
                        // No apply pending, and nothing moved since the last real scan.
                        continue;
                    }

                    if (!NoteOpAndCheckFrozen(key, nowMs))
                    {
                        // No apply pending for this key — an ordinary genuine local change. The
                        // full movement since the last real scan is published immediately.
                        PublishDetected(key, actual - prior, prior);
                    }
                }

                ReplaceStore(_engineBelief, storeObjectId, openStoreScan);
            }
        }
        else if (_openStoreId != 0)
        {
            // No store open now, but one was last tick — nothing further to detect for it (its
            // last scan already captured everything; see the type's own remarks). Just stop
            // tracking "the open store" — _engineBelief keeps whatever that store's last scan
            // said.
            _openStoreId = 0;
            _openStoreBaselined = false;
        }

        // ---- step 2: apply every REMOTE op the wire layer has genuinely accepted -------------
        foreach (StoreOpPacket op in session.DrainStoreOps())
        {
            if (NoteOpAndCheckFrozen(op.Key, nowMs))
            {
                continue;
            }

            if (_pendingApplies.Count >= MaxPendingApplies)
            {
                log($"store: dropping apply for '{op.Key.Resref}' in store 0x{op.Key.StoreObjectId:X}"
                    + $" — already holding {MaxPendingApplies} queued, the drain has fallen behind");
                continue;
            }

            // Every subsequent op for the SAME key builds on whatever the wire already expects
            // (a still-pending target if one exists, otherwise this pump's own real-scan belief)
            // — never on a stale belief left over from before an EARLIER queued-but-not-yet-landed
            // apply for the same key. This is what makes back-to-back ops for one key (a sell then
            // an immediate re-sell) converge to the right cumulative total instead of each one
            // re-deriving its target from a belief the first one hasn't caught up to yet.
            int baseline = _wireTarget.TryGetValue(op.Key, out int pending)
                ? pending
                : _engineBelief.GetValueOrDefault(op.Key);

            if (op.Kind == StoreOpKind.Set)
            {
                // Absolute resync target — only safe to convert into a delta against an engine
                // belief THIS pump already holds (own prior scan, or a prior apply's own target).
                // A key with no entry in EITHER has never been locally observed by this instance
                // at all; guessing "Add[target] from nothing" would very likely be wrong (this
                // instance's own true pristine baseline is unknown to this Set) — left alone,
                // matching docs/research/store-sync.md's own documented open limitation for this
                // narrow case. Once THIS instance eventually scans that store itself, the
                // first-open baseline branch above picks up the truth directly.
                if (!_wireTarget.ContainsKey(op.Key) && !_engineBelief.ContainsKey(op.Key))
                {
                    continue;
                }

                int target = op.CountDelta;
                int diff = target - baseline;
                if (diff == 0)
                {
                    continue;
                }

                StoreOpKind applyKind = diff > 0 ? StoreOpKind.Add : StoreOpKind.Remove;
                ushort applyCount = (ushort)Math.Min(Math.Abs(diff), ushort.MaxValue);

                _pendingApplies.Enqueue(new CoopAgent.PendingStoreApply(applyKind, op.Key, applyCount));
                _wireTarget[op.Key] = target;
            }
            else
            {
                // Add/Remove: apply the RAW DELTA directly — never "set to wire total" (design
                // rule (c)). Correct regardless of whether this pump has ever scanned the
                // affected store: a relative delta needs no baseline knowledge at all.
                _pendingApplies.Enqueue(new CoopAgent.PendingStoreApply(op.Kind, op.Key, op.CountDelta));

                int target = op.Kind == StoreOpKind.Add
                    ? baseline + op.CountDelta
                    : Math.Max(0, baseline - op.CountDelta);
                _wireTarget[op.Key] = target;
            }
        }
    }

    /// <summary>Called by whatever already arbitrates the single-slot staging-cheat channel, at
    /// most once per ITS tick — mirrors <c>PartyPoolPump.TryDequeuePendingApply</c> exactly.</summary>
    public bool TryDequeuePendingApply(out CoopAgent.PendingStoreApply next) =>
        _pendingApplies.TryDequeue(out next);

    /// <summary>Publishes a signed, already-resolved local delta for one key — a positive
    /// <paramref name="delta"/> is an Add, negative is a Remove. Shared by both branches of the
    /// per-key loop above: an ordinary genuine local change (the full movement) and a pending
    /// key's excess beyond its target.</summary>
    private void PublishDetected(StoreStackKey key, int delta, int localBaseline)
    {
        StoreOpKind kind = delta > 0 ? StoreOpKind.Add : StoreOpKind.Remove;
        uint count = (uint)Math.Abs(delta);

        log($"store: local {kind.ToString().ToLowerInvariant()} of {count}x "
            + $"'{key.Resref}' detected in store 0x{key.StoreObjectId:X}, publishing");

        session.PublishStoreOp(kind, key, count, localBaseline);
    }

    /// <summary>True if <paramref name="key"/> is currently frozen (already tripped the loop
    /// breaker, still cooling down) — the caller must skip publishing/applying anything for it
    /// this tick. Does NOT itself count an op; see <see cref="NoteOpAndCheckFrozen"/> for the
    /// side-effecting counterpart used at each actual publish/apply site.</summary>
    private bool IsFrozen(StoreStackKey key, long nowMs)
    {
        if (!_frozenUntilMs.TryGetValue(key, out long until))
        {
            return false;
        }

        if (nowMs < until)
        {
            return true;
        }

        // Cooldown elapsed — give the key a clean slate.
        _frozenUntilMs.Remove(key);
        _opRate.Remove(key);
        return false;
    }

    /// <summary>Notes one more publish/apply event toward <paramref name="key"/>'s rolling-window
    /// count and trips the loop breaker (freezing the key and requesting a resync, once per
    /// episode) the moment the count exceeds <see cref="LoopBreakerMaxOpsPerWindow"/> within
    /// <see cref="LoopBreakerWindowMs"/>. Returns true if the key is now frozen (either just
    /// tripped, or already was) — the caller must skip the action it was about to take.</summary>
    private bool NoteOpAndCheckFrozen(StoreStackKey key, long nowMs)
    {
        if (IsFrozen(key, nowMs))
        {
            return true;
        }

        (long windowStartMs, int count) = _opRate.TryGetValue(key, out (long, int) entry) ? entry : (nowMs, 0);
        if (nowMs - windowStartMs > LoopBreakerWindowMs)
        {
            windowStartMs = nowMs;
            count = 0;
        }

        count++;
        _opRate[key] = (windowStartMs, count);

        if (count <= LoopBreakerMaxOpsPerWindow)
        {
            return false;
        }

        _frozenUntilMs[key] = nowMs + LoopBreakerWindowMs;
        log($"store: loop breaker tripped for '{key.Resref}' in store 0x{key.StoreObjectId:X} — "
            + $"{count} ops within {LoopBreakerWindowMs}ms, freezing and requesting a resync");
        session.RequestStoreResync();
        return true;
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
}

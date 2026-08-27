using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The shared party item-pool channel (<c>CSWPartyTable+0x118</c>). Two independent jobs each
/// tick, both driven by <see cref="PartyPoolDiffRule.Diff"/>:
///
/// 1. <b>Detect.</b> Diff this instance's own fresh engine scan against the LAST scan (skipping
///    any key with an apply already in flight — see below) to find a genuine local player
///    action (someone dragged an item into or out of the shared stash by hand) and publish it.
/// 2. <b>Reconcile.</b> Diff the fresh scan against <see cref="ICoopSession.PartyPoolTotals"/>
///    (the merged cross-instance belief) to find where THIS engine is behind what the session
///    now knows — a peer's add/remove not yet applied here — and queue an apply for it.
///
/// The two never fight because step 1 always runs first (against the OLD baseline, before step
/// 2's queued applies exist) and step 2 always reads the store AFTER step 1's publishes have
/// already updated it, so a key step 1 just published never shows a residual diff in step 2.
///
/// <b>Echo suppression, two layers.</b> Wire-level: <c>CoopHost</c>/<c>CoopClient</c> already
/// stop a peer's own change from re-applying to its own STORE (see
/// <c>CoopClient.HandlePartyPoolOp</c>'s origin check). Engine-level, here: once this pump
/// queues an apply for a key, it remembers the engine total that apply should PRODUCE
/// (<c>_pendingApplyTarget</c>) and skips that key in step 1 until a fresh scan actually shows
/// that value — otherwise the moment the queued apply lands, the very next scan would see the
/// engine's own total "jump" and step 1 would misread the pump's OWN application as a fresh
/// local action and re-publish it right back onto the wire.
///
/// ⚠ ROOT CAUSE 2, THE DEPOSITING-SIDE DUPLICATE (2026-08-28, docs/research/party-pool-sync.md
/// §9-§10). The guard above only ever protected step 2's OWN queued applies — a key step 1
/// itself just detected and published had NO equivalent protection at all, on either side of
/// the host/client symmetry the class remarks above describe. That asymmetry is real: a
/// HOST-originated deposit reaches the engine through the single-slot staging-cheat channel
/// (<c>pooladd</c>, or a GUI drag) exactly like any other local action, but UNLIKE a queued
/// automatic apply, step 1's own publish left the key instantly eligible for re-detection on
/// the very next tick — so if a fresh engine scan ever transiently disagreed with what was just
/// published (a stale scan-cache read, an item whose resref briefly failed to resolve, or any
/// other momentary engine-read inconsistency — see docs/research/party-pool-sync.md's own
/// KotorGameBridge.PartyPool.cs 1-second scan-cache remarks), step 1 would misread its OWN
/// recent local action as a FURTHER genuine change and republish it — which, relayed to a peer
/// and reconciled back, is exactly the shape of a double apply. Fixed the same way root cause 1
/// was: a key step 1 just published now also lands in <c>_pendingApplyTarget</c> (via
/// <c>_localDetectExpiry</c>, released either once a fresh scan reconfirms the value OR after
/// <see cref="LocalDetectGraceMs"/> elapses — NOT held indefinitely like step 2's own entries,
/// because unlike a queued apply this instance is IN THE PROCESS of making true, a step-1
/// detection is already true; the grace window exists only to smooth over one scan-cache cycle's
/// worth of transient noise, not to wait for anything to "land" — an indefinite hold here would
/// permanently swallow a genuine second local action, e.g. a player who deposits then
/// immediately withdraws the same stack).
///
/// See docs/research/party-pool-sync.md for the full design and the engine-call chain the
/// queued applies eventually run through.
/// </summary>
internal sealed class PartyPoolPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    /// <summary>One apply this instance owes its own engine, queued for the arbitrated
    /// staging-cheat channel to submit one at a time — the same shape and the same reason as
    /// <c>LootPump</c>'s pending-give queue: only one cheat request is ever in flight, so an
    /// automatic apply must be drained by whatever already arbitrates that channel rather than
    /// firing here directly.</summary>
    /// <summary>Bounded for the same reason <c>LootPump</c>'s queue is: a stuck drain or a
    /// malformed peer must not grow this without limit.</summary>
    private const int MaxPendingApplies = 256;

    private readonly Queue<CoopAgent.PendingPartyPoolApply> _pendingApplies = new();

    /// <summary>This instance's own last-scanned engine totals — the step 1 baseline.</summary>
    private Dictionary<PartyPoolStackKey, int> _lastScan = [];

    /// <summary>Keys with an apply already queued (or in flight through the cheat channel),
    /// mapped to the engine total that apply is expected to produce once it lands. A key stays
    /// here — excluded from BOTH steps — until a fresh scan confirms that value.</summary>
    private readonly Dictionary<PartyPoolStackKey, int> _pendingApplyTarget = [];

    /// <summary>How long a key step 1 just detected-and-published stays in
    /// <see cref="_pendingApplyTarget"/> even if a later scan disagrees with it — root cause 2's
    /// fix, see the class remarks. Only entries ORIGINATED by step 1 get a deadline here; step
    /// 2's own queued-apply entries are absent from this dictionary and keep waiting
    /// indefinitely for a matching scan, unchanged from before this fix. Generous relative to
    /// <c>KotorGameBridge.PartyPool.cs</c>'s own 1-second scan-cache — long enough to absorb one
    /// full stale-cache cycle, short enough that a genuine second local change on the same key
    /// (deposit then immediately withdraw) is never swallowed for long.</summary>
    private const long LocalDetectGraceMs = 2000;

    private readonly Dictionary<PartyPoolStackKey, long> _localDetectExpiry = [];

    private bool _baselined;

    public void Pump(SessionCapabilities caps)
    {
        if (!bridge.SupportsPartyPool || (caps & SessionCapabilities.PartyPool) == 0)
        {
            return;
        }

        if (!bridge.TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> engine))
        {
            // No world loaded yet (main menu, loading screen) — nothing to compare against.
            return;
        }

        // The first scan of a session is a silent baseline, exactly like ContainerLoot's own
        // first-scan-per-module rule: the pool's pre-existing contents are not "new" events.
        if (!_baselined)
        {
            _lastScan = new Dictionary<PartyPoolStackKey, int>(engine);
            _baselined = true;
            return;
        }

        // ---- step 1: detect a genuine local action ---------------------------------------
        //
        // ⚠ ECHO BUG, LIVE-VERIFIED (2026-08-27, docs/research/party-pool-sync.md's live pass):
        // "resolve applies that have landed" USED TO run here, BEFORE this step, removing a
        // just-landed key from _pendingApplyTarget the SAME tick its apply's own fresh scan
        // confirms it. That let this step's own exclusion set (Without(..., _pendingApplyTarget
        // .Keys)) go right back to including that key on the very tick it landed — so step 1
        // diffed the STALE _lastScan (captured before the apply) against the NEW engine value
        // the pump itself just produced, "detected" its own automatic apply as a fresh local
        // player action, and republished it. Measured live: every deposit/withdrawal on either
        // instance was silently applied TWICE (once as the real reconcile, once as this bogus
        // echo) before the numbers stopped moving — a real party-pool total left double what
        // was actually deposited, not a log-only cosmetic issue. Landed-key resolution now runs
        // AFTER this step instead (below), so a key stays excluded for the ENTIRE tick its
        // apply is confirmed and only rejoins ordinary detection on the NEXT tick, once
        // _lastScan already reflects the applied value.
        Dictionary<PartyPoolStackKey, int> prevForDetect = Without(_lastScan, _pendingApplyTarget.Keys);
        Dictionary<PartyPoolStackKey, int> engineForDetect = Without(engine, _pendingApplyTarget.Keys);

        // Staged, not applied to _pendingApplyTarget yet — see the "resolve applies that have
        // landed" block below for why this has to wait until AFTER that block runs: registering
        // a key here immediately would let this SAME tick's landed-check see it (target already
        // equal to the just-scanned value) and release it right back out before step 2 ever gets
        // a chance to see it excluded, which is exactly the protection root cause 2's fix needs.
        List<(PartyPoolStackKey Key, int Value)>? justDetected = null;

        foreach (PartyPoolDiffRule.Delta delta in PartyPoolDiffRule.Diff(prevForDetect, engineForDetect))
        {
            log($"party pool: local {delta.Kind.ToString().ToLowerInvariant()} of {delta.Count}x "
                + $"'{delta.Key.Resref}' detected, publishing");
            session.PublishPartyPoolOp(delta.Kind, delta.Key, delta.Count);
            (justDetected ??= []).Add((delta.Key, engineForDetect.GetValueOrDefault(delta.Key)));
        }

        _lastScan = new Dictionary<PartyPoolStackKey, int>(engine);

        long nowMs = Environment.TickCount64;

        // Resolve applies that have landed, now that step 1 has safely run against a baseline
        // that still excluded them: a key whose fresh scan value now matches what its queued
        // apply was supposed to produce is done, and rejoins ordinary tracking starting NEXT
        // tick (_lastScan above already reflects the landed value, so next tick's diff sees no
        // change). A step-1-originated entry (present in _localDetectExpiry) ALSO releases once
        // its grace window elapses, even without a matching scan — see LocalDetectGraceMs's own
        // remarks for why an indefinite hold would be wrong here specifically.
        if (_pendingApplyTarget.Count > 0)
        {
            List<PartyPoolStackKey>? landed = null;
            foreach ((PartyPoolStackKey key, int target) in _pendingApplyTarget)
            {
                bool confirmed = engine.GetValueOrDefault(key) == target;
                bool expired = _localDetectExpiry.TryGetValue(key, out long expiry) && nowMs >= expiry;

                if (confirmed || expired)
                {
                    (landed ??= []).Add(key);
                }
            }

            if (landed is not null)
            {
                foreach (PartyPoolStackKey key in landed)
                {
                    _pendingApplyTarget.Remove(key);
                    _localDetectExpiry.Remove(key);
                }
            }
        }

        // Now safe to register this tick's own fresh detections — AFTER the landed-check above,
        // so they cannot resolve themselves on the very tick they were published.
        if (justDetected is not null)
        {
            foreach ((PartyPoolStackKey key, int value) in justDetected)
            {
                _pendingApplyTarget[key] = value;
                _localDetectExpiry[key] = nowMs + LocalDetectGraceMs;
            }
        }

        // ---- step 2: reconcile toward the (now-updated) merged truth ----------------------
        IReadOnlyDictionary<PartyPoolStackKey, int> desired = session.PartyPoolTotals;
        Dictionary<PartyPoolStackKey, int> desiredForReconcile = Without(desired, _pendingApplyTarget.Keys);
        Dictionary<PartyPoolStackKey, int> engineForReconcile = Without(engine, _pendingApplyTarget.Keys);

        // Diff(expected: engine, actual: desired) — an Add delta means desired > engine (this
        // instance owes the engine that many more units), a Remove delta means desired < engine.
        foreach (PartyPoolDiffRule.Delta delta in PartyPoolDiffRule.Diff(engineForReconcile, desiredForReconcile))
        {
            if (_pendingApplies.Count >= MaxPendingApplies)
            {
                log($"party pool: dropping apply for '{delta.Key.Resref}' — already holding "
                    + $"{MaxPendingApplies} queued, the drain has fallen behind");
                continue;
            }

            _pendingApplies.Enqueue(
                new CoopAgent.PendingPartyPoolApply(delta.Kind, delta.Key, (ushort)delta.Count));
            _pendingApplyTarget[delta.Key] = desired.GetValueOrDefault(delta.Key);
        }
    }

    /// <summary>Called by whatever already arbitrates the single-slot staging-cheat channel (the
    /// command dispatcher's pending-cheat-serial check), at most once per ITS tick — mirrors
    /// <c>LootPump.TryDequeuePendingLoot</c> exactly.</summary>
    public bool TryDequeuePendingApply(out CoopAgent.PendingPartyPoolApply next) =>
        _pendingApplies.TryDequeue(out next);

    private static Dictionary<PartyPoolStackKey, int> Without(
        IReadOnlyDictionary<PartyPoolStackKey, int> source, IReadOnlyCollection<PartyPoolStackKey> excluded)
    {
        if (excluded.Count == 0)
        {
            return new Dictionary<PartyPoolStackKey, int>(source);
        }

        Dictionary<PartyPoolStackKey, int> result = [];
        foreach ((PartyPoolStackKey key, int value) in source)
        {
            if (!excluded.Contains(key))
            {
                result[key] = value;
            }
        }

        return result;
    }
}

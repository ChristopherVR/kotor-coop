using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The shared party item-pool channel (<c>CSWPartyTable+0x118</c>). Two jobs each tick:
///
/// 1. <b>Detect.</b> Compare this instance's own fresh engine scan against the LAST scan, key by
///    key, to find a genuine local player action (someone dragged an item into or out of the
///    shared stash by hand, or a queued apply for a peer's change landing) and publish whatever
///    is genuinely new — see the per-key loop in <see cref="Pump"/> for exactly how a key with a
///    queued apply in flight is handled differently from an ordinary one.
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
/// (<c>_pendingApplyTarget</c>) so step 1 can recognise the apply landing cleanly (no publish)
/// versus something ELSE also moving that key (publish only the excess) — see root cause 3
/// below for why this is no longer a blanket exclusion.
///
/// ⚠ ROOT CAUSE 2, THE DEPOSITING-SIDE DUPLICATE (2026-08-28, docs/research/party-pool-sync.md
/// §9-§10, superseded below by root cause 3). The guard above only ever protected step 2's OWN
/// queued applies — a key step 1 itself just detected and published had NO equivalent
/// protection at all. Root cause 2's original fix gave a step-1 detection its own time-boxed
/// exclusion entry too (<c>LocalDetectGraceMs</c>, 2 seconds), released either once a fresh scan
/// reconfirmed the value or the window elapsed.
///
/// ⚠ ROOT CAUSE 3, THE UNDER-COUNT/LOCKOUT BUG (2026-08-28, docs/research/party-pool-sync.md
/// §11-§12). Root cause 2's blanket exclusion traded a duplicate for the opposite defect: ANY
/// key currently excluded (self-detected and still inside its grace window, OR a queued apply
/// still awaiting its exact target) was invisible to step 1 in its ENTIRETY — a SECOND genuine
/// local change to the SAME key inside that window was silently lost, not just delayed, because
/// <c>_lastScan</c> is updated unconditionally every tick regardless of exclusion, so by the
/// time the key was released there was no residual diff left to find. A queued apply's own
/// entry was worse: it never expired at all, so if the local player ALSO changed the same key
/// while the apply was in flight, the entry could never match its original target again and the
/// key stayed locked out of both steps indefinitely.
///
/// <b>The fix: count-aware, not key-blanket.</b> A pending entry (<c>_pendingApplyTarget</c>,
/// now populated ONLY by step 2's queued applies — a step-1 detection no longer registers one
/// at all, see below) means "this key owes the engine a specific TARGET value, not yet
/// confirmed." Every tick, for every key whose fresh scan value differs at all from the last
/// scan: if the key has no pending entry, the WHOLE difference is a genuine local change,
/// published immediately — no exclusion window, so a second (or third, or Nth) rapid change to
/// the same key is never invisible while an earlier one is "settling," because there IS no
/// settling period for a self-detected change (it is already true in the engine the instant it
/// is observed). If the key DOES have a pending entry: a fresh value that has not moved AT ALL
/// since the last scan means the apply simply has not landed yet — keep waiting, exactly as
/// before. A fresh value that HAS moved is compared against the pending TARGET: an exact match
/// is the apply landing cleanly (an echo — nothing new to report, matching the wire-received
/// op's own contribution the store already accounts for). Anything else is "a later read that
/// differs by exactly the expected delta is an echo; any FURTHER difference beyond the
/// expectation is a genuine new local change" — publish exactly the EXCESS beyond the target
/// (not the raw movement, which would double-count the apply's own already-accounted-for
/// contribution), then treat the pending entry as resolved either way: if the apply turns out
/// not to have actually landed, the very next reconcile pass (step 2, using the fresh engine
/// value against the now-updated store) re-queues whatever gap remains, so nothing is lost by
/// clearing the expectation here.
///
/// This trades away root cause 2's own glitch-immunity for a self-detected change (a transient
/// one-tick scan disagreement that fully self-heals is no longer silently absorbed — see
/// <c>PartyPoolLoopbackTests.AStepOneDetectedLocalChangeSurvivesATransientScanGlitchWithoutChurning</c>'s
/// updated expectations) in exchange for never losing or permanently locking out a genuine rapid
/// change — consistent with this channel's own multiset design (§2.1: "a store's stock
/// genuinely goes up and down repeatedly... a one-way ratchet is wrong here too"), which values
/// correctness over avoiding a rare extra pair of wire ops. Root cause 1 (a landed queued apply
/// re-detected as a fresh local action) stays fixed by construction: the pending-entry branch
/// above always computes zero excess when a queued apply lands exactly on target, so nothing
/// republishes it.
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

    /// <summary>This instance's own last-scanned engine totals — the step 1 baseline, and the
    /// value every ordinary (non-pending) key's movement is measured against each tick.</summary>
    private Dictionary<PartyPoolStackKey, int> _lastScan = [];

    /// <summary>Keys with a STEP-2 apply already queued (or in flight through the cheat
    /// channel), mapped to the engine total that apply ALONE is expected to produce once it
    /// lands. Populated only by step 2's reconcile below — a step-1 self-detection no longer
    /// registers an entry here at all (see the class remarks' root cause 3 section): it already
    /// IS true in the engine the instant it is observed, so there is nothing left to "wait for
    /// landing" the way a queued apply's own delayed cheat-channel round trip needs. A key with
    /// an entry here is excluded from step 2's reconcile (no second apply queued for the same
    /// key while one is already in flight) but NOT excluded from step 1's detection — see
    /// <see cref="Pump"/>'s own per-key loop for how a pending entry is compared against a fresh
    /// scan value (exact match = landed cleanly; anything else = publish the excess).</summary>
    private readonly Dictionary<PartyPoolStackKey, int> _pendingApplyTarget = [];

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

        // ---- step 1: detect movement since the last scan, key by key ----------------------
        //
        // Every key either _lastScan or the fresh scan knows about, once. A key present in only
        // one of the two just went to/from zero (matching PartyPoolDiffRule's own two passes).
        HashSet<PartyPoolStackKey> keys = new(_lastScan.Keys);
        keys.UnionWith(engine.Keys);

        foreach (PartyPoolStackKey key in keys)
        {
            int prior = _lastScan.GetValueOrDefault(key);
            int actual = engine.GetValueOrDefault(key);

            if (actual == prior)
            {
                // Nothing moved since the last scan. If a queued apply is outstanding for this
                // key, it simply has not landed yet — keep waiting.
                continue;
            }

            if (_pendingApplyTarget.TryGetValue(key, out int target))
            {
                // A step-2 apply is in flight for this key. `target` is the value that apply
                // ALONE should produce. An exact match means it landed cleanly — nothing new to
                // report (the store already accounts for its own contribution). Anything else
                // means the apply's own effect (however much of it has landed) is entangled with
                // something ELSE moving the same key — most commonly a genuine local action on
                // the same key while the apply was still in flight. Publish only the EXCESS
                // beyond what the apply alone explains, and treat the apply as settled either
                // way: if it turns out not to have actually landed, the next reconcile pass
                // below re-queues whatever gap remains from the fresh engine/desired comparison,
                // so nothing is lost by clearing the expectation here.
                _pendingApplyTarget.Remove(key);

                int excess = actual - target;
                if (excess != 0)
                {
                    PublishDetected(key, excess);
                }
            }
            else
            {
                // No apply pending for this key — an ordinary genuine local change. The full
                // movement since the last scan is published immediately: unlike the old
                // "exclude this key for up to 2s" guard, a second (or third...) rapid change to
                // the SAME key is never invisible while an earlier one is still "settling" —
                // there is no settling period for a self-detected change, since it is already
                // true in the engine the instant it is observed.
                PublishDetected(key, actual - prior);
            }
        }

        _lastScan = new Dictionary<PartyPoolStackKey, int>(engine);

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

            // This instance now owes its own engine an apply — the very next scan needs to be
            // fresh so the apply's landing (and anything else that moves in the meantime) is
            // seen as soon as it is true, not delayed behind a cache window that started
            // ticking before we had any reason to look again. Same reasoning as
            // PublishDetected's own call below.
            bridge.InvalidatePartyPoolScanCache();
        }
    }

    /// <summary>Publishes a signed, already-resolved local delta for one key — a positive
    /// <paramref name="delta"/> is an Add, negative is a Remove. Shared by both branches of the
    /// per-key loop in <see cref="Pump"/>: an ordinary genuine local change (the full movement)
    /// and a pending key's excess beyond its target (only the unexplained remainder).</summary>
    private void PublishDetected(PartyPoolStackKey key, int delta)
    {
        PartyPoolOpKind kind = delta > 0 ? PartyPoolOpKind.Add : PartyPoolOpKind.Remove;
        uint count = (uint)Math.Abs(delta);

        log($"party pool: local {kind.ToString().ToLowerInvariant()} of {count}x "
            + $"'{key.Resref}' detected, publishing");
        session.PublishPartyPoolOp(kind, key, count);

        // A genuine local change just happened — the bridge's own scan cache (up to 1s stale,
        // see KotorGameBridge.PartyPool.cs) must not be allowed to mask a SECOND rapid change to
        // the same or another key behind the rest of that window. See
        // docs/research/party-pool-sync.md §14.
        bridge.InvalidatePartyPoolScanCache();
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

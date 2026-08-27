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

        // Resolve applies that have landed: a key whose fresh scan value now matches what its
        // queued apply was supposed to produce is done, and rejoins ordinary tracking.
        if (_pendingApplyTarget.Count > 0)
        {
            List<PartyPoolStackKey>? landed = null;
            foreach ((PartyPoolStackKey key, int target) in _pendingApplyTarget)
            {
                if (engine.GetValueOrDefault(key) == target)
                {
                    (landed ??= []).Add(key);
                }
            }

            if (landed is not null)
            {
                foreach (PartyPoolStackKey key in landed)
                {
                    _pendingApplyTarget.Remove(key);
                }
            }
        }

        // ---- step 1: detect a genuine local action ---------------------------------------
        Dictionary<PartyPoolStackKey, int> prevForDetect = Without(_lastScan, _pendingApplyTarget.Keys);
        Dictionary<PartyPoolStackKey, int> engineForDetect = Without(engine, _pendingApplyTarget.Keys);

        foreach (PartyPoolDiffRule.Delta delta in PartyPoolDiffRule.Diff(prevForDetect, engineForDetect))
        {
            log($"party pool: local {delta.Kind.ToString().ToLowerInvariant()} of {delta.Count}x "
                + $"'{delta.Key.Resref}' detected, publishing");
            session.PublishPartyPoolOp(delta.Kind, delta.Key, delta.Count);
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

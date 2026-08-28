using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The merchant/store stock channel's HOST-AUTHORITATIVE v2 design (<c>store_sync=2</c>) — the
/// store's mirror of <see cref="PartyPoolPumpV2"/>, identical reasoning, widened by the store's
/// own object id and carrying forward the one real engine limitation v1 already established: a
/// store's contents can only be READ (never applied) while a local player has its GUI open
/// (docs/research/shared-inventory-v2.md §2.6/§3.1 — unchanged from
/// docs/research/store-sync.md §2.3/§2.6).
///
/// <b>Host</b>: every tick, scan whichever store (if any) is currently open, merge that ONE
/// store's fresh totals into the host's running cross-store belief
/// (<see cref="CoopHost.PublishStoreSnapshotV2"/>'s own merge — every other already-known store's
/// belief is left untouched), then publish the whole merged belief; drain accepted intents and
/// queue one engine apply per intent, unconditionally (applying never needed the GUI open).
///
/// <b>Client</b>: detection/reconcile only ever runs against whichever ONE store is open THIS
/// tick — there is no way to notice a local buy/sell in a shop nobody has opened, matching v1's
/// own limitation exactly. The reconcile-apply itself needs no GUI open either, so a key already
/// converging toward its target keeps converging on ticks where a DIFFERENT store (or none) is
/// open; only fresh detection is store-gated.
/// </summary>
internal sealed class StorePumpV2(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const int MaxPendingApplies = 256;

    private readonly Lock _pendingGate = new();

    private readonly Queue<CoopAgent.PendingStoreApply> _pendingApplies = new();

    private readonly Dictionary<StoreStackKey, int> _lastSnapshotValue = [];

    private readonly Dictionary<StoreStackKey, int> _reconcileInFlight = [];

    /// <summary>See <see cref="PartyPoolPumpV2._reconcileQueuedAtMs"/>'s identical remarks.</summary>
    private readonly Dictionary<StoreStackKey, long> _reconcileQueuedAtMs = [];

    /// <summary>See <see cref="PartyPoolPumpV2.ReconcileInFlightTimeoutMs"/>'s identical remarks.</summary>
    private const long ReconcileInFlightTimeoutMs = 10_000;

    /// <summary>See <see cref="PartyPoolPumpV2._lastActual"/>'s identical remarks.</summary>
    private readonly Dictionary<StoreStackKey, int> _lastActual = [];

    private readonly Dictionary<StoreStackKey, List<OutstandingIntent>> _outstanding = [];

    private readonly Dictionary<uint, StoreStackKey> _outstandingBySerial = [];

    private const long IntentTimeoutMs = 10_000;

    /// <summary>See <see cref="PartyPoolPumpV2.AckGraceMs"/>'s identical remarks.</summary>
    private const long AckGraceMs = 2_500;

    private sealed class OutstandingIntent(uint serial, int delta, long queuedAtMs)
    {
        public uint Serial { get; } = serial;
        public int Delta { get; } = delta;
        public long QueuedAtMs { get; } = queuedAtMs;
        public long AckedAtMs { get; set; } = -1;
    }

    public void Pump(SessionCapabilities caps)
    {
        if (!bridge.SupportsStore || (caps & SessionCapabilities.Store) == 0)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            PumpHost();
        }
        else
        {
            PumpClient();
        }
    }

    private void PumpHost()
    {
        if (bridge.TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> totals))
        {
            session.PublishStoreSnapshotV2(storeObjectId, totals);
        }

        foreach (StoreIntentPacketV2 intent in session.DrainStoreIntentsV2())
        {
            EnqueueApply(intent.Kind, intent.Key, intent.Count);
        }
    }

    private void PumpClient()
    {
        long now = SessionClock.NowMs;

        foreach (uint serial in session.DrainAckedStoreIntentsV2())
        {
            MarkAcked(serial, now);
        }

        PruneOutstanding(now);
        PruneStaleReconcileInFlight(now);

        if (!bridge.TryScanOpenStore(out uint storeObjectId, out IReadOnlyDictionary<StoreStackKey, int> scan))
        {
            // No store open this tick — nothing to freshly detect. A reconcile already in
            // flight for a store that just closed simply keeps waiting until it reopens (or the
            // target moves again on a later tick where it IS open); applying never needed the
            // GUI open, only detecting a local change does.
            return;
        }

        if (!session.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> snapshot))
        {
            return;
        }

        HashSet<StoreStackKey> keys = new(scan.Keys);
        foreach (StoreStackKey key in snapshot.Keys)
        {
            if (key.StoreObjectId == storeObjectId)
            {
                keys.Add(key);
            }
        }

        foreach (StoreStackKey key in _lastSnapshotValue.Keys)
        {
            if (key.StoreObjectId == storeObjectId)
            {
                keys.Add(key);
            }
        }

        foreach (StoreStackKey key in _reconcileInFlight.Keys)
        {
            if (key.StoreObjectId == storeObjectId)
            {
                keys.Add(key);
            }
        }

        foreach (StoreStackKey key in _outstanding.Keys)
        {
            if (key.StoreObjectId == storeObjectId)
            {
                keys.Add(key);
            }
        }

        foreach (StoreStackKey key in keys)
        {
            int snapshotValue = snapshot.GetValueOrDefault(key);

            // detectTarget (snapshot + outstanding) is ONLY ever valid for the DETECTION check
            // at the bottom of this loop — see ReconcileTarget's own remarks for why a
            // RECONCILE-APPLY must never target it directly (PartyPoolPumpV2's identical fix).
            int detectTarget = snapshotValue + OutstandingNet(key);
            int actual = scan.GetValueOrDefault(key);

            // See PartyPoolPumpV2's identical remarks: compares the RAW host snapshot value,
            // never detectTarget.
            bool firstSight = !_lastSnapshotValue.TryGetValue(key, out int priorSnapshotValue);
            bool snapshotMoved = !firstSight && priorSnapshotValue != snapshotValue;
            _lastSnapshotValue[key] = snapshotValue;

            bool actualMoved = _lastActual.TryGetValue(key, out int priorActual) && priorActual != actual;
            _lastActual[key] = actual;

            if (_reconcileInFlight.TryGetValue(key, out int inFlightTarget))
            {
                // See PartyPoolPumpV2's identical remarks: an exact match against the frozen
                // target alone can never fire again once a scan-cache gap lets the observed
                // value skip past it — clear unconditionally on ANY movement (the queued apply's
                // own delta lands in one synchronous engine call, so any movement while in
                // flight IS that apply landing, by construction).
                if (actual == inFlightTarget || actualMoved)
                {
                    _reconcileInFlight.Remove(key);
                    _reconcileQueuedAtMs.Remove(key);

                    // See PartyPoolPumpV2's identical remarks: a reconcile landing on behalf of
                    // an outstanding intent must retire it HERE, or it lingers in OutstandingNet
                    // forever and permanently mis-inflates detectTarget below.
                    int net = OutstandingNet(key);
                    if (net != 0 && (net > 0 ? actual >= snapshotValue : actual <= snapshotValue))
                    {
                        ClearOutstanding(key);
                    }
                }
                else
                {
                    int rearmTarget = ReconcileTarget(key, actual, snapshotValue);
                    if (rearmTarget != inFlightTarget)
                    {
                        QueueReconcile(key, actual, rearmTarget);
                    }
                }

                continue;
            }

            if (firstSight)
            {
                if (actual < snapshotValue)
                {
                    QueueReconcile(key, actual, ReconcileTarget(key, actual, snapshotValue));
                }

                continue;
            }

            if (snapshotMoved)
            {
                // See PartyPoolPumpV2.ReconcileTarget's own remarks: reconciling toward
                // snapshotValue + OutstandingNet(key) double-counts a delta the snapshot has
                // already absorbed once the client's own engine already explains it.
                int reconcileTarget = ReconcileTarget(key, actual, snapshotValue);
                if (actual != reconcileTarget)
                {
                    QueueReconcile(key, actual, reconcileTarget);
                }

                continue;
            }

            if (actual != detectTarget)
            {
                int delta = actual - detectTarget;
                StoreOpKind kind = delta > 0 ? StoreOpKind.Add : StoreOpKind.Remove;
                ushort count = (ushort)Math.Min(Math.Abs(delta), ushort.MaxValue);

                uint serial = session.PublishStoreIntentV2(kind, key, count);
                if (serial != 0)
                {
                    log($"store: local {kind.ToString().ToLowerInvariant()} of {count}x "
                        + $"'{key.Resref}' detected in store 0x{key.StoreObjectId:X}, "
                        + $"requesting (serial {serial})");
                    AddOutstanding(key, serial, delta, now);
                }
            }
        }
    }

    private void QueueReconcile(StoreStackKey key, int actual, int target)
    {
        int delta = target - actual;
        StoreOpKind kind = delta > 0 ? StoreOpKind.Add : StoreOpKind.Remove;
        ushort count = (ushort)Math.Min(Math.Abs(delta), ushort.MaxValue);

        if (!EnqueueApply(kind, key, count))
        {
            return;
        }

        _reconcileInFlight[key] = target;
        _reconcileQueuedAtMs[key] = SessionClock.NowMs;
    }

    /// <summary>See <see cref="PartyPoolPumpV2.PruneStaleReconcileInFlight"/>'s identical
    /// remarks.</summary>
    private void PruneStaleReconcileInFlight(long nowMs)
    {
        List<StoreStackKey>? expired = null;
        foreach ((StoreStackKey key, long queuedAtMs) in _reconcileQueuedAtMs)
        {
            if (nowMs - queuedAtMs > ReconcileInFlightTimeoutMs)
            {
                (expired ??= []).Add(key);
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (StoreStackKey key in expired)
        {
            _reconcileInFlight.Remove(key);
            _reconcileQueuedAtMs.Remove(key);
            log($"store: reconcile for '{key.Resref}' in store 0x{key.StoreObjectId:X} never "
                + $"observed landing within {ReconcileInFlightTimeoutMs / 1000}s — presumed "
                + "resolved, re-evaluating fresh");
        }
    }

    private bool EnqueueApply(StoreOpKind kind, StoreStackKey key, ushort count)
    {
        lock (_pendingGate)
        {
            if (_pendingApplies.Count >= MaxPendingApplies)
            {
                log($"store v2: dropping apply for '{key.Resref}' in store 0x{key.StoreObjectId:X}"
                    + $" — already holding {MaxPendingApplies} queued, the drain has fallen behind");
                return false;
            }

            _pendingApplies.Enqueue(new CoopAgent.PendingStoreApply(kind, key, count));
            return true;
        }
    }

    /// <summary>See <see cref="PartyPoolPumpV2.ReconcileTarget"/>'s identical remarks.</summary>
    private int ReconcileTarget(StoreStackKey key, int actual, int snapshotValue)
    {
        int net = OutstandingNet(key);
        if (net == 0)
        {
            return snapshotValue;
        }

        bool explainedByOwnEngine = net > 0 ? actual >= snapshotValue : actual <= snapshotValue;
        if (!explainedByOwnEngine)
        {
            return snapshotValue;
        }

        ClearOutstanding(key);
        return actual;
    }

    private int OutstandingNet(StoreStackKey key)
    {
        if (!_outstanding.TryGetValue(key, out List<OutstandingIntent>? entries))
        {
            return 0;
        }

        int net = 0;
        foreach (OutstandingIntent entry in entries)
        {
            net += entry.Delta;
        }

        return net;
    }

    private void AddOutstanding(StoreStackKey key, uint serial, int delta, long nowMs)
    {
        (_outstanding.TryGetValue(key, out List<OutstandingIntent>? entries)
            ? entries
            : _outstanding[key] = []).Add(new OutstandingIntent(serial, delta, nowMs));
        _outstandingBySerial[serial] = key;
    }

    private void RemoveOutstanding(uint serial)
    {
        if (!_outstandingBySerial.Remove(serial, out StoreStackKey key)
            || !_outstanding.TryGetValue(key, out List<OutstandingIntent>? entries))
        {
            return;
        }

        entries.RemoveAll(e => e.Serial == serial);
        if (entries.Count == 0)
        {
            _outstanding.Remove(key);
        }
    }

    /// <summary>See <see cref="PartyPoolPumpV2.ClearOutstanding"/>'s identical remarks.</summary>
    private void ClearOutstanding(StoreStackKey key)
    {
        if (!_outstanding.Remove(key, out List<OutstandingIntent>? entries))
        {
            return;
        }

        foreach (OutstandingIntent entry in entries)
        {
            _outstandingBySerial.Remove(entry.Serial);
        }
    }

    /// <summary>See <see cref="PartyPoolPumpV2.MarkAcked"/>'s identical remarks.</summary>
    private void MarkAcked(uint serial, long nowMs)
    {
        if (_outstandingBySerial.TryGetValue(serial, out StoreStackKey key)
            && _outstanding.TryGetValue(key, out List<OutstandingIntent>? entries))
        {
            foreach (OutstandingIntent entry in entries)
            {
                if (entry.Serial == serial)
                {
                    entry.AckedAtMs = nowMs;
                    return;
                }
            }
        }
    }

    /// <summary>See <see cref="PartyPoolPumpV2.PruneOutstanding"/>'s identical remarks.</summary>
    private void PruneOutstanding(long nowMs)
    {
        List<uint>? expired = null;
        foreach (List<OutstandingIntent> entries in _outstanding.Values)
        {
            foreach (OutstandingIntent entry in entries)
            {
                bool neverAckedAndTimedOut = entry.AckedAtMs < 0 && nowMs - entry.QueuedAtMs >= IntentTimeoutMs;
                bool ackedLongEnoughAgo = entry.AckedAtMs >= 0 && nowMs - entry.AckedAtMs >= AckGraceMs;

                if (neverAckedAndTimedOut || ackedLongEnoughAgo)
                {
                    (expired ??= []).Add(entry.Serial);
                }
            }
        }

        if (expired is null)
        {
            return;
        }

        foreach (uint serial in expired)
        {
            RemoveOutstanding(serial);
        }
    }

    /// <summary>Mirrors <see cref="PartyPoolPumpV2.TryDequeuePendingApply"/> exactly.</summary>
    public bool TryDequeuePendingApply(out CoopAgent.PendingStoreApply next)
    {
        lock (_pendingGate)
        {
            return _pendingApplies.TryDequeue(out next);
        }
    }
}

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

    /// <summary>See <see cref="PartyPoolPumpV2.AckGraceMs"/>'s identical remarks — raised for the
    /// identical reason (§7, 2026-08-29). Doubly necessary here: a store's scan is GUI-gated
    /// (only READING needs the GUI open, §2.6), so while the relevant store's GUI stays closed on
    /// the host, the evidence-based path (a snapshot that has genuinely moved) cannot fire AT
    /// ALL, no matter how long this pump waits — <see cref="CoopHost.HandleStoreIntentV2"/>'s own
    /// write-through exists specifically so the broadcast belief still reflects an accepted
    /// intent promptly even with the GUI closed, keeping the evidence-based path available in
    /// that case too; this timer remains the last-resort backstop for when it genuinely
    /// isn't.</summary>
    private const long AckGraceMs = 15_000;

    private sealed class OutstandingIntent(uint serial, int delta, long queuedAtMs)
    {
        public uint Serial { get; } = serial;

        /// <summary>See <c>PartyPoolPumpV2.OutstandingIntent.Delta</c>'s identical remarks —
        /// mutable so <see cref="RetireOutstandingBy"/> can partially shrink it.</summary>
        public int Delta { get; set; } = delta;

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
            // §7 (2026-08-29), item 3: a store the host has never scanned is UNKNOWN, not zero —
            // `snapshot.GetValueOrDefault(key)` returning 0 for every one of that store's keys is
            // indistinguishable, by itself, from "the host scanned it and it is genuinely empty".
            // Left ungated, this client's own local scan of a real, never-before-shared store's
            // pristine stock would read as a giant "local deposit" against a false zero baseline
            // (a real defect: the client's own real stock, not just a spurious wire report). Skip
            // the key ENTIRELY — no bookkeeping dictionary writes either — so the moment the
            // store becomes known this pump's ordinary `firstSight` seeding handles it correctly,
            // exactly as if this were the very first tick this key had ever existed.
            if (!session.IsStoreKnownV2(key.StoreObjectId))
            {
                continue;
            }

            int snapshotValue = snapshot.GetValueOrDefault(key);
            int actual = scan.GetValueOrDefault(key);

            // See PartyPoolPumpV2's identical remarks: compares the RAW host snapshot value,
            // never detectTarget.
            bool firstSight = !_lastSnapshotValue.TryGetValue(key, out int priorSnapshotValue);
            bool snapshotMoved = !firstSight && priorSnapshotValue != snapshotValue;
            _lastSnapshotValue[key] = snapshotValue;

            bool actualMoved = _lastActual.TryGetValue(key, out int priorActual) && priorActual != actual;
            _lastActual[key] = actual;

            // §7 (2026-08-29): see PartyPoolPumpV2's identical remarks — the ONLY place
            // outstanding is retired from confirmation evidence, proportional to the snapshot's
            // own confirmed movement, never by comparing against this instance's own `actual`
            // (unsound the moment more than one outstanding intent is in flight for the same
            // key — found live via the party-pool version of this exact pump).
            if (snapshotMoved)
            {
                RetireOutstandingBy(key, snapshotValue - priorSnapshotValue);
            }

            // detectTarget (snapshot + outstanding) is ONLY ever valid for the DETECTION check
            // at the bottom of this loop — see ReconcileTarget's own remarks for why a
            // RECONCILE-APPLY must never target it directly (PartyPoolPumpV2's identical fix).
            // Computed AFTER the retirement above so a partial retirement this same tick is
            // already reflected.
            int detectTarget = snapshotValue + OutstandingNet(key);

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

                    // §7 (2026-08-29): outstanding retirement is handled entirely by the
                    // snapshotMoved-driven RetireOutstandingBy call above now — see
                    // PartyPoolPumpV2's identical remarks for why the old
                    // actual-vs-snapshotValue-gated ClearOutstanding call here was unsound.
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

    /// <summary>See <see cref="PartyPoolPumpV2.ReconcileTarget"/>'s identical remarks — a PURE
    /// decision, no side effects (§7, 2026-08-29).</summary>
    private int ReconcileTarget(StoreStackKey key, int actual, int snapshotValue)
    {
        int net = OutstandingNet(key);
        if (net == 0)
        {
            return snapshotValue;
        }

        bool explainedByOwnEngine = net > 0 ? actual >= snapshotValue : actual <= snapshotValue;
        return explainedByOwnEngine ? actual : snapshotValue;
    }

    /// <summary>See <see cref="PartyPoolPumpV2.RetireOutstandingBy"/>'s identical remarks.</summary>
    private void RetireOutstandingBy(StoreStackKey key, int amount)
    {
        if (amount == 0 || !_outstanding.TryGetValue(key, out List<OutstandingIntent>? entries))
        {
            return;
        }

        int direction = Math.Sign(amount);
        int remaining = Math.Abs(amount);

        List<OutstandingIntent>? fullyRetired = null;
        foreach (OutstandingIntent entry in entries)
        {
            if (remaining == 0 || Math.Sign(entry.Delta) != direction)
            {
                continue;
            }

            int entryMagnitude = Math.Abs(entry.Delta);
            int take = Math.Min(entryMagnitude, remaining);
            remaining -= take;

            if (take == entryMagnitude)
            {
                (fullyRetired ??= []).Add(entry);
            }
            else
            {
                entry.Delta -= take * direction;
            }
        }

        if (fullyRetired is null)
        {
            return;
        }

        foreach (OutstandingIntent entry in fullyRetired)
        {
            entries.Remove(entry);
            _outstandingBySerial.Remove(entry.Serial);
        }

        if (entries.Count == 0)
        {
            _outstanding.Remove(key);
        }
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

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
/// <b>Client</b> (redesigned §9, 2026-08-29 — see the task's own item 2): the apply primitive
/// (<c>Odyssey.Store.cs</c>'s <c>InsertItemIntoStore</c>/<c>RemoveStoreStack</c>, which the
/// <c>StoreInsert</c>/<c>StoreRemove</c> cheats drive) resolves the target store purely by object
/// id (<c>GetStoreByGameObjectID</c>) — it needs no GUI open at all. Only READING a store's stock
/// (<c>TryScanOpenStore</c>) needs the GUI open. The two are therefore split here: RECONCILE runs
/// for every store this client's session knows about (<see cref="ICoopSession.IsStoreKnownV2"/>),
/// regardless of whether that particular store's GUI is open THIS tick, converging
/// <see cref="_belief"/> (this pump's own best estimate of the local engine's value for a key —
/// updated by a real scan whenever the GUI happens to be open on that store, otherwise tracked
/// purely from queued reconcile deltas) toward the host's snapshot exactly like
/// <see cref="PartyPoolPumpV2"/> does. DETECTION of a fresh local buy/sell — the belief-delta scan
/// diff — only ever runs for whichever ONE store is open THIS tick, since that is the only store
/// this instance can freshly observe at all; there is no way to notice a local change in a shop
/// nobody has opened, matching v1's own limitation exactly.
/// </summary>
internal sealed class StorePumpV2(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const int MaxPendingApplies = 256;

    private readonly Lock _pendingGate = new();

    private readonly Queue<CoopAgent.PendingStoreApply> _pendingApplies = new();

    /// <summary>See <see cref="PartyPoolPumpV2._lastSnapshotValue"/>'s identical remarks — drives
    /// ONLY <see cref="RetireOutstandingBy"/>, never the detect step.</summary>
    private readonly Dictionary<StoreStackKey, int> _lastSnapshotValue = [];

    /// <summary>See <see cref="PartyPoolPumpV2._belief"/>'s identical remarks. For a key whose
    /// store has never once been open locally, seeded to 0 on first sight rather than a real scan
    /// value (see <see cref="PumpClient"/>'s own remarks on the reconcile-without-GUI path) — the
    /// same "unknown baseline treated as the safest available assumption" shape
    /// docs/research/shared-inventory-v2.md §2.7 already accepts for the host's own apply
    /// surface, now symmetric on the client's reconcile side too.</summary>
    private readonly Dictionary<StoreStackKey, int> _belief = [];

    /// <summary>See <see cref="PartyPoolPumpV2._expectedFromReconcile"/>'s identical remarks.</summary>
    private readonly Dictionary<StoreStackKey, List<ExpectedReconcileMovement>> _expectedFromReconcile = [];

    /// <summary>See <see cref="PartyPoolPumpV2.ExpectedTimeoutMs"/>'s identical remarks.</summary>
    private const long ExpectedTimeoutMs = 10_000;

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

    /// <summary>See <see cref="PartyPoolPumpV2.ExpectedReconcileMovement"/>'s identical remarks.</summary>
    private sealed class ExpectedReconcileMovement(int delta, long queuedAtMs)
    {
        public int Delta { get; set; } = delta;

        public long QueuedAtMs { get; } = queuedAtMs;
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
        PruneExpiredExpected(now);

        if (!session.TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> snapshot))
        {
            return;
        }

        // Whichever ONE store (if any) is open on this client right now — the only store this
        // instance can freshly SCAN this tick. A closed GUI is NOT a reason to skip the whole
        // tick any more (§9, 2026-08-29, item 2): RECONCILE below runs for every KNOWN key
        // regardless, since applying an accepted intent's own reconcile never needed the GUI —
        // only detecting a fresh local change against a live scan does.
        bool storeOpen = bridge.TryScanOpenStore(
            out uint openStoreObjectId, out IReadOnlyDictionary<StoreStackKey, int> openScan);

        HashSet<StoreStackKey> keys = new(snapshot.Keys);
        keys.UnionWith(_belief.Keys);
        keys.UnionWith(_expectedFromReconcile.Keys);
        keys.UnionWith(_outstanding.Keys);
        if (storeOpen)
        {
            keys.UnionWith(openScan.Keys);
        }

        foreach (StoreStackKey key in keys)
        {
            // §7 (2026-08-29), item 3: a store the host has never scanned is UNKNOWN, not zero —
            // see PartyPoolPumpV2's callers for the identical "unknown vs. zero" reasoning this
            // mirrors. Skip the key ENTIRELY — no bookkeeping dictionary writes either — so the
            // moment the store becomes known this pump's ordinary `firstSight` seeding handles it
            // correctly, exactly as if this were the very first tick this key had ever existed.
            if (!session.IsStoreKnownV2(key.StoreObjectId))
            {
                continue;
            }

            int snapshotValue = snapshot.GetValueOrDefault(key);

            bool firstSnapshotSight = !_lastSnapshotValue.TryGetValue(key, out int priorSnapshotValue);
            if (!firstSnapshotSight && priorSnapshotValue != snapshotValue)
            {
                RetireOutstandingBy(key, snapshotValue - priorSnapshotValue);
            }

            _lastSnapshotValue[key] = snapshotValue;

            bool liveThisTick = storeOpen && key.StoreObjectId == openStoreObjectId;

            // §9 (2026-08-29): no special "first sight" branch — see
            // PartyPoolPumpV2.PumpClient's identical remarks for why a delta-based design must
            // never silently seed-and-defer a standing surplus (it can never recover it, unlike
            // the old absolute-target design's one-tick defer). A key never locally observed
            // defaults to belief 0 — the same "unknown baseline treated as the safest available
            // assumption" shape §2.7 already accepts for the host's own cold-start apply surface,
            // now symmetric here so a store this client has never opened can still reconcile UP
            // to reflect another peer's real action against it (item 2's own "host sells while
            // the client's GUI is closed" requirement) rather than being silently skipped forever
            // the way the pre-§9 GUI-gated early return did.
            int belief = _belief.GetValueOrDefault(key);

            if (liveThisTick)
            {
                int actual = openScan.GetValueOrDefault(key);
                int rawMovement = actual - belief;
                belief = actual;
                _belief[key] = actual;

                if (rawMovement != 0)
                {
                    int localChange = ConsumeExpected(key, rawMovement);
                    if (localChange != 0)
                    {
                        StoreOpKind kind = localChange > 0 ? StoreOpKind.Add : StoreOpKind.Remove;
                        ushort count = (ushort)Math.Min(Math.Abs(localChange), ushort.MaxValue);

                        uint serial = session.PublishStoreIntentV2(kind, key, count);
                        if (serial != 0)
                        {
                            log($"store: local {kind.ToString().ToLowerInvariant()} of {count}x "
                                + $"'{key.Resref}' detected in store 0x{key.StoreObjectId:X}, "
                                + $"requesting (serial {serial})");
                            AddOutstanding(key, serial, localChange, now);
                        }
                    }
                }
            }

            // Reconcile runs EVERY tick, live scan or not — see the class remarks (item 2). Uses
            // `belief`, never a live-only `actual`, so a store whose GUI is closed this tick can
            // still be asked to converge based on this pump's own best current estimate.
            int target = ReconcileTarget(key, belief, snapshotValue);
            int projected = belief + ExpectedFromReconcileNet(key);
            int reconcileDelta = target - projected;
            if (reconcileDelta != 0)
            {
                QueueReconcile(key, reconcileDelta, now);
            }
        }
    }

    private void QueueReconcile(StoreStackKey key, int delta, long nowMs)
    {
        if (delta == 0)
        {
            return;
        }

        StoreOpKind kind = delta > 0 ? StoreOpKind.Add : StoreOpKind.Remove;
        ushort count = (ushort)Math.Min(Math.Abs(delta), ushort.MaxValue);

        if (!EnqueueApply(kind, key, count))
        {
            return;
        }

        (_expectedFromReconcile.TryGetValue(key, out List<ExpectedReconcileMovement>? entries)
            ? entries
            : _expectedFromReconcile[key] = []).Add(new ExpectedReconcileMovement(delta, nowMs));
    }

    /// <summary>See <see cref="PartyPoolPumpV2.ConsumeExpected"/>'s identical remarks.</summary>
    private int ConsumeExpected(StoreStackKey key, int rawMovement)
    {
        if (!_expectedFromReconcile.TryGetValue(key, out List<ExpectedReconcileMovement>? entries)
            || entries.Count == 0)
        {
            return rawMovement;
        }

        int direction = Math.Sign(rawMovement);
        int remaining = Math.Abs(rawMovement);

        List<ExpectedReconcileMovement>? fullyConsumed = null;
        foreach (ExpectedReconcileMovement entry in entries)
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
                (fullyConsumed ??= []).Add(entry);
            }
            else
            {
                entry.Delta -= take * direction;
            }
        }

        if (fullyConsumed is not null)
        {
            foreach (ExpectedReconcileMovement entry in fullyConsumed)
            {
                entries.Remove(entry);
            }

            if (entries.Count == 0)
            {
                _expectedFromReconcile.Remove(key);
            }
        }

        return direction * remaining;
    }

    private int ExpectedFromReconcileNet(StoreStackKey key)
    {
        if (!_expectedFromReconcile.TryGetValue(key, out List<ExpectedReconcileMovement>? entries))
        {
            return 0;
        }

        int net = 0;
        foreach (ExpectedReconcileMovement entry in entries)
        {
            net += entry.Delta;
        }

        return net;
    }

    /// <summary>See <see cref="PartyPoolPumpV2.PruneExpiredExpected"/>'s identical remarks.</summary>
    private void PruneExpiredExpected(long nowMs)
    {
        List<StoreStackKey>? emptied = null;
        foreach ((StoreStackKey key, List<ExpectedReconcileMovement> entries) in _expectedFromReconcile)
        {
            int removed = entries.RemoveAll(e => nowMs - e.QueuedAtMs > ExpectedTimeoutMs);
            if (removed > 0)
            {
                log($"store: expected-reconcile entry for '{key.Resref}' in store "
                    + $"0x{key.StoreObjectId:X} never observed landing within "
                    + $"{ExpectedTimeoutMs / 1000}s — presumed resolved, re-evaluating fresh");
            }

            if (entries.Count == 0)
            {
                (emptied ??= []).Add(key);
            }
        }

        if (emptied is null)
        {
            return;
        }

        foreach (StoreStackKey key in emptied)
        {
            _expectedFromReconcile.Remove(key);
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
    private int ReconcileTarget(StoreStackKey key, int localValue, int snapshotValue)
    {
        int net = OutstandingNet(key);
        if (net == 0)
        {
            return snapshotValue;
        }

        bool explainedByOwnEngine = net > 0 ? localValue >= snapshotValue : localValue <= snapshotValue;
        return explainedByOwnEngine ? localValue : snapshotValue;
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

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
internal sealed class StorePumpV2(
    IGameBridge bridge,
    ICoopSession session,
    Action<string> log,
    bool trace = false,
    long expectedTimeoutMs = StorePumpV2.DefaultExpectedTimeoutMs)
{
    private const int MaxPendingApplies = 256;

    private readonly Lock _pendingGate = new();

    private readonly Queue<CoopAgent.PendingStoreApply> _pendingApplies = new();

    /// <summary>See <see cref="PartyPoolPumpV2._lastSnapshotValue"/>'s identical remarks — drives
    /// ONLY <see cref="RetireOutstandingBy"/>, never the detect step.</summary>
    private readonly Dictionary<StoreStackKey, int> _lastSnapshotValue = [];

    /// <summary>See <see cref="PartyPoolPumpV2._belief"/>'s identical remarks. Unlike the party
    /// pool's, this value is only sometimes an OBSERVATION: a store can only be scanned while its
    /// own GUI is open, so for every other store this is the pump's best assumption instead —
    /// seeded from the host's own first snapshot of that store and then carried forward by
    /// whatever reconciles have been queued against it. <see cref="_scannedStores"/> is how the
    /// two are told apart; see <see cref="PumpClient"/>'s own seeding remarks.</summary>
    private readonly Dictionary<StoreStackKey, int> _belief = [];

    /// <summary>See <see cref="PartyPoolPumpV2._expectedFromReconcile"/>'s identical remarks.</summary>
    private readonly Dictionary<StoreStackKey, List<ExpectedReconcileMovement>> _expectedFromReconcile = [];

    /// <summary>See <see cref="PartyPoolPumpV2.DefaultExpectedTimeoutMs"/>'s identical
    /// remarks.</summary>
    internal const long DefaultExpectedTimeoutMs = 10_000;

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

        /// <summary>See <c>PartyPoolPumpV2.ExpectedReconcileMovement.Dispatched</c>'s identical
        /// remarks.</summary>
        public bool Dispatched { get; set; }

        /// <summary>
        /// See <c>PartyPoolPumpV2.ExpectedReconcileMovement.TimerFromMs</c>'s remarks. Doubly
        /// load-bearing here: a store's stock can only be READ while its GUI is open, so for a
        /// key whose store is closed there is no observation that could EVER consume this entry.
        /// Expiring it on a wall clock in that state is what produced the live re-insertion loop
        /// (docs/research/shared-inventory-v2.md §10.2) — the entry vanished, `projected`
        /// collapsed back to `belief`, and the whole snapshot re-applied, every ~10s, forever.
        /// The clock is therefore slid forward for as long as the key cannot be observed at all,
        /// and only runs once it genuinely could have been seen landing.
        /// </summary>
        public long TimerFromMs { get; set; } = queuedAtMs;
    }

    /// <summary>See <c>PartyPoolPumpV2._dispatchOrder</c>'s identical remarks.</summary>
    private readonly Queue<ExpectedReconcileMovement?> _dispatchOrder = new();

    /// <summary>Store ids this client has genuinely SCANNED at least once (its own GUI was open
    /// on that store). Until then every belief for its keys is an ASSUMPTION, not an
    /// observation — the first real scan re-seeds them all rather than diffing against the
    /// guess. See <see cref="PumpClient"/>'s own first-sight seeding.</summary>
    private readonly HashSet<uint> _scannedStores = [];

    /// <summary>
    /// Store ids this pump has already processed at least one host snapshot for. The FIRST
    /// snapshot naming a store is that store's pristine content — the stock both instances
    /// loaded out of the same module — so every key it carries is standing shared content, never
    /// anybody's sale, and is seeded as already-agreed. A key or quantity that only appears in a
    /// LATER snapshot is a genuine host-side change and is mirrored from 0.
    ///
    /// Without this split, a client that has never opened a shop reads the host's entire
    /// published stock as missing from its own (belief 0) and blind-inserts all of it into a shop
    /// that already had it — which is the other half of docs/research/shared-inventory-v2.md
    /// §10.2's 432-insert cascade, and the reason its 18 re-inserted resrefs were the shop's
    /// whole base inventory rather than anything that round ever touched.
    /// </summary>
    private readonly HashSet<uint> _baselinedStores = [];

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

        // Which stores this tick actually looked at — folded into the two "we have a baseline
        // for this store now" sets only AFTER the whole loop, so every key of a store first seen
        // this tick is seeded consistently rather than the first one baselining the rest.
        HashSet<uint> touched = [];
        HashSet<uint> scanned = [];

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

            // ⚠ 2026-08-29 (shared-inventory-v2.md §12). First sight seeds to the host's own
            // snapshot value, not 0 — in BOTH branches, for two different reasons that land on
            // the same number:
            //
            // - Not live (this store's GUI is closed, and may never have been open here at all):
            //   there is no way to read the real local baseline, and the only defensible
            //   assumption is that this instance's own engine already holds what the host says it
            //   does — both loaded the same module content. Seeding 0 instead, as §9 did, made
            //   the very first blind reconcile "converge" a store that was already correct by
            //   inserting its ENTIRE stock again, which is half of §10.2's 432-insert cascade.
            // - Live: the same reasoning as PartyPoolPumpV2's own min() seeding, and it matters
            //   even more here — a client opening a shop the host has already published would
            //   otherwise read the shop's whole pristine stock as a fresh local DEPOSIT and
            //   report it to the host, doubling the shop. min() still reports a genuine local
            //   surplus beyond what the host knows, immediately.
            //
            // Any residual disagreement is not lost either way: the reconcile below pulls this
            // instance's engine to the host's number, which is exactly what host-authoritative
            // means (§2.1).
            touched.Add(key.StoreObjectId);

            // Re-seed rather than diff whenever this pump has no OBSERVED belief for the key:
            // never seen it at all, or (the case a blind reconcile creates) the belief it does
            // have was assumed from the host's snapshot and this is the first time the store's
            // GUI has actually been open to check. Diffing a fresh scan against an assumption
            // reports the difference between a guess and reality as a player action.
            bool firstLocalSight = !_belief.TryGetValue(key, out int belief);
            bool reseed = firstLocalSight
                || (liveThisTick && !_scannedStores.Contains(key.StoreObjectId));
            int rawMovement = 0;
            int published = 0;
            int actual = belief;

            if (liveThisTick)
            {
                scanned.Add(key.StoreObjectId);

                actual = openScan.GetValueOrDefault(key);
                if (reseed)
                {
                    belief = Math.Min(actual, snapshotValue);
                }

                rawMovement = actual - belief;
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
                            published = localChange;
                            log($"store: local {kind.ToString().ToLowerInvariant()} of {count}x "
                                + $"'{key.Resref}' detected in store 0x{key.StoreObjectId:X}, "
                                + $"requesting (serial {serial})");
                            AddOutstanding(key, serial, localChange, now);
                        }
                    }
                }
            }
            else
            {
                if (firstLocalSight)
                {
                    // A store already baselined here (by a real scan, or by its own first
                    // snapshot) has a belief for every key it held at that moment — including 0
                    // for the ones it did not stock. A key first surfacing AFTER that is
                    // therefore genuinely new: the host gained it since, and it is mirrored from
                    // 0. A store being baselined right now (this is its first snapshot) is
                    // pristine shared content instead, and is seeded as already agreed. See
                    // _baselinedStores.
                    belief = _baselinedStores.Contains(key.StoreObjectId) ? 0 : snapshotValue;
                    _belief[key] = belief;
                    actual = belief;
                }

                // ⚠ THE §10.2 RE-INSERTION LOOP LIVES HERE. `belief` cannot advance without a
                // scan, so a reconcile queued for an unobservable store is held off re-firing by
                // its expected-movement entry ALONE — and that entry used to expire on a blind
                // 10s wall clock, at which point `projected` collapsed back to `belief` and the
                // identical delta was queued again. Live: every one of a store's 18 known keys
                // re-inserted every ~10.1-10.9s, 432 inserts in 13 minutes, host clean at 0.
                // Nothing can consume the entry while nothing can scan the store, so nothing may
                // expire it either: slide its clock for exactly as long as that stays true.
                SlideExpectedTimers(key, now);
            }

            // Reconcile runs EVERY tick, live scan or not — see the class remarks (item 2). Uses
            // `belief`, never a live-only `actual`, so a store whose GUI is closed this tick can
            // still be asked to converge based on this pump's own best current estimate.
            int target = ReconcileTarget(key, belief, snapshotValue);
            int expectedNet = ExpectedFromReconcileNet(key);
            int projected = belief + expectedNet;
            int reconcileDelta = target - projected;
            if (reconcileDelta != 0)
            {
                QueueReconcile(key, reconcileDelta, now);
            }

            if (trace && (rawMovement != 0 || reconcileDelta != 0 || expectedNet != 0
                || OutstandingNet(key) != 0 || reseed))
            {
                log($"store trace: '{key.Resref}' in 0x{key.StoreObjectId:X} "
                    + $"actual={actual} belief={belief} snap={snapshotValue} raw={rawMovement} "
                    + $"expected={expectedNet} outstanding={OutstandingNet(key)} "
                    + $"published={published} target={target} reconcile={reconcileDelta} "
                    + (liveThisTick ? "live" : "blind")
                    + (reseed ? " reseed" : string.Empty));
            }
        }

        _baselinedStores.UnionWith(touched);
        _scannedStores.UnionWith(scanned);
    }

    /// <summary>Pushes every not-yet-consumed expected-movement entry for <paramref name="key"/>
    /// past this tick, so <see cref="PruneExpiredExpected"/> cannot presume it resolved. Called
    /// on every tick the key's own store is not scannable — see the call site's own remarks and
    /// <see cref="ExpectedReconcileMovement.TimerFromMs"/>.</summary>
    private void SlideExpectedTimers(StoreStackKey key, long nowMs)
    {
        if (!_expectedFromReconcile.TryGetValue(key, out List<ExpectedReconcileMovement>? entries))
        {
            return;
        }

        lock (_pendingGate)
        {
            foreach (ExpectedReconcileMovement entry in entries)
            {
                entry.TimerFromMs = nowMs;
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

        ExpectedReconcileMovement expected = new(delta, nowMs);
        if (!EnqueueApply(kind, key, count, expected))
        {
            return;
        }

        (_expectedFromReconcile.TryGetValue(key, out List<ExpectedReconcileMovement>? entries)
            ? entries
            : _expectedFromReconcile[key] = []).Add(expected);
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
            int removed;
            lock (_pendingGate)
            {
                removed = entries.RemoveAll(
                    e => e.Dispatched && nowMs - e.TimerFromMs > expectedTimeoutMs);
            }

            if (removed > 0)
            {
                log($"store: expected-reconcile entry for '{key.Resref}' in store "
                    + $"0x{key.StoreObjectId:X} never observed landing within "
                    + $"{expectedTimeoutMs / 1000}s — presumed resolved, re-evaluating fresh");
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

    private bool EnqueueApply(
        StoreOpKind kind, StoreStackKey key, ushort count,
        ExpectedReconcileMovement? expected = null)
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
            _dispatchOrder.Enqueue(expected);
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
            if (!_pendingApplies.TryDequeue(out next))
            {
                return false;
            }

            if (_dispatchOrder.TryDequeue(out ExpectedReconcileMovement? expected)
                && expected is not null)
            {
                expected.Dispatched = true;
                expected.TimerFromMs = SessionClock.NowMs;
            }

            return true;
        }
    }
}

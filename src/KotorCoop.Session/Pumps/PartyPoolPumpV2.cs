using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The shared party item-pool channel's HOST-AUTHORITATIVE v2 design (<c>party_pool_sync=2</c>).
/// See docs/research/shared-inventory-v2.md for the full design this implements and why it
/// cannot amplify the way <see cref="PartyPoolPump"/>'s (mode 1, untouched by this class) own
/// op-log did after seven root causes and six live-rig rounds.
///
/// Branches on <see cref="ICoopSession.IsEnemyAuthority"/>, the same host/client role-split idiom
/// <see cref="PartyPump"/>/<see cref="LivenessPump"/> already use — no new idiom introduced here.
///
/// <b>Host: publish, don't detect</b> (§2.4). Every tick: read the engine, publish the result as
/// an absolute snapshot unconditionally (no comparison against anything — ambient-script noise
/// is just part of what the next scan reads), then drain accepted client intents and queue one
/// engine apply per intent through the SAME <see cref="CoopAgent.PendingPartyPoolApply"/> queue
/// mode 1 already established.
///
/// <b>Client: detect, request, reconcile</b> (§2.5). Every tick, per key (the union of the host's
/// latest snapshot, this instance's own fresh scan, and any key with an intent still outstanding):
/// <c>target[key] = snapshot[key] + Σ(outstanding intent deltas for key not yet acked/timed out)</c>.
/// If the target itself moved since the LAST tick (host truth advanced — another peer's intent,
/// an ambient host-side change, anything NOT caused by this instance), the local engine is
/// reconciled toward it via the identical apply queue — never read as evidence of a local action.
/// If the target is STABLE and no reconcile is already in flight for the key, yet the local scan
/// still disagrees, that is read as a genuine local transaction (or, rarely, ambient noise this
/// instance cannot distinguish from one — see docs/research/shared-inventory-v2.md §2.2/§5#1) and
/// published as a bounded, one-shot intent. A key with a reconcile already in flight is excluded
/// from BOTH detect and reconcile until it resolves, so the reconcile's own engine write is never
/// itself misread as a fresh local action.
/// </summary>
internal sealed class PartyPoolPumpV2(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const int MaxPendingApplies = 256;

    /// <summary>Same cross-thread discipline as <see cref="PartyPoolPump"/>'s own
    /// <c>_pendingGate</c> — written here by <see cref="Pump"/>, drained by
    /// <see cref="TryDequeuePendingApply"/> from a genuinely independent thread.</summary>
    private readonly Lock _pendingGate = new();

    private readonly Queue<CoopAgent.PendingPartyPoolApply> _pendingApplies = new();

    /// <summary>Client only: the RAW host snapshot value for a key as of the LAST tick
    /// (deliberately not the full target, which also includes this pump's own outstanding-intent
    /// adjustment — see the class remarks) — used to tell "the host's own truth moved" from
    /// "nothing changed, so any local disagreement is genuinely new"
    /// (docs/research/shared-inventory-v2.md §2.5).</summary>
    private readonly Dictionary<PartyPoolStackKey, int> _lastSnapshotValue = [];

    /// <summary>Keys a reconcile apply is currently in flight for, mapped to the target it was
    /// queued toward — excluded from fresh detection until the local scan lands on that exact
    /// target (or the target itself moves again, which re-arms a fresh reconcile immediately).</summary>
    private readonly Dictionary<PartyPoolStackKey, int> _reconcileInFlight = [];

    /// <summary>When each <see cref="_reconcileInFlight"/> entry was queued — see
    /// <see cref="ReconcileInFlightTimeoutMs"/>'s own remarks for why this exists.</summary>
    private readonly Dictionary<PartyPoolStackKey, long> _reconcileQueuedAtMs = [];

    /// <summary>
    /// ⚠ A round trip fast enough to land ENTIRELY within one bridge scan-cache window (the
    /// local engine read is throttled, docs/research/shared-inventory-v2.md §3.1) is invisible
    /// to both <c>actual == inFlightTarget</c> AND <see cref="_lastActual"/>'s own "did anything
    /// move" check — the scan simply never samples the intermediate value, so from this pump's
    /// perspective NOTHING happened at all (observed live: a deposit immediately followed by a
    /// withdrawal, both landing before the next cache refresh, permanently stuck a key in
    /// <see cref="_reconcileInFlight"/> forever — no timeout, no re-arm, since neither
    /// <c>actual</c> nor the recomputed target ever differs from what was already recorded).
    /// Generous past the dispatcher's own single-slot cheat-answer timeout, matching every other
    /// in-flight timeout in this codebase's family (<c>PartyPoolPump.ApplyInFlightTimeoutMs</c>,
    /// <see cref="IntentTimeoutMs"/>): an entry older than this is presumed resolved one way or
    /// another and is dropped unconditionally, letting the very next tick's ordinary
    /// detect/reconcile logic re-evaluate the key fresh against whatever is CURRENTLY true —
    /// never a value captured once.
    /// </summary>
    private const long ReconcileInFlightTimeoutMs = 10_000;

    /// <summary>This pump's own last-observed local scan value, per key — updated EVERY tick for
    /// EVERY key seen, regardless of in-flight state. What the <see cref="_reconcileInFlight"/>
    /// branch compares against to detect "something moved" — see that branch's own remarks for
    /// why an exact match against the frozen target alone is not enough.</summary>
    private readonly Dictionary<PartyPoolStackKey, int> _lastActual = [];

    /// <summary>Client only: every intent this pump has published but not yet seen acked or
    /// timed out, per key — the <c>Σ(outstanding)</c> term in the target formula.</summary>
    private readonly Dictionary<PartyPoolStackKey, List<OutstandingIntent>> _outstanding = [];

    private readonly Dictionary<uint, PartyPoolStackKey> _outstandingBySerial = [];

    /// <summary>Generous past the dispatcher's own single-slot cheat-answer timeout — see
    /// <see cref="PartyPoolPump"/>'s identical <c>ApplyInFlightTimeoutMs</c> reasoning. If a
    /// client's own detect step is slow to notice the underlying movement is still unexplained,
    /// the re-request is merely delayed by up to one pump interval, not lost. Applies only to an
    /// intent that was NEVER acked at all — an acked one is retired by
    /// <see cref="AckGraceMs"/> instead (see that field's own remarks).</summary>
    private const long IntentTimeoutMs = 10_000;

    /// <summary>
    /// ⚠ An acked intent is NOT dropped from <see cref="_outstanding"/> the instant the ack
    /// arrives — the ack only confirms the HOST accepted it and wrote it through into its own
    /// belief; the client's NEXT scheduled snapshot BROADCAST (throttled independently, ~1s) is
    /// what actually carries that fact to this instance. Dropping on ack alone would, for one or
    /// more ticks, make <c>target</c> fall back to a stale snapshot value that has not yet
    /// caught up — read as "the local engine now disagrees with a STABLE target", which is
    /// exactly the shape a genuine local action takes, producing a SPURIOUS duplicate intent for
    /// the very same transaction that was just acked. Instead, an acked entry is marked (not
    /// removed) and kept contributing to <see cref="OutstandingNet"/> until
    /// <see cref="AckGraceMs"/> has elapsed since the ack — comfortably more than one broadcast
    /// interval (with one resend's worth of margin for a lost cycle), so by the time it is
    /// finally dropped the snapshot has almost certainly already absorbed it, and the drop
    /// itself is read as a (harmless, no-op) "target moved" rather than a fresh local action.
    /// </summary>
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
        if (!bridge.SupportsPartyPool || (caps & SessionCapabilities.PartyPool) == 0)
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
        if (!bridge.TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals))
        {
            return;
        }

        session.PublishPartyPoolSnapshotV2(totals);

        foreach (PartyPoolIntentPacketV2 intent in session.DrainPartyPoolIntentsV2())
        {
            EnqueueApply(intent.Kind, intent.Key, intent.Count);
        }
    }

    private void PumpClient()
    {
        long now = SessionClock.NowMs;

        foreach (uint serial in session.DrainAckedPartyPoolIntentsV2())
        {
            MarkAcked(serial, now);
        }

        PruneOutstanding(now);
        PruneStaleReconcileInFlight(now);

        if (!session.TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> snapshot))
        {
            // No complete cycle received yet — nothing to reconcile against, and publishing an
            // intent against an unknown baseline would very likely be wrong (the same reasoning
            // v1's own Set-with-no-known-baseline carve-out already established).
            return;
        }

        bridge.TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> scan);

        HashSet<PartyPoolStackKey> keys = new(snapshot.Keys);
        keys.UnionWith(scan.Keys);
        keys.UnionWith(_lastSnapshotValue.Keys);
        keys.UnionWith(_reconcileInFlight.Keys);
        keys.UnionWith(_outstanding.Keys);

        foreach (PartyPoolStackKey key in keys)
        {
            int snapshotValue = snapshot.GetValueOrDefault(key);
            int actual = scan.GetValueOrDefault(key);

            // `detectTarget` (snapshot + outstanding) is ONLY ever valid for the DETECTION
            // check at the bottom of this loop ("is this movement already explained, so don't
            // report ANOTHER intent") — see ReconcileTarget's own remarks for why a
            // RECONCILE-APPLY must never target it directly.
            int detectTarget = snapshotValue + OutstandingNet(key);

            // ⚠ Compares the RAW host snapshot value, never detectTarget (which also includes
            // this pump's OWN outstanding-intent adjustment) — an outstanding entry shrinking
            // (an ack) must never, by itself, read as "the host's truth changed" or "a fresh
            // local action appeared". See docs/research/shared-inventory-v2.md §2.5.
            bool firstSight = !_lastSnapshotValue.TryGetValue(key, out int priorSnapshotValue);
            bool snapshotMoved = !firstSight && priorSnapshotValue != snapshotValue;
            _lastSnapshotValue[key] = snapshotValue;

            bool actualMoved = _lastActual.TryGetValue(key, out int priorActual) && priorActual != actual;
            _lastActual[key] = actual;

            if (_reconcileInFlight.TryGetValue(key, out int inFlightTarget))
            {
                // ⚠ An exact match against the frozen target alone is not enough — a scan-cache
                // gap (the local engine read is throttled, docs/research/shared-inventory-v2.md
                // §3.1) can let the observable value skip straight PAST the target before this
                // pump ever samples it landing exactly on it (e.g. a queued apply lands at 1,
                // then a genuinely NEW local action removes it again before the next scan
                // refresh — this pump would see 0, never 1). Comparing only for an exact match
                // then leaves this key permanently stuck: neither branch below fires (`actual`
                // never equals `inFlightTarget` again, and the RE-ARM target — recomputed from a
                // snapshot that has not heard about the newer action yet — still equals
                // `inFlightTarget` too), the same permanent-stall shape `PartyPoolPump`'s own
                // root cause 6 fixed. The queued apply's own delta lands in ONE synchronous
                // engine call — there is no observable PARTIAL-landing state a poll-based scan
                // could ever catch mid-flight — so ANY movement seen for a key currently in
                // flight IS, by construction, that one apply landing (root cause 6's identical
                // argument, reused verbatim). Clear unconditionally the moment anything moves; a
                // gap still remaining (the target moved further, or a fresh action landed in the
                // very same window) is caught by the ordinary comparison below, at most one tick
                // later.
                if (actual == inFlightTarget || actualMoved)
                {
                    _reconcileInFlight.Remove(key);
                    _reconcileQueuedAtMs.Remove(key);

                    // ⚠ The reconcile that just landed may itself have been chasing the RAW
                    // snapshot on behalf of an outstanding intent this pump published earlier
                    // (ReconcileTarget's "not yet explained" branch never clears outstanding —
                    // only ITS "already explained" branch does). The instant the engine's own
                    // value reaches (or passes, in the right direction) the CURRENT snapshot,
                    // whatever outstanding entry made the reconcile necessary is now covered —
                    // retire it here, or it lingers in OutstandingNet forever and permanently
                    // mis-inflates detectTarget below, misreading the engine's own genuine
                    // agreement with the host as a phantom local removal (which becomes a fresh
                    // outstanding entry of its own — observed live: an unbroken Add/Remove flip
                    // that never settles). Scoped to exactly this landing moment rather than
                    // every tick unconditionally, so a client legitimately AHEAD of the snapshot
                    // on its own (a fresh action stacked on top, never queued through THIS
                    // reconcile) is never wrongly pulled back down to it.
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
                        // The target moved again while the earlier apply was still in flight —
                        // re-arm toward the NEW target immediately rather than waiting for the
                        // stale one to land (recomputed fresh every tick, never a value captured
                        // once).
                        QueueReconcile(key, actual, rearmTarget);
                    }
                }

                continue;
            }

            if (firstSight)
            {
                // The very first time this pump has ever looked at this key: converge UP if the
                // host already knows about more than the local engine currently shows (ordinary
                // join-time/late-discovery catch-up). If the LOCAL engine shows more than the
                // host does, this is either a fresh local action the host has never heard of, or
                // (rarely) both instances independently loaded the same pristine save content
                // and the host simply has not scanned/published yet — deliberately NOT read as
                // a local action on this very first tick (silently seeded instead), so a narrow
                // startup race cannot double-count a save's own pristine content. The SECOND
                // tick's ordinary comparison (below) correctly reports it as a genuine local
                // action if the discrepancy is still unexplained by then.
                if (actual < snapshotValue)
                {
                    QueueReconcile(key, actual, ReconcileTarget(key, actual, snapshotValue));
                }

                continue;
            }

            if (snapshotMoved)
            {
                // The host's own published truth changed since we last looked — NEVER evidence
                // of a local action, only ever something to converge toward (see
                // ReconcileTarget's own remarks for why that target is never
                // snapshotValue + OutstandingNet(key)).
                int reconcileTarget = ReconcileTarget(key, actual, snapshotValue);
                if (actual != reconcileTarget)
                {
                    QueueReconcile(key, actual, reconcileTarget);
                }

                continue;
            }

            if (actual != detectTarget)
            {
                // Host truth is stable, no reconcile pending, yet the local engine disagrees: a
                // genuine local transaction (or, rarely, ambient noise this instance cannot
                // distinguish from one — a documented, bounded residual risk, see
                // docs/research/shared-inventory-v2.md §2.2/§5#1).
                int delta = actual - detectTarget;
                PartyPoolOpKind kind = delta > 0 ? PartyPoolOpKind.Add : PartyPoolOpKind.Remove;
                ushort count = (ushort)Math.Min(Math.Abs(delta), ushort.MaxValue);

                uint serial = session.PublishPartyPoolIntentV2(kind, key, count);
                if (serial != 0)
                {
                    log($"party pool: local {kind.ToString().ToLowerInvariant()} of {count}x "
                        + $"'{key.Resref}' detected, requesting (serial {serial})");
                    AddOutstanding(key, serial, delta, now);
                }
            }
        }
    }

    private void QueueReconcile(PartyPoolStackKey key, int actual, int target)
    {
        int delta = target - actual;
        PartyPoolOpKind kind = delta > 0 ? PartyPoolOpKind.Add : PartyPoolOpKind.Remove;
        ushort count = (ushort)Math.Min(Math.Abs(delta), ushort.MaxValue);

        if (!EnqueueApply(kind, key, count))
        {
            return;
        }

        _reconcileInFlight[key] = target;
        _reconcileQueuedAtMs[key] = SessionClock.NowMs;
    }

    /// <summary>Drops any <see cref="_reconcileInFlight"/> entry that has been waiting longer
    /// than <see cref="ReconcileInFlightTimeoutMs"/> — see that field's own remarks for the
    /// permanent-stall shape this guards against. Called BEFORE the per-key loop builds its
    /// key set, so a freed key is immediately eligible for fresh detection/reconcile the SAME
    /// tick rather than waiting one more.</summary>
    private void PruneStaleReconcileInFlight(long nowMs)
    {
        List<PartyPoolStackKey>? expired = null;
        foreach ((PartyPoolStackKey key, long queuedAtMs) in _reconcileQueuedAtMs)
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

        foreach (PartyPoolStackKey key in expired)
        {
            _reconcileInFlight.Remove(key);
            _reconcileQueuedAtMs.Remove(key);
            log($"party pool: reconcile for '{key.Resref}' never observed landing within "
                + $"{ReconcileInFlightTimeoutMs / 1000}s — presumed resolved, re-evaluating fresh");
        }
    }

    private bool EnqueueApply(PartyPoolOpKind kind, PartyPoolStackKey key, ushort count)
    {
        lock (_pendingGate)
        {
            if (_pendingApplies.Count >= MaxPendingApplies)
            {
                log($"party pool v2: dropping apply for '{key.Resref}' — already holding "
                    + $"{MaxPendingApplies} queued, the drain has fallen behind");
                return false;
            }

            _pendingApplies.Enqueue(new CoopAgent.PendingPartyPoolApply(kind, key, count));
            return true;
        }
    }

    /// <summary>
    /// The value this instance's OWN engine should be reconciled TOWARD for <paramref name="key"/>
    /// — deliberately NEVER <c>snapshotValue + OutstandingNet(key)</c>. That sum is only ever
    /// valid for the DETECTION check ("is this local movement already explained by an intent I
    /// already published, so don't report another one") — a RECONCILE-APPLY toward it
    /// double-counts, because this pump's own outstanding intents are facts THIS instance
    /// already applied to its own engine the moment it detected and published them (that is what
    /// <paramref name="actual"/> already reflects); the host's snapshot catching up to include
    /// that same contribution must never be added a SECOND time on top (observed live: a
    /// client's own +3 deposit, once mirrored back in the host's next snapshot, reconciled UP to
    /// +6 — the snapshot already said 3, and the still-outstanding +3 got added again).
    ///
    /// Once the engine's own current value already sits at or past the raw snapshot in the
    /// outstanding intents' own direction, they are moot — retired outright (not merely
    /// excluded from this one computation) rather than waiting out <see cref="AckGraceMs"/>,
    /// since genuine agreement, not a merely-stale snapshot, is exactly what was just observed;
    /// the target then IS <paramref name="actual"/> itself (nothing to apply). Otherwise —
    /// outstanding intents that have NOT yet been reflected — reconcile toward the RAW snapshot
    /// value alone; outstanding never contributes to what gets APPLIED, only to whether a fresh
    /// local action gets reported.
    /// </summary>
    private int ReconcileTarget(PartyPoolStackKey key, int actual, int snapshotValue)
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

    private int OutstandingNet(PartyPoolStackKey key)
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

    private void AddOutstanding(PartyPoolStackKey key, uint serial, int delta, long nowMs)
    {
        (_outstanding.TryGetValue(key, out List<OutstandingIntent>? entries)
            ? entries
            : _outstanding[key] = []).Add(new OutstandingIntent(serial, delta, nowMs));
        _outstandingBySerial[serial] = key;
    }

    private void RemoveOutstanding(uint serial)
    {
        if (!_outstandingBySerial.Remove(serial, out PartyPoolStackKey key)
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

    /// <summary>Retires EVERY outstanding entry for one key at once — the raw snapshot already
    /// matching this instance's own engine (see the <c>snapshotMoved</c> branch above) means the
    /// host has genuinely caught up, so nothing further is owed for this key regardless of any
    /// entry's own ack/timeout state.</summary>
    private void ClearOutstanding(PartyPoolStackKey key)
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

    /// <summary>An ack arrived — marks the entry (does NOT remove it yet); see
    /// <see cref="AckGraceMs"/>'s own remarks for why.</summary>
    private void MarkAcked(uint serial, long nowMs)
    {
        if (_outstandingBySerial.TryGetValue(serial, out PartyPoolStackKey key)
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

    /// <summary>Retires an outstanding entry once it is genuinely safe to stop crediting it:
    /// either it was NEVER acked and <see cref="IntentTimeoutMs"/> has elapsed since it was sent
    /// (presumed lost — the next detect pass re-requests it if the movement is still
    /// unexplained), or it WAS acked and <see cref="AckGraceMs"/> has elapsed since the ack (the
    /// snapshot has almost certainly caught up to it by then — see that field's own
    /// remarks).</summary>
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

    /// <summary>Called by whatever already arbitrates the single-slot staging-cheat channel, at
    /// most once per ITS tick — mirrors <see cref="PartyPoolPump.TryDequeuePendingApply"/>
    /// exactly.</summary>
    public bool TryDequeuePendingApply(out CoopAgent.PendingPartyPoolApply next)
    {
        lock (_pendingGate)
        {
            return _pendingApplies.TryDequeue(out next);
        }
    }
}

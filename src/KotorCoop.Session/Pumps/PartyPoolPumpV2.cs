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
/// <b>Client: detect, request, reconcile</b> (§2.5, redesigned §9 2026-08-29). Every tick, per
/// key: a running <see cref="_belief"/> value (this pump's own last-observed local scan) is
/// compared against the FRESH scan; the raw movement since <see cref="_belief"/> is netted
/// against <see cref="_expectedFromReconcile"/> — the ledger of every reconcile-apply THIS pump
/// itself queued against its own engine, not yet confirmed landed — consuming what that ledger
/// explains oldest-first. Only the UNEXPLAINED remainder is ever reported as a fresh local
/// intent; <see cref="_belief"/> advances to the fresh scan value regardless of whether anything
/// was reported. This directly replaces the single-value, exact-match-adjacent
/// <c>_reconcileInFlight</c> exclusion this class used before §9 — see §9's own write-up for the
/// live-observed defect that design still had (a reconcile-apply's own landing occasionally
/// slipping past the exclusion window and being re-detected as a fresh local change, once other
/// outstanding-ledger bookkeeping for the same key was also in play).
///
/// Separately (and independent of detection), every tick a key's <see cref="ReconcileTarget"/> is
/// compared against what THIS pump's own already-queued-but-unconfirmed reconciles project the
/// engine to reach (<c>belief + ExpectedFromReconcileNet</c>); any gap queues one more reconcile
/// for exactly the remainder — never a value captured once, always fresh against whatever is
/// currently true. <see cref="_outstanding"/> (this instance's own published-but-unconfirmed
/// intents to the HOST) is a completely separate ledger, retired only by an ack + grace window or
/// by the host's own snapshot genuinely moving in the outstanding entries' own direction
/// (<see cref="RetireOutstandingBy"/>, §7's fix, unchanged) — it exists solely so
/// <see cref="ReconcileTarget"/> never reconciles away a local deposit the host has not echoed
/// back yet, and never feeds the (now belief-based) detect step at all.
/// </summary>
internal sealed class PartyPoolPumpV2(
    IGameBridge bridge,
    ICoopSession session,
    Action<string> log,
    bool trace = false,
    long expectedTimeoutMs = PartyPoolPumpV2.DefaultExpectedTimeoutMs)
{
    private const int MaxPendingApplies = 256;

    /// <summary>Same cross-thread discipline as <see cref="PartyPoolPump"/>'s own
    /// <c>_pendingGate</c> — written here by <see cref="Pump"/>, drained by
    /// <see cref="TryDequeuePendingApply"/> from a genuinely independent thread.</summary>
    private readonly Lock _pendingGate = new();

    private readonly Queue<CoopAgent.PendingPartyPoolApply> _pendingApplies = new();

    /// <summary>Client only: the RAW host snapshot value for a key as of the LAST tick — used
    /// only to drive <see cref="RetireOutstandingBy"/> (§7's own evidence-based outstanding
    /// retirement, unchanged by §9) — never consulted by the detect step any more (see the class
    /// remarks).</summary>
    private readonly Dictionary<PartyPoolStackKey, int> _lastSnapshotValue = [];

    /// <summary>Client only: this pump's own last-observed LOCAL scan value per key — the anchor
    /// the belief-delta detect step in <see cref="PumpClient"/> measures every fresh scan
    /// against. Advances to the fresh scan value every tick, unconditionally, whether or not the
    /// movement since the last tick was reported as anything (§9, 2026-08-29).</summary>
    private readonly Dictionary<PartyPoolStackKey, int> _belief = [];

    /// <summary>Client only: every reconcile-apply this pump has queued against its OWN engine
    /// for a key, not yet confirmed landed by an observed scan movement — the ledger the detect
    /// step nets a fresh raw movement against before reporting anything as a genuine local
    /// change (§9, 2026-08-29, replacing the old single-value <c>_reconcileInFlight</c>
    /// exclusion). Consumed oldest-first, partially or fully, by <see cref="ConsumeExpected"/>;
    /// pruned by <see cref="PruneExpiredExpected"/> after <see cref="ExpectedTimeoutMs"/> for an
    /// entry whose landing was never observed (a scan-cache gap swallowed the intermediate value
    /// entirely — the exact permanent-stall shape root cause 6/§7 already named — presumed
    /// resolved rather than left stuck forever).</summary>
    private readonly Dictionary<PartyPoolStackKey, List<ExpectedReconcileMovement>> _expectedFromReconcile = [];

    /// <summary>Generous past the dispatcher's own single-slot cheat-answer timeout, matching
    /// every other in-flight timeout in this codebase's family (<c>PartyPoolPump
    /// .ApplyInFlightTimeoutMs</c>, <see cref="IntentTimeoutMs"/>) — see
    /// <see cref="_expectedFromReconcile"/>'s own remarks for why an entry can otherwise never
    /// resolve on its own. Overridable ONLY through this class's own constructor, and only so a
    /// unit test can drive the expiry path in milliseconds instead of waiting out ten real
    /// seconds (<c>SharedInventoryV2PumpTests</c>); production always takes the default.</summary>
    internal const long DefaultExpectedTimeoutMs = 10_000;

    /// <summary>The bridge's own <see cref="IPartyPoolBridge.PartyPoolScanEpoch"/> as of the last
    /// tick — a change means the bridge dropped bookkeeping the totals depend on (a module
    /// change), so the step in what it reports is NOT engine movement and must never be detected
    /// as a local change. See <see cref="ResetForNewScanEpoch"/>.</summary>
    private int _lastScanEpoch;

    private bool _everPumped;

    /// <summary>Client only: every intent this pump has published but not yet seen acked or
    /// timed out, per key — the <c>Σ(outstanding)</c> term <see cref="ReconcileTarget"/> reads to
    /// avoid reconciling away a local deposit the host has not echoed back yet. NOT consulted by
    /// the detect step (§9, 2026-08-29 — see the class remarks for why conflating the two was the
    /// root cause this pass closed).</summary>
    private readonly Dictionary<PartyPoolStackKey, List<OutstandingIntent>> _outstanding = [];

    private readonly Dictionary<uint, PartyPoolStackKey> _outstandingBySerial = [];

    // A refused engine apply must never make the next reconcile delete the item that the player
    // still owns locally. Retry a few times, then hold that key out of automatic reconciliation
    // and leave the item visible for manual recovery.
    private readonly Dictionary<PartyPoolStackKey, FailedIntentState> _failedIntentStates = [];

    private const int MaxFailedIntentRetries = 3;

    private sealed class FailedIntentState(int failures)
    {
        public int Failures { get; set; } = failures;
    }

    /// <summary>Generous past the dispatcher's own single-slot cheat-answer timeout — see
    /// <see cref="PartyPoolPump"/>'s identical <c>ApplyInFlightTimeoutMs</c> reasoning. If a
    /// client's own detect step is slow to notice the underlying movement is still unexplained,
    /// the re-request is merely delayed by up to one pump interval, not lost. Applies only to an
    /// intent that was NEVER acked at all — an acked one is retired by
    /// <see cref="AckGraceMs"/> instead (see that field's own remarks).</summary>
    private const long IntentTimeoutMs = 10_000;

    /// <summary>
    /// ⚠ An acked intent is NOT dropped from <see cref="_outstanding"/> the instant the ack
    /// arrives — the ack only confirms the HOST accepted it and QUEUED it for its own engine to
    /// apply; the client's NEXT scheduled snapshot BROADCAST (throttled independently, ~1s), once
    /// the host's own scan has actually observed the apply land, is what carries that fact to
    /// this instance. Dropping on ack alone would, for one or more ticks, let
    /// <see cref="ReconcileTarget"/> fall back to a stale raw snapshot value that has not yet
    /// caught up — read as "the host's own truth disagrees with my local deposit", producing a
    /// reconcile that erases it until the snapshot catches up (§5's own "Bug 2"). Instead, an
    /// acked entry is marked (not removed) and kept contributing to <see cref="OutstandingNet"/>
    /// until EITHER the snapshot is observed to have genuinely moved to reflect it
    /// (<see cref="RetireOutstandingBy"/>, the primary and normally-fast path) OR, as a
    /// last-resort safety net, <see cref="AckGraceMs"/> has elapsed since the ack.
    ///
    /// <b>§7 (2026-08-29)</b>: this safety net used to be 2,500&#160;ms — raised to a value that
    /// comfortably outlasts a realistic backlog of several accepted intents draining one at a
    /// time through the single-slot arbitrated cheat channel, rather than assuming a single
    /// in-flight apply.
    /// </summary>
    private const long AckGraceMs = 15_000;

    private sealed class OutstandingIntent(uint serial, int delta, long queuedAtMs)
    {
        public uint Serial { get; } = serial;

        /// <summary>Mutable: <see cref="RetireOutstandingBy"/> shrinks this toward zero as
        /// confirmed snapshot movement partially covers it, rather than only ever removing the
        /// whole entry outright.</summary>
        public int Delta { get; set; } = delta;

        public long QueuedAtMs { get; } = queuedAtMs;
        public long AckedAtMs { get; set; } = -1;
    }

    /// <summary>One reconcile-apply this pump queued against its own engine, awaiting
    /// confirmation via an observed scan movement — see <see cref="_expectedFromReconcile"/>'s
    /// own remarks. Mutable <see cref="Delta"/> so <see cref="ConsumeExpected"/> can shrink an
    /// entry that only partially explains an observed movement rather than only ever consuming
    /// it whole.</summary>
    private sealed class ExpectedReconcileMovement(int delta, long queuedAtMs)
    {
        public int Delta { get; set; } = delta;

        public long QueuedAtMs { get; } = queuedAtMs;

        /// <summary>
        /// False until the apply this entry stands for has actually been handed to the
        /// single-slot arbitrated cheat channel (<see cref="TryDequeuePendingApply"/>). While it
        /// is still sitting in this pump's OWN queue it definitionally cannot have landed, so
        /// <see cref="PruneExpiredExpected"/> must never presume it resolved — the whole point of
        /// the "presumed resolved" timeout is a landing this pump could not OBSERVE, not one that
        /// has not been attempted yet. A burst of applies drains one per dispatcher poll, so a
        /// wall-clock timer started at QUEUE time expires the tail of any long burst while it is
        /// still queued, which is exactly the "expected entry vanishes, the gap re-fires" shape
        /// that produced the live re-insertion loops (docs/research/shared-inventory-v2.md §10.2).
        ///
        /// Written under the same <c>_pendingGate</c> lock that owns the apply queue, and read
        /// under it too — the dispatcher drain runs on a genuinely independent thread.
        /// </summary>
        public bool Dispatched { get; set; }

        /// <summary>When this entry's expiry clock started running: the dispatch moment, not the
        /// queue moment — see <see cref="Dispatched"/>. Also slid forward for as long as the
        /// key cannot be observed at all, which the store's own sibling pump needs (a store whose
        /// GUI is closed can never be scanned, so nothing can ever consume the entry).</summary>
        public long TimerFromMs { get; set; } = queuedAtMs;
    }

    /// <summary>Every <see cref="ExpectedReconcileMovement"/> in the exact order its apply was
    /// enqueued, so <see cref="TryDequeuePendingApply"/> can stamp the right one dispatched.
    /// <c>null</c> for a HOST-side apply (a drained client intent), which has no expected entry
    /// of its own — the two queues must stay index-for-index aligned regardless. Guarded by
    /// <see cref="_pendingGate"/>, exactly like <see cref="_pendingApplies"/> itself.</summary>
    private readonly Queue<ExpectedReconcileMovement?> _dispatchOrder = new();

    public void Pump(SessionCapabilities caps, string localModule = "")
    {
        if (!bridge.SupportsPartyPool || (caps & SessionCapabilities.PartyPool) == 0)
        {
            return;
        }

        if (session.IsEnemyAuthority)
        {
            PumpHost(localModule);
        }
        else
        {
            PumpClient(localModule);
        }
    }

    private void PumpHost(string localModule)
    {
        // A stunt module's party pool repository is a cutscene-only world (see
        // StuntModuleRule) — do not scan-and-publish it, and do not drain/apply intents
        // against it either (an accepted deposit applied to whatever the stunt module's own
        // pool object happens to be would be a write into a world that is about to be torn
        // down, not the shared one). Hold everything exactly as it stood the tick before this
        // host entered the stunt module: `_lastScanEpoch`/`_everPumped` are deliberately left
        // untouched here, so the epoch-change withdraw-and-resweep below still fires correctly
        // the moment this host is back in a real module and the bridge's own bookkeeping has
        // moved on from whatever it was before the cutscene.
        if (StuntModuleRule.IsStuntModule(localModule))
        {
            return;
        }

        // The bridge dropped its scan bookkeeping (the host's own world was replaced — a reload
        // of the same or another module, IWorldStateBridge.WorldEpoch). Whatever this host last
        // published described the OLD world's pool; stop broadcasting it until a fresh sweep of
        // the new one lands below. Live 2026-09-02 (shared-inventory-v2.md §17): a client that
        // rejoined after the host's in-world reload was handed the old totals as truth.
        int epoch = bridge.PartyPoolScanEpoch;
        if (_everPumped && epoch != _lastScanEpoch)
        {
            session.WithdrawPartyPoolSnapshotV2();
            log("party pool: the bridge reset its own scan bookkeeping (module change) — "
                + "withdrawing the published snapshot until the new world has been scanned");
        }

        _lastScanEpoch = epoch;
        _everPumped = true;

        // A stale scan hands back the last GOOD totals — an honest answer for a client's delta
        // step, but not a fresh statement of truth to broadcast: after a reload it is the old
        // world's pool until the bridge has swept the new one.
        if (!bridge.TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> totals))
        {
            return;
        }

        if (!bridge.LastPartyPoolScanWasStale)
        {
            session.PublishPartyPoolSnapshotV2(totals);
        }

        if (trace)
        {
            TraceHostSnapshot(totals);
        }

        // Intents still drain on a stale tick: an accepted deposit must not wait out a 15 s
        // item-tag rebuild window on the host before it is even attempted.
        bool queuedAny = false;
        foreach (PartyPoolIntentPacketV2 intent in session.DrainPartyPoolIntentsV2())
        {
            bool queued = EnqueueApply(intent.Kind, intent.Key, intent.Count, expected: null,
                intentPeerId: intent.PeerId, intentSerial: intent.IntentSerial);
            queuedAny |= queued;
            if (!queued)
            {
                // The wire layer deduped this intent, so it will not be delivered again as a
                // new queue entry. Refuse it explicitly; otherwise the sender retransmits it
                // forever with no result and its local stack remains permanently outstanding.
                session.CompletePartyPoolIntentV2(intent.PeerId, intent.IntentSerial, accepted: false);
            }
        }

        if (queuedAny)
        {
            // The host's own engine now owes an apply; its next PUBLISH must reflect the landing
            // as soon as it is true rather than a cache window later, or a client waiting on the
            // echo of its own intent sits on a stale snapshot for up to a full window and its
            // outstanding ledger drifts against it.
            bridge.InvalidatePartyPoolScanCache();
        }
    }

    /// <summary>Rig-only (<c>party_pool_trace</c>): the HOST's side of the same trace the client
    /// prints per key — what it is actually broadcasting, logged only when it changes, so a
    /// client's "snap=0" for a key the host visibly holds can be read against the host's own
    /// scan (stale, suppressed as ambient, or unresolved) instead of guessed at.</summary>
    private string _lastTracedSnapshot = string.Empty;

    private void TraceHostSnapshot(IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        string rendered = string.Join(", ", totals
            .OrderBy(kv => kv.Key.Resref, StringComparer.Ordinal)
            .Select(kv => $"{kv.Key.Resref}/c{kv.Key.Charges}/m{kv.Key.MaxCharges}/u{kv.Key.Upgrades}/f{kv.Key.Flags}={kv.Value}"));
        string line = $"{(bridge.LastPartyPoolScanWasStale ? "stale " : string.Empty)}{totals.Count} key(s): {rendered}";
        if (line == _lastTracedSnapshot)
        {
            return;
        }

        _lastTracedSnapshot = line;
        log($"party pool trace: host snapshot {line}");
    }

    private void PumpClient(string localModule)
    {
        // A client in a stunt module or between lives has no authoritative local pool
        // observation. Its host snapshot still describes the last real world, so treating
        // the stunt/load pool as empty would manufacture removes/adds during HostReload.
        // Keep all ledgers and pending intents intact; the first valid scan after adoption
        // will resume reconciliation against the host snapshot.
        if (StuntModuleRule.IsStuntModule(localModule) || bridge.IsWorldBetweenLives)
        {
            return;
        }

        long now = SessionClock.NowMs;

        foreach (PartyPoolIntentResultV2 result in session.DrainPartyPoolIntentResultsV2())
        {
            if (result.Accepted)
            {
                _failedIntentStates.Remove(result.Intent.Key);
                MarkAcked(result.Intent.IntentSerial, now);
            }
            else
            {
                HandleRejectedIntent(result.Intent, now);
            }
        }

        // Compatibility for sessions that still expose only the serial drain.
        foreach (uint serial in session.DrainAckedPartyPoolIntentsV2())
        {
            MarkAcked(serial, now);
        }

        PruneOutstanding(now);

        if (!session.TryGetPartyPoolSnapshotV2(out IReadOnlyDictionary<PartyPoolStackKey, int> snapshot))
        {
            // No complete cycle received yet — nothing to reconcile against, and publishing an
            // intent against an unknown baseline would very likely be wrong (the same reasoning
            // v1's own Set-with-no-known-baseline carve-out already established).
            return;
        }

        // ⚠ 2026-08-29 (shared-inventory-v2.md §12): the return value is NOT optional. The bridge
        // documents its own contract explicitly — "False when there is no world loaded yet
        // (main menu, loading screen) or the read otherwise failed — the caller MUST NOT treat
        // that as 'the pool is empty'" — and hands back an EMPTY dictionary in exactly that case
        // (KotorGameBridge.PartyPool.cs returns `_partyPoolLastScan`, which is empty until the
        // first successful sweep). Ignoring it made `actual` read 0 for every key at once, which
        // this delta-based detect step then reads as the player having emptied the entire pool in
        // one tick — a phantom REMOVE per key, published to the host and applied there — followed
        // by the mirror-image phantom ADD the moment the scan starts working again. A failed scan
        // is NO OBSERVATION: do nothing at all this tick, including no reconcile (a reconcile
        // computed against a value we did not actually read is just as wrong as a detection is).
        //
        // ⚠ 2026-08-29 (shared-inventory-v2.md §15): "the read failed" is not the only way a tick
        // can carry no observation. Every failure path in the real bridge hands back the LAST
        // GOOD scan and returns TRUE — "stale but honest" — and a delta detect step is fine with
        // that (an unchanged value moves nothing). The EXPIRY clock is not. A freshly created
        // item's id cannot be resolved to a resref until the item-tag cache is rebuilt, and
        // EnsureItemKnown refuses to rebuild for 15s, which is longer than this pump's own 10s
        // expected-movement timeout: a reconcile apply that landed correctly went unobservable
        // for the whole window, was "presumed resolved", re-queued and applied a SECOND time.
        // Watched happen 20 seconds into a live session, doubling a frag grenade. So a stale tick
        // is treated exactly like a failed one — nothing detected, nothing reconciled — and every
        // in-flight expiry clock is slid past it, since a clock that runs while nothing can be
        // seen is timing the blindness rather than the apply. Same fix, same reasoning, as §12.1's
        // own SlideExpectedTimers on the store side.
        if (!bridge.TryScanPartyPoolTotals(out IReadOnlyDictionary<PartyPoolStackKey, int> scan)
            || bridge.LastPartyPoolScanWasStale)
        {
            SlideExpectedTimers(now);
            return;
        }

        PruneExpiredExpected(now);

        // A module change drops the bridge's ambient-noise baseline, so the same unchanged pool
        // can report bigger totals than it did a tick ago (KotorGameBridge.PartyPool.cs's own
        // _partyPoolAmbientBaseline). That step is not engine movement; re-seed rather than
        // detect it. Covers the same ground for any future bridge-side bookkeeping reset.
        int epoch = bridge.PartyPoolScanEpoch;
        if (_everPumped && epoch != _lastScanEpoch)
        {
            ResetForNewScanEpoch();
        }

        _lastScanEpoch = epoch;
        _everPumped = true;

        HashSet<PartyPoolStackKey> keys = new(snapshot.Keys);
        keys.UnionWith(scan.Keys);
        keys.UnionWith(_belief.Keys);
        keys.UnionWith(_expectedFromReconcile.Keys);
        keys.UnionWith(_outstanding.Keys);

        foreach (PartyPoolStackKey key in keys)
        {
            int snapshotValue = snapshot.GetValueOrDefault(key);
            int actual = scan.GetValueOrDefault(key);

            // Drives ONLY RetireOutstandingBy (§7's own evidence-based outstanding retirement) —
            // completely decoupled from the belief-delta detect step below (§9).
            bool firstSnapshotSight = !_lastSnapshotValue.TryGetValue(key, out int priorSnapshotValue);
            if (!firstSnapshotSight && priorSnapshotValue != snapshotValue)
            {
                RetireOutstandingBy(key, snapshotValue - priorSnapshotValue);
            }

            _lastSnapshotValue[key] = snapshotValue;

            // §9 (2026-08-29): the belief-delta detect step. Replaces the old single-value
            // `_reconcileInFlight` exclusion — this key is NEVER skipped wholesale any more, so
            // a reconcile queued for it and a genuine, independent local action for it can
            // coexist and both resolve correctly, rather than one hiding behind the other's
            // frozen target. `rawMovement` is whatever actually happened to the engine since the
            // last tick, for ANY reason; ConsumeExpected nets out exactly the portion THIS pump's
            // own still-outstanding reconciles explain (oldest-first), leaving only a genuine
            // unexplained remainder to ever be reported.
            //
            // Deliberately NO special "first sight" branch: an EARLIER version of this method
            // seeded `_belief[key] = actual` unconditionally on the very first observation and
            // `continue`d, mirroring the old absolute-target design's "defer one tick" idiom —
            // but a DELTA-based design can never recover from that seed, because nothing ever
            // moves again to re-surface the standing value it silently absorbed (found live via
            // ThreeRapidClientDepositsConvergeToExactlySumOnBothSides: a genuine +3 local action
            // already sitting in the engine before this pump ever looked at the key was eaten
            // permanently, never published). Defaulting `belief` to 0 for a truly new key and
            // running the SAME detect/reconcile logic as any other tick reports a standing
            // surplus immediately instead of deferring it — a strictly EARLIER, not incorrect,
            // report; the "two instances independently loaded identical pristine content" risk
            // this used to defer against is the same accepted, bounded, at-most-one-time
            // over-count docs/research/shared-inventory-v2.md §2.4 already documents (and
            // IdenticalAmbientNoiseOnBothSidesConvergesWithoutASecondRoundOfAmplification already
            // tolerates both outcomes).
            // ⚠ 2026-08-29 (shared-inventory-v2.md §12): first sight seeds to
            // min(actual, snapshotValue), NOT to 0.
            //
            // §9 removed the old "seed silently and defer a tick" branch for a real reason (a
            // delta design can never recover a value it silently absorbs) and replaced it with a
            // flat 0 — which publishes a key's ENTIRE standing quantity as a fresh local deposit
            // the first time this pump ever looks at it. At session start both instances have
            // loaded the SAME save and hold the SAME pool, so that is a guaranteed
            // publish-then-double of every stack in the stash, on the very first tick the pump
            // and the first snapshot coincide. The minimum splits the two cases apart with no
            // heuristic at all: whatever the host ALREADY agrees this key holds is standing
            // shared content and is never anybody's deposit; anything this instance holds BEYOND
            // that is a genuine local surplus and is still reported immediately, exactly as §9
            // requires. (actual=3/snapshot=0 -> belief 0, publishes +3, §9's own regression case;
            // actual=5/snapshot=5 -> belief 5, publishes nothing; actual=0/snapshot=5 -> belief
            // 0, publishes nothing and reconciles UP.)
            bool firstLocalSight = !_belief.TryGetValue(key, out int belief);
            if (firstLocalSight)
            {
                belief = Math.Min(actual, snapshotValue);
            }

            int rawMovement = actual - belief;
            _belief[key] = actual;

            int published = 0;
            if (rawMovement != 0)
            {
                int localChange = ConsumeExpected(key, rawMovement);
                if (localChange != 0)
                {
                    PartyPoolOpKind kind = localChange > 0 ? PartyPoolOpKind.Add : PartyPoolOpKind.Remove;
                    ushort count = (ushort)Math.Min(Math.Abs(localChange), ushort.MaxValue);

                    uint serial = session.PublishPartyPoolIntentV2(kind, key, count);
                    if (serial != 0)
                    {
                        published = localChange;
                        log($"party pool: local {kind.ToString().ToLowerInvariant()} of {count}x "
                            + $"'{key.Resref}' detected, requesting (serial {serial})");
                        AddOutstanding(key, serial, localChange, now);

                        // A genuine local change just happened — the bridge's own scan cache (up
                        // to 1s stale) must not mask a SECOND rapid change to this or any other
                        // key behind the rest of that window. The identical call mode 1's own
                        // PublishDetected makes, for the identical reason
                        // (docs/research/party-pool-sync.md §14); §2.8 always claimed mode 2 made
                        // it too and it never did.
                        bridge.InvalidatePartyPoolScanCache();
                    }
                }
            }

            // Independent of detection: does the engine still need to move toward the host's
            // truth, accounting for whatever this pump has already asked for but not yet seen
            // land? Recomputed fresh every tick — never a value captured once — so a target that
            // moves again while an earlier reconcile is still in flight is caught the very next
            // tick, and a target that is already fully covered by what is already queued queues
            // nothing further.
            if (IsFailedIntentProtected(key, actual, snapshotValue))
            {
                continue;
            }

            int target = ReconcileTarget(key, actual, snapshotValue);
            int expectedNet = ExpectedFromReconcileNet(key);
            int projected = actual + expectedNet;
            int reconcileDelta = target - projected;
            if (reconcileDelta != 0)
            {
                QueueReconcile(key, reconcileDelta, now);
            }

            if (trace && (rawMovement != 0 || reconcileDelta != 0 || expectedNet != 0
                || OutstandingNet(key) != 0 || firstLocalSight))
            {
                log($"party pool trace: '{key.Resref}'/c{key.Charges}/m{key.MaxCharges}/u{key.Upgrades}/f{key.Flags} actual={actual} belief={belief} "
                    + $"snap={snapshotValue} raw={rawMovement} expected={expectedNet} "
                    + $"outstanding={OutstandingNet(key)} published={published} "
                    + $"target={target} reconcile={reconcileDelta}"
                    + (firstLocalSight ? " firstSight" : string.Empty));
            }
        }
    }

    /// <summary>The bridge reset bookkeeping the totals depend on (a module change). Everything
    /// this pump believes about LOCAL values is measured against those totals, so all of it is
    /// now meaningless — drop it and let the per-key first-sight seeding above rebuild it from
    /// whatever is true now. Deliberately keeps <see cref="_outstanding"/>: those are real
    /// requests genuinely in flight to the host, and they retire on their own timers.</summary>
    private void ResetForNewScanEpoch()
    {
        _belief.Clear();
        _lastSnapshotValue.Clear();
        _expectedFromReconcile.Clear();

        log("party pool: the bridge reset its own scan bookkeeping (module change) — re-seeding "
            + "local belief rather than reading the step as engine movement");
    }

    private void QueueReconcile(PartyPoolStackKey key, int delta, long nowMs)
    {
        if (delta == 0)
        {
            return;
        }

        PartyPoolOpKind kind = delta > 0 ? PartyPoolOpKind.Add : PartyPoolOpKind.Remove;
        ushort count = (ushort)Math.Min(Math.Abs(delta), ushort.MaxValue);

        ExpectedReconcileMovement expected = new(delta, nowMs);
        if (!EnqueueApply(kind, key, count, expected))
        {
            return;
        }

        (_expectedFromReconcile.TryGetValue(key, out List<ExpectedReconcileMovement>? entries)
            ? entries
            : _expectedFromReconcile[key] = []).Add(expected);

        // This instance now owes its own engine an apply — the very next scan needs to be fresh
        // so the landing (and anything else that moves meanwhile) is seen as soon as it is true,
        // not delayed behind a cache window that started ticking before we had any reason to look
        // again. Mode 1 has always done this (PartyPoolPump's own reconcile loop); §2.8 claimed
        // mode 2 did too and it never did, so a landed reconcile could be read out of a stale
        // cache one full window later and mistaken for a fresh local action.
        bridge.InvalidatePartyPoolScanCache();
    }

    /// <summary>
    /// Nets <paramref name="rawMovement"/> (the engine's OWN observed movement since
    /// <see cref="_belief"/> was last recorded for <paramref name="key"/>, whatever the cause)
    /// against <see cref="_expectedFromReconcile"/>, consuming what it explains oldest-first, and
    /// returns whatever remains unexplained — the only thing ever safe to report as a genuine
    /// local change. A movement fully explained by this pump's own queued reconciles returns 0;
    /// a movement in the OPPOSITE direction of every pending entry (a genuine local action
    /// happening while an unrelated reconcile is also in flight for the same key) is left
    /// entirely unconsumed and returned whole, exactly like <see cref="RetireOutstandingBy"/>'s
    /// identical direction-gated logic.
    /// </summary>
    private int ConsumeExpected(PartyPoolStackKey key, int rawMovement)
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

    private int ExpectedFromReconcileNet(PartyPoolStackKey key)
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

    /// <summary>Drops any <see cref="_expectedFromReconcile"/> entry that has been waiting longer
    /// than <see cref="ExpectedTimeoutMs"/> without being consumed — see that field's own
    /// remarks for the permanent-stall shape this guards against (a scan-cache gap swallowing the
    /// intermediate value entirely). Called BEFORE the per-key loop builds its key set, so a
    /// freed entry no longer suppresses that key's detect step the SAME tick rather than waiting
    /// one more.</summary>
    /// <summary>Pushes every not-yet-consumed expected-movement entry past this tick, so
    /// <see cref="PruneExpiredExpected"/> cannot presume it resolved. Called on every tick the
    /// pool could not actually be OBSERVED — see the call site's own remarks and
    /// <see cref="ExpectedReconcileMovement.TimerFromMs"/>. The store pump's own
    /// <c>SlideExpectedTimers</c> is the per-key version of this (a store can be unscannable on
    /// its own while others are fine; the party pool is one repository, so it is all or
    /// nothing).</summary>
    private void SlideExpectedTimers(long nowMs)
    {
        lock (_pendingGate)
        {
            foreach ((_, List<ExpectedReconcileMovement> entries) in _expectedFromReconcile)
            {
                foreach (ExpectedReconcileMovement entry in entries)
                {
                    entry.TimerFromMs = nowMs;
                }
            }
        }
    }

    private void PruneExpiredExpected(long nowMs)
    {
        List<PartyPoolStackKey>? emptied = null;
        foreach ((PartyPoolStackKey key, List<ExpectedReconcileMovement> entries) in _expectedFromReconcile)
        {
            int removed;
            lock (_pendingGate)
            {
                // Dispatched-gated: an entry whose apply has not even left this pump's own queue
                // yet CANNOT have landed, so "presumed resolved" would be a lie. See
                // ExpectedReconcileMovement.Dispatched.
                removed = entries.RemoveAll(
                    e => e.Dispatched && nowMs - e.TimerFromMs > expectedTimeoutMs);
            }

            if (removed > 0)
            {
                log($"party pool: expected-reconcile entry for '{key.Resref}' never observed "
                    + $"landing within {expectedTimeoutMs / 1000}s — presumed resolved, "
                    + "re-evaluating fresh");
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

        foreach (PartyPoolStackKey key in emptied)
        {
            _expectedFromReconcile.Remove(key);
        }
    }

    private bool EnqueueApply(
        PartyPoolOpKind kind, PartyPoolStackKey key, ushort count,
        ExpectedReconcileMovement? expected = null,
        uint intentPeerId = 0, uint intentSerial = 0)
    {
        lock (_pendingGate)
        {
            if (_pendingApplies.Count >= MaxPendingApplies)
            {
                log($"party pool v2: dropping apply for '{key.Resref}' — already holding "
                    + $"{MaxPendingApplies} queued, the drain has fallen behind");
                return false;
            }

            _pendingApplies.Enqueue(new CoopAgent.PendingPartyPoolApply(
                kind, key, count, intentPeerId, intentSerial));
            _dispatchOrder.Enqueue(expected);
            return true;
        }
    }

    /// <summary>
    /// The value this instance's OWN engine should be reconciled TOWARD for <paramref name="key"/>
    /// — deliberately NEVER <c>snapshotValue + OutstandingNet(key)</c>. That sum would double-count
    /// a delta this instance already applied to its own engine the moment it detected and
    /// published it (that is what <paramref name="localValue"/> already reflects); the host's
    /// snapshot catching up to include that same contribution must never be added a SECOND time
    /// on top (§5's own "Bug 2"). Once the engine's own current value already sits at or past the
    /// raw snapshot in the outstanding intents' own direction, the target is simply
    /// <paramref name="localValue"/> itself (nothing to apply — genuine local agreement, don't
    /// roll it back); otherwise the target is the RAW snapshot value alone — outstanding never
    /// contributes to what gets APPLIED, only to whether reconciling away a not-yet-echoed local
    /// deposit would be premature.
    /// </summary>
    private int ReconcileTarget(PartyPoolStackKey key, int localValue, int snapshotValue)
    {
        int net = OutstandingNet(key);
        if (net == 0)
        {
            return snapshotValue;
        }

        bool explainedByOwnEngine = net > 0 ? localValue >= snapshotValue : localValue <= snapshotValue;
        return explainedByOwnEngine ? localValue : snapshotValue;
    }

    /// <summary>
    /// §7 (2026-08-29): retires up to <paramref name="amount"/> worth of outstanding credit for
    /// <paramref name="key"/>, oldest entry first, restricted to entries moving in the SAME
    /// direction as <paramref name="amount"/> — the literal reading of
    /// docs/research/shared-inventory-v2.md §2.2: "retired — by however much, not necessarily all
    /// at once — the moment a NEW snapshot's own value... has moved in the same direction... an
    /// exact match is not required". <paramref name="amount"/> is the snapshot's own confirmed
    /// movement since the previous tick, never a comparison against this instance's own engine
    /// value — comparing against the engine value alone is unsound once more than one outstanding
    /// entry is in flight for the same key (this is why §9 also stopped the DETECT step from
    /// depending on this ledger at all — see the class remarks). An entry only partially covered
    /// by <paramref name="amount"/> has its own remaining credit shrunk, not removed outright, so
    /// nothing is over-retired.
    /// </summary>
    private void RetireOutstandingBy(PartyPoolStackKey key, int amount)
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

    private void HandleRejectedIntent(PartyPoolIntentPacketV2 intent, long nowMs)
    {
        RemoveOutstanding(intent.IntentSerial);

        _failedIntentStates.TryGetValue(intent.Key, out FailedIntentState? state);
        state ??= _failedIntentStates[intent.Key] = new FailedIntentState(0);
        state.Failures++;

        if (state.Failures <= MaxFailedIntentRetries)
        {
            uint retrySerial = session.PublishPartyPoolIntentV2(intent.Kind, intent.Key, intent.Count);
            if (retrySerial != 0)
            {
                int delta = intent.Kind == PartyPoolOpKind.Add ? intent.Count : -intent.Count;
                AddOutstanding(intent.Key, retrySerial, delta, nowMs);
                log($"party pool: host refused {intent.Kind.ToString().ToLowerInvariant()} of "
                    + $"{intent.Count}x '{intent.Key.Resref}' (engine apply failed); retry "
                    + $"{state.Failures}/{MaxFailedIntentRetries} (serial {retrySerial})");
                return;
            }
        }

        log($"party pool: host refused {intent.Kind.ToString().ToLowerInvariant()} of "
            + $"{intent.Count}x '{intent.Key.Resref}'; preserving the local item and stopping "
            + $"automatic retries after {MaxFailedIntentRetries} failures");
    }

    private bool IsFailedIntentProtected(PartyPoolStackKey key, int actual, int snapshot)
    {
        if (!_failedIntentStates.ContainsKey(key))
        {
            return false;
        }

        // Once the host snapshot independently catches up, the failure has been resolved and the
        // key can return to normal reconciliation. Until then, preserve the player's local
        // stack rather than silently rolling it back to the stale host value.
        if (actual == snapshot)
        {
            _failedIntentStates.Remove(key);
            return false;
        }

        return true;
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
            if (!_pendingApplies.TryDequeue(out next))
            {
                return false;
            }

            // The apply is now genuinely on its way to the engine through the single-slot
            // arbitrated cheat channel — only NOW does its expected-movement entry start its
            // "never observed landing" clock. See ExpectedReconcileMovement.Dispatched.
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

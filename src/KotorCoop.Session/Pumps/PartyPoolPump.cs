using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// The shared party item-pool channel (<c>CSWPartyTable+0x118</c>). Two jobs each tick:
///
/// 1. <b>Detect.</b> Compare this instance's own fresh engine scan against
///    <see cref="_engineBelief"/> (what this instance last believed the engine held), key by
///    key, to find a genuine local player action versus a queued apply landing — see the
///    per-key loop in <see cref="Pump"/> and root cause 4 below for exactly how the two are
///    told apart.
/// 2. <b>Reconcile.</b> Diff the fresh belief against <see cref="ICoopSession.PartyPoolTotals"/>
///    (<c>wireTotal</c> — the merged cross-instance belief) to find where THIS engine is behind
///    what the session now knows, and queue an apply that converges toward whatever the wire
///    currently says — recomputed fresh every tick, never a value captured once and trusted
///    forever.
///
/// See docs/research/party-pool-sync.md for the full design and the engine-call chain the
/// queued applies eventually run through.
///
/// ⚠ ROOT CAUSE 4, THE UNBOUNDED PING-PONG (2026-08-28, docs/research/party-pool-sync.md §21-
/// §22). Live evidence: three rapid same-key <c>pooladd</c>s inflated into an alternating
/// host&lt;-&gt;client sequence of 1, 2, 4, 12, 40, 180, 615, 1068, 906 units, then a Remove of
/// 1938, before settling back to zero — no crash, but a severe correctness catastrophe. The
/// prior design (root cause 3, §12) tracked one FROZEN SNAPSHOT per key
/// (<c>_pendingApplyTarget[key] = desired.GetValueOrDefault(key)</c>, captured once at the
/// moment an apply was queued) and treated any later scan that did not land EXACTLY on that
/// snapshot as needing correction. But the queued apply's own trip through the arbitrated,
/// single-slot staging-cheat channel is not instantaneous — it can take several ticks, during
/// which <c>wireTotal</c> for that SAME key can move again (another rapid deposit, a peer's own
/// op arriving) while the snapshot sits frozen at its original value. When the apply finally
/// lands, the comparison is against a number that no longer describes what is actually owed,
/// so the "excess" computed against it is not a real local event at all — it is stale-target
/// error, published as if it were, which moves the OTHER side's own wire, which repeats the
/// same mistake back, amplifying every round trip. Tuning the snapshot's own grace window
/// cannot fix this: any fixed value captured once and compared against a moving target is
/// unsound by construction, however wide the window.
///
/// <b>The fix: loop-proof by construction, not by tuning.</b> Two pieces of state per key,
/// both re-read fresh every tick instead of frozen at queue time:
///
/// - <see cref="_engineBelief"/> — what this instance last observed the engine hold (replaces
///   <c>_lastScan</c>/root cause 3's per-key belief, same role).
/// - <c>wireTotal</c> — <see cref="ICoopSession.PartyPoolTotals"/>, read fresh at the top of
///   every <see cref="Pump"/> call, never cached across ticks.
///
/// A fresh scan that differs from <see cref="_engineBelief"/> is measured against how far
/// <c>wireTotal</c> currently sits from that same belief (<see cref="ExcessBeyondWire"/>):
/// movement that does no more than close that gap — including landing exactly on it — is the
/// apply catching the engine up to what is already known and true; it is adopted into
/// <see cref="_engineBelief"/> silently, publishing nothing, because there is nothing new to
/// report. Movement that goes FURTHER than the gap (or the wrong direction entirely, including
/// the ordinary case where nothing was owed at all — the gap is zero, so ANY movement is
/// entirely unexplained) is a genuine local event layered on top, and only that EXCESS is
/// published — never the raw movement, which would double-count whatever the apply itself
/// already explains. Because the comparison is against the CURRENT wire, not a value captured
/// once, a SECOND wire move while the first apply is still in flight is folded into the very
/// next comparison automatically — there is no stale snapshot left to drift away from reality.
///
/// <see cref="_applyInFlight"/> (a plain key set, not a value to go stale) still exists — it
/// gates step 2's reconcile so a second apply is never queued for a key already chasing one,
/// exactly as <c>_pendingApplyTarget</c>'s keys did — but it carries no snapshot, so there is
/// nothing in it that ageing can invalidate.
///
/// <b>The hard backstop.</b> The reasoning above closes the mechanism §21 found, but this
/// channel ships silently ON by default eventually, so a defence that only works "if the
/// reasoning above is complete" is not enough on its own. <see cref="PublishDetected"/> counts
/// how many ops it has actually published for each key inside a rolling
/// <see cref="PublishRateWindowMs"/> window; a key that crosses <see cref="MaxPublishesPerWindow"/>
/// trips the breaker (<see cref="TripLoopBreaker"/>): publishing for that key freezes for
/// <see cref="FreezeDurationMs"/>, a diagnostic logs once, and
/// <see cref="ICoopSession.RequestPartyPoolResync"/> asks the host to re-broadcast every key as
/// an absolute <see cref="PartyPoolOpKind.Set"/> — the same resync a version/digest mismatch or
/// a reconnect already uses — discarding whatever this channel's own loop diverged into for
/// that key in favour of the host's own authoritative total. Reconcile also skips a frozen key
/// entirely (it will not chase a <c>wireTotal</c> that might itself be mid-correction) until the
/// freeze lifts. No amount of amplification inside the window this catches can exceed roughly
/// <see cref="MaxPublishesPerWindow"/> published ops for a single key — a world away from
/// §21's 615/1068/906/1938.
/// </summary>
internal sealed class PartyPoolPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    /// <summary>Bounded for the same reason <c>LootPump</c>'s queue is: a stuck drain or a
    /// malformed peer must not grow this without limit.</summary>
    private const int MaxPendingApplies = 256;

    /// <summary>
    /// ⚠ ROOT CAUSE 5 (2026-08-28, docs/research/party-pool-sync.md §24): <see cref="_pendingApplies"/>
    /// is written by <see cref="Pump"/> — called from <c>CoopAgent.RunAsync</c>'s own tick loop —
    /// and drained by <see cref="TryDequeuePendingApply"/>, called from
    /// <c>CoopCommandDispatcher.RunAsync</c>'s own INDEPENDENT, separately-scheduled loop
    /// (<c>AgentComposition.cs</c> starts both as separate tasks with no shared synchronization
    /// context — confirmed this pass). A plain <see cref="Queue{T}"/> is not thread-safe for ANY
    /// concurrent access, including this exact single-producer/single-consumer shape: a
    /// concurrent <c>Enqueue</c>/<c>TryDequeue</c> pair (most dangerously while an internal
    /// array resize is in flight) can silently corrupt the queue's own bookkeeping, losing an
    /// entry or handing back a stale/duplicate one — non-deterministically, depending on
    /// scheduling, and only when both sides are genuinely active at once. This is exactly the
    /// shape §21/§23's residual "smaller" defect had: asymmetric (sometimes the depositing side,
    /// sometimes the receiving side), non-deterministic (reproduced once, not the next identical
    /// run), and bounded (a handful of units, never the unbounded root-cause-4 runaway, since a
    /// torn queue read corrupts at most the ONE entry involved, not a compounding feedback loop).
    /// Guarded by <see cref="_pendingGate"/> below — the same discipline <see cref="PartyPoolStore"/>/
    /// <see cref="ReliablePartyPoolSender"/> already apply to every field either of THEIR own
    /// cross-thread callers touches.
    /// </summary>
    private readonly Lock _pendingGate = new();

    private readonly Queue<CoopAgent.PendingPartyPoolApply> _pendingApplies = new();

    /// <summary>This instance's own last-scanned engine totals — the step 1 baseline every
    /// key's movement is measured against each tick. Updated to the FULL fresh scan at the end
    /// of step 1, every tick, regardless of what step 1 did with any individual key — see root
    /// cause 4's class remarks above.</summary>
    private Dictionary<PartyPoolStackKey, int> _engineBelief = [];

    /// <summary>Keys with a step-2 apply queued (or already dequeued and in flight through the
    /// arbitrated cheat channel) whose landing this instance is still waiting to observe.
    /// Excludes a key from step 2's reconcile (never queue a second apply for a key already
    /// chasing one) — unlike root cause 3's <c>_pendingApplyTarget</c>, this carries no VALUE to
    /// go stale; every comparison against it re-reads <c>wireTotal</c> fresh, see the class
    /// remarks.</summary>
    private readonly HashSet<PartyPoolStackKey> _applyInFlight = [];

    // ---- the hard loop breaker (root cause 4's own backstop, §22) -------------------------

    private const int MaxPublishesPerWindow = 8;
    private const long PublishRateWindowMs = 10_000;
    private const long FreezeDurationMs = 10_000;

    /// <summary>Timestamps (<see cref="SessionClock.NowMs"/>) of this key's own recent
    /// publishes, oldest first — pruned to <see cref="PublishRateWindowMs"/> on every publish.
    /// Bounded the same way <see cref="_warnedSuspiciousStackSize"/> is: at most
    /// <c>ProtocolConstants.MaxPartyPoolStacks</c> distinct keys ever pass through this pump in
    /// a session.</summary>
    private readonly Dictionary<PartyPoolStackKey, Queue<long>> _publishHistory = [];

    /// <summary>A key present here with a still-future timestamp is frozen: step 1 still tracks
    /// its belief normally but never calls <see cref="PublishDetected"/> for it, and step 2
    /// never queues a new apply for it either, until the timestamp passes. Self-pruning — see
    /// <see cref="IsFrozen"/> — so an entry never outlives its own freeze by more than one
    /// query.</summary>
    private readonly Dictionary<PartyPoolStackKey, long> _frozenUntil = [];

    /// <summary>
    /// A stack total above this is presumed to be an ambient-script bug rather than a
    /// deliberate deposit — docs/research/party-pool-sync.md §19's own live pass observed
    /// exactly this from the module's own duros-registration script (stack 65535, 6783, and a
    /// remove of 38937 against the SAME repository this channel scans), all classic KOTOR
    /// retail script sentinel/overflow values, not a coop-mod artifact. Not a real KOTOR
    /// gameplay stack: nothing in the base game's own UI or scripting stacks past a few dozen
    /// of anything. This never changes what gets published or applied — the count is already
    /// safe to carry (<c>KotorCoop.Memory.ItemIdentityLayout.Decode</c> clamps any raw read to
    /// <c>[1, ushort.MaxValue]</c> before it ever reaches this dictionary, so a genuinely
    /// unbounded/garbage value cannot poison a peer) — it only decides whether this pump logs a
    /// one-time diagnostic so a session watching the log can tell "the ambient module is doing
    /// this again" from "party_pool_sync grew a real bug", rather than needing a live-attach
    /// probe to find out after the fact.
    /// </summary>
    private const int SuspiciousStackSizeThreshold = 99;

    /// <summary>Keys already warned about via <see cref="SuspiciousStackSizeThreshold"/> —
    /// logged once per key, not once per tick, matching every other "say it once, not every
    /// frame" diagnostic in this codebase. Never pruned: bounded by
    /// <c>ProtocolConstants.MaxPartyPoolStacks</c> (256) the same way the store/loot channels'
    /// own key spaces are, so this cannot grow without limit over a session.</summary>
    private readonly HashSet<PartyPoolStackKey> _warnedSuspiciousStackSize = [];

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
            _engineBelief = new Dictionary<PartyPoolStackKey, int>(engine);
            _baselined = true;
            return;
        }

        long now = SessionClock.NowMs;

        // Read fresh for step 1 — see the class remarks' root cause 4 section for why this must
        // never be a value captured once and trusted across MULTIPLE TICKS the way the old
        // per-key target was. WITHIN this one tick, step 2 below re-reads it AGAIN after step 1
        // has run, rather than reusing this copy — step 1's own publishes update the store
        // instantly (session.PublishPartyPoolOp), and step 2 must see that, exactly like the
        // pre-existing "step 2 always reads the store AFTER step 1's publishes" invariant this
        // class has documented since root cause 1.
        IReadOnlyDictionary<PartyPoolStackKey, int> wireTotal = session.PartyPoolTotals;

        // ---- step 1: detect movement since the last belief, key by key --------------------
        //
        // Every key either _engineBelief or the fresh scan knows about, once. A key present in
        // only one of the two just went to/from zero (matching PartyPoolDiffRule's own two
        // passes).
        HashSet<PartyPoolStackKey> keys = new(_engineBelief.Keys);
        keys.UnionWith(engine.Keys);

        foreach (PartyPoolStackKey key in keys)
        {
            int belief = _engineBelief.GetValueOrDefault(key);
            int actual = engine.GetValueOrDefault(key);

            if (actual > SuspiciousStackSizeThreshold && _warnedSuspiciousStackSize.Add(key))
            {
                log($"party pool: suspiciously large stack — {actual}x '{key.Resref}' "
                    + $"(charges {key.Charges}/{key.MaxCharges}, upgrades 0x{key.Upgrades:X}). "
                    + "Likely an ambient script bug (docs/research/party-pool-sync.md §19/§20), "
                    + "not a coop-mod artifact — carried through unchanged, logged once.");
            }

            if (actual == belief)
            {
                // Nothing moved since the last belief. If an apply is in flight for this key,
                // it simply has not landed yet — keep waiting.
                continue;
            }

            // How much of this movement is NOT explained by the engine catching up to what the
            // wire already knows — see ExcessBeyondWire's own doc for the exact rule. Zero
            // means "this is (all or part of) an apply landing," published nothing; nonzero
            // means a genuine local event, published exactly that much.
            int excess = ExcessBeyondWire(belief, actual, wireTotal.GetValueOrDefault(key));

            if (excess != 0)
            {
                PublishDetected(key, excess, now);
            }

            // ROOT CAUSE 6 (2026-08-28, docs/research/party-pool-sync.md §24): this used to
            // require actual == wireTotal.GetValueOrDefault(key) exactly — sound only if the
            // wire never moves again while ONE apply is in flight. It routinely does (a second
            // deposit of the same key arriving mid-flight, live-reproduced by
            // RapidHostOriginatedPoolAddsThroughTheRealCheatChannelConvergeToExactlyThreeOnBothSides):
            // the queued apply was sized for an OLDER, smaller wire target, so `actual` can only
            // ever reach THAT number, never the newer one — the exact-match check then never
            // fires again, EVER, for this key: _applyInFlight stays set, step 2 keeps excluding
            // the key from reconcile, and the engine sticks at whatever the one landed apply
            // produced no matter how many further deposits arrive on the wire. A genuine,
            // permanent stall, not a transient one.
            //
            // The fix needs no stored target at all (this class's whole point since root cause
            // 4): AddItemToPartyPool/RemovePartyPoolStack apply their FULL requested delta in one
            // synchronous engine-side call (§2.4/§1.3) — there is no OBSERVABLE partial-landing
            // state a poll-based scan could ever catch mid-flight, so any movement seen for a key
            // that is currently in flight IS, by construction, that one queued apply landing in
            // full. `actual != belief` was already established above (the `continue` guard), so
            // reaching this line at all already means "a distinct entry currently in flight for
            // this key just moved" — clear it unconditionally and let step 2, re-reading the
            // store fresh below, notice and queue for whatever gap (if any) remains right now.
            if (_applyInFlight.Contains(key))
            {
                _applyInFlight.Remove(key);
            }
        }

        _engineBelief = new Dictionary<PartyPoolStackKey, int>(engine);

        // ---- step 2: reconcile toward the (now-current) wire truth -------------------------
        //
        // Re-read AFTER step 1 — never reuse the copy above. Step 1's own PublishDetected calls
        // (session.PublishPartyPoolOp) update this instance's own store the instant they run,
        // and this reconcile pass must see that update in the SAME tick; using the stale
        // pre-step-1 snapshot here would make reconcile believe the engine (already at the new,
        // just-published total) is AHEAD of a wire that has not "caught up" yet, and queue a
        // bogus Remove to drag the engine back down to match it — a real regression this fix
        // introduced and caught via the full PartyPoolLoopbackTests run before this comment was
        // written.
        //
        // Excludes a key with an apply already in flight (never queue a second one chasing the
        // same key) and a key currently frozen by the loop breaker (never chase a wire total
        // that might itself still be mid-correction from the resync this same breaker just
        // asked for).
        IReadOnlyDictionary<PartyPoolStackKey, int> wireForReconcile = session.PartyPoolTotals;

        HashSet<PartyPoolStackKey> excluded = new(_applyInFlight);
        foreach ((PartyPoolStackKey key, long until) in _frozenUntil)
        {
            if (now < until)
            {
                excluded.Add(key);
            }
        }

        Dictionary<PartyPoolStackKey, int> desiredForReconcile = Without(wireForReconcile, excluded);
        Dictionary<PartyPoolStackKey, int> engineForReconcile = Without(_engineBelief, excluded);

        // Diff(expected: engine, actual: desired) — an Add delta means desired > engine (this
        // instance owes the engine that many more units), a Remove delta means desired < engine.
        foreach (PartyPoolDiffRule.Delta delta in PartyPoolDiffRule.Diff(engineForReconcile, desiredForReconcile))
        {
            // See _pendingGate's own doc comment (root cause 5, §24): the count check and the
            // enqueue must be one atomic operation under the SAME lock TryDequeuePendingApply
            // takes — this queue is drained from a genuinely separate, independently-scheduled
            // thread (CoopCommandDispatcher.RunAsync), not merely a different call frame.
            lock (_pendingGate)
            {
                if (_pendingApplies.Count >= MaxPendingApplies)
                {
                    log($"party pool: dropping apply for '{delta.Key.Resref}' — already holding "
                        + $"{MaxPendingApplies} queued, the drain has fallen behind");
                    continue;
                }

                _pendingApplies.Enqueue(
                    new CoopAgent.PendingPartyPoolApply(delta.Kind, delta.Key, (ushort)delta.Count));
            }

            _applyInFlight.Add(delta.Key);

            // This instance now owes its own engine an apply — the very next scan needs to be
            // fresh so the apply's landing (and anything else that moves in the meantime) is
            // seen as soon as it is true, not delayed behind a cache window that started
            // ticking before we had any reason to look again. Same reasoning as
            // PublishDetected's own call below.
            bridge.InvalidatePartyPoolScanCache();
        }
    }

    /// <summary>
    /// How much of the movement from <paramref name="belief"/> to <paramref name="actual"/> is
    /// NOT explained by the engine catching up toward <paramref name="wire"/> — the one
    /// computation root cause 4's whole fix rests on (see the class remarks above).
    ///
    /// Nothing was owed (<paramref name="wire"/> already equalled <paramref name="belief"/>):
    /// every unit of movement is new by construction, since there was nothing to converge
    /// toward — returns the full delta. Something WAS owed and the movement goes the right way
    /// but does not pass it (including landing exactly on it): fully explained by convergence —
    /// returns zero. Movement the wrong way entirely, or past the wire once it is reached: only
    /// the part beyond what convergence explains is new — returns that signed remainder.
    /// </summary>
    private static int ExcessBeyondWire(int belief, int actual, int wire)
    {
        int delta = actual - belief;
        int gap = wire - belief;

        if (gap == 0 || Math.Sign(delta) != Math.Sign(gap))
        {
            return delta;
        }

        return Math.Abs(delta) > Math.Abs(gap) ? delta - gap : 0;
    }

    /// <summary>Publishes a signed, already-resolved local delta for one key — a positive
    /// <paramref name="delta"/> is an Add, negative is a Remove — unless the loop breaker has
    /// this key frozen (see the class remarks). Tracks this key's own publish rate and trips
    /// the breaker itself the moment a publish would cross <see cref="MaxPublishesPerWindow"/>
    /// inside <see cref="PublishRateWindowMs"/> — that OP is suppressed too, not just the ones
    /// after it, so the count this method ever lets through a single key in any 10s window is
    /// bounded at exactly <see cref="MaxPublishesPerWindow"/>.</summary>
    private void PublishDetected(PartyPoolStackKey key, int delta, long now)
    {
        if (IsFrozen(key, now))
        {
            return;
        }

        Queue<long> history = _publishHistory.TryGetValue(key, out Queue<long>? existing)
            ? existing
            : _publishHistory[key] = new Queue<long>();

        history.Enqueue(now);
        while (history.Count > 0 && now - history.Peek() > PublishRateWindowMs)
        {
            history.Dequeue();
        }

        if (history.Count > MaxPublishesPerWindow)
        {
            TripLoopBreaker(key, now, history.Count);
            return;
        }

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

    /// <summary>True (and self-pruning) while <paramref name="key"/> is still inside a freeze
    /// this pump imposed on itself via <see cref="TripLoopBreaker"/>.</summary>
    private bool IsFrozen(PartyPoolStackKey key, long now)
    {
        if (!_frozenUntil.TryGetValue(key, out long until))
        {
            return false;
        }

        if (now < until)
        {
            return true;
        }

        _frozenUntil.Remove(key);
        return false;
    }

    /// <summary>
    /// The hard backstop, independent of whether root cause 4's own reasoning above turns out
    /// to have a gap: this key has published more than <see cref="MaxPublishesPerWindow"/> ops
    /// of its own within <see cref="PublishRateWindowMs"/>, which no legitimate rapid-deposit
    /// recipe this channel is meant to support should ever do. Freezes publishing for this key
    /// for <see cref="FreezeDurationMs"/> (reconcile also skips it for the same span — see
    /// <see cref="Pump"/>'s step 2), logs once, and asks the session for a full resync so the
    /// host's own absolute totals overwrite whatever this loop diverged into, on every peer,
    /// once the freeze lifts and reconcile is willing to chase the wire again.
    /// </summary>
    private void TripLoopBreaker(PartyPoolStackKey key, long now, int opsInWindow)
    {
        _frozenUntil[key] = now + FreezeDurationMs;

        log($"party pool: '{key.Resref}' published {opsInWindow} ops within "
            + $"{PublishRateWindowMs / 1000}s — loop breaker tripped, freezing this key and "
            + "requesting a resync from the host's absolute totals "
            + "(docs/research/party-pool-sync.md §22)");

        session.RequestPartyPoolResync();
    }

    /// <summary>Called by whatever already arbitrates the single-slot staging-cheat channel (the
    /// command dispatcher's pending-cheat-serial check), at most once per ITS tick — mirrors
    /// <c>LootPump.TryDequeuePendingLoot</c> exactly. Locked under <see cref="_pendingGate"/> —
    /// see that field's own doc comment (root cause 5, §24): this runs on a thread genuinely
    /// independent of <see cref="Pump"/>'s own enqueue.</summary>
    public bool TryDequeuePendingApply(out CoopAgent.PendingPartyPoolApply next)
    {
        lock (_pendingGate)
        {
            return _pendingApplies.TryDequeue(out next);
        }
    }

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

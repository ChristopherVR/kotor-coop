using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the merchant/store stock's v2 channel (<c>store_sync=2</c>): the same
/// publish-don't-detect design as <c>CoopHost.PartyPoolV2.cs</c>'s party-pool channel,
/// widened by the store's own object id. See docs/research/shared-inventory-v2.md §2.4/§2.6 and
/// §7's 2026-08-29 defect write-up — `Pumps.StorePumpV2` owns the "which stores does this
/// instance have a belief for at all" merge across scans (a store's contents can only be READ
/// while its GUI is open, unchanged from v1); this file only republishes whatever belief it is
/// handed, and applies an accepted client intent to its own engine at most once
/// (<see cref="HandleStoreIntentV2"/>'s dedup + <see cref="DrainStoreIntentsV2"/>) — no other
/// apply-to-engine path exists here.
/// </summary>
public sealed partial class CoopHost
{
    private Dictionary<StoreStackKey, int> _storeBeliefV2 = [];

    private uint _storeGenerationV2;

    private const int StoreV2BroadcastIntervalMs = 1000;

    private long _lastStoreV2BroadcastMs;

    private bool _forceStoreV2Broadcast;

    private readonly HashSet<(uint Peer, uint Serial)> _storeIntentDedupV2 = [];

    private readonly Queue<(uint Peer, uint Serial)> _storeIntentDedupHistoryV2 = new();

    private readonly Queue<StoreIntentPacketV2> _storeIntentDrainV2 = new();

    private readonly Lock _storeIntentDrainV2Gate = new();

    /// <summary>Every store object id this instance's OWN bridge has EVER successfully scanned
    /// this session, regardless of whether that scan came back empty — the host's own,
    /// unambiguous answer to "do I know anything about this store at all", added to in
    /// <see cref="PublishStoreSnapshotV2"/> unconditionally (§7, 2026-08-29: item 3's "a store
    /// never opened on the host is absent = unknown" requirement). Never consulted by
    /// <c>Pumps.StorePumpV2</c>'s own host branch (the host never detects anything to gate); kept
    /// for interface symmetry with <see cref="IsStoreKnownV2"/> and for tests.</summary>
    private readonly HashSet<uint> _storeKnownIdsV2 = [];

    /// <summary>First moment (<see cref="SessionClock.NowMs"/>) each currently-believed key was
    /// observed ABSENT from a fresh scan of its own store, cleared the instant it reappears —
    /// the debounce state <see cref="PublishStoreSnapshotV2"/>'s own remarks describe.</summary>
    private readonly Dictionary<StoreStackKey, long> _storeMissingSinceV2 = [];

    /// <summary>See <see cref="PublishStoreSnapshotV2"/>'s own remarks (§7, 2026-08-29): a key
    /// must read absent from TWO scans of its own store, at least this many real milliseconds
    /// apart, before it is actually removed from belief. Comfortably longer than the bridge's own
    /// ~1s scan-cache throttle, so this always spans at least one genuinely fresh re-read, not
    /// two draws from the same cached (possibly mid-rebuild) result.</summary>
    private const long StoreStaleRemovalConfirmMs = 2_000;

    /// <summary>
    /// Per key, the value <see cref="HandleStoreIntentV2"/>'s write-through predicted and the
    /// moment that prediction stops being defended — see <see cref="_storeIntentEffectV2"/>.
    /// </summary>
    private readonly record struct StoreIntentEffectV2(int Expected, long UntilMs);

    /// <summary>
    /// ⚠ 2026-08-29, docs/research/shared-inventory-v2.md §15.5 — THE STORE WOBBLE'S ROOT CAUSE.
    ///
    /// <see cref="HandleStoreIntentV2"/>'s write-through moves this host's belief for a key the
    /// instant an intent is accepted, so the very next broadcast already reflects it even with
    /// the shop's GUI closed (§7's own reason for the write-through). But the engine apply that
    /// write-through PREDICTS is queued, not done: it goes through the single-slot arbitrated
    /// cheat channel and typically lands a second or two later. If the host's own store GUI
    /// happens to be open in the meantime, its next scan reads the PRE-apply stock and
    /// <see cref="PublishStoreSnapshotV2"/> overwrites the prediction with it — so the broadcast
    /// says "1", then "0" (write-through), then "1" again (stale scan), then finally "0". Live,
    /// that middle regression is exactly what made the client retire its outstanding credit, read
    /// the restored "1" as the host's truth, and RE-INSERT the item it had just bought, removing
    /// it again 3s later (§15.5's two spurious engine writes).
    ///
    /// The published snapshot must therefore be MONOTONE with respect to accepted intents: once
    /// the host has told everyone a key moved because it accepted a client's intent, it must not
    /// say otherwise again until it has genuinely re-observed the key at the predicted value (the
    /// apply landed) or given up waiting. Until then a scan disagreeing with the prediction is
    /// not evidence about this key — it is a read of an engine that has not run the apply yet.
    /// Bounded by <see cref="StoreIntentEffectHoldMs"/> so a genuinely dropped apply still heals:
    /// the scan wins once the hold lapses, and the client reconciles to it as usual.
    /// </summary>
    private readonly Dictionary<StoreStackKey, StoreIntentEffectV2> _storeIntentEffectV2 = [];

    /// <summary>How long <see cref="_storeIntentEffectV2"/> defends a write-through prediction
    /// against a scan that disagrees with it. Matches the client's own
    /// <c>Pumps.StorePumpV2</c> intent/expected-movement timeout — comfortably past a realistic
    /// backlog draining one at a time through the single-slot arbitrated cheat channel, and short
    /// enough that a genuinely dropped apply self-heals inside one <c>AckGraceMs</c>.</summary>
    private const long StoreIntentEffectHoldMs = 10_000;

    /// <summary>
    /// Merges one store's fresh scan into the host's running cross-store belief, wholesale for
    /// <paramref name="storeObjectId"/>'s own keys only (a store's contents can only be scanned
    /// while its GUI is open — docs/research/shared-inventory-v2.md §2.6 — so every other
    /// already-known store's belief is left untouched). Mirrors v1's
    /// <c>StorePump.ReplaceStore</c>'s intent: a key present in the belief before but genuinely
    /// absent from this store's stock now should eventually leave belief too — including a store
    /// that scanned genuinely EMPTY, which is exactly why <paramref name="storeObjectId"/> is
    /// carried explicitly rather than inferred from <paramref name="totals"/>'s (possibly empty)
    /// keys.
    ///
    /// <b>§7 (2026-08-29), the "virgin wipe" fix</b>: a live rig round found this pump wipe then
    /// restore nearly an entire pristine store's stock, unprompted, roughly five minutes after
    /// the store was first opened and scanned cleanly — traced to this method treating a SINGLE
    /// scan's absence as proof a key genuinely left the store. The engine's own item repository
    /// can transiently read back a reduced/zero count mid-rebuild (a stack merge/split, a fresh
    /// larger-scope re-read forced by an unrelated cheat) while the underlying READS still
    /// "succeed" from the bridge's own point of view — nothing here can tell that apart from a
    /// real depletion in a single sample. A key now missing from <paramref name="totals"/> is no
    /// longer removed on the spot: its FIRST missing moment is recorded
    /// (<see cref="_storeMissingSinceV2"/>), and it is only actually dropped once it has read
    /// absent continuously for <see cref="StoreStaleRemovalConfirmMs"/> — i.e. confirmed by a
    /// SECOND, independent fresh scan of this same store, not just a repeat read of a cached one.
    /// A genuine depletion (the item was actually sold/removed) stays absent across that whole
    /// window and is still removed, just up to ~2s later than before; a one-off transient glitch
    /// self-heals on its very next real scan and never touches belief at all.
    /// </summary>
    public void PublishStoreSnapshotV2(uint storeObjectId, IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        long now = SessionClock.NowMs;

        lock (_gate)
        {
            _storeKnownIdsV2.Add(storeObjectId);

            PruneStoreIntentEffectsV2(now);

            List<StoreStackKey>? stale = null;
            foreach (StoreStackKey key in _storeBeliefV2.Keys)
            {
                if (key.StoreObjectId != storeObjectId || totals.ContainsKey(key))
                {
                    continue;
                }

                // A key whose accepted-intent prediction is still being defended is not
                // "missing" — the apply that will put it there has not run yet. See
                // _storeIntentEffectV2.
                if (_storeIntentEffectV2.ContainsKey(key))
                {
                    continue;
                }

                if (_storeMissingSinceV2.TryGetValue(key, out long missingSinceMs))
                {
                    if (now - missingSinceMs >= StoreStaleRemovalConfirmMs)
                    {
                        (stale ??= []).Add(key);
                    }
                }
                else
                {
                    _storeMissingSinceV2[key] = now;
                }
            }

            if (stale is not null)
            {
                foreach (StoreStackKey key in stale)
                {
                    _storeBeliefV2.Remove(key);
                    _storeMissingSinceV2.Remove(key);
                }
            }

            foreach ((StoreStackKey key, int count) in totals)
            {
                if (_storeIntentEffectV2.TryGetValue(key, out StoreIntentEffectV2 effect))
                {
                    if (count != effect.Expected)
                    {
                        // The apply has not landed yet — this read is of an engine that has not
                        // run it. Leave belief on the prediction and keep waiting.
                        _storeMissingSinceV2.Remove(key);
                        continue;
                    }

                    // Observed at the predicted value: the apply landed, the prediction is now an
                    // observation, and ordinary scan-wins behaviour resumes for this key.
                    _storeIntentEffectV2.Remove(key);
                }

                _storeBeliefV2[key] = count;
                _storeMissingSinceV2.Remove(key);
            }
        }
    }

    /// <summary>Drops every accepted-intent prediction whose hold has lapsed — after that the
    /// host's own scan is the truth again for that key, exactly as it was before the intent. See
    /// <see cref="_storeIntentEffectV2"/>. Caller holds <c>_gate</c>.</summary>
    private void PruneStoreIntentEffectsV2(long now)
    {
        if (_storeIntentEffectV2.Count == 0)
        {
            return;
        }

        List<StoreStackKey>? lapsed = null;
        foreach ((StoreStackKey key, StoreIntentEffectV2 effect) in _storeIntentEffectV2)
        {
            if (now >= effect.UntilMs)
            {
                (lapsed ??= []).Add(key);
            }
        }

        if (lapsed is null)
        {
            return;
        }

        foreach (StoreStackKey key in lapsed)
        {
            _storeIntentEffectV2.Remove(key);
        }
    }

    public bool TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        totals = EmptyStoreTotals.Instance;
        return false;
    }

    /// <summary>Trivially true for anything the host's own bridge has ever scanned — the host is
    /// the truth, so it never needs to ask itself this. Not consulted by
    /// <c>Pumps.StorePumpV2</c>'s host branch; implemented for interface symmetry with
    /// <see cref="CoopClient.IsStoreKnownV2"/> and for tests. See <see cref="_storeKnownIdsV2"/>.</summary>
    public bool IsStoreKnownV2(uint storeObjectId)
    {
        lock (_gate)
        {
            return _storeKnownIdsV2.Contains(storeObjectId);
        }
    }

    public uint PublishStoreIntentV2(StoreOpKind kind, StoreStackKey key, ushort count) => 0;

    public IReadOnlyList<StoreIntentPacketV2> DrainStoreIntentsV2()
    {
        lock (_storeIntentDrainV2Gate)
        {
            if (_storeIntentDrainV2.Count == 0)
            {
                return [];
            }

            List<StoreIntentPacketV2> drained = [.. _storeIntentDrainV2];
            _storeIntentDrainV2.Clear();
            return drained;
        }
    }

    public IReadOnlyList<uint> DrainAckedStoreIntentsV2() => [];

    /// <summary>
    /// See <see cref="CoopHost.HandlePartyPoolIntentV2"/>'s identical remarks —
    /// <c>ApplyClientIntent(serial)</c>, dedup-then-queue-then-ack, widened by the store's own
    /// object id (already carried inside <see cref="StoreStackKey"/>, so no separate per-store
    /// dedup ring is needed).
    ///
    /// <b>§7 (2026-08-29)</b>: unlike the party pool (scanned unconditionally every host tick,
    /// §2.4), a store can only be SCANNED while its GUI is open (§2.6) — applying never needed
    /// it, only reading does. If this method did nothing but queue the real engine apply, a store
    /// closed on the host at the moment an intent lands would never scan again until it reopens,
    /// so <see cref="_storeBeliefV2"/> would carry a stale value for this key INDEFINITELY — not
    /// just delaying <c>Pumps.StorePumpV2</c>'s client-side evidence-based outstanding retirement
    /// (<c>ReconcileTarget</c>'s own "snapshot genuinely moved" check), but making it permanently
    /// unreachable, leaving only <c>AckGraceMs</c>'s timer-based safety net to ever retire it — a
    /// live-rig-shaped test with the store GUI closed proved exactly this: a client's buy-back
    /// applied once correctly, then applied a SECOND time once the timer (formerly 2,500&#160;ms)
    /// elapsed and the client re-detected its own already-applied change as fresh. This
    /// write-through keeps <see cref="_storeBeliefV2"/> (and hence the broadcast, and hence the
    /// evidence-based path) current for THIS key regardless of GUI state, exactly like it did
    /// before the write-through was briefly removed earlier in this same pass — the removal was
    /// correct for the party pool (unconditionally scanned, so a real scan always catches up on
    /// its own) but wrong for the store specifically, for the GUI-gating reason above. Still never
    /// applies anything the host's OWN scan would not eventually also read back — the next real
    /// scan of this store simply overwrites this guess with the truth, exactly as
    /// <see cref="PublishStoreSnapshotV2"/>'s own remarks describe for an ordinary re-scan.
    /// </summary>
    private void HandleStoreIntentV2(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreIntentV2(span) is not { } intent)
        {
            _channel.NoteMalformed();
            return;
        }

        (uint Peer, uint Serial) dedupKey = (intent.PeerId, intent.IntentSerial);
        bool isNew;
        lock (_gate)
        {
            isNew = _storeIntentDedupV2.Add(dedupKey);
            if (isNew)
            {
                _storeIntentDedupHistoryV2.Enqueue(dedupKey);
                while (_storeIntentDedupHistoryV2.Count > MaxRememberedIntentsV2)
                {
                    _storeIntentDedupV2.Remove(_storeIntentDedupHistoryV2.Dequeue());
                }

                int before = _storeBeliefV2.GetValueOrDefault(intent.Key);
                int after = intent.Kind == StoreOpKind.Add
                    ? before + intent.Count
                    : Math.Max(0, before - intent.Count);

                if (after > 0)
                {
                    _storeBeliefV2[intent.Key] = after;
                }
                else
                {
                    _storeBeliefV2.Remove(intent.Key);
                }

                // Not a fresh "missing" observation for the debounce (§7's own virgin-wipe fix)
                // — this key's belief just changed for a real, known reason, not because a scan
                // of its store came back without it.
                _storeMissingSinceV2.Remove(intent.Key);

                // ...and defend that prediction against this host's own pre-apply scan until the
                // engine actually catches up — §15.5's wobble. See _storeIntentEffectV2.
                _storeIntentEffectV2[intent.Key] =
                    new StoreIntentEffectV2(after, now + StoreIntentEffectHoldMs);
            }
        }

        if (isNew)
        {
            lock (_storeIntentDrainV2Gate)
            {
                _storeIntentDrainV2.Enqueue(intent);
            }
        }

        int length = PacketCodec.Write(
            _replyBuffer, new StoreIntentAckPacketV2(intent.PeerId, intent.IntentSerial, Accepted: true));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void BroadcastStoreV2(long now, List<PeerConnection> recipients)
    {
        if (recipients.Count == 0)
        {
            return;
        }

        if (!_forceStoreV2Broadcast && now - _lastStoreV2BroadcastMs < StoreV2BroadcastIntervalMs)
        {
            return;
        }

        _forceStoreV2Broadcast = false;
        _lastStoreV2BroadcastMs = now;

        // §7 (2026-08-29): unlike the party pool's own belief (replaced wholesale every
        // publish), _storeBeliefV2 is mutated IN PLACE by PublishStoreSnapshotV2's per-store
        // merge — a bare reference copy here would hand Paginate the SAME live Dictionary a
        // concurrent scan (running on a different store, on the same Tick task, or interleaved
        // via re-entrancy) could still be mutating while this enumerates it outside the lock.
        // Clone under the lock so Paginate only ever walks a private, unshared copy — see
        // CoopHost.PartyPoolV2.cs's BroadcastPartyPoolV2 for the identical discipline.
        Dictionary<StoreStackKey, int> belief;
        uint generation;
        lock (_gate)
        {
            // Incremented under the same lock HandleStoreIntentV2 reads it under, so the
            // generation an ack promises ("the next one") can never name a cycle that has
            // already gone out — §15.5.
            generation = ++_storeGenerationV2;
            belief = new Dictionary<StoreStackKey, int>(_storeBeliefV2);
        }

        foreach (StoreSnapshotPacketV2 page in
            StoreSnapshotPagingV2.Paginate(now, generation, belief))
        {
            int length = PacketCodec.Write(_sendBuffer, page);
            if (length == 0)
            {
                continue;
            }

            foreach (PeerConnection peer in recipients)
            {
                _channel.Send(peer.EndPoint, _sendBuffer.AsSpan(0, length));
            }
        }
    }
}

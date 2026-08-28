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

            List<StoreStackKey>? stale = null;
            foreach (StoreStackKey key in _storeBeliefV2.Keys)
            {
                if (key.StoreObjectId != storeObjectId || totals.ContainsKey(key))
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
                _storeBeliefV2[key] = count;
                _storeMissingSinceV2.Remove(key);
            }
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
        _storeGenerationV2++;

        // §7 (2026-08-29): unlike the party pool's own belief (replaced wholesale every
        // publish), _storeBeliefV2 is mutated IN PLACE by PublishStoreSnapshotV2's per-store
        // merge — a bare reference copy here would hand Paginate the SAME live Dictionary a
        // concurrent scan (running on a different store, on the same Tick task, or interleaved
        // via re-entrancy) could still be mutating while this enumerates it outside the lock.
        // Clone under the lock so Paginate only ever walks a private, unshared copy — see
        // CoopHost.PartyPoolV2.cs's BroadcastPartyPoolV2 for the identical discipline.
        Dictionary<StoreStackKey, int> belief;
        lock (_gate)
        {
            belief = new Dictionary<StoreStackKey, int>(_storeBeliefV2);
        }

        foreach (StoreSnapshotPacketV2 page in
            StoreSnapshotPagingV2.Paginate(now, _storeGenerationV2, belief))
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

using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the merchant/store stock's v2 channel (<c>store_sync=2</c>): the same
/// publish-don't-detect design as <c>CoopHost.PartyPoolV2.cs</c>'s party-pool channel,
/// widened by the store's own object id. See docs/research/shared-inventory-v2.md §2.4/§2.6 —
/// `Pumps.StorePumpV2` owns the "which stores does this instance have a belief for at all"
/// merge across scans (a store's contents can only be READ while its GUI is open, unchanged
/// from v1); this file only republishes whatever belief it is handed.
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

    /// <summary>
    /// Merges one store's fresh scan into the host's running cross-store belief, wholesale for
    /// <paramref name="storeObjectId"/>'s own keys only (a store's contents can only be scanned
    /// while its GUI is open — docs/research/shared-inventory-v2.md §2.6 — so every other
    /// already-known store's belief is left untouched). Mirrors v1's
    /// <c>StorePump.ReplaceStore</c> exactly: a key present in the belief before but absent from
    /// this fresh scan genuinely left that store's stock — including a store that scanned
    /// genuinely EMPTY, which is exactly why <paramref name="storeObjectId"/> is carried
    /// explicitly rather than inferred from <paramref name="totals"/>'s (possibly empty) keys.
    /// </summary>
    public void PublishStoreSnapshotV2(uint storeObjectId, IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        lock (_gate)
        {
            List<StoreStackKey>? stale = null;
            foreach (StoreStackKey key in _storeBeliefV2.Keys)
            {
                if (key.StoreObjectId == storeObjectId && !totals.ContainsKey(key))
                {
                    (stale ??= []).Add(key);
                }
            }

            if (stale is not null)
            {
                foreach (StoreStackKey key in stale)
                {
                    _storeBeliefV2.Remove(key);
                }
            }

            foreach ((StoreStackKey key, int count) in totals)
            {
                _storeBeliefV2[key] = count;
            }
        }
    }

    public bool TryGetStoreSnapshotV2(out IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        totals = EmptyStoreTotals.Instance;
        return false;
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

    /// <summary>See <see cref="CoopHost.HandlePartyPoolIntentV2"/>'s identical remarks — same
    /// dedup-then-write-through-then-queue-then-ack shape, widened by the store's own object id
    /// (already carried inside <see cref="StoreStackKey"/>, so no separate per-store dedup ring
    /// is needed).</summary>
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

        Dictionary<StoreStackKey, int> belief;
        lock (_gate)
        {
            belief = _storeBeliefV2;
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

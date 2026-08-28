using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — the merchant/store stock channel: reliable, (store object id + content) keyed
/// add/remove ops. See <see cref="StoreStore"/>/<see cref="ReliableStoreSender"/> for the retry
/// and dedup machinery this file drives, and docs/research/store-sync.md for the design.
///
/// Same split as <see cref="CoopHost.PartyPool"/>: applying a change to THIS instance's own
/// engine is <c>Pumps.StorePump</c>'s job, not this file's — this file only keeps the STORE
/// converged across the wire.
/// </summary>
public sealed partial class CoopHost
{
    private readonly ReliableStoreSender _storeSender = new();
    private readonly StoreStore _storeStore = new();

    /// <summary>Every REMOTE store op this host's own <c>ApplyOp</c> has accepted since the last
    /// drain — see <see cref="DrainStoreOps"/>/<see cref="HandleStoreOp"/>'s own remarks. Guarded
    /// by <see cref="_storeOpDrainQueueGate"/> because it is written from this host's own
    /// packet-receive path and read from <c>Pumps.StorePump</c>'s tick, which run on different
    /// threads (the same cross-thread boundary <see cref="StoreStore"/>'s own <c>_gate</c>
    /// protects).</summary>
    private readonly Queue<StoreOpPacket> _storeOpDrainQueue = new();
    private readonly Lock _storeOpDrainQueueGate = new();
    private const int MaxStoreOpDrainQueue = 1024;

    public StoreStore Store => _storeStore;

    /// <summary>Someone's local store scan found a change; broadcast it reliably. Gated on the
    /// store: a delta the store already reflects (this host's own earlier publish echoing, or a
    /// peer op already applied) reports no change and is never re-sent. See
    /// <see cref="IStoreSession.PublishStoreOp"/>'s own remarks for <paramref
    /// name="localBaselineIfUnknown"/>.</summary>
    public void PublishStoreOp(StoreOpKind kind, StoreStackKey key, uint countDelta, int localBaselineIfUnknown = 0)
    {
        ushort clamped = (ushort)Math.Min(countDelta, ushort.MaxValue);
        if (clamped == 0 || !_storeStore.Record(kind, key, clamped, localBaselineIfUnknown))
        {
            return;
        }

        _storeSender.Enqueue(HostPeerId, kind, key, clamped, SessionClock.NowMs);
    }

    public IReadOnlyDictionary<StoreStackKey, int> StoreTotals => _storeStore.Snapshot();

    public IReadOnlySet<StoreStackKey> StoreKnownKeys => _storeStore.KnownKeys;

    /// <summary>See <see cref="IStoreSession.DrainStoreOps"/>'s own remarks — every op queued
    /// here comes from <see cref="HandleStoreOp"/>'s own successful <c>ApplyOp</c> call, i.e. a
    /// CLIENT-originated fact this host's engine also owes (the host is never its own client, so
    /// there is no self-echo risk to filter here the way <see cref="CoopClient.HandleStoreOp"/>
    /// must).</summary>
    public IReadOnlyList<StoreOpPacket> DrainStoreOps()
    {
        lock (_storeOpDrainQueueGate)
        {
            if (_storeOpDrainQueue.Count == 0)
            {
                return [];
            }

            List<StoreOpPacket> drained = [.. _storeOpDrainQueue];
            _storeOpDrainQueue.Clear();
            return drained;
        }
    }

    /// <summary>Retransmits store ops to every client until each has acked — the same shape as
    /// <see cref="BroadcastPartyPool"/>.</summary>
    private void BroadcastStore(long now, List<PeerConnection> recipients)
    {
        List<uint> recipientIds = [.. recipients.Select(p => p.Id)];

        foreach (StoreOpPacket op in _storeSender.CollectDue(recipientIds, now))
        {
            int length = PacketCodec.Write(_sendBuffer, op);
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

    /// <summary>
    /// A client's own store scan found a change; apply it to the host's own store (so it
    /// converges too) and relay it onward under the SAME origin peer id but a FRESH host-minted
    /// sequence — identical reasoning to <see cref="HandlePartyPoolOp"/>'s relay (a store total
    /// is not naturally idempotent, so the relay keeps the ORIGINAL contributor's id so that
    /// peer's own receive handler can recognise its own echo by origin; only the sequence is
    /// re-minted, since <see cref="ReliableStoreSender"/>'s pending table is keyed by a bare
    /// <c>uint</c> sequence).
    /// </summary>
    private void HandleStoreOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        if (_storeStore.ApplyOp(op))
        {
            _storeSender.Enqueue(op.OriginPeerId, op.Kind, op.Key, op.CountDelta, now);

            lock (_storeOpDrainQueueGate)
            {
                if (_storeOpDrainQueue.Count < MaxStoreOpDrainQueue)
                {
                    _storeOpDrainQueue.Enqueue(op);
                }
                else
                {
                    Console.Error.WriteLine(
                        "[coop] store op drain queue refused: already holding "
                        + $"{MaxStoreOpDrainQueue} unapplied ops — the drain has fallen behind.");
                }
            }
        }

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new StoreAckPacket(HostPeerId, op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    /// <summary>A client's version/digest disagrees; re-offer everything, the same way
    /// <see cref="HandlePartyPoolSyncRequest"/> does — this channel has no separate snapshot
    /// packet either.</summary>
    private void HandleStoreSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        ResyncStore(SessionClock.NowMs);
        _storeSender.ResetBackoff();
    }

    /// <summary>
    /// Re-offers every (store, stack) entry this session has EVER transacted, as absolute
    /// <see cref="StoreOpKind.Set"/> facts under the host's own id — identical reasoning to
    /// <see cref="ResyncPartyPool"/>: no per-contributor history to replay, and a peer being
    /// resynced may already hold a different total for a key. Two callers: <see cref="HandleHello"/>
    /// on (re-)admission, and <see cref="HandleStoreSyncRequest"/> for a still-connected peer
    /// whose digest disagrees.
    ///
    /// Deliberately <see cref="StoreStore.SnapshotKnown"/>, NOT <see cref="StoreStore.Snapshot"/>:
    /// the latter excludes a key legitimately depleted to zero (pruned once its total hits zero),
    /// which would silently omit that fact from the resync and leave a peer who never locally
    /// scanned the depleted item believing it is still pristine forever
    /// (docs/research/store-sync.md's "Bug 4" write-up — the SAME "absent means unknown, not
    /// zero" rule <c>Pumps.StorePump</c>'s reconcile step now also honours). A store nobody has
    /// ever transacted contributes NOTHING here — its keys were never recorded — so this resync
    /// never touches a store this session has no business asserting anything about.
    /// </summary>
    private void ResyncStore(long nowMs)
    {
        foreach ((StoreStackKey key, int count) in _storeStore.SnapshotKnown())
        {
            _storeSender.Enqueue(
                HostPeerId, StoreOpKind.Set, key, (ushort)Math.Min(count, ushort.MaxValue), nowMs);
        }
    }

    private void HandleStoreAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreAck(span) is { } ack)
        {
            _storeSender.Acknowledge(ack.PeerId, ack.OpSequence);
        }
    }

    /// <summary>See <see cref="IStoreSession.RequestStoreResync"/>'s own remarks. The host is
    /// already the authority for this channel — there is nobody to ask — so this just re-runs
    /// its own resync broadcast, exactly as if a client had asked for one.</summary>
    public void RequestStoreResync() => ResyncStore(SessionClock.NowMs);
}

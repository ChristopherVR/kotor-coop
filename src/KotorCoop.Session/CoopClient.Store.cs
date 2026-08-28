using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — the merchant/store stock channel: this client's own detected changes queued to
/// the reliable sender, and the host's reliable ops/acks applied here. See
/// <see cref="CoopHost.Store"/> for the host half and docs/research/store-sync.md for the design.
/// </summary>
public sealed partial class CoopClient
{
    private readonly StoreStore _storeStore = new();
    private readonly ReliableStoreSender _storeSender = new();

    /// <summary>Every REMOTE store op this client's own <c>ApplyOp</c> has accepted since the
    /// last drain — see <see cref="DrainStoreOps"/>/<see cref="HandleStoreOp"/>'s own remarks.
    /// Guarded the same cross-thread way <see cref="CoopHost"/>'s own drain queue is.</summary>
    private readonly Queue<StoreOpPacket> _storeOpDrainQueue = new();
    private readonly Lock _storeOpDrainQueueGate = new();
    private const int MaxStoreOpDrainQueue = 1024;

    public StoreStore Store => _storeStore;

    /// <summary>This client's own store scan found a change. Reliable, like every world-truth
    /// channel here: a change is a one-shot fact, not a level the next tick re-sends. See
    /// <see cref="IStoreSession.PublishStoreOp"/>'s own remarks for
    /// <paramref name="localBaselineIfUnknown"/>.</summary>
    public void PublishStoreOp(StoreOpKind kind, StoreStackKey key, uint countDelta, int localBaselineIfUnknown = 0)
    {
        ushort clamped = (ushort)Math.Min(countDelta, ushort.MaxValue);
        if (clamped == 0 || !_storeStore.Record(kind, key, clamped, localBaselineIfUnknown))
        {
            return;
        }

        // Queued under the last admitted identity so a change made during a brief outage still
        // reaches the host afterwards — see SetFlag.
        _storeSender.Enqueue(_sessionPeerId, kind, key, clamped, SessionClock.NowMs);
    }

    public IReadOnlyDictionary<StoreStackKey, int> StoreTotals => _storeStore.Snapshot();

    public IReadOnlySet<StoreStackKey> StoreKnownKeys => _storeStore.KnownKeys;

    /// <summary>See <see cref="IStoreSession.DrainStoreOps"/>'s own remarks — every op queued
    /// here comes from <see cref="HandleStoreOp"/>'s own successful <c>ApplyOp</c> call, which
    /// that method's own origin check already restricts to a genuinely REMOTE fact (a peer's
    /// buy/sell, or the host's resync).</summary>
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

    /// <summary>
    /// Records a store op from the host and acks it.
    ///
    /// ⚠ <paramref name="span"/> may be THIS CLIENT'S OWN change, echoed back by the host's
    /// broadcast (<see cref="CoopHost.HandleStoreOp"/> relays under the ORIGINAL contributor's id
    /// specifically so this check can work). If so, the store already reflects it — recorded the
    /// moment <see cref="PublishStoreOp"/> queued it — so applying the relay again would
    /// double-count it (and, per the same reasoning, must NOT be queued into
    /// <see cref="_storeOpDrainQueue"/> — this instance's own engine already reflects it, that is
    /// literally what triggered the original publish). Still ack unconditionally: the host's
    /// sender is waiting for this specific peer's ack to retire the op regardless of who
    /// originated it.
    /// </summary>
    private void HandleStoreOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        if (op.OriginPeerId != _localPeerId && _storeStore.ApplyOp(op))
        {
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

        int length = PacketCodec.Write(_replyBuffer, new StoreAckPacket(_localPeerId, op.OpSequence));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleStoreAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreAck(span) is { } ack)
        {
            // The ack names its sender (the host) so the shared per-recipient sender can mark
            // exactly that recipient done.
            _storeSender.Acknowledge(ack.PeerId, ack.OpSequence);
        }
    }

    /// <summary>See <see cref="IStoreSession.RequestStoreResync"/>'s own remarks. Sent from a
    /// LOCAL BUFFER, not the shared <c>_replyBuffer</c> other handlers here use — this can be
    /// called from <c>Pumps.StorePump</c>'s own tick thread, a different thread than the one
    /// that owns <c>_replyBuffer</c>, so it must not share mutable state with it.</summary>
    public void RequestStoreResync()
    {
        Span<byte> buffer = stackalloc byte[32];
        int length = PacketCodec.Write(
            buffer, new StoreSyncRequestPacket(_localPeerId, _storeStore.Version, _storeStore.Digest));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, buffer[..length]);
        }
    }
}

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

    public StoreStore Store => _storeStore;

    /// <summary>This client's own store scan found a change. Reliable, like every world-truth
    /// channel here: a change is a one-shot fact, not a level the next tick re-sends.</summary>
    public void PublishStoreOp(StoreOpKind kind, StoreStackKey key, uint countDelta)
    {
        ushort clamped = (ushort)Math.Min(countDelta, ushort.MaxValue);
        if (clamped == 0 || !_storeStore.Record(kind, key, clamped))
        {
            return;
        }

        // Queued under the last admitted identity so a change made during a brief outage still
        // reaches the host afterwards — see SetFlag.
        _storeSender.Enqueue(_sessionPeerId, kind, key, clamped, SessionClock.NowMs);
    }

    public IReadOnlyDictionary<StoreStackKey, int> StoreTotals => _storeStore.Snapshot();

    /// <summary>
    /// Records a store op from the host and acks it.
    ///
    /// ⚠ <paramref name="span"/> may be THIS CLIENT'S OWN change, echoed back by the host's
    /// broadcast (<see cref="CoopHost.HandleStoreOp"/> relays under the ORIGINAL contributor's id
    /// specifically so this check can work). If so, the store already reflects it — recorded the
    /// moment <see cref="PublishStoreOp"/> queued it — so applying the relay again would
    /// double-count it. Still ack unconditionally: the host's sender is waiting for this specific
    /// peer's ack to retire the op regardless of who originated it.
    /// </summary>
    private void HandleStoreOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadStoreOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        if (op.OriginPeerId != _localPeerId)
        {
            _storeStore.ApplyOp(op);
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
}

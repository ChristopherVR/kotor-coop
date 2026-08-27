using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — the shared party item-pool channel: this client's own detected changes queued
/// to the reliable sender, and the host's reliable ops/acks applied here. See
/// <see cref="CoopHost.PartyPool"/> for the host half and docs/research/party-pool-sync.md for
/// the design.
/// </summary>
public sealed partial class CoopClient
{
    private readonly PartyPoolStore _partyPoolStore = new();
    private readonly ReliablePartyPoolSender _partyPoolSender = new();

    public PartyPoolStore PartyPool => _partyPoolStore;

    /// <summary>This client's own pool scan found a change. Reliable, like every world-truth
    /// channel here: a change is a one-shot fact, not a level the next tick re-sends.</summary>
    public void PublishPartyPoolOp(PartyPoolOpKind kind, PartyPoolStackKey key, uint countDelta)
    {
        ushort clamped = (ushort)Math.Min(countDelta, ushort.MaxValue);
        if (clamped == 0 || !_partyPoolStore.Record(kind, key, clamped))
        {
            return;
        }

        // Queued under the last admitted identity so a change made during a brief outage still
        // reaches the host afterwards — see SetFlag.
        _partyPoolSender.Enqueue(_sessionPeerId, kind, key, clamped, SessionClock.NowMs);
    }

    public IReadOnlyDictionary<PartyPoolStackKey, int> PartyPoolTotals => _partyPoolStore.Snapshot();

    /// <summary>
    /// Records a party-pool op from the host and acks it.
    ///
    /// ⚠ <paramref name="span"/> may be THIS CLIENT'S OWN change, echoed back by the host's
    /// broadcast (<see cref="CoopHost.HandlePartyPoolOp"/> relays under the ORIGINAL
    /// contributor's id specifically so this check can work). If so, the store already reflects
    /// it — it was recorded the moment <see cref="PublishPartyPoolOp"/> queued it — so applying
    /// the relay again would double-count it. Still ack unconditionally: the host's sender is
    /// waiting for this specific peer's ack to retire the op regardless of who originated it.
    /// </summary>
    private void HandlePartyPoolOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyPoolOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        if (op.OriginPeerId != _localPeerId)
        {
            _partyPoolStore.ApplyOp(op);
        }

        int length = PacketCodec.Write(
            _replyBuffer, new PartyPoolAckPacket(_localPeerId, op.OpSequence));
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandlePartyPoolAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadPartyPoolAck(span) is { } ack)
        {
            // The ack names its sender (the host) so the shared per-recipient sender can mark
            // exactly that recipient done.
            _partyPoolSender.Acknowledge(ack.PeerId, ack.OpSequence);
        }
    }
}

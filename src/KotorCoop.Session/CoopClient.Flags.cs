using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — World event-flag replication: this client's own writes apply locally straight
/// away and are queued to the reliable sender even while disconnected; the host's
/// acks and snapshot merges are applied here too.
/// </summary>
public sealed partial class CoopClient
{
    private readonly ReliableFlagSender _flagSender = new();

    public EventFlagStore Flags { get; } = new();

    public void SetFlag(uint flagId, sbyte value)
    {
        // Applied locally straight away so the local player is not waiting on a round
        // trip; the host's authoritative version will confirm or correct it. Always mints a
        // fresh, outranking serial (see EventFlagStore.Set), so this local write is
        // guaranteed to win over whatever this store already held — including a genuine
        // decrease.
        if (!Flags.Set(flagId, value, _sessionPeerId))
        {
            return;
        }

        // ⚠ QUEUED EVEN WHILE THE LINK IS DOWN. The gate used to be _localPeerId, which is
        // zeroed the moment the host goes quiet — so every world change the player made
        // during an outage was applied locally and never queued at all, and the reliable
        // channel that exists to survive exactly this had nothing in it to deliver.
        if (_sessionPeerId == 0 || !Flags.TryGetStamp(flagId, out uint serial, out _))
        {
            return;
        }

        // Null means the reliable sender refused the op (its pending table is at its cap,
        // logged there) — see RetransmitBackoff's remarks. Nothing to send; a session that
        // far gone is not coming back, but the local store still applied the change above so
        // this player's own game is at least correct.
        if (_flagSender.Enqueue(_sessionPeerId, flagId, value, SessionClock.NowMs, serial) is not { } op)
        {
            return;
        }

        if (IsConnected)
        {
            SendFlagOp(op);
        }
    }

    private void SendFlagOp(FlagOpPacket op)
    {
        int length = PacketCodec.Write(_sendBuffer, op);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void HandleFlagAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadFlagAck(span) is { } ack)
        {
            _flagSender.Acknowledge(ack.OpSequence);
        }
    }

    private void HandleFlagSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadFlagSnapshot(span) is { } flagSnapshot)
        {
            // Merge, never replace: each entry applies last-writer-wins by its own
            // (Serial, OriginPeerId), so a snapshot can neither erase nor blindly
            // stomp a local advance (or decrease) this instance made that the host
            // has not heard of yet — see EventFlagStore's remarks. Flags used to
            // overwrite outright, which is how a change made during an outage
            // disappeared on reconnect.
            Flags.MergeSnapshot(flagSnapshot.Flags, flagSnapshot.Version);
        }
    }
}

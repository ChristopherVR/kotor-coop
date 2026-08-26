using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — World event-flag replication: the host is authoritative, so a client's op is
/// applied and acked immediately; clients pick up host-side changes through the
/// flag version/digest carried in every world snapshot.
/// </summary>
public sealed partial class CoopHost
{
    public EventFlagStore Flags { get; } = new();

    public void SetFlag(uint flagId, sbyte value)
    {
        // The host is authoritative, so its own changes apply immediately. Clients pick
        // them up through the version tripwire in the next world snapshot.
        Flags.Set(flagId, value, HostPeerId);
    }

    private void HandleFlagOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadFlagOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        Flags.ApplyOp(op);

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new FlagAckPacket(op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleFlagSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadFlagSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        FlagSnapshotPacket snapshot = new(Flags.Version, Flags.SnapshotEntries());
        int length = PacketCodec.Write(_replyBuffer, snapshot);
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }
}

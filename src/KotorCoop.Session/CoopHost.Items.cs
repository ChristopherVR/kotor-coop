using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopHost — Ground-item replication: dropped/removed items, host-authoritative the same way
/// flags are, plus the host's item-id allocator (top 16 bits = owning peer).
/// </summary>
public sealed partial class CoopHost
{
    public GroundItemStore Items { get; } = new();

    private uint _nextItemSequence = 1;

    public uint DropItem(
        string resref, string module, Vector3f position, ItemIdentity identity = default)
    {
        uint itemId;
        lock (_gate)
        {
            itemId = (HostPeerId << 16) | (_nextItemSequence++ & 0xFFFF);
        }

        return Items.Drop(new GroundItem(itemId, resref, module, position, identity))
            ? itemId
            : 0;
    }

    public void RemoveItem(uint itemId) => Items.Remove(itemId);

    private void HandleItemOp(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadItemOp(span) is not { } op)
        {
            _channel.NoteMalformed();
            return;
        }

        Items.ApplyOp(op);

        // Ack unconditionally: a duplicate op means the previous ack was lost.
        int length = PacketCodec.Write(_replyBuffer, new ItemAckPacket(op.OpSequence));
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }

    private void HandleItemSyncRequest(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadItemSyncRequest(span) is null)
        {
            _channel.NoteMalformed();
            return;
        }

        ItemSnapshotPacket snapshot = new(Items.Version, Items.Snapshot());
        int length = PacketCodec.Write(_replyBuffer, snapshot);
        if (length > 0)
        {
            _channel.Send(from, _replyBuffer.AsSpan(0, length));
        }
    }
}

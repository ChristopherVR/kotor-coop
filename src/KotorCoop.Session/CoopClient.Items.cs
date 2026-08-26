using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// CoopClient — Ground-item replication: drops/removals applied locally straight away and queued
/// to the reliable sender, plus the host's acks and full-snapshot replace.
/// </summary>
public sealed partial class CoopClient
{
    private readonly ReliableItemSender _itemSender = new();

    public GroundItemStore Items { get; } = new();

    public uint DropItem(
        string resref, string module, Vector3f position, ItemIdentity identity = default)
    {
        if (_sessionPeerId == 0)
        {
            return 0;
        }

        uint itemId;
        lock (_gate)
        {
            itemId = (_sessionPeerId << 16) | (_nextItemSequence++ & 0xFFFF);
        }

        GroundItem item = new(itemId, resref, module, position, identity);

        // Applied locally straight away so the dropping player is not waiting on a round
        // trip; the host's authoritative version will confirm or correct it.
        if (!Items.Drop(item))
        {
            return 0;
        }

        ItemOpPacket op = _itemSender.Enqueue(_sessionPeerId, ItemOpKind.Drop, item, SessionClock.NowMs);

        if (IsConnected)
        {
            SendItemOp(op);
        }

        return itemId;
    }

    public void RemoveItem(uint itemId)
    {
        if (!Items.Remove(itemId) || _sessionPeerId == 0)
        {
            return;
        }

        GroundItem tombstone = new(itemId, string.Empty, string.Empty, Vector3f.Zero);
        ItemOpPacket op = _itemSender.Enqueue(_sessionPeerId, ItemOpKind.Remove, tombstone, SessionClock.NowMs);

        if (IsConnected)
        {
            SendItemOp(op);
        }
    }

    private void SendItemOp(ItemOpPacket op)
    {
        int length = PacketCodec.Write(_sendBuffer, op);
        if (length > 0)
        {
            _channel.Send(_hostEndPoint, _sendBuffer.AsSpan(0, length));
        }
    }

    private void HandleItemAck(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadItemAck(span) is { } itemAck)
        {
            _itemSender.Acknowledge(itemAck.OpSequence);
        }
    }

    private void HandleItemSnapshot(IPEndPoint from, ReadOnlySpan<byte> span, long now)
    {
        if (PacketCodec.ReadItemSnapshot(span) is { } itemSnapshot)
        {
            Items.ReplaceAll(itemSnapshot.Items, itemSnapshot.Version);
        }
    }
}

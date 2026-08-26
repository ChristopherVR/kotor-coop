namespace KotorCoop.Protocol;

// The ground-item channel: drop/remove ops, the full-resync snapshot, and the shared
// GroundItem read/write helper both use.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, ItemOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ItemOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt8((byte)packet.Kind);
        WriteGroundItem(ref w, packet.Item);
        return w.Failed ? 0 : w.Position;
    }

    public static ItemOpPacket? ReadItemOp(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ItemOp)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        byte kind = r.ReadUInt8();
        GroundItem? item = ReadGroundItem(ref r);

        if (r.Failed || item is null || kind is not ((byte)ItemOpKind.Drop or (byte)ItemOpKind.Remove))
        {
            return null;
        }

        return new ItemOpPacket(origin, opSeq, (ItemOpKind)kind, item);
    }

    public static int Write(Span<byte> buffer, ItemAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ItemAck);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static ItemAckPacket? ReadItemAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ItemAck)
        {
            return null;
        }

        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new ItemAckPacket(opSeq);
    }

    public static int Write(Span<byte> buffer, ItemSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ItemSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        return w.Failed ? 0 : w.Position;
    }

    public static ItemSyncRequestPacket? ReadItemSyncRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ItemSyncRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint version = r.ReadUInt32();
        return r.Failed ? null : new ItemSyncRequestPacket(peerId, version);
    }

    public static int Write(Span<byte> buffer, ItemSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ItemSnapshot);
        w.WriteUInt32(packet.Version);

        int count = Math.Min(packet.Items.Count, ProtocolConstants.MaxGroundItems);
        w.WriteUInt16((ushort)count);

        for (int i = 0; i < count; i++)
        {
            WriteGroundItem(ref w, packet.Items[i]);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static ItemSnapshotPacket? ReadItemSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ItemSnapshot)
        {
            return null;
        }

        uint version = r.ReadUInt32();
        ushort count = r.ReadUInt16();
        if (r.Failed || count > ProtocolConstants.MaxGroundItems)
        {
            return null;
        }

        List<GroundItem> items = new(count);
        for (int i = 0; i < count; i++)
        {
            GroundItem? item = ReadGroundItem(ref r);
            if (r.Failed || item is null)
            {
                return null;
            }

            items.Add(item);
        }

        return new ItemSnapshotPacket(version, items);
    }

    private static void WriteGroundItem(ref PacketWriter w, GroundItem item)
    {
        w.WriteUInt32(item.ItemId);
        w.WriteString(item.Resref, ProtocolConstants.MaxResrefLength);
        w.WriteString(item.Module, ProtocolConstants.MaxModuleLength);
        w.WriteVector3(item.Position);

        // Nine fixed bytes of per-instance state, always present. A variable-length
        // encoding would save eight bytes on an unknown identity and buy a second way for
        // the two sides to disagree about how long an item entry is; the worst-case
        // snapshot still fits one datagram with the fixed form (asserted by a test).
        w.WriteUInt16(item.Identity.StackSize);
        w.WriteUInt8(item.Identity.Charges);
        w.WriteUInt8(item.Identity.MaxCharges);
        w.WriteUInt32(item.Identity.Upgrades);
        w.WriteUInt8(item.Identity.Flags);
    }

    private static GroundItem? ReadGroundItem(ref PacketReader r)
    {
        uint itemId = r.ReadUInt32();
        string resref = r.ReadString();
        string module = r.ReadString();
        Vector3f position = r.ReadVector3();

        ushort stackSize = r.ReadUInt16();
        byte charges = r.ReadUInt8();
        byte maxCharges = r.ReadUInt8();
        uint upgrades = r.ReadUInt32();
        byte flags = r.ReadUInt8();

        return r.Failed
            ? null
            : new GroundItem(
                itemId, resref, module, position,
                new ItemIdentity(stackSize, charges, maxCharges, upgrades, flags));
    }
}

namespace KotorCoop.Protocol;

// The merchant/store stock channel: add/remove ops, their acks, and the resync request.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, StoreOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.StoreOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteUInt32(packet.Key.StoreObjectId);
        w.WriteString(packet.Key.Resref, ProtocolConstants.MaxResrefLength);
        w.WriteUInt8(packet.Key.Charges);
        w.WriteUInt8(packet.Key.MaxCharges);
        w.WriteUInt32(packet.Key.Upgrades);
        w.WriteUInt8(packet.Key.Flags);
        w.WriteUInt16(packet.CountDelta);
        return w.Failed ? 0 : w.Position;
    }

    public static StoreOpPacket? ReadStoreOp(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.StoreOp)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        byte kind = r.ReadUInt8();
        uint storeObjectId = r.ReadUInt32();
        string resref = r.ReadString(ProtocolConstants.MaxResrefLength);
        byte charges = r.ReadUInt8();
        byte maxCharges = r.ReadUInt8();
        uint upgrades = r.ReadUInt32();
        byte flags = r.ReadUInt8();
        ushort countDelta = r.ReadUInt16();

        if (r.Failed
            || kind is not ((byte)StoreOpKind.Add or (byte)StoreOpKind.Remove or (byte)StoreOpKind.Set)
            || (countDelta == 0 && kind != (byte)StoreOpKind.Set))
        {
            return null;
        }

        StoreStackKey key = new(storeObjectId, resref, charges, maxCharges, upgrades, flags);
        return new StoreOpPacket(origin, opSeq, (StoreOpKind)kind, key, countDelta);
    }

    public static int Write(Span<byte> buffer, StoreAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.StoreAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static StoreAckPacket? ReadStoreAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.StoreAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new StoreAckPacket(peerId, opSeq);
    }

    public static int Write(Span<byte> buffer, StoreSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.StoreSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        w.WriteUInt64(packet.KnownDigest);
        return w.Failed ? 0 : w.Position;
    }

    public static StoreSyncRequestPacket? ReadStoreSyncRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.StoreSyncRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint version = r.ReadUInt32();
        ulong digest = r.ReadUInt64();
        return r.Failed ? null : new StoreSyncRequestPacket(peerId, version, digest);
    }
}

namespace KotorCoop.Protocol;

// The shared party item-pool channel: add/remove ops, their acks, and the resync request.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, PartyPoolOpPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartyPoolOp);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteString(packet.Key.Resref, ProtocolConstants.MaxResrefLength);
        w.WriteUInt8(packet.Key.Charges);
        w.WriteUInt8(packet.Key.MaxCharges);
        w.WriteUInt32(packet.Key.Upgrades);
        w.WriteUInt8(packet.Key.Flags);
        w.WriteUInt16(packet.CountDelta);
        return w.Failed ? 0 : w.Position;
    }

    public static PartyPoolOpPacket? ReadPartyPoolOp(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartyPoolOp)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        byte kind = r.ReadUInt8();
        string resref = r.ReadString(ProtocolConstants.MaxResrefLength);
        byte charges = r.ReadUInt8();
        byte maxCharges = r.ReadUInt8();
        uint upgrades = r.ReadUInt32();
        byte flags = r.ReadUInt8();
        ushort countDelta = r.ReadUInt16();

        if (r.Failed
            || kind is not ((byte)PartyPoolOpKind.Add or (byte)PartyPoolOpKind.Remove
                or (byte)PartyPoolOpKind.Set)
            || (countDelta == 0 && kind != (byte)PartyPoolOpKind.Set))
        {
            return null;
        }

        PartyPoolStackKey key = new(resref, charges, maxCharges, upgrades, flags);
        return new PartyPoolOpPacket(origin, opSeq, (PartyPoolOpKind)kind, key, countDelta);
    }

    public static int Write(Span<byte> buffer, PartyPoolAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartyPoolAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static PartyPoolAckPacket? ReadPartyPoolAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartyPoolAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new PartyPoolAckPacket(peerId, opSeq);
    }

    public static int Write(Span<byte> buffer, PartyPoolSyncRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartyPoolSyncRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.KnownVersion);
        w.WriteUInt64(packet.KnownDigest);
        return w.Failed ? 0 : w.Position;
    }

    public static PartyPoolSyncRequestPacket? ReadPartyPoolSyncRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartyPoolSyncRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint version = r.ReadUInt32();
        ulong digest = r.ReadUInt64();
        return r.Failed ? null : new PartyPoolSyncRequestPacket(peerId, version, digest);
    }
}

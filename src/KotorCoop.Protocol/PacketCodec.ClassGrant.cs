namespace KotorCoop.Protocol;

// The class-grant relay's two packets (docs/research/class-and-power-grants.md): the grant
// itself and its ack. Same fixed-width shape as the dialogue-grant pair in
// PacketCodec.Dialogue.cs, minus the item list.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, ClassGrantPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ClassGrant);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteInt32(packet.ClassId);
        return w.Failed ? 0 : w.Position;
    }

    public static ClassGrantPacket? ReadClassGrant(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ClassGrant)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        int classId = r.ReadInt32();

        if (r.Failed || classId < 0 || classId > ClassGrantPacket.MaxClassId)
        {
            // A class id this side never writes: malformed, not something to hand the plugin.
            return null;
        }

        return new ClassGrantPacket(origin, opSeq, classId);
    }

    public static int Write(Span<byte> buffer, ClassGrantAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ClassGrantAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static ClassGrantAckPacket? ReadClassGrantAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ClassGrantAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new ClassGrantAckPacket(peerId, origin, opSeq);
    }
}

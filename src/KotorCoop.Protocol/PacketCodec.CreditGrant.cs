namespace KotorCoop.Protocol;

// The credits-reward relay (docs/research/personal-inventory.md §4). Byte-level encoding is
// asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, CreditGrantPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CreditGrant);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteInt32(packet.Amount);
        return w.Failed ? 0 : w.Position;
    }

    public static CreditGrantPacket? ReadCreditGrant(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.CreditGrant)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        int amount = r.ReadInt32();

        // A grant is never zero/negative on the wire (the sender's own guard, see
        // CreditGrantSender) — a malformed or hostile peer's claim is rejected here rather than
        // trusted as "nothing to apply" or, worse, a credits REMOVAL.
        if (amount <= 0)
        {
            return null;
        }

        return r.Failed ? null : new CreditGrantPacket(origin, opSeq, amount);
    }

    public static int Write(Span<byte> buffer, CreditGrantAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CreditGrantAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static CreditGrantAckPacket? ReadCreditGrantAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.CreditGrantAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new CreditGrantAckPacket(peerId, origin, opSeq);
    }
}

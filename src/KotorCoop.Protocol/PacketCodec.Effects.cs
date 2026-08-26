namespace KotorCoop.Protocol;

// Non-damage combat effects (stun, paralyze, knockdown, ...) forwarded from a peer's
// phantom to their real character.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, PeerEffectPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PeerEffect);
        w.WriteUInt32(packet.FromPeerId);
        w.WriteUInt32(packet.TargetPeerId);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteUInt32(packet.DurationMs);
        w.WriteUInt32(packet.Magnitude);
        w.WriteUInt32(packet.Sequence);

        return w.Failed ? 0 : w.Position;
    }

    public static PeerEffectPacket? ReadPeerEffect(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PeerEffect)
        {
            return null;
        }

        uint from = r.ReadUInt32();
        uint target = r.ReadUInt32();
        PeerEffectKind kind = (PeerEffectKind)r.ReadUInt8();
        uint durationMs = r.ReadUInt32();
        uint magnitude = r.ReadUInt32();
        uint sequence = r.ReadUInt32();

        return r.Failed ? null : new PeerEffectPacket(from, target, kind, durationMs, magnitude, sequence);
    }
}

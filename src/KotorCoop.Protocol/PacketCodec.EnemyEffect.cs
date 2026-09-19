namespace KotorCoop.Protocol;

// The client-cast enemy-effect relay's two packets (docs/research/enemy-effects.md): the report
// and its ack. Fixed-width like the class-grant pair, plus the module string the enemy census
// also carries. Byte-level encoding is asserted by EnemyEffectRelayTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, EnemyEffectPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.EnemyEffect);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.EnemyId);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteUInt32(packet.DurationMs);
        w.WriteUInt32(packet.Magnitude);
        return w.Failed ? 0 : w.Position;
    }

    public static EnemyEffectPacket? ReadEnemyEffect(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.EnemyEffect)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        string module = r.ReadString();
        uint enemyId = r.ReadUInt32();
        byte kind = r.ReadUInt8();
        uint durationMs = r.ReadUInt32();
        uint magnitude = r.ReadUInt32();

        // A kind this side never forwards (a buff, a disguise, poison, an unknown byte) is
        // malformed, not something to hand the plugin: the receiver never trusts the wire's
        // kind to have been filtered by the sender.
        if (r.Failed || enemyId == 0 || !EnemyEffectRules.IsForwardable((PeerEffectKind)kind))
        {
            return null;
        }

        return new EnemyEffectPacket(
            origin, opSeq, module, enemyId, (PeerEffectKind)kind,
            EnemyEffectRules.ClampDurationMs(durationMs), EnemyEffectRules.ClampMagnitude(magnitude));
    }

    public static int Write(Span<byte> buffer, EnemyEffectAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.EnemyEffectAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static EnemyEffectAckPacket? ReadEnemyEffectAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.EnemyEffectAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new EnemyEffectAckPacket(peerId, origin, opSeq);
    }
}

namespace KotorCoop.Protocol;

// The per-object interaction lease: a peer's claim and the host's authoritative set
// of currently granted locks.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, InteractionClaimPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.InteractionClaim);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteString(packet.Tag, ProtocolConstants.MaxTagLength);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteUInt8(packet.Active ? (byte)1 : (byte)0);
        w.WriteUInt32(packet.TargetId); // version 37: InteractionKind.Container's placeable id
        return w.Failed ? 0 : w.Position;
    }

    public static InteractionClaimPacket? ReadInteractionClaim(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.InteractionClaim)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        string tag = r.ReadString();
        byte kind = r.ReadUInt8();
        byte active = r.ReadUInt8();
        uint targetId = r.ReadUInt32();

        // ⚠ An unknown kind is DROPPED rather than coerced. A lock whose release rules this
        // build does not know is worse than no lock at all, because it is the OWNER that
        // would hold the body and never learn when to stop.
        if (r.Failed || kind > (byte)InteractionKind.Container)
        {
            return null;
        }

        return new InteractionClaimPacket(peerId, module, tag, (InteractionKind)kind, active != 0, targetId);
    }

    public static int Write(Span<byte> buffer, InteractionLockPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.InteractionLock);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);

        int count = Math.Min(packet.Locks.Count, ProtocolConstants.MaxInteractionLocks);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            InteractionLock entry = packet.Locks[i];
            w.WriteUInt32(entry.PeerId);
            w.WriteString(entry.Tag, ProtocolConstants.MaxTagLength);
            w.WriteUInt8((byte)entry.Kind);
            w.WriteUInt32(entry.TargetId); // version 37
        }

        return w.Failed ? 0 : w.Position;
    }

    public static InteractionLockPacket? ReadInteractionLock(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.InteractionLock)
        {
            return null;
        }

        long serverTimeMs = r.ReadInt64();
        string module = r.ReadString();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxInteractionLocks)
        {
            return null;
        }

        List<InteractionLock> locks = new(count);
        for (int i = 0; i < count; i++)
        {
            uint peerId = r.ReadUInt32();
            string tag = r.ReadString();
            byte kind = r.ReadUInt8();
            uint targetId = r.ReadUInt32();

            if (r.Failed || kind > (byte)InteractionKind.Container)
            {
                return null;
            }

            locks.Add(new InteractionLock(peerId, tag, (InteractionKind)kind, targetId));
        }

        return r.Failed ? null : new InteractionLockPacket(serverTimeMs, module, locks);
    }
}

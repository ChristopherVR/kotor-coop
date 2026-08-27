namespace KotorCoop.Protocol;

// Shared progression: the host's absolute gold/XP broadcast and a peer's cumulative
// earned/spent/XP report.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, ProgressionSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ProgressionSnapshot);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt32(packet.Gold);
        w.WriteUInt32(packet.Experience);

        return w.Failed ? 0 : w.Position;
    }

    public static ProgressionSnapshotPacket? ReadProgressionSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ProgressionSnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        uint gold = r.ReadUInt32();
        uint xp = r.ReadUInt32();

        return r.Failed ? null : new ProgressionSnapshotPacket(serverTime, gold, xp);
    }

    public static int Write(Span<byte> buffer, ProgressionReportPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.ProgressionReport);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt32(packet.GoldEarned);
        w.WriteUInt32(packet.GoldSpent);
        w.WriteUInt32(packet.Experience);

        return w.Failed ? 0 : w.Position;
    }

    public static ProgressionReportPacket? ReadProgressionReport(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.ProgressionReport)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint generation = r.ReadUInt32();
        uint earned = r.ReadUInt32();
        uint spent = r.ReadUInt32();
        uint experience = r.ReadUInt32();

        return r.Failed
            ? null
            : new ProgressionReportPacket(peerId, generation, earned, spent, experience);
    }

    public static int Write(Span<byte> buffer, CompanionXpSnapshotPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CompanionXpSnapshot);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteUInt16(packet.PresentMask);

        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            w.WriteUInt32(slot < packet.Xp.Length ? packet.Xp[slot] : 0);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static CompanionXpSnapshotPacket? ReadCompanionXpSnapshot(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.CompanionXpSnapshot)
        {
            return null;
        }

        long serverTime = r.ReadInt64();
        ushort mask = r.ReadUInt16();

        uint[] xp = new uint[ProtocolConstants.PartyRosterSlots];
        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            xp[slot] = r.ReadUInt32();
        }

        return r.Failed ? null : new CompanionXpSnapshotPacket(serverTime, mask, xp);
    }

    public static int Write(Span<byte> buffer, CompanionXpReportPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CompanionXpReport);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt16(packet.PresentMask);

        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            w.WriteUInt32(slot < packet.Xp.Length ? packet.Xp[slot] : 0);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static CompanionXpReportPacket? ReadCompanionXpReport(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.CompanionXpReport)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        ushort mask = r.ReadUInt16();

        uint[] xp = new uint[ProtocolConstants.PartyRosterSlots];
        for (int slot = 0; slot < ProtocolConstants.PartyRosterSlots; slot++)
        {
            xp[slot] = r.ReadUInt32();
        }

        return r.Failed ? null : new CompanionXpReportPacket(peerId, mask, xp);
    }
}

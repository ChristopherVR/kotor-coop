namespace KotorCoop.Protocol;

// Client -> host: cumulative damage taken by a companion companion_local_combat has released.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, CompanionDamagePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CompanionDamage);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt32(packet.Incarnation); w.WriteUInt32(packet.HostSessionId);
        w.WriteUInt32(packet.HostLoadSerial); w.WriteUInt32(packet.HostNativeEpoch);

        int count = Math.Min(packet.Damage.Count, ProtocolConstants.MaxPartyMembers);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            CompanionDamageEntry entry = packet.Damage[i];
            w.WriteUInt8((byte)Math.Min(entry.RosterSlotPlusOne, (uint)byte.MaxValue));
            w.WriteUInt32(entry.CumulativeDamage); w.WriteUInt32(entry.OccupantGeneration);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static CompanionDamagePacket? ReadCompanionDamage(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.CompanionDamage)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint generation = r.ReadUInt32();
        uint incarnation = r.ReadUInt32(), session = r.ReadUInt32(), serial = r.ReadUInt32(), epoch = r.ReadUInt32();
        byte count = r.ReadUInt8();

        if (r.Failed || count > ProtocolConstants.MaxPartyMembers)
        {
            return null;
        }

        List<CompanionDamageEntry> damage = new(count);
        for (int i = 0; i < count; i++)
        {
            byte rosterSlotPlusOne = r.ReadUInt8();
            uint cumulative = r.ReadUInt32();
            uint occupant = r.ReadUInt32();

            if (r.Failed)
            {
                return null;
            }

            damage.Add(new CompanionDamageEntry(rosterSlotPlusOne, cumulative, occupant));
        }

        return new CompanionDamagePacket(peerId, generation, damage, incarnation, session, serial, epoch);
    }
}

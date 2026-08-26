namespace KotorCoop.Protocol;

// The full per-module creature liveness publish, wider than the combat census in
// PacketCodec.Enemies.cs and possibly paged across several datagrams.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, CreatureLivenessPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CreatureLiveness);
        w.WriteInt64(packet.ServerTimeMs);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt64(packet.Digest);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt8(packet.PageIndex);
        w.WriteUInt8(packet.PageCount);

        IReadOnlyList<CreatureLivenessEntry> entries = packet.Entries;
        int count = Math.Min(entries.Count, CreatureLivenessPacket.MaxEntriesPerPacket);
        w.WriteUInt8((byte)count);

        for (int i = 0; i < count; i++)
        {
            CreatureLivenessEntry entry = entries[i];
            w.WriteUInt32(entry.ObjectId);
            w.WriteUInt8((byte)entry.State);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static CreatureLivenessPacket? ReadCreatureLiveness(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.CreatureLiveness)
        {
            return null;
        }

        long serverTimeMs = r.ReadInt64();
        string module = r.ReadString();
        ulong digest = r.ReadUInt64();
        uint generation = r.ReadUInt32();
        byte pageIndex = r.ReadUInt8();
        byte pageCount = r.ReadUInt8();

        int count = r.ReadUInt8();
        if (r.Failed || count > CreatureLivenessPacket.MaxEntriesPerPacket)
        {
            // A count this side never writes: treat the datagram as malformed rather than
            // allocating on a number a peer chose.
            return null;
        }

        CreatureLivenessEntry[] entries = new CreatureLivenessEntry[count];
        for (int i = 0; i < count; i++)
        {
            uint objectId = r.ReadUInt32();
            byte state = r.ReadUInt8();

            // An unrecognised state byte from a future build: treat it as Alive rather than
            // failing the whole page. Alive is the "do nothing" verdict on the receiving side
            // (see LivenessRule), which is the safe direction to fail towards — never resurrect,
            // never destroy a body on a state this build cannot interpret.
            entries[i] = new CreatureLivenessEntry(
                objectId, Enum.IsDefined((CreatureLivenessState)state)
                    ? (CreatureLivenessState)state
                    : CreatureLivenessState.Alive);
        }

        return r.Failed ? null : new CreatureLivenessPacket(
            serverTimeMs, module, digest, generation, pageIndex, pageCount, entries);
    }
}

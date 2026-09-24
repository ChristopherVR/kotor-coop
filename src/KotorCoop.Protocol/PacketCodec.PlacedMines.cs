namespace KotorCoop.Protocol;

// Party-placed floor-mine replication (docs/research/mines.md §14): the client intent, its ack,
// and the host's roster level.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, PlacedMineIntentPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PlacedMineIntent);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        w.WriteUInt8((byte)packet.Kind);
        w.WriteUInt32(packet.ModuleHash);
        w.WriteUInt32(packet.CaptureSerial);
        w.WriteUInt32(packet.MineSerial);
        WritePlacedMineRecord(ref w, packet.Record);
        return w.Failed ? 0 : w.Position;
    }

    public static PlacedMineIntentPacket? ReadPlacedMineIntent(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PlacedMineIntent)
        {
            return null;
        }

        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        PlacedMineIntentKind kind = (PlacedMineIntentKind)r.ReadUInt8();
        uint moduleHash = r.ReadUInt32();
        uint captureSerial = r.ReadUInt32();
        uint mineSerial = r.ReadUInt32();
        PlacedMineRecord record = ReadPlacedMineRecord(ref r);

        if (r.Failed)
        {
            return null;
        }

        bool shaped = kind switch
        {
            PlacedMineIntentKind.Placed => captureSerial != 0 && record.IsValid,
            PlacedMineIntentKind.Gone => mineSerial != 0,
            _ => false,
        };

        return shaped
            ? new PlacedMineIntentPacket(origin, opSeq, kind, moduleHash, captureSerial, mineSerial, record)
            : null;
    }

    public static int Write(Span<byte> buffer, PlacedMineIntentAckPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PlacedMineIntentAck);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteUInt32(packet.OpSequence);
        return w.Failed ? 0 : w.Position;
    }

    public static PlacedMineIntentAckPacket? ReadPlacedMineIntentAck(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PlacedMineIntentAck)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint origin = r.ReadUInt32();
        uint opSeq = r.ReadUInt32();
        return r.Failed ? null : new PlacedMineIntentAckPacket(peerId, origin, opSeq);
    }

    public static int Write(Span<byte> buffer, PlacedMineRosterPacket packet)
    {
        if (packet.Entries.Count > PlacedMineRosterPacket.MaxEntries)
        {
            return 0;
        }

        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PlacedMineRoster);
        w.WriteUInt32(packet.Generation);
        w.WriteUInt32(packet.ModuleHash);
        w.WriteUInt32(packet.HighestSerial);
        w.WriteUInt8((byte)packet.Entries.Count);
        foreach (PlacedMineRosterEntry entry in packet.Entries)
        {
            w.WriteUInt32(entry.Serial);
            w.WriteUInt32(entry.OwnerPeerId);
            w.WriteUInt32(entry.CaptureSerial);
            WritePlacedMineRecord(ref w, entry.Record);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static PlacedMineRosterPacket? ReadPlacedMineRoster(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PlacedMineRoster)
        {
            return null;
        }

        uint generation = r.ReadUInt32();
        uint moduleHash = r.ReadUInt32();
        uint highest = r.ReadUInt32();
        int count = r.ReadUInt8();
        if (r.Failed || count > PlacedMineRosterPacket.MaxEntries)
        {
            return null;
        }

        List<PlacedMineRosterEntry> entries = new(count);
        uint previous = 0;
        for (int i = 0; i < count; i++)
        {
            uint serial = r.ReadUInt32();
            uint owner = r.ReadUInt32();
            uint capture = r.ReadUInt32();
            PlacedMineRecord record = ReadPlacedMineRecord(ref r);

            // Strictly ascending, never above the fence: the receiver registers identities in
            // serial order (PartyPlacedMineIdentityMap), so a shuffled roster is malformed.
            if (r.Failed || serial == 0 || serial <= previous || serial > highest || !record.IsValid)
            {
                return null;
            }

            previous = serial;
            entries.Add(new PlacedMineRosterEntry(serial, owner, capture, record));
        }

        return r.Failed ? null : new PlacedMineRosterPacket(generation, moduleHash, highest, entries);
    }

    private static void WritePlacedMineRecord(ref PacketWriter w, PlacedMineRecord record)
    {
        w.WriteUInt8(record.TrapType);
        w.WriteSingle(record.X);
        w.WriteSingle(record.Y);
        w.WriteSingle(record.Z);
        w.WriteSingle(record.OrientX);
        w.WriteSingle(record.OrientY);
        w.WriteUInt16(unchecked((ushort)record.DetectDc));
        w.WriteUInt16(unchecked((ushort)record.DisarmDc));
        w.WriteUInt16(record.Faction);
    }

    private static PlacedMineRecord ReadPlacedMineRecord(ref PacketReader r) => new(
        r.ReadUInt8(),
        r.ReadSingle(),
        r.ReadSingle(),
        r.ReadSingle(),
        r.ReadSingle(),
        r.ReadSingle(),
        unchecked((short)r.ReadUInt16()),
        unchecked((short)r.ReadUInt16()),
        r.ReadUInt16());
}

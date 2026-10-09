namespace KotorCoop.Protocol;

// Shared corpse loot: one player's body-bag takes. Do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, CorpseTakesPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.CorpseTakes);
        w.WriteUInt32(packet.HostSessionId);
        w.WriteUInt32(packet.OriginPeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);

        int count = Math.Min(packet.Entries.Count, CorpseTakesPacket.MaxEntries);
        w.WriteUInt8((byte)count);
        for (int i = 0; i < count; i++)
        {
            w.WriteSingle(packet.Entries[i].X);
            w.WriteSingle(packet.Entries[i].Y);
            w.WriteString(packet.Entries[i].Resref, ProtocolConstants.MaxResrefLength);
            w.WriteUInt16(packet.Entries[i].Count);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static CorpseTakesPacket? ReadCorpseTakes(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.CorpseTakes)
        {
            return null;
        }

        uint session = r.ReadUInt32();
        uint origin = r.ReadUInt32();
        string module = r.ReadString();
        byte count = r.ReadUInt8();
        if (r.Failed || count > CorpseTakesPacket.MaxEntries)
        {
            return null;
        }

        List<CorpseTakeEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            float x = r.ReadSingle();
            float y = r.ReadSingle();
            string resref = r.ReadString();
            ushort taken = r.ReadUInt16();
            if (r.Failed || resref.Length > ProtocolConstants.MaxResrefLength || !float.IsFinite(x) || !float.IsFinite(y))
            {
                return null;
            }

            entries.Add(new CorpseTakeEntry(x, y, resref, taken));
        }

        return new CorpseTakesPacket(session, origin, module, entries);
    }
}

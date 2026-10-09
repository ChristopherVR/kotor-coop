namespace KotorCoop.Protocol;

// Host spawns only: the host's script-spawned creature blueprints. Do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, SpawnTemplatesPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.SpawnTemplates);
        w.WriteUInt32(packet.HostSessionId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.NativeEpoch);

        int count = Math.Min(packet.Entries.Count, SpawnTemplatesPacket.MaxEntries);
        w.WriteUInt8((byte)count);
        for (int i = 0; i < count; i++)
        {
            w.WriteUInt32(packet.Entries[i].ObjectId);
            w.WriteString(packet.Entries[i].Template, ProtocolConstants.MaxResrefLength);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static SpawnTemplatesPacket? ReadSpawnTemplates(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.SpawnTemplates)
        {
            return null;
        }

        uint session = r.ReadUInt32();
        string module = r.ReadString();
        uint epoch = r.ReadUInt32();
        byte count = r.ReadUInt8();
        if (r.Failed || count > SpawnTemplatesPacket.MaxEntries)
        {
            return null;
        }

        List<SpawnTemplateEntry> entries = new(count);
        for (int i = 0; i < count; i++)
        {
            uint id = r.ReadUInt32();
            string template = r.ReadString();
            if (r.Failed || template.Length > ProtocolConstants.MaxResrefLength)
            {
                return null;
            }

            entries.Add(new SpawnTemplateEntry(id, template));
        }

        return new SpawnTemplatesPacket(session, module, epoch, entries);
    }
}

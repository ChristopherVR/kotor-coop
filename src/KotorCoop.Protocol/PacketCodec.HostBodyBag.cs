namespace KotorCoop.Protocol;

// Shared corpse loot: one host body bag's contents. Do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, HostBodyBagPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.HostBodyBag);
        w.WriteUInt32(packet.HostSessionId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteSingle(packet.X);
        w.WriteSingle(packet.Y);

        int count = Math.Min(packet.Items.Count, HostBodyBagPacket.MaxItems);
        w.WriteUInt8((byte)count);
        for (int i = 0; i < count; i++)
        {
            w.WriteString(packet.Items[i].Resref, ProtocolConstants.MaxResrefLength);
            w.WriteUInt16(packet.Items[i].Stack);
        }

        return w.Failed ? 0 : w.Position;
    }

    public static HostBodyBagPacket? ReadHostBodyBag(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.HostBodyBag)
        {
            return null;
        }

        uint session = r.ReadUInt32();
        string module = r.ReadString();
        float x = r.ReadSingle();
        float y = r.ReadSingle();
        byte count = r.ReadUInt8();
        if (r.Failed || count > HostBodyBagPacket.MaxItems || !float.IsFinite(x) || !float.IsFinite(y))
        {
            return null;
        }

        List<BodyBagItemEntry> items = new(count);
        for (int i = 0; i < count; i++)
        {
            string resref = r.ReadString();
            ushort stack = r.ReadUInt16();
            if (r.Failed || resref.Length > ProtocolConstants.MaxResrefLength)
            {
                return null;
            }

            items.Add(new BodyBagItemEntry(resref, stack));
        }

        return new HostBodyBagPacket(session, module, x, y, items);
    }
}

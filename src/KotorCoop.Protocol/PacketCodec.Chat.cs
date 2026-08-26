namespace KotorCoop.Protocol;

// The player text-chat relay.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, ChatPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Chat);
        w.WriteUInt32(packet.FromPeerId);
        w.WriteName(packet.Name);
        w.WriteString(packet.Text);

        return w.Failed ? 0 : w.Position;
    }

    public static ChatPacket? ReadChat(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Chat)
        {
            return null;
        }

        uint fromPeerId = r.ReadUInt32();
        string name = r.ReadString();
        string text = r.ReadString();

        return r.Failed || text.Length == 0 ? null : new ChatPacket(fromPeerId, name, text);
    }
}

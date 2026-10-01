namespace KotorCoop.Protocol;

// The host-start conversation relay. Byte-level encoding is asserted by PacketCodecTests; do
// not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, HostConversationRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.HostConversationRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.RequestSerial);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteString(packet.Tag, HostConversationRequestPacket.MaxTagLength);
        w.WriteString(packet.Resref, HostConversationRequestPacket.MaxResrefLength);
        return w.Failed ? 0 : w.Position;
    }

    public static HostConversationRequestPacket? ReadHostConversationRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.HostConversationRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint requestSerial = r.ReadUInt32();
        string module = r.ReadString();
        string tag = r.ReadString();
        string resref = r.ReadString();

        if (r.Failed || requestSerial == 0 || tag.Length == 0 && resref.Length == 0)
        {
            return null;
        }

        return new HostConversationRequestPacket(peerId, requestSerial, module, tag, resref);
    }
}

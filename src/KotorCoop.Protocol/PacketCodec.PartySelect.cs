namespace KotorCoop.Protocol;

// The party-select relay: a client's own suppressed CSWGuiPartySelection screen, relayed to the
// host as a request, and the host's answer(s). Byte-level encoding is asserted by
// PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, PartySelectRequestPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartySelectRequest);
        w.WriteUInt32(packet.PeerId);
        w.WriteUInt32(packet.RequestSerial);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteString(packet.ExitScript, PartySelectRequestPacket.MaxScriptLength);
        w.WriteInt32(packet.ForceNpc1);
        w.WriteInt32(packet.ForceNpc2);
        w.WriteUInt32(packet.TriggerId);
        return w.Failed ? 0 : w.Position;
    }

    public static PartySelectRequestPacket? ReadPartySelectRequest(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartySelectRequest)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        uint requestSerial = r.ReadUInt32();
        string module = r.ReadString();
        string exitScript = r.ReadString();
        int forceNpc1 = r.ReadInt32();
        int forceNpc2 = r.ReadInt32();
        uint triggerId = r.ReadUInt32();

        if (r.Failed || requestSerial == 0)
        {
            return null;
        }

        return new PartySelectRequestPacket(
            peerId, requestSerial, module, exitScript, forceNpc1, forceNpc2, triggerId);
    }

    public static int Write(Span<byte> buffer, PartySelectResponsePacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.PartySelectResponse);
        w.WriteUInt32(packet.RequestSerial);
        w.WriteUInt32(packet.ResponseSerial);
        w.WriteUInt8((byte)packet.Status);
        w.WriteString(packet.Detail, PartySelectResponsePacket.MaxDetailLength);
        return w.Failed ? 0 : w.Position;
    }

    public static PartySelectResponsePacket? ReadPartySelectResponse(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.PartySelectResponse)
        {
            return null;
        }

        uint requestSerial = r.ReadUInt32();
        uint responseSerial = r.ReadUInt32();
        byte status = r.ReadUInt8();
        string detail = r.ReadString();

        if (r.Failed || requestSerial == 0 || status > (byte)PartySelectResponseStatus.AlreadyPending)
        {
            return null;
        }

        return new PartySelectResponsePacket(requestSerial, responseSerial, (PartySelectResponseStatus)status, detail);
    }
}

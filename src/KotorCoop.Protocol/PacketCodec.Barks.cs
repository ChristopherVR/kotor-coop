namespace KotorCoop.Protocol;

// One-line NPC barks, which never touch the dialogue system.
// Byte-level encoding is asserted by PacketCodecTests; do not reorder fields.
public static partial class PacketCodec
{
    public static int Write(Span<byte> buffer, BarkPacket packet)
    {
        PacketWriter w = new(buffer);
        w.WriteHeader(PacketType.Bark);
        w.WriteUInt32(packet.PeerId);
        w.WriteString(packet.Module, ProtocolConstants.MaxModuleLength);
        w.WriteUInt32(packet.Speaker);
        w.WriteUInt32(packet.Flags);
        w.WriteUInt32(packet.Serial);

        // ⚠ RAW BYTES, NOT WriteString. The line comes out of the game's own TLK in the
        // engine's 8-bit encoding; WriteString is UTF-8, so an accented character would
        // expand and a 200-byte line could overrun its single length byte and be truncated
        // mid-sentence. The resref is a fixed CResRef char[16] and is not a string at all.
        byte[] text = packet.Text ?? [];
        int length = Math.Min(text.Length, BarkPacket.MaxTextBytes);
        w.WriteUInt8((byte)length);
        w.WriteBytes(text.AsSpan(0, length));

        byte[] sound = packet.SoundResRef ?? [];
        byte[] resRef = new byte[BarkPacket.ResRefBytes];
        sound.AsSpan(0, Math.Min(sound.Length, BarkPacket.ResRefBytes)).CopyTo(resRef);
        w.WriteBytes(resRef);

        return w.Failed ? 0 : w.Position;
    }

    public static BarkPacket? ReadBark(ReadOnlySpan<byte> buffer)
    {
        PacketReader r = new(buffer);
        if (r.ReadHeader() != PacketType.Bark)
        {
            return null;
        }

        uint peerId = r.ReadUInt32();
        string module = r.ReadString();
        uint speaker = r.ReadUInt32();
        uint flags = r.ReadUInt32();
        uint serial = r.ReadUInt32();

        int length = r.ReadUInt8();
        if (length > BarkPacket.MaxTextBytes)
        {
            return null; // a length this side never writes: treat the datagram as malformed
        }

        byte[] text = new byte[length];
        r.ReadBytes(text);

        byte[] resRef = new byte[BarkPacket.ResRefBytes];
        r.ReadBytes(resRef);

        return r.Failed ? null : new BarkPacket(peerId, module, speaker, text, resRef, flags, serial);
    }
}

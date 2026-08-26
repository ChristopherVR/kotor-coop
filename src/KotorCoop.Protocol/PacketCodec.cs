namespace KotorCoop.Protocol;

/// <summary>
/// Hand-written binary serialisation. Explicit rather than reflective so the wire format
/// is auditable and stable, and so a corrupt datagram can never do more than fail a read.
/// </summary>
public static partial class PacketCodec
{
    /// <summary>Peeks the type without consuming the body. Returns None for foreign traffic.</summary>
    public static PacketType PeekType(ReadOnlySpan<byte> buffer)
    {
        PacketReader reader = new(buffer);
        PacketType type = reader.ReadHeader();
        return reader.Failed ? PacketType.None : type;
    }

    /// <summary>
    /// Reads magic and type WITHOUT rejecting on a version mismatch, so a peer running a
    /// different co-op version can be answered with a typed Reject instead of silence. The
    /// version is returned separately; <paramref name="version"/> differing from
    /// <see cref="ProtocolConstants.Version"/> is exactly the case the strict
    /// <see cref="PeekType"/> hides. Still returns <see cref="PacketType.None"/> for
    /// non-ours traffic (wrong magic).
    /// </summary>
    public static PacketType PeekTypeAnyVersion(ReadOnlySpan<byte> buffer, out byte version)
    {
        PacketReader reader = new(buffer);
        ushort magic = reader.ReadUInt16();
        version = reader.ReadUInt8();
        byte type = reader.ReadUInt8();

        if (reader.Failed || magic != ProtocolConstants.Magic)
        {
            return PacketType.None;
        }

        return Enum.IsDefined((PacketType)type) ? (PacketType)type : PacketType.None;
    }
}

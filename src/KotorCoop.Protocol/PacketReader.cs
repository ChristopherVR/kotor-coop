using System.Buffers.Binary;
using System.Text;

namespace KotorCoop.Protocol;

/// <summary>
/// Little-endian cursor over received bytes. Reading past the end sets
/// <see cref="Failed"/> and yields defaults; callers check <see cref="Failed"/> once at
/// the end rather than after every field.
/// </summary>
public ref struct PacketReader
{
    private readonly ReadOnlySpan<byte> _buffer;
    private int _position;

    public PacketReader(ReadOnlySpan<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
        Failed = false;
    }

    public bool Failed { get; private set; }

    public readonly int Remaining => _buffer.Length - _position;

    private ReadOnlySpan<byte> Take(int count)
    {
        if (Failed || _position + count > _buffer.Length)
        {
            Failed = true;
            return default;
        }

        ReadOnlySpan<byte> slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    /// <summary>
    /// Validates magic and version and returns the packet type.
    /// Returns <see cref="PacketType.None"/> for anything that is not ours.
    /// </summary>
    public PacketType ReadHeader()
    {
        ushort magic = ReadUInt16();
        byte version = ReadUInt8();
        byte type = ReadUInt8();

        if (Failed || magic != ProtocolConstants.Magic || version != ProtocolConstants.Version)
        {
            Failed = true;
            return PacketType.None;
        }

        return Enum.IsDefined((PacketType)type) ? (PacketType)type : PacketType.None;
    }

    /// <summary>Raw bytes into the caller's buffer, no length prefix — the counterpart of
    /// <c>PacketWriter.WriteBytes</c> for fixed-size opaque payloads.</summary>
    public void ReadBytes(Span<byte> destination)
    {
        ReadOnlySpan<byte> slice = Take(destination.Length);
        if (!Failed)
        {
            slice.CopyTo(destination);
        }
    }

    public byte ReadUInt8()
    {
        ReadOnlySpan<byte> slice = Take(1);
        return Failed ? (byte)0 : slice[0];
    }

    public bool ReadBool() => ReadUInt8() != 0;

    public ushort ReadUInt16()
    {
        ReadOnlySpan<byte> slice = Take(2);
        return Failed ? (ushort)0 : BinaryPrimitives.ReadUInt16LittleEndian(slice);
    }

    public uint ReadUInt32()
    {
        ReadOnlySpan<byte> slice = Take(4);
        return Failed ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(slice);
    }

    public int ReadInt32()
    {
        ReadOnlySpan<byte> slice = Take(4);
        return Failed ? 0 : BinaryPrimitives.ReadInt32LittleEndian(slice);
    }

    public long ReadInt64()
    {
        ReadOnlySpan<byte> slice = Take(8);
        return Failed ? 0L : BinaryPrimitives.ReadInt64LittleEndian(slice);
    }

    public float ReadSingle()
    {
        ReadOnlySpan<byte> slice = Take(4);
        return Failed ? 0f : BinaryPrimitives.ReadSingleLittleEndian(slice);
    }

    public Vector3f ReadVector3()
    {
        float x = ReadSingle();
        float y = ReadSingle();
        float z = ReadSingle();
        return new Vector3f(x, y, z);
    }

    /// <summary>
    /// Reads a length-prefixed UTF-8 string. <paramref name="maxLength"/> must match the cap
    /// the writer used for this field: a field with its own larger budget (the dialogue
    /// spectate line) passes it here too, or a legitimate long value would be rejected as
    /// malformed and take the whole packet down with it.
    /// </summary>
    public string ReadString(int maxLength = ProtocolConstants.MaxStringLength)
    {
        byte length = ReadUInt8();
        if (Failed)
        {
            return string.Empty;
        }

        if (length > maxLength)
        {
            Failed = true;
            return string.Empty;
        }

        ReadOnlySpan<byte> slice = Take(length);
        return Failed ? string.Empty : Encoding.UTF8.GetString(slice);
    }

    public PlayerState ReadPlayerState()
    {
        Vector3f position = ReadVector3();
        float facing = ReadSingle();
        string module = ReadString();
        int health = ReadInt32();
        uint animation = ReadUInt32();
        uint appearance = ReadUInt32();
        int maxHealth = ReadInt32();
        string body = ReadString();
        string rightWeapon = ReadString();
        string leftWeapon = ReadString();
        uint flags = ReadUInt32();
        ushort oneShot = ReadUInt16();
        byte oneShotSequence = ReadUInt8();
        return new PlayerState(
            position, facing, module, health, animation, appearance, maxHealth,
            body, rightWeapon, leftWeapon, flags, oneShot, oneShotSequence);
    }
}

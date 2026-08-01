using System.Buffers.Binary;
using System.Text;

namespace KotorCoop.Protocol;

/// <summary>
/// Little-endian cursor over a caller-supplied buffer. Overruns set <see cref="Failed"/>
/// rather than throwing, so a malformed or oversized packet is a dropped packet.
/// </summary>
public ref struct PacketWriter
{
    private readonly Span<byte> _buffer;
    private int _position;

    public PacketWriter(Span<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
        Failed = false;
    }

    public bool Failed { get; private set; }

    public int Position => _position;

    public readonly ReadOnlySpan<byte> Written => _buffer[.._position];

    private Span<byte> Take(int count)
    {
        if (Failed || _position + count > _buffer.Length)
        {
            Failed = true;
            return default;
        }

        Span<byte> slice = _buffer.Slice(_position, count);
        _position += count;
        return slice;
    }

    public void WriteHeader(PacketType type)
    {
        WriteUInt16(ProtocolConstants.Magic);
        WriteUInt8(ProtocolConstants.Version);
        WriteUInt8((byte)type);
    }

    public void WriteUInt8(byte value)
    {
        Span<byte> slice = Take(1);
        if (!Failed)
        {
            slice[0] = value;
        }
    }

    public void WriteBool(bool value) => WriteUInt8(value ? (byte)1 : (byte)0);

    public void WriteUInt16(ushort value)
    {
        Span<byte> slice = Take(2);
        if (!Failed)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(slice, value);
        }
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> slice = Take(4);
        if (!Failed)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(slice, value);
        }
    }

    /// <summary>Raw bytes, no length prefix — for fixed-size opaque payloads whose length
    /// both sides already agree on.</summary>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        Span<byte> slice = Take(data.Length);
        if (!Failed)
        {
            data.CopyTo(slice);
        }
    }

    public void WriteInt32(int value)
    {
        Span<byte> slice = Take(4);
        if (!Failed)
        {
            BinaryPrimitives.WriteInt32LittleEndian(slice, value);
        }
    }

    public void WriteInt64(long value)
    {
        Span<byte> slice = Take(8);
        if (!Failed)
        {
            BinaryPrimitives.WriteInt64LittleEndian(slice, value);
        }
    }

    public void WriteSingle(float value)
    {
        Span<byte> slice = Take(4);
        if (!Failed)
        {
            BinaryPrimitives.WriteSingleLittleEndian(slice, value);
        }
    }

    public void WriteVector3(Vector3f value)
    {
        WriteSingle(value.X);
        WriteSingle(value.Y);
        WriteSingle(value.Z);
    }

    /// <summary>
    /// Writes a length-prefixed UTF-8 string, truncated to <paramref name="maxLength"/>.
    ///
    /// The cap is whatever the caller asks for, bounded only by the single length byte —
    /// it is NOT clamped to <see cref="ProtocolConstants.MaxStringLength"/>. That constant
    /// is the default for the many short identity fields (names, resrefs, tags) whose
    /// combined worst case has to fit one snapshot datagram; a field with its own budget,
    /// like the dialogue spectate line, states its own larger cap and is respected.
    /// </summary>
    public void WriteString(string? value, int maxLength = ProtocolConstants.MaxStringLength)
    {
        string text = value ?? string.Empty;
        int limit = Math.Min(maxLength, ProtocolConstants.MaxLengthPrefixedString);
        int byteCount = Encoding.UTF8.GetByteCount(text);

        while (byteCount > limit && text.Length > 0)
        {
            text = text[..^1];
            byteCount = Encoding.UTF8.GetByteCount(text);
        }

        WriteUInt8((byte)byteCount);
        Span<byte> slice = Take(byteCount);
        if (!Failed && byteCount > 0)
        {
            Encoding.UTF8.GetBytes(text, slice);
        }
    }

    public void WritePlayerState(in PlayerState state)
    {
        WriteVector3(state.Position);
        WriteSingle(state.Facing);
        WriteString(state.Module, ProtocolConstants.MaxModuleLength);
        WriteInt32(state.Health);
        WriteUInt32(state.Animation);
        WriteUInt32(state.Appearance);
        WriteInt32(state.MaxHealth);

        // Visible equipment as blueprint resrefs, so the peer's phantom can wear the
        // real gear. Empty string = slot empty (or unreadable, which must look the same).
        WriteString(state.EquippedBody, ProtocolConstants.MaxResrefLength);
        WriteString(state.EquippedRightWeapon, ProtocolConstants.MaxResrefLength);
        WriteString(state.EquippedLeftWeapon, ProtocolConstants.MaxResrefLength);
        WriteUInt32(state.Flags);

        // The one-shot animation edge: id plus a sequence, so a repeated identical action
        // still replays rather than being swallowed as "unchanged".
        WriteUInt16(state.OneShotAnimation);
        WriteUInt8(state.OneShotSequence);
    }

    public void WriteName(string? value) => WriteString(value, ProtocolConstants.MaxNameLength);
}

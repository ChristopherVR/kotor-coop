namespace KotorCoop.Protocol;

/// <summary>Where the retail game selected the save before dispatching its native load.</summary>
public enum LoadProvenanceSource : uint
{
    None = 0,
    MainMenuEntry = 1,
    ServerLoadRequest = 2,
}

/// <summary>Result of the bounded, read-only native argument capture.</summary>
public enum LoadProvenanceStatus : uint
{
    None = 0,
    Captured = 1,
    InvalidEntry = 2,
    InvalidSaveName = 3,
    SaveNameTooLong = 4,
}

/// <summary>
/// Exact save request observed before the native load routine runs. The save name is the
/// raw bounded byte sequence from the engine's CExoString; it is not a content hash or an
/// acknowledgement that loading completed.
/// </summary>
public sealed record LoadProvenance(
    uint Generation,
    int Slot,
    LoadProvenanceSource Source,
    LoadProvenanceStatus Status,
    uint WorldLoadSerialAtRequest,
    byte[] SaveNameBytes)
{
    public string SaveName => System.Text.Encoding.Latin1.GetString(SaveNameBytes);
}

/// <summary>Pure decoder for the v74 seqlock mailbox; payload begins after generation.</summary>
public static class LoadProvenanceCodec
{
    public const int NameCapacity = 64;
    public const int PayloadLength = 20 + NameCapacity;

    /// <summary>Validates the native CExoString convention used by the supported build
    /// (stored length includes exactly one final NUL) and returns the bounded name bytes.</summary>
    public static bool TryStripCExoTerminator(
        ReadOnlySpan<byte> stored,
        out ReadOnlySpan<byte> content)
    {
        content = default;
        if (stored.Length is < 2 or > NameCapacity + 1 || stored[^1] != 0)
        {
            return false;
        }

        ReadOnlySpan<byte> candidate = stored[..^1];
        if (candidate.Contains((byte)0)) return false;
        content = candidate;
        return true;
    }

    public static bool TryDecode(
        uint generationBefore,
        ReadOnlySpan<byte> payload,
        uint generationAfter,
        out LoadProvenance? record)
    {
        record = null;
        if (generationBefore == 0 || (generationBefore & 1) != 0
            || generationBefore != generationAfter || payload.Length < PayloadLength)
        {
            return false;
        }

        int slot = ReadInt32(payload, 0);
        var source = (LoadProvenanceSource)ReadUInt32(payload, 4);
        var status = (LoadProvenanceStatus)ReadUInt32(payload, 8);
        uint worldLoadSerial = ReadUInt32(payload, 12);
        uint nameLength = ReadUInt32(payload, 16);

        if (source is not (LoadProvenanceSource.MainMenuEntry or LoadProvenanceSource.ServerLoadRequest)
            || status is not (LoadProvenanceStatus.Captured
                or LoadProvenanceStatus.InvalidEntry
                or LoadProvenanceStatus.InvalidSaveName
                or LoadProvenanceStatus.SaveNameTooLong)
            || nameLength > NameCapacity)
        {
            return false;
        }

        if (status == LoadProvenanceStatus.Captured)
        {
            if (slot < -1 || nameLength == 0)
            {
                return false;
            }

            ReadOnlySpan<byte> bytes = payload.Slice(20, (int)nameLength);
            if (bytes.Contains((byte)0))
            {
                return false;
            }

            record = new LoadProvenance(
                generationBefore, slot, source, status, worldLoadSerial, bytes.ToArray());
            return true;
        }

        if (nameLength != 0)
        {
            return false;
        }

        record = new LoadProvenance(
            generationBefore, slot, source, status, worldLoadSerial, []);
        return true;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));

    private static int ReadInt32(ReadOnlySpan<byte> data, int offset) =>
        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
}

/// <summary>Advances the mailbox's nonzero even generation, reserving the intervening odd
/// value as the in-progress marker. Wrap skips zero so zero always means never published.</summary>
public static class LoadProvenanceGeneration
{
    public static uint Next(uint currentEven, out uint inProgressOdd)
    {
        uint odd = unchecked(currentEven + 1);
        if ((odd & 1) == 0) odd = unchecked(odd + 1);
        uint published = unchecked(odd + 1);
        if (published == 0) published = 2;
        inProgressOdd = odd;
        return published;
    }
}

/// <summary>Encoder for the tiny x86 entry stubs used by the v74 observers. Kept pure so
/// the exact bytes, argument stack offsets, and relative branches are testable offline.</summary>
public static class LoadProvenanceStubBuilder
{
    public const int MainMenuLength = 27;
    public const int ServerLength = 31;
    private static ReadOnlySpan<byte> RelocatedPrologue => [0x55, 0x8B, 0xEC, 0x83, 0xE4, 0xF8];

    public static int Write(Span<byte> destination, uint stubAddress, uint callbackAddress, uint resumeAddress, bool server)
    {
        int required = server ? ServerLength : MainMenuLength;
        if (destination.Length < required) throw new ArgumentException("stub buffer too small", nameof(destination));
        int i = 0;
        destination[i++] = 0x9C; // pushfd
        destination[i++] = 0x60; // pushad
        if (server)
        {
            // After pushfd/pushad, original [esp+8] (name) is +0x2C. Following
            // its push, original [esp+4] (slot) is also +0x2C.
            destination[i++] = 0xFF; destination[i++] = 0x74; destination[i++] = 0x24; destination[i++] = 0x2C;
            destination[i++] = 0xFF; destination[i++] = 0x74; destination[i++] = 0x24; destination[i++] = 0x2C;
            destination[i++] = 0xE8;
            WriteRelative(destination, ref i, stubAddress, callbackAddress);
            destination[i++] = 0x83; destination[i++] = 0xC4; destination[i++] = 0x08;
        }
        else
        {
            // Main-menu thiscall's sole stack argument is the selected entry at +0x28.
            destination[i++] = 0xFF; destination[i++] = 0x74; destination[i++] = 0x24; destination[i++] = 0x28;
            destination[i++] = 0xE8;
            WriteRelative(destination, ref i, stubAddress, callbackAddress);
            destination[i++] = 0x83; destination[i++] = 0xC4; destination[i++] = 0x04;
        }

        destination[i++] = 0x61; // popad
        destination[i++] = 0x9D; // popfd
        RelocatedPrologue.CopyTo(destination[i..]);
        i += RelocatedPrologue.Length;
        destination[i++] = 0xE9;
        WriteRelative(destination, ref i, stubAddress, resumeAddress);
        return i;
    }

    private static void WriteRelative(Span<byte> destination, ref int offset, uint stubAddress, uint target)
    {
        uint nextInstruction = unchecked(stubAddress + (uint)offset + 4);
        int displacement = unchecked((int)(target - nextInstruction));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(destination.Slice(offset, 4), displacement);
        offset += 4;
    }
}

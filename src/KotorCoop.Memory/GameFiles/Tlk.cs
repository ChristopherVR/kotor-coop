using System.Buffers.Binary;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// BioWare's talk table (<c>dialog.tlk</c>) — the game's string store, which every
/// player-visible label in the data files points into by StrRef.
///
/// Layout, per the reference reader:
///
///   header (20 bytes):
///     char[4] "TLK ", char[4] "V3.0"
///     uint32 languageId, stringCount, stringEntriesOffset
///   entry (40, one per string, starting at 20):
///     uint32 flags, char[16] soundResRef, uint32 volumeVariance, pitchVariance,
///     uint32 offsetToString, stringSize, float soundLength
///
/// Bit 0 of the flags is TEXT_PRESENT; an entry without it has no string however plausible
/// its offset looks. Strings live at <c>stringEntriesOffset + offsetToString</c> and are
/// NOT NUL-terminated — the length is the entry's, which is the trap the CExoString rule
/// in this codebase already warns about from the other direction.
///
/// Read lazily and cached by the caller: dialog.tlk is ~10 MB and a browser label needs a
/// handful of strings out of it.
/// </summary>
public sealed class Tlk
{
    private const int HeaderSize = 20;
    private const int EntrySize = 40;
    private const uint TextPresent = 1;

    private readonly byte[] _data;
    private readonly uint _stringCount;
    private readonly uint _entriesOffset;

    public int Count => (int)_stringCount;

    private Tlk(byte[] data)
    {
        _data = data;

        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException($"TLK: {data.Length} bytes is too short for the header");
        }

        if (GameFileText.Decode(data.AsSpan(0, 4)) != "TLK ")
        {
            throw new InvalidDataException("TLK: wrong magic");
        }

        _stringCount = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12, 4));
        _entriesOffset = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16, 4));
    }

    // Not File.ReadAllBytes: swkotor.exe can hold dialog.tlk open with write access it
    // never uses while it is the game running, which File.ReadAllBytes's default
    // FileShare.Read cannot share against. See GameFileIo.ReadAllBytes.
    public static Tlk Open(string path) => new(GameFileIo.ReadAllBytes(path));

    public static Tlk Parse(byte[] data) => new(data);

    /// <summary>
    /// The string for <paramref name="strRef"/>, or "" for a reference this table does not
    /// have. Out-of-range and text-absent entries are ordinary cases — a -1 StrRef means "no
    /// string" throughout the game's data — so neither throws.
    /// </summary>
    public string Get(int strRef)
    {
        if (strRef < 0 || strRef >= _stringCount)
        {
            return string.Empty;
        }

        long entry = HeaderSize + ((long)EntrySize * strRef);
        if (entry + EntrySize > _data.Length)
        {
            return string.Empty;
        }

        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)entry, 4));
        if ((flags & TextPresent) == 0)
        {
            return string.Empty;
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)entry + 28, 4));
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan((int)entry + 32, 4));

        long at = _entriesOffset + offset;
        if (at < 0 || size > int.MaxValue || at + size > _data.Length)
        {
            return string.Empty;
        }

        return GameFileText.Decode(_data.AsSpan((int)at, (int)size));
    }
}

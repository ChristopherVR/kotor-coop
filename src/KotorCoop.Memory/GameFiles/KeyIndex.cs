using System.Buffers.Binary;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// chitin.key, 'KEY V1  ' — the global index over every BIF in the install.
///
///   0x00 char[4] "KEY ", 0x04 char[4] "V1  "
///   0x08 uint32 bifCount
///   0x0C uint32 keyCount
///   0x10 uint32 offsetToFileTable
///   0x14 uint32 offsetToKeyTable
///   0x18 uint32 buildYear, 0x1C uint32 buildDay, 0x20 32 reserved bytes
/// file entry (12 bytes): uint32 fileSize, uint32 filenameOffset, uint16 filenameSize, uint16 drives
/// key  entry (22 bytes): char[16] resref, uint16 resType, uint32 resId
///     resId &gt;&gt; 20     = index into the BIF file table
///     resId &amp; 0xFFFFF = index of the resource inside that BIF
/// </summary>
internal sealed class KeyIndex
{
    private readonly string _gameDirectory;
    private readonly List<string> _bifNames = [];
    private readonly Dictionary<(string Resref, int Type), (int BifIndex, int ResourceIndex)> _entries = [];

    /// <summary>Parsed BIF tables by index. A BIF's table is a few hundred KB at most and
    /// resolving one resource per lookup would re-read it constantly without this.</summary>
    private readonly Dictionary<int, BifArchive> _bifCache = [];

    public string Path { get; }

    public int EntryCount => _entries.Count;

    /// <summary>Parses chitin.key. Throws <see cref="InvalidDataException"/> on a wrong
    /// magic or truncation — the caller (GameFiles) turns that into IsUsable=false.</summary>
    public static KeyIndex Load(string path) => new(path);

    private KeyIndex(string path)
    {
        Path = path;
        _gameDirectory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!;

        // Not File.ReadAllBytes: it opens with FileShare.Read, which is a sharing
        // violation against the handle swkotor.exe holds on chitin.key while the game the
        // agent is fingerprinting is the one running. See GameFileIo.ReadAllBytes.
        byte[] data = GameFileIo.ReadAllBytes(path);

        if (data.Length < 0x20 || GameFileText.Decode(data.AsSpan(0, 4)) != "KEY ")
        {
            throw new InvalidDataException($"{path}: not a KEY file");
        }

        uint bifCount = ReadU32(data, 8);
        uint keyCount = ReadU32(data, 12);
        uint filesOffset = ReadU32(data, 16);
        uint keysOffset = ReadU32(data, 20);

        for (uint i = 0; i < bifCount; i++)
        {
            uint at = filesOffset + (12 * i);
            uint nameOffset = ReadU32(data, at + 4);
            ushort nameLength = ReadU16(data, at + 8);

            if (nameOffset + nameLength > data.Length)
            {
                throw new InvalidDataException($"{path}: BIF name {i} runs past the file");
            }

            // Stored as "data\\2da.bif"; normalised so path joins work on any separator.
            _bifNames.Add(GameFileText
                .CString(data.AsSpan((int)nameOffset, nameLength))
                .Replace('\\', System.IO.Path.DirectorySeparatorChar)
                .Replace('/', System.IO.Path.DirectorySeparatorChar));
        }

        for (uint i = 0; i < keyCount; i++)
        {
            uint at = keysOffset + (22 * i);

            if (at + 22 > data.Length)
            {
                throw new InvalidDataException($"{path}: key entry {i} runs past the file");
            }

            string resref = GameFileText.CString(data.AsSpan((int)at, 16));
            ushort resourceType = ReadU16(data, at + 16);
            uint resourceId = ReadU32(data, at + 18);

            // Later duplicates overwrite earlier ones, matching the reference's dict
            // assignment (and, observably, the engine's own behaviour).
            _entries[(resref.ToLowerInvariant(), resourceType)] =
                ((int)(resourceId >> 20), (int)(resourceId & 0xFFFFF));
        }
    }

    /// <summary>Resolves and reads one resource out of its BIF. False when the name is
    /// unknown, the extension is unknown, or the BIF is missing/corrupt — a Try method
    /// never throws for "not there".</summary>
    public bool TryRead(string resref, string extension, out byte[] data)
    {
        data = [];

        if (!ResourceTypes.TryGetType(extension, out int type)
            || !_entries.TryGetValue((resref.ToLowerInvariant(), type), out (int BifIndex, int ResourceIndex) hit))
        {
            return false;
        }

        try
        {
            BifArchive bif = BifAt(hit.BifIndex);
            return bif.TryRead(hit.ResourceIndex, out data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    private BifArchive BifAt(int index)
    {
        if (_bifCache.TryGetValue(index, out BifArchive? cached))
        {
            return cached;
        }

        if (index < 0 || index >= _bifNames.Count)
        {
            throw new InvalidDataException($"{Path}: key entry points at BIF {index}, table has {_bifNames.Count}");
        }

        BifArchive bif = new(System.IO.Path.Combine(_gameDirectory, _bifNames[index]));
        _bifCache[index] = bif;
        return bif;
    }

    private static uint ReadU32(byte[] data, uint offset) =>
        offset + 4 <= data.Length
            ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)offset, 4))
            : throw new InvalidDataException($"KEY: truncated at 0x{offset:X}");

    private static ushort ReadU16(byte[] data, uint offset) =>
        offset + 2 <= data.Length
            ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan((int)offset, 2))
            : throw new InvalidDataException($"KEY: truncated at 0x{offset:X}");
}

/// <summary>
/// One BIF data file, 'BIFFV1  '.
///
///   0x00 char[4] "BIFF", 0x04 char[4] "V1  "
///   0x08 uint32 variableResourceCount
///   0x0C uint32 fixedResourceCount
///   0x10 uint32 variableTableOffset
/// variable entry (16 bytes): uint32 id, uint32 offset, uint32 size, uint32 type
///
/// The low 20 bits of the entry id are the index within the BIF — the same number the
/// KEY stores in its resId, which is how the two files join.
/// </summary>
internal sealed class BifArchive
{
    private readonly string _path;
    private readonly Dictionary<int, (uint Offset, uint Size)> _byIndex = [];

    public BifArchive(string path)
    {
        _path = path;

        byte[] header = GameFileIo.ReadSlice(path, 0, 20);

        if (GameFileText.Decode(header.AsSpan(0, 4)) != "BIFF")
        {
            throw new InvalidDataException($"{path}: not a BIF");
        }

        uint variableCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        uint tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16, 4));

        byte[] table = GameFileIo.ReadSlice(path, tableOffset, checked(16 * variableCount));

        for (int i = 0; i < variableCount; i++)
        {
            ReadOnlySpan<byte> entry = table.AsSpan(16 * i, 16);
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(entry[..4]);

            _byIndex[(int)(id & 0xFFFFF)] = (
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(8, 4)));
        }
    }

    public bool TryRead(int resourceIndex, out byte[] data)
    {
        if (!_byIndex.TryGetValue(resourceIndex, out (uint Offset, uint Size) slot))
        {
            data = [];
            return false;
        }

        data = GameFileIo.ReadSlice(_path, slot.Offset, slot.Size);
        return true;
    }
}

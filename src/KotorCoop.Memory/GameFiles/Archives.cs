using System.Buffers.Binary;

namespace KotorCoop.Memory.GameFiles;

/// <summary>One entry in a RIM or ERF archive: where a resource's bytes live.</summary>
public sealed record ArchiveResource(string Resref, int ResourceType, uint Offset, uint Size)
{
    public string Extension => ResourceTypes.NameFor(ResourceType);

    public string Name => $"{Resref}.{Extension}";
}

/// <summary>
/// Shared machinery for the two module-archive formats. The entry table is parsed once
/// up front; resource bytes are read from disk on demand, because a module RIM can be
/// tens of megabytes and a caller usually wants one .git out of it.
/// </summary>
public abstract class GameArchive
{
    private readonly Dictionary<(string Resref, int Type), ArchiveResource> _index = [];
    private readonly List<ArchiveResource> _resources = [];

    /// <summary>The archive file on disk. Reads reopen it per call, so a stale handle can
    /// never block the game from patching or deleting its own files.</summary>
    public string Path { get; }

    public IReadOnlyList<ArchiveResource> Resources => _resources;

    private protected GameArchive(string path) => Path = path;

    private protected void AddResource(ArchiveResource resource)
    {
        _resources.Add(resource);
        _index[(resource.Resref.ToLowerInvariant(), resource.ResourceType)] = resource;
    }

    public ArchiveResource? Find(string resref, string extension) =>
        ResourceTypes.TryGetType(extension, out int type)
            && _index.TryGetValue((resref.ToLowerInvariant(), type), out ArchiveResource? hit)
            ? hit
            : null;

    /// <summary>Reads one member's bytes. False when the member is absent or the file has
    /// become unreadable underneath us (the game holds these files while running).</summary>
    public bool TryRead(string resref, string extension, out byte[] data)
    {
        ArchiveResource? resource = Find(resref, extension);

        if (resource is null)
        {
            data = [];
            return false;
        }

        try
        {
            data = GameFileIo.ReadSlice(Path, resource.Offset, resource.Size);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            data = [];
            return false;
        }
    }
}

/// <summary>
/// 'RIM V1.0' — modules/*.rim and rims/*.rim.
///
///   0x00 char[4] "RIM ", 0x04 char[4] "V1.0"
///   0x08 uint32 reserved
///   0x0C uint32 file count
///   0x10 uint32 file table offset
///   0x14 uint32 isExtension flag (1 for *_s.rim)
///   0x18..0x78 reserved
/// entry (32 bytes): char[16] resref, uint32 type, uint32 index, uint32 offset, uint32 size
/// </summary>
public sealed class RimArchive : GameArchive
{
    /// <summary>True for the *_s.rim companion archives that extend a base module.</summary>
    public bool IsExtension { get; }

    public static RimArchive Open(string path) => new(path);

    private RimArchive(string path)
        : base(path)
    {
        byte[] header = GameFileIo.ReadSlice(path, 0, 0x78);

        if (GameFileText.Decode(header.AsSpan(0, 4)) != "RIM ")
        {
            throw new InvalidDataException($"{path}: not a RIM (magic '{GameFileText.Decode(header.AsSpan(0, 4))}')");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x0C, 4));
        uint tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x10, 4));
        IsExtension = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x14, 4)) != 0;

        byte[] table = GameFileIo.ReadSlice(path, tableOffset, checked(32 * count));

        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = table.AsSpan(32 * i, 32);
            AddResource(new ArchiveResource(
                GameFileText.CString(entry[..16]),
                (int)BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(16, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(24, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(entry.Slice(28, 4))));
        }
    }
}

/// <summary>
/// 'ERF V1.0' / 'MOD V1.0' / 'SAV V1.0' / 'HAK V1.0' — patch.erf, *.mod, savegames.
///
///   0x00 char[4] type, 0x04 char[4] version
///   0x08 uint32 languageCount, 0x0C uint32 localizedStringSize
///   0x10 uint32 entryCount
///   0x14 uint32 offsetToLocalizedString
///   0x18 uint32 offsetToKeyList, 0x1C uint32 offsetToResourceList
///   0x20 uint32 buildYear (since 1900), 0x24 uint32 buildDay
///   0x28 uint32 descriptionStrRef, then 116 reserved bytes (header = 160)
/// key entry      (24 bytes): char[16] resref, uint32 resId, uint16 resType, uint16 unused
/// resource entry (8 bytes):  uint32 offset, uint32 size
/// </summary>
public sealed class ErfArchive : GameArchive
{
    /// <summary>The container flavour: "ERF ", "MOD ", "SAV " or "HAK ".</summary>
    public string FileType { get; }

    public int BuildYear { get; }

    public int BuildDay { get; }

    public static ErfArchive Open(string path) => new(path);

    private ErfArchive(string path)
        : base(path)
    {
        byte[] header = GameFileIo.ReadSlice(path, 0, 160);

        FileType = GameFileText.Decode(header.AsSpan(0, 4));
        string version = GameFileText.Decode(header.AsSpan(4, 4));

        if (version.Trim() != "V1.0")
        {
            throw new InvalidDataException($"{path}: unsupported ERF version '{version}'");
        }

        uint entryCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x10, 4));
        uint keysOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x18, 4));
        uint valuesOffset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x1C, 4));
        BuildYear = 1900 + (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x20, 4));
        BuildDay = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x24, 4));

        byte[] keys = GameFileIo.ReadSlice(path, keysOffset, checked(24 * entryCount));
        byte[] values = GameFileIo.ReadSlice(path, valuesOffset, checked(8 * entryCount));

        for (int i = 0; i < entryCount; i++)
        {
            ReadOnlySpan<byte> key = keys.AsSpan(24 * i, 24);
            ReadOnlySpan<byte> value = values.AsSpan(8 * i, 8);

            AddResource(new ArchiveResource(
                GameFileText.CString(key[..16]),
                BinaryPrimitives.ReadUInt16LittleEndian(key.Slice(20, 2)),
                BinaryPrimitives.ReadUInt32LittleEndian(value[..4]),
                BinaryPrimitives.ReadUInt32LittleEndian(value.Slice(4, 4))));
        }
    }

    /// <summary>
    /// Finds one member of an ERF held IN MEMORY rather than on disk. A save's module state
    /// is a nested container — <c>SAVEGAME.sav</c> holds <c>&lt;module&gt;.sav</c>, which
    /// holds <c>module.ifo</c> and the GIT — and the nested one is a byte range of its
    /// parent, never a file of its own, so the path-based reader above cannot open it.
    ///
    /// <paramref name="resref"/> null matches the first member of the type. Bounds-checked
    /// throughout: a truncated or hostile save reads as "absent" rather than throwing.
    /// </summary>
    public static bool TryFindMember(byte[] erf, int resourceType, string? resref, out byte[] data)
    {
        ArgumentNullException.ThrowIfNull(erf);
        data = [];

        if (erf.Length < 160)
        {
            return false;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan(0x10, 4));
        uint keysOffset = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan(0x18, 4));
        uint valuesOffset = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan(0x1C, 4));

        // The cap is a runaway guard on a corrupt count, not a format limit; no real save
        // member list comes near it.
        for (uint i = 0; i < count && i < 4096; i++)
        {
            long key = keysOffset + (24L * i);
            long value = valuesOffset + (8L * i);
            if (key + 24 > erf.Length || value + 8 > erf.Length)
            {
                return false;
            }

            int type = erf[key + 20] | (erf[key + 21] << 8);
            if (type != resourceType)
            {
                continue;
            }

            string name = GameFileText.CString(erf.AsSpan((int)key, 16));
            if (resref is not null && !string.Equals(name, resref, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan((int)value, 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(erf.AsSpan((int)value + 4, 4));
            if ((long)offset + size > erf.Length)
            {
                return false;
            }

            data = erf.AsSpan((int)offset, (int)size).ToArray();
            return true;
        }

        return false;
    }
}

/// <summary>Shared "read a byte range out of a file" used by every container reader.</summary>
internal static class GameFileIo
{
    /// <summary>
    /// Reads exactly <paramref name="size"/> bytes at <paramref name="offset"/>.
    /// FileShare.ReadWrite because the running game keeps these files open; a reader
    /// demanding exclusivity would fail the moment swkotor.exe is up — which is exactly
    /// when the co-op agent wants to read them.
    /// </summary>
    public static byte[] ReadSlice(string path, long offset, uint size)
    {
        using FileStream stream = new(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        if (offset < 0 || offset + size > stream.Length)
        {
            throw new InvalidDataException(
                $"{path}: {size} bytes at 0x{offset:X} runs past the {stream.Length}-byte file");
        }

        byte[] data = new byte[size];
        stream.Position = offset;
        stream.ReadExactly(data);
        return data;
    }

    /// <summary>
    /// Whole-file counterpart to <see cref="ReadSlice"/>, same FileShare.ReadWrite reason:
    /// <c>File.ReadAllBytes</c> opens with the BCL's default FileShare.Read, which is a
    /// sharing violation against a handle swkotor.exe holds with write access even though
    /// it never writes through it — chitin.key, dialog.tlk and a modded Override\*.2da are
    /// exactly the files a live agent reads while the game that has them open is the one
    /// being fingerprinted. A test process that never opens the game's own handle cannot
    /// see this: it is a live-only failure mode, which is why every caller here must go
    /// through this helper instead of the BCL convenience method.
    /// </summary>
    public static byte[] ReadAllBytes(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }
}

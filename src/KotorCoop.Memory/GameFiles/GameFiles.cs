using System.Buffers.Binary;

namespace KotorCoop.Memory.GameFiles;

/// <summary>
/// Read-only access to a KOTOR install's data files, resolving resources the way the
/// engine does: a loose file in override\ wins over the same resref in the BIFs. That
/// precedence is the whole point — it is what lets the digest work detect that two
/// installs disagree because one of them has a modded appearance.2da in override.
///
/// Never writes anything, and never throws from the constructor: a plugin-less or odd
/// install (no chitin.key, no override) degrades to <see cref="IsUsable"/> = false so
/// the caller can carry on without file intelligence rather than crash.
/// </summary>
public sealed class GameFiles : IDisposable
{
    private readonly string _gameDirectory;
    private readonly string? _overrideDirectory;
    private readonly string? _modulesDirectory;
    private readonly string? _tlkPath;
    private readonly KeyIndex? _key;

    /// <summary>False when chitin.key is missing or unparseable. Override reads and the
    /// censuses still work in that state; only BIF resolution is gone.</summary>
    public bool IsUsable => _key is not null;

    /// <summary>The install this instance reads, exactly as given to the constructor.</summary>
    public string GameDirectory => _gameDirectory;

    public GameFiles(string gameDirectory)
    {
        _gameDirectory = gameDirectory;

        // All lookups are case-insensitive: GOG ships "Override", Steam era files vary,
        // and a resref's case is never significant to the engine.
        _overrideDirectory = FindDirectory(gameDirectory, "override");
        _modulesDirectory = FindDirectory(gameDirectory, "modules");
        _tlkPath = FindFile(gameDirectory, "dialog.tlk");

        string? keyPath = FindFile(gameDirectory, "chitin.key");

        if (keyPath is not null)
        {
            try
            {
                _key = KeyIndex.Load(keyPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A corrupt or locked key is the same situation as a missing one:
                // degrade, do not crash the host.
                _key = null;
            }
        }
    }

    // -- resource resolution -----------------------------------------------------

    /// <summary>
    /// Reads "resref.extension", override directory first, then chitin.key/BIF — the
    /// engine's own precedence, so what this returns is what the game actually loads.
    /// </summary>
    public bool TryReadResource(string resref, string extension, out byte[] data)
    {
        data = [];

        if (resref.Length == 0)
        {
            return false;
        }

        string? loose = OverridePath(resref, extension);

        if (loose is not null)
        {
            try
            {
                // Not File.ReadAllBytes: the running game can hold this same loose file
                // open (it resolves it too, override-first, same as us), and its default
                // FileShare.Read would then be a sharing violation. See GameFileIo.
                data = GameFileIo.ReadAllBytes(loose);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // An unreadable override file falls through to the BIF copy, which is
                // also roughly what the engine does when a loose file open fails.
            }
        }

        return _key is not null && _key.TryRead(resref, extension, out data);
    }

    /// <summary>True when a loose override file would shadow this resource.</summary>
    public bool OverrideHasFile(string resref, string extension) =>
        OverridePath(resref, extension) is not null;

    private string? OverridePath(string resref, string extension)
    {
        if (_overrideDirectory is null)
        {
            return null;
        }

        // File.Exists is case-insensitive on Windows, which is the only place a KOTOR
        // install lives; the directory itself was already located case-insensitively.
        string path = Path.Combine(_overrideDirectory, $"{resref}.{extension.TrimStart('.')}");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The install's modules folder as it is spelled on disk, or null when there is
    /// none.</summary>
    public string? ModulesDirectory => _modulesDirectory;

    /// <summary>
    /// Reads one member out of a module archive (<c>modules\&lt;name&gt;.rim</c>, or a
    /// <c>.mod</c> of that name when there is no rim): the way to get a module's own
    /// <c>.are</c>/<c>.git</c>/<c>module.ifo</c>, which live nowhere else. Read-only; false
    /// when the module or the member is absent or the archive cannot be read.
    /// </summary>
    public bool TryReadModuleMember(string moduleName, string resref, string extension, out byte[] data)
    {
        data = [];

        if (_modulesDirectory is null)
        {
            return false;
        }

        foreach (string suffix in new[] { ".rim", ".mod" })
        {
            string? path = FindFile(_modulesDirectory, moduleName + suffix);
            if (path is null)
            {
                continue;
            }

            try
            {
                GameArchive archive = suffix == ".rim" ? RimArchive.Open(path) : ErfArchive.Open(path);
                if (archive.TryRead(resref, extension, out data))
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A locked or damaged archive reads as "not there"; the caller reports it.
            }
        }

        return false;
    }

    // -- censuses ----------------------------------------------------------------

    /// <summary>Every loose file in override\ as (lowercase name, size), sorted by name.
    /// This is digest input: two installs whose censuses differ are not running the same
    /// content, whatever their versions claim.</summary>
    public IReadOnlyList<(string Name, long Size)> OverrideCensus() => Census(_overrideDirectory);

    /// <summary>Same census for modules\ — the module set differs between installs when
    /// one side has a mod that adds or replaces areas.</summary>
    public IReadOnlyList<(string Name, long Size)> ModulesCensus() => Census(_modulesDirectory);

    private static IReadOnlyList<(string Name, long Size)> Census(string? directory)
    {
        if (directory is null)
        {
            return [];
        }

        List<(string Name, long Size)> files = [];

        try
        {
            foreach (string path in Directory.EnumerateFiles(directory))
            {
                files.Add((Path.GetFileName(path).ToLowerInvariant(), new FileInfo(path).Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A directory that vanishes or denies mid-enumeration yields a partial
            // census; the digest comparison will simply flag the mismatch.
        }

        // Ordinal sort so both peers produce the identical sequence regardless of
        // culture settings — this list is hashed, and hash inputs must be canonical.
        files.Sort(static (a, b) => string.CompareOrdinal(a.Name, b.Name));
        return files;
    }

    // -- dialog.tlk ---------------------------------------------------------------

    /// <summary>
    /// dialog.tlk's header: 'TLK V3.0', uint32 languageId at 0x08, uint32 stringCount at
    /// 0x0C. The entry count plus total size is enough to fingerprint the talk table —
    /// two differing dialog.tlk files mean different text ids, without parsing 49k
    /// entries to prove it.
    /// </summary>
    public bool TryReadTlkHeader(out int entryCount, out long fileSize)
    {
        entryCount = 0;
        fileSize = 0;

        if (_tlkPath is null)
        {
            return false;
        }

        try
        {
            using FileStream stream = new(_tlkPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            byte[] header = new byte[16];
            if (stream.Length < header.Length)
            {
                return false;
            }

            stream.ReadExactly(header);

            if (GameFileText.Decode(header.AsSpan(0, 4)) != "TLK ")
            {
                return false;
            }

            entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            fileSize = stream.Length;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // -- case-insensitive lookups -------------------------------------------------

    private static string? FindDirectory(string parent, string name)
    {
        try
        {
            if (!Directory.Exists(parent))
            {
                return null;
            }

            foreach (string path in Directory.EnumerateDirectories(parent))
            {
                if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static string? FindFile(string parent, string name)
    {
        try
        {
            if (!Directory.Exists(parent))
            {
                return null;
            }

            foreach (string path in Directory.EnumerateFiles(parent))
            {
                if (string.Equals(Path.GetFileName(path), name, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return null;
    }

    /// <summary>Nothing here holds a file handle between calls — every read opens and
    /// closes its own stream — so disposal is a formality kept for the surface contract
    /// and for any future caching that does pin a handle.</summary>
    public void Dispose()
    {
    }
}

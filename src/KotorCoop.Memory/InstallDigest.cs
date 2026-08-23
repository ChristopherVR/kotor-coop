using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

/// <summary>
/// Fingerprints a KOTOR install into a <see cref="ContentDigest"/> for the co-op
/// mod-compatibility handshake. Each field is an FNV-1a-32 hash (or a plain count); a
/// field that cannot be computed clears its bit in the known mask, so a filesystem quirk
/// on one side downgrades to a warning rather than refusing a session.
///
/// Computed once at agent start and carried in Hello/Welcome. Read-only throughout.
/// </summary>
public static class InstallDigest
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>
    /// Builds the digest from a game directory and its executable. Any field whose source
    /// is missing or unreadable is simply left unknown — the whole thing degrades to
    /// <see cref="ContentDigest.Unknown"/> in the worst case, which compares as "unchecked".
    /// </summary>
    public static ContentDigest Compute(string gameDirectory, string executablePath)
    {
        ContentDigest.Field known = ContentDigest.Field.None;
        uint exe = 0, overrideCensus = 0, twoDa = 0, appearance = 0, tlk = 0, journal = 0, modules = 0;
        ushort overrideCount = 0, appearanceRows = 0;
        uint tlkEntryCount = 0;

        if (TryHashExecutable(executablePath, out exe))
        {
            known |= ContentDigest.Field.Exe;
        }

        GameFiles.GameFiles? files = null;
        try
        {
            files = new GameFiles.GameFiles(gameDirectory);

            // Every file-derived field is gated on a real install (chitin.key present).
            // Without it, an empty census would otherwise be reported as a KNOWN empty
            // value, which is indistinguishable from "no game here" and would compare as a
            // false match. Better to know nothing than to claim an empty install.
            if (files.IsUsable)
            {
                if (TryHashCensus(files.OverrideCensus(), out overrideCensus, out overrideCount))
                {
                    known |= ContentDigest.Field.Override;
                }

                if (TryHashCensus(files.ModulesCensus(), out modules, out _))
                {
                    known |= ContentDigest.Field.Modules;
                }

                if (TryHashOverride2das(gameDirectory, files, out twoDa))
                {
                    known |= ContentDigest.Field.TwoDa;
                }
            }

            if (files.IsUsable && TryHashAppearance(files, out appearance, out appearanceRows))
            {
                known |= ContentDigest.Field.Appearance;
            }

            if (files.TryReadTlkHeader(out int tlkEntries, out long tlkSize))
            {
                tlk = HashTlk(gameDirectory, tlkEntries, tlkSize);
                tlkEntryCount = (uint)tlkEntries;
                known |= ContentDigest.Field.Tlk;
            }

            if (files.IsUsable && TryHashJournal(files, out journal))
            {
                known |= ContentDigest.Field.Journal;
            }
        }
        catch
        {
            // A digest is a best effort. Whatever was gathered before the failure stands;
            // the rest stays unknown and only warns.
        }
        finally
        {
            files?.Dispose();
        }

        return new ContentDigest(
            (ushort)known, exe, overrideCensus, overrideCount, twoDa, appearance, appearanceRows,
            tlk, tlkEntryCount, journal, modules);
    }

    /// <summary>
    /// Size and the two ends of the executable — enough to catch a different build, an
    /// exe-patching mod (4 GB LAA, widescreen) or a different localisation, cheaply. A full
    /// hash of a 5 MB file every start is unnecessary when only "same or not" matters.
    /// </summary>
    private static bool TryHashExecutable(string path, out uint digest)
    {
        digest = 0;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return false;
            }

            using FileStream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            long length = info.Length;
            int window = (int)Math.Min(64 * 1024, length);
            byte[] head = new byte[window];
            byte[] tail = new byte[window];

            stream.ReadExactly(head, 0, window);
            stream.Seek(-window, SeekOrigin.End);
            stream.ReadExactly(tail, 0, window);

            digest = (uint)length ^ Fnv(head) ^ Fnv(tail);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryHashCensus(
        IReadOnlyList<(string Name, long Size)> census, out uint digest, out ushort count)
    {
        uint h = FnvOffset;
        foreach ((string name, long size) in census)
        {
            h = FnvString(h, name);
            h = FnvUInt(h, (uint)size);
        }

        digest = h;
        count = (ushort)Math.Min(census.Count, ushort.MaxValue);
        return true;
    }

    /// <summary>
    /// Hashes the full content of every <c>Override\*.2da</c>, since a 2DA edit is applied
    /// in place by TSLPatcher and its content — not just its presence — is what differs.
    /// The two 2DAs that would actually corrupt co-op (appearance, and the globals
    /// catalogue) have their own dedicated fields; this is the broad "some table differs"
    /// signal.
    /// </summary>
    private static bool TryHashOverride2das(string gameDirectory, GameFiles.GameFiles files, out uint digest)
    {
        digest = FnvOffset;

        try
        {
            string? overrideDir = FindDirectory(gameDirectory, "override");
            if (overrideDir is null)
            {
                return true; // no override folder is a valid, empty answer
            }

            foreach (string path in Directory.EnumerateFiles(overrideDir, "*.2da").OrderBy(p =>
                Path.GetFileName(p).ToLowerInvariant(), StringComparer.Ordinal))
            {
                digest = FnvString(digest, Path.GetFileName(path).ToLowerInvariant());

                // Not File.ReadAllBytes: while swkotor.exe is running (exactly when this
                // digest is computed) it can hold an Override\*.2da file open with write
                // access it never uses, and File.ReadAllBytes's default FileShare.Read is
                // a sharing violation against that. See GameFileIo.ReadAllBytes.
                digest = Fnv(digest, GameFileIo.ReadAllBytes(path));
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Hashes each appearance.2da row's index and label. A reordered or resized table
    /// paints the wrong body from a replicated row index, or indexes off the end (a crash),
    /// so this — and the row count — is what the appearance channel is gated on.
    /// </summary>
    private static bool TryHashAppearance(GameFiles.GameFiles files, out uint digest, out ushort rows)
    {
        digest = 0;
        rows = 0;

        if (!files.TryReadResource("appearance", "2da", out byte[] data))
        {
            return false;
        }

        TwoDa table = TwoDa.Parse(data);
        uint h = FnvOffset;
        for (int row = 0; row < table.RowCount; row++)
        {
            h = FnvUInt(h, (uint)row);
            h = FnvString(h, table.Get(row, "label"));
        }

        digest = h;
        rows = (ushort)Math.Min(table.RowCount, ushort.MaxValue);
        return true;
    }

    /// <summary>
    /// Hashes the sorted quest tags in global.jrl. A tag one install lacks would be
    /// appended as a phantom journal entry, so the journal channel filters to shared tags
    /// when this differs. The journal is indexed under an odd restype in the retail build
    /// (utw, not jrl), so both are tried.
    /// </summary>
    private static bool TryHashJournal(GameFiles.GameFiles files, out uint digest)
    {
        digest = 0;

        if (!files.TryReadResource("global", "jrl", out byte[] data)
            && !files.TryReadResource("global", "utw", out data))
        {
            return false;
        }

        Gff gff = Gff.Parse(data);
        List<string> tags = [];
        foreach (GffStruct category in gff.Root.GetList("Categories"))
        {
            if (category.TryGetString("Tag", out string tag) && tag.Length > 0)
            {
                tags.Add(tag.ToLowerInvariant());
            }
        }

        tags.Sort(StringComparer.Ordinal);

        uint h = FnvOffset;
        foreach (string tag in tags)
        {
            h = FnvString(h, tag);
        }

        digest = h;
        return true;
    }

    private static uint HashTlk(string gameDirectory, int entries, long size)
    {
        uint h = (uint)entries ^ (uint)size;

        try
        {
            string? tlk = FindFile(gameDirectory, "dialog.tlk");
            if (tlk is not null)
            {
                using FileStream stream = File.Open(tlk, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                int window = (int)Math.Min(4096, size);
                byte[] head = new byte[window];
                stream.ReadExactly(head, 0, window);
                h ^= Fnv(head);
            }
        }
        catch
        {
            // The header numbers alone still distinguish most TLK differences.
        }

        return h;
    }

    // ---- FNV-1a helpers, the house hash ---------------------------------------

    private static uint Fnv(ReadOnlySpan<byte> data) => Fnv(FnvOffset, data);

    private static uint Fnv(uint seed, ReadOnlySpan<byte> data)
    {
        uint h = seed;
        foreach (byte b in data)
        {
            h = (h ^ b) * FnvPrime;
        }

        return h;
    }

    private static uint FnvString(uint seed, string value)
    {
        uint h = seed;
        foreach (char c in value)
        {
            h = (h ^ (byte)c) * FnvPrime;
        }

        return h * FnvPrime;
    }

    private static uint FnvUInt(uint seed, uint value)
    {
        uint h = seed;
        for (int i = 0; i < 4; i++)
        {
            h = (h ^ (byte)(value >> (i * 8))) * FnvPrime;
        }

        return h;
    }

    private static string? FindDirectory(string root, string name)
    {
        try
        {
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(d => string.Equals(Path.GetFileName(d), name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static string? FindFile(string root, string name)
    {
        try
        {
            string direct = Path.Combine(root, name);
            if (File.Exists(direct))
            {
                return direct;
            }

            return Directory.EnumerateFiles(root)
                .FirstOrDefault(f => string.Equals(Path.GetFileName(f), name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }
}

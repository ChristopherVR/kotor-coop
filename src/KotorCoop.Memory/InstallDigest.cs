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
    ///
    /// <paramref name="liveImageDigest"/> is the plugin's hash of the LOADED executable image
    /// (<see cref="KotorGameBridge.TryReadExeImageDigest"/>), and it — not the file — is what
    /// <see cref="ContentDigest.ExeDigest"/> carries. See the field's own note below for why
    /// the file hash cannot be the answer and cannot be the fallback either.
    /// </summary>
    public static ContentDigest Compute(
        string gameDirectory, string executablePath, uint? liveImageDigest = null)
    {
        ContentDigest.Field known = ContentDigest.Field.None;
        uint exe = 0, overrideCensus = 0, twoDa = 0, appearance = 0, tlk = 0, journal = 0, modules = 0;
        ushort overrideCount = 0, appearanceRows = 0;
        uint tlkEntryCount = 0;
        uint gameplay = 0;

        // ⚠ THE EXE FIELD IS THE RUNNING IMAGE, NOT THE FILE — and there is deliberately no
        // fallback to the file hash. Steam ships the same link as the DRM-free build wrapped
        // in SteamStub v3, which encrypts .text on disk and decrypts it in place before the
        // game's entry point runs: the two FILES hash differently, the two running images are
        // byte-identical, and ContentCompatibility refuses on RefusalReason.Executable over
        // nothing (docs/research/steam-support.md §8). Falling back to the file hash when the
        // image one is missing would be WORSE than leaving the field unknown, because then a
        // peer carrying an image hash and a peer carrying a file hash would compare two
        // different KINDS of number and refuse every time. Unknown warns; it never lies.
        if (liveImageDigest is uint image)
        {
            exe = image;
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
                ModContentManifest manifest = ModContentManifest.Compute(gameDirectory);
                if (manifest.Complete)
                {
                    overrideCensus = manifest.Digest(file => file.Path.StartsWith("override/", StringComparison.Ordinal));
                    overrideCount = (ushort)Math.Min(files.OverrideCensus().Count, ushort.MaxValue);
                    known |= ContentDigest.Field.Override;
                    modules = manifest.Digest(file => file.Path.StartsWith("modules/", StringComparison.Ordinal));
                    known |= ContentDigest.Field.Modules;
                    gameplay = manifest.Digest(file => file.Gameplay);
                    known |= ContentDigest.Field.Gameplay;
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

        string kmrp = liveImageDigest.HasValue ? KmrpInstallIdentity.Compute(gameDirectory, executablePath) : "";
        return new ContentDigest(
            (ushort)known, exe, overrideCensus, overrideCount, twoDa, appearance, appearanceRows,
            tlk, tlkEntryCount, journal, modules, gameplay,
            kmrp.Length == 0 ? null : kmrp);
    }

    /// <summary>
    /// Size and the two ends of the executable file. NO LONGER PART OF THE HANDSHAKE — it
    /// says "different build" about a Steam install and a DRM-free one whose running code is
    /// identical (see <see cref="Compute"/>). Kept because it is still a useful LABEL: the
    /// agent prints it beside the image digest, and it is the number that tells a Steam
    /// install from an unwrapped one at a glance in a log.
    /// </summary>
    public static bool TryHashExecutableFile(string path, out uint digest)
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

using System.Security.Cryptography;
using KotorCoop.Protocol;
using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>Full file hashes for install-time mod checks. Never runs on the gameplay pump.</summary>
public sealed record ModContentManifest(IReadOnlyList<ModContentFile> Files, IReadOnlyList<string> Errors)
{
    public bool Complete => Errors.Count == 0;
    private static readonly HashSet<string> GameplayExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".2da", ".utc", ".uti", ".utd", ".utp", ".utm", ".utt", ".ute", ".utw",
        ".dlg", ".ncs", ".git", ".are", ".ifo", ".jrl", ".gff", ".fac",
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".bik", ".avi", ".mve", ".mpg", ".mp4", ".wmv", ".wav", ".mp3", ".wma", ".ogg", ".flac", ".lip", ".mod",
    };

    public static ModContentManifest Compute(string gameDirectory)
    {
        var files = new List<ModContentFile>();
        var errors = new List<string>();
        var hashedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fileSnapshots = new List<(string Path, long Size, long Modified)>();
        var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(gameDirectory))
            return new([], [$"game directory does not exist: {gameDirectory}"]);
        foreach (string folder in new[] { "override", "modules" })
        {
            try
            {
                string? directory = Directory.EnumerateDirectories(gameDirectory)
                    .FirstOrDefault(path => Path.GetFileName(path).Equals(folder, StringComparison.OrdinalIgnoreCase));
                if (directory is null) continue;
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    string extension = Path.GetExtension(path);
                    if (extension.Equals(".log", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".sav", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                        || extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (folder == "modules" && extension.ToLowerInvariant() is not (".mod" or ".rim" or ".erf")) continue;
                    // The PvP arena module is generated here from this player's own tar_m02ae
                    // files (docs/research/pvp-arenas.md). Whether a peer HAS it is negotiated by
                    // the arena handshake (PvpOp.AwareArena), and a modded cantina already shows
                    // up as its own module file, so counting the generated file as well would only
                    // raise "module files differ" over an install that simply has not run setup.
                    if (folder == "modules" && Path.GetFileName(path).Equals("k_coop_arena.mod", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = $"{folder}/{Path.GetFileName(path).ToLowerInvariant()}";
                    bool gameplay = folder == "modules" || GameplayExtensions.Contains(extension) || MediaExtensions.Contains(extension);
                    HashFile(path, name, gameplay);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{folder}: {ex.Message}");
            }
        }
        // The KEY is the engine's authoritative archive list. Hash the index and full BIF
        // bytes so a same-size script edit in a base archive cannot look like vanilla.
        string? key = null;
        try
        {
            key = Directory.EnumerateFiles(gameDirectory)
                .FirstOrDefault(path => Path.GetFileName(path).Equals("chitin.key", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add($"chitin.key: {ex.Message}");
        }
        if (key is not null)
        {
            HashFile(key, "chitin.key", true);
            try
            {
                foreach (string relative in KeyIndex.Load(key).BifNames)
                {
                    string path = Path.GetFullPath(Path.Combine(gameDirectory, relative));
                    string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory)) + Path.DirectorySeparatorChar;
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"archive path escapes game directory: {relative}");
                    HashFile(path, Path.GetRelativePath(gameDirectory, path).Replace('\\', '/').ToLowerInvariant(), true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
            {
                errors.Add($"chitin.key: {ex.Message}");
            }
        }
        // Media is chosen by scripts and conversations, often from nested voice folders.
        // Hash the complete engine media surface instead of guessing which scripts run.
        foreach (string folder in new[] { "movies", "streammusic", "streamsounds", "streamwaves", "streamvoice", "lips" })
        {
            try
            {
                string? directory = Directory.EnumerateDirectories(gameDirectory)
                    .FirstOrDefault(path => Path.GetFileName(path).Equals(folder, StringComparison.OrdinalIgnoreCase));
                if (directory is not null) WalkMedia(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{folder}: {ex.Message}");
            }
        }
        string profile = Path.Combine(gameDirectory, ModSupportProfile.FileName);
        if (File.Exists(profile)) HashFile(profile, ModSupportProfile.FileName, true);
        if (ModSupportProfile.TryLoad(gameDirectory, out var catalog, out string profileError))
        {
            foreach (string path in catalog.ActiveFiles)
            {
                string relative = Path.GetRelativePath(gameDirectory, path).Replace('\\', '/').ToLowerInvariant();
                if (relative != ModSupportProfile.FileName) HashFile(path, relative, true);
            }
        }
        else errors.Add(profileError);
        // Detect an installer changing a file after it was hashed while later archives stream.
        foreach (var snapshot in fileSnapshots)
        {
            try
            {
                var info = new FileInfo(snapshot.Path);
                if (!info.Exists || info.Length != snapshot.Size || info.LastWriteTimeUtc.Ticks != snapshot.Modified)
                    errors.Add($"{snapshot.Path}: file changed during audit; retry after mod installation finishes");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{snapshot.Path}: {ex.Message}");
            }
        }
        files.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return new(files, errors);

        void WalkMedia(string directory)
        {
            Stack<string> pending = new();
            pending.Push(directory);
            while (pending.TryPop(out string? current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException($"linked media directory cannot be audited: {current}");
                foreach (string path in Directory.EnumerateFiles(current))
                    if (MediaExtensions.Contains(Path.GetExtension(path)))
                        HashFile(path, Path.GetRelativePath(gameDirectory, path).Replace('\\', '/').ToLowerInvariant(), true);
                foreach (string child in Directory.EnumerateDirectories(current)) pending.Push(child);
            }
        }

        void HashFile(string path, string name, bool gameplay)
        {
            if (!hashedPaths.Add(name)) return;
            try
            {
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectory));
                string? part = Path.GetFullPath(path);
                bool isFile = true;
                while (part is not null && !part.Equals(root, StringComparison.OrdinalIgnoreCase))
                {
                    if (!isFile && checkedDirectories.Contains(part)) break;
                    if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("linked content path cannot be audited");
                    if (!isFile) checkedDirectories.Add(part);
                    part = Path.GetDirectoryName(part);
                    isFile = false;
                }
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 128 * 1024, FileOptions.SequentialScan);
                long size = stream.Length;
                long modified = File.GetLastWriteTimeUtc(path).Ticks;
                string sha = Convert.ToHexString(SHA256.HashData(stream));
                if (stream.Length != size || File.GetLastWriteTimeUtc(path).Ticks != modified)
                    throw new IOException("file changed during hashing; retry after mod installation finishes");
                files.Add(new(name, size, sha, gameplay));
                fileSnapshots.Add((path, size, modified));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add($"{name}: {ex.Message}");
            }
        }
    }

    /// <summary>A compact canonical fingerprint of a category, using full SHA-256 file
    /// hashes as inputs. The wire still uses its established FNV-1a 32-bit digest shape.</summary>
    public uint Digest(Func<ModContentFile, bool> include)
    {
        uint value = 2166136261;
        foreach (ModContentFile file in Files.Where(include))
            foreach (char c in $"{file.Path}\0{file.Sha256}\0") value = (value ^ (byte)c) * 16777619;
        return value;
    }

    public IReadOnlyList<string> Differences(ModContentManifest other)
    {
        var a = Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var b = other.Files.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        return a.Keys.Union(b.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)
            .Where(path => !a.TryGetValue(path, out var left) || !b.TryGetValue(path, out var right)
                || left.Sha256 != right.Sha256).ToArray();
    }
}

public sealed record ModContentFile(string Path, long Size, string Sha256, bool Gameplay);

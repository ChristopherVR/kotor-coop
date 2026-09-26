using System.Security.Cryptography;
using KotorCoop.Protocol;

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

    public static ModContentManifest Compute(string gameDirectory)
    {
        var files = new List<ModContentFile>();
        var errors = new List<string>();
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
                    string name = $"{folder}/{Path.GetFileName(path).ToLowerInvariant()}";
                    bool gameplay = folder == "modules" || GameplayExtensions.Contains(Path.GetExtension(path));
                    HashFile(path, name, gameplay);
                }
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
        files.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return new(files, errors);

        void HashFile(string path, string name, bool gameplay)
        {
            try
            {
                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long size = stream.Length;
                long modified = File.GetLastWriteTimeUtc(path).Ticks;
                string sha = Convert.ToHexString(SHA256.HashData(stream));
                if (stream.Length != size || File.GetLastWriteTimeUtc(path).Ticks != modified)
                    throw new IOException("file changed during hashing; retry after mod installation finishes");
                files.Add(new(name, size, sha, gameplay));
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

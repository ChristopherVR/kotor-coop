using System.Text.Json;

namespace KotorCoop.Agent;

/// <summary>
/// Remembers where <see cref="PackageInstaller.FindGameDirectories"/> found a KOTOR install
/// last time, so a later run of `setup`/`install` can skip the expensive part (every fixed
/// drive, the registry, every Steam library - a few seconds on a machine with several drives)
/// instead of repeating it on every launch.
///
/// Kept per-user under LocalApplicationData rather than beside the exe: a player runs setup
/// from wherever they last extracted the zip, which changes every upgrade, while the actual
/// game installs on their machine do not.
///
/// Never trusted blindly. <see cref="TryLoad"/> drops any entry whose directory no longer
/// looks like a game install (moved, uninstalled) and re-derives the executable form and the
/// installed kotor-coop version fresh (both single small file reads, not a directory scan) -
/// so what a cache hit reports is never stale on either of those two facts, only skips the
/// expensive part of finding the directory in the first place.
/// </summary>
internal static class InstallScanCache
{
    /// <summary>One remembered directory: enough to re-derive a full <see cref="PackageInstaller.Candidate"/>
    /// on load without repeating the scan that found it.</summary>
    public sealed record Entry(string Directory, string RawSource, bool Supported);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "kotor-coop", "install-scan-cache.json");

    /// <summary>
    /// The cached candidates, re-verified and re-described fresh - or null when there is no
    /// cache, it is unreadable, or nothing in it still exists (all cases the caller should
    /// fall back to a full scan for).
    /// </summary>
    public static IReadOnlyList<PackageInstaller.Candidate>? TryLoad()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return null;
            }

            List<Entry>? entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(CachePath), JsonOptions);
            if (entries is null || entries.Count == 0)
            {
                return null;
            }

            List<PackageInstaller.Candidate> valid = [];
            foreach (Entry entry in entries)
            {
                if (PackageInstaller.IsGameDirectory(entry.Directory))
                {
                    valid.Add(PackageInstaller.DescribeCandidate(entry.Directory, entry.RawSource, entry.Supported));
                }
            }

            return valid.Count > 0 ? valid : null;
        }
        catch (Exception)
        {
            // Corrupt or unreadable cache file: treat exactly like no cache at all.
            return null;
        }
    }

    /// <summary>Best-effort write; a failed save just means the next run scans again.</summary>
    public static void Save(IReadOnlyList<Entry> entries)
    {
        try
        {
            string path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(entries, JsonOptions));
        }
        catch (Exception)
        {
        }
    }
}

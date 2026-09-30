using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;

namespace KotorCoop.Agent;

public static class ModsCommand
{
    public static int Run(AgentOptions options)
    {
        string directory = DirectoryFor(options.GamePath);
        ModContentManifest local = ModContentManifest.Compute(directory);
        Console.WriteLine($"mod audit: {directory}");
        Console.WriteLine($"{local.Files.Count} resource/archive/media/profile files; {local.Files.Count(file => file.Gameplay)} gameplay files; full SHA-256 hashes");
        using GameFiles files = new(directory);
        bool valid = local.Complete && files.IsUsable;
        if (!files.IsUsable) Console.WriteLine("missing or invalid chitin.key; game resources cannot be checked");
        foreach (string error in local.Errors) Console.WriteLine($"unchecked: {error}");
        if (!ModSupportProfile.TryLoad(directory, out var profile, out string profileError))
        {
            Console.WriteLine(profileError);
            valid = false;
        }
        else if (profile.Id.Length > 0)
        {
            Console.WriteLine($"recruitment profile: {profile.Id}; {profile.Companions.Count} companion rules");
            if (profile.HoldUnmappedSlots.Count > 0)
                Console.WriteLine($"unmapped template recovery held for slots: {string.Join(", ", profile.HoldUnmappedSlots)}");
            foreach (var companion in profile.Companions)
            {
                if (companion.Template.Length == 0)
                {
                    Console.WriteLine($"slot {companion.Slot}, {companion.Module}: template recovery held (script-managed identity)");
                    continue;
                }
                bool found = files.TryReadResource(companion.Template, "utc", out _)
                    || HasModuleCreature(directory, companion);
                string condition = companion.When is { } when ? $" when {when.Global}={when.Min}..{when.Max}" : "";
                Console.WriteLine($"slot {companion.Slot}, {companion.Module}{condition}: {companion.Template}.utc {(found ? "found" : "MISSING")}");
                valid &= found;
            }
        }
        else Console.WriteLine("no recruitment profile; stock companion recovery applies");

        foreach (var mod in profile.Mods)
            Console.WriteLine($"mod {mod.Id}: {(mod.Active ? "ACTIVE" : "not detected")}; {mod.Status}; {Path.GetFileName(mod.SourcePath)}");

        if (options.CompareGamePath is { } otherDirectory)
        {
            string comparisonDirectory = DirectoryFor(otherDirectory);
            ModContentManifest other = ModContentManifest.Compute(comparisonDirectory);
            using GameFiles otherFiles = new(comparisonDirectory);
            bool otherInstall = otherFiles.IsUsable;
            if (!otherInstall) Console.WriteLine("other install missing or invalid chitin.key");
            foreach (string error in other.Errors) Console.WriteLine($"other install unchecked: {error}");
            var differences = local.Differences(other);
            Console.WriteLine($"comparison: {differences.Count} differing or missing files");
            foreach (string path in differences.Take(40)) Console.WriteLine($"  {path}");
            if (differences.Count > 40) Console.WriteLine($"  ... {differences.Count - 40} more");
            valid &= otherInstall && other.Complete && differences.Count == 0;
        }
        Console.WriteLine("Campaign runtime compatibility requires a two-player playthrough check; matching files do not certify script or cutscene support.");
        return valid ? 0 : 1;
    }

    private static string DirectoryFor(string path) => Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
        ? Path.GetDirectoryName(Path.GetFullPath(path))! : Path.GetFullPath(path);

    private static bool HasModuleCreature(string directory, ModSupportProfile.Companion companion)
    {
        string modules = Path.Combine(directory, "modules");
        if (!Directory.Exists(modules)) return false;
        foreach (string path in Directory.EnumerateFiles(modules))
        {
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (extension is not (".mod" or ".rim")) continue;
            string module = Path.GetFileNameWithoutExtension(path);
            if (module.EndsWith("_s", StringComparison.OrdinalIgnoreCase)) module = module[..^2];
            if (!ModSupportProfile.Matches(companion.Module, module)) continue;
            try
            {
                GameArchive archive = extension == ".mod" ? ErfArchive.Open(path) : RimArchive.Open(path);
                if (archive.Find(companion.Template, "utc") is not null) return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // A missing/unreadable declaration is reported as MISSING above.
            }
        }
        return false;
    }
}

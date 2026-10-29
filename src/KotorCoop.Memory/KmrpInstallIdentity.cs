using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KotorCoop.Memory;

/// <summary>
/// Identifies KMRP's installed patch inputs independently of runtime allocation addresses.
/// This is an admission fallback, not proof that its hooks coexist with ours. Unsupported,
/// incomplete or unreadable layouts supply no fallback; the live image check still applies.
/// See docs/research/kmrp-compatibility.md for the inspected upstream layout.
/// </summary>
public static partial class KmrpInstallIdentity
{
    [GeneratedRegex("^id\\s*=\\s*\"([a-zA-Z0-9_-]+)\"\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PatchId();

    [GeneratedRegex("^dll\\s*=\\s*\"patches/([a-zA-Z0-9_-]+)\\.dll\"\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex PatchDll();

    public static string Compute(string gameDirectory, string executablePath)
    {
        try
        {
            string root = Path.GetFullPath(gameDirectory);
            if (!Path.GetFullPath(executablePath).Equals(Path.Combine(root, "swkotor.exe"), StringComparison.OrdinalIgnoreCase)) return "";
            string config = Path.Combine(root, "patch_config.toml");
            if (!File.Exists(config)) return "";

            // Restrict the fallback to the relative-path layout emitted by KMRP's installer.
            // Never assume an absolute/external module's bytes were covered by our census.
            string configText = File.ReadAllText(config);
            string[] blocks = configText.Split("[[patches]]", StringSplitOptions.None);
            if (blocks.Length < 2) return "";
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "KotorPatcher.dll", "binkw32.dll", "binkw32Hooked.dll", "patch_config.toml",
                "configs/kmrp.ini", "swkotor.exe",
            };
            bool kmrp = false;
            foreach (string block in blocks.Skip(1))
            {
                string? id = null, dll = null;
                foreach (string raw in block.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#')) continue;
                    if (line.StartsWith('[')) break;
                    Match match = PatchId().Match(line);
                    if (match.Success && id is null) { id = match.Groups[1].Value; continue; }
                    match = PatchDll().Match(line);
                    if (match.Success && dll is null) { dll = match.Groups[1].Value; continue; }
                    return "";
                }
                if (id is null || !string.Equals(id, dll, StringComparison.Ordinal)) return "";
                required.Add($"patches/{dll}.dll");
                if (id == "kmrp-controller") required.Add("configs/kmrp-controller.ini");
                kmrp |= id == "kmrp";
            }
            if (!kmrp) return "";

            var paths = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var directorySnapshots = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            Add(executablePath, "swkotor.exe");
            foreach (string relative in required) Add(Path.Combine(root, relative), relative.ToLowerInvariant());
            string[] rootFiles = RuntimeRootFiles();
            foreach (string path in rootFiles)
            {
                Add(path, Path.GetFileName(path).ToLowerInvariant());
            }
            foreach (string folder in new[] { "patches", "configs" }) Walk(Path.Combine(root, folder));
            string chain = Path.Combine(root, "dinput8.chain");
            string? chainText = null;
            if (File.Exists(chain))
            {
                chainText = File.ReadAllText(chain);
                foreach (string raw in chainText.Split('\n'))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
                    if (line != Path.GetFileName(line) || !line.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) return "";
                    Add(Path.Combine(root, line), line.ToLowerInvariant());
                }
                Add(chain, "dinput8.chain");
            }

            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            digest.AppendData(Encoding.UTF8.GetBytes("kotor-coop-kmrp-install-1\0"));
            // Keep every input open without write/delete sharing through the complete audit.
            // No size/mtime cache: same-size edits must change the identity too.
            var streams = new List<FileStream>();
            try
            {
                foreach (var (name, path) in paths)
                {
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    streams.Add(stream);
                    if (stream.Length == 0 && required.Contains(name)) return "";
                    digest.AppendData(Encoding.UTF8.GetBytes(name + "\0"));
                    digest.AppendData(SHA256.HashData(stream));
                }
                // The configuration used to select required modules must be the one hashed.
                if (File.ReadAllText(config) != configText) return "";
                if (chainText is not null && File.ReadAllText(chain) != chainText) return "";
                if (!rootFiles.SequenceEqual(RuntimeRootFiles(), StringComparer.OrdinalIgnoreCase)) return "";
                foreach (var (directory, entries) in directorySnapshots)
                    if (!entries.SequenceEqual(Directory.GetFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase),
                            StringComparer.OrdinalIgnoreCase)) return "";
                return Convert.ToHexString(digest.GetHashAndReset());
            }
            finally { foreach (FileStream stream in streams) stream.Dispose(); }

            void Add(string path, string name)
            {
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase)) throw new IOException("external runtime input");
                for (string? part = full; part is not null; part = Path.GetDirectoryName(part))
                {
                    if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                        throw new IOException("linked runtime input");
                    if (part.Equals(root, StringComparison.OrdinalIgnoreCase)) break;
                }
                paths[name] = full;
            }

            void Walk(string directory)
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("linked runtime directory");
                directorySnapshots[directory] = Directory.GetFileSystemEntries(directory).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                foreach (string path in Directory.EnumerateFiles(directory))
                    Add(path, Path.GetRelativePath(root, path).Replace('\\', '/').ToLowerInvariant());
                foreach (string child in Directory.EnumerateDirectories(directory)) Walk(child);
            }

            string[] RuntimeRootFiles() => Directory.EnumerateFiles(root)
                .Where(path => Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    || Path.GetExtension(path).Equals(".asi", StringComparison.OrdinalIgnoreCase)
                    || (Path.GetExtension(path).Equals(".ini", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(path).Equals("coop.ini", StringComparison.OrdinalIgnoreCase)
                        && !Path.GetFileName(path).Equals("swkotor.ini", StringComparison.OrdinalIgnoreCase))
                    || Path.GetFileName(path).Equals("dinput8.chain", StringComparison.OrdinalIgnoreCase))
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }
}

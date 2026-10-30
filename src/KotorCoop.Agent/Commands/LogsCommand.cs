using System.IO.Compression;
using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace KotorCoop.Agent;

/// <summary>
/// The <c>logs</c> verb: bundles what a maintainer needs to diagnose a report into ONE zip a
/// non-technical player can attach — the plugin and agent logs (rotated generations included),
/// a crash summary, a redacted <c>coop.ini</c>, and a short system note. Review §2 (2026-09-18): crash tooling was developer-only
/// (<c>crashbin.ps1</c>/<c>crashlog.ps1</c> are not in the package, no "collect logs" verb).
///
/// Nothing is uploaded; the zip is written beside the player's Desktop (or the game folder when
/// there is no Desktop) and the path is printed. Text is automatically redacted; a readable
/// manifest explains what remains. The original logs and raw crash stack stay on the PC.
/// </summary>
public static class LogsCommand
{
    /// <summary>coop.ini keys whose value must never leave the machine.</summary>
    private static readonly string[] SecretKeys = ["password", "host"];

    private static readonly Regex IniLine = new(
        @"^(?<lead>\s*)(?<key>[A-Za-z0-9_]+)(?<sep>\s*=\s*)(?<value>.*)$", RegexOptions.Compiled);

    /// <summary>Blanks the value of every key in <see cref="SecretKeys"/> (case-insensitive),
    /// leaving comments, other keys and line endings untouched. An already-empty value stays
    /// empty so the bundle still shows "no password was set".</summary>
    public static string RedactIni(string iniText)
    {
        ArgumentNullException.ThrowIfNull(iniText);

        string[] lines = iniText.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            Match m = IniLine.Match(line.TrimEnd('\r'));
            if (!m.Success)
            {
                continue;
            }

            string key = m.Groups["key"].Value;
            if (!SecretKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string value = m.Groups["value"].Value.Trim();
            string replaced = value.Length == 0 ? string.Empty : "<redacted>";
            lines[i] = m.Groups["lead"].Value + key + m.Groups["sep"].Value + replaced
                + (line.EndsWith('\r') ? "\r" : string.Empty);
        }

        return string.Join('\n', lines);
    }

    /// <summary>True for the top-level game-directory files the bundle includes: the plugin/agent
    /// logs with their rotated generations, and the crash record.</summary>
    public static bool IsSupportFile(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
        {
            return false;
        }

        string name = fileName.ToLowerInvariant();
        return Regex.IsMatch(name, @"^kotorcoop-(?:plugin|agent|setup)(?:\.\d+)?\.log(?:\.\d+)?$")
            || name == "kotorcoop-crash.bin"
            || name == "dinput8-chain.log";
    }

    /// <summary>Writes the bundle. Returns the number of files added (coop.ini and system.txt
    /// included), or 0 when the game directory holds nothing to collect.</summary>
    public static int WriteBundle(string gameDirectory, string zipPath, string agentVersion)
    {
        ArgumentException.ThrowIfNullOrEmpty(gameDirectory);
        ArgumentException.ThrowIfNullOrEmpty(zipPath);

        List<string> logs = Directory.EnumerateFiles(gameDirectory)
            .Where(f => IsSupportFile(Path.GetFileName(f)))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
        string iniPath = Path.Combine(gameDirectory, "coop.ini");
        bool hasIni = File.Exists(iniPath);
        string iniText = hasIni ? File.ReadAllText(iniPath) : string.Empty;
        SupportRedaction redaction = new(iniText);

        if (logs.Count == 0 && !hasIni)
        {
            return 0;
        }

        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }

        int count = 0;
        using (ZipArchive zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            List<string> included = [];
            foreach (string log in logs)
            {
                // Read shared: the game may still have its own log open.
                using FileStream input = new(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                string name = Path.GetFileName(log);
                if (name.Equals("kotorcoop-crash.bin", StringComparison.OrdinalIgnoreCase))
                {
                    // The raw record contains stack memory, which cannot be reliably redacted.
                    // Export only its fixed numeric header; retain the original locally.
                    name = "crash-summary.txt";
                    AddText(zip, name, DescribeCrash(input));
                }
                else
                {
                    using StreamReader reader = new(input);
                    ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
                    while (reader.ReadLine() is { } line)
                    {
                        writer.WriteLine(redaction.Redact(line));
                    }
                }
                included.Add(name);
                count++;
            }

            if (hasIni)
            {
                AddText(zip, "coop.ini", redaction.Redact(RedactIni(iniText)));
                included.Add("coop.ini");
                count++;
            }

            // This summary contains fixed labels/names and no paths. Preserve dotted build
            // versions: an IP-address redactor would erase a legitimate version like 1.0.0.0.
            AddText(zip, "system.txt", DescribeSystem(gameDirectory, agentVersion));
            included.Add("system.txt");
            AddText(zip, "READ-ME-FIRST.txt", DescribeBundle(included));
            count += 2;
        }

        return count;
    }

    private static string DescribeBundle(IEnumerable<string> included) =>
        "KOTOR Co-op support report\n\n"
        + "Nothing has been uploaded. Original files have not been changed.\n"
        + "The report removes recognized IP addresses, personal file paths, connection commands,\n"
        + "invitation tokens, and the host, password and player name from your co-op settings.\n"
        + "Other players' names and free-text chat may remain; review the text files before sharing.\n"
        + "Automatic redaction is best effort, not a guarantee that every personal detail is removed.\n"
        + "The raw crash stack, saved games and game assets are not included. A crash summary\n"
        + "retains the exception code and addresses. The original crash record stays on your PC.\n\n"
        + "Included files:\n" + string.Join("\n", included.Select(name => "- " + name)) + "\n\n"
        + "In your bug report, describe what you expected, what happened, who was hosting,\n"
        + "your game mods and whether the players were on the same network. For connection\n"
        + "problems, ask your friend to create their own report too.\n";

    private static string DescribeCrash(Stream input)
    {
        Span<byte> header = stackalloc byte[24];
        if (input.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
        {
            return "Crash record is incomplete. Raw bytes were not included.\n";
        }
        return $"serial      {BinaryPrimitives.ReadUInt32LittleEndian(header):X8}\n"
            + $"exception   0x{BinaryPrimitives.ReadUInt32LittleEndian(header[4..]):X8}\n"
            + $"address     0x{BinaryPrimitives.ReadUInt32LittleEndian(header[8..]):X8}\n"
            + $"instruction 0x{BinaryPrimitives.ReadUInt32LittleEndian(header[12..]):X8}\n"
            + "Raw stack memory omitted for privacy; the original remains in the game folder.\n";
    }

    private static void AddText(ZipArchive zip, string name, string text)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false));
        writer.Write(text);
    }

    private static string DescribeSystem(string gameDirectory, string agentVersion)
    {
        StringBuilder sb = new();
        sb.AppendLine($"generated   {DateTime.UtcNow:u}");
        sb.AppendLine($"agent       {agentVersion}");
        sb.AppendLine($"installed   {PackageInstaller.ReadVersion(gameDirectory) ?? "unknown"}");
        sb.AppendLine($"os          {Environment.OSVersion} ({(Environment.Is64BitOperatingSystem ? "64" : "32")}-bit)");
        sb.AppendLine("game dir    <path omitted>");

        foreach (string name in new[] { "swkotor.exe", "dinput8.dll", "KotorCoop.Plugin.dll", "dinput8.chain" })
        {
            string path = Path.Combine(gameDirectory, name);
            if (File.Exists(path))
            {
                FileInfo info = new(path);
                sb.AppendLine($"{name,-22}{info.Length,12:N0} bytes  {info.LastWriteTimeUtc:u}");
            }
            else
            {
                sb.AppendLine($"{name,-22}(absent)");
            }
        }

        return sb.ToString();
    }

    /// <summary>The verb itself: resolve the game folder like <c>uninstall</c> does, write the zip
    /// to the Desktop, and tell the player what to do with it.</summary>
    internal static int Run(AgentOptions options)
    {
        string? game = InstallCommand.ResolveGameDirectory(options, interactive: false, options.Rescan);
        if (game is null)
        {
            return 1;
        }

        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string folder = !string.IsNullOrEmpty(desktop) && Directory.Exists(desktop) ? desktop : game;
        string zip = Path.Combine(folder, $"kotorcoop-support-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

        string version = typeof(LogsCommand).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(LogsCommand).Assembly.GetName().Version?.ToString() ?? "unknown";
        int files = WriteBundle(game, zip, version);
        if (files == 0)
        {
            Console.WriteLine($"nothing to collect in {game} (no kotorcoop logs or coop.ini yet - run the game once first).");
            return 1;
        }

        Console.WriteLine($"wrote {zip} ({files} file(s)).");
        Console.WriteLine("Open the ZIP and read READ-ME-FIRST.txt for its contents and privacy summary.");
        Console.WriteLine("Recognized addresses, paths and credentials were removed automatically from the text.");
        Console.WriteLine("Other players' names and chat may remain. Review the report, then attach the ZIP to your issue.");
        Console.WriteLine("Nothing was uploaded; your original logs and crash record were left unchanged.");
        return 0;
    }

    internal static int RunInteractive(AgentOptions options)
    {
        bool interactive = !Console.IsInputRedirected && !Console.IsOutputRedirected;
        string here = AppContext.BaseDirectory;
        string? game = options.HasGamePath ? options.GamePath
            : PackageInstaller.IsGameDirectory(here) ? here
            : InstallCommand.ChooseGameDirectory(interactive, options.Rescan, "Collect logs from");
        return game is null ? 1 : Run(options with { GamePath = game, HasGamePath = true });
    }
}

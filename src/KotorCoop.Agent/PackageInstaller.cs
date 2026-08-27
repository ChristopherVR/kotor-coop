using System.Text;
using Microsoft.Win32;

namespace KotorCoop.Agent;

/// <summary>
/// Puts a kotor-coop package into a game directory, or takes it back out. THE one
/// implementation: <c>KotorCoop.Agent.exe install/uninstall</c>, the no-argument setup
/// (double-clicking the exe), <c>tools/install.ps1</c> and <c>build.ps1 -Install</c> all
/// come here.
///
/// A package is a game-directory IMAGE — the same layout the files end up in, so extracting
/// the zip straight into the game folder is itself a valid install and this class only
/// ever copies files by their relative path:
///
///   dinput8.dll                 the plugin (a dinput8 proxy the game loads itself)
///   KotorCoop.*                 the agent
///   coop.ini                    config template (never overwrites an existing one)
///   Override\k_coop_*           override assets (never anything else)
///   chain-loader\dinput8.dll    the wrapper used in chain mode (see below)
///
/// CHAIN MODE. Only one file can be called dinput8.dll. When another mod already owns the
/// name (ReShade, an ASI loader...), the loader is installed as dinput8.dll instead, the
/// plugin as KotorCoop.Plugin.dll, the other mod is renamed dinput8.prev.dll, and a
/// dinput8.chain file lists the load order. Our two DLLs are recognised by markers, so a
/// foreign dinput8.dll is never overwritten or deleted without being asked.
/// </summary>
public static class PackageInstaller
{
    public const string PluginFileName = "dinput8.dll";
    public const string ChainPluginFileName = "KotorCoop.Plugin.dll";
    public const string PreviousFileName = "dinput8.prev.dll";
    public const string ChainFileName = "dinput8.chain";
    public const string LoaderRelativePath = @"chain-loader\dinput8.dll";
    public const string GameExecutable = "swkotor.exe";

    /// <summary>What a dinput8.dll on disk is.</summary>
    public enum DinputKind
    {
        None,
        Plugin,
        Loader,
        Foreign,
    }

    public sealed record Options(
        string PackageDirectory,
        string GameDirectory,
        bool Chain = false,
        IReadOnlyList<string>? Load = null,
        bool Force = false);

    /// <summary>Thrown for a condition the caller must decide about (a foreign dinput8.dll
    /// without -Chain/-Force); the message says what the options are.</summary>
    public sealed class RefusedException(string message) : Exception(message);

    // The plugin's startup banner (a UTF-16 .NET literal) and the loader's exported marker
    // symbol (ASCII). Byte patterns, so the search is alignment-agnostic.
    private static readonly byte[] PluginMarker = Encoding.Unicode.GetBytes("=== KotorCoop plugin loaded");
    private static readonly byte[] LoaderMarker = Encoding.ASCII.GetBytes("KotorCoop.Loader chain dinput8");

    public static DinputKind Identify(string path)
    {
        if (!File.Exists(path))
        {
            return DinputKind.None;
        }

        byte[] image = File.ReadAllBytes(path);
        if (image.AsSpan().IndexOf(LoaderMarker) >= 0)
        {
            return DinputKind.Loader;
        }

        return image.AsSpan().IndexOf(PluginMarker) >= 0 ? DinputKind.Plugin : DinputKind.Foreign;
    }

    /// <summary>True when <paramref name="directory"/> looks like an extracted package (or a
    /// game directory the package was extracted into).</summary>
    public static bool IsPackageDirectory(string directory) =>
        File.Exists(Path.Combine(directory, PluginFileName))
        && File.Exists(Path.Combine(directory, "KotorCoop.Agent.exe"))
        && Directory.Exists(Path.Combine(directory, "Override"));

    public static bool IsGameDirectory(string directory) =>
        File.Exists(Path.Combine(directory, GameExecutable));

    public static bool IsInstalled(string gameDirectory) =>
        Identify(Path.Combine(gameDirectory, PluginFileName)) is DinputKind.Plugin or DinputKind.Loader
        && File.Exists(Path.Combine(gameDirectory, "KotorCoop.Agent.exe"));

    /// <summary>Installs; every file placed is reported through <paramref name="log"/>.</summary>
    public static void Install(Options options, Action<string> log)
    {
        string package = Path.GetFullPath(options.PackageDirectory);
        string game = Path.GetFullPath(options.GameDirectory);
        IReadOnlyList<string> extra = options.Load ?? [];
        bool chainMode = options.Chain || extra.Count > 0;

        if (!Directory.Exists(game))
        {
            throw new DirectoryNotFoundException($"game directory not found: {game}");
        }

        if (!IsGameDirectory(game))
        {
            log($"warning: no {GameExecutable} in {game} - is this really the game directory?");
        }

        string pluginSource = Path.Combine(package, PluginFileName);
        if (!File.Exists(pluginSource))
        {
            throw new FileNotFoundException($"{PluginFileName} not found in {package} - run this from an extracted kotor-coop package");
        }

        if (string.Equals(package, game, StringComparison.OrdinalIgnoreCase))
        {
            // The zip was extracted straight into the game folder: that IS the install.
            log($"package and game directory are the same ({game}); the files are already in place");
            return;
        }

        string proxyTarget = Path.Combine(game, PluginFileName);
        DinputKind existing = Identify(proxyTarget);

        if (existing == DinputKind.Foreign && !chainMode && !options.Force)
        {
            throw new RefusedException(
                $"{proxyTarget} already exists and is not kotor-coop's - another mod is using the dinput8 slot.\n"
                + "  chain  keeps it: the loader becomes dinput8.dll and loads both (recommended)\n"
                + "  force  overwrites it (that mod stops working)");
        }

        if (chainMode)
        {
            string loaderSource = Path.Combine(package, LoaderRelativePath);
            if (!File.Exists(loaderSource))
            {
                throw new FileNotFoundException($"{LoaderRelativePath} not found in {package} - this package has no chain loader");
            }

            if (existing == DinputKind.Foreign)
            {
                string previous = Path.Combine(game, PreviousFileName);
                if (File.Exists(previous))
                {
                    throw new RefusedException($"{previous} already exists - resolve that first (an earlier chain install?)");
                }

                File.Move(proxyTarget, previous);
                log($"kept the existing dinput8.dll as {PreviousFileName}");
            }

            File.Copy(loaderSource, proxyTarget, overwrite: true);
            log($"installed loader -> {proxyTarget}");

            string pluginTarget = Path.Combine(game, ChainPluginFileName);
            File.Copy(pluginSource, pluginTarget, overwrite: true);
            log($"installed plugin -> {pluginTarget}");

            // The plugin first (it receives the game's calls), the previous mod next (so the
            // plugin forwards into it), then whatever was asked for.
            List<string> entries = [ChainPluginFileName];
            if (File.Exists(Path.Combine(game, PreviousFileName)))
            {
                entries.Add(PreviousFileName);
            }

            foreach (string item in extra)
            {
                if (item.Length > 0 && !entries.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(item);
                }
            }

            string chainFile = Path.Combine(game, ChainFileName);
            File.WriteAllLines(chainFile,
            [
                "# dinput8.chain - DLLs the kotor-coop loader (dinput8.dll) loads, in order.",
                "# One path per line, relative to this directory. # and ; start a comment.",
                "# The first entry that exports DirectInput8Create receives the game's DirectInput",
                "# calls; the kotor-coop plugin forwards them to the entry after itself, then to",
                "# the system dinput8.dll. See README.md in the package.",
                .. entries,
            ]);
            log($"wrote {chainFile} ({string.Join(", ", entries)})");
        }
        else
        {
            // Plain mode: the plugin IS dinput8.dll. Clear a previous chain-mode install so
            // two copies of the plugin cannot both load.
            foreach (string stale in new[] { ChainPluginFileName, ChainFileName })
            {
                string path = Path.Combine(game, stale);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    log($"removed stale chain-mode file {path}");
                }
            }

            File.Copy(pluginSource, proxyTarget, overwrite: true);
            log($"installed -> {proxyTarget}");
        }

        // The agent, into the game ROOT: AgentLauncher searches the game directory first, so
        // a loose copy there would shadow anything deeper.
        int agentFiles = 0;
        foreach (string file in Directory.GetFiles(package, "KotorCoop.*"))
        {
            string target = Path.Combine(game, Path.GetFileName(file));
            try
            {
                File.Copy(file, target, overwrite: true);
                agentFiles++;
            }
            catch (IOException ex)
            {
                // A running agent holds its own image; the plugin above still installed.
                throw new IOException($"agent file locked ({Path.GetFileName(file)}) - stop KotorCoop.Agent.exe and the game, then install again", ex);
            }
        }

        log($"installed agent -> {game} ({agentFiles} files)");

        // Override assets: every one is a k_coop_ resref, so this can never collide with a
        // mod and only ever adds to the folder.
        string overrideSource = Path.Combine(package, "Override");
        if (Directory.Exists(overrideSource))
        {
            string overrideTarget = Path.Combine(game, "Override");
            Directory.CreateDirectory(overrideTarget);
            int assets = 0;
            foreach (string asset in Directory.GetFiles(overrideSource, "k_coop_*"))
            {
                File.Copy(asset, Path.Combine(overrideTarget, Path.GetFileName(asset)), overwrite: true);
                assets++;
            }

            log($"installed override assets -> {overrideTarget} ({assets} files)");
        }

        // coop.ini is the player's own config once it exists; never clobber it.
        string iniSource = Path.Combine(package, "coop.ini");
        string iniTarget = Path.Combine(game, "coop.ini");
        if (File.Exists(iniTarget))
        {
            log($"coop.ini already exists at {iniTarget} - left untouched");
        }
        else if (File.Exists(iniSource))
        {
            File.Copy(iniSource, iniTarget);
            log($"installed -> {iniTarget}");
        }
    }

    /// <summary>Removes only what Install placed, recognising our DLLs by marker; restores a
    /// foreign dinput8.dll kept by chain mode; keeps coop.ini.</summary>
    public static void Uninstall(string gameDirectory, Action<string> log)
    {
        string game = Path.GetFullPath(gameDirectory);
        string proxy = Path.Combine(game, PluginFileName);

        switch (Identify(proxy))
        {
            case DinputKind.None:
                log($"nothing installed at {proxy}");
                break;
            case DinputKind.Foreign:
                log($"left {proxy} alone: it is not ours");
                break;
            case DinputKind kind:
                File.Delete(proxy);
                log($"removed {proxy} ({kind})");
                break;
        }

        string chainFile = Path.Combine(game, ChainFileName);
        if (File.Exists(chainFile))
        {
            File.Delete(chainFile);
            log($"removed {chainFile}");
        }

        string previous = Path.Combine(game, PreviousFileName);
        if (File.Exists(previous) && !File.Exists(proxy))
        {
            File.Move(previous, proxy);
            log($"restored the other mod's dinput8.dll from {PreviousFileName}");
        }

        foreach (string name in new[] { "kotorcoop-plugin.log", "dinput8-chain.log" })
        {
            string logFile = Path.Combine(game, name);
            if (File.Exists(logFile))
            {
                File.Delete(logFile);
                log($"removed {logFile}");
            }
        }

        // Every agent file (and the chain-mode plugin) is KotorCoop.*.
        foreach (string ours in Directory.GetFiles(game, "KotorCoop.*"))
        {
            try
            {
                File.Delete(ours);
                log($"removed {ours}");
            }
            catch (IOException)
            {
                log($"could not remove {ours} (in use - is this the agent doing the uninstall? run it from the extracted package instead)");
            }
        }

        string overrideDir = Path.Combine(game, "Override");
        if (Directory.Exists(overrideDir))
        {
            foreach (string ours in Directory.GetFiles(overrideDir, "k_coop_*"))
            {
                File.Delete(ours);
                log($"removed override {ours}");
            }
        }
    }

    /// <summary>A game install found on this machine and whether it is one we support.</summary>
    public sealed record Candidate(string Directory, string Source, bool Supported);

    /// <summary>
    /// Finds KOTOR installs: GOG's registry entry first (the supported build), then every
    /// fixed drive's usual GOG/Galaxy folders, then Steam (listed so the user is told why it
    /// will not work rather than silently ignored).
    /// </summary>
    public static IReadOnlyList<Candidate> FindGameDirectories()
    {
        List<Candidate> found = [];

        void Add(string? directory, string source, bool supported)
        {
            if (directory is null || !IsGameDirectory(directory))
            {
                return;
            }

            string full = Path.GetFullPath(directory).TrimEnd('\\');
            if (!found.Any(c => string.Equals(c.Directory, full, StringComparison.OrdinalIgnoreCase)))
            {
                found.Add(new Candidate(full, source, supported));
            }
        }

        if (OperatingSystem.IsWindows())
        {
            // GOG's own product id for Star Wars: Knights of the Old Republic.
            foreach (string hive in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games\1207666283", @"SOFTWARE\GOG.com\Games\1207666283" })
            {
                try
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(hive);
                    Add(key?.GetValue("path") as string, "GOG (registry)", supported: true);
                }
                catch (Exception)
                {
                    // No registry access is just "not found".
                }
            }
        }

        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            string root = drive.RootDirectory.FullName;
            foreach (string library in new[] { "GOG Games", @"GOG Galaxy\Games", @"Program Files (x86)\GOG Galaxy\Games", @"Program Files\GOG Galaxy\Games", "Games" })
            {
                string libraryPath = Path.Combine(root, library);
                if (!Directory.Exists(libraryPath))
                {
                    continue;
                }

                Add(libraryPath, "GOG folder", supported: true);
                try
                {
                    foreach (string sub in Directory.GetDirectories(libraryPath))
                    {
                        Add(sub, "GOG folder", supported: true);
                    }
                }
                catch (Exception)
                {
                    // Unreadable library folder: skip it.
                }
            }

            Add(Path.Combine(root, @"Program Files (x86)\Steam\steamapps\common\swkotor"), "Steam (not supported: DRM refuses a second instance and the binary differs)", supported: false);
        }

        return found;
    }
}

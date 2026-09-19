using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using KotorCoop.Memory;
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
/// name (ReShade, an ASI loader...), this is detected AUTOMATICALLY: the loader is installed
/// as dinput8.dll instead, the plugin as KotorCoop.Plugin.dll, the other mod is renamed
/// dinput8.prev.dll, and a dinput8.chain file lists the load order — no flag needed, one
/// summary line names what was found (<see cref="ForeignDinputIdentity"/>). The exception is
/// a plain copy of Windows' own dinput8.dll: that isn't a mod, so it's just replaced, the same
/// as if nothing had been there. <c>--force</c> always overwrites a foreign dinput8.dll
/// outright instead of chaining it; <c>--chain</c> forces chain mode explicitly (now mostly a
/// no-op, since it is the automatic default). Our two DLLs are recognised by markers, so a
/// foreign dinput8.dll is never overwritten or deleted without being asked (again, short of
/// --force). Re-running install over an existing install — plain or chain — upgrades files in
/// place rather than duplicating or dropping anything; see <see cref="Install"/>.
/// </summary>
public static partial class PackageInstaller
{
    public const string PluginFileName = "dinput8.dll";
    public const string ChainPluginFileName = "KotorCoop.Plugin.dll";
    public const string PreviousFileName = "dinput8.prev.dll";
    public const string ChainFileName = "dinput8.chain";
    public const string LoaderRelativePath = @"chain-loader\dinput8.dll";
    public const string GameExecutable = "swkotor.exe";

    /// <summary>
    /// Whether <see cref="Install"/>, <see cref="Check"/> and <see cref="Uninstall"/> touch the
    /// Windows Firewall (<see cref="WindowsFirewall"/>). ON for every real run; the test suite
    /// turns it off once, because an elevated test run (CI's Windows runners are) would
    /// otherwise add a real inbound rule for a fake exe path in a temp directory.
    /// </summary>
    public static bool FirewallEnabled { get; set; } = true;

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

    /// <summary>
    /// Thrown for a condition auto-detection cannot resolve on its own — today just
    /// dinput8.prev.dll already existing when a chain install would need to create it (an
    /// earlier chain install left in a half-finished state). A plain foreign dinput8.dll no
    /// longer throws this: it is chained automatically instead (see the class doc comment).
    /// </summary>
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

    /// <summary>
    /// A friendly name for the foreign (not-ours) file at <paramref name="path"/>, for the
    /// install-summary message — "ReShade", "Ultimate ASI Loader", "the system dinput8.dll
    /// (Microsoft Corporation)", or a generic "another dinput8 mod" fallback. The byte-content
    /// checks are <see cref="ForeignDinputIdentity"/> (pure, unit-tested); this just supplies
    /// the file bytes and, when readable, the version resource's FileDescription for the
    /// fallback name.
    /// </summary>
    public static ForeignDinputIdentity.Identity IdentifyForeign(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        string? fileDescription = null;
        try
        {
            fileDescription = FileVersionInfo.GetVersionInfo(path).FileDescription;
        }
        catch (Exception)
        {
            // No version resource, or one FileVersionInfo could not parse: judged on the
            // file's content alone.
        }

        return ForeignDinputIdentity.Identify(image, fileDescription);
    }

    /// <summary>
    /// The mod's own version, read from <c>KotorCoop.Agent.exe</c>'s version resource in
    /// <paramref name="directory"/> — <c>tools/package.ps1</c> stamps it at publish time
    /// (<c>-p:InformationalVersion</c>) from <c>git describe</c>; a plain <c>dotnet build</c>
    /// dev binary reports the csproj's own placeholder instead. Null when there's no
    /// <c>KotorCoop.Agent.exe</c> there at all (nothing installed, or not a package/game dir).
    /// </summary>
    public static string? ReadVersion(string directory)
    {
        string path = Path.Combine(directory, "KotorCoop.Agent.exe");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string? version = FileVersionInfo.GetVersionInfo(path).ProductVersion?.Trim();
            return string.IsNullOrEmpty(version) ? null : version;
        }
        catch (Exception)
        {
            return null;
        }
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

    /// <summary>
    /// Installs; every file placed is reported through <paramref name="log"/>. Returns
    /// whether the health check run at the end (<see cref="Check"/>) passed — the caller
    /// decides what to do with a failing install (exit code, a "here's what to try" message).
    /// </summary>
    public static bool Install(Options options, Action<string> log)
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
            return Check(game, log);
        }

        string proxyTarget = Path.Combine(game, PluginFileName);
        DinputKind existing = Identify(proxyTarget);

        // Whether THIS directory already looks like a chain install (the loader is in the
        // dinput8.dll slot, or its two companion files are already here) — used below so a
        // plain re-run of `install` over an existing chain install upgrades it in place
        // instead of silently falling through to plain mode and overwriting the loader with
        // the bare plugin, which would orphan KotorCoop.Plugin.dll/dinput8.chain/dinput8.prev.dll.
        bool chainAlreadyInstalled = existing == DinputKind.Loader
            || File.Exists(Path.Combine(game, ChainFileName))
            || File.Exists(Path.Combine(game, ChainPluginFileName));

        // The friendly name of whatever we're chaining behind, when this run is the one doing
        // the chaining — carried through to the .asi summary below.
        string? chainedModName = null;

        if (existing == DinputKind.Foreign && !options.Force)
        {
            ForeignDinputIdentity.Identity identity = IdentifyForeign(proxyTarget);
            if (identity.Kind == ForeignDinputIdentity.Kind.SystemFile)
            {
                // Not a mod - Windows' own file landed in the slot somehow. Nothing to keep,
                // so this is treated exactly like "nothing was there": chainMode stays
                // whatever it already was (an explicit --chain still chains it if asked).
                if (!chainMode)
                {
                    log($"{proxyTarget} is {identity.FriendlyName} - not a mod, so it is simply replaced (no chain needed)");
                }
            }
            else
            {
                chainedModName = identity.FriendlyName;
                if (!chainMode)
                {
                    chainMode = true;
                    log($"found another mod in dinput8.dll ({identity.FriendlyName}); installing as a chain: our loader "
                        + "takes dinput8.dll, the other mod is kept as dinput8.prev.dll and loaded after us");
                }
            }
        }
        else if (!chainMode && chainAlreadyInstalled)
        {
            chainMode = true;
            log($"{proxyTarget} is already the kotor-coop chain loader; upgrading the plugin and loader in place (chain kept)");
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

            // Start from whatever dinput8.chain already lists — a re-run must not drop an
            // entry a player added by hand, or duplicate the ones we manage ourselves — then
            // make sure our plugin leads (it receives the game's calls), the kept foreign DLL
            // follows it, and any --load extras are present.
            string chainFile = Path.Combine(game, ChainFileName);
            List<string> entries = ParseChainEntries(chainFile);
            entries.RemoveAll(e => string.Equals(e, ChainPluginFileName, StringComparison.OrdinalIgnoreCase));
            entries.Insert(0, ChainPluginFileName);

            if (File.Exists(Path.Combine(game, PreviousFileName))
                && !entries.Contains(PreviousFileName, StringComparer.OrdinalIgnoreCase))
            {
                entries.Insert(1, PreviousFileName);
            }

            foreach (string item in extra)
            {
                if (item.Length > 0 && !entries.Contains(item, StringComparer.OrdinalIgnoreCase))
                {
                    entries.Add(item);
                }
            }

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

            // .asi mods (Ultimate ASI Loader's own plugin folders, or any dropped loose) keep
            // working unmodified - mention them so the player isn't left wondering. Never moved.
            IReadOnlyList<string> asiMods = FindAsiMods(game);
            if (asiMods.Count > 0)
            {
                string via = chainedModName ?? "the chain";
                string plural = asiMods.Count == 1 ? "" : "s";
                log($"found {asiMods.Count} .asi mod{plural} ({string.Join(", ", asiMods)}) - they will keep loading through {via}");
            }
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

            string orphanedPrevious = Path.Combine(game, PreviousFileName);
            if (File.Exists(orphanedPrevious))
            {
                log($"note: {orphanedPrevious} exists but nothing chains to it - run install --chain to load it alongside kotor-coop");
            }
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

        // After coop.ini, so the rule opens the port that file names. Adds it when elevated,
        // prints the exact command otherwise — see WindowsFirewall for why the game hides
        // Windows' own prompt.
        if (FirewallEnabled)
        {
            WindowsFirewall.Install(game, log);
        }

        log(string.Empty);
        log("checking the install...");
        return Check(game, log);
    }

    /// <summary>
    /// Every <c>*.asi</c> beside the game or in its <c>scripts\</c>/<c>plugins\</c>
    /// subfolders (the two locations Ultimate ASI Loader and similar tools scan), as paths
    /// relative to <paramref name="game"/>. Purely informational — nothing here is ever moved.
    /// </summary>
    private static List<string> FindAsiMods(string game)
    {
        List<string> found = [];
        foreach (string dir in new[] { game, Path.Combine(game, "scripts"), Path.Combine(game, "plugins") })
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir, "*.asi"))
            {
                found.Add(Path.GetRelativePath(game, file));
            }
        }

        return found;
    }

    /// <summary>
    /// The entries already listed in a <c>dinput8.chain</c> file (comments and blank lines
    /// stripped, in file order), or empty when it doesn't exist. Shared by <see cref="Install"/>
    /// (to merge in what a re-run needs without dropping what a player added by hand) and
    /// <see cref="Check"/> (to verify every entry still exists).
    /// </summary>
    private static List<string> ParseChainEntries(string chainFilePath)
    {
        List<string> entries = [];
        if (!File.Exists(chainFilePath))
        {
            return entries;
        }

        foreach (string rawLine in File.ReadAllLines(chainFilePath))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            {
                continue;
            }

            entries.Add(line);
        }

        return entries;
    }

    /// <summary>
    /// Reports the install layout found in <paramref name="gameDirectory"/> — plain, chain,
    /// foreign-only or none — and verifies every piece that should be there actually is:
    /// every <c>dinput8.chain</c> entry exists on disk, the loader and
    /// <see cref="ChainPluginFileName"/> carry their markers, <c>coop.ini</c> exists. Changes
    /// nothing. Every line is a PASS/FAIL through <paramref name="log"/>; the return value is
    /// whether everything passed.
    /// </summary>
    public static bool Check(string gameDirectory, Action<string> log)
    {
        string game = Path.GetFullPath(gameDirectory);
        bool ok = true;

        void Report(bool passed, string message)
        {
            log($"{(passed ? "PASS" : "FAIL")}: {message}");
            ok &= passed;
        }

        if (!Directory.Exists(game))
        {
            log($"FAIL: game directory not found: {game}");
            return false;
        }

        string proxy = Path.Combine(game, PluginFileName);
        DinputKind kind = Identify(proxy);
        string chainPluginPath = Path.Combine(game, ChainPluginFileName);
        string chainFilePath = Path.Combine(game, ChainFileName);
        bool hasChainPlugin = File.Exists(chainPluginPath);
        bool hasChainFile = File.Exists(chainFilePath);

        string layout = kind switch
        {
            DinputKind.Loader => "chain",
            DinputKind.Plugin => "plain",
            DinputKind.Foreign => "foreign-only",
            _ when hasChainPlugin || hasChainFile => "chain (dinput8.dll missing)",
            _ => "none",
        };

        log($"layout: {layout} ({proxy})");

        string? installedVersion = ReadVersion(game);
        log(installedVersion is null
            ? "version: KotorCoop.Agent.exe not found - version unknown"
            : $"version: {installedVersion}");

        switch (kind)
        {
            case DinputKind.None:
                Report(false, $"no dinput8.dll in {game} - kotor-coop is not installed here");
                break;

            case DinputKind.Foreign:
                Report(false, $"{proxy} belongs to another mod - kotor-coop is not installed here "
                    + "(run install to chain behind it or --force to overwrite it)");
                break;

            case DinputKind.Plugin:
                Report(true, $"{proxy} carries the kotor-coop plugin marker");
                break;

            case DinputKind.Loader:
                Report(true, $"{proxy} carries the chain loader marker");
                Report(hasChainPlugin, hasChainPlugin
                    ? $"{chainPluginPath} is present"
                    : $"{chainPluginPath} is MISSING");

                if (hasChainPlugin)
                {
                    Report(Identify(chainPluginPath) == DinputKind.Plugin,
                        $"{chainPluginPath} carries the kotor-coop plugin marker");
                }

                if (hasChainFile)
                {
                    List<string> entries = ParseChainEntries(chainFilePath);
                    Report(entries.Count > 0, $"{chainFilePath} lists at least one entry");
                    foreach (string entry in entries)
                    {
                        string entryPath = Path.IsPathRooted(entry) ? entry : Path.Combine(game, entry);
                        Report(File.Exists(entryPath), $"chain entry '{entry}' exists ({entryPath})");
                    }
                }
                else
                {
                    Report(false, $"{chainFilePath} is MISSING");
                }

                break;
        }

        string iniPath = Path.Combine(game, "coop.ini");
        Report(File.Exists(iniPath), File.Exists(iniPath) ? $"{iniPath} exists" : $"{iniPath} is MISSING");

        // A NOTE, never a FAIL: the install is complete without it, and a LAN-only host whose
        // partner sits on the same network never needs it — see WindowsFirewall.Report.
        if (FirewallEnabled)
        {
            WindowsFirewall.Report(game, log);
        }

        return ok;
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

        if (FirewallEnabled)
        {
            WindowsFirewall.Remove(log);
        }
    }

    /// <summary>A game install found on this machine, whether it is one we support, and the
    /// kotor-coop version already installed there (null when nothing is installed yet).</summary>
    public sealed record Candidate(string Directory, string Source, bool Supported, string? InstalledVersion = null);

    /// <summary>
    /// Builds one <see cref="Candidate"/> for an already-confirmed game directory: the
    /// executable form (<see cref="SteamBuild"/>) and the installed kotor-coop version
    /// (<see cref="ReadVersion"/>), if any. Both are single small file reads, not a directory
    /// scan, so re-deriving them for a cached directory on every run - rather than trusting a
    /// stale cached label - costs nothing worth avoiding.
    /// </summary>
    internal static Candidate DescribeCandidate(string directory, string rawSource, bool supported)
    {
        string full = Path.GetFullPath(directory).TrimEnd('\\');
        KotorExecutableForm form = SteamBuild.InspectFile(Path.Combine(full, GameExecutable), out _);
        string formLabel = form switch
        {
            KotorExecutableForm.SteamStub => "SteamStub-wrapped 1.0.3",
            KotorExecutableForm.DrmFree => "DRM-free 1.0.3",
            _ => "unrecognised build",
        };

        return new Candidate(full, $"{rawSource}, {formLabel}", supported, ReadVersion(full));
    }

    /// <summary>
    /// Finds KOTOR installs: <see cref="InstallScanCache"/> first (a handful of cheap file
    /// checks - no drive enumeration) when it has anything left standing, a full
    /// <see cref="ScanGameDirectories"/> otherwise. The cache is refreshed from whatever a
    /// full scan just found, so the very next run is the fast path again; pass
    /// <paramref name="forceRescan"/> (<c>install --rescan</c>) to skip straight to a full
    /// scan, e.g. after installing a new copy of the game the cache does not know about yet.
    /// </summary>
    public static IReadOnlyList<Candidate> FindGameDirectories(bool forceRescan = false)
    {
        if (!forceRescan)
        {
            IReadOnlyList<Candidate>? cached = InstallScanCache.TryLoad();
            if (cached is { Count: > 0 })
            {
                return cached;
            }
        }

        return ScanGameDirectories();
    }

    /// <summary>
    /// The full, uncached machine sweep behind <see cref="FindGameDirectories"/>: GOG's
    /// registry entry first, then every fixed drive's usual GOG/Galaxy folders, then every
    /// Steam library on the machine. Checking every fixed drive can take a few seconds on a
    /// machine with several of them - <see cref="InstallScanCache"/> exists specifically to
    /// avoid paying that cost on every run.
    ///
    /// ⚠ STEAM IS SUPPORTED, and used not to be. The Steam copy is the same link as the
    /// DRM-free one wrapped in Valve's SteamStub v3, which decrypts <c>.text</c> in place
    /// before the game's own entry point runs — every hardcoded engine address is the same VA
    /// at runtime, and <c>dinput8.dll</c> is a static import of both files at the same thunk,
    /// so the proxy loads identically (docs/research/steam-support.md §§1-5). The one thing
    /// that genuinely blocked it was our own handshake hashing the FILE, fixed in §8. What is
    /// still true of a Steam install, and belongs in documentation rather than in a refusal to
    /// install: the wrapper goes through the Steam client, so <c>--instance 2</c> cannot start
    /// a second local copy of THAT install (a dev-rig concern, never two players on two
    /// machines), and Steam's overlay may own the SwapBuffers import slot the frame hook wants.
    ///
    /// Every candidate is labelled with the form its executable is actually in
    /// (<see cref="SteamBuild"/>), not with the folder it was found in — a "GOG Games" folder
    /// holding an unwrapped Steam exe is a real thing on this very machine, and the label
    /// should say what is there rather than what the path suggests.
    /// </summary>
    private static IReadOnlyList<Candidate> ScanGameDirectories()
    {
        List<Candidate> found = [];
        // Same directories as `found`, but with the source label BEFORE DescribeCandidate
        // combined in the form label - what InstallScanCache needs so a reload can re-derive
        // a fresh form label/version later instead of trusting a stale cached one.
        List<InstallScanCache.Entry> raw = [];

        void Add(string? directory, string source, bool supported)
        {
            if (directory is null || !IsGameDirectory(directory))
            {
                return;
            }

            string full = Path.GetFullPath(directory).TrimEnd('\\');
            if (found.Any(c => string.Equals(c.Directory, full, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            found.Add(DescribeCandidate(full, source, supported));
            raw.Add(new InstallScanCache.Entry(full, source, supported));
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

            // The default client location on this drive. Libraries anywhere else (a second
            // drive, a custom folder) come from libraryfolders.vdf below.
            Add(Path.Combine(root, @"Program Files (x86)\Steam\steamapps\common\swkotor"), "Steam", supported: true);
        }

        foreach (string library in SteamLibraryDirectories())
        {
            Add(Path.Combine(library, @"steamapps\common\swkotor"), "Steam library", supported: true);
        }

        // Linux/Proton. NEEDS-LIVE: there is no Wine box in this environment, so none of this
        // has been exercised against a real install — see ProtonPaths' own doc comment for why
        // it is written to be testable without one. Harmless on real Windows either way: every
        // path below only exists at all when Z:\ is actually Wine's root mapping, and every
        // candidate is re-verified for swkotor.exe exactly like every other source above.
        Add(ProtonPaths.SteamCompatInstallPath(Environment.GetEnvironmentVariable), "Steam (Proton)", supported: true);

        foreach (string library in ProtonSteamLibraryDirectories())
        {
            Add(Path.Combine(library, @"steamapps\common\swkotor"), "Steam library (Proton)", supported: true);
        }

        foreach (string library in ProtonPaths.HeroicLutrisRoots(ProtonPaths.HomeDirectories(Directory.Exists, SafeEnumerateDirectories)))
        {
            if (!Directory.Exists(library))
            {
                continue;
            }

            Add(library, "Heroic/Lutris folder", supported: true);
            try
            {
                foreach (string sub in Directory.GetDirectories(library))
                {
                    Add(sub, "Heroic/Lutris folder", supported: true);
                }
            }
            catch (Exception)
            {
                // Unreadable library folder: skip it.
            }
        }

        InstallScanCache.Save(raw);
        return found;
    }

    /// <summary>Adds <paramref name="path"/> to <paramref name="roots"/> if it exists on disk
    /// and is not already there (case-insensitively) — the one piece of logic every Steam
    /// library root, native or Proton, is collected through. A malformed or unresolvable path
    /// is simply not a library; this never throws.</summary>
    private static void AddLibraryRoot(List<string> roots, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string full = Path.GetFullPath(path).TrimEnd('\\');
            if (Directory.Exists(full) && !roots.Any(r => string.Equals(r, full, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(full);
            }
        }
        catch (Exception)
        {
            // A malformed path is simply not a library.
        }
    }

    /// <summary>
    /// Follows every root already in <paramref name="roots"/> to its own
    /// <c>steamapps\libraryfolders.vdf</c> and adds whatever extra libraries it lists — how a
    /// second drive's library (native) or a second mount (Proton) is found, the common case
    /// for a large collection. Deliberately a regex over the VDF rather than a parser: the
    /// only thing wanted is the <c>"path"</c> values, and a wrong or stale line costs nothing
    /// because every result is checked for <c>swkotor.exe</c> afterwards. A vdf entry that is
    /// itself a POSIX path (a Linux Steam client's own library list) is converted to Wine form
    /// the same way <see cref="ProtonPaths.ToWinePath"/> converts everything else here — an
    /// entry that is already a Windows path passes through unchanged. Never throws: an
    /// unreadable or absent file leaves <paramref name="roots"/> exactly as it already was.
    /// </summary>
    private static void ExpandLibraryFoldersVdf(List<string> roots)
    {
        foreach (string root in roots.ToList())
        {
            try
            {
                string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(vdf))
                {
                    continue;
                }

                foreach (Match match in SteamLibraryPathPattern().Matches(File.ReadAllText(vdf)))
                {
                    string raw = match.Groups[1].Value.Replace(@"\\", @"\");
                    AddLibraryRoot(roots, ProtonPaths.ToWinePath(raw));
                }
            }
            catch (Exception)
            {
                // Unreadable libraryfolders.vdf: the roots already found still stand.
            }
        }
    }

    /// <summary><see cref="Directory.GetDirectories(string)"/>, but "unreadable" is just an
    /// empty result instead of an exception — the shape <see cref="ProtonPaths"/>' delegates
    /// need, and the same tolerance every other directory walk in this file already has.</summary>
    private static IEnumerable<string> SafeEnumerateDirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>
    /// Every Steam library root on this machine: the client's own install directory, plus
    /// every path listed in its <c>steamapps\libraryfolders.vdf</c> — which is how a second
    /// drive's library is found, the common case for a large collection.
    /// </summary>
    public static IReadOnlyList<string> SteamLibraryDirectories()
    {
        List<string> roots = [];

        if (OperatingSystem.IsWindows())
        {
            try
            {
                using RegistryKey? user = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                AddLibraryRoot(roots, user?.GetValue("SteamPath") as string);
            }
            catch (Exception)
            {
                // No registry access is just "not found".
            }

            foreach (string hive in new[] { @"SOFTWARE\WOW6432Node\Valve\Steam", @"SOFTWARE\Valve\Steam" })
            {
                try
                {
                    using RegistryKey? key = Registry.LocalMachine.OpenSubKey(hive);
                    AddLibraryRoot(roots, key?.GetValue("InstallPath") as string);
                }
                catch (Exception)
                {
                    // Same.
                }
            }
        }

        // The usual locations too, in case the registry says nothing.
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            AddLibraryRoot(roots, Path.Combine(drive.RootDirectory.FullName, @"Program Files (x86)\Steam"));
            AddLibraryRoot(roots, Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
        }

        ExpandLibraryFoldersVdf(roots);

        return roots;
    }

    /// <summary>
    /// The Linux/Proton equivalent of <see cref="SteamLibraryDirectories"/>: starts from
    /// <see cref="ProtonPaths.SteamLibraryRoots"/> (the native/Flatpak/SD-card Steam
    /// installs reachable under Wine's <c>Z:\</c>) instead of the registry and drive letters,
    /// then follows the exact same <c>libraryfolders.vdf</c> expansion — a Proton Steam
    /// client's OWN extra libraries are found through it identically to a native one's.
    /// </summary>
    public static IReadOnlyList<string> ProtonSteamLibraryDirectories()
    {
        List<string> roots = [];
        IReadOnlyList<string> homes = [.. ProtonPaths.HomeDirectories(Directory.Exists, SafeEnumerateDirectories)];

        foreach (string root in ProtonPaths.SteamLibraryRoots(homes, Directory.Exists, SafeEnumerateDirectories))
        {
            AddLibraryRoot(roots, root);
        }

        ExpandLibraryFoldersVdf(roots);

        return roots;
    }

    [GeneratedRegex("\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex SteamLibraryPathPattern();
}

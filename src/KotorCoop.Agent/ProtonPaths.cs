namespace KotorCoop.Agent;

/// <summary>
/// Candidate KOTOR install locations reachable only through Wine/Proton's environment
/// variables and Linux filesystem conventions — everything <see cref="PackageInstaller"/>'s
/// registry- and drive-based detection cannot see, because under Wine there is no GOG/Steam
/// registry key and no <c>C:\Program Files (x86)\Steam</c> at all.
///
/// PURE by design: every method here takes its environment/filesystem as parameters
/// (a <c>getEnvironmentVariable</c> function, a <c>directoryExists</c> predicate, an
/// <c>enumerateDirectories</c> function) rather than calling <see cref="Environment"/> or
/// <see cref="Directory"/> directly, so ProtonPathsTests can drive every branch with fakes —
/// there is no Linux/Wine box in this environment to test any of this against for real (see
/// MEMORY.md's "Autonomous live testing" note: this whole feature is NEEDS-LIVE). Every method
/// also never throws on its own — an enumeration failure is the caller's <c>enumerateDirectories</c>
/// delegate's problem to catch (<see cref="PackageInstaller"/>'s callers wrap
/// <see cref="Directory.GetDirectories(string)"/> exactly the way its existing GOG/Steam walks
/// already do).
///
/// Wine maps the Linux root to <c>Z:\</c>, so every path this class returns is already in that
/// form — a plain Windows path (<see cref="Directory.Exists(string?)"/>, etc) that happens to
/// resolve only when actually running under Wine. On real Windows none of these paths exist
/// (unless a machine happens to have mapped a Z: drive itself), so every candidate is simply
/// not found — harmless, since every result <see cref="PackageInstaller"/> uses this for is
/// re-verified against <c>swkotor.exe</c> before it is ever shown to a player.
/// </summary>
public static class ProtonPaths
{
    /// <summary>Proton sets this to the title's own Linux install directory (e.g.
    /// <c>/home/deck/.local/share/Steam/steamapps/common/swkotor</c>) while the game is
    /// launched through Steam's Proton wrapper. <c>STEAM_COMPAT_DATA_PATH</c> (the compatdata
    /// prefix) and <c>WINEPREFIX</c> are deliberately never read anywhere in this class:
    /// neither ever names the game itself.</summary>
    public const string EnvSteamCompatInstallPath = "STEAM_COMPAT_INSTALL_PATH";

    /// <summary>Wine's fixed mapping of the Linux root to a Windows drive letter.</summary>
    private const string WineRootDrive = "Z:";

    /// <summary>
    /// A POSIX path (as Proton's env vars and Steam's <c>libraryfolders.vdf</c> both use)
    /// rewritten to the Wine form this process can actually pass to
    /// <see cref="Directory.Exists(string?)"/> — <c>/home/deck/x</c> becomes
    /// <c>Z:\home\deck\x</c>. A value that does not start with <c>/</c> is returned unchanged:
    /// Proton's own vars and a Linux Steam client's vdf entries are always POSIX, but this is
    /// also reached with values that are already Windows paths (a hand-set environment
    /// variable while testing on a real Windows box, or a vdf entry from an actual Windows
    /// Steam library), and those must not be mangled. Null or blank input yields null.
    /// </summary>
    public static string? ToWinePath(string? posixOrWindowsPath)
    {
        if (string.IsNullOrWhiteSpace(posixOrWindowsPath))
        {
            return null;
        }

        string path = posixOrWindowsPath.Trim();
        return path.StartsWith('/') ? WineRootDrive + path.Replace('/', '\\') : path;
    }

    /// <summary>The game directory named directly by Proton's own environment, in Wine form —
    /// or null when <see cref="EnvSteamCompatInstallPath"/> is not set (i.e. this is not a
    /// Proton launch at all).</summary>
    public static string? SteamCompatInstallPath(Func<string, string?> getEnvironmentVariable) =>
        ToWinePath(getEnvironmentVariable(EnvSteamCompatInstallPath));

    /// <summary>
    /// Every "home directory" Wine can plausibly be running as, for the enumerations below:
    /// every entry under <c>Z:\home</c> (a normal Linux desktop, or a Steam Deck's "deck"
    /// user), plus <c>Z:\root</c> for a container or CI run as root. <paramref name="directoryExists"/>
    /// and <paramref name="enumerateDirectories"/> stand in for <see cref="Directory"/> so this
    /// never touches a real filesystem in a test; <paramref name="enumerateDirectories"/> is
    /// only ever called after <paramref name="directoryExists"/> has already confirmed
    /// <c>Z:\home</c> exists, so a caller's real-filesystem implementation is only exercised on
    /// an actual Wine prefix.
    /// </summary>
    public static IEnumerable<string> HomeDirectories(
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories)
    {
        const string homeRoot = WineRootDrive + @"\home";
        if (directoryExists(homeRoot))
        {
            foreach (string home in enumerateDirectories(homeRoot))
            {
                yield return home;
            }
        }

        const string rootHome = WineRootDrive + @"\root";
        if (directoryExists(rootHome))
        {
            yield return rootHome;
        }
    }

    /// <summary>The Steam-client root under a home directory, relative to it: a native
    /// install, the client's current data directory, and the Flatpak sandboxed install.</summary>
    private static readonly string[] SteamClientRelativeRoots =
    [
        @".steam\steam",
        @".local\share\Steam",
        @".var\app\com.valvesoftware.Steam\.local\share\Steam",
    ];

    /// <summary>
    /// The Linux Steam-LIBRARY roots reachable from a set of home directories — each one holds
    /// its own <c>steamapps\libraryfolders.vdf</c>, meant to be fed to the same
    /// <c>libraryfolders.vdf</c> parsing <see cref="PackageInstaller.SteamLibraryDirectories"/>
    /// already does for a native Windows Steam install (see
    /// <see cref="PackageInstaller.ProtonSteamLibraryDirectories"/>, which does exactly that).
    /// Covers, under every home directory, the native/current-client and Flatpak install
    /// locations (<see cref="SteamClientRelativeRoots"/>); and, NOT under any home directory at
    /// all, a Steam Deck SD-card library mounted at <c>/run/media/&lt;user&gt;/&lt;card&gt;</c>
    /// (or, without the per-user layer, directly at <c>/run/media/&lt;card&gt;</c>).
    /// </summary>
    public static IEnumerable<string> SteamLibraryRoots(
        IEnumerable<string> homeDirectories,
        Func<string, bool> directoryExists,
        Func<string, IEnumerable<string>> enumerateDirectories)
    {
        foreach (string home in homeDirectories)
        {
            foreach (string relative in SteamClientRelativeRoots)
            {
                string candidate = Path.Combine(home, relative);
                if (directoryExists(candidate))
                {
                    yield return candidate;
                }
            }
        }

        string runMedia = WineRootDrive + @"\run\media";
        if (!directoryExists(runMedia))
        {
            yield break;
        }

        foreach (string entry in enumerateDirectories(runMedia))
        {
            // Directly-mounted card: /run/media/<card>/steamapps.
            if (directoryExists(Path.Combine(entry, "steamapps")))
            {
                yield return entry;
            }

            // The usual per-user layer: /run/media/<user>/<card>/steamapps.
            foreach (string nested in enumerateDirectories(entry))
            {
                if (directoryExists(Path.Combine(nested, "steamapps")))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// GOG-on-Linux library roots via Heroic (default library folder) or Lutris (its own
    /// default install folder) — deliberately modest: just <c>Games\Heroic</c> and
    /// <c>Games</c> under each home directory, walked exactly like the existing "GOG folder"
    /// case in <see cref="PackageInstaller.FindGameDirectories"/> (the root itself, plus every
    /// immediate subdirectory, each checked for <c>swkotor.exe</c>).
    /// </summary>
    public static IEnumerable<string> HeroicLutrisRoots(IEnumerable<string> homeDirectories)
    {
        foreach (string home in homeDirectories)
        {
            yield return Path.Combine(home, "Games", "Heroic");
            yield return Path.Combine(home, "Games");
        }
    }
}

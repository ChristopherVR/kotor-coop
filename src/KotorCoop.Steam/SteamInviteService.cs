namespace KotorCoop.Steam;

/// <summary>
/// The mod's whole relationship with the Steam client: publish a <c>connect</c> string while
/// hosting, and hand back whatever a friend's "Join Game" produces.
///
/// ⚠ OPTIONAL AT EVERY LEVEL, BY CONSTRUCTION. Three independent things can be absent, and
/// none of them is an error:
///
///   1. the player turned it off (<c>steam_invites = 0</c>);
///   2. <c>steam_api64.dll</c> is not beside the agent (the normal case for a GOG install);
///   3. Steam is not running, or the account does not own app 32370.
///
/// Each produces exactly one line and then silence, and the LAN/direct-IP path this mod has
/// always had keeps working untouched. This is not politeness — it is the constraint that
/// makes shipping Steam support acceptable at all, since most of this project's players do not
/// have KOTOR on Steam. See docs/research/steam-support.md §12.
///
/// The publish side uses the same diff-and-repost shape <c>PublicAddressAnnouncer</c> uses:
/// <see cref="Publish"/> is safe to call every tick and only crosses into native code when the
/// string it is given actually changes. Steam rich presence is a network round trip on write,
/// and a host ticking at 30 Hz would otherwise make 30 of them a second saying the same thing.
/// </summary>
public sealed class SteamInviteService : IDisposable
{
    /// <summary>Knights of the Old Republic on Steam. The agent presents as this app so that
    /// friends see "Join Game" on a KOTOR entry rather than on an unknown one.</summary>
    public const int KotorAppId = 32370;

    /// <summary>The file <c>SteamAPI_Init</c> reads to learn which app it is when it was not
    /// launched by Steam.</summary>
    public const string AppIdFileName = "steam_appid.txt";

    private readonly Action<string> _log;
    private readonly int _appId;
    private readonly bool _enabled;

    private SteamNative? _native;
    private bool _attempted;
    private string? _published;

    /// <param name="enabled">coop.ini <c>steam_invites</c>. False makes every member a no-op
    /// and never touches the file system or the native library.</param>
    /// <param name="log">One line per state change. Never called per tick.</param>
    /// <param name="appId">Overridable only so a test can prove the id is threaded through;
    /// production always passes <see cref="KotorAppId"/>.</param>
    public SteamInviteService(bool enabled, Action<string> log, int appId = KotorAppId)
    {
        _enabled = enabled;
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _appId = appId;
    }

    /// <summary>Whether Steam is actually up and usable. False until something has asked for
    /// it and the attempt succeeded.</summary>
    public bool IsActive => _native is { IsReady: true };

    /// <summary>This account's own SteamID64 while <see cref="IsActive"/>, else 0.</summary>
    public ulong SteamId64 => _native?.SteamId ?? 0;

    /// <summary>
    /// Brings Steam up if it is not already, exactly once per process. A failed attempt is
    /// remembered: retrying every tick would mean a `LoadLibrary` per tick for the large
    /// majority of players who will never have the DLL.
    /// </summary>
    /// <returns>Whether Steam is usable now.</returns>
    public bool TryActivate()
    {
        if (!_enabled)
        {
            return false;
        }

        if (_attempted)
        {
            return IsActive;
        }

        _attempted = true;

        SteamNative native = new();
        if (!native.TryLoad(_appId, _log))
        {
            native.Dispose();
            return false;
        }

        _native = native;
        return true;
    }

    /// <summary>
    /// Sets what a friend joins, or clears it with null/empty when this install stops hosting.
    /// Idempotent and cheap: repeated calls with the same value do nothing at all, so the
    /// caller may hand it the current state every tick rather than tracking transitions.
    /// </summary>
    public void Publish(string? connectString)
    {
        if (!_enabled)
        {
            return;
        }

        string normalised = connectString ?? string.Empty;
        if (_published == normalised)
        {
            return;
        }

        // Only worth waking Steam up for the first thing actually worth publishing. A client
        // (which never publishes) therefore never loads the library at all.
        if (normalised.Length > 0 && !TryActivate())
        {
            _published = normalised;
            return;
        }

        _published = normalised;

        if (!IsActive)
        {
            return;
        }

        _native!.SetConnectString(normalised.Length == 0 ? null : normalised);
        _log(normalised.Length == 0
            ? "steam: cleared the join-me presence"
            : $"steam: friends can now join this session ({normalised})");
    }

    /// <summary>
    /// Pumps Steam's callbacks. Does nothing until something has activated the service, so a
    /// player who never hosts and never gets invited pays nothing — a joiner is activated by
    /// <see cref="TryActivate"/> at startup when invites are on, which is what makes an
    /// incoming invite reachable at all.
    /// </summary>
    /// <param name="onJoinRequested">Called with the raw, UNTRUSTED connect string. The caller
    /// validates it (<c>SteamConnectStringRule.TryParse</c>) before it becomes an address.</param>
    public void Pump(Action<string> onJoinRequested)
    {
        if (!IsActive)
        {
            return;
        }

        _native!.Pump(onJoinRequested);
    }

    /// <summary>Invites one friend by SteamID, carrying <paramref name="connectString"/>.
    /// False when Steam is not up or the invite was refused.</summary>
    public bool Invite(ulong steamId, string connectString) =>
        IsActive && _native!.InviteUserToGame(steamId, connectString);

    /// <summary>
    /// Whether a <c>steam_api64.dll</c> is anywhere we would find one — beside the agent
    /// assembly, or in <paramref name="directory"/>.
    ///
    /// Asked BEFORE <see cref="TryEnsureAppIdFile"/> so that a player who will never use Steam
    /// does not get a <c>steam_appid.txt</c> dropped into their game folder. It would be inert,
    /// but it is also a file <c>uninstall</c> would not remove (its rule is "everything
    /// <c>KotorCoop.*</c> is ours"), and leaving litter behind in someone's game directory for
    /// a feature they never switched on is not a trade worth making.
    /// </summary>
    public static bool IsRedistributablePresent(string directory)
    {
        try
        {
            if (File.Exists(Path.Combine(AppContext.BaseDirectory, SteamNative.LibraryName)))
            {
                return true;
            }

            return directory.Length > 0
                && File.Exists(Path.Combine(directory, SteamNative.LibraryName));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Writes <c>steam_appid.txt</c> beside the agent if it is not already there, which is how
    /// <c>SteamAPI_Init</c> identifies an app it did not launch. Best-effort: a read-only game
    /// directory just means Steam support stays off, which is a state the whole design already
    /// handles.
    /// </summary>
    /// <returns>Whether the file exists with the right contents afterwards.</returns>
    public static bool TryEnsureAppIdFile(string directory, int appId, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        try
        {
            string path = Path.Combine(directory, AppIdFileName);
            string wanted = appId.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (File.Exists(path) && File.ReadAllText(path).Trim() == wanted)
            {
                return true;
            }

            File.WriteAllText(path, wanted);
            log($"steam: wrote {AppIdFileName} ({wanted}) beside the agent");
            return true;
        }
        catch (Exception ex)
        {
            log($"steam: could not write {AppIdFileName} ({ex.Message}); invites stay off");
            return false;
        }
    }

    public void Dispose()
    {
        _native?.Dispose();
        _native = null;
    }
}

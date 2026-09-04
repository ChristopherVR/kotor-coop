using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Steam;

/// <summary>
/// The hand-bound slice of the Steamworks flat API this mod needs, resolved lazily out of
/// <c>steam_api64.dll</c> if — and only if — the player put one beside the agent.
///
/// ⚠ NOTHING HAPPENS AT CONSTRUCTION OR AT ASSEMBLY LOAD. Every entry point is resolved on the
/// first <see cref="TryLoad"/>, and a missing DLL, a missing export or a failed init leaves
/// every method here a no-op. That is the whole contract this type exists to keep: a player
/// who owns KOTOR on GOG, or on Steam but without the redistributable, must see one log line
/// and a co-op mod that behaves exactly as it did before Steam support existed. A
/// <c>DllImport</c> would not do — it binds at first call and throws
/// <see cref="DllNotFoundException"/> into whatever happened to call it — so this uses
/// <see cref="NativeLibrary"/> explicitly and holds unmanaged function pointers.
///
/// WHY THE FLAT API AND MANUAL DISPATCH. The C++ SDK delivers callbacks through
/// <c>CCallback</c> templates that a managed binding cannot construct. Valve ships
/// <c>SteamAPI_ManualDispatch_*</c> for exactly this reason: pump the pipe yourself and read
/// a <c>CallbackMsg_t</c> per iteration. That turns the one callback we care about
/// (<see cref="GameRichPresenceJoinRequestedCallback"/>, id 337) into a struct read rather
/// than an interop problem.
///
/// ⚠ NEEDS-LIVE. None of the native calls below have been exercised: this wave had no
/// <c>steam_api64.dll</c> and does not use the rig. The managed side (loading, failing soft,
/// the string codec, the join wiring) is unit-tested; the interop is written from the SDK's
/// documented ABI and is unverified. See docs/research/steam-support.md §16, phase 2.
/// </summary>
public sealed unsafe class SteamNative : IDisposable
{
    /// <summary>The redistributable's file name. Searched beside the agent and then on the
    /// ordinary library search path, so "drop it next to KotorCoop.Agent.exe" works.</summary>
    public const string LibraryName = "steam_api64.dll";

    /// <summary><c>GameRichPresenceJoinRequested_t</c> — <c>k_iSteamFriendsCallbacks</c> (300)
    /// + 37. The one callback this mod subscribes to.</summary>
    public const int GameRichPresenceJoinRequestedCallback = 337;

    /// <summary>
    /// Version-suffixed accessor names for <c>ISteamFriends</c>, newest first. Valve bumps the
    /// suffix when the interface changes and keeps the old accessor exported for a while, so
    /// probing a short list is what lets one build of this mod work across several SDK
    /// versions. Which one bound is logged, because "it silently picked an old one" is exactly
    /// the kind of thing worth seeing in a bug report.
    /// </summary>
    private static readonly string[] FriendsAccessors =
    [
        "SteamAPI_SteamFriends_v018",
        "SteamAPI_SteamFriends_v017",
    ];

    /// <summary>
    /// Version-suffixed accessor names for <c>ISteamUser</c>, newest first, the same reasoning
    /// as <see cref="FriendsAccessors"/>. <c>GetSteamID</c> itself has been stable since the
    /// SDK's earliest public versions, so a build too old to have any of these is not expected
    /// in practice — the probe still exists so a mismatch fails soft (one log line) rather than
    /// refusing to load Steam support outright.
    /// </summary>
    private static readonly string[] UserAccessors =
    [
        "SteamAPI_SteamUser_v023",
        "SteamAPI_SteamUser_v021",
        "SteamAPI_SteamUser_v020",
    ];

    private nint _library;
    private nint _friends;
    private nint _user;

    private delegate* unmanaged[Cdecl]<void> _shutdown;
    private delegate* unmanaged[Cdecl]<int> _getPipe;
    private delegate* unmanaged[Cdecl]<int, void> _runFrame;
    private delegate* unmanaged[Cdecl]<int, CallbackMsg*, byte> _getNextCallback;
    private delegate* unmanaged[Cdecl]<int, void> _freeLastCallback;
    private delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte> _setRichPresence;
    private delegate* unmanaged[Cdecl]<nint, void> _clearRichPresence;
    private delegate* unmanaged[Cdecl]<nint, ulong, byte*, byte> _inviteUserToGame;
    private delegate* unmanaged[Cdecl]<nint, ulong> _getSteamId;

    /// <summary>Whether the library loaded, initialised, and bound everything needed. False
    /// means every other member is a no-op.</summary>
    public bool IsReady { get; private set; }

    /// <summary>This account's own SteamID64 once <see cref="IsReady"/>, else 0. Useful on its
    /// own for a status line — "am I logged in, as whom" — and it is what a player would hand
    /// a friend so the friend can <c>steaminvite</c> back, since this build has no
    /// friends-list/name lookup (see <see cref="UserAccessors"/>'s doc comment).</summary>
    public ulong SteamId { get; private set; }

    /// <summary>
    /// Steam's own callback message header. Layout fixed by the SDK; on x64 the pointer is
    /// 8-aligned, so the two leading <c>int</c>s pack into the first 8 bytes and the trailing
    /// <c>int</c> is followed by 4 bytes of tail padding — 24 bytes total.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CallbackMsg
    {
        public int SteamUser;
        public int Callback;
        public byte* Param;
        public int ParamSize;
    }

    /// <summary>
    /// Loads the redistributable and brings the API up. Returns false — never throws — for
    /// every failure: no DLL, a 32-bit DLL, an SDK too old to export what we need, Steam not
    /// running, or the user not owning app <paramref name="appId"/>. Each of those is a normal
    /// state for a player who is not using this feature, so each produces one line through
    /// <paramref name="log"/> and nothing else.
    /// </summary>
    /// <param name="appId">The Steam app id to present as (32370 for KOTOR). Requires a
    /// <c>steam_appid.txt</c> beside the executable holding the same number, which is how
    /// <c>SteamAPI_Init</c> identifies an app it did not launch itself.</param>
    /// <param name="log">One line per outcome.</param>
    public bool TryLoad(int appId, Action<string> log)
    {
        ArgumentNullException.ThrowIfNull(log);

        if (IsReady)
        {
            return true;
        }

        try
        {
            if (!NativeLibrary.TryLoad(LibraryName, typeof(SteamNative).Assembly, null, out _library))
            {
                log($"steam: {LibraryName} not found beside the agent - Steam invites are off "
                    + "(this is normal for a GOG install; see docs/research/steam-support.md §11)");
                return false;
            }

            if (!TryBind(log))
            {
                Unload();
                return false;
            }

            if (!Initialise(appId, log))
            {
                Unload();
                return false;
            }

            IsReady = true;
            return true;
        }
        catch (Exception ex)
        {
            // Any surprise from the loader or from a mismatched DLL lands here. Steam support
            // going quiet is always preferable to the agent dying: the session it is running
            // has nothing to do with Steam.
            log($"steam: disabled after an unexpected error while loading {LibraryName}: {ex.Message}");
            Unload();
            return false;
        }
    }

    private bool TryBind(Action<string> log)
    {
        // SteamAPI_Init is the one export whose signature changed: SDK 1.59 introduced
        // SteamAPI_InitFlat(char* errMsg) returning an ESteamAPIInitResult, while older SDKs
        // export SteamAPI_Init() returning bool. Both are probed, newest first.
        if (!TryGet("SteamAPI_Shutdown", out nint shutdown, log)
            || !TryGet("SteamAPI_GetHSteamPipe", out nint pipe, log)
            || !TryGet("SteamAPI_ManualDispatch_Init", out _, log)
            || !TryGet("SteamAPI_ManualDispatch_RunFrame", out nint runFrame, log)
            || !TryGet("SteamAPI_ManualDispatch_GetNextCallback", out nint next, log)
            || !TryGet("SteamAPI_ManualDispatch_FreeLastCallback", out nint free, log)
            || !TryGet("SteamAPI_ISteamFriends_SetRichPresence", out nint setPresence, log)
            || !TryGet("SteamAPI_ISteamFriends_ClearRichPresence", out nint clearPresence, log)
            || !TryGet("SteamAPI_ISteamFriends_InviteUserToGame", out nint invite, log)
            || !TryGet("SteamAPI_ISteamUser_GetSteamID", out nint getSteamId, log))
        {
            return false;
        }

        _shutdown = (delegate* unmanaged[Cdecl]<void>)shutdown;
        _getPipe = (delegate* unmanaged[Cdecl]<int>)pipe;
        _runFrame = (delegate* unmanaged[Cdecl]<int, void>)runFrame;
        _getNextCallback = (delegate* unmanaged[Cdecl]<int, CallbackMsg*, byte>)next;
        _freeLastCallback = (delegate* unmanaged[Cdecl]<int, void>)free;
        _setRichPresence = (delegate* unmanaged[Cdecl]<nint, byte*, byte*, byte>)setPresence;
        _clearRichPresence = (delegate* unmanaged[Cdecl]<nint, void>)clearPresence;
        _inviteUserToGame = (delegate* unmanaged[Cdecl]<nint, ulong, byte*, byte>)invite;
        _getSteamId = (delegate* unmanaged[Cdecl]<nint, ulong>)getSteamId;
        return true;
    }

    private bool Initialise(int appId, Action<string> log)
    {
        bool started;

        if (NativeLibrary.TryGetExport(_library, "SteamAPI_InitFlat", out nint initFlat))
        {
            // ESteamAPIInitResult: 0 == k_ESteamAPIInitResult_OK. The out buffer is a
            // SteamErrMsg, a fixed 1024-byte char array the SDK fills with a human-readable
            // reason - worth surfacing verbatim, since "Steam is not running" and "you do not
            // own this app" are different problems with the same symptom.
            byte* message = stackalloc byte[1024];
            message[0] = 0;
            int result = ((delegate* unmanaged[Cdecl]<byte*, int>)initFlat)(message);
            started = result == 0;

            if (!started)
            {
                log($"steam: SteamAPI_Init failed ({result}): {ReadUtf8(message, 1024)}");
            }
        }
        else if (NativeLibrary.TryGetExport(_library, "SteamAPI_Init", out nint init))
        {
            started = ((delegate* unmanaged[Cdecl]<byte>)init)() != 0;

            if (!started)
            {
                log("steam: SteamAPI_Init returned false - Steam is not running, or this "
                    + $"account does not own app {appId}");
            }
        }
        else
        {
            log($"steam: {LibraryName} exports neither SteamAPI_InitFlat nor SteamAPI_Init");
            return false;
        }

        if (!started)
        {
            return false;
        }

        // Manual dispatch must be armed after init and before the first RunFrame.
        if (NativeLibrary.TryGetExport(_library, "SteamAPI_ManualDispatch_Init", out nint dispatchInit))
        {
            ((delegate* unmanaged[Cdecl]<void>)dispatchInit)();
        }

        string? friendsAccessor = null;
        foreach (string accessor in FriendsAccessors)
        {
            if (NativeLibrary.TryGetExport(_library, accessor, out nint get))
            {
                _friends = ((delegate* unmanaged[Cdecl]<nint>)get)();
                if (_friends != 0)
                {
                    friendsAccessor = accessor;
                    break;
                }
            }
        }

        if (friendsAccessor is null)
        {
            log("steam: could not obtain ISteamFriends - the redistributable is a version this "
                + $"build does not know ({string.Join(", ", FriendsAccessors)})");
            _shutdown();
            return false;
        }

        // ISteamUser, the same probe shape. Not fatal on its own if it fails: rich presence and
        // receiving an invite (both driven off ISteamFriends alone) still work without it, so
        // this only costs the SteamID a status line and an explicit steaminvite would report.
        string? userAccessor = null;
        foreach (string accessor in UserAccessors)
        {
            if (NativeLibrary.TryGetExport(_library, accessor, out nint get))
            {
                _user = ((delegate* unmanaged[Cdecl]<nint>)get)();
                if (_user != 0)
                {
                    userAccessor = accessor;
                    break;
                }
            }
        }

        if (userAccessor is not null)
        {
            SteamId = _getSteamId(_user);
            log($"steam: ready as app {appId}, SteamID {SteamId} (ISteamFriends via {friendsAccessor}, "
                + $"ISteamUser via {userAccessor})");
        }
        else
        {
            log($"steam: ready as app {appId} (ISteamFriends via {friendsAccessor}); could not obtain "
                + $"ISteamUser ({string.Join(", ", UserAccessors)}) - steamstatus will not show a SteamID");
        }

        return true;
    }

    private bool TryGet(string name, out nint address, Action<string> log)
    {
        if (NativeLibrary.TryGetExport(_library, name, out address))
        {
            return true;
        }

        log($"steam: {LibraryName} does not export {name} - Steam invites are off "
            + "(the redistributable is too old; SDK 1.51 or newer is needed for manual dispatch)");
        return false;
    }

    /// <summary>
    /// Publishes (or clears, when <paramref name="value"/> is null or empty) the
    /// <c>connect</c> rich-presence key — the string Steam shows a friend a "Join Game" button
    /// for and hands back verbatim when they press it.
    /// </summary>
    public void SetConnectString(string? value)
    {
        if (!IsReady)
        {
            return;
        }

        try
        {
            if (string.IsNullOrEmpty(value))
            {
                _clearRichPresence(_friends);
                return;
            }

            fixed (byte* key = Utf8("connect"))
            fixed (byte* text = Utf8(value))
            {
                _setRichPresence(_friends, key, text);
            }
        }
        catch (Exception)
        {
            // A fault inside Steam's own DLL must not take the session with it.
            IsReady = false;
        }
    }

    /// <summary>Sends a Steam invite carrying <paramref name="connectString"/> to one friend.
    /// Returns false when Steam is not up or refused.</summary>
    public bool InviteUserToGame(ulong steamId, string connectString)
    {
        if (!IsReady || string.IsNullOrEmpty(connectString))
        {
            return false;
        }

        try
        {
            fixed (byte* text = Utf8(connectString))
            {
                return _inviteUserToGame(_friends, steamId, text) != 0;
            }
        }
        catch (Exception)
        {
            IsReady = false;
            return false;
        }
    }

    /// <summary>
    /// Pumps Steam's callback pipe once and reports every join request found, in order. Call
    /// it from the agent's own tick — it is non-blocking and allocates only when a request
    /// actually arrives.
    /// </summary>
    /// <param name="onJoinRequested">Invoked with the connect string the inviting friend
    /// published. Untrusted: it comes from another user's client, and the caller parses it
    /// with <c>SteamConnectStringRule</c> before it becomes an address.</param>
    public void Pump(Action<string> onJoinRequested)
    {
        ArgumentNullException.ThrowIfNull(onJoinRequested);

        if (!IsReady)
        {
            return;
        }

        List<string>? requests = null;

        try
        {
            int pipe = _getPipe();
            if (pipe == 0)
            {
                return;
            }

            _runFrame(pipe);

            CallbackMsg message = default;
            while (_getNextCallback(pipe, &message) != 0)
            {
                if (message.Callback == GameRichPresenceJoinRequestedCallback
                    && message.Param is not null
                    && message.ParamSize >= sizeof(ulong) + 1)
                {
                    // struct GameRichPresenceJoinRequested_t
                    // { CSteamID m_steamIDFriend; char m_rgchConnect[256]; }
                    // The connect string is a NUL-terminated char array 8 bytes in. Bounded by
                    // the reported payload size rather than by trusting the 256 — a short or
                    // hostile payload must not read past what Steam actually handed us.
                    int available = message.ParamSize - sizeof(ulong);
                    string connect = ReadUtf8(message.Param + sizeof(ulong), available);

                    if (connect.Length > 0)
                    {
                        (requests ??= []).Add(connect);
                    }
                }

                // ⚠ ALWAYS free, on every iteration and whatever the callback was. Skipping
                // this on an uninteresting callback wedges the pipe: GetNextCallback keeps
                // returning the same message forever.
                _freeLastCallback(pipe);
            }
        }
        catch (Exception)
        {
            IsReady = false;
            return;
        }

        // Dispatched OUTSIDE the pump loop, so a handler that decides to start a session (and
        // therefore takes its time) is not doing so while Steam's callback pipe is mid-walk.
        if (requests is null)
        {
            return;
        }

        foreach (string request in requests)
        {
            onJoinRequested(request);
        }
    }

    public void Dispose()
    {
        if (IsReady)
        {
            try
            {
                _clearRichPresence(_friends);
                _shutdown();
            }
            catch (Exception)
            {
                // Shutting down is best-effort; the process is going away regardless.
            }
        }

        Unload();
    }

    private void Unload()
    {
        IsReady = false;
        _friends = 0;

        if (_library != 0)
        {
            try
            {
                NativeLibrary.Free(_library);
            }
            catch (Exception)
            {
                // Nothing useful to do; the handle leaks for the life of the process.
            }

            _library = 0;
        }
    }

    private static byte[] Utf8(string value)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(value) + 1];
        Encoding.UTF8.GetBytes(value, bytes);
        return bytes;
    }

    private static string ReadUtf8(byte* start, int maxLength)
    {
        if (start is null || maxLength <= 0)
        {
            return string.Empty;
        }

        int length = 0;
        while (length < maxLength && start[length] != 0)
        {
            length++;
        }

        return length == 0 ? string.Empty : Encoding.UTF8.GetString(start, length);
    }
}

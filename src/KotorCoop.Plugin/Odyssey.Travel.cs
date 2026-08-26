// Odyssey.Travel.cs - module travel, render-window activation, the Bink movie
// player, and writing the cinematic transition queue (the staging instrument).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- module travel -----------------------------------------------------------

    // CServerExoApp::SetMoveToModuleString(CExoString*): assigns the destination module
    // name into CServerExoAppInternal+0x10084 through CExoString::operator= (0x005E5C50),
    // so the engine copies the characters with its own allocator — a stack-local source
    // string is safe. Verified by disassembly against the GOG image (disk == memory).
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, void> SetMoveToModuleStringFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, void>)0x004AECD0;

    // CServerExoApp::SetMoveToModuleStartWaypoint(CExoString*): same shape, +0x1008C.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, void> SetMoveToModuleStartWaypointFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, void>)0x004AED30;

    // CServerExoApp::SetMoveToModulePending(int): plain dword store to +0x10080. The
    // engine's own MainLoop notices it and performs the entire transition.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetMoveToModulePendingFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004AECC0;

    /// <summary>
    /// True when the live module's name equals the given ascii name, ignoring case. Reads
    /// the CServerInfo module-name string (the same one the epoch hashes), so it is only
    /// meaningful while the world is stable.
    /// </summary>
    public static bool ModuleNameEquals(byte* ascii, int length)
    {
        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0)
        {
            return false;
        }

        nint serverInfo = Deref(internalServer + ServerInfoOffset);
        if (serverInfo == 0)
        {
            return false;
        }

        nint text = Deref(serverInfo + ServerInfoModuleName);
        if (text == 0)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            byte live = *(byte*)(text + i);
            byte want = ascii[i];

            // Fold A-Z onto a-z; module names are plain ascii resrefs.
            if ((uint)(live - 'A') <= 'Z' - 'A')
            {
                live += 0x20;
            }

            if ((uint)(want - 'A') <= 'Z' - 'A')
            {
                want += 0x20;
            }

            if (live != want || live == 0)
            {
                return false;
            }
        }

        return *(byte*)(text + length) == 0;
    }

    /// <summary>
    /// Sends this instance to another module through the engine's own warp path: set the
    /// destination string and optional start waypoint, then raise the pending flag. The
    /// engine's MainLoop performs the whole transition with every invariant intact —
    /// party collection, save-out of the old area, loading screen, entry placement —
    /// because this is exactly what the shipped `warp` cheat calls.
    ///
    /// The caller must flush plugin-spawned objects FIRST: the transition stores the
    /// outgoing area, and anything left standing becomes a permanent resident of it.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TravelToModule(
        byte* moduleAscii, int moduleLength, byte* waypointAscii, int waypointLength, out int failure)
    {
        failure = 0;

        nint server = GetServer();
        if (server == 0)
        {
            failure = 1;
            return false;
        }

        if (moduleLength <= 0)
        {
            failure = 2;
            return false;
        }

        ExoString module = new() { CString = (nint)moduleAscii, Length = (uint)moduleLength };
        SetMoveToModuleStringFn(server, &module);

        // Always written, even when empty: a stale waypoint from a previous request would
        // otherwise aim this transition at a tag that does not exist in the destination.
        ExoString waypoint = new()
        {
            CString = waypointLength > 0 ? (nint)waypointAscii : 0,
            Length = (uint)Math.Max(waypointLength, 0),
        };
        SetMoveToModuleStartWaypointFn(server, &waypoint);

        SetMoveToModulePendingFn(server, 1);
        return true;
    }

    /// <summary>
    /// True when the server can hand back a player creature, which is the cheapest
    /// "are we actually in a world" test available.
    /// </summary>
    public static bool HasPlayer
    {
        get
        {
            nint server = GetServer();
            return server != 0 && GetPlayerCreatureFn(server) != 0;
        }
    }

    /// <summary>Reads the raw pause-reason bitmask, or 0 when there is no server yet.</summary>
    public static int GetPauseMask()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        return internalServer == 0 ? 0 : *(byte*)(internalServer + ServerPauseMaskOffset);
    }

    /// <summary>
    /// Clears one pause reason through the engine's own setter, so the world timer is
    /// unpaused and the state change is announced the way the game expects. Returns true
    /// if the reason was set and has now been cleared.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ClearPauseReason(int mask)
    {
        if ((GetPauseMask() & mask) == 0)
        {
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        SetPauseStateFn(server, mask, 0);
        return true;
    }

    // ---- render window activation -------------------------------------------------
    //
    // A launched instance comes up with its render window NEVER ACTIVATED, and draws
    // nothing until the player clicks into it — the reported "one of the two windows is
    // black until I focus it". Measured on the live pair: both instances sat at 0 straight
    // after launch, focusing one took it to 1, and focusing the OTHER left the first at 1.
    // So it is a latch, not a focus mirror: activating once per launch is enough, and
    // nothing turns it off again in normal play.

    /// <summary>Global: nonzero once the render window has been activated.</summary>
    private const nint RenderWindowActiveFlag = 0x007A3A38;

    /// <summary>
    /// Global::ActivateRenderWindow() — __stdcall, NO arguments (0x00401E83 is a plain
    /// `ret`). Sets the flag above, and, when the server exists and is not already running,
    /// clears the unfocused pause reason through SetPauseState(2, 0) — the same reason
    /// FocusKeeper clears. It early-outs when the flag is already set, so calling it
    /// repeatedly is free.
    /// </summary>
    private static readonly delegate* unmanaged[Stdcall]<void> ActivateRenderWindowFn =
        (delegate* unmanaged[Stdcall]<void>)0x00401E00;

    /// <summary>True once this instance's render window has been activated.</summary>
    public static bool IsRenderWindowActive => *(int*)RenderWindowActiveFlag != 0;

    /// <summary>
    /// Activates the render window if it has not been. Returns true when this call did it,
    /// so a caller can log the once-per-launch edge.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool EnsureRenderWindowActive()
    {
        // The function dereferences the app manager; before it exists there is nothing to
        // activate and the call would fault.
        if (Deref(AppManagerPtr) == 0 || IsRenderWindowActive)
        {
            return false;
        }

        ActivateRenderWindowFn();

        return IsRenderWindowActive;
    }

    // ---- cinematics: the Bink movie player ------------------------------------
    //
    // See docs/research/cinematics.md. Everything here is READ-ONLY on purpose: the whole
    // point of these accessors is to let the plugin SAY what the engine did during the
    // seconds it was not being called at all, not to steer the movie player.
    //
    // The load-bearing fact behind them: while a pre-rendered movie plays,
    // CClientExoAppInternal::WaitForMovie (0x005F6F20) spins the MAIN THREAD in a loop at
    // 0x005F7063-0x005F70B5 that runs nothing but Global::messagepump, so
    // CServerExoApp::MainLoop is never reached, GDI SwapBuffers is never called, and NEITHER
    // of the plugin's frame hooks fires. A movie is therefore observable only from its edges.

    /// <summary>
    /// CClientExoApp::IsMoviePlaying 0x005EDB40 — <c>__thiscall</c>, NO arguments.
    ///
    /// Argument size checked against the callee's own <c>ret</c> per docs/research/engine-api.md:
    /// the whole chain is 0x005EDB40 → 0x005F2BD0 → 0x0070C1F0 → 0x00404DD0, and 0x00404DD0 is
    /// literally <c>mov eax,[ecx+0x30]; ret</c> — a bare <c>ret</c>, so the argument size is 0.
    /// Every hop is a pointer load with a null guard at 0x005F2BD8, which is why this is safe
    /// to call on any frame, world or menu.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> IsMoviePlayingFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x005EDB40;

    /// <summary>
    /// <c>CClientExoAppInternal::move_to_module_movies</c> — the <c>CExoArrayList&lt;CExoString&gt;</c>
    /// of movies a module transition is going to play, named in the DB's own offsets table.
    ///
    /// NWScript <c>StartNewModule(module, waypoint, movie1..movie6)</c>
    /// (CSWVirtualMachineCommands::ExecuteCommandStartNewModule 0x00544390) pushes its six
    /// movie arguments into this list through AddMoveToModuleMovie 0x005EDB50, and
    /// CServerExoAppInternal::StartNewModule 0x004BA920 drains it with PlayLoadModuleMovies
    /// 0x005EDB20 while the new module loads. So a non-empty list is the ONE pre-warning the
    /// engine gives that a cinematic is about to take the main thread away.
    /// </summary>
    private const int ClientMoveToModuleMovies = 0x2D8;

    /// <summary>CExoArrayList layout, from the DB: <c>+0x00</c> data, <c>+0x04</c> size,
    /// <c>+0x08</c> capacity.</summary>
    private const int ArrayListData = 0x00;
    private const int ArrayListSize = 0x04;

    /// <summary>CExoString layout, from the DB: <c>+0x00 char*</c>, <c>+0x04</c> length —
    /// and that length INCLUDES the terminating null (see the CExoString length trap), which
    /// is why the reader below cuts at the first null rather than trusting it.</summary>
    private const int ExoStringSize = 0x08;

    /// <summary>
    /// True while the engine's Bink player has a movie up.
    ///
    /// ⚠ In a loaded module this practically never reads true from a frame callback, and that
    /// is the finding rather than a defect: an in-world movie blocks the main thread inside
    /// WaitForMovie, so no callback runs while it is true. It DOES read true at the main menu,
    /// whose looping movie is played asynchronously and is the reason the SwapBuffers frame
    /// hook is menu-dormant (docs/research/menu-frame-hook.md).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool IsMoviePlaying()
    {
        nint client = GetClient();
        return client != 0 && IsMoviePlayingFn(client) != 0;
    }

    /// <summary>
    /// How many movies the pending module transition has queued. Zero in the ordinary case.
    /// A pure read of two pointers deep; no engine call, so it is cheap enough for every frame.
    /// </summary>
    public static int QueuedTransitionMovieCount()
    {
        nint internalClient = GetClientInternal();
        if (internalClient == 0)
        {
            return 0;
        }

        int size = *(int*)(internalClient + ClientMoveToModuleMovies + ArrayListSize);

        // A list this long is not a list, it is a torn or wrong read. Six is the engine's own
        // ceiling (StartNewModule takes exactly six movie arguments); allow a little slack and
        // refuse anything past it rather than walk off into memory below.
        return size is > 0 and <= 16 ? size : 0;
    }

    /// <summary>
    /// Copies the name of queued transition movie <paramref name="index"/> into
    /// <paramref name="destination"/> as ASCII, and returns how many bytes were written.
    ///
    /// Cuts at the first null deliberately: a CExoString's length field counts the terminator,
    /// and trusting it has produced three separate off-by-one bugs in this codebase already.
    /// </summary>
    public static int ReadQueuedTransitionMovie(int index, byte* destination, int capacity)
    {
        if (destination == null || capacity <= 0 || index < 0 || index >= QueuedTransitionMovieCount())
        {
            return 0;
        }

        nint internalClient = GetClientInternal();
        if (internalClient == 0)
        {
            return 0;
        }

        nint data = Deref(internalClient + ClientMoveToModuleMovies + ArrayListData);
        if (data == 0)
        {
            return 0;
        }

        nint text = Deref(data + (index * ExoStringSize));
        if (text == 0)
        {
            return 0;
        }

        int written = 0;
        while (written < capacity)
        {
            byte c = *(byte*)(text + written);
            if (c == 0)
            {
                break;
            }

            destination[written++] = c;
        }

        return written;
    }

    // ---- cinematics: WRITING the transition queue (the staging instrument) -----
    //
    // Everything above this line is read-only. The two calls below are the exception, and
    // they exist for one purpose: to let a live test stage a movie-carrying module
    // transition with nobody at the controls. See docs/research/cinematics.md §7.
    //
    // Read the consequence before calling either of them. A queued transition movie is
    // played by CServerExoAppInternal::StartNewModule 0x004BA920 (via PlayLoadModuleMovies
    // 0x005EDB20), which parks the MAIN THREAD inside WaitForMovie 0x005F6F20 for the length
    // of the film. Neither frame hook fires in that window, so nothing this plugin does
    // happens during it — including the pre-store flush that keeps plugin-spawned bodies out
    // of the saved module. These are therefore guarded by MovieStaging, not called directly.

    /// <summary>
    /// CClientExoApp::AddMoveToModuleMovie 0x005EDB50 — <c>__thiscall</c>, ONE argument
    /// (<c>CExoString*</c>), returning the engine's own verdict.
    ///
    /// Argument size checked against the callee's own <c>ret</c> per docs/research/engine-api.md:
    /// 0x005EDB50 is the usual <c>mov ecx,[ecx+4]; jmp</c> adjustor thunk onto 0x006027A0,
    /// which is <c>mov edx,[esp+4]; lea eax,[ecx+0x2D8]; push eax; push edx;
    /// call 0x005FBBF0; <b>ret 4</b></c> — one 4-byte argument, and EAX falls straight out of
    /// the callee, which is why this is declared as returning <c>int</c> rather than void.
    ///
    /// That callee is `CClientExoAppInternal::AddMovieToExoArrayList 0x005FBBF0`, and its
    /// return value is worth having: it refuses an empty name (`CExoString::operator!=` against
    /// "" at 0x005FBC25, returning 0 at 0x005FBF39) and, for a name containing no <c>:</c>,
    /// builds <c>MOVIES:%s</c> (0x0073D7D8) and tries to open the file — returning 0 and adding
    /// NOTHING when it cannot. So a name this build has no <c>.bik</c> for is refused by the
    /// engine itself, and it tells us so. (That upgrades cinematics.md's INFERRED note about
    /// this guard to VERIFIED, and explains why the retail game gets away with referencing
    /// movies it does not ship.) The add itself is
    /// `CExoArrayList&lt;CExoString&gt;::AddUnique 0x005F7C10`, which copies the string — so
    /// the temporary below can be destroyed immediately.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, int> AddMoveToModuleMovieFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, int>)0x005EDB50;

    /// <summary>
    /// CClientExoApp::RemoveMoveToModuleMovies 0x005EE380 — <c>__thiscall</c>, NO arguments
    /// (the whole function is <c>mov eax,[ecx+4]; mov [eax+0x2DC],0; ret</c> — a bare
    /// <c>ret</c>, so the argument size is 0). It zeroes the array list's SIZE field, which is
    /// exactly what the engine itself does on the way out of WaitForMovie.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> RemoveMoveToModuleMoviesFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005EE380;

    /// <summary>True once the client application object exists. Cheaper and earlier than
    /// <see cref="HasServer"/>: the client is what owns the movie player and the transition
    /// movie queue.</summary>
    public static bool HasClient => GetClient() != 0;

    /// <summary>
    /// Appends one movie name to the engine's move-to-module queue, and returns whether the
    /// ENGINE accepted it — not whether we asked. A refusal means the name is empty or this
    /// install has no such file, and reporting our own optimism instead would produce a
    /// transition with no cinematic and a log claiming otherwise.
    ///
    /// The <c>CExoString</c> is built by the engine's own <c>CStrConstructor 0x005E5A90</c>
    /// and freed by <c>Destructor_2 0x005E5C20</c>, deliberately rather than being assembled
    /// by hand: a hand-built one has to get the length convention right, and a CExoString's
    /// length INCLUDES the terminating null — the trap that has produced three separate bugs
    /// in this codebase already.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool QueueTransitionMovie(byte* ascii, int length)
    {
        nint client = GetClient();
        if (client == 0 || ascii == null || length <= 0 || length > 16)
        {
            return false;
        }

        byte* name = stackalloc byte[24];
        for (int i = 0; i < length; i++)
        {
            name[i] = ascii[i];
        }

        name[length] = 0;

        byte* text = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            text[i] = 0;
        }

        CExoStringCtorFn(text, name);
        int accepted = AddMoveToModuleMovieFn(client, text);
        CExoStringDtorFn(text);

        return accepted != 0;
    }

    /// <summary>
    /// Empties the transition movie queue. The way out of a staging run that is not going to
    /// happen — an armed queue left behind would attach a cinematic to whatever transition
    /// came next, hours later, with nothing in the log to connect the two.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ClearQueuedTransitionMovies()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        RemoveMoveToModuleMoviesFn(client);
        return true;
    }

    /// <summary>The player's own pause toggle. See EngineGlobals on the agent side; the
    /// shared-pause mirror uses this same bit so either player can pause both worlds.</summary>
    public const int PauseReasonPlayer = 0x01;

    // CClientExoApp::SetPauseState(int, int, int) — THREE arguments, not two. ⚠ DO NOT CALL.
    //
    // Its tail walks a notify list only valid on the engine's own call path, and crashes both
    // instances when called off it. The two-argument signature this was once declared with was
    // also WRONG: the implementation (0x005ED650 is a thunk to 0x005F7FA0) ends in `ret 0xc` at
    // 0x005F8285 and reads a third argument at 0x005F822A, so a two-argument call also leaves
    // the stack skewed by four bytes every time. See docs/research/engine-api.md.
    //
    // Left uncalled, but now declared honestly, because a signature kept "for documentation"
    // that documents the wrong thing is worse than none. SetPlayerPause assembles the client
    // half from the individually-verified pieces instead.
#pragma warning disable IDE0051, CS0414
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int, void> ClientSetPauseStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, int, void>)0x005ED650;
#pragma warning restore IDE0051, CS0414

    /// <summary>CClientExoAppInternal + this = the client's own pause-reason bitmask.</summary>
    private const int ClientPauseMaskOffset = 0x1CC;

    /// <summary>Reads the client's pause-reason bitmask, or 0 when there is no client.</summary>
    public static int GetClientPauseMask()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint internalClient = Deref(client + AppClientOffset);
        return internalClient == 0 ? 0 : *(byte*)(internalClient + ClientPauseMaskOffset);
    }

    /// <summary>
    /// Sets or clears a pause reason on BOTH the client and the server, which is what a
    /// real pause does. Pausing one alone does not hold: the two keep separate masks and
    /// separate world timers, and they reconcile against each other every frame.
    ///
    /// Returns true when this call changed something, so a caller can log an edge rather
    /// than every re-assertion.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetPlayerPause(bool paused)
    {
        int value = paused ? 1 : 0;

        bool clientWas = (GetClientPauseMask() & PauseReasonPlayer) != 0;
        bool serverWas = (GetPauseMask() & PauseReasonPlayer) != 0;
        bool changed = clientWas != paused || serverWas != paused;

        // The SERVER half goes through the engine's own setter — it drives the server world
        // timer and the simulation stop, and that entry has run from this plugin for days
        // (FocusKeeper) without incident.
        nint server = GetServer();
        if (server != 0)
        {
            SetPauseStateFn(server, PauseReasonPlayer, value);
        }

        // The CLIENT half must NOT go through CClientExoApp::SetPauseState: a direct call
        // crashes in the notify walk at its tail (0x5F8240..0x5F825C), which iterates a list
        // only valid on the engine's own call path (both instances died at 0x5E9BC9 live — see
        // docs/research/engine-api.md). Everything that call maintains is individually safe
        // with a verified body, so assemble it by hand: the mask byte is a plain flag, the
        // world-timer pause is a one-field function, and the banner/GUI belief has its own
        // setter.
        nint clientInternal = GetClient() == 0 ? 0 : Deref(GetClient() + AppClientOffset);
        if (clientInternal != 0)
        {
            byte* mask = (byte*)(clientInternal + ClientPauseMaskOffset);
            if (paused)
            {
                *mask |= PauseReasonPlayer;
            }
            else
            {
                *mask &= unchecked((byte)~PauseReasonPlayer);
            }
        }

        nint client = GetClient();
        if (client != 0)
        {
            nint timer = ClientGetWorldTimerFn(client);
            if (timer != 0)
            {
                if (paused)
                {
                    PauseWorldTimerFn(timer);
                }
                else
                {
                    UnpauseWorldTimerFn(timer);
                }
            }
        }

        nint gui = GetInGameGui();
        if (gui != 0 && *(int*)(gui + 0x108) != 0)
        {
            SetPauseVisualsFn(gui, value, PauseReasonManual);
        }

        return changed;
    }

    /// <summary>The reason byte whose banner is the plain "PAUSED" (reason 1 would read
    /// "ENEMY SIGHTED!" — the label jump table at 0x6C02BC maps 4 to the default strref).</summary>
    private const int PauseReasonManual = 4;

    /// <summary>True when the player-pause reason is held on either half.</summary>
    public static bool IsPlayerPaused =>
        ((GetPauseMask() | GetClientPauseMask()) & PauseReasonPlayer) != 0;

}

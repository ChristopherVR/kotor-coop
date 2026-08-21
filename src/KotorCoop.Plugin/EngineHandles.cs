namespace KotorCoop.Plugin;

/// <summary>
/// A SMALL, DELIBERATE duplicate of a handful of pointer resolutions <c>Odyssey.cs</c> already
/// has privately, plus two new native calls neither this pass nor any earlier one has needed.
///
/// WHY A SEPARATE FILE, DUPLICATING RATHER THAN REUSING. The instructions for this pass ask
/// that <c>Odyssey.cs</c> not be touched, and every resolution below (the app-manager global,
/// the server/client/module/area offsets, the in-game GUI pointer) is a PRIVATE member there —
/// <c>Odyssey.HasServer</c> and <c>Odyssey.IsInGame</c> are the only public doors into that
/// same chain, and neither hands back the pointer this file's callers need. So this file
/// re-declares the same handful of ALREADY-PUBLISHED addresses (every one of them is also
/// documented in docs/research/ingame-gui-transition.md and cinematics.md) under its own
/// names, rather than editing the file that owns them.
///
/// ⚠ KEEP IN SYNC. If <c>Odyssey.cs</c>'s <c>AppManagerPtr</c>, <c>AppServerOffset</c>,
/// <c>AppClientOffset</c>, <c>ModuleAreaOffset</c> or the in-game-GUI chain ever move, this
/// file has to move with them — the same discipline the codebase already keeps for
/// <c>KotorCoop.MockGame</c>'s <c>GameStateBlock</c> and <c>SharedBlock</c> ("Layout is
/// byte-identical. Keep them in sync.").
/// </summary>
internal static unsafe class EngineHandles
{
    // ---- pointer chain (duplicated from Odyssey.cs's private GetServer/GetClient/
    // GetAreaPointer/GetInGameGui — see the type doc above for why) --------------------

    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;
    private const int AppClientOffset = 0x04;
    private const int ModuleAreaOffset = 0x220;

    /// <summary>CClientExoAppInternal + this = CGuiInGame*.</summary>
    private const int ClientInternalGuiOffset = 0x40;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AE6B0;

    private static nint Deref(nint p) => p == 0 ? 0 : *(nint*)p;

    /// <summary>Range-only pointer sanity — a char buffer need not be 4-aligned, so this is
    /// deliberately looser than a heap-pointer check. Same bound Odyssey.cs's own
    /// LooksLikeDataPointer uses.</summary>
    private static bool LooksLikePointer(nint p) => p is > 0x10000 and < 0x7FFF0000;

    /// <summary>CServerExoApp*, or 0 before a module is loaded.</summary>
    public static nint GetServer()
    {
        nint appManager = Deref(AppManagerPtr);
        return appManager == 0 ? 0 : Deref(appManager + AppServerOffset);
    }

    /// <summary>CClientExoApp*, which exists from startup, menu included.</summary>
    public static nint GetClient()
    {
        nint appManager = Deref(AppManagerPtr);
        return appManager == 0 ? 0 : Deref(appManager + AppClientOffset);
    }

    /// <summary>The server-side area of the loaded module, or 0. Same chain
    /// <c>Odyssey.IsAreaTransitionPending</c> already walks.</summary>
    public static nint GetAreaPointer()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint module = GetModuleFn(server);
        return module == 0 ? 0 : Deref(module + ModuleAreaOffset);
    }

    /// <summary>The live CGuiInGame, or 0 outside a loaded module. Same chain
    /// <c>Odyssey.IsInGame</c> already walks.</summary>
    public static nint GetInGameGui()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint clientInternal = Deref(client + AppClientOffset);
        if (!LooksLikePointer(clientInternal))
        {
            return 0;
        }

        nint gui = Deref(clientInternal + ClientInternalGuiOffset);
        return LooksLikePointer(gui) ? gui : 0;
    }

    // ---- new native calls -------------------------------------------------------------

    /// <summary>
    /// CSWSArea::SetTransitionPending(this, int enable, char id) — VERIFIED signature
    /// (docs/research/cinematics.md §8.2/§8.3a, §"Key addresses"; <c>0x00506B30</c>,
    /// <c>__thiscall</c>). Writes <c>trans_pending</c> at area+0x2C4 — the exact byte
    /// <c>Odyssey.IsAreaTransitionPending</c> reads — and is the SAME call the door
    /// transition's own phase 2 makes to CLEAR the flag once it finishes
    /// (<c>SetTransitionPending(0, 0)</c> at <c>0x0058C1B9</c>).
    ///
    /// Calling it early, mid-fade, to try to abort a transition is INFERRED and NEEDS-LIVE:
    /// nothing in the disassembly read for this pass shows whether phase 2 re-checks the flag
    /// before running or is already unconditionally scheduled once phase 1 starts the fade. It
    /// is SAFE regardless — this writes the identical state the engine itself writes on every
    /// normal transition completion, never a new poke — but it may simply do nothing.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, byte, void> SetTransitionPendingFn =
        (delegate* unmanaged[Thiscall]<nint, int, byte, void>)0x00506B30;

    /// <summary>Best-effort: clears <c>trans_pending</c> on the current area. Returns false
    /// (harmlessly) with no area loaded. See <see cref="SetTransitionPendingFn"/>'s own doc for
    /// what is and is not proven about the effect of calling this mid-transition.</summary>
    public static bool TryClearTransitionPending()
    {
        nint area = GetAreaPointer();
        if (area == 0)
        {
            return false;
        }

        SetTransitionPendingFn(area, 0, 0);
        return true;
    }

    /// <summary>
    /// CGuiInGame::StartGlobalFade(this, int direction, float wait, float length, float* rgb)
    /// — VERIFIED signature (docs/research/cinematics.md §1.6: <c>__thiscall</c>, <c>ret
    /// 0x10</c>, four arguments, forwarded to <c>CSWGuiFade::SetFade 0x006244E0</c>). The ONE
    /// caller read in this codebase's research (<c>MainLoop 0x6031BE</c>, the one-shot
    /// world-reveal fade-IN) does not show its literal arguments in the disassembly excerpts
    /// available, so the DIRECTION value that means "fade to black" rather than "fade from
    /// black" is INFERRED, not read off the binary — <c>FadeDirectionOut</c> below is a best
    /// guess (the lower of the two obvious values) and NEEDS-LIVE verification. Getting it
    /// wrong cannot crash (it is a plain field write inside <c>CSWGuiFade::SetFade</c> with no
    /// pointer dereference on the direction itself) and at worst fades the wrong way or not at
    /// all — which is why this ships gated off by default
    /// (<c>transition_announce_fade</c> in coop.ini) rather than withheld entirely.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, float, float, float*, void> StartGlobalFadeFn =
        (delegate* unmanaged[Thiscall]<nint, int, float, float, float*, void>)0x0062ABF0;

    /// <summary>INFERRED, NEEDS-LIVE — see <see cref="StartGlobalFadeFn"/>.</summary>
    private const int FadeDirectionOut = 0;

    /// <summary>
    /// Best-effort fade to black over <paramref name="lengthSeconds"/>, through the engine's
    /// own global fade — the same mechanism <c>MainLoop</c> uses for the world-reveal fade-in,
    /// run in the opposite (INFERRED) direction. Self-timed: the engine's own <c>wait</c>/
    /// <c>length</c> floats bound the effect, so there is nothing to clean up if the caller
    /// never runs again (unlike input-disable, which is why this pass does not touch input).
    /// Returns false (harmlessly) with no in-game GUI resolved.
    /// </summary>
    public static bool TryFadeToBlack(float lengthSeconds)
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            return false;
        }

        float* black = stackalloc float[3];
        black[0] = 0f;
        black[1] = 0f;
        black[2] = 0f;

        StartGlobalFadeFn(gui, FadeDirectionOut, 0f, lengthSeconds, black);
        return true;
    }
}

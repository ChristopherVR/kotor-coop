namespace KotorCoop.Plugin;

/// <summary>
/// The immediate-play half of the mid-module movie relay (docs/research/trigger-relay.md §6,
/// docs/research/cinematics.md's 2026-08-22 section): calling
/// <c>CClientExoApp::PlayMovie 0x005EDB30</c> directly, on the SAME engine entry
/// <see cref="MoviePlayHook"/> watches, so a peer's own <c>PlayMovie</c> call is REPRODUCED,
/// not approximated by some other route into the movie player.
///
/// A standalone file rather than an <c>Odyssey.cs</c> addition, for the SAME reason
/// <c>OdysseyTriggers.cs</c> and <c>OdysseyEffects.cs</c> already are: several concurrent
/// worktree agents already touch that file, and <c>Odyssey</c> is not <c>partial</c>. The
/// small set of primitives this needs — the app-manager global, the CExoString
/// constructor/destructor — are re-declared here from the SAME already-verified addresses
/// <c>Odyssey.cs</c> documents (<c>AppManagerPtr 0x007A39FC</c>, <c>AppClientOffset 0x04</c>,
/// <c>CStrConstructor 0x005E5A90</c>, <c>Destructor_2 0x005E5C20</c>) — same "one number in
/// two files" discipline every duplicated primitive in this codebase already lives under.
///
/// MAIN THREAD ONLY, and calling <see cref="PlayMovieNow"/> BLOCKS it for the length of the
/// film (docs/research/cinematics.md §1.3). Every caller here MUST gate this behind
/// <see cref="MovieStaging.DecidePlayNow"/> first — this binding performs no engine-state
/// checks of its own, the same division of labour <c>Odyssey.QueueTransitionMovie</c> already
/// has with <see cref="MovieStaging.Decide"/>.
/// </summary>
internal static unsafe class OdysseyMovies
{
    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppClientOffset = 0x04;

    private static nint Deref(nint p) => *(nint*)p;

    /// <summary>Resolves CClientExoApp, which exists from startup, menu included. Duplicated
    /// from <c>Odyssey.GetClient</c> — see the class doc for why.</summary>
    private static nint GetClient()
    {
        nint appManager = Deref(AppManagerPtr);
        return appManager == 0 ? 0 : Deref(appManager + AppClientOffset);
    }

    private static readonly delegate* unmanaged[Thiscall]<byte*, byte*, void> CExoStringCtorFn =
        (delegate* unmanaged[Thiscall]<byte*, byte*, void>)0x005E5A90;

    private static readonly delegate* unmanaged[Thiscall]<byte*, void> CExoStringDtorFn =
        (delegate* unmanaged[Thiscall]<byte*, void>)0x005E5C20;

    /// <summary>
    /// <c>CClientExoApp::PlayMovie 0x005EDB30</c> — a <c>mov ecx,[ecx+4]; jmp</c> thunk onto
    /// <c>CClientExoAppInternal::PlayMovie 0x00602CF0</c> (VERIFIED; see
    /// <see cref="MoviePlayHook"/>'s own header comment for the disassembly).
    /// <c>ret 0x10</c>: FOUR stack arguments.
    ///
    /// Disassembling <c>ExecuteCommandPlayMovie 0x00540ED0</c>'s own call site
    /// (<c>0x00540F87</c>) end to end this pass: arg1 = <c>&amp;name</c> (the CExoString
    /// <c>StackPopString</c> just filled — what this binding writes), arg2 = the address of a
    /// fresh LOCAL the caller allocates on its OWN stack and never reads back afterward (an
    /// out-parameter of whatever <c>PlayMoviesInExoArrayList 0x00602AF0</c> does with it —
    /// INFERRED, not traced past this function). Both real call sites in the binary
    /// (<c>ExecuteCommandPlayMovie</c> and <c>CSWGuiTitleMovies::OnMoviePicked</c> at
    /// <c>0x006DCF5E</c>) always pass a valid local address here, never null, so this binding
    /// does the same rather than guessing null is safe. arg3/arg4: <c>ExecuteCommandPlayMovie</c>
    /// passes <c>(0, 0)</c>; <c>OnMoviePicked</c> passes <c>(0, 1)</c>. This binding reproduces
    /// <c>ExecuteCommandPlayMovie</c>'s own values — the actual NWScript <c>PlayMovie</c>
    /// semantics this whole channel exists to relay, not the menu-gallery variant.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, nint, int, int, void> PlayMovieFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, nint, int, int, void>)0x005EDB30;

    /// <summary>
    /// Plays <paramref name="asciiResRef"/> RIGHT NOW, through the exact engine entry a peer's
    /// own script used — blocking the MAIN THREAD for the length of the film. Returns false
    /// only for a locally-detectable reason (no client, no name, name too long); the engine's
    /// own "no such <c>.bik</c>" refusal is silent here exactly as it is in
    /// <c>Odyssey.QueueTransitionMovie</c>, because <c>PlayMovieFn</c> does not hand its return
    /// value back through the thunk in any form this binding could read (§ see the doc above).
    /// </summary>
    public static bool PlayMovieNow(byte* asciiResRef, int length)
    {
        nint client = GetClient();
        if (client == 0 || asciiResRef == null || length <= 0 || length > 16)
        {
            return false;
        }

        byte* name = stackalloc byte[24];
        for (int i = 0; i < length; i++)
        {
            name[i] = asciiResRef[i];
        }

        name[length] = 0;

        byte* text = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            text[i] = 0;
        }

        // arg2: a fresh local, never read back — see PlayMovieFn's own doc for why a valid
        // address is used rather than null.
        byte* scratch = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            scratch[i] = 0;
        }

        CExoStringCtorFn(text, name);
        PlayMovieFn(client, text, (nint)scratch, 0, 0);
        CExoStringDtorFn(text);

        return true;
    }
}

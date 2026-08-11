using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Records which dialogue reply the local player picks, so a spectating peer can replay the
/// same pick and stay on the same line of the conversation.
///
/// Polling cannot see this. The highlighted slot (<c>CSWGuiDialog+0x1C8A</c>) moves with the
/// mouse and the commit happens between frames, so a frame-sampled read either misses the
/// pick or reports a hover. <c>CSWGuiDialog::SelectReply</c> (0x006A7110) is the single
/// click-reply-N entry — every reply, keyboard or mouse, funnels through it — so hooking it
/// catches exactly the picks and nothing else.
///
/// The detour records and returns; it calls nothing and touches no engine state, so it
/// cannot change the behaviour of the call it is standing in front of.
/// </summary>
internal static unsafe class DialogueReplyHook
{
    /// <summary>
    /// <c>CSWGuiDialog::SelectReply</c> — the commit for the ordinary dialogue panel, and
    /// (through the same address in their vtables) for the two computer panels.
    ///
    /// The five bytes the jump overwrites: <c>mov eax,[ecx+0x6C]</c> then the first two of
    /// <c>mov edx,[esp+4]</c>. Both are position-independent — no rel32, no rip-relative —
    /// so relocating them into the stub is safe.
    /// </summary>
    private static readonly byte[] BasePrologue = [0x8B, 0x41, 0x6C, 0x8B, 0x54];

    /// <summary>
    /// <c>CSWGuiDialogCinematic::SelectReply</c> — a DIFFERENT function, and the reason both
    /// players could still answer.
    ///
    /// ⚠ Hooking only 0x006A7110 covers three of the four dialogue panel classes.
    /// KOTOR's story conversations are the fourth: `CSWGuiDialogCinematic`'s vtable slot
    /// +0x6C is 0x006A7E20, and every commit reaches the panel through
    /// `HandleSelectReply 0x006A7390`'s `call [eax+0x6C]` — `xrefs 0x006A7110` finds zero
    /// direct call sites. So in a letterbox conversation the follower's clicks went straight
    /// past the block and the owner's picks were never captured, which is exactly the
    /// "dialogue is still broken allowing each to click the options" report.
    ///
    /// Its first instruction is the six-byte SEH load <c>mov eax, fs:[0]</c>, so six bytes
    /// are relocated rather than five. Position-independent, and the refusal path returns
    /// before the SEH frame is ever built. Same <c>ret 4</c> as the base.
    /// </summary>
    private static readonly byte[] CinematicPrologue = [0x64, 0xA1, 0x00, 0x00, 0x00, 0x00];

    private const uint MemCommitReserve = 0x1000 | 0x2000;
    private const uint PageExecuteReadWrite = 0x40;

    private static bool _installed;

    /// <summary>The slot last picked, and a counter that ticks on every pick.</summary>
    public static int LastReplySlot { get; private set; }

    public static uint Sequence { get; private set; }

    /// <summary>
    /// While this is in the future, the player's own reply clicks are BLOCKED outright — the
    /// spectator listens, the owner chooses — and nothing is recorded for broadcast.
    ///
    /// ⚠ A LEASE, NOT A FLAG, AND THAT IS THE WHOLE POINT.
    ///
    /// This used to be a plain <c>bool Suppressed</c> re-decided from false at the top of the
    /// spectate controller's frame pass. That is correct only while the frame pass RUNS, and
    /// it does not always run: <c>PluginHost</c>'s frame function returns early above the
    /// spectate controller during an area-transition flush, during a session travel warp, and
    /// for the whole window in which there is no world epoch at all (a loading screen). Across
    /// any of those the flag simply held its last value — and a stuck <c>true</c> is not a
    /// cosmetic bug, because the detour below swallows <c>CSWGuiDialog::SelectReply</c>
    /// outright: the player can never answer ANY conversation again, including their own, and
    /// the dialogue just sits there. Reported from play.
    ///
    /// A lease cannot latch. The controller renews it every frame it holds a peer's
    /// conversation, and if the controller stops running for any reason the gag expires on its
    /// own. The failure it trades into — a spectator who could commit one reply after a frame
    /// stall longer than <see cref="SuppressLeaseMs"/> — is bounded, recoverable and vastly
    /// cheaper than a player who can never talk again.
    ///
    /// ⚠ int, not long, and compared with a wrap-safe unchecked subtraction: a 32-bit store is
    /// naturally atomic on x86, so the detour can never read a torn deadline. (Both sides run
    /// on the game's main thread today — the frame hook is on SwapBuffers and a reply click is
    /// on the input dispatch — but the hook must be correct without depending on that.)
    /// </summary>
    private static volatile int _suppressUntilTick = Environment.TickCount;

    /// <summary>How long one renewal buys. Several frames' worth at any playable frame rate,
    /// and short enough that a controller which has genuinely stopped hands the player their
    /// dialogue back within a couple of seconds.</summary>
    public const int SuppressLeaseMs = 2000;

    /// <summary>Renew the gag for <see cref="SuppressLeaseMs"/>. Called every frame that a
    /// peer holds the session's conversation.</summary>
    public static void SuppressReplies() => _suppressUntilTick = Environment.TickCount + SuppressLeaseMs;

    /// <summary>Hand the replies back, now. Called every frame that no peer holds one, so the
    /// deadline is a live level in both directions rather than something that ages out.</summary>
    public static void AllowReplies() => _suppressUntilTick = Environment.TickCount;

    /// <summary>Whether a reply click is currently refused.</summary>
    public static bool Suppressed => unchecked(_suppressUntilTick - Environment.TickCount) > 0;

    /// <summary>
    /// Set for the duration of our own replay call, so the one pick we dispatch on the
    /// follower's panel passes through the block. Same-thread, same-call-chain: the flag is
    /// read inside the call it brackets.
    /// </summary>
    public static bool AllowReplay { get; set; }

    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        // Both commits are hooked, or the block has a hole the size of every story
        // conversation. The base is required; the cinematic one is reported separately so a
        // future build that moves it is visible rather than silently unguarded.
        if (!InstallOne(0x006A7110, BasePrologue, out string baseDiagnostic))
        {
            diagnostic = baseDiagnostic;
            return false;
        }

        bool cinematic = InstallOne(0x006A7E20, CinematicPrologue, out string cinematicDiagnostic);

        _installed = true;
        diagnostic = cinematic
            ? $"{baseDiagnostic}; cinematic {cinematicDiagnostic}"
            : $"{baseDiagnostic}; ⚠ CINEMATIC NOT HOOKED ({cinematicDiagnostic}) — "
                + "replies in story conversations are unguarded";
        return true;
    }

    private static bool InstallOne(nint target, byte[] prologue, out string diagnostic)
    {
        byte* site = (byte*)target;
        for (int i = 0; i < prologue.Length; i++)
        {
            if (site[i] != prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not hooking 0x{target:X}";
                return false;
            }
        }

        nint resumeAt = target + prologue.Length;

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        // Entered by JMP, so esp still points at the caller's return address and the single
        // stack argument sits at [esp+4]. pushad shifts that by 0x20 -> [esp+0x24].
        //
        // The callback RETURNS whether the call may proceed: 0 blocks it (the spectator's
        // own click while following), nonzero lets it through. The test runs on the raw
        // return value BEFORE popad, and the blocked path pops the registers and returns
        // callee-clean (ret 4) exactly as the real function would, so to the engine a
        // blocked click is indistinguishable from a click that did nothing.
        s[n++] = 0x60;                                       // pushad
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;         // push dword [esp+0x24]  (slot)
        *(int*)(s + n) = 0x24;
        n += 4;

        nint callback = (nint)(delegate* unmanaged[Cdecl]<int, int>)&OnSelectReply;
        s[n++] = 0xE8;                                       // call OnSelectReply -> eax
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04;         // add esp, 4  (cdecl cleanup)
        s[n++] = 0x85; s[n++] = 0xC0;                        // test eax, eax
        s[n++] = 0x74; s[n++] = (byte)(prologue.Length + 6); // jz blocked (skip resume path)
        s[n++] = 0x61;                                       // popad

        for (int i = 0; i < prologue.Length; i++)             // the relocated prologue
        {
            s[n++] = prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp target + prologue
        *(int*)(s + n) = (int)(resumeAt - (stub + n + 4));
        n += 4;

        s[n++] = 0x61;                                       // blocked: popad
        s[n++] = 0xC2; s[n++] = 0x04; s[n++] = 0x00;         // ret 4

        if (!NativeMethods.VirtualProtect(target, (nuint)prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (target + 5));
        for (int i = 5; i < prologue.Length; i++)
        {
            site[i] = 0x90;                                  // pad the remainder of the
        }                                                    //  relocated instruction

        NativeMethods.VirtualProtect(target, (nuint)prologue.Length, old, out _);

        diagnostic = $"0x{target:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs inside the engine's call, before its own body. Decides whether the call may
    /// proceed (nonzero) and records the pick when it is the local player's own. No engine
    /// calls, no allocation, nothing that could throw — this is the game's thread
    /// mid-dispatch. (<see cref="Suppressed"/> reads <c>Environment.TickCount</c>, which is a
    /// bare <c>GetTickCount</c>: no allocation, no managed state, cannot throw.)
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnSelectReply(int slot)
    {
        // ⚠ The body genuinely cannot throw today, and the catch stays anyway: this is a
        // reverse-P/Invoke boundary, so the cost of being wrong about that later is not a
        // logged exception, it is the process. Every entry point in the plugin is fenced by
        // rule rather than by inspection. Letting the call through (1) is the safe default —
        // the worst case is an unrecorded pick, not a frozen conversation.
        try
        {
            if (AllowReplay)
            {
                return 1; // our own replay on the follower's panel: let through, do not record
            }

            if (Suppressed)
            {
                return 0; // a spectator's click: the owner chooses, nobody else
            }

            if (slot >= 0)
            {
                LastReplySlot = slot;
                Sequence++;
            }
        }
        catch
        {
            // Never unwind into the engine's dialogue dispatch.
        }

        return 1;
    }
}

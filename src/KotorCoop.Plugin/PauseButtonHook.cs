using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Gives the mod the HUD pause toggle, because the engine's own one cannot work here.
///
/// <c>CSWGuiMainInterface::OnPauseButtonPressed</c> (0x00688590) decides which way to
/// toggle by asking <c>CClientExoApp::GetPausedByCombat</c> (0x005EDC10) — which despite
/// the name is <c>CServerExoApp::GetPauseState(2)</c>, server pause-mask bit <b>0x02</b>.
///
/// ⚠ THAT IS THE SAME BIT AS THE WINDOW-FOCUS PAUSE, and <see cref="FocusKeeper"/> clears
/// it every 30 frames so an unfocused instance keeps simulating. So the button's oracle
/// always answers "not paused", and every click asks to PAUSE — the second click just
/// re-raises a banner that is already up. Measured live: the shared block's
/// <c>PausesCleared</c> counter went 0 → 1 → 2, exactly one per hourglass click, while both
/// masks stayed at our own 0x01.
///
/// Bit 0x02 cannot simply be adopted: one KOTOR window in a co-op pair is ALWAYS unfocused,
/// so FocusKeeper has to keep clearing it or that instance stops simulating. The mod's
/// private bit 0x01 is the right design; it just has to own the click too — the same
/// arrangement <see cref="PauseKeyController"/> already has for the space bar.
///
/// ⚠ THE TOGGLE IS DEFERRED TO THE NEXT FRAME. <c>Odyssey.SetPlayerPause(false)</c> calls
/// <c>CGuiInGame::SetPaused(0, …)</c>, whose unpause path hides <c>CSWGuiInGamePause</c> —
/// the very panel one of the two hooked handlers belongs to. The engine defers its own
/// toggle through <c>CClientExoAppInternal+0x37C</c> for exactly this reason; so do we.
/// </summary>
internal static unsafe class PauseButtonHook
{
    /// <summary>The HUD hourglass. __stdcall(void* context), <c>ret 4</c> at 0x00688601.
    /// Registered as GUI event 0x27 at 0x0068D115 on the control at
    /// CSWGuiMainInterface+0xC0AC.</summary>
    private const nint HudPauseButton = 0x00688590;

    /// <summary>The PAUSED overlay's own button. __stdcall(void* context), <c>ret 4</c> at
    /// 0x006C03A6. The same body and the same fault.</summary>
    private const nint PanelUnpauseButton = 0x006C0350;

    private const uint MemCommitReserve = 0x1000 | 0x2000;
    private const uint PageExecuteReadWrite = 0x40;

    /// <summary>
    /// <c>mov eax,[esp+4]</c> / <c>mov ecx,[eax+0x4C]</c> / <c>test ecx,ecx</c> — the shared
    /// prologue of both handlers. Five bytes are replaced and four padded; we never call the
    /// original on, so nothing has to be relocated.
    /// </summary>
    private static readonly byte[] Prologue = [0x8B, 0x44, 0x24, 0x04, 0x8B, 0x48, 0x4C, 0x85, 0xC9];

    private static int _requests;
    private static bool _installed;

    /// <summary>Every click the detour saw, including ones the alpha guard rejected. This is
    /// the difference between "the hook never fires" and "the hook fires and the toggle is
    /// wrong", which a synthetic click test could not tell apart.</summary>
    private static int _clicks;

    /// <summary>Clicks rejected because the panel read as faded out.</summary>
    private static int _rejected;

    public static int Clicks => Volatile.Read(ref _clicks);

    public static int Rejected => Volatile.Read(ref _rejected);

    /// <summary>
    /// Consumed once per frame by <see cref="PauseKeyController"/>. Coalescing on purpose: a
    /// mash of clicks inside one frame is one toggle, which is what the engine's single
    /// pending slot does too.
    /// </summary>
    public static bool TakeToggleRequest() => Interlocked.Exchange(ref _requests, 0) != 0;

    /// <summary>
    /// ⚠ This hook SWALLOWS the click — it never calls the stock handler on — so if the
    /// deferred toggle ever fails to fire, the button does NOTHING, which is worse than the
    /// bug it fixes. It was therefore held off by default until proven, and it now is:
    ///
    ///   click #1 hourglass, unpaused  -> "HUD button -> paused",   peer mirrored in 47 ms
    ///   click #2 PAUSED panel         -> "HUD button -> unpaused", peer released in 70 ms
    ///   click #3 hourglass, unpaused  -> "HUD button -> paused",   peer mirrored
    ///   click #4 hourglass, PAUSED    -> "HUD button -> unpaused", peer released in 63 ms
    ///
    /// Both controls, both directions, both edges mirrored. The earlier inconclusive run was
    /// a click that never reached a control at all — the counter this class now publishes is
    /// what separated "the hook never fired" from "the hook fired and the toggle was wrong".
    ///
    /// coop.ini <c>pause_button_hook=0</c> restores the stock (broken) button.
    /// </summary>
    private static readonly bool Wanted = PluginConfig.ReadBool("pause_button_hook", true);

    public static bool Install(out string diagnostic)
    {
        if (!Wanted)
        {
            diagnostic = "pause button hook off (coop.ini pause_button_hook=1 to try it)";
            return false;
        }

        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        nint[] sites = [HudPauseButton, PanelUnpauseButton];

        foreach (nint site in sites)
        {
            byte* p = (byte*)site;
            for (int i = 0; i < Prologue.Length; i++)
            {
                if (p[i] != Prologue[i])
                {
                    diagnostic = $"prologue mismatch at 0x{site:X}+{i} ({p[i]:X2}); pause button not hooked";
                    return false;
                }
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        s[n++] = 0x60;                                    // pushad  (esp -= 0x20)

        // push [esp+0x24] — 0x20 of saved registers plus the 4-byte return address puts the
        // handler's single stack argument (the GUI context) there.
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x24;

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, void>)&OnPauseButtonClicked;
        s[n++] = 0xE8;                                    // call OnPauseButtonClicked
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x04;      // add esp, 4  (cdecl cleanup)
        s[n++] = 0x61;                                    // popad

        // Both handlers are __stdcall with one stack argument, verified from their own
        // epilogues at 0x00688601 and 0x006C03A6. We swallow the click entirely.
        s[n++] = 0xC2; s[n++] = 0x04; s[n++] = 0x00;      // ret 4

        foreach (nint site in sites)
        {
            if (!NativeMethods.VirtualProtect(site, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
            {
                diagnostic = $"VirtualProtect failed at 0x{site:X}";
                return false;
            }

            byte* p = (byte*)site;
            p[0] = 0xE9;                                  // jmp stub
            *(int*)(p + 1) = (int)(stub - (site + 5));

            for (int i = 5; i < Prologue.Length; i++)
            {
                p[i] = 0x90;                              // pad, so nothing reads a half instruction
            }

            NativeMethods.VirtualProtect(site, (nuint)Prologue.Length, old, out _);
        }

        _installed = true;
        diagnostic = $"pause button owned at 0x{HudPauseButton:X} and 0x{PanelUnpauseButton:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Records the click and returns. No engine call, no allocation, no logging: this runs
    /// inside the GUI event dispatch of a panel that the toggle may destroy.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnPauseButtonClicked(nint context)
    {
        try
        {
            Interlocked.Increment(ref _clicks);

            // Both stock handlers refuse a click on a faded-out panel: CSWGuiPanel+0x4C is
            // the panel alpha, and 0.0f reads as an all-zero dword. Keep that guard, or a
            // click landing mid-fade toggles a pause with no banner to show for it.
            if (context == 0 || *(int*)(context + 0x4C) == 0)
            {
                Interlocked.Increment(ref _rejected);
                return;
            }

            Interlocked.Exchange(ref _requests, 1);
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }
}

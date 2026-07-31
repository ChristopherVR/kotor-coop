using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Gives the plugin a per-frame callback that ALSO fires at the main menu, where the
/// <see cref="FrameHook"/> SwapBuffers callback is dormant: the menu plays a looping movie and
/// both back-buffer swaps are gated behind <c>!CClientExoApp::IsMoviePlaying()</c>, so the menu
/// is presented through the Bink path and never hits our hooked import. See
/// docs/research/menu-frame-hook.md.
///
/// <para>The fix is to intercept <c>CClientExoApp::MainLoop 0x005ED7D0</c>, which WinMain calls
/// once per frame at <c>0x004046BD</c> — above and outside the movie gate. That call site is
/// rewritten to jump to a tiny stub that preserves <c>ecx</c> (MainLoop's <c>this</c>), runs our
/// managed callback, then tail-jumps to MainLoop so its return value (the quit flag) still
/// reaches WinMain. Fires at the menu and in-world.</para>
///
/// Used only to drive the menu Co-op button (<see cref="ConnectMenuController"/>); the heavy
/// per-frame work stays on the SwapBuffers hook.
/// </summary>
internal static unsafe class MainLoopHook
{
    private const nint CallSite = 0x004046BD;     // `call 0x005ED7D0` inside WinMain
    private const nint CallSiteNext = 0x004046C2; // CallSite + 5
    private const nint MainLoop = 0x005ED7D0;
    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    private static Action? _onFrame;
    private static bool _installed;

    /// <summary>Rewrites the MainLoop call site to route through our stub. MAIN THREAD ONLY.</summary>
    public static bool Install(Action onFrame, out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        _onFrame = onFrame;
        byte* site = (byte*)CallSite;

        // Refuse unless the site is exactly `call 0x005ED7D0` (E8 + rel32 to MainLoop).
        int expectedRel = (int)(MainLoop - CallSiteNext);
        if (site[0] != 0xE8 || *(int*)(site + 1) != expectedRel)
        {
            diagnostic = "MainLoop call site mismatch; not hooking";
            return false;
        }

        // Stub: push ecx / call callback / pop ecx / jmp MainLoop.
        nint stub = NativeMethods.VirtualAlloc(0, 16, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        nint callback = (nint)(delegate* unmanaged[Cdecl]<void>)&MenuFrame;
        s[0] = 0x51;                                     // push ecx
        s[1] = 0xE8;                                     // call callback
        *(int*)(s + 2) = (int)(callback - (stub + 6));
        s[6] = 0x59;                                     // pop ecx
        s[7] = 0xE9;                                     // jmp MainLoop
        *(int*)(s + 8) = (int)(MainLoop - (stub + 12));

        // Repoint the call site's rel32 at the stub.
        if (!NativeMethods.VirtualProtect(CallSite + 1, (nuint)4, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        *(int*)(site + 1) = (int)(stub - CallSiteNext);
        NativeMethods.VirtualProtect(CallSite + 1, (nuint)4, old, out _);

        _installed = true;
        diagnostic = $"MainLoop call site hooked (stub 0x{stub:X})";
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void MenuFrame()
    {
        try
        {
            _onFrame?.Invoke();
        }
        catch
        {
            // Never let a managed exception unwind into the engine's frame loop.
        }
    }
}

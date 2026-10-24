using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Enhanced's menu hit tests use the OS cursor only while foreground, otherwise falling
/// back to retail's unscaled hit areas. Supply the isolated rig's cursor ONLY during those
/// two synchronous hit-test calls. No window activation, OS cursor move or global API hook.
/// Ordinary play (isolate_real_input=0) always takes the untouched forwarding path.
/// </summary>
internal static unsafe class EnhancedMenuInputBridge
{
    private const nint HitSite = 0x0040B690;
    private const nint PointSite = 0x0040BA20;
    private static delegate* unmanaged[Thiscall]<nint, int, int, int> _hit;
    private static delegate* unmanaged[Thiscall]<nint, int*, int*, void> _point;
    private static delegate* unmanaged[Stdcall]<nint> _foreground;
    private static delegate* unmanaged[Stdcall]<NativeMethods.Point*, int> _cursor;
    [ThreadStatic] private static bool _inHitTest;
    private static bool _installed;

    public static void EnsureInstalled()
    {
        if (_installed || !DirectInputKeyboardHook.IsolateRealInput) return;
        nint renderer = NativeMethods.GetModuleHandle("opengl32.dll");
        if (renderer == 0 || NativeMethods.GetProcAddress(renderer, "KotorEnhancedMouseWheel") == 0) return;
        nint hit = RendererJump(HitSite, renderer), point = RendererJump(PointSite, renderer);
        if (hit == 0 || point == 0) return; // Unrecognized/unfinished renderer hook: leave it alone.
        nint foregroundSlot = ImportSlot(renderer, "GetForegroundWindow");
        nint cursorSlot = ImportSlot(renderer, "GetCursorPos");
        nint user32 = NativeMethods.GetModuleHandle("user32.dll");
        if (foregroundSlot == 0 || cursorSlot == 0 || user32 == 0
            || *(nint*)foregroundSlot != NativeMethods.GetProcAddress(user32, "GetForegroundWindow")
            || *(nint*)cursorSlot != NativeMethods.GetProcAddress(user32, "GetCursorPos")) return;

        // Acquire all write permissions before changing anything. The two engine routines
        // execute on this main thread; this runs in the diagnostic command drain between frames.
        nint[] sites = [foregroundSlot, cursorSlot, HitSite, PointSite];
        nuint[] sizes = [4, 4, 5, 5];
        uint[] old = new uint[4];
        int acquired = 0;
        try
        {
            for (; acquired < sites.Length; acquired++)
                if (!NativeMethods.VirtualProtect(sites[acquired], sizes[acquired], 0x40, out old[acquired])) return;
            _hit = (delegate* unmanaged[Thiscall]<nint, int, int, int>)hit;
            _point = (delegate* unmanaged[Thiscall]<nint, int*, int*, void>)point;
            _foreground = (delegate* unmanaged[Stdcall]<nint>)*(nint*)foregroundSlot;
            _cursor = (delegate* unmanaged[Stdcall]<NativeMethods.Point*, int>)*(nint*)cursorSlot;
            *(nint*)foregroundSlot = (nint)(delegate* unmanaged[Stdcall]<nint>)&Foreground;
            *(nint*)cursorSlot = (nint)(delegate* unmanaged[Stdcall]<NativeMethods.Point*, int>)&Cursor;
            *(int*)(HitSite + 1) = (int)((nint)(delegate* unmanaged[Thiscall]<nint, int, int, int>)&Hit - HitSite - 5);
            *(int*)(PointSite + 1) = (int)((nint)(delegate* unmanaged[Thiscall]<nint, int*, int*, void>)&Point - PointSite - 5);
            NativeMethods.FlushInstructionCache((nint)(-1), HitSite, 5);
            NativeMethods.FlushInstructionCache((nint)(-1), PointSite, 5);
            _installed = true;
            PluginHost.Log("Enhanced: isolated menu hit tests use the engine cursor; OS focus and cursor unchanged");
        }
        finally
        {
            for (int i = acquired - 1; i >= 0; i--)
                NativeMethods.VirtualProtect(sites[i], sizes[i], old[i], out _);
        }
    }

    private static nint RendererJump(nint site, nint renderer)
    {
        if (*(byte*)site != 0xE9) return 0;
        nint target = site + 5 + *(int*)(site + 1), owner = 0;
        return NativeMethods.GetModuleHandleEx(6, target, &owner) && owner == renderer ? target : 0;
    }

    private static nint ImportSlot(nint module, string name)
    {
        // This is a mapped x86 module owned by the Windows loader, not an untrusted PE file.
        int pe = *(int*)(module + 0x3C);
        if (*(uint*)(module + pe) != 0x4550 || *(ushort*)(module + pe + 24) != 0x10B) return 0;
        uint rva = *(uint*)(module + pe + 128);
        if (rva == 0) return 0;
        for (nint desc = module + (int)rva; *(uint*)(desc + 12) != 0; desc += 20)
        {
            uint names = *(uint*)desc, slots = *(uint*)(desc + 16);
            if (names == 0) continue;
            for (int i = 0; ; i += 4)
            {
                uint entry = *(uint*)(module + (int)names + i);
                if (entry == 0) break;
                if ((entry & 0x80000000) == 0 && Marshal.PtrToStringAnsi(module + (int)entry + 2) == name)
                    return module + (int)slots + i;
            }
        }
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static int Hit(nint control, int x, int y)
    {
        bool previous = _inHitTest;
        _inHitTest = DirectInputKeyboardHook.IsolateRealInput;
        try { return _hit(control, x, y); }
        catch { return 0; }
        finally { _inHitTest = previous; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void Point(nint control, int* x, int* y)
    {
        bool previous = _inHitTest;
        _inHitTest = DirectInputKeyboardHook.IsolateRealInput;
        try { _point(control, x, y); }
        catch { }
        finally { _inHitTest = previous; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint Foreground()
    {
        try { if (_inHitTest && Odyssey.GameWindow != 0) return Odyssey.GameWindow; }
        catch { }
        return _foreground();
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Cursor(NativeMethods.Point* point)
    {
        try
        {
            if (_inHitTest)
            {
                if (point == null || !Odyssey.TryGetCursor(out int x, out int y)) return 0;
                NativeMethods.Point screen = new() { X = x, Y = y };
                if (NativeMethods.ClientToScreen(Odyssey.GameWindow, ref screen))
                {
                    *point = screen;
                    return 1;
                }
                return 0; // Never substitute the physical cursor on a failed synthetic conversion.
            }
        }
        catch { return 0; }
        return _cursor(point);
    }
}

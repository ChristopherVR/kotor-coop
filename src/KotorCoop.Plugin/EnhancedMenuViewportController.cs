using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Enhanced generates full-screen GUI roots at startup. Retail's panel draw rejects
/// these roots after switching to a smaller viewport. Permit clipping those specific
/// roots instead: changing their extents breaks Enhanced's managed-GUI hit detection.
/// Child layout/fonts still require a restart for regeneration.
/// </summary>
internal static unsafe partial class EnhancedMenuViewportController
{
    private static int _generatedWidth, _generatedHeight;
    private static bool _attempted;
    private static nint _observedWindow;
    private static int _clientWidth, _clientHeight;
    private static delegate* unmanaged[Stdcall]<uint, int*, void> _getViewport;
    private static delegate* unmanaged[Stdcall]<int, int, int, int, void> _setViewport;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, int* rect);

    /// <summary>
    /// At the end of a rendered menu frame, repair a stale base GL viewport following a
    /// client resize (e.g. removing 4K window decorations). Never substitute the INI size
    /// for a smaller physical client, or interfere with an offset viewport.
    /// </summary>
    public static void OnRenderFrame()
    {
        if (_generatedWidth == 0 || Odyssey.HasServer) return;
        nint window = Odyssey.GameWindow;
        int* client = stackalloc int[4];
        if (window == 0 || !GetClientRect(window, client)) return;
        int width = client[2], height = client[3];
        if (window == _observedWindow && width == _clientWidth && height == _clientHeight) return;
        if (width != Odyssey.ScreenWidth || height != Odyssey.ScreenHeight
            || width is < 640 or > 16384 || height is < 480 or > 16384) return;
        if (_getViewport == null || _setViewport == null)
        {
            nint renderer = NativeMethods.GetModuleHandle("opengl32.dll");
            _getViewport = (delegate* unmanaged[Stdcall]<uint, int*, void>)NativeMethods.GetProcAddress(renderer, "glGetIntegerv");
            _setViewport = (delegate* unmanaged[Stdcall]<int, int, int, int, void>)NativeMethods.GetProcAddress(renderer, "glViewport");
            if (_getViewport == null || _setViewport == null) return;
        }
        int* viewport = stackalloc int[4];
        _getViewport(0x0BA2 /* GL_VIEWPORT */, viewport);
        if (viewport[0] != 0 || viewport[1] != 0 || viewport[2] < 640 || viewport[3] < 480) return;
        if (viewport[2] != width || viewport[3] != height)
        {
            _setViewport(0, 0, width, height);
            PluginHost.Log($"Enhanced: refreshed base GL viewport {viewport[2]}x{viewport[3]} -> {width}x{height} after client resize");
        }
        _observedWindow = window;
        _clientWidth = width;
        _clientHeight = height;
    }

    public static void OnFrame()
    {
        if (_attempted || Odyssey.HasServer) return;
        nint renderer = NativeMethods.GetModuleHandle("opengl32.dll");
        if (renderer == 0 || NativeMethods.GetProcAddress(renderer, "KotorEnhancedMouseWheel") == 0) return;
        int width = Odyssey.ScreenWidth, height = Odyssey.ScreenHeight;
        if (width is < 640 or > 16384 || height is < 480 or > 16384) return;
        nint menu = Odyssey.GetMainMenu();
        if (menu == 0 || !NativeMethods.IsReadable(menu, 20)
            || Odyssey.GetControlExtent(menu) != new MenuColumnRule.Extent(0, 0, width, height)) return;
        _generatedWidth = width;
        _generatedHeight = height;
        _attempted = true;

        // Verify both signed bounds checks. This runs on the game thread between frames.
        ReadOnlySpan<byte> expected = [
            0x0F,0x8F,0xA9,0,0,0, 0x8B,0x56,0x08, 0x8B,0x4E,0x10,
            0x03,0xCA, 0x0F,0xBF,0x50,0x6E, 0x3B,0xCA, 0x0F,0x8F,0x95,0,0,0];
        const nint first = 0x0040B7AF, second = 0x0040B7C3;
        if (!new ReadOnlySpan<byte>((void*)first, expected.Length).SequenceEqual(expected))
        {
            PluginHost.Log("Enhanced: panel bounds signature differs; clipping fix not installed");
            return;
        }
        nint stubs = NativeMethods.VirtualAlloc(0, 64, 0x3000, 0x40);
        if (stubs == 0) return;
        BuildGuard(stubs, first + 6);
        BuildGuard(stubs + 32, second + 6);
        NativeMethods.FlushInstructionCache((nint)(-1), stubs, 64);
        if (!NativeMethods.VirtualProtect(first, 26, 0x40, out uint old))
        {
            NativeMethods.VirtualFree(stubs, 0, 0x8000);
            return;
        }
        *(byte*)first = 0xE9;
        *(int*)(first + 1) = (int)(stubs - first - 5);
        *(byte*)(first + 5) = 0x90;
        *(byte*)second = 0xE9;
        *(int*)(second + 1) = (int)(stubs + 32 - second - 5);
        *(byte*)(second + 5) = 0x90;
        NativeMethods.FlushInstructionCache((nint)(-1), first, 26);
        NativeMethods.VirtualProtect(first, 26, old, out _);
        PluginHost.Log($"Enhanced: clipping startup menu canvas {width}x{height} after mode changes; root hit geometry preserved");
    }

    private static void BuildGuard(nint stub, nint resume)
    {
        byte* p = (byte*)stub;
        p[0] = 0x0F; p[1] = 0x8E; // jle resume: retain the normal in-bounds path
        *(int*)(p + 2) = (int)(resume - stub - 6);
        p[6] = 0x60; // pushad
        p[7] = 0x56; // push esi (panel), stdcall argument
        p[8] = 0xB8;
        *(nint*)(p + 9) = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&CanClip;
        p[13] = 0xFF; p[14] = 0xD0; // call eax
        p[15] = 0x85; p[16] = 0xC0; // test eax,eax
        p[17] = 0x61; // popad preserves the predicate's flags
        p[18] = 0x0F; p[19] = 0x85;
        *(int*)(p + 20) = (int)(resume - stub - 24);
        p[24] = 0xE9;
        *(int*)(p + 25) = (int)(0x0040B85E - stub - 29); // original rejection epilogue
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int CanClip(nint panel)
    {
        try
        {
            if (Odyssey.HasServer) return 0;
            // The engine supplies a live panel. Only Enhanced's original full canvas
            // qualifies; dialogs, gameplay UI and displaced roots retain retail bounds.
            return *(int*)(panel + 4) == 0 && *(int*)(panel + 8) == 0
                && *(int*)(panel + 12) == _generatedWidth
                && *(int*)(panel + 16) == _generatedHeight ? 1 : 0;
        }
        catch { return 0; }
    }
}

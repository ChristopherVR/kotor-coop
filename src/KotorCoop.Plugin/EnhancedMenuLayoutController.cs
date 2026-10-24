using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>Resizes Enhanced's loaded menu trees from immutable startup geometry. The
/// generated files remain the source for future loads; the load hook normalizes those too.</summary>
internal static unsafe class EnhancedMenuLayoutController
{
    private const nint LoadSite = 0x0040B8F0;
    private static nint _renderer;
    private static int _sourceWidth, _sourceHeight, _sourcePercent;
    private static int _width, _height, _percent;
    private static bool _installed, _inLoad, _world;
    private static int _manualScale;
    private static delegate* unmanaged[Thiscall]<nint, void> _load;
    private static delegate* unmanaged[Cdecl]<nint, void> _refresh;
    private static delegate* unmanaged[Thiscall]<nint, byte> _managed;
    private static readonly Dictionary<nint, Panel> Panels = new();
    private sealed record Control(nint Address, nint Vtable, MenuColumnRule.Extent Original);
    private sealed record Panel(nint Vtable, MenuColumnRule.Extent Original, Control[] Controls, bool Full, bool Main);

    [DllImport("kernel32.dll", EntryPoint = "GetPrivateProfileIntW", CharSet = CharSet.Unicode)]
    private static extern uint ReadProfileInt(string section, string key, int fallback, string path);

    public static void OnFrame()
    {
        if (!_installed && !Install()) return;
        if (_inLoad) return;
        bool changed = UpdateDimensions();
        bool world = Odyssey.HasServer, restore = world && !_world;
        _world = world;
        bool fontsChanged = EnhancedMenuFontController.UpdateScale(_percent, restoreForWorld: restore);
        if (world) { Panels.Clear(); return; }
        nint manager = *(nint*)0x007A39F4;
        if (!NativeMethods.IsReadable(manager, 0x90)) return;
        nint array = *(nint*)(manager + 0x88);
        int count = *(int*)(manager + 0x8C);
        if (count is < 1 or > 64 || !NativeMethods.IsReadable(array, (nuint)(count * 4))) return;
        for (int i = 0; i < count; i++)
        {
            nint root = *(nint*)(array + i * 4);
            if (!Panels.TryGetValue(root, out Panel? panel))
            {
                panel = Capture(root);
                if (panel == null) continue;
                Panels[root] = panel;
                Apply(root, panel);
            }
            else if (changed || fontsChanged) Apply(root, panel);
        }
        // A panel pointer is never a lifetime token. Drop removed roots; the load hook
        // replaces snapshots even when a constructor or reload reuses the same address.
        foreach (nint root in Panels.Keys.ToArray())
        {
            bool present = false;
            for (int i = 0; i < count; i++) if (*(nint*)(array + i * 4) == root) { present = true; break; }
            if (!present) Panels.Remove(root);
        }
    }

    private static bool Install()
    {
        if (Odyssey.HasServer) return false;
        nint module = NativeMethods.GetModuleHandle("opengl32.dll");
        if (module == 0 || NativeMethods.GetProcAddress(module, "KotorEnhancedMouseWheel") == 0) return false;
        if (!NativeMethods.IsReadable(module, 0x1000)) return false;
        int pe = *(int*)(module + 0x3C);
        // Private vendor RVAs are only used for this exact supplied x86 build, with
        // instruction signatures and ownership checks in addition to PE identity.
        if (pe is < 0x40 or > 0xF00 || *(uint*)(module + pe) != 0x4550
            || *(ushort*)(module + pe + 4) != 0x14C
            || *(uint*)(module + pe + 8) != 0x6AD934F3 || *(uint*)(module + pe + 80) != 0x1E3000) return false;
        if (!new ReadOnlySpan<byte>((void*)(module + 0x1C30), 10).SequenceEqual<byte>(
                [0x55,0x53,0x57,0x56,0x81,0xEC,0xD4,0,0,0])
            || !new ReadOnlySpan<byte>((void*)(module + 0x2300), 13).SequenceEqual<byte>(
                [0x9C,0x60,0x89,0xE5,0x81,0xEC,0x20,0x02,0,0,0x83,0xE4,0xF0])
            || !new ReadOnlySpan<byte>((void*)(module + 0x160B0), 7).SequenceEqual<byte>(
                [0x55,0x53,0x57,0x56,0x83,0xEC,0x24])
            || *(nint*)(module + 0x1753B8) != LoadSite + 7
            || *(byte*)LoadSite != 0xE9
            || LoadSite + 5 + *(int*)(LoadSite + 1) != module + 0x2300) return false;
        int sw = *(int*)(module + 0x175008), sh = *(int*)(module + 0x17500C), sp = *(int*)(module + 0x175004);
        if (sw is < 640 or > 16384 || sh is < 480 or > 16384 || sp is < 50 or > 450) return false;
        if (!NativeMethods.VirtualProtect(LoadSite, 5, 0x40, out uint old)) return false;
        _renderer = module;
        _sourceWidth = _width = sw; _sourceHeight = _height = sh; _sourcePercent = _percent = sp;
        _load = (delegate* unmanaged[Thiscall]<nint, void>)(module + 0x2300);
        _refresh = (delegate* unmanaged[Cdecl]<nint, void>)(module + 0x1C30);
        _managed = (delegate* unmanaged[Thiscall]<nint, byte>)(module + 0x160B0);
        _manualScale = (int)ReadProfileInt("Enhanced", "MainMenuScalePercent", 0,
            Path.Combine(NativeMethods.GetGameDirectory(), "kotor_enhanced.ini"));
        *(int*)(LoadSite + 1) = (int)((nint)(delegate* unmanaged[Thiscall]<nint, void>)&Loaded - LoadSite - 5);
        NativeMethods.FlushInstructionCache((nint)(-1), LoadSite, 5);
        NativeMethods.VirtualProtect(LoadSite, 5, old, out _);
        _installed = true;
        PluginHost.Log($"Enhanced: live menu layout enabled from {sw}x{sh}, font tier {sp}%");
        return true;
    }

    private static bool UpdateDimensions()
    {
        int w = Odyssey.HasServer ? _sourceWidth : Odyssey.ScreenWidth;
        int h = Odyssey.HasServer ? _sourceHeight : Odyssey.ScreenHeight;
        if (w is < 640 or > 16384 || h is < 480 or > 16384) return false;
        int percent = w == _sourceWidth && h == _sourceHeight ? _sourcePercent : EnhancedMenuLayoutRule.TargetPercent(w, h, _manualScale);
        bool changed = w != _width || h != _height || percent != _percent;
        _width = w; _height = h; _percent = percent;
        SetVendorDimensions(w, h, percent);
        if (changed) PluginHost.Log($"Enhanced: live menu mode {w}x{h}, font tier {percent}%");
        return changed;
    }

    private static void SetVendorDimensions(int w, int h, int percent)
    {
        *(int*)(_renderer + 0x175008) = w;
        *(int*)(_renderer + 0x17500C) = h;
        *(int*)(_renderer + 0x175004) = percent;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvThiscall)])]
    private static void Loaded(nint root)
    {
        try
        {
            _inLoad = true;
            UpdateDimensions();
            Panels.Remove(root);
            if (!Odyssey.HasServer && Capture(root) is { } panel)
            {
                Panels[root] = panel;
                Apply(root, panel);
            }
        }
        catch (Exception ex) { PluginHost.Log("Enhanced: layout load skipped: " + ex.Message); }
        finally { _inLoad = false; }
        _load(root);
    }

    private static Panel? Capture(nint root)
    {
        if (!IsLivePanel(root)) return null;
        nint vtable = *(nint*)root;
        if (!IsSetter(vtable)) return null;
        MenuColumnRule.Extent extent = Odyssey.GetControlExtent(root);
        bool full = extent == new MenuColumnRule.Extent(0, 0, _sourceWidth, _sourceHeight);
        if (!full)
        {
            // The on-disk layout is still at the startup tier. Ask Enhanced to recognize
            // it in that coordinate system before changing any live object.
            int accepted;
            SetVendorDimensions(_sourceWidth, _sourceHeight, _sourcePercent);
            try { accepted = _managed(root); }
            finally { SetVendorDimensions(_width, _height, _percent); }
            if (accepted == 0) return null;
        }
        int count = *(int*)(root + 0x24);
        nint array = *(nint*)(root + 0x20);
        if (count is < 0 or > 512 || (count > 0 && !NativeMethods.IsReadable(array, (nuint)(count * 4)))) return null;
        var controls = new List<Control>(count);
        for (int i = 0; i < count; i++)
        {
            nint control = *(nint*)(array + i * 4);
            if (control == 0) continue;
            if (!NativeMethods.IsReadable(control, 0x5C) || !IsSetter(*(nint*)control)) return null;
            controls.Add(new(control, *(nint*)control, Odyssey.GetControlExtent(control)));
        }
        return new(vtable, extent, controls.ToArray(), full, vtable == 0x00752F70);
    }

    private static bool IsSetter(nint vtable)
    {
        if (vtable < 0x00730000 || vtable > 0x00790000 || !NativeMethods.IsReadable(vtable, 8)) return false;
        nint setter = *(nint*)(vtable + 4);
        return setter >= 0x00400000 && setter < 0x00730000;
    }

    private static bool IsLivePanel(nint root)
    {
        if (!NativeMethods.IsReadable(root, 0x60) || *(nint*)root == 0x0073E010) return false;
        // StopLoadFromLayout is also called by the base destructor AFTER deleting this
        // background and the embedded controls. Never treat that call as a fresh load.
        nint background = *(nint*)(root + 0x5C);
        return NativeMethods.IsReadable(background, 8) && IsSetter(*(nint*)background);
    }

    private static void Apply(nint root, Panel panel)
    {
        if (!IsLivePanel(root) || *(nint*)root != panel.Vtable) return;
        // Validate every child before mutating the tree, not just the panel address.
        foreach (Control control in panel.Controls)
            if (!NativeMethods.IsReadable(control.Address, 0x5C) || *(nint*)control.Address != control.Vtable) return;
        MenuColumnRule.Extent extent = panel.Full ? new(0, 0, _width, _height)
            : EnhancedMenuLayoutRule.ResizeDialogControl(panel.Original, _sourcePercent, _percent);
        SetExtent(root, extent);
        foreach (Control control in panel.Controls)
        {
            MenuColumnRule.Extent e = control.Original;
            if (e.Width <= 0 || e.Height <= 0) continue;
            if (panel.Main)
                e = e == panel.Original ? new(0, 0, _width, _height)
                    : EnhancedMenuLayoutRule.ResizeMainControl(e, _sourceWidth, _sourceHeight,
                        _sourcePercent, _width, _height, _percent,
                        control.Address != root + 0x64 && control.Address != root + 0xCC4);
            else if (panel.Full)
                e = EnhancedMenuLayoutRule.ResizeFullCanvasControl(e, _sourceWidth, _sourceHeight, _width, _height);
            else e = EnhancedMenuLayoutRule.ResizeDialogControl(e, _sourcePercent, _percent);
            SetExtent(control.Address, e);
            if (control.Vtable == 0x0073E658) EnhancedMenuFontController.TryRewrap(control.Address + 0x154);
            else if (control.Vtable == 0x0073E5B8) EnhancedMenuFontController.TryRewrap(control.Address + 0xD0);
        }
        _refresh(root);
    }

    private static void SetExtent(nint control, MenuColumnRule.Extent extent)
    {
        int* rect = stackalloc int[4] { extent.Left, extent.Top, extent.Width, extent.Height };
        ((delegate* unmanaged[Thiscall]<nint, int*, void>)*(nint*)(*(nint*)control + 4))(control, rect);
    }
}


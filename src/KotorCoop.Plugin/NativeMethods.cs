using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

internal static partial class NativeMethods
{
    public const uint MemCommit = 0x1000;
    public const uint MemReserve = 0x2000;
    public const uint PageReadWrite = 0x04;

    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint LoadLibrary(string path);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint GetProcAddress(nint module, string name);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? name);

    public const uint GetModuleHandleExFlagFromAddress = 0x4;
    public const uint GetModuleHandleExFlagUnchangedRefcount = 0x2;

    /// <summary>
    /// With <see cref="GetModuleHandleExFlagFromAddress"/>, resolves the module that contains
    /// a code address — the only way THIS DLL can learn its own handle, since it may not be
    /// called dinput8.dll at all when a chain loader owns that name (see Exports).
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool GetModuleHandleEx(uint flags, nint address, nint* module);

    // Raw pointers rather than char[]: array parameters would require the runtime
    // marshalling layer, which a NativeAOT library deliberately does without.
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true)]
    public static unsafe partial uint GetModuleFileName(nint module, char* buffer, uint size);

    /// <summary>
    /// In a 32-bit process on 64-bit Windows this returns SysWOW64, which is where the
    /// real 32-bit dinput8.dll lives. Hardcoding the path would be wrong on 32-bit
    /// Windows and fragile everywhere else.
    /// </summary>
    [LibraryImport("kernel32.dll", EntryPoint = "GetSystemDirectoryW", SetLastError = true)]
    public static unsafe partial uint GetSystemDirectory(char* buffer, uint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint VirtualAlloc(nint address, nuint size, uint allocationType, uint protect);

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll")]
    public static partial nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    /// <summary>
    /// Whether an address is committed and readable RIGHT NOW. The plugin runs inside the
    /// game, so a bad dereference is an access violation in the game's own process — this
    /// is how a pointer that came from a researched offset gets checked before it is
    /// handed to an engine function that does no checking of its own.
    ///
    /// Not a correctness proof: readable memory can still hold the wrong thing. It only
    /// rules out the wild-pointer case, which is the one that crashes.
    /// </summary>
    public static bool IsReadable(nint address)
    {
        if (address <= 0 || address >= 0x7FFF_0000)
        {
            return false;
        }

        nuint size = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
        if (VirtualQuery(address, out MemoryBasicInformation info, size) == 0)
        {
            return false;
        }

        const uint memCommit = 0x1000;
        const uint pageGuard = 0x100;
        const uint pageNoAccess = 0x01;
        const uint readableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

        return info.State == memCommit
            && (info.Protect & (pageGuard | pageNoAccess)) == 0
            && (info.Protect & readableMask) != 0;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentProcessId();

    /// <summary>Virtual-key codes used by the console overlay's edge detection.</summary>
    public const int VkReturn = 0x0D;

    /// <summary>The pause key; the plugin owns it (see PauseKeyController).</summary>
    public const int VkSpace = 0x20;

    /// <summary>Page Up / Page Down, read-only, scroll the chat overlay's history window.</summary>
    public const int VkPageUp = 0x21;
    public const int VkPageDown = 0x22;

    /// <summary>Server-browser panel navigation.</summary>
    public const int VkEscape = 0x1B;
    public const int VkUp = 0x26;
    public const int VkDown = 0x28;
    public const int VkF9 = 0x78;

    /// <summary>Tab, edge-detected while the ~ console is open, cycles verb completion.</summary>
    public const int VkTab = 0x09;

    /// <summary>
    /// Physical key state, independent of the message queue. The high bit is set while the
    /// key is down. Reads no engine state and cannot affect the game.
    ///
    /// ⚠ It is also independent of FOCUS: it reports the keyboard, not this window's input.
    /// With two instances on one machine that means every instance sees every keypress, so
    /// pressing Enter in one game opened the chat prompt in BOTH. Always gate these reads on
    /// <see cref="IsForeground"/>.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int virtualKey);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    /// <summary>
    /// True when the foreground window belongs to THIS game process — i.e. keystrokes the
    /// player is typing are meant for us. The single guard that keeps two instances on one
    /// machine from both reacting to the same key.
    /// </summary>
    public static bool IsForeground()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
        {
            return false;
        }

        _ = GetWindowThreadProcessId(window, out uint processId);
        return processId == GetCurrentProcessId();
    }

    /// <summary>Key-is-down, but only while this instance has the keyboard.</summary>
    public static bool IsKeyDownHere(int virtualKey) =>
        IsForeground() && (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    // ── Mouse emulation for the gamepad (GamepadController) ─────────────────────────────
    // The engine takes its cursor and clicks from ordinary window messages, position in
    // lParam (docs/research/mouse-cursor-input.md §"Recipe A"), so a pad drives the cursor by
    // posting the same messages a mouse would. PostMessage (not Send): it lands on the game's
    // own PeekMessage pump next frame, exactly like real input, with no re-entrancy.

    public const uint WmMouseMove = 0x0200;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmRButtonDown = 0x0204;
    public const uint WmRButtonUp = 0x0205;
    public const uint WmMouseWheel = 0x020A;
    public const uint MkLButton = 0x0001;
    public const uint MkRButton = 0x0002;
    public const int WheelDelta = 120;

    [LibraryImport("user32.dll", EntryPoint = "PostMessageA")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetCursorPos(int x, int y);

    [StructLayout(LayoutKind.Sequential)]
    public struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ClientToScreen(nint window, ref Point point);

    /// <summary>Packs client coordinates the way every mouse message carries them.</summary>
    public static nint MouseLParam(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    public static unsafe string GetSystemDirectoryPath()
    {
        char* buffer = stackalloc char[512];
        uint length = GetSystemDirectory(buffer, 512);
        return length == 0 ? @"C:\Windows\System32" : new string(buffer, 0, (int)length);
    }

    /// <summary>
    /// The game directory: where swkotor.exe itself lives. This is deliberately NOT "the
    /// directory of dinput8.dll" — when a chain loader (src/KotorCoop.Loader) owns the
    /// dinput8.dll name this plugin is loaded under another file name, and the executable's
    /// own directory is the one thing that is the same in every layout. coop.ini, the logs,
    /// the agent and the saves folder are all found relative to it.
    /// </summary>
    public static string GetGameDirectory() => GetModulePathDirectory(GetModuleHandle(null));

    /// <summary>Full path of the module that contains <paramref name="codeAddress"/>, or
    /// null. Pass the address of one of this DLL's own exports to find this DLL.</summary>
    public static unsafe string? GetModulePathContaining(nint codeAddress)
    {
        nint module = 0;
        if (!GetModuleHandleEx(GetModuleHandleExFlagFromAddress | GetModuleHandleExFlagUnchangedRefcount, codeAddress, &module) || module == 0)
        {
            return null;
        }

        return GetModulePath(module);
    }

    public static unsafe string? GetModulePath(nint module)
    {
        if (module == 0)
        {
            return null;
        }

        char* buffer = stackalloc char[512];
        uint length = GetModuleFileName(module, buffer, 512);
        return length == 0 ? null : new string(buffer, 0, (int)length);
    }

    private static string GetModulePathDirectory(nint module)
    {
        string? path = GetModulePath(module);
        if (path is null)
        {
            return ".";
        }

        int lastSlash = path.LastIndexOf('\\');
        return lastSlash > 0 ? path[..lastSlash] : ".";
    }
}

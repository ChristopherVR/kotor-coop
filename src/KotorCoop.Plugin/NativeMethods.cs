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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool VirtualFree(nint address, nuint size, uint freeType);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlushInstructionCache(nint process, nint address, nuint size);

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

    /// <summary>Key-is-down, but only while this instance has the keyboard. On an isolated rig
    /// (coop.ini <c>isolate_real_input</c>) a key held through the <c>input key ... down</c>
    /// verb also counts, so the plugin's own polled keys (Enter to chat, the pause key, F9,
    /// console Tab) can be driven unattended; the rig's windows are never foreground.</summary>
    public static bool IsKeyDownHere(int virtualKey) =>
        (IsForeground() && (GetAsyncKeyState(virtualKey) & 0x8000) != 0)
        || DirectInputKeyboardHook.IsHeldByRig(DikFor(virtualKey));

    /// <summary>The DirectInput scan code for the virtual keys <see cref="IsKeyDownHere"/> is
    /// asked about; 0 for any other.</summary>
    private static byte DikFor(int virtualKey) => virtualKey switch
    {
        VkReturn => 0x1C,
        VkSpace => 0x39,
        VkEscape => 0x01,
        VkTab => 0x0F,
        VkUp => 0xC8,
        VkDown => 0xD0,
        VkPageUp => 0xC9,
        VkPageDown => 0xD1,
        VkF9 => 0x43,
        _ => 0,
    };

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

    // ── Character emulation for the gamepad on-screen keyboard (GamepadController.TextEntry) ──
    // The ~ console and the address/password editbox both take typed characters through
    // Win32 WM_CHAR (docs/research/connect-flow.md, docs/research/console-open-safe.md) — a
    // DIFFERENT path from the buffered DirectInput read the rest of this layer uses, so those
    // two boxes cannot be "typed into" via DirectInputKeyboardHook at all. Posting WM_CHAR is
    // the same class of trick as the mouse messages above: PostMessage (not Send) from inside
    // this process, landing on the game's own PeekMessage pump next frame like real input.
    // wParam is the ANSI character code (PostMessageA, same as every mouse post above), which
    // is exactly what DoConsole's `bl = the typed char` and the editbox's AddNewChar read.
    public const uint WmChar = 0x0102;
    public const byte AsciiBackspace = 0x08;
    public const byte AsciiEscape = 0x1B;
    public const byte AsciiCarriageReturn = 0x0D;

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

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
    public static partial bool VirtualProtect(nint address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentProcessId();

    /// <summary>Virtual-key codes used by the console overlay's edge detection.</summary>
    public const int VkReturn = 0x0D;

    /// <summary>Page Up / Page Down, read-only, scroll the chat overlay's history window.</summary>
    public const int VkPageUp = 0x21;
    public const int VkPageDown = 0x22;

    /// <summary>
    /// Physical key state, independent of the message queue. The high bit is set while the
    /// key is down. Used only to detect the Enter that submits a console line — reads no
    /// engine state and cannot affect the game.
    /// </summary>
    [LibraryImport("user32.dll")]
    public static partial short GetAsyncKeyState(int virtualKey);

    public static unsafe string GetSystemDirectoryPath()
    {
        char* buffer = stackalloc char[512];
        uint length = GetSystemDirectory(buffer, 512);
        return length == 0 ? @"C:\Windows\System32" : new string(buffer, 0, (int)length);
    }

    public static unsafe string GetModuleDirectory(string moduleName)
    {
        nint module = GetModuleHandle(moduleName);
        if (module == 0)
        {
            return ".";
        }

        char* buffer = stackalloc char[512];
        uint length = GetModuleFileName(module, buffer, 512);
        if (length == 0)
        {
            return ".";
        }

        string path = new(buffer, 0, (int)length);
        int lastSlash = path.LastIndexOf('\\');
        return lastSlash > 0 ? path[..lastSlash] : ".";
    }
}

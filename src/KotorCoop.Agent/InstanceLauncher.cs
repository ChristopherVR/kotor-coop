using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Agent;

/// <summary>
/// Starts a second (third, ...) copy of KOTOR alongside one that is already running.
///
/// The retail game allows exactly one instance, enforced at 0x00404222: it calls
/// CreateMutexA with the name "swkotor", does a zero-timeout WaitForSingleObject on the
/// handle, and returns -1 when another process already owns it. That single -1 is the
/// entire guard.
///
/// The fix is to give the second instance a different mutex name. The process is created
/// suspended, the name string at 0x0073D75C is rewritten before a single instruction of
/// game code runs, and then the main thread is resumed. Verified against this build: that
/// address is referenced from exactly one place in .text - the guard's own push - so
/// renaming it changes nothing else. No game code is patched and no file on disk is
/// modified.
///
/// This is the GOG (DRM-free) build. The Steam copy additionally refuses to start a second
/// time through its own DRM wrapper, which is why co-op testing needs this build.
/// </summary>
public static partial class InstanceLauncher
{
    /// <summary>Address of the ASCII mutex name "swkotor" in .rdata. No ASLR, so absolute.</summary>
    private const nint MutexNameAddress = 0x0073D75C;

    /// <summary>The stock name. A replacement must be at most this long to fit in place.</summary>
    private const string StockMutexName = "swkotor";

    private const uint CreateSuspended = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2;
        public nint Reserved3;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(
        string? applicationName,
        string? commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool WriteProcessMemory(
        nint process, nint address, byte* buffer, nuint size, out nuint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool ReadProcessMemory(
        nint process, nint address, byte* buffer, nuint size, out nuint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtectEx(
        nint process, nint address, nuint size, uint newProtect, out uint oldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    /// <summary>
    /// Launches the game with a per-instance mutex name so it coexists with a running copy.
    /// Returns the new process, or null with a diagnostic on failure.
    /// </summary>
    public static unsafe Process? Launch(string gamePath, string mutexName, out string diagnostic)
    {
        diagnostic = string.Empty;

        if (!File.Exists(gamePath))
        {
            diagnostic = $"game executable not found: {gamePath}";
            return null;
        }

        if (mutexName.Length == 0 || mutexName.Length > StockMutexName.Length)
        {
            diagnostic =
                $"mutex name must be 1..{StockMutexName.Length} characters to fit in place, got '{mutexName}'";
            return null;
        }

        string workingDirectory = Path.GetDirectoryName(gamePath)!;

        StartupInfo startup = new() { Cb = (uint)Marshal.SizeOf<StartupInfo>() };

        if (!CreateProcess(
                gamePath, null, 0, 0, false, CreateSuspended, 0, workingDirectory,
                ref startup, out ProcessInformation info))
        {
            diagnostic = $"CreateProcess failed: {Marshal.GetLastWin32Error()}";
            return null;
        }

        try
        {
            if (!TryRenameMutex(info.Process, mutexName, out string renameError))
            {
                // Never resume a process whose guard is intact: it would exit -1 anyway,
                // and a half-patched instance is worse than none.
                TerminateProcess(info.Process, 1);
                diagnostic = renameError;
                return null;
            }

            if (ResumeThread(info.Thread) == unchecked((uint)-1))
            {
                TerminateProcess(info.Process, 1);
                diagnostic = $"ResumeThread failed: {Marshal.GetLastWin32Error()}";
                return null;
            }

            diagnostic = $"launched pid {info.ProcessId} with mutex name '{mutexName}'";
            return Process.GetProcessById((int)info.ProcessId);
        }
        finally
        {
            CloseHandle(info.Thread);
            CloseHandle(info.Process);
        }
    }

    private static unsafe bool TryRenameMutex(nint process, string mutexName, out string error)
    {
        error = string.Empty;

        // The image is mapped by CreateProcess before the first instruction runs, so the
        // string is already there to read even though the process is suspended.
        byte[] existing = new byte[StockMutexName.Length + 1];

        fixed (byte* buffer = existing)
        {
            if (!ReadProcessMemory(process, MutexNameAddress, buffer, (nuint)existing.Length, out _))
            {
                error = $"could not read the mutex name: {Marshal.GetLastWin32Error()}";
                return false;
            }
        }

        // Refuse rather than corrupt something if this is not the build we mapped. A wrong
        // write here lands in the middle of .rdata.
        string found = Encoding.ASCII.GetString(existing, 0, StockMutexName.Length);
        if (found != StockMutexName || existing[^1] != 0)
        {
            error =
                $"expected the mutex name '{StockMutexName}' at 0x{MutexNameAddress:X} but found "
                + $"'{found}'; this is not the expected KOTOR build";
            return false;
        }

        byte[] replacement = new byte[StockMutexName.Length + 1];
        Encoding.ASCII.GetBytes(mutexName, replacement);

        if (!VirtualProtectEx(
                process, MutexNameAddress, (nuint)replacement.Length,
                MemoryConstants.PageReadWrite, out uint oldProtect))
        {
            error = $"VirtualProtectEx failed: {Marshal.GetLastWin32Error()}";
            return false;
        }

        bool written;
        fixed (byte* buffer = replacement)
        {
            written = WriteProcessMemory(
                process, MutexNameAddress, buffer, (nuint)replacement.Length, out _);
        }

        int writeError = Marshal.GetLastWin32Error();
        VirtualProtectEx(process, MutexNameAddress, (nuint)replacement.Length, oldProtect, out _);

        if (!written)
        {
            error = $"WriteProcessMemory failed: {writeError}";
            return false;
        }

        return true;
    }

    private static class MemoryConstants
    {
        public const uint PageReadWrite = 0x04;
    }
}

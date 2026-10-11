using System.Runtime.InteropServices;

namespace KotorCoop.Memory;

[StructLayout(LayoutKind.Sequential)]
internal struct MemoryBasicInformation
{
    public nint BaseAddress;
    public nint AllocationBase;
    public uint AllocationProtect;
    public uint Alignment1;
    public nuint RegionSize;
    public uint State;
    public uint Protect;
    public uint Type;
    public uint Alignment2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ModuleInformation
{
    public nint BaseOfDll;
    public uint SizeOfImage;
    public nint EntryPoint;
}

internal static class ProcessAccess
{
    public const uint QueryInformation = 0x0400;
    public const uint VmOperation = 0x0008;
    public const uint VmRead = 0x0010;
    public const uint VmWrite = 0x0020;

    public const uint ReadWrite = QueryInformation | VmOperation | VmRead | VmWrite;
    public const uint ReadOnly = QueryInformation | VmRead;
}

internal static class MemoryConstants
{
    public const uint MemCommit = 0x1000;

    public const uint MemPrivate = 0x20000;
    public const uint MemMapped = 0x40000;
    public const uint MemImage = 0x1000000;

    public const uint PageNoAccess = 0x01;
    public const uint PageReadOnly = 0x02;
    public const uint PageReadWrite = 0x04;
    public const uint PageWriteCopy = 0x08;
    public const uint PageExecuteRead = 0x20;
    public const uint PageExecuteReadWrite = 0x40;
    public const uint PageExecuteWriteCopy = 0x80;
    public const uint PageGuard = 0x100;

    /// <summary>Modules of every bitness, so an x64 host can enumerate a WOW64 target.</summary>
    public const uint ListModulesAll = 0x03;
}

internal static partial class NativeMethods
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool ReadProcessMemory(
        nint process,
        nint baseAddress,
        byte* buffer,
        nuint size,
        out nuint bytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool WriteProcessMemory(
        nint process,
        nint baseAddress,
        byte* buffer,
        nuint size,
        out nuint bytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nuint VirtualQueryEx(
        nint process,
        nint address,
        out MemoryBasicInformation buffer,
        nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWow64Process(nint process, out int wow64Process);

    // psapi: EnumProcessModules fails against a 32-bit target from a 64-bit caller,
    // so the Ex variant with LIST_MODULES_ALL is used instead of Process.Modules.
    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool EnumProcessModulesEx(
        nint process,
        nint* moduleArray,
        uint arraySizeBytes,
        out uint bytesNeeded,
        uint filterFlag);

    [LibraryImport("psapi.dll", EntryPoint = "GetModuleFileNameExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetModuleFileNameEx(
        nint process,
        nint module,
        Span<char> fileName,
        uint size);

    [LibraryImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetModuleInformation(
        nint process,
        nint module,
        out ModuleInformation moduleInfo,
        uint size);
}

/// <summary>FILE_BASIC_INFO. <see cref="ChangeTime"/> moves on any write to the file's data or
/// metadata, including a tool putting the write time back, which is why the content cache keys on
/// it (ModContentManifest).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FileBasicInfo
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

internal static partial class NativeMethods
{
    /// <summary>FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo.</summary>
    internal const int FileBasicInfoClass = 0;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(
        Microsoft.Win32.SafeHandles.SafeFileHandle file, int informationClass, out FileBasicInfo information, uint size);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Memory;

/// <summary>
/// A handle on another process's address space. This is the whole reason an external
/// agent can work at all: no injection, no DLL loaded into the game, just
/// OpenProcess + ReadProcessMemory/WriteProcessMemory.
///
/// A 64-bit agent can read and write a 32-bit target (swkotor.exe is x86), but pointer
/// reads must respect the target's pointer width, which is what <see cref="PointerSize"/>
/// is for.
/// </summary>
public sealed class ProcessMemory : IDisposable
{
    private nint _handle;

    private ProcessMemory(nint handle, int processId, string processName, bool is32Bit)
    {
        _handle = handle;
        ProcessId = processId;
        ProcessName = processName;
        Is32Bit = is32Bit;
    }

    public int ProcessId { get; }

    public string ProcessName { get; }

    /// <summary>True when the target runs under WOW64, i.e. it is an x86 process.</summary>
    public bool Is32Bit { get; }

    public int PointerSize => Is32Bit ? 4 : 8;

    public bool IsOpen => _handle != 0;

    /// <summary>
    /// The highest address worth scanning. A 32-bit process without /LARGEADDRESSAWARE
    /// only ever uses the low 2 GB, which keeps a full sweep fast.
    /// </summary>
    public nint MaxScanAddress => Is32Bit ? 0x7FFF_FFFF : unchecked((nint)0x7FFF_FFFF_FFFF);

    public static ProcessMemory Attach(int processId, bool writable = true)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException($"No process with id {processId} is running.", ex);
        }

        using (process)
        {
            uint access = writable ? ProcessAccess.ReadWrite : ProcessAccess.ReadOnly;
            nint handle = NativeMethods.OpenProcess(access, false, (uint)processId);

            if (handle == 0)
            {
                int error = Marshal.GetLastWin32Error();
                string hint = error == 5
                    ? " (access denied: run the agent elevated, or as the same user that launched the game)"
                    : string.Empty;
                throw new Win32Exception(error, $"OpenProcess failed for pid {processId}{hint}.");
            }

            try
            {
                if (!NativeMethods.IsWow64Process(handle, out int wow64))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot determine target pointer width.");
                }

                bool is32Bit = !Environment.Is64BitOperatingSystem || wow64 != 0;
                return new ProcessMemory(handle, processId, process.ProcessName, is32Bit);
            }
            catch
            {
                NativeMethods.CloseHandle(handle);
                throw;
            }
        }
    }

    /// <summary>Attaches to the single running process with the given name.</summary>
    public static ProcessMemory AttachByName(string processName, bool writable = true)
    {
        Process[] matches = Process.GetProcessesByName(processName);
        try
        {
            if (matches.Length == 0)
            {
                throw new InvalidOperationException($"No running process named '{processName}'.");
            }

            if (matches.Length > 1)
            {
                string ids = string.Join(", ", matches.Select(p => p.Id));
                throw new InvalidOperationException(
                    $"{matches.Length} processes named '{processName}' are running (pids {ids}). Pass --pid to choose one.");
            }

            return Attach(matches[0].Id, writable);
        }
        finally
        {
            foreach (Process process in matches)
            {
                process.Dispose();
            }
        }
    }

    // ---- reads ----------------------------------------------------------------

    public unsafe bool TryRead(nint address, Span<byte> buffer)
    {
        if (!IsOpen || buffer.Length == 0)
        {
            return buffer.Length == 0 && IsOpen;
        }

        fixed (byte* pointer = buffer)
        {
            bool ok = NativeMethods.ReadProcessMemory(_handle, address, pointer, (nuint)buffer.Length, out nuint read)
                && read == (nuint)buffer.Length;
            MemoryTelemetry.NoteRead(buffer.Length, ok);
            return ok;
        }
    }

    public bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        T candidate = default;
        Span<byte> buffer = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref candidate, 1));
        if (!TryRead(address, buffer))
        {
            return false;
        }

        value = candidate;
        return true;
    }

    public bool TryReadInt32(nint address, out int value) => TryRead(address, out value);

    public bool TryReadUInt32(nint address, out uint value) => TryRead(address, out value);

    public bool TryReadUInt16(nint address, out ushort value) => TryRead(address, out value);

    public bool TryReadSingle(nint address, out float value) => TryRead(address, out value);

    /// <summary>Reads a pointer using the target's pointer width, not the agent's.</summary>
    public bool TryReadPointer(nint address, out nint pointer)
    {
        pointer = 0;

        if (Is32Bit)
        {
            if (!TryRead(address, out uint narrow))
            {
                return false;
            }

            pointer = (nint)narrow;
            return true;
        }

        if (!TryRead(address, out long wide))
        {
            return false;
        }

        pointer = (nint)wide;
        return true;
    }

    /// <summary>
    /// Reads a fixed-width, null-padded ASCII field. KOTOR stores resource references
    /// (module names among them) as 16-byte ResRef fields in exactly this shape.
    /// </summary>
    public bool TryReadAsciiString(nint address, int maxLength, out string value)
    {
        value = string.Empty;
        if (maxLength <= 0 || maxLength > 1024)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[maxLength];
        if (!TryRead(address, buffer))
        {
            return false;
        }

        int end = buffer.IndexOf((byte)0);
        if (end < 0)
        {
            end = maxLength;
        }

        for (int i = 0; i < end; i++)
        {
            // Anything outside printable ASCII means the offset is not a string field.
            if (buffer[i] < 0x20 || buffer[i] > 0x7E)
            {
                return false;
            }
        }

        value = Encoding.ASCII.GetString(buffer[..end]);
        return true;
    }

    // ---- writes ---------------------------------------------------------------

    public unsafe bool TryWrite(nint address, ReadOnlySpan<byte> data)
    {
        if (!IsOpen || data.Length == 0)
        {
            return data.Length == 0 && IsOpen;
        }

        fixed (byte* pointer = data)
        {
            MemoryTelemetry.NoteWrite(data.Length);
            return NativeMethods.WriteProcessMemory(_handle, address, pointer, (nuint)data.Length, out nuint written)
                && written == (nuint)data.Length;
        }
    }

    public bool TryWrite<T>(nint address, in T value) where T : unmanaged
    {
        ReadOnlySpan<byte> buffer = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1));
        return TryWrite(address, buffer);
    }

    /// <summary>
    /// Writes a fixed-width ASCII field, zero-padded past whatever content fits.
    ///
    /// ⚠ USES THE FULL <paramref name="fieldLength"/> BYTES — do not reserve one for a
    /// terminator. A KOTOR ResRef is a bare <c>char[16]</c>: up to 16 characters with NO
    /// required trailing null (docs/research/companion-state-sync.md, 2026-08-28 round). This
    /// method used to cap content at <c>fieldLength - 1</c> "to leave room for a terminator",
    /// which silently dropped the LAST character of any value exactly <paramref
    /// name="fieldLength"/> characters long — live-observed for a companion's real 16-char
    /// starting sidearm, <c>g_w_blstrpstl001</c>, which crossed this same shared-block write as
    /// <c>g_w_blstrpstl00</c> and was refused by <c>CreateItemOnObject</c> (failure 8) as an
    /// unknown blueprint. <see cref="TryReadAsciiString"/> already tolerates a buffer with no
    /// null in it at all (falls back to reading the full field), so nothing on the read side
    /// needed to change — only this reservation was wrong. The buffer is explicitly cleared
    /// so anything past the written content is correctly null-padded.
    /// </summary>
    public bool TryWriteAsciiString(nint address, string value, int fieldLength)
    {
        if (fieldLength <= 0)
        {
            return false;
        }

        Span<byte> buffer = fieldLength <= 1024 ? stackalloc byte[fieldLength] : new byte[fieldLength];
        buffer.Clear();
        int count = Math.Min(value.Length, fieldLength);
        for (int i = 0; i < count; i++)
        {
            char c = value[i];
            buffer[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        return TryWrite(address, buffer);
    }

    // ---- layout discovery -----------------------------------------------------

    public IEnumerable<MemoryRegion> EnumerateRegions(ScanScope scope)
    {
        if (!IsOpen)
        {
            yield break;
        }

        nint address = 0;
        nuint structSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();

        while (address < MaxScanAddress)
        {
            if (NativeMethods.VirtualQueryEx(_handle, address, out MemoryBasicInformation info, structSize) == 0)
            {
                break;
            }

            long size = (long)info.RegionSize;
            if (size <= 0)
            {
                break;
            }

            MemoryRegion region = new(info.BaseAddress, size, info.Protect, info.State, info.Type);

            if (region.IsReadable && MatchesScope(region, scope))
            {
                yield return region;
            }

            nint next = info.BaseAddress + (nint)size;
            if (next <= address)
            {
                break;
            }

            address = next;
        }
    }

    private static bool MatchesScope(MemoryRegion region, ScanScope scope) => scope switch
    {
        ScanScope.PrivateCommitted => region.IsPrivate,
        ScanScope.Images => region.IsImage,
        ScanScope.AllReadable => true,
        _ => false,
    };

    public unsafe IReadOnlyList<ModuleRegion> GetModules()
    {
        if (!IsOpen)
        {
            return [];
        }

        const int capacity = 1024;
        nint[] handles = new nint[capacity];
        uint needed;

        fixed (nint* pointer = handles)
        {
            if (!NativeMethods.EnumProcessModulesEx(
                    _handle, pointer, (uint)(capacity * sizeof(nint)), out needed, MemoryConstants.ListModulesAll))
            {
                return [];
            }
        }

        int count = Math.Min((int)(needed / (uint)sizeof(nint)), capacity);
        List<ModuleRegion> modules = new(count);
        char[] nameBuffer = new char[512];

        for (int i = 0; i < count; i++)
        {
            nint module = handles[i];
            uint length = NativeMethods.GetModuleFileNameEx(_handle, module, nameBuffer, (uint)nameBuffer.Length);
            string fullPath = length > 0 ? new string(nameBuffer, 0, (int)length) : string.Empty;

            long size = 0;
            if (NativeMethods.GetModuleInformation(
                    _handle, module, out ModuleInformation info, (uint)Marshal.SizeOf<ModuleInformation>()))
            {
                size = info.SizeOfImage;
            }

            modules.Add(new ModuleRegion(Path.GetFileName(fullPath), fullPath, module, size));
        }

        return modules;
    }

    public ModuleRegion? FindModule(string name)
    {
        foreach (ModuleRegion module in GetModules())
        {
            if (string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return module;
            }
        }

        return null;
    }

    public bool IsAlive
    {
        get
        {
            if (!IsOpen)
            {
                return false;
            }

            try
            {
                using Process process = Process.GetProcessById(ProcessId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = 0;
        }
    }
}

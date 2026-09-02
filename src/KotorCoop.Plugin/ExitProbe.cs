using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Names the OTHER way a game vanishes: a clean <c>ExitProcess</c>.
///
/// <see cref="CrashProbe"/> covers hardware faults (its vectored stub) and escaping managed
/// exceptions (the unhandled-exception event), and <c>tools\crashlog.ps1</c> covers the
/// fail-fasts that reach the Windows Application log. What none of them cover is the engine
/// deciding to leave: a client that died 100 ms into a follow after a wipe-recovery rejoin
/// (2026-09-02 23:34:39, docs/research/transition-crashes.md) left NO first-chance fault in
/// this log, NO Application-log entry, and a plugin log that simply stopped after
/// "epoch: world unloading". That signature — nothing anywhere — is exactly what
/// <c>ExitProcess</c> looks like from the outside, and the CRT's <c>exit()</c>, the engine's
/// own fatal-error paths and a plain "quit" all end there.
///
/// So this redirects swkotor.exe's IAT slot for <c>KERNEL32!ExitProcess</c> (found by walking
/// the import table at install time, not by a hard-coded address, because the slot is not one
/// the frame hook already validated) through a 6-instruction stub that hands the caller's
/// return address, the exit code and the caller's stack pointer to <see cref="Detour"/>, which
/// writes one line — synchronously, <see cref="PluginHost.Log"/> appends inline — and then
/// jumps to the real <c>ExitProcess</c> with the stack exactly as the caller left it.
///
/// Like <see cref="FrameHook"/> this is a single aligned dword store into the IAT, so it is
/// race-free with nothing suspended, and it chains rather than refuses if another layer (an
/// overlay) already sits in the slot: the line is the point, not exclusivity.
///
/// A <c>TerminateProcess</c> (what WER does after an unhandled fault, and what
/// <c>Stop-Process -Force</c> does) never comes through here — so a session that ends WITHOUT
/// this line and without a fault line was killed from outside or fail-fasted.
/// </summary>
internal static unsafe partial class ExitProbe
{
    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageReadWrite = 0x04;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>Same image range <see cref="CrashProbe"/> uses to pick candidate return
    /// addresses out of a raw stack slice.</summary>
    private const uint ImageLow = 0x400000;
    private const uint ImageHigh = 0x8C3000;

    private const int StackWords = 64;

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint module, string name);

    private static nint _slot;
    private static nint _real;

    /// <summary>The dword the stub's <c>jmp [ptr]</c> reads: the real ExitProcess, in memory
    /// this class owns so the stub has an absolute address to indirect through.</summary>
    private static nint* _realCell;

    private static volatile bool _installed;

    public static void Install()
    {
        try
        {
            nint image = NativeMethods.GetModuleHandle(null);
            if (image == 0)
            {
                PluginHost.Log("exit probe NOT installed: no main module");
                return;
            }

            nint slot = FindImportSlot((byte*)image, "KERNEL32.DLL", "ExitProcess");
            if (slot == 0)
            {
                PluginHost.Log("exit probe NOT installed: swkotor.exe's import table has no KERNEL32!ExitProcess slot");
                return;
            }

            nint current = *(nint*)slot;
            nint kernel32 = NativeMethods.GetModuleHandle("kernel32.dll");
            nint expected = kernel32 == 0 ? 0 : GetProcAddress(kernel32, "ExitProcess");
            string chained = current == expected || expected == 0
                ? string.Empty
                : $" (slot held 0x{current:X}, not kernel32's 0x{expected:X}: chaining behind whatever hooked it first)";

            _realCell = (nint*)NativeMethods.VirtualAlloc(0, (nuint)sizeof(nint), MemCommitReserve, PageReadWrite);
            if (_realCell is null)
            {
                PluginHost.Log("exit probe NOT installed: could not allocate its cell");
                return;
            }

            *_realCell = current;
            nint stub = BuildStub((nint)_realCell);
            if (stub == 0)
            {
                PluginHost.Log("exit probe NOT installed: could not allocate its stub");
                return;
            }

            if (!NativeMethods.VirtualProtect(slot, (nuint)sizeof(nint), PageReadWrite, out uint oldProtect))
            {
                PluginHost.Log($"exit probe NOT installed: VirtualProtect failed, error {Marshal.GetLastWin32Error()}");
                return;
            }

            _slot = slot;
            _real = current;

            // Single aligned dword store: atomic on x86.
            *(nint*)slot = stub;
            NativeMethods.VirtualProtect(slot, (nuint)sizeof(nint), oldProtect, out _);
            _installed = true;

            PluginHost.Log($"exit probe installed: IAT 0x{slot:X} -> stub 0x{stub:X}, real ExitProcess 0x{current:X}{chained}");
        }
        catch (Exception ex)
        {
            PluginHost.Log($"exit probe NOT installed: {ex.Message}");
        }
    }

    public static void Uninstall()
    {
        if (!_installed || _slot == 0)
        {
            return;
        }

        if (NativeMethods.VirtualProtect(_slot, (nuint)sizeof(nint), PageReadWrite, out uint oldProtect))
        {
            *(nint*)_slot = _real;
            NativeMethods.VirtualProtect(_slot, (nuint)sizeof(nint), oldProtect, out _);
        }

        _installed = false;
    }

    /// <summary>
    /// Walks the PE import directory of <paramref name="image"/> for the IAT entry that binds
    /// <paramref name="function"/> from <paramref name="module"/>. Returns the ADDRESS OF THE
    /// SLOT (the dword the engine's <c>call [..]</c> reads), or 0. Every pointer it follows
    /// is an RVA out of the headers of a module that is already mapped and running, so the
    /// walk cannot fault; it can only fail to find the name.
    /// </summary>
    private static nint FindImportSlot(byte* image, string module, string function)
    {
        int ntOffset = *(int*)(image + 0x3C);
        byte* nt = image + ntOffset;
        if (*(uint*)nt != 0x00004550)
        {
            return 0; // not "PE\0\0"
        }

        // PE32 optional header: data directories start at +0x60; entry 1 is the import table.
        byte* optional = nt + 0x18;
        uint importRva = *(uint*)(optional + 0x60 + 8);
        if (importRva == 0)
        {
            return 0;
        }

        for (byte* descriptor = image + importRva; ; descriptor += 20)
        {
            uint originalFirstThunk = *(uint*)(descriptor + 0);
            uint nameRva = *(uint*)(descriptor + 12);
            uint firstThunk = *(uint*)(descriptor + 16);
            if (nameRva == 0 && firstThunk == 0)
            {
                return 0;
            }

            if (nameRva == 0 || !AsciiEqualsIgnoreCase(image + nameRva, module))
            {
                continue;
            }

            uint names = originalFirstThunk != 0 ? originalFirstThunk : firstThunk;
            for (int i = 0; ; i++)
            {
                uint thunk = *(uint*)(image + names + (4 * i));
                if (thunk == 0)
                {
                    break;
                }

                if ((thunk & 0x80000000) != 0)
                {
                    continue; // imported by ordinal; ExitProcess never is
                }

                // IMAGE_IMPORT_BY_NAME: a 2-byte hint, then the name.
                if (AsciiEqualsIgnoreCase(image + thunk + 2, function))
                {
                    return (nint)(image + firstThunk + (4 * i));
                }
            }
        }
    }

    private static bool AsciiEqualsIgnoreCase(byte* text, string expected)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            byte c = text[i];
            if (c == 0)
            {
                return false;
            }

            if (char.ToUpperInvariant((char)c) != char.ToUpperInvariant(expected[i]))
            {
                return false;
            }
        }

        return text[expected.Length] == 0;
    }

    /// <summary>
    /// Emits the IAT target. Entered by the engine's <c>call [slot]</c> with the stack exactly
    /// as for ExitProcess: <c>[esp]</c> the return address, <c>[esp+4]</c> the exit code.
    ///
    /// <code>
    ///   mov  eax, [esp]          ; return address
    ///   lea  ecx, [esp+4]        ; the caller's stack, from the argument up
    ///   push ecx                 ; arg3: stack pointer
    ///   push dword [esp+8]       ; arg2: exit code (it moved by the push above)
    ///   push eax                 ; arg1: return address
    ///   call Detour              ; stdcall, three args: pops its own 12 bytes
    ///   jmp  dword ptr [cell]    ; the real ExitProcess, stack as on entry
    /// </code>
    /// </summary>
    private static nint BuildStub(nint cell)
    {
        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            return 0;
        }

        byte* s = (byte*)stub;
        int n = 0;

        void Emit(params byte[] bytes)
        {
            foreach (byte b in bytes)
            {
                s[n++] = b;
            }
        }

        void Emit32(int value)
        {
            *(int*)(s + n) = value;
            n += 4;
        }

        Emit(0x8B, 0x04, 0x24);                   // mov eax, [esp]
        Emit(0x8D, 0x4C, 0x24, 0x04);             // lea ecx, [esp+4]
        Emit(0x51);                               // push ecx
        Emit(0xFF, 0x74, 0x24, 0x08);             // push dword [esp+8]
        Emit(0x50);                               // push eax

        nint detour = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, void>)&Detour;
        Emit(0xE8); Emit32((int)(detour - (stub + n + 4)));   // call rel32
        Emit(0xFF, 0x25); Emit32((int)cell);      // jmp dword ptr [cell]

        return stub;
    }

    /// <summary>
    /// Runs on the exiting thread, in native mode from the engine's point of view, which is
    /// the ordinary reverse-P/Invoke context every other hook in this plugin uses. Everything
    /// is inside a catch: a throw here would turn a clean exit into a fail-fast, and the one
    /// job is the line.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Detour(nint returnAddress, uint exitCode, nint stackPointer)
    {
        try
        {
            (uint ownBase, uint ownSize) = CrashProbe.OwnImage;
            string where = ownSize != 0 && (uint)returnAddress >= ownBase && (uint)returnAddress < ownBase + ownSize
                ? $" (OURS: rva 0x{(uint)returnAddress - ownBase:X})"
                : (uint)returnAddress is >= ImageLow and < ImageHigh ? " (swkotor.exe)" : string.Empty;

            // The caller's stack, upward from its argument: anything in the exe's range is a
            // candidate return address, enough to name the path with tools/disasm.py. The
            // slice is clamped to the committed region the pointer sits in (an access
            // violation is not a catchable exception in .NET, so "probably deep enough" is
            // not a guard).
            int wordsAvailable = 0;
            if (NativeMethods.VirtualQuery(stackPointer, out NativeMethods.MemoryBasicInformation region,
                    (nuint)sizeof(NativeMethods.MemoryBasicInformation)) != 0
                && region.State == 0x1000 /* MEM_COMMIT */)
            {
                long end = (long)region.BaseAddress + (long)region.RegionSize;
                wordsAvailable = (int)Math.Min(StackWords, (end - (long)stackPointer) / 4);
            }

            StringBuilder trace = new();
            uint* words = (uint*)stackPointer;
            for (int i = 0; i < wordsAvailable; i++)
            {
                uint value = words[i];
                if (value is >= ImageLow and < ImageHigh)
                {
                    trace.Append($" 0x{value:X}");
                }
                else if (ownSize != 0 && value >= ownBase && value < ownBase + ownSize)
                {
                    trace.Append($" ours+0x{value - ownBase:X}");
                }
            }

            PluginHost.Log(
                $"EXITPROCESS({exitCode}) called from 0x{(uint)returnAddress:X}{where} on thread "
                + $"{Environment.CurrentManagedThreadId}; image-range stack values:{trace}");
        }
        catch
        {
            // A departing process must not be made harder to read.
        }
    }
}

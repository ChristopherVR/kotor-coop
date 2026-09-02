using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Records where the game died, without being the reason it died.
///
/// ⚠ THE HANDLER THAT USED TO LIVE HERE WAS THE CRASH. It was an ordinary
/// <c>[UnmanagedCallersOnly]</c> method registered with AddVectoredExceptionHandler, and a
/// vectored handler is called BY WINDOWS ON THE FAULTING THREAD. When the fault happened
/// inside the plugin's own managed code that thread is in the runtime's COOPERATIVE mode, and
/// entering a reverse P/Invoke from cooperative mode is, to NativeAOT, a corrupt program:
///
///     Fatal error. Invalid Program: attempted to call a UnmanagedCallersOnly method from
///     managed code.
///
/// It answers with RaiseFailFastException — STATUS_FAIL_FAST_EXCEPTION, 0xC0000602 — and the
/// process is gone before anything is written. That is the crash that killed the host the
/// moment a session reached two remote peers, and the reason its log ended mid-sentence: the
/// probe meant to describe the fault was the thing that turned it into an execution.
///
/// It also means every ordinary NullReferenceException in this plugin was a silent process
/// kill. NativeAOT implements null checks as hardware faults on the null page and converts
/// them back into managed exceptions in its OWN vectored handler — but ours was registered
/// later with first=TRUE, which puts it at the FRONT of the chain, so it ran first and
/// fail-fasted before the conversion could happen. Every try/catch in the plugin was dead
/// code for that fault class.
///
/// So the handler is now MACHINE CODE (<see cref="BuildHandler"/>): it copies five dwords and
/// a slice of the faulting stack into a fixed buffer, bumps a serial, and returns
/// EXCEPTION_CONTINUE_SEARCH. No managed frame, no transition, nothing that can fail fast. The
/// worker thread — which is in native mode when it polls, and where formatting a string is
/// free — turns the buffer into a log line.
///
/// The second half is <see cref="AppDomain.UnhandledException"/>, which fires on the runtime's
/// own path to a fail-fast and is the last moment an escaping managed exception is still an
/// object we can read. Between them: hardware faults land in the log via the stub, managed
/// faults via the event, and anything the runtime aborts on internally leaves a Windows
/// Application log entry that <c>tools\crashlog.ps1</c> explains.
/// </summary>
internal static unsafe partial class CrashProbe
{
    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, nint handler);

    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageReadWrite = 0x04;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>Image range of swkotor.exe: values in it are candidate return addresses.</summary>
    private const uint ImageLow = 0x400000;
    private const uint ImageHigh = 0x8C3000;

    // Layout of the record the stub fills in. Kept in one place because the byte emitter
    // below hard-codes these displacements.
    private const int RecSerial = 0x00;
    private const int RecCode = 0x04;
    private const int RecAddress = 0x08;
    private const int RecEip = 0x0C;
    private const int RecEsp = 0x10;
    private const int RecStackWords = 0x14;
    private const int RecStack = 0x18;
    private const int StackWords = 64;
    private const int RecordSize = RecStack + (StackWords * 4);

    /// <summary>STATUS_ACCESS_VIOLATION.</summary>
    private const uint AccessViolation = 0xC0000005;

    /// <summary>STATUS_FAIL_FAST_EXCEPTION — a .NET abort, if a handler ever sees one.</summary>
    private const uint FailFast = 0xC0000602;

    private const uint StackOverflow = 0xC00000FD;

    private static uint* _record;
    private static uint _reported;
    private static int _logged;

    /// <summary>This DLL's own load range. The base moves per process, so a fault address in
    /// a dead process's log could never be symbolized after the fact without it — the RVA
    /// (eip − base) is what <c>llvm-symbolizer --relative-address</c> wants.</summary>
    private static uint _ownBase;
    private static uint _ownSize;

    /// <summary>Resolves the range of the image containing this code, from the PE header
    /// (<c>SizeOfImage</c> at <c>e_lfanew + 0x50</c>). Silent on any failure: a probe that
    /// cannot name its own image still records faults.</summary>
    private static void ResolveOwnImage()
    {
        try
        {
            delegate*<void> here = &ResolveOwnImage;
            nint module = 0;
            if (!NativeMethods.GetModuleHandleEx(
                    NativeMethods.GetModuleHandleExFlagFromAddress | NativeMethods.GetModuleHandleExFlagUnchangedRefcount,
                    (nint)here, &module) || module == 0)
            {
                return;
            }

            byte* image = (byte*)module;
            int ntHeaders = *(int*)(image + 0x3C);
            _ownSize = *(uint*)(image + ntHeaders + 0x50);
            _ownBase = (uint)module;
            PluginHost.Log($"crash probe: this plugin's image is 0x{_ownBase:X}..0x{_ownBase + _ownSize:X}"
                + " (a fault eip in that range is ours: llvm-symbolizer --obj=dinput8.dll --relative-address <eip - base>)");
        }
        catch
        {
            // Diagnostic only.
        }
    }

    /// <summary>Lines this will write before it goes quiet. A first-chance fault is no longer
    /// fatal, so a recoverable one could otherwise repeat every frame.</summary>
    private const int MaxLogged = 8;

    public static void Install()
    {
        try
        {
            nint buffer = NativeMethods.VirtualAlloc(0, RecordSize, MemCommitReserve, PageReadWrite);
            if (buffer == 0)
            {
                PluginHost.Log("crash probe NOT installed: could not allocate its record");
                return;
            }

            _record = (uint*)buffer;
            for (int i = 0; i < RecordSize / 4; i++)
            {
                _record[i] = 0;
            }

            nint handler = BuildHandler(buffer);
            if (handler == 0)
            {
                PluginHost.Log("crash probe NOT installed: could not allocate its handler");
                return;
            }

            if (AddVectoredExceptionHandler(1, handler) != 0)
            {
                PluginHost.Log($"crash probe installed (native handler 0x{handler:X})");
                ResolveOwnImage();
            }
        }
        catch (Exception ex)
        {
            PluginHost.Log($"crash probe NOT installed: {ex.Message}");
        }

        // Registered separately so a failure to attach one still leaves the other in place.
        try
        {
            AppDomain.CurrentDomain.UnhandledException += OnManagedUnhandled;
            PluginHost.Log("unhandled-exception hook installed (managed faults now reach this log)");
        }
        catch (Exception ex)
        {
            PluginHost.Log($"could not install the unhandled-exception hook: {ex.Message}");
        }

        // One line saying where the OTHER half of a crash report lives. Cheap insurance: a
        // fail-fast the runtime raises with __fastfail reaches no handler at all, and its only
        // trace is in the Windows Application log — which is not where anyone looks.
        PluginHost.Log(
            "⚠ if the game vanishes with nothing after this point, the crash may be a .NET "
            + "fail-fast (0xC0000602), which bypasses this log. Check the Windows Application "
            + "log: pwsh tools\\crashlog.ps1");
    }

    /// <summary>
    /// Emits the vectored handler as x86 machine code.
    ///
    /// <code>
    ///   LONG __stdcall Handler(EXCEPTION_POINTERS* p)   // p at [esp+4] on entry
    /// </code>
    ///
    /// It reads the exception code, the faulting address, EIP and ESP out of the OS structures
    /// and copies them plus 256 bytes of the faulting stack into the record, then returns 0
    /// (EXCEPTION_CONTINUE_SEARCH) so crash behaviour is otherwise unchanged. The serial is
    /// written LAST, so the poller never reads a half-filled record.
    ///
    /// ⚠ The stack copy is bounded by the THREAD'S OWN limits, read from the TEB at fs:[4]
    /// (StackBase) and fs:[8] (StackLimit). Everything between them is committed, so the copy
    /// cannot fault — and a handler that faults while handling a fault is how a diagnosable
    /// crash becomes an undiagnosable one.
    /// </summary>
    private static nint BuildHandler(nint record)
    {
        nint stub = NativeMethods.VirtualAlloc(0, 256, MemCommitReserve, PageExecuteReadWrite);
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

        Emit(0x53);                               // push ebx
        Emit(0x56);                               // push esi
        Emit(0x57);                               // push edi
        Emit(0x8B, 0x74, 0x24, 0x10);             // mov esi, [esp+0x10]   ; EXCEPTION_POINTERS*
        Emit(0x85, 0xF6);                         // test esi, esi
        Emit(0x74, 0x00); int j1 = n - 1;         // je done
        Emit(0x8B, 0x06);                         // mov eax, [esi]        ; ExceptionRecord
        Emit(0x85, 0xC0);                         // test eax, eax
        Emit(0x74, 0x00); int j2 = n - 1;         // je done
        Emit(0x8B, 0x5E, 0x04);                   // mov ebx, [esi+4]      ; ContextRecord
        Emit(0x85, 0xDB);                         // test ebx, ebx
        Emit(0x74, 0x00); int j3 = n - 1;         // je done

        Emit(0xBF); Emit32((int)record);          // mov edi, record

        Emit(0x8B, 0x08);                         // mov ecx, [eax]        ; ExceptionCode
        Emit(0x89, 0x4F, RecCode);                // mov [edi+4], ecx
        Emit(0x8B, 0x48, 0x0C);                   // mov ecx, [eax+0x0C]   ; ExceptionAddress
        Emit(0x89, 0x4F, RecAddress);             // mov [edi+8], ecx
        Emit(0x8B, 0x8B); Emit32(0xB8);           // mov ecx, [ebx+0xB8]   ; CONTEXT.Eip
        Emit(0x89, 0x4F, RecEip);                 // mov [edi+0x0C], ecx
        Emit(0x8B, 0x8B); Emit32(0xC4);           // mov ecx, [ebx+0xC4]   ; CONTEXT.Esp
        Emit(0x89, 0x4F, RecEsp);                 // mov [edi+0x10], ecx
        Emit(0xC7, 0x47, RecStackWords); Emit32(0); // mov dword [edi+0x14], 0

        Emit(0x8B, 0xF1);                         // mov esi, ecx          ; faulting esp
        Emit(0x64, 0xA1); Emit32(0x08);           // mov eax, fs:[8]       ; TEB.StackLimit
        Emit(0x3B, 0xF0);                         // cmp esi, eax
        Emit(0x72, 0x00); int j4 = n - 1;         // jb  store             ; below the stack
        Emit(0x64, 0xA1); Emit32(0x04);           // mov eax, fs:[4]       ; TEB.StackBase
        Emit(0x2D); Emit32(StackWords * 4);       // sub eax, 0x100
        Emit(0x3B, 0xF0);                         // cmp esi, eax
        Emit(0x77, 0x00); int j5 = n - 1;         // ja  store             ; too near the top

        Emit(0x57);                               // push edi
        Emit(0x83, 0xC7, RecStack);               // add edi, 0x18
        Emit(0xB9); Emit32(StackWords);           // mov ecx, 64
        Emit(0xFC);                               // cld
        Emit(0xF3, 0xA5);                         // rep movsd
        Emit(0x5F);                               // pop edi
        Emit(0xC7, 0x47, RecStackWords); Emit32(StackWords); // mov dword [edi+0x14], 64

        int store = n;
        Emit(0xFF, 0x07);                         // inc dword [edi]       ; serial, written last

        int done = n;
        Emit(0x5F);                               // pop edi
        Emit(0x5E);                               // pop esi
        Emit(0x5B);                               // pop ebx
        Emit(0x33, 0xC0);                         // xor eax, eax          ; CONTINUE_SEARCH
        Emit(0xC2, 0x04, 0x00);                   // ret 4                 ; __stdcall, 1 arg

        // Every branch is rel8 and every target is forward; the displacement is measured from
        // the byte after the jump.
        s[j1] = (byte)(done - (j1 + 1));
        s[j2] = (byte)(done - (j2 + 1));
        s[j3] = (byte)(done - (j3 + 1));
        s[j4] = (byte)(store - (j4 + 1));
        s[j5] = (byte)(store - (j5 + 1));

        return stub;
    }

    /// <summary>
    /// Turns anything the stub recorded into a log line. Called from the plugin's WORKER
    /// thread, which is an ordinary managed thread doing ordinary managed work — the exact
    /// context the formatting could not safely happen in before.
    /// </summary>
    public static void DrainReports()
    {
        uint* record = _record;
        if (record is null)
        {
            return;
        }

        uint serial = record[RecSerial / 4];
        if (serial == _reported)
        {
            return;
        }

        uint missed = serial - _reported - 1;
        _reported = serial;

        if (_logged >= MaxLogged)
        {
            return;
        }

        _logged++;

        uint code = record[RecCode / 4];
        uint address = record[RecAddress / 4];
        uint eip = record[RecEip / 4];
        uint esp = record[RecEsp / 4];
        uint words = record[RecStackWords / 4];

        string name = code switch
        {
            AccessViolation => "access violation",
            FailFast => "FAIL-FAST (0xC0000602)",
            StackOverflow => "stack overflow",
            _ => "exception",
        };

        // Raw stack slice: anything in the image range is a candidate return address, which
        // is backtrace enough for the symbol DB. An address in DINPUT8's range instead means
        // the fault is OURS, and the plugin's own PDB will name the line.
        System.Text.StringBuilder trace = new();
        for (int i = 0; i < words && i < StackWords; i++)
        {
            uint value = record[(RecStack / 4) + i];
            if (value is >= ImageLow and < ImageHigh)
            {
                trace.Append($" 0x{value:X}");
            }
        }

        // Our own fault, named as the RVA the PDB understands — the base is gone with the process.
        string ours = _ownSize != 0 && eip >= _ownBase && eip < _ownBase + _ownSize
            ? $" (OURS: rva 0x{eip - _ownBase:X})"
            : string.Empty;

        PluginHost.Log(
            $"FIRST-CHANCE {name} (0x{code:X8}) at 0x{address:X}, eip 0x{eip:X}{ours}, esp 0x{esp:X}"
            + (missed > 0 ? $" ({missed} earlier not shown)" : string.Empty)
            + $"; image-range stack values:{trace}");
    }

    /// <summary>
    /// The last point at which an escaping managed exception is still readable. Runs on the
    /// faulting thread with the process already committed to dying, so it does the minimum:
    /// format and append. Everything is inside a catch, because throwing from here would
    /// replace a diagnosable crash with an undiagnosable one.
    /// </summary>
    private static void OnManagedUnhandled(object? sender, UnhandledExceptionEventArgs args)
    {
        try
        {
            string detail = args.ExceptionObject is Exception ex
                ? $"{ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex}"
                : $"non-exception throw: {args.ExceptionObject}";

            PluginHost.Log(
                $"FATAL (managed, terminating={args.IsTerminating}) on thread "
                + $"{Environment.CurrentManagedThreadId}: {detail}");
        }
        catch
        {
            // A dying process must not be made harder to read.
        }
    }
}

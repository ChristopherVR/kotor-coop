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
///
/// 2026-09-02: none of that helps if the process is TERMINATED before the WORKER thread's
/// next poll ever runs — WER off (the default) plus an engine filter that calls
/// TerminateProcess right after the fault leaves the record filled in memory but the log
/// line never written, because <see cref="DrainReports"/> only runs from the worker loop.
/// This is exactly what killed a client 100 ms into a follow after a wipe-recovery rejoin
/// (docs/research/transition-crashes.md, the "CLIENT vanishing" section): no first-chance
/// line, no Application-log entry, log just stops. So the stub now hands the record to
/// <c>kernel32!WriteFile</c> itself, on the faulting thread, in the same breath as filling
/// it — <c>kotorcoop-crash.bin</c> next to the plugin log — so a report reaches disk even
/// when nothing managed ever runs again. <c>tools\crashbin.ps1</c> decodes it. Still no
/// managed call: WriteFile is an ordinary stdcall function, resolved once at
/// <see cref="Install"/> time and called through a fixed cell, exactly like
/// <see cref="ExitProbe"/>'s indirect jump to the real <c>ExitProcess</c>.
/// </summary>
internal static unsafe partial class CrashProbe
{
    [LibraryImport("kernel32.dll")]
    private static partial nint AddVectoredExceptionHandler(uint first, nint handler);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateFileW(
        string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    private const uint PageExecuteReadWrite = 0x40;
    private const uint PageReadWrite = 0x04;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>CreateFileW flags for the .bin record: write-only, readable by another
    /// process (a live decoder or a session that watches for it) while this one still
    /// holds it, fresh every session so a decoder never sees a report from a previous run.</summary>
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileShareRead = 0x1;
    private const uint CreateAlways = 2;
    private const uint FileAttributeNormal = 0x80;
    private const nint InvalidHandleValue = -1;

    /// <summary>FILE_BEGIN for the stub's SetFilePointer call — named for the comment at
    /// the call site, even though its value is the same 0 as a NULL argument.</summary>
    private const uint FileBegin = 0;

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

    // Past RecordSize, so WriteFile's fixed nNumberOfBytesToWrite=RecordSize never touches
    // them: scratch cells the stub reaches by absolute address, exactly like the record
    // fields above, filled in once by Install (never by the stub) and then only ever READ
    // by the stub. RecWrittenScratch is WriteFile's mandatory out-param target; its value
    // is never read back by anything.
    private const int RecWriteFileCell = RecordSize;
    private const int RecSetFilePointerCell = RecordSize + 4;
    private const int RecWrittenScratch = RecordSize + 8;
    private const int RecordAreaSize = RecordSize + 12;

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

    /// <summary>This DLL's load range once <see cref="Install"/> has resolved it (both 0
    /// before that, or if the PE walk failed). <see cref="ExitProbe"/> uses it to tag a
    /// return address that is ours.</summary>
    internal static (uint Base, uint Size) OwnImage => (_ownBase, _ownSize);

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

    public static void Install(string directory)
    {
        try
        {
            nint buffer = NativeMethods.VirtualAlloc(0, RecordAreaSize, MemCommitReserve, PageReadWrite);
            if (buffer == 0)
            {
                PluginHost.Log("crash probe NOT installed: could not allocate its record");
                return;
            }

            _record = (uint*)buffer;
            for (int i = 0; i < RecordAreaSize / 4; i++)
            {
                _record[i] = 0;
            }

            // Resolved HERE, on the startup thread where an ordinary managed GetProcAddress
            // call is safe, and written into the record area's cells — the stub never
            // resolves anything itself, only ever indirects through what Install put there,
            // the same discipline ExitProbe's _realCell uses for the real ExitProcess.
            // Both are foundational kernel32 exports that predate every Windows version this
            // plugin can run on; if either is somehow missing, the WHOLE probe (including
            // the log-only path) is withheld rather than baking a null call target into the
            // stub — a `call dword ptr [cell]` through an unresolved (zero) cell would jump
            // to address 0 the moment the process next faults, which is precisely the
            // "handler that faults while handling a fault" failure mode this file exists to
            // rule out.
            nint kernel32 = NativeMethods.GetModuleHandle("kernel32.dll");
            nint writeFile = kernel32 == 0 ? 0 : NativeMethods.GetProcAddress(kernel32, "WriteFile");
            nint setFilePointer = kernel32 == 0 ? 0 : NativeMethods.GetProcAddress(kernel32, "SetFilePointer");
            if (writeFile == 0 || setFilePointer == 0)
            {
                PluginHost.Log("crash probe NOT installed: could not resolve kernel32!WriteFile/SetFilePointer");
                return;
            }

            *(nint*)((byte*)buffer + RecWriteFileCell) = writeFile;
            *(nint*)((byte*)buffer + RecSetFilePointerCell) = setFilePointer;

            // CREATE_ALWAYS: a fresh, empty file every session, so a decoder never mistakes
            // a stale report from a previous run for this one. FILE_SHARE_READ so
            // tools\crashbin.ps1 (or another instance) can read it while this process still
            // holds the handle open.
            string binPath = Path.Combine(directory, "kotorcoop-crash.bin");
            nint fileHandle = CreateFileW(
                binPath, GenericWrite, FileShareRead, 0, CreateAlways, FileAttributeNormal, 0);
            if (fileHandle == InvalidHandleValue)
            {
                // Not fatal to the rest of the probe: WriteFile against a bad handle fails
                // gracefully (ERROR_INVALID_HANDLE) rather than faulting — see BuildHandler's
                // doc comment — so the FIRST-CHANCE log line still works even without it.
                PluginHost.Log(
                    $"crash probe: could not open {binPath} for the binary record "
                    + $"(error {Marshal.GetLastWin32Error()}); FIRST-CHANCE log lines still work");
            }
            else
            {
                PluginHost.Log($"crash probe: binary crash record at {binPath} (tools\\crashbin.ps1 decodes it)");
            }

            nint handler = BuildHandler(buffer, fileHandle);
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
    /// and copies them plus 256 bytes of the faulting stack into the record, bumps the serial,
    /// hands the record to <c>kernel32!WriteFile</c> (see below), then returns 0
    /// (EXCEPTION_CONTINUE_SEARCH) so crash behaviour is otherwise unchanged. The serial is
    /// written before the WriteFile calls but after every field, so the poller never reads a
    /// half-filled record and the .bin's serial (offset 0) always matches what it copied.
    ///
    /// ⚠ The stack copy is bounded by the THREAD'S OWN limits, read from the TEB at fs:[4]
    /// (StackBase) and fs:[8] (StackLimit). Everything between them is committed, so the copy
    /// cannot fault — and a handler that faults while handling a fault is how a diagnosable
    /// crash becomes an undiagnosable one.
    ///
    /// After the serial bump, two more stdcall calls, both indirect through a cell
    /// <see cref="Install"/> resolved and filled in (never re-resolved here — see its comment
    /// on why a null cell must never reach this stub):
    ///
    /// <code>
    ///   SetFilePointer(fileHandle, 0, NULL, FILE_BEGIN);
    ///   WriteFile(fileHandle, record, RecordSize, &amp;written, NULL);
    /// </code>
    ///
    /// SetFilePointer resets to offset 0 before every write, so <c>kotorcoop-crash.bin</c>
    /// stays a fixed RecordSize bytes — the LATEST report only — no matter how many
    /// first-chance faults this process sees before it dies; growing it unboundedly would
    /// repeat the exact "a recoverable one could otherwise repeat every frame" risk
    /// <see cref="MaxLogged"/> already guards on the log side, except unbounded on disk
    /// instead of merely unbounded in the log. <c>fileHandle</c> may be
    /// <c>INVALID_HANDLE_VALUE</c> if <see cref="Install"/> could not open the file — that is
    /// safe to bake in and call through unconditionally, because a bad HANDLE value is data,
    /// not an address: the OS validates it in kernel mode and both calls just fail with
    /// <c>ERROR_INVALID_HANDLE</c>, no fault, no different from any other WriteFile failure
    /// this stub does not check. <c>FlushFileBuffers</c> is deliberately NOT called: WriteFile
    /// returning has already handed the bytes to the OS file cache, which survives a
    /// TerminateProcess of THIS process (the cache belongs to the OS, not the process), and a
    /// synchronous flush-to-disk is a blocking call this thread cannot afford if it is itself
    /// the reason the process is about to die.
    ///
    /// Both new calls are stdcall (callee pops its own argument bytes, preserves
    /// ebx/esi/edi/ebp) and both run after every field of the record is filled and after EDI
    /// has been restored to point at the record's start (whether or not the stack copy ran —
    /// both the copy path and the two "too close to a stack limit" skips reconverge on it), so
    /// <c>push edi</c> is exactly <c>lpBuffer=record</c> with no extra bookkeeping.
    /// </summary>
    private static nint BuildHandler(nint record, nint fileHandle)
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
        Emit(0x0F, 0x84); int j1 = n; Emit32(0);  // je near done          ; disp32 patched below
        Emit(0x8B, 0x06);                         // mov eax, [esi]        ; ExceptionRecord
        Emit(0x85, 0xC0);                         // test eax, eax
        Emit(0x0F, 0x84); int j2 = n; Emit32(0);  // je near done          ; disp32 patched below
        Emit(0x8B, 0x5E, 0x04);                   // mov ebx, [esi+4]      ; ContextRecord
        Emit(0x85, 0xDB);                         // test ebx, ebx
        Emit(0x0F, 0x84); int j3 = n; Emit32(0);  // je near done          ; disp32 patched below

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

        // SetFilePointer(fileHandle, 0, NULL, FILE_BEGIN) — reset to offset 0 so the .bin
        // stays fixed-size. Args pushed right-to-left, last param nearest the call.
        nint setFilePointerCell = record + RecSetFilePointerCell;
        Emit(0x68); Emit32((int)FileBegin);              // push FILE_BEGIN        ; dwMoveMethod
        Emit(0x68); Emit32(0);                            // push 0                 ; lpDistanceToMoveHigh
        Emit(0x68); Emit32(0);                            // push 0                 ; lDistanceToMove
        Emit(0x68); Emit32((int)fileHandle);              // push fileHandle        ; hFile
        Emit(0xFF, 0x15); Emit32((int)setFilePointerCell); // call dword ptr [setFilePointerCell]

        // WriteFile(fileHandle, record, RecordSize, &written, NULL) — edi is still `record`
        // here (restored above whether or not the stack copy ran).
        nint writeFileCell = record + RecWriteFileCell;
        nint writtenCell = record + RecWrittenScratch;
        Emit(0x68); Emit32(0);                            // push 0                 ; lpOverlapped
        Emit(0x68); Emit32((int)writtenCell);             // push writtenCell        ; lpNumberOfBytesWritten
        Emit(0x68); Emit32(RecordSize);                   // push RecordSize          ; nNumberOfBytesToWrite
        Emit(0x57);                                        // push edi                  ; lpBuffer = record
        Emit(0x68); Emit32((int)fileHandle);               // push fileHandle            ; hFile
        Emit(0xFF, 0x15); Emit32((int)writeFileCell);      // call dword ptr [writeFileCell]

        int done = n;
        Emit(0x5F);                               // pop edi
        Emit(0x5E);                               // pop esi
        Emit(0x5B);                               // pop ebx
        Emit(0x33, 0xC0);                         // xor eax, eax          ; CONTINUE_SEARCH
        Emit(0xC2, 0x04, 0x00);                   // ret 4                 ; __stdcall, 1 arg

        // j1-j3 are near (rel32) jumps because `done` now sits well past a short jump's
        // +127 reach; the displacement is measured from the byte after the 4-byte immediate,
        // i.e. from (slot + 4). j4/j5 stay short (rel8) — their target, `store`, did not move.
        *(int*)(s + j1) = done - (j1 + 4);
        *(int*)(s + j2) = done - (j2 + 4);
        *(int*)(s + j3) = done - (j3 + 4);
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

        // A fault in the path-update trim: name the creature and count the guard saw last.
        if (eip is >= WaypointGuardHook.FunctionLow and < WaypointGuardHook.FunctionHigh)
        {
            PluginHost.Log($"  in EvaluateNewWayPoints: {WaypointGuardHook.LastCallSummary()}");
        }
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

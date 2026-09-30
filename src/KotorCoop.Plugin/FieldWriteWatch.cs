using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace KotorCoop.Plugin;

/// <summary>
/// A DIAGNOSTIC hardware watchpoint: "who writes this dword?" answered by the CPU rather than by
/// a static scan. Arms debug register DR0 on the game's main thread as a 4-byte write breakpoint
/// and records, from a vectored exception handler, the instruction that just wrote the field
/// (the trap fires AFTER the store, so <c>eip</c> is the NEXT instruction), the value it left,
/// and the top of the stack (for a poor man's backtrace — every dword that lands inside
/// swkotor.exe's code is printed, so a return address is in there somewhere).
///
/// Built 2026-09-03 for the galaxy-map relay: every static sweep of the binary (capstone over
/// tools/kotor1_0_3.db, any base register, any write mnemonic) says <c>CGuiInGame+0x30</c> is
/// written by <c>ShowSWInGameGui</c>, <c>HideSWInGameGui</c>, the constructor and
/// <c>CSWGuiPartySelection::OnDone</c> only — yet <c>galaxymap open</c> on the host flips it
/// 0 → 1 with <see cref="ShowInGameGuiProbeHook"/> recording no call. The watchpoint named
/// the writer in one run: <c>CClientExoAppInternal::SetInputClass</c> 0x006200E0 (stores at
/// 0x006202BE and 0x00620272 — a class-range scan of <c>CGuiInGame</c>'s own methods could
/// never see it), plus <c>CGuiInGame::ShowStatusSummary</c> 0x0062EF90 in the stack capture
/// as the thing that left a save-joined client in input class 2. The fix that came out of it
/// is <c>Odyssey.RestoreGameInputClass</c>; the finding is written up in docs/research/
/// galaxy-map-and-party-select.md's 2026-09-03 addendum. Kept for the next such question.
///
/// Default OFF — <c>coop.ini gui_probe=1</c> arms it on <c>CGuiInGame+0x30</c> from the frame
/// hook, re-arming whenever the in-game GUI object changes (it is rebuilt per module load).
/// The debug registers belong to ONE thread, so the arm runs on a helper thread that suspends
/// the main thread for the few microseconds <c>SetThreadContext</c> needs; Windows keeps the
/// registers across context switches and the vectored handler runs on whichever thread traps.
///
/// Read-only in effect: the handler returns <c>EXCEPTION_CONTINUE_EXECUTION</c> without
/// touching anything but DR6, and records into a preallocated ring drained by the frame hook.
/// </summary>
internal static unsafe class FieldWriteWatch
{
    private const int Capacity = 64;
    private const int StackDwords = 12;

    private static readonly uint[] Eip = new uint[Capacity];
    private static readonly uint[] Value = new uint[Capacity];
    private static readonly uint[] Esp = new uint[Capacity];
    private static readonly uint[] Stack = new uint[Capacity * StackDwords];
    private static readonly uint[] Serials = new uint[Capacity];

    private static int _write;
    private static uint _sequence;
    private static uint _drained;

    private static nint _handler;
    private static nint _watched;
    private static nint _armedOn;
    private static uint _mainThreadId;
    private static int _armInFlight;
    private static string? _lastArmResult;

    // Separate default-OFF investigation target. Summary notifications are written
    // through CGuiInGame::UpdateStatusSummary, not through the busy-field setter.
    // This changes only the diagnostic address; no game flags are cleared or set.
    public static bool WatchStatusSummary { get; set; }

    // ---- Win32 ---------------------------------------------------------------------------

    private const uint ContextDebugRegisters = 0x00010010;
    private const int ContextSize = 0x2CC;
    private const int ContextDr0 = 0x04;
    private const int ContextDr6 = 0x14;
    private const int ContextDr7 = 0x18;
    private const int ContextEip = 0xB8;
    private const int ContextEsp = 0xC4;
    private const uint StatusSingleStep = 0x80000004;
    private const int ExceptionContinueExecution = -1;
    private const int ExceptionContinueSearch = 0;
    private const uint ThreadAllAccess = 0x1FFFFF;

    /// <summary>DR7: local-enable DR0 (bit 0), R/W0 = 01 (break on data writes, bits 16-17),
    /// LEN0 = 11 (4 bytes, bits 18-19).</summary>
    private const uint Dr7WriteDword = 0x1 | (0x1u << 16) | (0x3u << 18);

    [DllImport("kernel32.dll")]
    private static extern nint AddVectoredExceptionHandler(uint first, nint handler);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenThread(uint access, int inherit, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SuspendThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetThreadContext(nint thread, nint context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int SetThreadContext(nint thread, nint context);

    [DllImport("kernel32.dll")]
    private static extern int CloseHandle(nint handle);

    // ---- frame-hook driver ---------------------------------------------------------------

    /// <summary>MAIN THREAD, every frame while <c>gui_probe</c> is on: keep DR0 on the live
    /// <c>CGuiInGame+0x30</c>, and turn the ring into log lines.</summary>
    public static void KeepArmedOnGuiBusy()
    {
        if (_mainThreadId == 0)
        {
            _mainThreadId = GetCurrentThreadId();
        }

        nint gui = EngineHandles.GetInGameGui();
        nint target = gui == 0 ? 0 : gui + 0x30;
        if (WatchStatusSummary)
        {
            target = 0;
            if (gui != 0 && NativeMethods.IsReadable(gui + 0xA8, 4))
            {
                nint panel = *(nint*)(gui + 0xA8);
                if (panel != 0 && NativeMethods.IsReadable(panel, 0x80)
                    && *(nint*)panel == 0x0074FF68)
                {
                    target = panel + 0x64;
                }
            }
        }

        // A missing/rebuilt summary must withdraw the old diagnostic watchpoint.
        // DR7 is disabled below for address zero; normal GUI-busy probing keeps
        // its existing target-selection behavior.
        if ((target != 0 || WatchStatusSummary) && target != _armedOn && Volatile.Read(ref _armInFlight) == 0)
        {
            Arm(target);
        }

        string? result = Interlocked.Exchange(ref _lastArmResult, null);
        if (result is not null)
        {
            PluginHost.Log($"field-watch: {result}");
        }

        Drain();
    }

    /// <summary>Installs the vectored handler once, then hands the register write to a helper
    /// thread (the main thread cannot rewrite its own debug registers from user mode).</summary>
    private static void Arm(nint address)
    {
        if (_handler == 0)
        {
            nint callback = (nint)(delegate* unmanaged[Stdcall]<nint, int>)&OnException;
            _handler = AddVectoredExceptionHandler(1, callback);
            if (_handler == 0)
            {
                _lastArmResult = "AddVectoredExceptionHandler failed - not arming";
                _armedOn = address; // do not retry every frame
                return;
            }
        }

        _armedOn = address;
        Volatile.Write(ref _armInFlight, 1);
        uint mainThread = _mainThreadId;
        var helper = new Thread(() => ArmOnThread(mainThread, address)) { IsBackground = true, Name = "kotorcoop-field-watch" };
        helper.Start();
    }

    private static void ArmOnThread(uint threadId, nint address)
    {
        string result;
        nint thread = OpenThread(ThreadAllAccess, 0, threadId);
        if (thread == 0)
        {
            result = $"OpenThread({threadId}) failed ({Marshal.GetLastWin32Error()})";
        }
        else
        {
            nint context = (nint)NativeMemory.AlignedAlloc(ContextSize, 16);
            NativeMemory.Clear((void*)context, ContextSize);
            *(uint*)context = ContextDebugRegisters;
            if (SuspendThread(thread) == 0xFFFFFFFF)
            {
                result = $"SuspendThread failed ({Marshal.GetLastWin32Error()})";
            }
            else
            {
                if (GetThreadContext(thread, context) == 0)
                {
                    result = $"GetThreadContext failed ({Marshal.GetLastWin32Error()})";
                }
                else
                {
                    *(uint*)(context + ContextDr0) = (uint)address;
                    *(uint*)(context + ContextDr6) = 0;
                    *(uint*)(context + ContextDr7) = address == 0 ? 0 : Dr7WriteDword;
                    bool installed = SetThreadContext(thread, context) != 0;
                    // Publish the matching read target while the game thread is
                    // suspended. In particular, never publish zero while its old
                    // DR0 watchpoint is still capable of trapping a game write.
                    if (installed)
                    {
                        _watched = address;
                    }
                    result = !installed
                        ? $"SetThreadContext failed ({Marshal.GetLastWin32Error()})"
                        : address == 0
                            ? $"DR0 diagnostic watch disabled for thread {threadId}"
                            : $"DR0 armed on 0x{address:X} (4-byte write) for thread {threadId}";
                }

                ResumeThread(thread);
            }

            NativeMemory.AlignedFree((void*)context);
            CloseHandle(thread);
        }

        Interlocked.Exchange(ref _lastArmResult, result);
        Volatile.Write(ref _armInFlight, 0);
    }

    // ---- the trap ------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnException(nint exceptionPointers)
    {
        try
        {
            nint record = *(nint*)exceptionPointers;
            nint context = *(nint*)(exceptionPointers + 4);
            if (record == 0 || context == 0 || *(uint*)record != StatusSingleStep)
            {
                return ExceptionContinueSearch;
            }

            uint dr6 = *(uint*)(context + ContextDr6);
            if ((dr6 & 0x1) == 0)
            {
                return ExceptionContinueSearch; // a single-step that is not our DR0
            }

            *(uint*)(context + ContextDr6) = 0;

            int slot = _write;
            Eip[slot] = *(uint*)(context + ContextEip);
            Esp[slot] = *(uint*)(context + ContextEsp);
            nint watched = _watched;
            Value[slot] = watched == 0 ? 0xFFFFFFFF : *(uint*)watched;
            uint* sp = (uint*)Esp[slot];
            for (int i = 0; i < StackDwords; i++)
            {
                Stack[(slot * StackDwords) + i] = sp[i];
            }

            Serials[slot] = ++_sequence;
            _write = (slot + 1) % Capacity;
            return ExceptionContinueExecution;
        }
        catch
        {
            return ExceptionContinueSearch;
        }
    }

    private static void Drain()
    {
        while (_drained < _sequence)
        {
            uint serial = _drained + 1;
            int slot = (int)((serial - 1) % Capacity);
            if (Serials[slot] != serial)
            {
                _drained = serial;
                continue;
            }

            var frames = new System.Text.StringBuilder();
            for (int i = 0; i < StackDwords; i++)
            {
                uint v = Stack[(slot * StackDwords) + i];
                if (v is >= 0x401000 and < 0x73D000)
                {
                    frames.Append($" [esp+{i * 4:X}]=0x{v:X}");
                }
            }

            PluginHost.Log($"field-watch: 0x{(uint)_watched:X} <- {Value[slot]} by the instruction before eip 0x{Eip[slot]:X}"
                + $" (esp 0x{Esp[slot]:X}) exe frames:{frames}");
            _drained = serial;
        }
    }
}

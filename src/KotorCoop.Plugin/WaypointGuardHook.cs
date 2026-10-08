using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Watches <c>CSWCCreature::EvaluateNewWayPoints 0x0060E7D0</c>, where the host crashed on the
/// Leviathan save (<c>lev_m40aa</c>) inside the function's trim of a path update:
///
/// <code>
///   0x0060EA46  call operator new            ; (count - i + 1) * 12
///   0x0060EA7C  xor  eax, eax                ; allocation failed: destination is null
///   0x0060EA92  lea  esi, [list + i*12]
///   0x0060EA95  rep  movsd                   ; &lt;- the live access violation
/// </code>
///
/// That copy reads <c>list[i..count]</c> — one point past the end of the buffer the caller
/// (<c>0x0066640B</c>) built from the message — so either a failed allocation or a list that
/// ends at a page boundary faults there. The first theory, a phantom's twin, did not survive
/// the log: one of the crashes came seven seconds into a world with no phantom spawned.
///
/// The guard's own log found which: every list it caught ended exactly on a page edge (a
/// 6-point list at 0x477FAFB8 ends at 0x477FB000). That is a full page heap (gflags, turned on
/// for swkotor.exe on 2026-09-25 to hunt heap corruption), which puts every block flush against
/// an inaccessible page. On an ordinary heap the over-read lands in the next block and is
/// harmless, which is why retail gets away with it. So this is a retail bug that only a debug
/// heap exposes, and the guard makes it harmless there too.
///
/// When the list is not readable one point past its end, the guard hands the engine a copy with
/// a spare zeroed point, so the path still applies and the over-read stays in our buffer. A
/// count no path update could produce is refused outright; the caller discards the return value
/// and frees the list itself, so a refused call leaves nothing allocated and nothing dangling.
/// It also records every call's inputs, so that if the function still faults,
/// <see cref="CrashProbe.DrainReports"/> can name the creature and the count in the crash line.
/// </summary>
internal static unsafe class WaypointGuardHook
{
    private const nint EvaluateNewWayPoints = 0x0060E7D0;

    /// <summary>Where the relocated prologue resumes: the <c>push eax</c> that follows it.</summary>
    private const nint ResumeAt = 0x0060E7DD;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// The SEH prologue we relocate: <c>push -1</c>, <c>push 0x727BBE</c>,
    /// <c>mov eax, fs:[0]</c>. Thirteen bytes, three whole instructions, none
    /// position-dependent.
    /// </summary>
    private static ReadOnlySpan<byte> Prologue =>
        [0x6A, 0xFF, 0x68, 0xBE, 0x7B, 0x72, 0x00, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00];

    /// <summary>How many refusals get a log line each; later ones only bump the counter.</summary>
    private const int LoggedRefusals = 8;

    /// <summary>The function's extent, for <see cref="CrashProbe"/> to tell a fault in it.</summary>
    public const uint FunctionLow = 0x0060E7D0;
    public const uint FunctionHigh = 0x0060ED90;

    /// <summary>No path a server sends a client has this many points; a list this long is a
    /// garbled message, not a walk.</summary>
    private const int MaxPlausiblePoints = 4096;

    private static uint _refusedCount;

    // The last call's inputs, written on the main thread before the function runs and read by
    // the crash reporter's worker thread while the process is dying. Plain words: a torn read
    // costs one diagnostic line, never correctness.
    private static uint _lastTwin;
    private static uint _lastObjectId;
    private static int _lastCount;
    private static uint _lastList;
    private static uint _lastArg3;
    private static uint _lastArg4;
    private static uint _calls;
    private static uint _paddedCount;

    // Grow-only scratch for padded copies. Main thread only, and never held past the call.
    private static byte* _scratch;
    private static nuint _scratchBytes;

    public static uint PaddedCount => _paddedCount;
    private static bool _installed;

    public static uint RefusedCount => _refusedCount;

    /// <summary>Verify-bytes-or-refuse. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)EvaluateNewWayPoints;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not guarding path updates";
                return false;
            }
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        s[n++] = 0x60;                                       // pushad
        s[n++] = 0x8D; s[n++] = 0x44; s[n++] = 0x24;         // lea eax, [esp + 0x24]
        s[n++] = 0x24;                                       //   the four stack args, past
                                                             //   pushad (0x20) + return (4)
        s[n++] = 0x50;                                       // push eax  (args)
        s[n++] = 0x51;                                       // push ecx  (the CSWCCreature)

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, uint*, int>)&OnEvaluateNewWayPoints;
        s[n++] = 0xE8;                                       // call OnEvaluateNewWayPoints
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08;         // add esp, 8  (two cdecl args)
        s[n++] = 0x85; s[n++] = 0xC0;                        // test eax, eax  (before popad —
                                                             //  popad overwrites eax)
        s[n++] = 0x61;                                       // popad (flags survive)
        s[n++] = 0x75;                                       // jnz refused
        s[n++] = (byte)(Prologue.Length + 5);                //   skip the prologue + jmp

        for (int i = 0; i < Prologue.Length; i++)            // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp EvaluateNewWayPoints+13
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));
        n += 4;

        s[n++] = 0x33; s[n++] = 0xC0;                        // refused: xor eax, eax
        s[n++] = 0xC2; s[n++] = 0x10; s[n++] = 0x00;         // ret 0x10 — four stack args,
                                                             //  read off the epilogue

        if (!NativeMethods.VirtualProtect(EvaluateNewWayPoints, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (EvaluateNewWayPoints + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90;                                  // pad, so nothing reads garbage
        }

        NativeMethods.VirtualProtect(EvaluateNewWayPoints, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"waypoint guard at 0x{EvaluateNewWayPoints:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>One line describing the last call, for a crash report.</summary>
    public static string LastCallSummary() =>
        $"last path update #{_calls}: creature 0x{_lastObjectId:X} (twin 0x{_lastTwin:X}), "
        + $"{_lastCount} point(s) at 0x{_lastList:X}, arg3 {_lastArg3}, arg4 0x{_lastArg4:X}; "
        + $"{_paddedCount} padded, {_refusedCount} refused so far";

    /// <summary>Returns non-zero to refuse the call. Runs inside the client's message
    /// handling on the main thread; no allocation on the accepted path.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnEvaluateNewWayPoints(nint twin, uint* args)
    {
        try
        {
            int count = (int)args[0];
            nint list = (nint)args[1];

            _calls++;
            _lastTwin = (uint)twin;
            _lastObjectId = twin != 0 && NativeMethods.IsReadable(twin + 4, sizeof(uint)) ? *(uint*)(twin + 4) : 0;
            _lastCount = count;
            _lastList = (uint)list;
            _lastArg3 = args[2];
            _lastArg4 = args[3];

            // count <= 0 is the engine's own early-out; let it take it.
            if (count <= 0)
            {
                return 0;
            }

            if (count > MaxPlausiblePoints || list == 0)
            {
                _refusedCount++;
                if (_refusedCount <= LoggedRefusals)
                {
                    PluginHost.Log($"waypoint guard: refused a path update for creature 0x{_lastObjectId:X} "
                        + $"(twin 0x{twin:X}): {count} point(s) at 0x{list:X} is not a path; refusal #{_refusedCount}");
                }

                return 1;
            }

            // The trim reads one point past the list. On an ordinary heap that is the next
            // block's bytes; when the list ends at the edge of committed memory it is a fault.
            // Hand the engine a copy with a spare zeroed point instead. Nothing keeps the
            // pointer: 0x0060C790 copies the points into the creature's own array, and the
            // caller frees its original list either way.
            nuint needed = (nuint)((count + 1) * 12);
            if (!NativeMethods.IsReadable(list, needed))
            {
                if (_scratchBytes < needed)
                {
                    _scratch = (byte*)NativeMemory.Realloc(_scratch, needed);
                    _scratchBytes = needed;
                }

                Buffer.MemoryCopy((void*)list, _scratch, (long)needed, count * 12);
                NativeMemory.Clear(_scratch + (count * 12), 12);
                args[1] = (uint)(nint)_scratch;
                _lastList = (uint)(nint)_scratch;
                _paddedCount++;
                if (_paddedCount == 1)
                {
                    PluginHost.Log($"waypoint guard: a {count}-point path list for creature 0x{_lastObjectId:X} ended at "
                        + $"the edge of readable memory (a page heap does that); the engine reads one point "
                        + $"past it, so it got a padded copy. Later ones are counted, not logged.");
                }
            }

            return 0;
        }
        catch
        {
            // Never let a managed fault unwind into the engine; fail open.
            return 0;
        }
    }
}

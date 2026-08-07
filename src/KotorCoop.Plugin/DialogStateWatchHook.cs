using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// A DIAGNOSTIC on <c>CGuiInGame::SetGlobalDialogState 0x0062EC60</c>, the one writer of
/// <c>CGuiInGame + 0xB4</c>, recording who calls it and with what.
///
/// It exists to answer one question that inspection could not. An instance can be left
/// believing forever that it is in a conversation: <c>gui+0xB4</c> reads non-zero while
/// <c>gui+0xB0</c> is 0 and BOTH participants are <c>OBJECT_INVALID</c>. It will then neither
/// start a conversation nor follow a peer's. Nothing recovers it —
/// <c>CGuiInGame::CloseDialog</c> runs and clears the flag, and something sets it straight
/// back on the next frame, so forcing the UI shut produces a one-frame flicker and nothing
/// else (measured; see docs/research/dialogue-sync.md).
///
/// <c>tools/fieldwrites.py</c> found the writers of <c>+0xB4</c>. Only three set it to 1:
/// <c>StartDialog</c> (0x004CF641), which is ruled out because the plugin's own StartDialog
/// counter does not move while wedged; 0x0060F764, immediately after
/// <c>CSWCMessage::SendPlayerToServerInput_Dialog 0x00677EA0</c>; and 0x0052D84F, in a
/// server-side message handler. The standing hypothesis is that the first of those is
/// self-sustaining — <c>SendPlayerToServerInput_Dialog</c> is itself gated on
/// <c>*(gui+0xB4)</c> (0x00677EB3) and 0x0060F764 sets the flag right after calling it, so a
/// stale 1 would keep the client sending dialogue input and the handler would set it back.
///
/// This records the RETURN ADDRESS of each call, which distinguishes the three
/// unambiguously. That is the whole experiment.
///
/// ⚠ OBSERVE ONLY. The engine's own write always runs; nothing is refused, so this cannot
/// change behaviour. The detour is installed only when <c>coop.ini dialog_state_watch</c> is
/// on, because it sits on a function called during ordinary conversation flow.
/// </summary>
internal static unsafe class DialogStateWatchHook
{
    private const nint SetGlobalDialogState = 0x0062EC60;

    /// <summary>Where the relocated prologue resumes.</summary>
    private const nint ResumeAt = 0x0062EC67;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// The prologue we relocate: <c>mov eax,[esp+4]</c> then <c>sub esp,0xc</c> — seven bytes,
    /// neither position-dependent, so they move verbatim.
    ///
    /// ⚠ Seven, not five. A five-byte jmp would land mid-<c>sub</c>, and the relocated bytes
    /// must be whole instructions. Verified against the binary before it is written.
    /// </summary>
    private static ReadOnlySpan<byte> Prologue =>
        [0x8B, 0x44, 0x24, 0x04, 0x83, 0xEC, 0x0C];

    /// <summary>From coop.ini <c>dialog_state_watch</c> (default OFF).</summary>
    public static bool Enabled;

    /// <summary>Distinct (caller, value) pairs seen, newest last. Small and fixed: the point
    /// is WHICH call sites fire, not how often, and an unbounded log of a per-frame loop is
    /// exactly what buried this the first time.</summary>
    private const int Capacity = 8;

    private static readonly nint[] Callers = new nint[Capacity];
    private static readonly int[] Values = new int[Capacity];
    private static readonly uint[] Counts = new uint[Capacity];
    private static int _known;

    public static int Known => _known;

    public static nint CallerAt(int i) => (uint)i < (uint)_known ? Callers[i] : 0;

    public static int ValueAt(int i) => (uint)i < (uint)_known ? Values[i] : 0;

    public static uint CountAt(int i) => (uint)i < (uint)_known ? Counts[i] : 0;

    private static bool _installed;

    /// <summary>Verify-bytes-or-refuse, and say what was seen when refusing. MAIN THREAD.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)SetGlobalDialogState;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not watching dialog state";
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

        s[n++] = 0x60;                                   // pushad

        // OnSetState(value, returnAddress). After pushad, esp+0x20 is the call's own return
        // address (into this stub's caller = the engine call site) and esp+0x24 is arg0.
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x20;   // push [esp+0x20]
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x28;   // push [esp+0x28] (arg0)

        nint callback = (nint)(delegate* unmanaged[Cdecl]<int, nint, void>)&OnSetState;
        s[n++] = 0xE8;
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08;     // add esp, 8  (cdecl: we clean)
        s[n++] = 0x61;                                   // popad

        for (int i = 0; i < Prologue.Length; i++)        // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                   // jmp SetGlobalDialogState+7
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(
                SetGlobalDialogState, (nuint)Prologue.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;
        *(int*)(site + 1) = (int)(stub - (SetGlobalDialogState + 5));
        for (int i = 5; i < Prologue.Length; i++)
        {
            site[i] = 0x90;
        }

        NativeMethods.VirtualProtect(SetGlobalDialogState, (nuint)Prologue.Length, old, out _);

        _installed = true;
        diagnostic = $"dialog-state watch at 0x{SetGlobalDialogState:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>Runs before the engine's own store. No engine calls, no allocation.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSetState(int value, nint caller)
    {
        try
        {
            for (int i = 0; i < _known; i++)
            {
                if (Callers[i] == caller && Values[i] == value)
                {
                    Counts[i]++;
                    return;
                }
            }

            if (_known < Capacity)
            {
                Callers[_known] = caller;
                Values[_known] = value;
                Counts[_known] = 1;
                _known++;
            }
        }
        catch
        {
            // Never let a managed fault unwind into the engine.
        }
    }
}

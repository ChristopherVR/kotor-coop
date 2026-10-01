using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Closes the Manaan "queued effect-removal" crash (docs/research/manaan-revive-crash-2026-10-01.md).
/// A splice on the CGameEffect destructor (0x503590) retires any queued event-14 that still
/// borrows the effect being freed, so the engine's later dispatch is its own null-payload no-op
/// instead of a read of freed memory. Game thread only; coop.ini effect_expiry_fence=0 opts out.
/// </summary>
internal static unsafe class EffectExpiryFence
{
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    private static uint _thread;
    private static int _logged;
    public static int Retired { get; private set; }

    public static bool Install(out string diagnostic)
    {
        _thread = GetCurrentThreadId();
        return DelayEventProbeHook.SpliceContext(0x503590, [0x90, 0x90, 0x90, 0x90],
            [0x6A, 0xFF, 0x68, 0xE6, 0xB7, 0x71, 0x00], 0x503597,
            (nint)(delegate* unmanaged[Cdecl]<uint, void>)&Destructor, out diagnostic);
    }

    /// <summary>Called by the diagnostic probe's own destructor splice when both are enabled.</summary>
    internal static void SetThread() => _thread = GetCurrentThreadId();

    internal static bool Active { get; set; }

    internal static void Retire(uint effect)
    {
        if (!Active || GetCurrentThreadId() != _thread) return;
        int count = Odyssey.RetireQueuedEffectExpiries((nint)effect);
        if (count == 0) return;
        Retired += count;
        if (_logged++ < 40)
            PluginHost.Log($"effect fence: retired {count} queued expiry event(s) borrowing effect 0x{effect:X} before it was freed");
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Destructor(uint context)
    {
        try
        {
            if (!NativeMethods.IsReadable((nint)context, 36)) return;
            Retire(*(uint*)(context + NativeProbeContextRule.SavedEcx));
        }
        catch { }
    }
}

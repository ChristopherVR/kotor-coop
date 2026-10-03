using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>Prevent the enemy-sighted banner at its producer when co-op suppresses
/// auto-pause. Clearing server reason 2 alone leaves a registered pause panel behind.
/// Other reasons and all unpause calls execute the original engine function.</summary>
internal static unsafe class EnemyPausePresentationHook
{
    private const nint Site = 0x0062DEF0;
    private static ReadOnlySpan<byte> Prologue => [0x56, 0x8B, 0xF1, 0x8B, 0x86, 0x08, 0x01, 0x00, 0x00];
    public static bool Suppress;
    public static uint SuppressedCount;
    private static bool _installed;

    public static bool Install(out string diagnostic)
    {
        if (_installed) { diagnostic = "enemy pause presentation gate already installed"; return true; }
        byte* site = (byte*)Site;
        for (int i = 0; i < Prologue.Length; i++)
            if (site[i] != Prologue[i])
            { diagnostic = $"unexpected pause presentation bytes at +{i}; not patching"; return false; }
        nint stub = NativeMethods.VirtualAlloc(0, 96, 0x3000, 0x40);
        if (stub == 0) { diagnostic = "pause presentation allocation failed"; return false; }
        byte* s = (byte*)stub;
        int n = 0;
        s[n++] = 0x60; // pushad
        // reason at original esp+8, then paused at original esp+4.
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x28;
        s[n++] = 0xFF; s[n++] = 0x74; s[n++] = 0x24; s[n++] = 0x28;
        nint callback = (nint)(delegate* unmanaged[Cdecl]<int, int, int>)&OnPause;
        s[n++] = 0xE8; *(int*)(s + n) = (int)(callback - (stub + n + 4)); n += 4;
        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08;
        s[n++] = 0x85; s[n++] = 0xC0; // test callback result
        s[n++] = 0x61; // popad preserves flags
        s[n++] = 0x75; s[n++] = (byte)(Prologue.Length + 5);
        foreach (byte b in Prologue) s[n++] = b;
        s[n++] = 0xE9; *(int*)(s + n) = (int)(Site + Prologue.Length - (stub + n + 4)); n += 4;
        s[n++] = 0xC2; s[n++] = 0x08; s[n++] = 0x00; // original ret 8
        if (!NativeMethods.VirtualProtect(Site, (nuint)Prologue.Length, 0x40, out uint old))
        { diagnostic = "pause presentation protection failed"; return false; }
        site[0] = 0xE9; *(int*)(site + 1) = (int)(stub - (Site + 5));
        for (int i = 5; i < Prologue.Length; i++) site[i] = 0x90;
        NativeMethods.VirtualProtect(Site, (nuint)Prologue.Length, old, out _);
        _installed = true;
        diagnostic = $"enemy pause presentation gate at 0x{Site:X} (stub 0x{stub:X})";
        return true;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnPause(int paused, int reason)
    {
        if (!PausePresentationRule.SuppressEnemyBanner(Suppress, paused, reason)) return 0;
        SuppressedCount++;
        return 1;
    }
}

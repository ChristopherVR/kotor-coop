namespace KotorCoop.Plugin;

/// <summary>
/// Odyssey — the engine's own stealth-mode setter, for a peer's stand-in
/// (<c>stealth_visual_sync</c>, default OFF, NEEDS-LIVE). See docs/research/effect-sync.md
/// §12.4 for the disassembly this rests on.
/// </summary>
internal static unsafe partial class Odyssey
{
    /// <summary>
    /// <c>CSWSCreature::SetStealthMode(byte mode, int arg2)</c> — thiscall, <c>ret 8</c>, VERIFIED
    /// by disassembly (2026-09-18):
    /// <code>
    /// 0x0050EE50  mov al, [esp+4]            ; mode
    /// 0x0050EE54  cmp al, [ecx+0x4D1]        ; CSWSCreature.stealth_mode (byte)
    /// 0x0050EE5A  je  ret                    ; idempotent: no write when unchanged
    /// 0x0050EE5C  mov [ecx+0x4D1], al
    /// 0x0050EE62  mov eax, [esp+8]           ; arg2
    /// 0x0050EE66  mov [ecx+0xABC], eax
    /// 0x0050EE6C  call 0x4F6D70              ; CSWSCreature::ComputeModifiedMovementRate
    /// 0x0050EE71  ret 8
    /// </code>
    /// It is the ONLY writer of <c>+0x4D1</c> (eleven callers, all inside <c>CSWSCreature</c>:
    /// <c>SetActivity</c> — the stealth button's server-side landing, activity 1 —
    /// <c>ActivityManager</c>, <c>AIUpdate</c>, <c>LoadCreature</c>/<c>LoadFromTemplate</c>'s
    /// GFF reads), and it rebuilds its own derived state (the movement rate) itself, which is
    /// what makes it the right write under this project's engine-write rules rather than a raw
    /// poke of the byte. Every toggle caller passes <c>arg2 = 0</c> (<c>push 0; push edx</c>
    /// with edx = 1 or 0), which is what this passes too.
    ///
    /// What it does NOT do, and why the flag stays OFF: the callers also maintain an activity
    /// mask at <c>+0x9FC</c> (bit 0 = stealth, bit 1 = detect; bits 2/3 veto stealth) that this
    /// setter leaves alone, and whether the CLIENT twin picks the stealth shader up from the
    /// server byte on a stand-in — a body the engine never put into this mode on its own — has
    /// not been watched on a live game.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte, int, void> SetStealthModeFn =
        (delegate* unmanaged[Thiscall]<nint, byte, int, void>)0x0050EE50;

    /// <summary>CSWSCreature + this = stealth_mode (byte) — the field the setter above compares
    /// and writes; read back after the call so a refused/no-op write is reported, not latched.</summary>
    private const int CreStealthMode = 0x4D1;

    /// <summary>
    /// Puts a creature into (or takes it out of) the engine's stealth mode through the setter
    /// above. Returns whether the field reads the requested mode afterwards. MAIN THREAD ONLY.
    /// </summary>
    public static bool SetStealthMode(nint creature, bool on)
    {
        if (creature == 0)
        {
            return false;
        }

        byte mode = on ? (byte)1 : (byte)0;
        SetStealthModeFn(creature, mode, 0);
        return *(byte*)(creature + CreStealthMode) == mode;
    }
}

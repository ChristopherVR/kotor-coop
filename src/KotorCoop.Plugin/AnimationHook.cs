using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Captures the local player's one-shot animations by HOOKING the engine rather than polling
/// for them.
///
/// Polling could never work, which is why weapon flourishes and stims showed nothing on the
/// peer's screen (docs/research/animation-and-minimap.md): a fire-and-forget animation is
/// dispatched, played and finished between two samples, and some of them never touch an
/// object field at all. What every one of them DOES pass through is
/// <c>CSWCObject::InsertFireForgetAnimation</c> — 15 call sites in the whole binary, covering
/// the weapon draw, every attack swing, parries, and item use — so a detour there sees each
/// one exactly once, with no id table to maintain and no race.
///
/// The hook is deliberately tiny and read-only: it records an animation id and bumps a
/// sequence, then runs the engine's own code unchanged. It filters to the LOCAL PLAYER's
/// client twin, so peers' phantoms (which we drive ourselves) cannot echo back.
/// </summary>
internal static unsafe class AnimationHook
{
    /// <summary>CSWCObject::InsertFireForgetAnimation(u16 anim, int durMs, float speed, int, int).</summary>
    private const nint InsertFireForgetAnimation = 0x0063C760;

    /// <summary>Where the relocated prologue resumes.</summary>
    private const nint ResumeAt = 0x0063C768;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>
    /// The SEH prologue we relocate: <c>push -1</c> then <c>mov eax, fs:[0]</c>. Eight bytes,
    /// none of them position-dependent, so they move verbatim.
    /// </summary>
    private static ReadOnlySpan<byte> Prologue => [0x6A, 0xFF, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00];

    /// <summary>Last one-shot the local player started, and a counter so an identical repeat
    /// (a second flourish) still replicates. Written on the game thread by the detour, read on
    /// the same thread by the frame callback.</summary>
    private static ushort _lastAnimation;
    private static uint _sequence;

    private static bool _installed;

    public static ushort LastAnimation => _lastAnimation;

    public static uint Sequence => _sequence;

    /// <summary>
    /// Installs the detour. Verify-bytes-or-refuse, like the rest of the plugin's patches:
    /// an unrecognised prologue means we do nothing rather than corrupt the engine.
    /// MAIN THREAD ONLY, once, at startup.
    /// </summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)InsertFireForgetAnimation;
        for (int i = 0; i < Prologue.Length; i++)
        {
            if (site[i] != Prologue[i])
            {
                diagnostic = $"prologue mismatch at +{i} ({site[i]:X2}); not hooking animations";
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

        // The stub is ENTERED BY JMP, so esp still points at the caller's return address and
        // the arguments sit where the engine's own code expects them. pushad shifts that by
        // 0x20, which is why the animation argument is read from [esp+0x24].
        s[n++] = 0x60;                                       // pushad
        s[n++] = 0xFF; s[n++] = 0xB4; s[n++] = 0x24;         // push dword [esp+0x24]  (anim)
        *(int*)(s + n) = 0x24;
        n += 4;
        s[n++] = 0x51;                                       // push ecx               (this)

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, uint, void>)&OnFireForget;
        s[n++] = 0xE8;                                       // call OnFireForget
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08;         // add esp, 8   (cdecl cleanup)
        s[n++] = 0x61;                                       // popad

        for (int i = 0; i < Prologue.Length; i++)             // the relocated prologue
        {
            s[n++] = Prologue[i];
        }

        s[n++] = 0xE9;                                       // jmp InsertFireForgetAnimation+8
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(InsertFireForgetAnimation, (nuint)8, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9;                                      // jmp stub
        *(int*)(site + 1) = (int)(stub - (InsertFireForgetAnimation + 5));
        site[5] = 0x90;                                      // pad the remaining 3 bytes so a
        site[6] = 0x90;                                      // disassembler (and any resume
        site[7] = 0x90;                                      // into the middle) sees no garbage
        NativeMethods.VirtualProtect(InsertFireForgetAnimation, (nuint)8, old, out _);

        _installed = true;
        diagnostic = $"animation hook at 0x{InsertFireForgetAnimation:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs inside the engine's call, before its own body. Records the animation when it
    /// belongs to the LOCAL player's twin and does nothing else — no engine calls, no
    /// allocation, no logging: this fires on every swing of every creature in the area.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnFireForget(nint twin, uint animation)
    {
        try
        {
            if (twin == 0 || twin != Odyssey.GetLocalPlayerTwin())
            {
                return;
            }

            _lastAnimation = (ushort)animation;
            _sequence++;
        }
        catch
        {
            // Never let a managed fault unwind into the engine's animation dispatch.
        }
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Counts finished character creations: a marker on the engine's own chargen-save call, so the
/// log can say whether a stalled <c>join_mode = newcharacter</c> join is waiting on a player
/// who has not finished chargen or on a module that has not come up.
///
/// ⚠ THIS HOOK ONCE HAD A JOB AND LOST IT, AND THE REASON IS WORTH KEEPING. It was written to
/// catch <c>Temp\pifo.ifo</c> at the one instant chargen was believed to write it — after a
/// 250 ms <c>MenuFrame</c> poll never saw the file (measured live 2026-08-29: pressing Play
/// runs <c>CSWGuiClassSelection::BeginPlayGame 0x6DBDF0</c> and the plugin's own log recorded
/// "the main thread was gone for 1265 ms" across it, so no frame callback ran inside that
/// span). The hook fired correctly and the file was ZERO BYTES, still zero after a 400 ms spin
/// on the game thread. Disassembly then said why: <c>SaveCharGenCreature</c> WRITES NO PIFO. It
/// does not reference the string (the five xrefs of 0x745318 are all in
/// <c>StorePlayerCharacters</c>, <c>DoPCAutosave</c>, <c>LoadCharacterFromIFO</c> and
/// <c>LoadPCAutoSave</c>), and its own tail writes <c>TEMP:temp</c> with restype 0x7DF ("bic")
/// through <c>CResGFF::WriteGFFFile</c> at 0x613434 — a <c>.bic</c> character file, not a
/// module IFO. docs/research/character-login-chain.md §2's writer (a) is wrong, and
/// <see cref="NewCharacterCaptureController"/> now takes the character out of a real save
/// instead.
///
/// What is left is a five-byte marker on a call that happens exactly once per character
/// creation, which is cheap, safe and the only in-process evidence that the player got that
/// far. It is installed only while a join is armed, so an install that never uses
/// <c>join_mode = newcharacter</c> still carries no patch.
///
/// THE HOOK SITE is the CALL, not the function: <c>0x006DBE9B</c>, the sole call site of
/// <c>CSWCCreature::SaveCharGenCreature 0x6123E0</c> in the whole image (`tools/xrefs.py
/// 0x6123E0`: one direct call, zero address-literal occurrences — so nothing else can reach
/// it and this hook cannot fire for anything but a finished character creation). Five bytes,
/// one whole instruction, instruction-aligned on both ends, resuming at <c>0x006DBEA0</c>.
/// Patching the function's own prologue would have meant relocating across its SEH frame
/// setup for no benefit.
///
/// <code>
/// 0x006DBE98  8B 4F 68              mov ecx, [edi+0x68]     ; the CSWCCreature - set BEFORE the site
/// 0x006DBE9B  E8 40 65 F3 FF        call SaveCharGenCreature (0x6123E0)   &lt;- the 5 bytes we take
/// 0x006DBEA0  68 1C D7 73 00        push 0x73D71C            ; resume here; does not read eax
/// </code>
///
/// The stub calls the original FIRST, then our callback inside <c>pushad</c>/<c>popad</c> —
/// which also preserves <c>eax</c>, though the resume point does not read it. <c>ecx</c> is
/// loaded by the instruction before the site and is untouched by the jump, so the original
/// receives exactly the <c>this</c> it would have.
///
/// The callback now does nothing but increment a counter, which is what every other hook here
/// does (capture, and let the frame tick act).
///
/// Installed only while a new-character join is armed (<see cref="NewCharacterCaptureController"/>),
/// so an install that never uses <c>join_mode = newcharacter</c> carries no patch at all.
/// </summary>
internal static unsafe class CharGenSaveHook
{
    /// <summary>The sole call site of <c>SaveCharGenCreature</c>, inside
    /// <c>CSWGuiClassSelection::BeginPlayGame</c>.</summary>
    private const nint SaveSite = 0x006DBE9B;

    /// <summary>Where the relocated call resumes — the next instruction.</summary>
    private const nint ResumeAt = 0x006DBEA0;

    private const nint SaveCharGenCreature = 0x006123E0;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary><c>call 0x6123E0</c> — the exact five bytes the site must hold, refused rather
    /// than patched blind if they differ.</summary>
    private static ReadOnlySpan<byte> Site => [0xE8, 0x40, 0x65, 0xF3, 0xFF];

    private static bool _installed;

    /// <summary>Monotonic count of characters the engine has saved since this was installed.
    /// A counter rather than a bool so a second creation (the player cancelled and started
    /// over) is visible rather than indistinguishable from the first.</summary>
    public static uint Sequence { get; private set; }

    /// <summary>MAIN THREAD, once. Verify-bytes-or-refuse, like every other patch here.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)SaveSite;
        for (int i = 0; i < Site.Length; i++)
        {
            if (site[i] != Site[i])
            {
                diagnostic = $"call-site mismatch at +{i} (saw {site[i]:X2}, expected {Site[i]:X2}); "
                    + "the chargen-finished marker is unavailable";
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

        // call SaveCharGenCreature — ecx is already the creature (set at 0x006DBE98, the
        // instruction before the site, which the jump does not disturb). Thiscall with no
        // stack arguments, so there is nothing to forward and nothing to clean up.
        s[n++] = 0xE8;
        *(int*)(s + n) = (int)(SaveCharGenCreature - (stub + n + 4));
        n += 4;

        s[n++] = 0x60; // pushad — the file exists now; protect everything across our call

        nint callback = (nint)(delegate* unmanaged[Cdecl]<void>)&OnSaved;
        s[n++] = 0xE8;
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x61; // popad

        s[n++] = 0xE9; // jmp 0x006DBEA0
        *(int*)(s + n) = (int)(ResumeAt - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(SaveSite, (nuint)Site.Length, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xE9; // jmp stub
        *(int*)(site + 1) = (int)(stub - (SaveSite + 5));
        NativeMethods.VirtualProtect(SaveSite, (nuint)Site.Length, old, out _);

        _installed = true;
        diagnostic = $"chargen-save watch at 0x{SaveSite:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>
    /// Runs on the game's own thread, immediately after the engine's chargen-save call returns
    /// and before <c>BeginPlayGame</c> continues into the module load. Counts, and nothing
    /// else.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSaved()
    {
        try
        {
            Sequence++;
        }
        catch
        {
            // Never let a managed fault unwind into the engine's chargen path.
        }
    }
}

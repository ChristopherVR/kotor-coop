using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Observes the engine writing the character a player just created — the one instant
/// <c>Temp\pifo.ifo</c> holds a brand-new PC and nothing has cleaned it up yet.
///
/// ⚠ WHY THIS EXISTS AT ALL, WHEN A FILE POLL WAS TRIED FIRST AND IS SIMPLER. Measured live
/// (2026-08-29, rig instance 2, a real Quick Character through the retail screens): the poll —
/// 250 ms, on <c>MenuFrame</c> — NEVER SAW THE FILE. Pressing Play runs
/// <c>CSWGuiClassSelection::BeginPlayGame 0x6DBDF0</c>, which writes the pifo and then walks
/// straight into the module load; the plugin's own log recorded "the main thread was gone for
/// 1265 ms" across exactly that span, so no frame callback ran inside it, and by the time one
/// did the whole <c>Temp\</c> directory was empty. The window is not merely short — for a
/// frame-driven poll it does not exist. The quarter of a second a hook was said to buy turned
/// out to be the entire thing.
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
/// The stub calls the original FIRST (so the file exists before we look), then our callback
/// inside <c>pushad</c>/<c>popad</c> — which also preserves <c>eax</c>, though the resume point
/// does not read it. <c>ecx</c> is loaded by the instruction before the site and is untouched
/// by the jump, so the original receives exactly the <c>this</c> it would have.
///
/// ⚠ AND IT DOES REAL WORK IN THE CALLBACK — a file copy — which every other hook here refuses
/// to do (they capture into a ring and let the frame tick act). That is deliberate and is the
/// whole point: deferring is precisely what failed. This fires AT MOST ONCE per armed join, on
/// a thread that is about to spend seconds loading a module anyway, and it is wrapped in a
/// catch that can never unwind into the engine.
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
                    + "the created character cannot be captured";
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
    /// Runs on the game's own thread, immediately after the engine has written the new
    /// character to <c>TEMP:pifo</c> and before <c>BeginPlayGame</c> continues into the module
    /// load that clears <c>Temp\</c>. This is the only moment the file is there to be had.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSaved()
    {
        try
        {
            Sequence++;
            NewCharacterCaptureController.OnCharacterSaved();
        }
        catch
        {
            // Never let a managed fault unwind into the engine's chargen path.
        }
    }
}

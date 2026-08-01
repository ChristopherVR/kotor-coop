namespace KotorCoop.Plugin;

/// <summary>
/// Lets a pause stand even when no "PausedBy" record backs it.
///
/// The engine audits the player pause every server tick: <c>CSWSModule::TimeStopSanityCheck</c>
/// (0x004C6480) walks the session's PausedBy list, requires the registered pauser to also
/// pass a per-player validity test, and if nothing qualifies it force-toggles pause reason 1
/// off. In a plugin-driven loopback session nothing ever qualifies, so EVERY pause — the
/// space bar, the HUD button, the co-op mirror — was being reverted within one tick.
/// Measured live: masks went 1 and back to 0 in 2.75 ms.
///
/// The patch overwrites the entry of the force-unpause block itself (0x004C6503) with a
/// jump over the toggle, because BOTH failure paths land there: an empty PausedBy list
/// jumps straight in (0x004C649F), and a list with no valid entry falls through
/// (0x004C6501) — patching only the branch left the empty-list path live, and an unbacked
/// pause still died, just one tick later. The check exists for multiplayer hygiene (a
/// paused game whose pauser disconnected); in this mod the shared pause is owned by the
/// session mirror, which handles exactly that case itself.
/// </summary>
internal static unsafe class PauseSanityPatch
{
    /// <summary>The force-unpause block: mov ecx,[0x7A39FC] / mov ecx,[ecx+8] / push 1 /
    /// call TogglePauseState, ending at 0x4C6513. Both "no valid pauser" paths enter here.</summary>
    private const nint Site = 0x004C6503;

    private const uint PageExecuteReadWrite = 0x40;

    public static bool Install(out string diagnostic)
    {
        byte* site = (byte*)Site;

        if (site[0] == 0xEB && site[1] == 0x0E)
        {
            diagnostic = "pause sanity check already disarmed";
            return true;
        }

        // mov ecx, dword ptr [0x7A39FC] — the block's first instruction.
        if (site[0] != 0x8B || site[1] != 0x0D || *(uint*)(site + 2) != 0x007A39FC)
        {
            diagnostic = $"unexpected bytes at 0x{Site:X}: {site[0]:X2} {site[1]:X2}; not patching";
            return false;
        }

        if (!NativeMethods.VirtualProtect(Site, 2, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        site[0] = 0xEB; // jmp +0x0E -> 0x4C6513, past the TogglePauseState call
        site[1] = 0x0E;
        NativeMethods.VirtualProtect(Site, 2, old, out _);

        diagnostic = $"pause sanity check disarmed at 0x{Site:X}";
        return true;
    }
}

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Puts the other players on the MINIMAP — the corner one you actually watch while walking,
/// not just the M-screen.
///
/// The research verdict on this was "cannot be done data-only", and that half stands:
/// <c>CSWGuiMainInterface::DrawMap 0x0068AB10</c> was read end to end and draws the area
/// image, the fog of war and exactly ONE arrow. No map-note loop, no party loop. The engine's
/// three <c>CSWSAreaMap</c> party slots are the right shape but are rewritten every frame by
/// <c>UpdateMapData</c> and read back under the real party size, so a peer cannot borrow one
/// without genuine party membership. What was rejected as too costly was the CONCLUSION drawn
/// from that — that showing a peer needs its own GUI control, its own viewport and ~120 lines
/// on the render path.
///
/// It does not. The arrow is already there, already the right size, already inside an open
/// viewport with the layer started. Drawing it a second time at an offset is the whole
/// feature. Nothing is constructed, no viewport is set up, and the engine's own scroll maths
/// (image at <c>side/2 - pixel*scale</c>) means the offset is purely RELATIVE to the arrow —
/// so this never needs to know where the minimap is on screen.
///
/// The hook is one <b>call-site redirect</b>: the <c>call AurGUICloseViewport</c> at
/// <c>0x0068ACB4</c> becomes a call to a stub that draws the peers and then tail-jumps to the
/// real function. Five bytes, no prologue to relocate, and the site is reached only on the
/// path where the viewport was genuinely opened (0x0068AC8A jumps past it otherwise), so a
/// peer marker can never be drawn outside one.
/// </summary>
internal static unsafe class MinimapPeerHook
{
    /// <summary>The <c>call AurGUICloseViewport</c> inside DrawMap, after the arrow is drawn
    /// and before the viewport closes.</summary>
    private const nint CallSite = 0x0068ACB4;

    /// <summary>What that call targets: <c>AurGUICloseViewport 0x00459580</c>, cdecl, no
    /// arguments, so the stub can simply jump to it with the original return address in
    /// place.</summary>
    private const nint CloseViewport = 0x00459580;

    private const uint PageExecuteReadWrite = 0x40;
    private const uint MemCommitReserve = 0x3000;

    /// <summary>Set once the block exists; the draw needs peer positions and the hook fires
    /// from the engine's render path, which has no other way to reach them.</summary>
    private static SharedBlockView? _block;

    private static bool _installed;

    private static bool _faulted;

    public static void Bind(SharedBlockView block) => _block = block;

    /// <summary>Verify-bytes-or-refuse. MAIN THREAD, once.</summary>
    public static bool Install(out string diagnostic)
    {
        if (_installed)
        {
            diagnostic = "already installed";
            return true;
        }

        byte* site = (byte*)CallSite;

        // E8 rel32, and the rel32 must resolve to AurGUICloseViewport — checking the target
        // rather than just the opcode is what makes this specific to the right call.
        if (site[0] != 0xE8)
        {
            diagnostic = $"expected a call at 0x{CallSite:X}, found {site[0]:X2}; not drawing peers on the minimap";
            return false;
        }

        nint target = CallSite + 5 + *(int*)(site + 1);
        if (target != CloseViewport)
        {
            diagnostic = $"call at 0x{CallSite:X} goes to 0x{target:X}, not AurGUICloseViewport; not hooking";
            return false;
        }

        nint stub = NativeMethods.VirtualAlloc(0, 64, MemCommitReserve, PageExecuteReadWrite);
        if (stub == 0)
        {
            diagnostic = "VirtualAlloc failed";
            return false;
        }

        byte* s = (byte*)stub;
        int n = 0;

        // At the call site esi is the CSWGuiMainInterface and edi is the draw argument
        // DrawMap was handed (loaded at 0x0068AC8C). pushad preserves both across the
        // callback; the arguments are pushed cdecl, right to left.
        s[n++] = 0x60;                                       // pushad
        s[n++] = 0x57;                                       // push edi   (draw arg)
        s[n++] = 0x56;                                       // push esi   (the panel)

        nint callback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnBeforeCloseViewport;
        s[n++] = 0xE8;                                       // call OnBeforeCloseViewport
        *(int*)(s + n) = (int)(callback - (stub + n + 4));
        n += 4;

        s[n++] = 0x83; s[n++] = 0xC4; s[n++] = 0x08;         // add esp, 8
        s[n++] = 0x61;                                       // popad

        // Tail-jump, not a call: the return address the redirected call pushed is still on
        // the stack, so AurGUICloseViewport returns straight into DrawMap.
        s[n++] = 0xE9;                                       // jmp AurGUICloseViewport
        *(int*)(s + n) = (int)(CloseViewport - (stub + n + 4));

        if (!NativeMethods.VirtualProtect(CallSite, 5, PageExecuteReadWrite, out uint old))
        {
            diagnostic = "VirtualProtect failed";
            return false;
        }

        *(int*)(site + 1) = (int)(stub - (CallSite + 5));
        NativeMethods.VirtualProtect(CallSite, 5, old, out _);

        _installed = true;
        diagnostic = $"peers on the minimap at 0x{CallSite:X} (stub 0x{stub:X})";
        return true;
    }

    /// <summary>Runs with the minimap's viewport open, once per frame the minimap draws.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnBeforeCloseViewport(nint mainInterface, nint drawArg)
    {
        try
        {
            SharedBlockView? block = _block;
            if (block is null || _faulted || block.Command != SharedBlock.CommandSpawnAndDrive)
            {
                return;
            }

            Odyssey.DrawMinimapPeers(mainInterface, drawArg, block);
        }
        catch
        {
            // Never let a managed fault unwind into the renderer — and never try again:
            // a fault here would otherwise repeat every frame for the rest of the session.
            _faulted = true;
        }
    }
}

// Odyssey.GalaxyMapAccept.cs - `galaxyaccept`: presses Accept on the galaxy map THE WAY A
// REAL CLICK DOES, through CSWGuiInGameGalaxyMap::HandleInputEvent's own input dispatch - the
// exact precedent Odyssey.Dialogue.SkipKey.cs's `skipkey` already sets for the dialogue panel
// (`HandleInputEvent(panel, 0x27, 1)` through the panel's own vtable slot), and the reason
// GalaxyMapConfirmHook's splice can finally be exercised without a human clicking. See
// docs/research/galaxy-map-and-party-select.md's 2026-09-03 `galaxyaccept` addendum for the
// full derivation of the event code below (tools/disasm.py's byte-lookup-table decode,
// independently re-derived this pass) and the live recipe.
//
// WHY THIS CALLS HandleInputEvent DIRECTLY RATHER THAN JUMPING TO THE ACCEPT CASE HANDLER.
// GalaxyMapConfirmHook's own class remarks already establish that 0x006959D0 (the accept case
// body) is a JUMP-TABLE TARGET, not a callable function - there is no return address to
// preserve, so a synthesised `call` into it would corrupt the real stack. HandleInputEvent
// itself (0x00695980) IS a genuine callable function - __thiscall(this, eventCode, secondArg),
// `ret 8` - the same shape skipkey already calls for the dialogue panel, and the galaxy-map
// pass that investigated `galaxymap confirm` (2026-09-03, deliberately not shipped under that
// name) already did the byte-level RE to find which raw eventCode reaches the accept case.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>
    /// <c>CSWGuiInGameGalaxyMap::HandleInputEvent</c>'s vtable slot. Independently re-derived
    /// this pass by dumping the class's own vtable (<see cref="GalaxyMapVtableAddress"/>) out of
    /// <c>swkotor.exe</c>'s <c>.rdata</c>: slot 15 (+0x3C) holds <c>0x00695980</c>, matching the
    /// symbol database's own <c>HandleInputEvent</c> address exactly and the SAME slot index
    /// <c>Odyssey.Dialogue.SkipKey.cs</c>'s <c>PanelHandleInputEventSlot</c> already uses for
    /// the two dialogue-panel classes — confirming <c>CSWGuiPanel</c>'s virtual layout is one
    /// shared shape, not re-derived per subclass. A distinct constant from that file's own
    /// (identically-valued) one purely because C# will not let two partial-class files declare
    /// the same private member name twice.
    /// </summary>
    private const int GalaxyMapPanelHandleInputEventSlot = 0x3C;

    /// <summary>
    /// The raw GUI event code that reaches <c>HandleInputEvent</c>'s accept-case handler
    /// (<c>0x006959D0</c>, jump-table index 0) — derived this pass by reading the byte-lookup
    /// table at <c>0x695B0C</c> (185 entries, <c>eax = ebx - 0x27</c>, range <c>[0,0xB8]</c>)
    /// directly out of the executable and finding every <c>eax</c> whose table entry is 0.
    /// TWO raw values decode to the accept case, <c>0x27</c> and <c>0x2D</c>; this uses
    /// <c>0x27</c> — the SAME value <c>Odyssey.Dialogue.SkipKey.cs</c>'s own
    /// <c>DialogueSkipEvent</c> constant already uses for the dialogue panel's own Enter/click
    /// case, consistent with <c>0x27</c> being this engine's general "Enter/confirm" GUI event
    /// code rather than something galaxy-map-specific — the strongest evidence available
    /// (short of a live capture of a real click's own event code) that this is the keyboard/
    /// Enter-equivalent of the two, not an unrelated second trigger. <c>0x2D</c> is NOT sent by
    /// this primitive; see the research doc's own NEEDS-LIVE note for why one is enough to
    /// exercise the refusal and neither has been confirmed against a real click live.
    /// </summary>
    private const int GalaxyMapAcceptEvent = 0x27;

    /// <summary>
    /// LIVE-TEST PRIMITIVE: presses Accept on the galaxy map THE WAY A KEYPRESS/CLICK DOES —
    /// <c>HandleInputEvent(panel, 0x27, 1)</c> through the panel's own vtable slot, so
    /// <see cref="GalaxyMapConfirmHook"/> — spliced into the SAME case handler this event code
    /// reaches — actually sees the click and can allow or refuse it, exactly as it would a real
    /// one. No new engine address: this calls the already-VERIFIED
    /// <c>CSWGuiInGameGalaxyMap::HandleInputEvent</c> (docs/research/
    /// galaxy-map-and-party-select.md §1.1/§4 item 2's own choke-point RE), not the jump-table
    /// TARGET the hook itself splices — see this file's own header for why that distinction
    /// matters.
    ///
    /// Refuses (rather than firing blind) when the galaxy-map panel has never been constructed,
    /// when what <c>CGuiInGame+0x80</c> points at does not carry this class's own vtable (the
    /// same guard <see cref="TryReadGalaxyMapState"/> uses), or when the full-screen GUI is not
    /// currently up (<c>CGuiInGame+0x30</c> — the live-confirmed "is it actually open" signal,
    /// see <c>Odyssey.GalaxyMap.cs</c>'s own 2026-09-03 live-pass note; <c>+0x80</c> persists
    /// once allocated and is NOT a state flag).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool SendGalaxyMapAcceptKey(out string detail)
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            detail = "no in-game GUI resolved (not in a loaded module)";
            return false;
        }

        nint panel = *(nint*)(gui + InGameGuiGalaxyMapOffset);
        if (panel == 0)
        {
            detail = "no galaxy-map panel has ever been constructed (CGuiInGame+0x80 = 0) - "
                + "open it first with `galaxymap open`";
            return false;
        }

        nint vtable = *(nint*)panel;
        if (vtable != GalaxyMapVtableAddress)
        {
            detail = $"CGuiInGame+0x80 (0x{panel:X}) does not carry CSWGuiInGameGalaxyMap's own "
                + $"vtable (saw 0x{vtable:X}, expected 0x{GalaxyMapVtableAddress:X}) - refusing "
                + "rather than sending an event into an unrelated object";
            return false;
        }

        int busyBefore = *(int*)(gui + InGameGuiBusyOffset);
        if (busyBefore == 0)
        {
            detail = "the galaxy map is not the open full-screen GUI (CGuiInGame+0x30 = 0) - "
                + "open it first with `galaxymap open`";
            return false;
        }

        var handleInputEvent = (delegate* unmanaged[Thiscall]<nint, int, int, void>)
            (*(nint*)(vtable + GalaxyMapPanelHandleInputEventSlot));

        if (!LooksLikeCodePointer((nint)handleInputEvent))
        {
            detail = $"HandleInputEvent's vtable slot (+0x{GalaxyMapPanelHandleInputEventSlot:X}) "
                + $"does not look like code (0x{(nint)handleInputEvent:X})";
            return false;
        }

        uint refusedBefore = GalaxyMapConfirmHook.RefusedCount;
        uint allowedBefore = GalaxyMapConfirmHook.AllowedCount;

        handleInputEvent(panel, GalaxyMapAcceptEvent, 1);

        int busyAfter = *(int*)(gui + InGameGuiBusyOffset);
        detail = $"HandleInputEvent(panel, 0x{GalaxyMapAcceptEvent:X}, 1) sent — "
            + $"CGuiInGame+0x30 {busyBefore} -> {busyAfter}, GalaxyMapConfirmHook "
            + $"refused {refusedBefore} -> {GalaxyMapConfirmHook.RefusedCount}, allowed "
            + $"{allowedBefore} -> {GalaxyMapConfirmHook.AllowedCount}";
        return true;
    }
}

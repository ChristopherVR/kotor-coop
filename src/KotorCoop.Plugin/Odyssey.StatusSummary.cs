// Odyssey.StatusSummary.cs - the retail "Status Summary" box (CSWGuiStatusSummary: the
// journal/XP/credits recap the engine raises after a game load, over a paused world), and the
// one thing this plugin needs from it: dismissing it the way its own OK button does.
//
// Found 2026-09-03 by FieldWriteWatch while chasing the galaxy-map relay's silent no-op on a
// save-joined client: the "loader leftover" every earlier pass blamed on the save/load panel is
// this box. CGuiInGame::ShowStatusSummary 0x0062EF90 (frame 0x62F01F in the watchpoint's stack
// capture) runs SetPausedByCombat(1, 6, 0), AddPanel(+0xA8, 1, 1) and SetInputClass(2, 0)
// after the load, and on an unattended client nobody ever clicks OK - so the world stays
// paused-by-GUI, the modal stack keeps the box, CGuiInGame+0x30 stays 1 and the input class
// stays 2 for the rest of the session. A human client clicks OK and none of this applies.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>CGuiInGame + this = CSWGuiStatusSummary* (allocated once, persists; whether it
    /// is UP is the GUI manager's registration, not this pointer). VERIFIED, fresh disasm of
    /// <c>ShowStatusSummary</c> 0x0062EF90 (<c>mov ecx,[esi+0xA8]; push 1; push 1; push ecx;
    /// mov ecx,[esi+0x38]; call AddPanel</c>).</summary>
    private const int InGameGuiStatusSummaryOffset = 0xA8;

    /// <summary>CSWGuiStatusSummary's vtable (constructor 0x006272A0 stores it at +0);
    /// <c>HandleInputEvent</c> 0x00625AC0 sits at the same +0x3C slot every CSWGuiPanel uses.</summary>
    private const nint StatusSummaryVtableAddress = 0x0074FF68;

    /// <summary>
    /// The Status Summary's own OK: <c>HandleInputEvent(panel, 0x27, 1)</c> through the vtable —
    /// the case at 0x00625AE7 (events 0x27/0x28/0x2D/0x2E) that marks the panel to destroy,
    /// calls <c>SetInputClass(0, 1)</c>, plays the GUI sound and, unless the panel's own
    /// <c>+0x7C</c> bit 0 says otherwise, <c>SetPausedByCombat(0, 0, 0)</c> to unpause the
    /// world the box paused. Exactly what a click does, so nothing is left half-open: the modal
    /// stack, <c>CGuiInGame+0x30</c>, the input class and the pause all come back together.
    ///
    /// Returns true having done nothing when the box is not up (the common case on a host, or
    /// on any client whose player clicked it away). MAIN THREAD ONLY.
    /// </summary>
    internal static bool DismissStatusSummary(out string detail)
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            detail = "no in-game GUI resolved (not in a loaded module)";
            return false;
        }

        nint panel = *(nint*)(gui + InGameGuiStatusSummaryOffset);
        if (panel == 0)
        {
            detail = "no status-summary panel constructed (CGuiInGame+0xA8 = 0) - nothing to dismiss";
            return true;
        }

        nint vtable = *(nint*)panel;
        if (vtable != StatusSummaryVtableAddress)
        {
            detail = $"CGuiInGame+0xA8 (0x{panel:X}) does not carry CSWGuiStatusSummary's vtable "
                + $"(saw 0x{vtable:X}, expected 0x{StatusSummaryVtableAddress:X}) - refusing";
            return false;
        }

        nint manager = *(nint*)(gui + InGameGuiManagerOffset);
        if (manager == 0 || GuiPanelExistsFn(manager, panel) == 0)
        {
            detail = "the status summary is not up (panel not registered with the GUI manager) - nothing to dismiss";
            return true;
        }

        var handleInputEvent = (delegate* unmanaged[Thiscall]<nint, int, int, void>)
            (*(nint*)(vtable + GalaxyMapPanelHandleInputEventSlot));
        if (!LooksLikeCodePointer((nint)handleInputEvent))
        {
            detail = $"HandleInputEvent's vtable slot does not look like code (0x{(nint)handleInputEvent:X})";
            return false;
        }

        int busyBefore = *(int*)(gui + InGameGuiBusyOffset);
        int modalsBefore = ReadModalStackCount(gui);
        handleInputEvent(panel, GalaxyMapAcceptEvent, 1);

        nint client = GetClientInternal();
        string inputClass = client == 0 ? "?" : (*(int*)(client + ClientInternalInputClassOffset)).ToString();
        detail = $"status summary dismissed with its own OK (HandleInputEvent 0x27) — CGuiInGame+0x30 "
            + $"{busyBefore} -> {*(int*)(gui + InGameGuiBusyOffset)}, modal stack {modalsBefore} -> "
            + $"{ReadModalStackCount(gui)}, input class now {inputClass}";
        return true;
    }
}

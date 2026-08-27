// Odyssey.PartySelect.cs - the `partyselect ok` live-test primitive (item 2,
// docs/research/galaxy-map-and-party-select.md's 2026-08-28 addendum): confirms the native
// party-selection screen (CSWGuiPartySelection) with its current on-screen selection, through
// the panel's own OK/Done handler, ConfirmPartySelection - the same code path a real click
// reaches. Same resolution chain PartySelectSuppressionController already proves live
// (CGuiInGame+0x078, vtable-guarded); this file adds the CONFIRM half rather than the
// suppress-and-teardown half that class already ships.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- resolution chain (same offsets PartySelectSuppressionController verifies live) -----

    private const nint PartySelectAppManagerPtr = 0x007A39FC;
    private const int PartySelectAppClientOffset = 0x04;
    private const int PartySelectClientInternalOffset = 0x04;

    /// <summary>CClientExoAppInternal + this = CGuiInGame*, or 0 outside a loaded module.</summary>
    private const int PartySelectClientInternalGui = 0x40;

    /// <summary>CGuiInGame + this = CSWGuiPartySelection*, or null. docs/research/
    /// galaxy-map-and-party-select.md §2.1 (VERIFIED, cross-checked twice) — the same offset
    /// <see cref="PartySelectSuppressionController"/> already reads live.</summary>
    private const int PartySelectGuiPanelOffset = 0x78;

    /// <summary>CSWGuiPartySelection's own vtable (symbol database) — the guard that a +0x78
    /// pointer really is this class before anything calls through it.</summary>
    private const nint PartySelectionVtableAddr = 0x00756D28;

    // ---- the shared "is a message box currently up" precondition ----------------------------
    //
    // CSWGuiPartySelection::ConfirmPartySelection (0x006BEC90, thiscall, ret 4) itself resolves
    // AppManagerPtr -> +4 client -> an internal call (0x005ED690, collapsing "+4 internal ->
    // +0x40 gui") -> +0x98 messageBox -> +0x64 bit 0, and only calls AcceptParty (0x006BE560)
    // when that bit is SET (disassembly-verified, tools/disasm.py 0x6BEC90). This is the exact
    // same field docs/research/session-save.md's `loadgame` precondition trap already found and
    // named: freshly constructed the byte reads 0x06 (bit 0 clear) and stays clear for the whole
    // life of an all-console-driven rig session, because nothing here ever opens a real
    // pause-menu message box to flip it. Poking it directly (the same live-verified workaround
    // `msgboxpoke.ps1`/loadgame's own fix uses) is the only way this cheat can ever reach
    // AcceptParty on this rig - see session-save.md's "message-box precondition trap" sections.
    private const int PartySelectMessageBoxOffset = 0x98;
    private const int PartySelectMessageBoxBit0Offset = 0x64;

    /// <summary>CSWGuiPartySelection::ConfirmPartySelection(this, unusedFiringControl) —
    /// thiscall, ret 4. Disassembly-verified (tools/disasm.py 0x6BEC90): the one stack dword is
    /// never dereferenced in the function body, so 0 is safe to pass — matches the shape a
    /// button's own AddEvent callback receives (ecx=owner, stack=firing control, unread here).
    /// Runs the message-box precondition check, then (only if it passes) calls AcceptParty,
    /// which performs the real CSWPartyTable::SpawnNPC/AddMember pair
    /// <see cref="PartyRosterController"/> already mirrors for every other roster change, and
    /// tears the panel down like a real Done click.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> ConfirmPartySelectionFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x006BEC90;

    /// <summary>Resolves the GUI root (CGuiInGame*) once, guarded at every hop — the shared
    /// first half both <see cref="GetPartySelectPanel"/> and <see cref="ConfirmPartySelect"/>
    /// need, so the panel and its message-box sibling are always read from the SAME resolved
    /// gui pointer rather than two independent walks that could observe different frames.</summary>
    private static nint ResolveGuiInGame()
    {
        nint appManager = Deref(PartySelectAppManagerPtr);
        if (appManager == 0)
        {
            return 0;
        }

        nint client = Deref(appManager + PartySelectAppClientOffset);
        if (client == 0)
        {
            return 0;
        }

        nint clientInternal = Deref(client + PartySelectClientInternalOffset);
        if (!LooksLikeHeapPointer(clientInternal))
        {
            return 0;
        }

        nint gui = Deref(clientInternal + PartySelectClientInternalGui);
        return LooksLikeHeapPointer(gui) ? gui : 0;
    }

    /// <summary>The live CSWGuiPartySelection*, vtable-verified, or 0 (no module loaded, the
    /// panel not open, or a pointer that does not match the offsets above). Same resolution
    /// <see cref="PartySelectSuppressionController.GetPartySelectionPanel"/> already proves live;
    /// duplicated here rather than shared because that method is private to a different
    /// controller and this file must not assume it stays that way.</summary>
    public static nint GetPartySelectPanel()
    {
        nint gui = ResolveGuiInGame();
        if (gui == 0)
        {
            return 0;
        }

        nint panel = Deref(gui + PartySelectGuiPanelOffset);
        if (!LooksLikeHeapPointer(panel))
        {
            return 0;
        }

        return Deref(panel) == PartySelectionVtableAddr ? panel : 0;
    }

    /// <summary>
    /// Confirms the currently-open native party-selection screen with its current on-screen
    /// selection, exactly as a real Done/Accept click would. Pokes the shared message-box
    /// precondition bit first (see the class comment) — a no-op if it is already set, so this
    /// is safe to call repeatedly. Returns false with no panel resolvable; true means
    /// <c>ConfirmPartySelectionFn</c> was actually called (not a guarantee the roster changed —
    /// the caller reads the party roster back to confirm that, the same way every other
    /// staging cheat's own "applied" answer is a request made, not a downstream fact proven).
    /// MAIN THREAD ONLY - do not call while the script VM is mid-call.
    /// </summary>
    public static bool ConfirmPartySelect(out string detail)
    {
        nint gui = ResolveGuiInGame();
        if (gui == 0)
        {
            detail = "no in-game GUI resolvable (no module loaded?)";
            return false;
        }

        nint panel = Deref(gui + PartySelectGuiPanelOffset);
        if (!LooksLikeHeapPointer(panel) || Deref(panel) != PartySelectionVtableAddr)
        {
            detail = "no party-selection panel is open on this instance";
            return false;
        }

        nint messageBox = Deref(gui + PartySelectMessageBoxOffset);
        if (!LooksLikeHeapPointer(messageBox))
        {
            detail = "party-selection panel found, but the message-box precondition object "
                + "(gui+0x98) did not resolve - refusing rather than guessing";
            return false;
        }

        byte* bit0 = (byte*)(messageBox + PartySelectMessageBoxBit0Offset);
        bool wasSet = (*bit0 & 1) != 0;
        *bit0 |= 1;

        ConfirmPartySelectionFn(panel, 0);

        detail = wasSet
            ? "confirmed (message-box precondition was already set)"
            : "confirmed (poked the message-box precondition bit first, same as loadgame's own workaround)";
        return true;
    }
}

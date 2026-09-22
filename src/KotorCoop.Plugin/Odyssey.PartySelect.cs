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

    /// <summary>CGuiInGame + this = CSWGuiManager*. This manager's registration list is the
    /// safe open-panel test; the cached +0x78 field can retain a freed panel.</summary>
    private const int PartySelectGuiManagerOffset = 0x38;

    // CSWGuiManager::PanelExists's helper at 0x40ABA0 reads count at +0x8C and an
    // address array at +0x88. Bound both fields and the full array before entering native code.
    private const int PartyManagerPanelArrayOffset = 0x88;
    private const int PartyManagerPanelCountOffset = 0x8C;
    private const int MaximumRegisteredPanels = 512;

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

    /// <summary>CSWGuiManager::PanelExists(manager, panel); compares the value against its
    /// registered panel array without dereferencing the candidate.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> PanelExistsFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x0040BD70;

    /// <summary>Resolves the GUI root (CGuiInGame*) once, guarded at every hop — the shared
    /// first half both <see cref="GetPartySelectPanel"/> and <see cref="ConfirmPartySelect"/>
    /// need, so the panel and its message-box sibling are always read from the SAME resolved
    /// gui pointer rather than two independent walks that could observe different frames.</summary>
    private static nint ResolveGuiInGame()
    {
        if (!NativeMethods.IsReadable(PartySelectAppManagerPtr, (nuint)sizeof(nint)))
        {
            return 0;
        }

        nint appManager = Deref(PartySelectAppManagerPtr);
        if (!LooksLikeHeapPointer(appManager)
            || !NativeMethods.IsReadable(appManager + PartySelectAppClientOffset, (nuint)sizeof(nint)))
        {
            return 0;
        }

        nint client = Deref(appManager + PartySelectAppClientOffset);
        if (!LooksLikeHeapPointer(client)
            || !NativeMethods.IsReadable(client + PartySelectClientInternalOffset, (nuint)sizeof(nint)))
        {
            return 0;
        }

        nint clientInternal = Deref(client + PartySelectClientInternalOffset);
        if (!LooksLikeHeapPointer(clientInternal)
            || !NativeMethods.IsReadable(clientInternal + PartySelectClientInternalGui, (nuint)sizeof(nint)))
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

        if (!TryReadGuiPanelPointers(gui, out nint manager, out nint panel)
            || !TryPanelIsRegistered(manager, panel, out bool registered)
            || !registered
            || !NativeMethods.IsReadable(panel, 4))
        {
            return 0;
        }

        return Deref(panel) == PartySelectionVtableAddr ? panel : 0;
    }

    /// <summary>
    /// Confirms the currently-open native party-selection screen with its current on-screen
    /// selection, exactly as a real Done/Accept click would. Pokes the shared message-box
    /// precondition bit first (see the class comment) — a no-op if it is already set, so this
    /// is safe to call repeatedly. It verifies manager registration before dereferencing the
    /// panel, snapshots its exit_script string, and reports whether the panel remains registered
    /// after the call. It does not claim that the script's effects completed.
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

        if (!TryReadGuiPanelPointers(gui, out nint manager, out nint panel)
            || !TryPanelIsRegistered(manager, panel, out bool registered)
            || !registered
            || !NativeMethods.IsReadable(panel, 0x7C)
            || Deref(panel) != PartySelectionVtableAddr)
        {
            detail = "no registered party-selection panel is open on this instance";
            return false;
        }

        if (!TryReadExitScript(panel, out string exitScript, out bool exitScriptEmpty))
        {
            detail = "registered party-selection panel found, but its exit_script CExoString "
                + "did not pass bounded pointer/length checks - refusing to confirm";
            return false;
        }

        if (!NativeMethods.IsReadable(gui + PartySelectMessageBoxOffset, (nuint)sizeof(nint)))
        {
            detail = "party-selection panel found, but the message-box pointer field is unreadable";
            return false;
        }

        nint messageBox = Deref(gui + PartySelectMessageBoxOffset);
        if (!LooksLikeHeapPointer(messageBox)
            || !NativeMethods.IsWritable(messageBox + PartySelectMessageBoxBit0Offset, 1))
        {
            detail = "party-selection panel found, but the message-box precondition object "
                + "(gui+0x98) or writable bit did not resolve - refusing rather than guessing";
            return false;
        }

        byte* bit0 = (byte*)(messageBox + PartySelectMessageBoxBit0Offset);
        bool wasSet = (*bit0 & 1) != 0;
        *bit0 |= 1;

        ConfirmPartySelectionFn(panel, 0);

        // PanelExists is safe with the captured pointer even if native code freed the panel:
        // it only compares the address against the manager's current registered addresses.
        string panelState;
        nint currentGui = ResolveGuiInGame();
        if (currentGui != gui || !TryReadGuiManager(currentGui, out nint currentManager)
            || currentManager != manager)
        {
            // An exit script can transition the module and free the captured GUI and manager.
            // Do not call PanelExists on the old manager after that callback.
            panelState = "gui-changed";
        }
        else if (TryPanelIsRegistered(currentManager, panel, out bool stillRegistered))
        {
            panelState = stillRegistered ? "registered" : "gone";
        }
        else
        {
            panelState = "unknown";
        }
        bool dispatchExpected = !exitScriptEmpty;
        PluginHost.Log($"party-select confirm diagnostic: exit_script="
            + (exitScriptEmpty ? "<empty>" : exitScript)
            + $"; confirm call returned; native VM dispatch expected={(dispatchExpected ? "yes (nonempty script field)" : "no (empty field)")}; "
            + $"dispatch directly observed=no; panel registration after call={panelState}; "
            + $"message-box bit initially {(wasSet ? "set" : "clear")}.");

        // This is the 64-byte shared cheat-detail field with its `partyselect ok: ` prefix;
        // keep the exact exit resref first so the useful fact survives truncation.
        detail = $"exit={(exitScriptEmpty ? "<empty>" : exitScript)};call=ok;panel={panelState}";
        return true;
    }

    /// <summary>Reads the panel's native CExoString { char* at +0, length at +4 }. The
    /// verified CExoString constructor at 0x5E5B00 scans through NUL, increments the stored
    /// length and copies the terminator, so the length includes NUL. A resref is capped at 16.</summary>
    private static bool TryReadExitScript(nint panel, out string value, out bool isEmpty)
    {
        value = string.Empty;
        isEmpty = false;

        nint field = panel + 0x74;
        if (!NativeMethods.IsReadable(field, 8))
        {
            return false;
        }

        nint chars = Deref(field);
        uint length = *(uint*)(field + 4);
        if (chars == 0 && length == 0)
        {
            isEmpty = true;
            return true;
        }

        if (!LooksLikeHeapPointer(chars) || length is 0 or > 17
            || !NativeMethods.IsReadable(chars, (nuint)length))
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        int written = 0;
        bool terminated = false;
        for (uint i = 0; i < length; i++)
        {
            byte b = *(byte*)(chars + i);
            if (b == 0)
            {
                if (i != length - 1)
                {
                    return false;
                }

                terminated = true;
                break;
            }

            if (i >= (uint)bytes.Length || b is < 0x21 or > 0x7E)
            {
                return false;
            }

            bytes[written++] = b;
        }

        if (!terminated)
        {
            return false;
        }

        if (written == 0)
        {
            isEmpty = true;
            return true;
        }

        value = System.Text.Encoding.ASCII.GetString(bytes[..written]);
        return true;
    }

    private static bool TryReadGuiPanelPointers(nint gui, out nint manager, out nint panel)
    {
        manager = 0;
        panel = 0;
        if (!TryReadGuiManager(gui, out manager)
            || !NativeMethods.IsReadable(gui + PartySelectGuiPanelOffset, (nuint)sizeof(nint)))
        {
            return false;
        }

        panel = Deref(gui + PartySelectGuiPanelOffset);
        return LooksLikeHeapPointer(panel);
    }

    private static bool TryReadGuiManager(nint gui, out nint manager)
    {
        manager = 0;
        if (!LooksLikeHeapPointer(gui)
            || !NativeMethods.IsReadable(gui + PartySelectGuiManagerOffset, (nuint)sizeof(nint)))
        {
            return false;
        }

        manager = Deref(gui + PartySelectGuiManagerOffset);
        return LooksLikeHeapPointer(manager);
    }

    /// <summary>Reproduces the exact data reads made by PanelExists's 0x40ABA0 helper before
    /// calling it: manager+0x8C count, manager+0x88 array pointer, then count pointer slots.</summary>
    private static bool TryPanelIsRegistered(nint manager, nint panel, out bool registered)
    {
        registered = false;
        if (!LooksLikeHeapPointer(manager) || !LooksLikeHeapPointer(panel)
            || !NativeMethods.IsReadable(manager + PartyManagerPanelArrayOffset, 8))
        {
            return false;
        }

        nint entries = Deref(manager + PartyManagerPanelArrayOffset);
        int count = *(int*)(manager + PartyManagerPanelCountOffset);
        if (count is < 0 or > MaximumRegisteredPanels)
        {
            return false;
        }

        if (count == 0)
        {
            return true;
        }

        nuint bytes = (nuint)count * (nuint)sizeof(nint);
        if (!LooksLikeHeapPointer(entries) || !NativeMethods.IsReadable(entries, bytes))
        {
            return false;
        }

        registered = PanelExistsFn(manager, panel) != 0;
        return true;
    }
}

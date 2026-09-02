// Odyssey.GalaxyMap.cs - `galaxymap open|close` live-test primitives (Tier 2 item 18 task 2's
// own "Live test plan" follow-up, docs/research/galaxy-map-and-party-select.md): the confirm
// refusal GalaxyMapConfirmHook splices has sat "Built, NEEDS-LIVE" (docs/limitations.md) because
// nothing on the console could open the galaxy map to begin with. See that research doc's
// 2026-09-03 addendum for the full recipe and the fresh disassembly this file is built from.
//
// CGuiInGame::ShowGalaxyMapGui (0x0062D040) / HideGalaxyMapGui (0x0062D1D0) - the research doc's
// §1.1 already named both addresses (symbol database), but neither had been independently
// disassembled for its OWN ABI the way ShowStoreGui was (Odyssey.Store.cs's own section header
// tells that story). Fresh disasm this pass (tools/disasm.py, sha256-matched GOG v1.0.3):
//
// ShowGalaxyMapGui(this, int nPlanet) - __thiscall, ONE stack arg, `ret 4` - VERIFIED. Gates on
// the SAME two preconditions ShowStoreGui does: InGameGuiInitializedOffset (+0x108) nonzero,
// InGameGuiBusyOffset (+0x30) zero - matching the note already recorded on InGameGuiBusyOffset's
// own doc comment ("fresh disasm of ShowStoreGui (0x0062E339) and ShowGalaxyMapGui
// (0x0062D069)"), now confirmed end to end by this pass's own full trace - PLUS a third gate
// this file adds to the record: GuiConversationActive (CGuiInGame+0xB4) must be zero (the exact
// same conversation-active refusal OpenStore already reports). Any gate failing skips straight
// to the shared exit; the map does not open, no crash, `ret 4` either way.
//
// nPlanet feeds CServerExoApp::GetPartyTable (0x004AEE70 - a bare 3-instruction accessor, no
// stack args of its own, VERIFIED fresh disasm) -> CSWPartyTable::SetSelectedPlanet
// (0x00563B60, VERIFIED full disasm this pass): bounds-checks nPlanet against [0,16) AND
// requires BOTH available_planets[nPlanet] (+0x60) and selectable_planets[nPlanet] (+0xA0)
// nonzero before writing galaxy_map_selected_planet (+0xE0) - silently no-ops on an
// out-of-range or unavailable index, without affecting whether the map itself opens. This
// upgrades §1.1's own "+0x60/+0xA0/+0xE0 ... treat as VERIFIED-by-paired-accessor-name" caveat
// to a genuine disassembly verification (both array offsets and the write offset all read
// exactly as that section already guessed). Passing -1 (SelectedPlanetNone) is therefore a
// safe, deliberate "leave the selection alone" sentinel for this verb: SetSelectedPlanet's own
// bounds check (`jl`/`jge` against 0/0x10) rejects it immediately and the panel still opens
// normally, showing whatever planet was last selected (or nothing, on a fresh map).
//
// HideGalaxyMapGui(this) - __thiscall, NO stack args, bare `ret` - VERIFIED, fresh disasm. Gates
// only on InGameGuiInitializedOffset; unlike HideSWInGameGui it does NOT bail on the GUI
// manager's modal-stack count, so no popmodal-style companion primitive is needed here.
//
// Both functions were fully traced end to end this pass (every branch read, not just the
// prologue) - see docs/research/galaxy-map-and-party-select.md's 2026-09-03 addendum for the
// raw tools/disasm.py transcript this comment summarises.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>CGuiInGame + this = CSWGuiInGameGalaxyMap*, or null. VERIFIED,
    /// docs/research/galaxy-map-and-party-select.md §1.1 (symbol database) and this file's own
    /// fresh disasm of ShowGalaxyMapGui's own write to this exact field
    /// (`mov dword ptr [esi+0x80], eax` at 0x0062D17F/0x0062D185).
    ///
    /// ⚠ NOT an "is the map open" flag. Live 2026-09-03 02:46 (client, ebo_m12aa): the pointer
    /// stayed <c>0x1B9161B0</c> across open → close → open — the panel object is allocated once
    /// and <c>HideGalaxyMapGui</c> does not null it. What DOES move is the full-screen-GUI
    /// field <see cref="InGameGuiBusyOffset"/> (+0x30, 0 → 1 on open, back to 0 on close) and
    /// the GUI manager's modal stack (0 → 1 → 0), so those are what the two verbs report
    /// success on; the pointer is printed for information only.</summary>
    private const int InGameGuiGalaxyMapOffset = 0x80;

    /// <summary>Passed as <c>nPlanet</c> to <see cref="ShowGalaxyMapGuiFn"/> when the caller has
    /// no planet selection to make — see the section header above for why this is safe
    /// (<c>SetSelectedPlanet</c>'s own bounds check rejects it as a no-op; the map opens
    /// regardless, exactly like a bare `ShowGalaxyMap()` VM call with no prior selection would).</summary>
    private const int SelectedPlanetNone = -1;

    /// <summary>CGuiInGame::ShowGalaxyMapGui(this, int nPlanet) 0x0062D040, ret 4. VERIFIED,
    /// fresh disasm this pass — see the section header above for the full trace.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> ShowGalaxyMapGuiFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x0062D040;

    /// <summary>CGuiInGame::HideGalaxyMapGui(this) 0x0062D1D0, bare ret (no stack args).
    /// VERIFIED, fresh disasm this pass — see the section header above for the full trace.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> HideGalaxyMapGuiFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x0062D1D0;

    /// <summary>
    /// Opens the galaxy-map GUI for the local player through the real engine entry point — the
    /// `galaxymap open` live-test primitive built to finally exercise
    /// <see cref="GalaxyMapConfirmHook"/>'s refusal without a human clicking (docs/research/
    /// galaxy-map-and-party-select.md's 2026-09-03 addendum). Takes no arguments: unlike
    /// <see cref="OpenStore"/> there is no target object to name, only "this instance's own
    /// map" — see <see cref="InGameGuiGalaxyMapOffset"/>. Reports the same three gates
    /// <c>ShowGalaxyMapGui</c> itself checks (HUD initialised, full-screen GUI not busy, no
    /// active conversation) by name when it refuses, rather than a bare "did not open" — the
    /// same discipline <see cref="OpenStore"/> already follows for its own three gates.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool OpenGalaxyMap(out string detail)
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            detail = "no in-game GUI resolved (not in a loaded module)";
            return false;
        }

        if (*(int*)(gui + InGameGuiInitializedOffset) == 0)
        {
            detail = "the HUD is not initialised yet (still loading)";
            return false;
        }

        int fullScreenGui = *(int*)(gui + InGameGuiBusyOffset);
        if (fullScreenGui != 0)
        {
            detail = "the full-screen in-game GUI is up (equipment / inventory / character / "
                + $"map / journal / options) — close it first, e.g. `closegui` (CGuiInGame+0x30 = "
                + $"{fullScreenGui}, GUI-manager modal stack = {ReadModalStackCount(gui)})";
            return false;
        }

        if (*(int*)(gui + GuiConversationActive) != 0)
        {
            detail = "a conversation is active";
            return false;
        }

        int modalsBefore = ReadModalStackCount(gui);

        ShowGalaxyMapGuiFn(gui, SelectedPlanetNone);

        nint panel = *(nint*)(gui + InGameGuiGalaxyMapOffset);
        int busyAfter = *(int*)(gui + InGameGuiBusyOffset);
        int modalsAfter = ReadModalStackCount(gui);
        detail = $"ShowGalaxyMapGui called — CGuiInGame+0x30 0 -> {busyAfter}, GUI-manager modal "
            + $"stack {modalsBefore} -> {modalsAfter}, panel 0x{panel:X}"
            + (busyAfter == 0 ? " (the engine did not open it)" : " (open)");
        return busyAfter != 0;
    }

    /// <summary>
    /// Closes the galaxy-map GUI through the real engine teardown — the `galaxymap close`
    /// live-test primitive, and the counterpart to <see cref="OpenGalaxyMap"/>. Deliberately a
    /// no-op (reported APPLIED, not refused) when the map is already closed — same reasoning
    /// <see cref="CloseFullScreenGui"/>'s own doc comment gives: a retry loop that treats
    /// "already fine" as a failure is how a live recipe stalls on its own success. Unlike
    /// <c>HideSWInGameGui</c>, <c>HideGalaxyMapGui</c> does not gate on the GUI manager's modal
    /// stack (VERIFIED, fresh disasm — see the section header above), so there is no
    /// <c>popmodal</c>-style companion primitive needed for this screen.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool CloseGalaxyMap(out string detail)
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            detail = "no in-game GUI resolved (not in a loaded module)";
            return false;
        }

        if (*(int*)(gui + InGameGuiInitializedOffset) == 0)
        {
            detail = "the HUD is not initialised yet (still loading)";
            return false;
        }

        int busyBefore = *(int*)(gui + InGameGuiBusyOffset);
        int modalsBefore = ReadModalStackCount(gui);
        if (busyBefore == 0)
        {
            detail = "no full-screen GUI is up, so the galaxy map is already closed (CGuiInGame+0x30 "
                + $"= 0, GUI-manager modal stack = {modalsBefore})";
            return true;
        }

        HideGalaxyMapGuiFn(gui);

        int busyAfter = *(int*)(gui + InGameGuiBusyOffset);
        detail = $"HideGalaxyMapGui called — CGuiInGame+0x30 {busyBefore} -> {busyAfter}, GUI-manager "
            + $"modal stack {modalsBefore} -> {ReadModalStackCount(gui)}, panel "
            + $"0x{*(nint*)(gui + InGameGuiGalaxyMapOffset):X} (the panel pointer persists by design)";
        return busyAfter == 0;
    }
}

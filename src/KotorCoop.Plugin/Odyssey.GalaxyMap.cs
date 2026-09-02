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

    /// <summary>CSWPartyTable::SetSelectedPlanet(this, int nPlanet) 0x00563B60, `ret 4` —
    /// VERIFIED, full disasm this pass (this file's own section header transcribes it in full).
    /// Used directly by <see cref="SetGalaxyMapSelectedPlanet"/> for `galaxymap select`, and
    /// indirectly by <see cref="ShowGalaxyMapGuiFn"/> itself (which calls the same address
    /// through a different this-resolve — see the section header's own two-call-sequence
    /// note).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetSelectedPlanetFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x00563B60;

    /// <summary><c>CSWGuiInGameGalaxyMap</c>'s vtable address (symbol database, docs/research/
    /// galaxy-map-and-party-select.md §1.1: size 0x2550, vtable 0x754910) — independently
    /// re-read this pass by dumping the vtable's own dwords out of <c>swkotor.exe</c>'s
    /// <c>.rdata</c> and confirming slot 15 (+0x3C) holds <c>0x00695980</c>
    /// (<c>HandleInputEvent</c>, matching the symbol DB's own function-address row exactly), the
    /// SAME slot index <c>Odyssey.Dialogue.SkipKey.cs</c>'s own <c>PanelHandleInputEventSlot</c>
    /// already uses for the two dialogue-panel classes — confirming <c>CSWGuiPanel</c>'s virtual
    /// layout is shared across every panel subclass this plugin calls through, not something
    /// re-derived per class. Used only as a read-only compare (a guard that <c>CGuiInGame+0x80</c>
    /// really points at this class), never dereferenced past the vtable slot itself — the same
    /// discipline docs/research/galaxy-map-and-party-select.md's party-select suppression
    /// section describes for <c>CSWGuiPartySelection</c> vtable <c>0x00756D28</c>.</summary>
    private const nint GalaxyMapVtableAddress = 0x00754910;

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

        // A client whose last full-screen GUI came down through this plugin's own close (or
        // never came down at all - the post-load status summary nobody clicked away) still holds
        // input class 2, and ShowGalaxyMapGui's SetInputClass(2, 1) returns without doing
        // anything for a class it already has - +0x30 stays 0 and the map never opens. Restore
        // the retail post-close state first; a no-op when the class is already 0.
        if (!RestoreGameInputClass(out string restoreDetail))
        {
            detail = $"could not restore the game input class first: {restoreDetail}";
            return false;
        }

        int modalsBefore = ReadModalStackCount(gui);
        string before = DescribeGuiPanels(gui);

        ShowGalaxyMapGuiFn(gui, SelectedPlanetNone);

        nint panel = *(nint*)(gui + InGameGuiGalaxyMapOffset);
        int busyAfter = *(int*)(gui + InGameGuiBusyOffset);
        int modalsAfter = ReadModalStackCount(gui);
        detail = $"ShowGalaxyMapGui called — CGuiInGame+0x30 0 -> {busyAfter}, GUI-manager modal "
            + $"stack {modalsBefore} -> {modalsAfter}, panel 0x{panel:X}"
            + (busyAfter == 0 ? " (the engine did not open it)" : " (open)")
            + $" [before: {before}; after: {DescribeGuiPanels(gui)}]";
        return busyAfter != 0;
    }

    /// <summary>CSWGuiManager::PanelExists(panel) 0x0040BD70 — pointer compare over the
    /// registered array (the same delegate <c>PartySelectSuppressionController</c> uses).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int> GuiPanelExistsFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int>)0x0040BD70;

    /// <summary>
    /// The state the galaxy-map open actually depends on, for the log: the client's input
    /// class (<c>CClientExoAppInternal+0x9C</c>; 1 = game, 2 = GUI — see
    /// <see cref="RestoreGameInputClass"/>), <c>CGuiInGame+0x34</c> (the SW GUI status
    /// <c>SetSWGuiStatus</c> drives), and for each of the map panel (<c>+0x80</c>), the pause
    /// panel (<c>+0x7C</c>, CSWGuiInGamePause) and the main interface (<c>+0x90</c>) whether
    /// the GUI manager has it registered and its <c>+0x44</c> state bits. Added 2026-09-03 when
    /// a client that had just run <c>popmodal</c>/<c>closegui</c> got a silent no-op from
    /// <c>ShowGalaxyMapGui</c> with every documented gate passing; the input class was the
    /// answer (a hardware watchpoint on <c>+0x30</c> found its writer).
    /// </summary>
    private static string DescribeGuiPanels(nint gui)
    {
        nint manager = *(nint*)(gui + InGameGuiManagerOffset);
        if (manager == 0)
        {
            return "no GUI manager";
        }

        string One(string name, nint panel)
        {
            if (panel == 0)
            {
                return $"{name}=null";
            }

            int exists = GuiPanelExistsFn(manager, panel);
            return $"{name}=0x{panel:X}{(exists != 0 ? " registered" : " unregistered")} bits 0x{*(uint*)(panel + 0x44):X}";
        }

        nint client = GetClientInternal();
        string inputClass = client == 0 ? "?" : (*(int*)(client + ClientInternalInputClassOffset)).ToString();
        return $"input class {inputClass}, status {*(int*)(gui + 0x34)}, "
            + One("map", *(nint*)(gui + InGameGuiGalaxyMapOffset)) + ", "
            + One("pause", *(nint*)(gui + 0x7C)) + ", "
            + One("maininterface", *(nint*)(gui + 0x90));
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

    /// <summary><c>CSWPartyTable + this</c> = <c>galaxy_map_selected_planet</c> (int). VERIFIED,
    /// full disasm of <c>SetSelectedPlanetFn</c>'s own write at <c>0x00563B80</c>
    /// (<c>mov [ecx+0xe0], eax</c>) — see this file's own section header.</summary>
    private const int SelectedPlanetFieldOffset = 0xE0;

    /// <summary><c>CSWPartyTable</c>'s <c>available_planets[16]</c> int array — the FIRST of
    /// the two gates <c>SetSelectedPlanet</c> (0x00563B60) reads before writing
    /// <see cref="SelectedPlanetFieldOffset"/>. VERIFIED, the same full disasm as that write
    /// (this file's header). Written by the story through <c>SetPlanetAvailable</c>.</summary>
    private const int AvailablePlanetsFieldOffset = 0x60;

    /// <summary><c>CSWPartyTable</c>'s <c>selectable_planets[16]</c> int array — the SECOND
    /// gate of the same pair. VERIFIED, same disasm.</summary>
    private const int SelectablePlanetsFieldOffset = 0xA0;

    /// <summary>
    /// `galaxymap select &lt;n&gt;` — the answer to this pass's own "if a planet must be
    /// selected first for the accept to do anything, add the selection step" requirement
    /// (docs/research/galaxy-map-and-party-select.md's 2026-09-03 `galaxyaccept` addendum).
    /// Calls <c>CSWPartyTable::SetSelectedPlanet</c> (0x00563B60, VERIFIED full disasm, this
    /// file's own section header) directly through the SAME <see cref="GetPartyTable"/> resolve
    /// every other roster routine in this plugin already uses — not the raw
    /// <c>CServerExoApp::GetPartyTable</c> function pointer <c>ShowGalaxyMapGuiFn</c> itself
    /// calls, which <c>Odyssey.Party.cs</c>'s own remarks already establish resolves the
    /// identical table (<c>[server+4] + 0x1B770</c>, confirmed against the same accessor).
    /// Independent of whether the map screen is even open: <c>SetSelectedPlanet</c> itself does
    /// not touch any GUI state, only the table field the confirm script reads.
    ///
    /// Deliberately does NOT require the map to be open first — the write is harmless either
    /// way (a later <c>galaxymap open</c> or <c>galaxyaccept</c> reads whatever is already
    /// selected) — but DOES report whether the engine's own bounds/availability check actually
    /// accepted it, by reading the field back, since <c>SetSelectedPlanet</c>'s own return value
    /// is <c>void</c> (its caller, <c>ShowGalaxyMapGui</c>, never checks it either — see this
    /// file's own section header for why passing an unavailable index is a safe, silent no-op
    /// rather than a crash).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    /// <param name="nPlanet">A `planetary.2da` row index, 0-15 (bounds-checked by the engine
    /// itself; this wrapper additionally refuses a value the engine's own `jl`/`jge` bounds
    /// check would silently reject, so the caller sees WHY nothing changed rather than a
    /// false "applied").</param>
    internal static bool SetGalaxyMapSelectedPlanet(int nPlanet, out string detail)
    {
        if (nPlanet is < 0 or > 0x0F)
        {
            detail = $"{nPlanet} is out of range [0,15] — CSWPartyTable::SetSelectedPlanet's own "
                + "bounds check (0x00563B60) would silently reject it, so this refuses instead of "
                + "sending a write nothing will apply";
            return false;
        }

        nint table = GetPartyTable();
        if (table == 0)
        {
            detail = "no party table resolved (not in a loaded module)";
            return false;
        }

        int before = *(int*)(table + SelectedPlanetFieldOffset);
        SetSelectedPlanetFn(table, nPlanet);
        int after = *(int*)(table + SelectedPlanetFieldOffset);

        detail = after == nPlanet
            ? $"SetSelectedPlanet({nPlanet}) applied — CSWPartyTable+0xE0 {before} -> {after}"
            : $"SetSelectedPlanet({nPlanet}) was a no-op — CSWPartyTable+0xE0 stayed {after} "
                + "(planet not available/selectable — see planetary.2da/K_KOTOR_MASTER gating, "
                + "docs/research/galaxy-map-and-party-select.md §1.2)";
        return after == nPlanet;
    }

    /// <summary>
    /// STAGING VERB: marks planet <paramref name="nPlanet"/> available AND selectable by
    /// writing 1 into both party-table gate arrays directly — the same two fields the story's
    /// own <c>SetPlanetAvailable</c>/<c>SetPlanetSelectable</c> script commands write, and the
    /// exact pair <see cref="SetGalaxyMapSelectedPlanet"/>'s engine call gates on. Live
    /// 2026-09-03 06:48 (host, ebo_m12aa on the Endar Spire save): <c>select 5</c> and
    /// <c>select 0</c> both no-op'd because neither array had a nonzero entry, so
    /// <c>galaxyaccept</c> ran the confirm script with +0xE0 still -1 and nothing departed.
    /// Two plain int writes, no engine call, no derived state to rebuild (the map panel
    /// re-reads the arrays when it is shown). Reports the before/after of both entries so a
    /// caller can tell "already unlocked" from "unlocked now". MAIN THREAD ONLY.
    /// </summary>
    internal static bool UnlockGalaxyMapPlanet(int nPlanet, out string detail)
    {
        if (nPlanet is < 0 or > 0x0F)
        {
            detail = $"{nPlanet} is out of range [0,15] — both gate arrays hold 16 entries";
            return false;
        }

        nint table = GetPartyTable();
        if (table == 0)
        {
            detail = "no party table resolved (not in a loaded module)";
            return false;
        }

        int* available = (int*)(table + AvailablePlanetsFieldOffset) + nPlanet;
        int* selectable = (int*)(table + SelectablePlanetsFieldOffset) + nPlanet;
        int availableBefore = *available;
        int selectableBefore = *selectable;
        *available = 1;
        *selectable = 1;

        detail = $"planet {nPlanet}: available (CSWPartyTable+0x60[{nPlanet}]) {availableBefore} -> 1, "
            + $"selectable (+0xA0[{nPlanet}]) {selectableBefore} -> 1";
        return true;
    }

    /// <summary>Read-only: reports the same three fields `galaxymap stats` needs to prove
    /// `galaxyaccept`'s outcome without a screenshot — whether the full-screen GUI is up
    /// (<c>CGuiInGame+0x30</c>, the live-confirmed "is the map actually open" signal, NOT
    /// <c>+0x80</c> — see this file's own 2026-09-03 live-pass note), the galaxy-map panel
    /// pointer's own vtable (so a caller can tell "no panel yet" from "some other full-screen
    /// GUI is up"), and the currently selected planet. No engine call, three reads. MAIN THREAD
    /// ONLY.</summary>
    internal static bool TryReadGalaxyMapState(out bool guiOpen, out int selectedPlanet, out bool panelLooksRight)
    {
        guiOpen = false;
        selectedPlanet = -1;
        panelLooksRight = false;

        nint gui = GetInGameGui();
        if (gui == 0)
        {
            return false;
        }

        guiOpen = *(int*)(gui + InGameGuiBusyOffset) != 0;

        nint panel = *(nint*)(gui + InGameGuiGalaxyMapOffset);
        panelLooksRight = panel != 0 && *(nint*)panel == GalaxyMapVtableAddress;

        nint table = GetPartyTable();
        if (table != 0)
        {
            selectedPlanet = *(int*)(table + SelectedPlanetFieldOffset);
        }

        return true;
    }
}

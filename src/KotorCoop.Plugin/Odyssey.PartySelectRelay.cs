// Odyssey.PartySelectRelay.cs - the native OPEN half of CSWGuiPartySelection, RE'd this pass
// (2026-09-25) from fresh disassembly of CGuiInGame::ShowPartySelection 0x0062DD20 and its
// caller ExecuteCommandShowPartySelectionGUI 0x005438B0. Odyssey.PartySelect.cs (2026-08-28,
// the `partyselect ok` verb) already owns the CONFIRM half of this same panel
// (ConfirmPartySelect/GetPartySelectPanel) — this is a SEPARATE file, not an edit of that one,
// so the existing live-tested confirm path is untouched; the two share no member names by
// construction (distinct offset-constant names, distinct method names) and are safe to build
// together. See docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay section for the
// full instruction trace and citations. One partial class — GetInGameGui, IsConversationActive,
// IsPlayerInCombat, GuiConversationActive, InGameGuiInitializedOffset, InGameGuiBusyOffset and
// TryReadExoString are declared in other Odyssey.*.cs files and reused here UNQUALIFIED, the
// same convention Odyssey.Store.cs's own header documents.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- ShowPartySelection's ABI, RE'd this pass ------------------------------------------
    //
    // CGuiInGame::ShowPartySelection 0x0062DD20 (thiscall, ret 0x10 — 4 stack dwords):
    //   this  = CGuiInGame*
    //   arg1  (CExoString*, at [esp+0x18] in the callee) = exit_script; copied into
    //           panel+0x074 via CExoString::operator=_2 0x005E5C50 (symbol-DB-named)
    //   arg2  (int, [esp+0x1c])  = "isForced" boolean, stored raw at panel+0x070
    //   arg3  (int, [esp+0x20])  = nForceNPC1, forwarded to SetForcedNPC 0x006BE310's first
    //           argument
    //   arg4  (int, [esp+0x24])  = nForceNPC2, forwarded to SetForcedNPC's second argument
    // Matches the nwscript signature ShowPartySelectionGUI(string sExitScript="",
    // int nForceNPC1=-1, int nForceNPC2=-1) exactly, confirming §2.1's own reading of
    // ExecuteCommandShowPartySelectionGUI's stack-pop shape.
    //
    // Lazily allocates+constructs CGuiInGame+0x78 the FIRST time this is ever called
    // (operator_new_2 0x006FA7E6, size 0x3A80, then CSWGuiPartySelection::Constructor
    // 0x006BFA40 — both symbol-DB-named, both seen live in this disassembly at 0x62DE1F/
    // 0x62DE42) and REUSES that same object on every later call (0x62DE18: `if
    // ([esi+0x78] == 0) allocate; else reuse`) — the engine's own design keeps ONE
    // CSWGuiPartySelection per CGuiInGame for the module's whole lifetime. This is the root
    // cause behind two separate findings: PartySelectSuppressionRule's "CGuiInGame+0x78 never
    // goes null again" write-storm postmortem, and the CGuiInGame::ShutDown 0x0062C310 crash
    // fixed the same pass this file was added — see PartySelectSuppressionController's own
    // header for the fix (nulling +0x78 ourselves right after the suppression sweep frees the
    // object, so a later ShowPartySelection call reallocates instead of writing into freed
    // memory, and ShutDown's own null-checked field walk skips it at module unload).
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, int, void> ShowPartySelectionFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, int, void>)0x0062DD20;

    /// <summary>CExoString::CStrConstructor(char*) 0x005E5A90 — the same address
    /// CharGenDriveController's own <c>ExoStringFromCStr</c> uses; redeclared here rather than
    /// shared because that one is private to a different class.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint> PartySelectExoStringCtor =
        (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x005E5A90;

    /// <summary>CExoString::~CExoString 0x005E5C20 — same address as
    /// CharGenDriveController's own <c>ExoStringDtor</c>.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PartySelectExoStringDtor =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005E5C20;

    /// <summary>CGuiInGame + this = the cached CSWGuiPartySelection* — VERIFIED §2.1, the same
    /// offset <see cref="PartySelectSuppressionController"/> reads.</summary>
    private const int PartySelectionPanelOffset = 0x78;

    /// <summary><c>party_data[9]</c>'s stride, corroborated this pass by SetForcedNPC's own
    /// <c>imul eax, eax, 0x454</c> (0x006BE322) — matches §2.1's already-VERIFIED stride
    /// (party_data 0x07C .. count_label 0x2770 = 9 * 0x454 exactly).</summary>
    private const int PartyDataStride = 0x454;

    private const int PartyDataSlots = 9;

    /// <summary>
    /// panel + this = the dword <c>SetForcedNPC 0x006BE310</c> ORs bit 2
    /// (<see cref="PartyDataForcedFlagBit"/>) into for slot 0 — derived this pass from
    /// SetForcedNPC's own <c>lea eax, [eax + ecx + 0x4c4]</c> (0x006BE328), i.e.
    /// <c>party_data(0x07C) + 0x448</c>. NOT a symbol-database-named field: it falls inside
    /// <c>character_label</c>'s own byte range (0x308..0x450), so this is reading a retail
    /// widget-internal flags dword by its OBSERVED behaviour (SetForcedNPC's own write),
    /// not a documented member. NEEDS-LIVE, same footing as the rest of this file.
    /// </summary>
    private const int PartyDataForcedFlagOffset = 0x4C4;

    private const int PartyDataForcedFlagBit = 4;

    /// <summary>Reads <c>exit_script</c> (<c>CSWGuiPartySelection+0x074</c>, a <c>CExoString</c>
    /// — VERIFIED §2.1) off an already-resolved, already-registered panel.</summary>
    internal static bool TryReadPartySelectExitScript(nint panel, out string exitScript) =>
        TryReadExoString(panel + 0x074, SharedBlock.PartySelectScriptLength, out exitScript);

    /// <summary>
    /// Scans <c>party_data[0..8]</c> for the forced-selection bit <c>SetForcedNPC</c> ORs in
    /// and returns up to two slot indices — NOT necessarily in the original nForceNPC1/2
    /// argument order (unrecoverable once folded into per-slot flags; not needed, since
    /// <see cref="ShowPartySelectionGui"/> only needs SOME two slots that reproduce the same
    /// forced SET, not the exact original argument split). -1 for either slot not found.
    /// </summary>
    internal static void ReadPartySelectForcedNpcs(nint panel, out int forceNpc1, out int forceNpc2)
    {
        forceNpc1 = -1;
        forceNpc2 = -1;

        for (int i = 0; i < PartyDataSlots; i++)
        {
            nint field = panel + PartyDataForcedFlagOffset + (i * PartyDataStride);
            if ((*(int*)field & PartyDataForcedFlagBit) == 0)
            {
                continue;
            }

            if (forceNpc1 < 0)
            {
                forceNpc1 = i;
            }
            else
            {
                forceNpc2 = i;
                return;
            }
        }
    }

    /// <summary>
    /// Opens THIS instance's own party-selection screen with the given exit script and forced
    /// NPCs, through the identical native call the retail <c>ShowPartySelectionGUI</c> nwscript
    /// action reaches — MAIN THREAD ONLY.
    ///
    /// Refuses (never half-opens) while a conversation is active, the local player is in
    /// combat, the HUD is not ready, or a full-screen GUI is already up — the same busy
    /// preconditions <see cref="DepartToPlanet"/> checks, EXCEPT this one does not force-close
    /// a peer's other GUI first: the host is being interrupted for someone ELSE's choice here,
    /// so a plain refusal (the caller reports it back to the requester) is the less surprising
    /// behaviour than dismissing whatever the host was doing.
    ///
    /// NEEDS-LIVE: everything in this file — the call has never been exercised against a real
    /// running game. See docs/research/galaxy-map-and-party-select.md's 2026-09-25 relay
    /// section for the live recipe.
    /// </summary>
    internal static bool ShowPartySelectionGui(string exitScript, int forceNpc1, int forceNpc2, out string detail)
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

        if (*(int*)(gui + GuiConversationActive) != 0 || IsConversationActive)
        {
            detail = "a conversation is active";
            return false;
        }

        if (IsPlayerInCombat)
        {
            detail = "in combat";
            return false;
        }

        if (*(int*)(gui + InGameGuiBusyOffset) != 0)
        {
            detail = "a full-screen GUI is already open";
            return false;
        }

        byte* nameBuffer = stackalloc byte[SharedBlock.PartySelectScriptLength + 1];
        int count = Math.Min(exitScript?.Length ?? 0, SharedBlock.PartySelectScriptLength);
        for (int i = 0; i < count; i++)
        {
            char c = exitScript![i];
            nameBuffer[i] = (byte)(c < 0x20 || c > 0x7E ? (byte)0 : (byte)c);
        }

        nameBuffer[count] = 0;

        // A stack CExoString, built and freed exactly like CharGenDriveController's own
        // OnNewGamePicked strings: ShowPartySelection COPIES it (CExoString::operator=_2) into
        // panel+0x074 rather than taking ownership, so the temporary may die right after.
        byte* exoString = stackalloc byte[8];
        PartySelectExoStringCtor((nint)exoString, (nint)nameBuffer);
        try
        {
            int isForced = forceNpc1 >= 0 || forceNpc2 >= 0 ? 1 : 0;
            ShowPartySelectionFn(gui, (nint)exoString, isForced, forceNpc1, forceNpc2);
        }
        finally
        {
            PartySelectExoStringDtor((nint)exoString);
        }

        nint panel = *(nint*)(gui + PartySelectionPanelOffset);
        if (panel == 0)
        {
            detail = "the engine did not open a screen (refused internally)";
            return false;
        }

        detail = "opened";
        return true;
    }
}

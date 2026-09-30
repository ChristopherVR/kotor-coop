namespace KotorCoop.Plugin;

/// <summary>
/// Drives the game's own character-creation screens by calling the engine's OWN button
/// handlers — the `newgame`, `chargen quick` and `chargen accept` console verbs.
///
/// WHY, AND WHY NOT CLICKS. Rig automation for this path started as synthesised mouse input on
/// the coordinates in docs/research/mouse-cursor-input.md. It works, and it is not trustworthy:
/// KOTOR's menu input dies intermittently and undiagnosably (the trap that made
/// <see cref="MenuLoadController"/> exist rather than clicking Load), and measured on
/// 2026-08-29 a scripted seven-click chargen sequence dropped a click in the middle and ended
/// back at the main menu having silently cancelled. Calling the handlers is the same discipline
/// the rest of this codebase already applies to Load Game and to party selection
/// (<c>Odyssey.ConfirmPartySelect</c>): replay the code path, not the input.
///
/// A PLAYER STILL USES THE REAL UI. These verbs add a second door to the same rooms; they do
/// not replace or bypass the screens. Nothing here is on the player's path.
///
/// ⚠ NOT A CHEAT KIND. <c>CheatController.Execute</c> refuses outright with no player loaded,
/// and the whole cheat channel sits behind <c>PluginHost.OnFrame</c>'s world-epoch gate — while
/// every one of these verbs is only ever meaningful at the MAIN MENU, where neither holds. So
/// this has its own request region and runs from <c>MenuFrame</c>, exactly as the menu-load
/// replay does and for exactly the same reason.
///
/// THE CHAIN, disassembled (tools/disasm.py against GOG 1.0.3; see
/// docs/research/world-adoption.md's 2026-08-29 section):
/// <code>
/// CSWGuiMainMenu::OnNewGamePicked 0x67AFB0
///   operator new(0x1560)                                  0x67B0F7
///   CSWGuiClassSelection::Constructor(gui, "END_M01AA")   0x67B11C -> 0x6DC3C0
///   CSWGuiManager::AddPanel(gui, panel, 0, 1)             0x67B130 -> 0x40BC70
///   [menu+0x44] = (&amp;~0x300) | 0x400   (teardown mark)     0x67B147
///
/// CSWGuiClassSelection::OnSelectButton 0x6DB9B0(this, control)
///   index  = (control - (this+0x6C)) / 0x25C              0x6DB9F4  (six CSWGuiClassSelChar)
///   [this+0x68] = [this + index*0x25C + 0x2C0]            0x6DBA19  (the class's creature)
///   operator new(0x22DC) + CSWGuiMainCharGen::Constructor 0x6DBA1C / 0x6EB420
///   AddPanel(gui, mainCharGen, 3, 1)                      0x6DBA58
///
/// CSWGuiQuickOrCustomPanel::OnSelectQuickCharacterButton 0x6F0800
///   -> CSWGuiMainCharGen::SetVisiblePanel(mainCharGen, 2) 0x6F081A -> 0x6EA760
/// CSWGuiQuickPanel::DoQuickStats 0x6EFFD0                 (no arguments; reads [quick+0x64])
///
/// CSWGuiMainCharGen::BeginPlayGame 0x6EB320               (no arguments)
///   -> CSWGuiClassSelection::BeginPlayGame 0x6DBDF0 -> SaveCharGenCreature 0x6123E0
/// </code>
///
/// ⚠ EVERY HANDLER THAT TAKES A FIRING CONTROL IS GIVEN A REAL ONE, never a synthesised or null
/// pointer. <c>OnSelectButton</c> dereferences its argument (<c>[control+0x4C]</c>) AND derives
/// the class index from the pointer's own address, so the only correct value is the genuine
/// embedded <c>CSWGuiClassSelChar</c> at <c>classSel + 0x6C + index*0x25C</c> — which is why
/// the class index reaches the engine as an address rather than a number. The two handlers with
/// no arguments (<c>SetVisiblePanel</c>'s target and <c>DoQuickStats</c>) are called directly.
/// </summary>
internal sealed unsafe class CharGenDriveController(SharedBlockView block)
{
    private const nint AppManagerPtr = 0x007A39FC;
    private const nint GuiManagerPtr = 0x007A39F4;
    private const int AppClientOffset = 0x04;
    private const int InternalOffset = 0x04;
    private const int ClientInternalMainMenu = 0x280;
    private const int MenuGuiManager = 0x18;
    private const int MenuStateBits = 0x44;

    /// <summary>CSWGuiManager + this = the panel array, and + 4 = its count. Read out of
    /// <c>CSWGuiManager::GetPanelIndex 0x40ABA0</c> (`[ecx+0x8C]` count, `[ecx+0x88]` data) and
    /// matching the symbol database's own <c>panels: CExoArrayList&lt;CSWGuiPanel*&gt;</c> at
    /// 0x088.</summary>
    private const int GuiManagerPanels = 0x88;

    private const int GuiManagerPanelCount = 0x8C;

    private const nint ClassSelectionVtable = 0x00758020;
    private const nint MainCharGenVtable = 0x007592A8;

    private const int ClassSelectionSize = 0x1560;

    /// <summary>The panel-layer <c>AddPanel</c>'s second stack argument selects. Retail passes
    /// 2 for the class-selection panel (EBX at 0x67B12A, set at 0x67B02E) and 3 for the chargen
    /// panel (0x6DBA4D).</summary>
    private const int ClassSelectionPanelLayer = 2;

    /// <summary>CSWGuiClassSelection + this = the first of six embedded CSWGuiClassSelChar
    /// controls, each 0x25C apart — the addresses <c>OnSelectButton</c> compares against to
    /// learn which class was clicked.</summary>
    private const int ClassSelectionFirstChoice = 0x6C;

    private const int ClassSelectionChoiceStride = 0x25C;

    private const int ClassChoiceCount = 6;

    /// <summary>CSWGuiClassSelChar + this = the DATA of the event being dispatched
    /// (<c>[+0x48]</c> is the event id). <c>OnSelectButton</c> bails silently when this is zero
    /// (0x6DB9CB), and it is only ever filled while the GUI manager is dispatching a real
    /// click.</summary>
    private const int GuiControlEventData = 0x4C;

    /// <summary>CSWGuiClassSelChar + this = that class's creature — the pointer
    /// <c>OnSelectButton</c> copies to <c>[classSel+0x68]</c> (0x6DBA0D reads
    /// <c>[index*0x25C + classSel + 0x2C0]</c>, i.e. this offset from the choice control).
    /// </summary>
    private const int ClassSelectionChoiceCreature = 0x2C0 - ClassSelectionFirstChoice;

    /// <summary>CSWGuiMainCharGen + this = the CSWGuiQuickPanel*, from the constructor's own
    /// store at 0x6EBCC6.</summary>
    private const int MainCharGenQuickPanel = 0x70;

    /// <summary>CSWGuiMainCharGen + this = the creature being created (0x6EBC7C reads it to
    /// hand to every sub-panel constructor).</summary>
    private const int MainCharGenCreature = 0x64;

    /// <summary><c>CSWGuiMainCharGen::SetVisiblePanel(int)</c> — 2 is the Quick panel, which is
    /// exactly what <c>OnSelectQuickCharacterButton</c> tail-jumps to.</summary>
    private const int VisiblePanelQuick = 2;

    // ---- engine bindings ---------------------------------------------------------------

    private static readonly delegate* unmanaged[Cdecl]<int, nint> OperatorNew =
        (delegate* unmanaged[Cdecl]<int, nint>)0x006FA7E6;

    /// <summary>CExoString::CStrConstructor(char*) — builds an engine-owned copy in place.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint> ExoStringFromCStr =
        (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x005E5A90;

    /// <summary>CExoString::~CExoString — retail's own OnNewGamePicked destroys the stack
    /// strings it built (0x67B160/0x67B16E), so the constructor copies rather than takes
    /// ownership, and so must we or the buffer leaks.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ExoStringDtor =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005E5C20;

    private static readonly delegate* unmanaged[Stdcall]<void> ResetSessionStartTime =
        (delegate* unmanaged[Stdcall]<void>)0x00563CF0;

    /// <summary>CSWGuiClassSelection::Constructor(guiManager, CExoString* startModule). The
    /// second argument is the START MODULE NAME — read out of OnNewGamePicked, where the
    /// literal at 0x752F58 is "END_M01AA" and ends up in <c>[classSel+0x1558]</c>, the same
    /// field <c>BeginPlayGame</c> later loads the module from.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, ExoString*, nint> ClassSelectionCtor =
        (delegate* unmanaged[Thiscall]<nint, nint, ExoString*, nint>)0x006DC3C0;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, void> AddPanel =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, void>)0x0040BC70;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> OnSelectClass =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x006DB9B0;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetVisiblePanel =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x006EA760;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> DoQuickStats =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006EFFD0;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> BeginPlayGame =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006EB320;

    /// <summary>Serial already acted on, so one request runs exactly one verb.</summary>
    private uint _firedSerial;

    public void OnFrame()
    {
        uint serial = block.ChargenDriveRequest;
        if (serial == block.ChargenDriveApplied || serial == _firedSerial)
        {
            return;
        }

        // While the native browser panel is up the main-menu object is FREED
        // (docs/research/native-browser-panel.md §2), so anything resolved through it would be
        // dangling. Same bail MenuLoadController opens with.
        if (NativeBrowserController.IsOpen)
        {
            return;
        }

        _firedSerial = serial;

        uint kind = block.ChargenDriveKind & 0xFFFF;
        int classIndex = (int)(block.ChargenDriveKind >> 16);

        uint status;
        try
        {
            status = kind switch
            {
                SharedBlock.ChargenDriveNewGame => NewGame(),
                SharedBlock.ChargenDriveQuick => Quick(classIndex),
                SharedBlock.ChargenDriveAccept => Accept(),
                _ => 1,
            };
        }
        catch (Exception ex)
        {
            PluginHost.Log($"chargen: verb {kind} threw - {ex.Message}");
            status = 9;
        }

        block.ChargenDriveStatus = status;
        block.ChargenDriveApplied = serial;
    }

    /// <summary>
    /// Replays <c>CSWGuiMainMenu::OnNewGamePicked</c>'s body: construct the class-selection
    /// panel, register it, and mark the main menu for the manager's delete sweep.
    ///
    /// Deliberately NOT calling <c>OnNewGamePicked</c> itself: it takes the firing control as a
    /// stack argument and dereferences it (<c>mov eax,[ecx+0x4C]</c> at 0x67AFF0), and the
    /// offset of BTN_NEWGAME inside CSWGuiMainMenu is not researched — there is no honest value
    /// to pass. Its body needs no control at all.
    ///
    /// The resource-directory dance retail does first (register "MODULES:", probe the start
    /// module as restype 0x7DB then 0xBBA, fall back to "END_M01AA") is skipped: every branch
    /// of it ends with the same string this passes, and the probes only decide whether a
    /// developer override module exists.
    /// </summary>
    private uint NewGame()
    {
        nint appManager = *(nint*)AppManagerPtr;
        nint client = appManager == 0 ? 0 : *(nint*)(appManager + AppClientOffset);
        nint clientInternal = client == 0 ? 0 : *(nint*)(client + InternalOffset);
        nint menu = clientInternal == 0 ? 0 : *(nint*)(clientInternal + ClientInternalMainMenu);

        if (menu == 0)
        {
            PluginHost.Log("chargen: newgame refused - not at the main menu (logo movies still playing?)");
            return 2;
        }

        // The menu object is freed once character creation replaces it, and the client keeps
        // returning its old address (a re-join while creation is already open crashed here,
        // live 2026-09-29). Unreadable means the menu is gone: creation is under way.
        if (!NativeMethods.IsReadable(menu + MenuStateBits, sizeof(uint))
            || !NativeMethods.IsReadable(menu + MenuGuiManager, (nuint)sizeof(nint)))
        {
            PluginHost.Log("chargen: newgame refused - the main menu object is gone (character creation is probably already open)");
            return 3;
        }

        // The same bit pattern OnNewGamePicked's own first check rejects on: already marked for
        // teardown means a game is starting and the object is about to be deleted.
        if ((*(uint*)(menu + MenuStateBits) & 0x600) == 0x400)
        {
            PluginHost.Log("chargen: newgame refused - the main menu is already tearing down");
            return 3;
        }

        nint guiManager = *(nint*)(menu + MenuGuiManager);
        if (guiManager == 0)
        {
            return 2;
        }

        if (FindPanel(ClassSelectionVtable) != 0)
        {
            PluginHost.Log("chargen: newgame is a no-op - character creation is already open");
            return 0;
        }

        nint panel = OperatorNew(ClassSelectionSize);
        if (panel == 0)
        {
            return 4;
        }

        byte* startModule = stackalloc byte[16];
        int n = 0;
        foreach (char c in "END_M01AA")
        {
            startModule[n++] = (byte)c;
        }

        startModule[n] = 0;

        ExoString module = default;
        ExoStringFromCStr((nint)(&module), (nint)startModule);

        ResetSessionStartTime();
        ClassSelectionCtor(panel, guiManager, &module);

        // ⚠ LAYER 2, NOT 0, AND THIS COST A LIVE ROUND. Retail's AddPanel call at 0x67B130 is
        // `push 1; push ebx; push eax` with EBX still holding the 2 set at 0x67B02E — the same
        // slot OnSelectButton passes 3 in for the chargen panel it adds (0x6DBA4D). Added at 0
        // the panel is constructed and registered but never driven: measured live, the screen
        // stayed black and every one of the six class controls had its +0x4C still zero, which
        // is precisely OnSelectButton's own silent bail. It looked exactly like a bad
        // constructor argument and was not.
        AddPanel(guiManager, panel, ClassSelectionPanelLayer, 1);
        NewGameIntentHook.PublishAccepted();

        // ⚠ After this the menu object is condemned; do not read `menu` again.
        *(uint*)(menu + MenuStateBits) = (*(uint*)(menu + MenuStateBits) & ~0x300u) | 0x400u;

        ExoStringDtor((nint)(&module));

        // ⚠ WHICH MANAGER. Retail adds the panel to the MENU's manager ([menu+0x18]); the one
        // that hit-tests and updates is the global at [0x7A39F4]. If those are ever different
        // objects the panel lands somewhere nothing drives, which looks exactly like "the panel
        // exists but its controls were never built" — the failure this line exists to tell
        // apart from every other reason a chargen screen could stay blank.
        nint globalManager = *(nint*)GuiManagerPtr;
        PluginHost.Log(
            $"chargen: newgame opened character creation (panel 0x{panel:X}, "
            + $"menu manager 0x{guiManager:X}, global manager 0x{globalManager:X}, "
            + $"{(guiManager == globalManager ? "same" : "DIFFERENT")})");
        return 0;
    }

    /// <summary>
    /// Picks a class through the panel's own <c>OnSelectButton</c> and then applies the
    /// optimised template the Quick Character route applies — <c>SetVisiblePanel(2)</c> (what
    /// the Quick button itself tail-jumps to) followed by <c>DoQuickStats</c>.
    /// </summary>
    private uint Quick(int classIndex)
    {
        if (classIndex is < 0 or >= ClassChoiceCount)
        {
            PluginHost.Log($"chargen: quick refused - class index {classIndex} is not 0..{ClassChoiceCount - 1}");
            return 5;
        }

        nint mainCharGen = FindPanel(MainCharGenVtable);

        if (mainCharGen == 0)
        {
            nint classSel = FindPanel(ClassSelectionVtable);
            if (classSel == 0)
            {
                PluginHost.Log("chargen: quick refused - character creation is not open (run newgame first)");
                return 6;
            }

            // ⚠ THE CLASS INDEX REACHES THE ENGINE AS AN ADDRESS. OnSelectButton derives it by
            // comparing the control pointer against this->0x6C + i*0x25C, so the only value
            // that can mean "class i" is the genuine embedded control at that address.
            nint choice = classSel + ClassSelectionFirstChoice + (classIndex * ClassSelectionChoiceStride);

            nint creature = *(nint*)(choice + ClassSelectionChoiceCreature);
            if (creature == 0)
            {
                PluginHost.Log(
                    $"chargen: quick refused - class {classIndex} has no creature yet "
                    + "(the panel is still building)");
                return 7;
            }

            // ⚠ THE ARGUMENT IS AN EVENT, NOT A CONTROL, and the difference is why this needs a
            // poke at all. OnSelectButton reads `[arg+0x48]` as an EVENT ID (comparing it to
            // 0x2D to decide whether to play a click sound) and `[arg+0x4C]` as that event's
            // DATA, bailing silently when the data is zero (0x6DB9CB) — and only then does it
            // derive the class index by comparing the pointer against `this+0x6C + i*0x25C`.
            // So the six embedded CSWGuiClassSelChar objects begin with an event header, and
            // the data field is filled by the GUI manager while it is dispatching a real click.
            // Measured live: the panel renders perfectly and every one of those fields is still
            // zero, so a call with no poke is a guaranteed silent no-op.
            //
            // Written, called, and PUT BACK. One dword, restored on every path, so nothing
            // outside this call can observe it — the same "touch the precondition, make the
            // call, leave no trace" shape Odyssey.ConfirmPartySelect already uses for the
            // party-selection message-box bit.
            nint* eventData = (nint*)(choice + GuiControlEventData);
            nint saved = *eventData;
            *eventData = 1;

            try
            {
                PluginHost.Log(
                    $"chargen: quick selecting class {classIndex} - panel 0x{classSel:X} "
                    + $"control 0x{choice:X} creature 0x{creature:X}");
                OnSelectClass(classSel, choice);
            }
            finally
            {
                *eventData = saved;
            }

            mainCharGen = FindPanel(MainCharGenVtable);
            if (mainCharGen == 0)
            {
                PluginHost.Log("chargen: quick failed - selecting the class did not open the chargen panel");
                return 7;
            }
        }

        SetVisiblePanel(mainCharGen, VisiblePanelQuick);

        nint quick = *(nint*)(mainCharGen + MainCharGenQuickPanel);
        if (quick == 0)
        {
            PluginHost.Log("chargen: quick failed - the chargen panel has no quick sub-panel");
            return 8;
        }

        DoQuickStats(quick);

        PluginHost.Log(
            $"chargen: quick applied the optimised template for class {classIndex} "
            + $"(panel 0x{mainCharGen:X}, creature 0x{*(nint*)(mainCharGen + MainCharGenCreature):X})");
        return 0;
    }

    /// <summary>
    /// Presses Play. This is the call that ends with <c>SaveCharGenCreature</c> writing the new
    /// character to <c>TEMP:pifo</c> — the file <see cref="CharGenSaveHook"/> is waiting for —
    /// and then starts the module.
    /// </summary>
    private uint Accept()
    {
        nint mainCharGen = FindPanel(MainCharGenVtable);
        if (mainCharGen == 0)
        {
            PluginHost.Log("chargen: accept refused - the character-creation panel is not open");
            return 6;
        }

        nint creature = *(nint*)(mainCharGen + MainCharGenCreature);
        if (creature == 0)
        {
            // BeginPlayGame hands this pointer to SaveCharGenCreature as `this`; a null would
            // fault inside the engine rather than fail here.
            PluginHost.Log("chargen: accept refused - no creature has been created yet");
            return 7;
        }

        // ⚠ DIAGNOSTIC ONLY 2026-09-10 (round 3, docs/research/world-adoption-clothes-notes.md):
        // does the chargen-time creature object already carry a real, small numeric id at its
        // own +0x04 (the same convention every other id read in this codebase uses), before
        // BeginPlayGame/SaveCharGenCreature ever run - independent of Odyssey.GetPlayerObjectId
        // (CServerExoApp::GetPlayerCreature), which LIVE-CONFIRMED round 3 reads the engine's own
        // 0x7FFFFFFF "no real player yet" placeholder for the entire post-BeginPlayGame repair
        // window on every first-time `join_mode = newcharacter` join tested. If this id is real
        // here and still names the same live creature later, it is a candidate for a repair-time
        // fallback that does not depend on either of the two "current player" accessors already
        // shown broken in that same window (see Odyssey.GetControlledCreatureObjectId's own doc
        // comment for the client-side one).
        uint chargenCreatureId = *(uint*)(creature + 0x04);
        PluginHost.Log(
            $"chargen-diag: creature panel-pointer 0x{creature:X} own id field (+0x04) = "
            + $"0x{chargenCreatureId:X}");

        PluginHost.Log($"chargen: accept - BeginPlayGame on panel 0x{mainCharGen:X}");
        BeginPlayGame(mainCharGen);
        return 0;
    }

    /// <summary>
    /// The live panel with this vtable, or 0. Walks the GUI manager's own
    /// <c>CExoArrayList&lt;CSWGuiPanel*&gt;</c> — data at +0x88, count at +0x8C, read out of
    /// <c>CSWGuiManager::GetPanelIndex 0x40ABA0</c>, which is the same walk
    /// <c>AddPanel</c>/<c>PanelExists</c> use.
    ///
    /// Vtable-matched rather than offset-chased, the same guard
    /// <c>Odyssey.GetPartySelectPanel</c> uses: a panel pointer is only ever called through
    /// after its own vtable has said what it is.
    /// </summary>
    private static nint FindPanel(nint vtable)
    {
        nint found = FindPanelIn(*(nint*)GuiManagerPtr, vtable);
        if (found != 0)
        {
            return found;
        }

        // The menu's own manager, for the window between construction and the global catching
        // up. Resolved defensively; 0 at every hop is normal outside the menu.
        nint appManager = *(nint*)AppManagerPtr;
        nint client = appManager == 0 ? 0 : *(nint*)(appManager + AppClientOffset);
        nint clientInternal = client == 0 ? 0 : *(nint*)(client + InternalOffset);
        nint menu = clientInternal == 0 ? 0 : *(nint*)(clientInternal + ClientInternalMainMenu);
        // The menu object is freed once character creation replaces it, and the client keeps
        // returning the old address: readable-or-nothing, never a raw dereference.
        return menu == 0 || !NativeMethods.IsReadable(menu + MenuGuiManager, (nuint)sizeof(nint))
            ? 0
            : FindPanelIn(*(nint*)(menu + MenuGuiManager), vtable);
    }

    private static nint FindPanelIn(nint guiManager, nint vtable)
    {
        if (guiManager == 0)
        {
            return 0;
        }

        if (!NativeMethods.IsReadable(guiManager + GuiManagerPanels, 8))
        {
            return 0;
        }

        nint data = *(nint*)(guiManager + GuiManagerPanels);
        int count = *(int*)(guiManager + GuiManagerPanelCount);

        // A count this side of sane is the signature of a wrong pointer, not of a busy screen;
        // refuse rather than walk whatever is there.
        if (data == 0 || count <= 0 || count > 256)
        {
            return 0;
        }

        if (!NativeMethods.IsReadable(data, (nuint)(count * 4)))
        {
            return 0;
        }

        for (int i = 0; i < count; i++)
        {
            nint panel = *(nint*)(data + (i * 4));
            if (panel != 0 && NativeMethods.IsReadable(panel, 4) && *(nint*)panel == vtable)
            {
                return panel;
            }
        }

        return 0;
    }
}

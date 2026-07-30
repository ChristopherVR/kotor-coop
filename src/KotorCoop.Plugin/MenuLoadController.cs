namespace KotorCoop.Plugin;

/// <summary>
/// Loads a save from the MAIN MENU on the agent's request, with no input injection.
///
/// Menu clicking proved intermittently dead in a way that resisted diagnosis (trap 25:
/// game foreground, activation cycled, DirectInput forwarding intact — clicks still
/// ignored), and a co-op session cannot depend on a human clicking three buttons in each
/// window. This replays the GUI's own code path instead, in-process, on the game's
/// thread, which cannot miss.
///
/// The path replayed is the one the death screen's "load last autosave" uses, read out
/// of the retail binary (see Handoff addendum): construct a CSWGuiSaveLoad panel exactly
/// as CSWGuiMainMenu::OnLoadSaveGame does, show it, and call LoadPCAutoSave — which
/// builds its own save-info from the AUTOSAVE literals and drives
/// CServerExoApp::ExecuteLoadModule with every GUI and input-state transition intact.
/// The agent has already copied the wanted save over the autosave slot on disk (a KOTOR
/// save is just a directory of five files), so "load the autosave" means "load the save
/// the session chose".
///
/// Runs BEFORE PluginHost's epoch gate, which returns early whenever no server exists —
/// and "no server" is precisely the main menu, the only place this may fire.
/// </summary>
internal sealed unsafe class MenuLoadController(SharedBlockView block)
{
    /// <summary>CClientExoAppInternal + this = CSWGuiMainMenu*, null outside the menu.</summary>
    private const int ClientInternalMainMenu = 0x280;

    /// <summary>CSWGuiMainMenu + this = the GUI owner the save panel is parented to.</summary>
    private const int MenuOwner = 0x18;

    /// <summary>CSWGuiMainMenu + this = panel state bits; OnLoadSaveGame sets 0x400.</summary>
    private const int MenuStateBits = 0x44;

    private const int SaveLoadPanelSize = 0x1160;

    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppClientOffset = 0x04;
    private const int InternalOffset = 0x04;

    // Global::operator_new_2(size) — the engine's own allocator, so the panel can be
    // freed by the engine's own teardown.
    private static readonly delegate* unmanaged[Cdecl]<int, nint> OperatorNew =
        (delegate* unmanaged[Cdecl]<int, nint>)0x006FA7E6;

    // CSWGuiSaveLoad::Constructor(owner, bSaveMode, bFromMainMenu) — argument order read
    // from OnLoadSaveGame's call site: push 1; push 0; push [menu+0x18].
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, nint> SaveLoadCtor =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, nint>)0x006CC680;

    // The panel-show call OnLoadSaveGame makes: 0x0040BC70(owner; panel, 2, 1).
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, void> ShowPanel =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, void>)0x0040BC70;

    // CSWGuiSaveLoad::LoadPCAutoSave(saveEntry). The argument is forwarded to
    // ExecuteLoadModule, whose internal reads the save identity from server state that
    // LoadPCAutoSave itself sets from its own constructed save-info — so null is safe,
    // and the death screen's caller behaviour confirms the state-based path is the one
    // that matters.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> LoadPCAutoSave =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x006CA250;

    /// <summary>Serial already acted on, so one request fires exactly one load.</summary>
    private uint _firedSerial;

    public void OnFrame()
    {
        uint serial = block.MenuLoadSerial;
        if (serial == block.MenuLoadApplied)
        {
            return;
        }

        // A server exists: either the load we fired has begun (mission accomplished) or
        // the game was never at the menu and the request is moot. Both are acks.
        if (Odyssey.HasServer)
        {
            block.MenuLoadStatus = 0;
            block.MenuLoadApplied = serial;
            return;
        }

        if (_firedSerial == serial)
        {
            // Fired; the transition takes a few hundred frames to produce a server.
            return;
        }

        nint menu = ResolveMainMenu();
        if (menu == 0)
        {
            // Logo movies still playing; the menu object appears when they end.
            return;
        }

        _firedSerial = serial;

        int failure = Fire(menu);
        if (failure == 0)
        {
            PluginHost.Log("menu: loading the autosave on the agent's request");
        }
        else
        {
            PluginHost.Log($"menu: autosave load refused (failure {failure})");
            block.MenuLoadStatus = failure;
            block.MenuLoadApplied = serial;
        }
    }

    private static int Fire(nint menu)
    {
        // The field at menu+0x18 is the CSWGuiManager — OnLoadSaveGame hands the same
        // pointer to both the constructor and AddPanel.
        nint guiManager = *(nint*)(menu + MenuOwner);
        if (guiManager == 0)
        {
            return 2;
        }

        PluginHost.Log($"menu: firing autosave load (menu 0x{menu:X}, gui 0x{guiManager:X})");

        nint panel = OperatorNew(SaveLoadPanelSize);
        if (panel == 0)
        {
            return 3;
        }

        // operator new does not zero; a GUI constructor that leaves any member
        // conditionally uninitialised would read heap garbage otherwise.
        new Span<byte>((void*)panel, SaveLoadPanelSize).Clear();

        PluginHost.Log("menu: panel allocated, constructing");
        SaveLoadCtor(panel, guiManager, 0, 1);

        PluginHost.Log("menu: constructed, adding panel");
        ShowPanel(guiManager, panel, 2, 1);

        // The state bits OnLoadSaveGame sets on the menu, so it knows a child panel is up.
        int* state = (int*)(menu + MenuStateBits);
        *state = (*state & ~0x300) | 0x400;

        PluginHost.Log("menu: panel added, calling LoadPCAutoSave");
        LoadPCAutoSave(panel, 0);
        return 0;
    }

    private static nint ResolveMainMenu()
    {
        nint appManager = *(nint*)AppManagerPtr;
        if (appManager == 0)
        {
            return 0;
        }

        nint client = *(nint*)(appManager + AppClientOffset);
        if (client == 0)
        {
            return 0;
        }

        nint clientInternal = *(nint*)(client + InternalOffset);
        return clientInternal == 0 ? 0 : *(nint*)(clientInternal + ClientInternalMainMenu);
    }
}

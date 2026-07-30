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
/// The replayed path is CSWGuiSaveLoad::LoadGame's main-menu autosave branch
/// (0x6CB256..0x6CB3E8 in the retail binary), reproduced call for call. The part 4.2
/// never found is in the middle: after CAppManager::CreateServer the GUI also pauses
/// both world timers, starts a LOOPBACK SESSION (CNetLayer::StartConnectToSession) and
/// sends the client's status request — that handshake is why a bare load request was
/// "accepted and never serviced" when only the server had been created. LoadPCAutoSave
/// then builds its save-info from the AUTOSAVE literals and drives
/// CServerExoApp::ExecuteLoadModule. The agent has already copied the wanted save over
/// the autosave slot on disk, so "load the autosave" means "load the save the session
/// chose".
///
/// Runs BEFORE PluginHost's epoch gate, which returns early whenever no server exists —
/// and "no server" is precisely the main menu, the only place this may fire.
/// </summary>
internal sealed unsafe class MenuLoadController(SharedBlockView block)
{
    /// <summary>CClientExoAppInternal + this = CSWGuiMainMenu*, null outside the menu.</summary>
    private const int ClientInternalMainMenu = 0x280;

    /// <summary>CSWGuiMainMenu + this = the CSWGuiManager the save panel is parented to.</summary>
    private const int MenuGuiManager = 0x18;

    /// <summary>CSWGuiMainMenu + this = panel state bits; OnLoadSaveGame sets 0x400.</summary>
    private const int MenuStateBits = 0x44;

    private const int SaveLoadPanelSize = 0x1160;

    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppClientOffset = 0x04;
    private const int AppServerOffset = 0x08;
    private const int InternalOffset = 0x04;

    /// <summary>A global byte fed to StartConnectToSession by the retail code.</summary>
    private const nint SessionBytePtr = 0x0074C540;

    // ---- engine bindings, in call order of the replayed branch -------------------

    private static readonly delegate* unmanaged[Cdecl]<int, nint> OperatorNew =
        (delegate* unmanaged[Cdecl]<int, nint>)0x006FA7E6;

    // CSWGuiSaveLoad::Constructor(guiManager, bSaveMode, bFromMainMenu); order read from
    // OnLoadSaveGame's call site: push 1; push 0; push [menu+0x18].
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, nint> SaveLoadCtor =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, nint>)0x006CC680;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, void> AddPanel =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, void>)0x0040BC70;

    private static readonly delegate* unmanaged[Stdcall]<void> ResetSessionStartTime =
        (delegate* unmanaged[Stdcall]<void>)0x00563CF0;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetInGameGui =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED690;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetGlobalUnpauseableState =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x0062B410;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> PopModalPanel =
        (delegate* unmanaged[Thiscall]<nint, void>)0x0040BE00;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> HideSWInGameGui =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x0062CBA0;

    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, void> SetLoadScreenByModuleName =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, void>)0x005EDCF0;

    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, void> SetLoadMusic =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, void>)0x005EDD30;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int, void> ShowLoadScreen =
        (delegate* unmanaged[Thiscall]<nint, int, int, int, void>)0x005EDA80;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetLoadBarProgress =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x005EDD40;

    // Global::UpdateScreen(dtBits, 0, 0) — renders one frame so the load screen is
    // actually shown. 0x3D088889 is 1/30 s as float bits, verbatim from the branch.
    private static readonly delegate* unmanaged[Cdecl]<int, int, int, void> UpdateScreen =
        (delegate* unmanaged[Cdecl]<int, int, int, void>)0x00401C10;

    // The DB lists no parameters, but the call site pushes one and thiscall is
    // callee-clean, so the argument is real.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> CreateServer =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x00401380;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> ClientGetWorldTimer =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED4F0;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> ServerGetWorldTimer =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AEDE0;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> PauseWorldTimer =
        (delegate* unmanaged[Thiscall]<nint, void>)0x004ADFF0;

    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, nint> GetClientName =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, nint>)0x005ED490;

    // CNetLayer::StartConnectToSession(0, name, address, 10, sessionByte) — the loopback
    // connect that makes the server actually service the load.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, ExoString*, int, int, void> StartConnectToSession =
        (delegate* unmanaged[Thiscall]<nint, int, nint, ExoString*, int, int, void>)0x005D5040;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetSWCMessage =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED6F0;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> RequestStatus =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006775E0;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetInputClass =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x005EDA60;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> DisableInput =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005EDBE0;

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
            // Fired; the transition takes a while to produce a server. Note UpdateScreen
            // inside the branch re-enters the frame hook — this early-out is what makes
            // that re-entry harmless.
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
        PluginHost.Log(failure == 0
            ? "menu: autosave load fired; waiting for the world"
            : $"menu: autosave load refused (failure {failure})");

        if (failure != 0)
        {
            block.MenuLoadStatus = failure;
            block.MenuLoadApplied = serial;
        }
    }

    private static int Fire(nint menu)
    {
        nint appManager = *(nint*)AppManagerPtr;
        nint client = appManager == 0 ? 0 : *(nint*)(appManager + AppClientOffset);
        nint guiManager = *(nint*)(menu + MenuGuiManager);

        if (client == 0 || guiManager == 0)
        {
            return 2;
        }

        nint panel = OperatorNew(SaveLoadPanelSize);
        if (panel == 0)
        {
            return 3;
        }

        PluginHost.Log("menu: constructing the save panel");
        SaveLoadCtor(panel, guiManager, 0, 1);
        AddPanel(guiManager, panel, 2, 1);

        int* menuState = (int*)(menu + MenuStateBits);
        *menuState = (*menuState & ~0x300) | 0x400;

        // A zeroed stand-in for the save-list entry the GUI would normally pass. Only
        // its loading-screen name (+0x1F0) and flags are read on this path, and empty
        // reads select defaults; the save identity itself comes from the AUTOSAVE
        // literals inside LoadPCAutoSave.
        byte* entry = stackalloc byte[0x400];
        new Span<byte>(entry, 0x400).Clear();

        PluginHost.Log("menu: staging the load screen");
        ResetSessionStartTime();
        SetGlobalUnpauseableState(GetInGameGui(client), 1);
        PopModalPanel(guiManager);
        HideSWInGameGui(GetInGameGui(client), 0);

        ExoString empty = default;
        SetLoadScreenByModuleName(client, (ExoString*)(entry + 0x1F0));
        SetLoadMusic(client, &empty);
        ShowLoadScreen(client, 1, 1, 0);
        SetLoadBarProgress(client, 0, 1);
        UpdateScreen(0x3D088889, 0, 0);

        PluginHost.Log("menu: creating the server and loopback session");
        CreateServer(appManager, 0);

        nint server = *(nint*)(appManager + AppServerOffset);
        if (server == 0)
        {
            return 4;
        }

        PauseWorldTimer(ClientGetWorldTimer(client));
        PauseWorldTimer(ServerGetWorldTimer(server));

        ExoString address = default;
        ExoString nameStorage = default;
        nint name = GetClientName(client, &nameStorage);
        int sessionByte = *(byte*)SessionBytePtr;
        StartConnectToSession(GetNetLayer(client), 0, name, &address, 0x0A, sessionByte);
        RequestStatus(GetSWCMessage(client));

        SetInputClass(client, 0, 1);
        DisableInput(client);

        PluginHost.Log("menu: calling LoadPCAutoSave");
        LoadPCAutoSave(panel, (nint)entry);
        return 0;
    }

    /// <summary>Client vtable slot +0x10 hands back the CNetLayer.</summary>
    private static nint GetNetLayer(nint client)
    {
        nint vtable = *(nint*)client;
        var fn = (delegate* unmanaged[Thiscall]<nint, nint>)(*(nint*)(vtable + 0x10));
        return fn(client);
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

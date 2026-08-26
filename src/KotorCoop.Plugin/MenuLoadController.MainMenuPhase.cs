namespace KotorCoop.Plugin;

// The engine bindings for the replayed main-menu autosave branch (CSWGuiSaveLoad::LoadGame,
// 0x6CB256..0x6CB3E8), in the order the branch calls them, plus FirePhase1 - the method that
// walks that whole sequence up to and including the loopback connect. See
// MenuLoadController.cs for the OnFrame dispatcher and the fields this shares with the
// other phase partials.
internal sealed unsafe partial class MenuLoadController
{
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

    /// <summary>CWorldTimer::UnpauseWorldTimer. The pause is a plain flag (timer+0x24), not
    /// a counter, so pausing and resuming are both idempotent.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> UnpauseWorldTimer =
        (delegate* unmanaged[Thiscall]<nint, void>)0x004AE030;

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

    /// <summary>
    /// CServerExoApp::SetDoingStartNewModule(int) — writes serverInternal+0x1007C, the flag
    /// RunModule forks the character login on. This one call is what makes the replay load
    /// the SAVED character; see the comment at its call site.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetDoingStartNewModule =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004AF090;

    /// <summary>CServerExoAppInternal + this = the DoingStartNewModule flag.</summary>
    private const int ServerDoingStartNewModule = 0x1007C;


    /// <summary>
    /// Everything up to and including the loopback connect: panel, load screen, server.
    /// Returns with the panel parked so the game's MainLoop can pump the connection for
    /// a couple of seconds before phase 2 asks for the actual load.
    /// </summary>
    private int FirePhase1(nint menu)
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

        // Deliberately NOT shown: the panel exists only as LoadPCAutoSave's `this`.
        // Registering it (AddPanel) and popping a modal that is not there
        // (PopModalPanel — in retail it pops the real save-list screen) both leave the
        // GUI stack wrong for the menu teardown that happens at world-up, which is the
        // prime suspect for the 0xc0000005 one frame after the world appeared.
        PluginHost.Log("menu: constructing the save panel");
        SaveLoadCtor(panel, guiManager, 0, 1);

        // Mark the MAIN MENU for the manager's delete sweep, which is the one thing retail's
        // OnLoadSaveGame does that this replay never did — and the reason the menu kept
        // drawing over the loaded world forever. The sweep unregisters and deletes it on the
        // next Update. ⚠ After this, `menu` must never be read again: the destructor does not
        // null [clientInternal+0x280].
        *(uint*)(menu + MenuStateBits) = (*(uint*)(menu + MenuStateBits) & ~0x300u) | 0x400u;
        PluginHost.Log("menu: main menu marked for teardown");

        PluginHost.Log("menu: staging the load screen");
        ResetSessionStartTime();
        SetGlobalUnpauseableState(GetInGameGui(client), 1);
        HideSWInGameGui(GetInGameGui(client), 0);

        if (_entryNative == 0)
        {
            _entryNative = OperatorNew(0x400);
        }

        if (_entryNative == 0)
        {
            return 5;
        }

        byte* entry = (byte*)_entryNative;
        new Span<byte>(entry, 0x400).Clear();

        // The MODULE NAME at entry+0x00 is the load's actual argument:
        // ExecuteLoadModule forwards the entry pointer verbatim and
        // CServerExoAppInternal::LoadModule reads it as a CExoString and loads THAT
        // module. An empty name here was the whole silent stall. The agent read the
        // name out of the staged save's ERF and published it in the block.
        byte* moduleName = block.MenuLoadModule;
        if (moduleName[0] == 0)
        {
            return 7;
        }

        ExoStringFromCStr((nint)entry, (nint)moduleName);

        // The flag bits the real branch tests on its list entry: 0x10 selects the
        // autosave path and 0x20 gates the save-info handoff.
        *(uint*)(entry + 0x1C4) = 0x30;

        // The DIRECTORY NAME at +0x210 is what LoadModule actually loads from —
        // ExecuteLoadModule forwards this entry verbatim, and an empty name here is
        // exactly the silent nothing-loads stall. Built with the engine's own string
        // constructor so the buffer belongs to the engine heap.
        byte* dirName = stackalloc byte[24];
        int n = 0;
        foreach (char c in "000001 - AUTOSAVE")
        {
            dirName[n++] = (byte)c;
        }

        dirName[n] = 0;
        ExoStringFromCStr((nint)(entry + 0x210), (nint)dirName);

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

        // The retail code passes a CONSTRUCTED empty string (valid pointer to "\0") as
        // the session address, not a null CString — a null here makes the netlayer's
        // string handling reject the connect silently, which leaves the load waiting
        // for a player that never joins.
        byte zero = 0;
        ExoString address = new() { CString = (nint)(&zero), Length = 0 };
        ExoString nameStorage = default;
        nint name = GetClientName(client, &nameStorage);
        int sessionByte = *(byte*)SessionBytePtr;
        StartConnectToSession(GetNetLayer(client), 0, name, &address, 0x0A, sessionByte);

        // RequestStatus is deliberately NOT sent. In retail it rides a connection whose
        // player is already logged in; sent from here it opens the status/party exchange
        // before the server has a player game object, and the server's
        // HandlePlayerToServerParty then dereferences a null faction
        // (CSWSPlayer::GetGameObject -> GetFaction -> `mov ecx,[eax]` at 0x5272D8) about
        // half a second after the world appears. Captured by the crash probe.

        PluginHost.Log($"menu: connect started; giving it {ConnectGraceMs} ms of real pumping before the load");
        _panel = panel;
        _phase2NotBeforeMs = Environment.TickCount64 + ConnectGraceMs;
        return 0;
    }
}

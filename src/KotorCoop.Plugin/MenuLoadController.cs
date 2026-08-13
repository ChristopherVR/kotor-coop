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

    // ---- the client-side presentation hand-off ----------------------------------
    //
    // Loading the module is not the same as SHOWING it. The engine brings the world on
    // screen from inside CClientExoAppInternal::MainLoop, but only once three client-side
    // gates hold, and this replay reached none of them: the main menu was never marked for
    // deletion (so it kept drawing over everything), the "display work pending" flag was
    // never set (which alone disables the whole in-world block), and the in-game GUI was
    // never built. The result was a correct, running world behind a main menu — reported
    // from play as "still in the main menu" and "loading forever".
    // See docs/research/ingame-gui-transition.md.

    /// <summary>CClientExoApp::GetModule — the client's own module; 0 until it is ready.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetClientModule =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED530;

    /// <summary>CClientExoApp::GetPlayerCreature — the client-side player.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetClientPlayerCreature =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED540;

    /// <summary>CClientExoApp::SetAreaNotReadyToDisplay — re-arms MainLoop's in-world block.
    /// Byte-for-byte what MainLoop itself writes during its own loading stall.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetAreaNotReadyToDisplay =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x005EDAE0;

    /// <summary>CClientExoApp::InitializeInGameGUI — builds the HUD (the only thing that
    /// sets gui+0x108); retail reaches it through the Module_Info handler.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> InitializeInGameGUI =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005ED730;

    /// <summary>CGuiInGame::SetSWGuiStatus(status, bDestroyPause) — status 1 = the HUD.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetSWGuiStatus =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x0062AA00;

    /// <summary>CGuiInGame::InitializeMap — the minimap. ⚠ Dereferences the SERVER module
    /// unchecked, so it must only be called while one exists (the travel crash).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> InitializeMapGui =
        (delegate* unmanaged[Thiscall]<nint, void>)0x0062B0A0;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> EnableInput =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005EDBF0;

    /// <summary>CClientExoApp::HideLoadScreen(bSomething, bFade). Pops ONE modal panel per
    /// call, so it must run exactly once.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> HideLoadScreen =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x005EDA90;

    /// <summary>CServerExoApp::GetModule — the gate for the map init above.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetServerModule =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AE6B0;

    /// <summary>CGuiInGame + this = the initialised flag; 0 means there is no HUD yet.</summary>
    private const int InGameGuiInitialized = 0x108;

    /// <summary>CGuiInGame + this = the status field; 1 once the HUD is up. The single best
    /// "did the transition complete" read.</summary>
    private const int InGameGuiStatus = 0x34;

    /// <summary>Presentation handed over for this load; steps that pop panels run once.</summary>
    private bool _presented;

    private long _nextPresentLogMs;

    /// <summary>
    /// CServerExoAppInternal + this = the player list. ⚠ It is a POINTER, not an inline
    /// list — probed live: +0x10064 holds a heap pointer and +0x1006C is 0, so the
    /// "+8 is the count" reading below (kept only for the diagnostic line, which has always
    /// printed 0 because of it) is wrong. Do not gate anything on it.
    /// </summary>
    private const int ServerPlayerListCount = 0x10064 + 8;

    // ---- phase 3: the pump the menu loop never provides --------------------------
    //
    // Global::UpdateScreen only calls the server's MainLoop and both netlayer pumps
    // when its SECOND argument is 1, and the main-menu outer loop never passes it. A
    // server created at the menu therefore sits unserviced forever: the module load
    // request is queued, no frames are processed, no player joins — the exact stall
    // both 4.2 and this controller hit. Until the world exists, we ARE the loop.

    private static readonly delegate* unmanaged[Thiscall]<nint, void> ServerMainLoop =
        (delegate* unmanaged[Thiscall]<nint, void>)0x004AE860;

    /// <summary>CNetLayer::ProcessReceivedFrames(direction): 1 = server, 0 = client.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> ProcessReceivedFrames =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x005D57D0;

    /// <summary>CExoString::CStrConstructor(char* text) — engine-owned copy.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, nint> ExoStringFromCStr =
        (delegate* unmanaged[Thiscall]<nint, nint, nint>)0x005E5A90;

    /// <summary>Serial already acted on, so one request fires exactly one load.</summary>
    private uint _firedSerial;

    /// <summary>The constructed save panel, alive between phase 1 and phase 2.</summary>
    private nint _panel;

    /// <summary>
    /// WALL-CLOCK time before which phase 2 must not fire. The loopback connect started
    /// in phase 1 completes asynchronously in the game's own MainLoop. The first
    /// two-phase attempt counted FRAMES (600), which at the menu's unthrottled render
    /// rate elapsed in ~0.2 s — not a grace period at all. Milliseconds, not frames.
    /// </summary>
    private long _phase2NotBeforeMs;

    /// <summary>
    /// How long the loopback connect gets before the load is fired. The connect turned
    /// out to be near-synchronous — the four-second grace of the diagnosis era is not
    /// needed — but one second of margin costs nothing.
    /// </summary>
    private const int ConnectGraceMs = 1000;

    /// <summary>
    /// The stand-in save-list entry, allocated on the ENGINE heap: LoadModule receives
    /// this pointer and reads it during and after our calls, so it must never move —
    /// a managed array pinned only inside a fixed block would.
    /// </summary>
    private nint _entryNative;

    /// <summary>True once LoadPCAutoSave has been asked for and we are pumping.</summary>
    private bool _pumping;

    /// <summary>True once the character login has been rerouted for this load.</summary>
    private bool _loginRerouted;

    public void OnFrame()
    {
        // While the native browser panel is up, the main-menu object is freed (see
        // docs/research/native-browser-panel.md §2) and a load staged now would resolve
        // dangling GUI. The browser hands its connect to the agent only after its panel is
        // swept and the fresh menu exists, so deferring here costs at most a frame.
        if (NativeBrowserController.IsOpen)
        {
            return;
        }

        uint serial = block.MenuLoadSerial;
        if (serial == block.MenuLoadApplied)
        {
            return;
        }

        // Phase 3: the load is in flight. Once ExecuteLoadModule has armed the app
        // transition state ([appManager+0x14] head nonzero), the game's OWN outer loop
        // services the load — pumping the server ourselves on top of that double-steps
        // the module finalisation and crashes the game one frame after the world
        // appears (measured: 0xc0000005 at world-up with the pump active). So here we
        // only WATCH: ack when the world exists, report if the game gave up.
        if (_pumping)
        {
            // THE step that makes this load the player's actual character, and it has a
            // window rather than a moment.
            //
            // LoadPCAutoSave leaves DoingStartNewModule set, and RunModule forks the
            // character login on that flag. Set, it takes the new-game flavour, which reads
            // the PC from `TEMP:pifo` — a file a manual save simply does not contain.
            // LoadCharacterFromIFO then fails silently (its caller never checks the return),
            // login is confirmed anyway, and InitiateModuleForPlayer manufactures a BLANK
            // creature at the module's start point: the 1/1-hit-point stranger that used to
            // arrive instead of the saved character, with no faction — which is exactly what
            // the world-up crash was about.
            //
            // Cleared, RunModule takes LoadPrimaryPlayer instead — the path a menu click on
            // a normal save uses — reading the PC out of the save's own module.ifo: real
            // stats, items, saved position, and PostProcess, so the faction is set the
            // engine's way.
            //
            // Cleared here rather than immediately after LoadPCAutoSave purely so it is the
            // first thing each frame of the load window, which is comfortably before
            // RunModule reads it at the mode 1->2 transition. The flag's only setter has
            // already returned by now, so one clear is enough — the guard on it being
            // non-zero just avoids a redundant call.
            // See docs/research/character-login-chain.md.
            // ⚠ UNLESS THE WORLD IS NOT OURS. When the staged save came from the host
            // (world adoption), its module.ifo holds the HOST's character, and rerouting to
            // LoadPrimaryPlayer would bring this player up in the host's body. Leaving the
            // flag SET is then correct rather than a bug: it is the flavour that reads
            // Temp\pifo.ifo, which the agent has already filled with this player's own
            // character. The engine's own autosave path works exactly this way — the
            // only reason the reroute exists is that a MANUAL save carries no pifo.
            if (!_loginRerouted && block.AdoptWorldLogin)
            {
                _loginRerouted = true;
                block.AdoptWorldApplied = SharedBlock.AdoptLoginCarriedCharacter;
                // ASCII only: the plugin log is written as bytes, and an em-dash arrives as
                // '?' in the file.
                PluginHost.Log("menu: adopting a world - login left on TEMP:pifo (the carried character)");
            }

            if (!_loginRerouted)
            {
                nint appManagerNow = *(nint*)AppManagerPtr;
                nint serverNow = appManagerNow == 0 ? 0 : *(nint*)(appManagerNow + AppServerOffset);
                nint serverInternalNow = serverNow == 0 ? 0 : *(nint*)(serverNow + InternalOffset);

                if (serverInternalNow != 0 && *(int*)(serverInternalNow + ServerDoingStartNewModule) != 0)
                {
                    SetDoingStartNewModule(serverNow, 0);
                    _loginRerouted = true;
                    block.AdoptWorldApplied = SharedBlock.AdoptLoginSavedCharacter;
                    PluginHost.Log("menu: routed the login to the saved character (LoadPrimaryPlayer)");
                }
            }

            if (Odyssey.HasPlayer)
            {
                _pumping = false;

                // Before anything else touches the new world: a load driven from here brings
                // the player up in NO faction, and the server's own object-update pass
                // dereferences that faction about half a second later. Repaired through the
                // engine's own membership call — see docs/research/world-up-faction-crash.md.
                int faction = Odyssey.EnsurePlayerFaction();
                if (faction != 0)
                {
                    PluginHost.Log($"menu: player faction repair returned {faction}");
                }

                // The world EXISTS; it is not yet SHOWN. Hand the presentation over before
                // acking, so the agent never reports a load that the player is still looking
                // at a menu through.
                if (!TryPresentWorld())
                {
                    _pumping = true; // still waiting on the client side; retry next frame
                    return;
                }

                block.MenuLoadStatus = 0;
                block.MenuLoadApplied = serial;
                PluginHost.Log("menu: the world is up and on screen");
                return;
            }

            if (Environment.TickCount64 >= _nextDiagnosticMs)
            {
                _nextDiagnosticMs = Environment.TickCount64 + 3000;
                LogLoadState("while waiting for the world");
            }

            return;
        }

        // Phase 2 window: a short breather between the connect and the load. No manual
        // pumping — see phase 3 for why that crashes.
        if (_panel != 0)
        {
            if (Odyssey.HasServer && Environment.TickCount64 < _phase2NotBeforeMs)
            {
                return;
            }

            nint panel = _panel;
            _panel = 0;
            FirePhase2(panel, serial);
            _pumping = true;
            _loginRerouted = false;
            _presented = false;
            return;
        }

        // A server exists before phase 1 even ran: the game was never at the menu and
        // the request is moot. Ack it.
        if (Odyssey.HasServer)
        {
            block.MenuLoadStatus = 0;
            block.MenuLoadApplied = serial;
            return;
        }

        if (_firedSerial == serial)
        {
            return;
        }

        nint menu = ResolveMainMenu();
        if (menu == 0)
        {
            // Logo movies still playing; the menu object appears when they end.
            return;
        }

        _firedSerial = serial;

        int failure = FirePhase1(menu);
        if (failure != 0)
        {
            PluginHost.Log($"menu: autosave load refused (failure {failure})");
            block.MenuLoadStatus = failure;
            block.MenuLoadApplied = serial;
        }
    }

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

    /// <summary>
    /// Puts the loaded world ON SCREEN: the step this replay was missing entirely.
    ///
    /// The engine does this itself, every frame, inside the client's MainLoop — but only once
    /// its own gates hold, and a plugin-driven load reaches none of them. Step 2 below
    /// (re-arming the display-pending flag) is on its own enough to make the engine perform
    /// the rest exactly as retail; everything after it is idempotent belt-and-braces so the
    /// world appears on THIS frame rather than the next one.
    ///
    /// Returns false while the client is not ready yet — the caller retries next frame. If it
    /// never becomes ready, the two probes logged here ARE the diagnosis.
    /// See docs/research/ingame-gui-transition.md.
    /// </summary>
    private bool TryPresentWorld()
    {
        if (_presented)
        {
            return true;
        }

        nint appManager = *(nint*)AppManagerPtr;
        nint client = appManager == 0 ? 0 : *(nint*)(appManager + AppClientOffset);
        nint server = appManager == 0 ? 0 : *(nint*)(appManager + AppServerOffset);
        if (client == 0)
        {
            return false;
        }

        // 1. Wait for the CLIENT's own world. The server having a player says nothing about
        //    whether the client has caught up, and every step below needs both.
        nint clientModule = GetClientModule(client);
        nint clientCreature = GetClientPlayerCreature(client);
        if (clientModule == 0 || clientCreature == 0)
        {
            if (Environment.TickCount64 >= _nextPresentLogMs)
            {
                _nextPresentLogMs = Environment.TickCount64 + 3000;
                PluginHost.Log(
                    $"menu: waiting for the client world (module {(clientModule == 0 ? "-" : "ok")}, "
                    + $"creature {(clientCreature == 0 ? "-" : "ok")})");
            }

            return false;
        }

        _presented = true;
        nint gui = GetInGameGui(client);

        // 2. Re-arm the engine's own in-world block. This alone is the fix; the rest just
        //    stops us waiting a frame for it.
        SetAreaNotReadyToDisplay(client, 1);

        // 3. Build the HUD if the client never serviced the message that normally does it.
        if (gui != 0 && *(int*)(gui + InGameGuiInitialized) == 0)
        {
            InitializeInGameGUI(client);
        }

        // 4. Show it.
        if (gui != 0 && *(int*)(gui + InGameGuiInitialized) != 0)
        {
            SetSWGuiStatus(gui, 1, 1);

            // 5. The minimap — ONLY with a live server module: it dereferences module+0x30
            //    unchecked, which is precisely the travel crash.
            if (server != 0 && GetServerModule(server) != 0)
            {
                InitializeMapGui(gui);
            }
        }

        // 6. Give the player their controls back. Phase 2 disabled input the way retail does;
        //    retail re-enables it from the MainLoop block we were never reaching.
        SetInputClass(client, 0, 1);
        EnableInput(client);

        // 7. Resume the world timers phase 2 paused. The engine's own presentation path
        //    does this; ours must too, and skipping it is not cosmetic: the client GUI
        //    re-derives its "paused" belief from this timer's flag EVERY frame, and while
        //    the flag is stuck the pause panel is armed, the space bar is routed to
        //    "unpause" — which early-outs because the pause mask is already clear — and
        //    the game is left with a permanent PAUSED banner and a pause key that does
        //    nothing. Measured live on both instances (gui+0xB3C == 1, all masks == 0).
        if (server != 0)
        {
            UnpauseWorldTimer(ServerGetWorldTimer(server));
        }

        UnpauseWorldTimer(ClientGetWorldTimer(client));

        // 8. Drop the load screen. Exactly once — it pops one modal panel per call.
        HideLoadScreen(client, 1, 1);

        // 9. Undo phase 1's unpauseable hold.
        if (gui != 0)
        {
            SetGlobalUnpauseableState(gui, 0);
        }

        int status = gui == 0 ? -1 : *(int*)(gui + InGameGuiStatus);
        PluginHost.Log($"menu: presentation handed over (gui status {status}, expect 1)");
        return true;
    }

    /// <summary>Pauses the world timers, locks input, and asks for the load itself.</summary>
    private void FirePhase2(nint panel, uint serial)
    {
        nint appManager = *(nint*)AppManagerPtr;
        nint client = appManager == 0 ? 0 : *(nint*)(appManager + AppClientOffset);
        nint server = appManager == 0 ? 0 : *(nint*)(appManager + AppServerOffset);

        if (client == 0 || server == 0)
        {
            PluginHost.Log("menu: phase 2 lost the client or server; giving up");
            block.MenuLoadStatus = 5;
            block.MenuLoadApplied = serial;
            return;
        }

        PauseWorldTimer(ClientGetWorldTimer(client));
        PauseWorldTimer(ServerGetWorldTimer(server));
        SetInputClass(client, 0, 1);
        DisableInput(client);

        PluginHost.Log("menu: calling LoadPCAutoSave");
        LoadPCAutoSave(panel, _entryNative);

        RestoreCarriedCharacter();

        LogLoadState("immediately after LoadPCAutoSave");
    }

    /// <summary>
    /// Puts the carried character back into <c>Temp\pifo.ifo</c>, in the one gap where it
    /// cannot be raced.
    ///
    /// ⚠ MEASURED, AND WHY THE AGENT CANNOT DO THIS. <c>LoadPCAutoSave</c> exports the staged
    /// save and then copies <c>&lt;export&gt;\pifo</c> over <c>TEMP:pifo</c>
    /// (<c>CExoResMan::FileCopy</c> at 0x6CA634). It makes that call even when the staged save
    /// contains no pifo — every manual save, and every world we adopt — and a copy with no
    /// source TRUNCATES THE DESTINATION TO ZERO BYTES. So the file the agent wrote before the
    /// load is empty by the time the login reads it, <c>LoadCharacterFromIFO</c> finds nothing
    /// usable, its caller ignores the failure, and <c>InitiateModuleForPlayer</c> manufactures
    /// a blank 1/1-hit-point creature at the module start point.
    ///
    /// Live, before this existed: the adopted world came up with a PC reading hp 1, xp 0, at
    /// exactly the module start point (15.42, 20.12) instead of the saved (21.01, 21.15). An
    /// agent-side re-write loop lost the race — truncation and the login's read fall inside
    /// the same load, and a poll from another process cannot fit between them. Here there is
    /// no race at all: this runs on the game's own thread, after the copy has happened and
    /// before the login that reads the result.
    /// </summary>
    private void RestoreCarriedCharacter()
    {
        if (!block.AdoptWorldLogin)
        {
            return;
        }

        try
        {
            string directory = NativeMethods.GetModuleDirectory("dinput8.dll");
            string carried = Path.Combine(directory, "Temp", CarriedCharacterFile);
            string pifo = Path.Combine(directory, "Temp", "pifo.ifo");

            if (!File.Exists(carried))
            {
                PluginHost.Log($"menu: adopting a world but {CarriedCharacterFile} is missing; "
                    + "the login will produce a blank character");
                return;
            }

            File.Copy(carried, pifo, overwrite: true);
            PluginHost.Log(
                $"menu: restored the carried character over TEMP:pifo ({new FileInfo(pifo).Length} bytes)");
        }
        catch (Exception ex)
        {
            // Never fatal: a failed restore costs the wrong character, not the process.
            PluginHost.Log($"menu: could not restore the carried character: {ex.Message}");
        }
    }

    /// <summary>
    /// Where the agent stashes the joining player's character, next to the pifo the loader
    /// clobbers. A separate name on purpose — the loader's FileCopy targets "pifo" by name,
    /// so anything we want to survive it must not be called that.
    /// </summary>
    internal const string CarriedCharacterFile = "kcoop_carried.ifo";

    private long _nextDiagnosticMs;

    /// <summary>One line of the load's actual state, for bisecting the stall.</summary>
    private static void LogLoadState(string when)
    {
        nint appManager = *(nint*)AppManagerPtr;
        nint server = appManager == 0 ? 0 : *(nint*)(appManager + AppServerOffset);
        nint serverInternal = server == 0 ? 0 : *(nint*)(server + InternalOffset);
        nint stats = appManager == 0 ? 0 : *(nint*)(appManager + 0x14);

        int loading = serverInternal == 0 ? -1 : *(int*)(serverInternal + 0x100B4);
        int mode = serverInternal == 0 ? -1 : *(int*)(serverInternal + 0x10008);
        int players = serverInternal == 0 ? -1 : *(int*)(serverInternal + 0x10064 + 8);
        int statsHead = stats == 0 ? -1 : *(int*)stats;
        int statsMode = stats == 0 ? -1 : *(int*)(stats + 4);

        string module = "?";
        if (serverInternal != 0)
        {
            nint info = *(nint*)(serverInternal + 0x10004);
            if (info != 0)
            {
                nint text = *(nint*)(info + 0xFC);
                uint length = *(uint*)(info + 0x100);
                module = text != 0 && length is > 0 and <= 32
                    ? System.Text.Encoding.ASCII.GetString((byte*)text, (int)length)
                    : "(empty)";
            }
        }

        PluginHost.Log(
            $"menu: state {when}: loading={loading} mode={mode} players={players} "
            + $"module={module} appstate=[{statsHead},{statsMode}]");
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

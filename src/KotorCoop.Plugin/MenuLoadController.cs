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
internal sealed unsafe partial class MenuLoadController(SharedBlockView block)
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
            _syncHoldStartedMs = 0;
            _syncHoldLogged = false;
            HoldingPresentation = false;
            return;
        }

        // A server already exists. Ordinarily the game was never at the menu and the request
        // is moot — but MenuLoadInSession names the one caller that WANTS exactly this state:
        // a genuine mid-session (pause-menu-equivalent) load, requested by the `loadgame`
        // console verb. See FireMidSessionPhase1's own header for the disassembly this
        // branches on and docs/research/session-save.md's dated entry.
        if (Odyssey.HasServer)
        {
            if (!block.MenuLoadInSession)
            {
                block.MenuLoadStatus = 0;
                block.MenuLoadApplied = serial;
                return;
            }

            if (_firedSerial == serial)
            {
                return;
            }

            _firedSerial = serial;

            int midFailure = FireMidSessionPhase1();
            if (midFailure != 0)
            {
                PluginHost.Log($"menu: mid-session load refused (failure {midFailure})");
                block.MenuLoadStatus = midFailure;
                block.MenuLoadApplied = serial;
            }

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
            string directory = NativeMethods.GetGameDirectory();
            string pifo = Path.Combine(directory, "Temp", "pifo.ifo");

            // ⚠ THE GAME ROOT FIRST, Temp\ ONLY AS A FALLBACK. The stash used to live beside
            // the pifo, and on a MID-SESSION adoption that is not a safe place for it:
            // measured live 2026-08-29, Temp\ was EMPTY by the time this ran (the agent had
            // written the stash two seconds earlier and pifo.ifo was back at 0 bytes, i.e. the
            // directory had been emptied and then the loader's source-less FileCopy recreated
            // the one name it targets). The main-menu adoption never hit it because the wipe
            // rides on the mid-session teardown - DestroyServer / UnloadModule - that a load
            // from the menu never performs. The Temp\ fallback stays so a newer plugin still
            // works with an older agent.
            string carried = Path.Combine(directory, CarriedCharacterFile);
            if (!File.Exists(carried))
            {
                carried = Path.Combine(directory, "Temp", CarriedCharacterFile);
            }

            if (!File.Exists(carried))
            {
                PluginHost.Log($"menu: adopting a world but {CarriedCharacterFile} is missing; "
                    + "the login will produce a blank character");
                return;
            }

            Directory.CreateDirectory(Path.Combine(directory, "Temp"));
            File.Copy(carried, pifo, overwrite: true);
            PluginHost.Log(
                $"menu: restored the carried character over TEMP:pifo ({new FileInfo(pifo).Length} bytes, "
                + $"from {Path.GetFileName(Path.GetDirectoryName(carried))}\\{CarriedCharacterFile})");
        }
        catch (Exception ex)
        {
            // Never fatal: a failed restore costs the wrong character, not the process.
            PluginHost.Log($"menu: could not restore the carried character: {ex.Message}");
        }
    }

    /// <summary>
    /// Where the agent stashes the joining player's character: the GAME DIRECTORY ROOT, under
    /// a name that is not "pifo".
    ///
    /// Both halves matter. The loader's <c>FileCopy</c> targets "pifo" by name and truncates
    /// it to zero when the staged save has none, so the stash must not be called that; and a
    /// mid-session adoption empties <c>Temp\</c> on its way through (measured 2026-08-29 — see
    /// <see cref="RestoreCarriedCharacter"/>), so the stash must not live there.
    /// </summary>
    internal const string CarriedCharacterFile = "kcoop_carried.ifo";

    private long _nextDiagnosticMs;


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

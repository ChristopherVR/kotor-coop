namespace KotorCoop.Plugin;

// The mid-session (pause-menu-equivalent) load path: the bindings/fields retail's
// bFromMainMenu==0 branch needs beyond FirePhase1's own, plus FireMidSessionPhase1 - see
// its own doc comment for exactly how it differs from FirePhase1. See MenuLoadController.cs
// for the OnFrame dispatcher this is called from.
internal sealed unsafe partial class MenuLoadController
{
    // ---- mid-session (pause-menu-equivalent) load — see FireMidSessionPhase1 -----------
    //
    // CAppManager::DestroyServer(this) — tears an EXISTING server down (StopServices,
    // destructor, operator delete, appManager+8 = 0) without building a replacement.
    // CreateServer's own disassembly (0x00401380) does the identical teardown internally
    // when appManager+8 is already nonzero, so calling this first is not strictly required
    // for CreateServer to succeed — it IS required to match retail's own call order, which
    // is what CSWGuiSaveLoad::LoadGame's bFromMainMenu==0 branch does at 0x006CB310..0x006CB331,
    // immediately before the SAME CreateServer(appManager, 0) call FirePhase1 already makes.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> DestroyServer =
        (delegate* unmanaged[Thiscall]<nint, void>)0x00401280;

    /// <summary>CClientExoApp::UnloadModule — the CLIENT-side half of the same retail
    /// pre-step (0x006CB324 in the disassembly): tears the client's own module/world state
    /// down, so GetClientModule(client) reads 0 afterward instead of a stale pointer
    /// TryPresentWorld's own client-module gate could otherwise read as "already ready".
    /// Also bound in Odyssey.cs as ClientUnloadModuleFn for a different purpose
    /// (travel-map-init-crash.md's server-module-gone window) — declared again here so this
    /// file stays self-contained, matching every other binding above.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ClientUnloadModule =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005ED430;

    /// <summary>CClientExoAppInternal + this = the client's gui-manager pointer — the SAME
    /// CSWGuiManager a main-menu load reads off [menu+0x18], reachable this way with no main
    /// menu object at all (docs/research/ingame-gui-transition.md §1.2's own "gui_manager").
    /// </summary>
    private const int ClientInternalGuiManager = 0x274;

    /// <summary>CGuiInGame + this = the lazily-constructed message-box panel
    /// (CSWGuiMessageBox*), always non-null once CGuiInGame::Initialize has run — i.e.
    /// always non-null mid-session, since the session's own world-up already ran it.
    /// Retail's bFromMainMenu==0 branch of LoadGame reads THIS object's own +0x64 bit 0
    /// before doing anything else and returns without loading at all if it is clear.
    ///
    /// ⚠ We LOG that bit and proceed; only a NULL sub-object is a refusal. Measured live
    /// 2026-08-29 on both rig instances simultaneously: `[msgbox+0x64]` = 0x06, bit 0 CLEAR,
    /// on a healthy host twenty minutes into normal play as well as on the client. It marks
    /// the pause-menu ROUTE into LoadGame, not "safe to load" — see FireMidSessionPhase1's
    /// own comment for the full argument and for the four live loads that had to poke it by
    /// hand before this changed.</summary>
    private const int InGameGuiMessageBox = 0x98;

    private const int MessageBoxStateBits = 0x64;


    /// <summary>
    /// The mid-session counterpart to <see cref="FirePhase1"/>: replays retail's OWN extra
    /// pre-step for <c>CSWGuiSaveLoad::LoadGame</c>'s <c>bFromMainMenu==0</c> branch
    /// (0x006CB101..0x006CB331 in the retail binary) before falling into the SAME
    /// autosave-branch sequence <see cref="FirePhase1"/> already reproduces
    /// (0x006CB256..0x006CB3E8) — see docs/research/session-save.md's dated entry for the
    /// disassembly trail this was derived from.
    ///
    /// Differences from <see cref="FirePhase1"/>, each traced to a real branch retail takes
    /// only when a server already exists:
    ///   1. <c>guiManager</c> comes from <see cref="ClientInternalGuiManager"/> rather than a
    ///      main-menu object, because there is no main menu to read <c>[menu+0x18]</c> from
    ///      mid-session.
    ///   2. The message-box precondition retail's <c>LoadGame</c> checks FIRST
    ///      (<see cref="InGameGuiMessageBox"/>'s own +0x64 bit 0) is READ AND LOGGED rather
    ///      than obeyed — see the comment at the check itself. A null sub-object is still a
    ///      refusal.
    ///   3. <see cref="DestroyServer"/> + <see cref="ClientUnloadModule"/> run immediately
    ///      before <see cref="CreateServer"/>, exactly where retail's own disassembly places
    ///      them (0x006CB310..0x006CB331) — this is the actual teardown that makes
    ///      <c>WorldLoadRule.IsUnannouncedTeardown</c> fire correctly for this path:
    ///      <c>TryGetWorldEpoch</c>'s own object-array-pointer half changes the instant
    ///      <see cref="CreateServer"/> replaces the <c>CServerExoApp</c> instance (confirmed
    ///      by disassembling <c>CreateServer</c> itself — 0x00401399..0x004013CC does the
    ///      identical StopServices/destructor/delete/null-then-construct-fresh sequence
    ///      whenever <c>appManager+8</c> is already nonzero, so the object really is torn
    ///      down and rebuilt, not merely reused).
    ///   4. There is no main-menu object to mark for the delete sweep — retail's own
    ///      <c>[menu+0x44]</c> write in <see cref="FirePhase1"/> has nothing to touch here.
    /// Everything else — the panel construction, the load-screen sequence, the loopback
    /// connect, and phases 2/3 in <see cref="OnFrame"/> below — is IDENTICAL to
    /// <see cref="FirePhase1"/>'s own path, because retail's own disassembly shows it is: the
    /// <c>bFromMainMenu</c> bit only gates the four differences above, not the autosave
    /// branch itself.
    ///
    /// Live-verified: the DestroyServer/UnloadModule/CreateServer sequence has run cleanly on
    /// the rig several times (docs/research/session-save.md's 2026-08-23 and 2026-08-24
    /// passes), each of those after poking the bit above by hand.
    /// </summary>
    private int FireMidSessionPhase1()
    {
        nint appManager = *(nint*)AppManagerPtr;
        nint client = appManager == 0 ? 0 : *(nint*)(appManager + AppClientOffset);
        if (client == 0)
        {
            return 2;
        }

        nint gui = GetInGameGui(client);
        if (gui == 0)
        {
            return 8;
        }

        // Retail's own FIRST check (0x006CB109..0x006CB120). A NULL sub-object is still a real
        // refusal: it means CGuiInGame::Initialize has not run, i.e. there is no in-game GUI to
        // load over at all.
        nint messageBox = *(nint*)(gui + InGameGuiMessageBox);
        if (messageBox == 0)
        {
            PluginHost.Log("menu: mid-session load refused - there is no in-game GUI here yet");
            return 9;
        }

        // ⚠ BIT 0 IS OBSERVED, NOT OBEYED — and that changed on 2026-08-29 on live evidence.
        //
        // Until then this returned failure 9 whenever the bit was clear, "defensively",
        // because its semantics had only ever been read out of the disassembly. Four separate
        // live rounds then had to poke the bit by hand to get a load at all
        // (docs/research/session-save.md: msgboxpoke.ps1 twice, `partyselect ok` twice), and
        // this round measured it directly on BOTH rig instances at once: `[msgbox+0x64]` reads
        // 0x06 — bit 0 CLEAR — on a client mid-conversation AND on a host that had been playing
        // normally for twenty minutes with nothing modal on screen. So the bit cannot mean
        // "something is blocking"; on this evidence it marks the GUI ROUTE (the pause menu
        // sets it on its way into LoadGame), and our load does not come through that route and
        // never will. Replicating it was therefore not a safety net — it was a guaranteed
        // refusal for every caller this function has.
        //
        // Nothing downstream reads the bit: retail's own branch only tests it here, and the
        // sequence below (DestroyServer / UnloadModule / CreateServer / LoadPCAutoSave) never
        // touches the message box. The four hand-poked live loads are the direct evidence that
        // proceeding with it clear is safe, because a poke that sets a bit nothing reads is
        // exactly equivalent to not reading it.
        //
        // It is still LOGGED, every time, because it is free evidence: if a future round ever
        // sees a mid-session load misbehave, the first question is what this byte held.
        byte messageBoxBits = *(byte*)(messageBox + MessageBoxStateBits);
        PluginHost.Log(
            $"menu: mid-session load precondition [msgbox+0x64] = 0x{messageBoxBits:X2} "
            + $"(retail's own gate is bit 0 = {messageBoxBits & 1}; observed, not obeyed)");

        nint clientInternal = *(nint*)(client + InternalOffset);
        nint guiManager = clientInternal == 0 ? 0 : *(nint*)(clientInternal + ClientInternalGuiManager);
        if (guiManager == 0)
        {
            return 2;
        }

        nint panel = OperatorNew(SaveLoadPanelSize);
        if (panel == 0)
        {
            return 3;
        }

        // Deliberately NOT shown, and no AddPanel/PopModalPanel — same reasoning as
        // FirePhase1's own comment: the panel exists only as LoadPCAutoSave's `this`. There
        // is no main-menu object here to mark for the delete sweep either.
        PluginHost.Log("menu: constructing the save panel (mid-session)");
        SaveLoadCtor(panel, guiManager, 0, 1);

        PluginHost.Log("menu: staging the load screen (mid-session)");
        ResetSessionStartTime();
        SetGlobalUnpauseableState(gui, 1);
        HideSWInGameGui(gui, 0);

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

        byte* moduleName = block.MenuLoadModule;
        if (moduleName[0] == 0)
        {
            return 7;
        }

        ExoStringFromCStr((nint)entry, (nint)moduleName);

        // Same flag bits FirePhase1 sets: 0x10 selects the autosave path, 0x20 gates the
        // save-info handoff (retail's own confirm-dialog branch, which this replay bypasses
        // entirely by never calling CSWGuiSaveLoad::LoadGame itself — same as FirePhase1).
        *(uint*)(entry + 0x1C4) = 0x30;

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

        // THE difference retail's own disassembly places right here (0x006CB310..0x006CB331):
        // tear the existing server AND the client's own module down before creating a fresh
        // server — the main-menu branch never needed this because there was nothing to tear
        // down there.
        PluginHost.Log("menu: tearing down the existing server for a mid-session load");
        DestroyServer(appManager);
        ClientUnloadModule(client);

        PluginHost.Log("menu: creating the server and loopback session (mid-session)");
        CreateServer(appManager, 0);

        nint server = *(nint*)(appManager + AppServerOffset);
        if (server == 0)
        {
            return 4;
        }

        // Same "constructed empty string, not a null CString" rule FirePhase1's own comment
        // explains.
        byte zero = 0;
        ExoString address = new() { CString = (nint)(&zero), Length = 0 };
        ExoString nameStorage = default;
        nint name = GetClientName(client, &nameStorage);
        int sessionByte = *(byte*)SessionBytePtr;
        StartConnectToSession(GetNetLayer(client), 0, name, &address, 0x0A, sessionByte);

        PluginHost.Log(
            $"menu: connect started; giving it {ConnectGraceMs} ms of real pumping before the load (mid-session)");
        _panel = panel;
        _phase2NotBeforeMs = Environment.TickCount64 + ConnectGraceMs;
        return 0;
    }
}

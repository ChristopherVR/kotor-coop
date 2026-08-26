namespace KotorCoop.Plugin;

// The client-side presentation hand-off: putting a loaded world ON SCREEN (re-arming the
// in-world block, building/showing the HUD and minimap, restoring input, resuming timers,
// dropping the load screen) plus the co-op sync hold that keeps the load screen up until a
// joining peer's phantom exists. See docs/research/ingame-gui-transition.md and
// MenuLoadController.cs for the OnFrame dispatcher this is called from.
internal sealed unsafe partial class MenuLoadController
{
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

    /// <summary>True while a ready world is being held behind the load screen waiting for
    /// the first co-op sync. PluginHost uses this to drive the world pipeline from MainLoop
    /// if the swap hook turns out to be dormant under the load screen — without that, the
    /// phantom the hold waits for could never spawn and every hold would hit its deadline.</summary>
    public bool HoldingPresentation { get; private set; }

    /// <summary>When the co-op presentation hold started (world ready, first sync not yet
    /// visible); 0 while no hold is running. See <see cref="TryPresentWorld"/>.</summary>
    private long _syncHoldStartedMs;

    private bool _syncHoldLogged;

    /// <summary>How long the load screen may keep covering a READY world while waiting for
    /// the first co-op sync. The phantom usually lands within a second of the world existing
    /// (the agent resolves and pumps under the load screen); this is the ceiling for an
    /// agent that is slow, dead, or whose peer walked into another module mid-load.</summary>
    private const long SyncHoldMaxMs = 4000;

    private long _nextPresentLogMs;

    /// <summary>
    /// CServerExoAppInternal + this = the player list. ⚠ It is a POINTER, not an inline
    /// list — probed live: +0x10064 holds a heap pointer and +0x1006C is 0, so the
    /// "+8 is the count" reading below (kept only for the diagnostic line, which has always
    /// printed 0 because of it) is wrong. Do not gate anything on it.
    /// </summary>
    private const int ServerPlayerListCount = 0x10064 + 8;


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

        // ⚠ THE SEAMLESS GATE. The world is ready — but if the session has admitted another
        // player and their body has not been spawned yet, presenting now means the player
        // walks into an "empty" world and watches co-op pop in around them. Hold the load
        // screen instead, so the agent's resolve, the peer slots, and the phantom spawn all
        // land while the screen is still covered.
        //
        // The signal is SessionPeers (written every dispatcher tick), NOT RemoteCount:
        // RemoteCount is only written after the agent's player resolve — a full memory scan
        // rate-limited to 1 Hz — which measured ~1.7 s AFTER world-ready, exactly the pop-in
        // this gate removes. Released the frame the phantom exists; released early when the
        // agent HAS resolved (it wrote the player id) and still filtered everyone out (the
        // peer is really in another module — nobody to wait for); released at the deadline
        // regardless (a slow or dead agent must never hold a ready world hostage).
        if (block.SessionPeers > 0 && block.PhantomCount == 0
            && !(block.PlayerCreature != 0 && block.RemoteCount == 0 && _syncHoldStartedMs != 0))
        {
            long now = Environment.TickCount64;
            if (_syncHoldStartedMs == 0)
            {
                _syncHoldStartedMs = now;
            }

            if (now - _syncHoldStartedMs < SyncHoldMaxMs)
            {
                if (!_syncHoldLogged)
                {
                    _syncHoldLogged = true;
                    HoldingPresentation = true;
                    PluginHost.Log("menu: world ready; holding presentation for the first co-op sync");
                }

                return false;
            }

            PluginHost.Log("menu: co-op sync hold timed out; presenting anyway");
        }
        else if (_syncHoldLogged)
        {
            PluginHost.Log($"menu: co-op sync landed under the load screen ({Environment.TickCount64 - _syncHoldStartedMs} ms)");
        }

        HoldingPresentation = false;
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
}

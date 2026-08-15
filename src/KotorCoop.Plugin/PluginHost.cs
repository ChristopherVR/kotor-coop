using System.Runtime.CompilerServices;
using System.Text;

namespace KotorCoop.Plugin;

/// <summary>
/// Runs inside swkotor.exe.
///
/// Two threads, with a strict division that matters:
///   - the WORKER thread only touches the shared block (heartbeat, diagnostics). It must
///     never call an engine function; the engine is not reentrant.
///   - the FRAME HOOK runs on the game's own thread and is the only place engine calls
///     happen: spawning phantoms and stepping them along.
/// </summary>
internal static unsafe class PluginHost
{
    private static readonly object Gate = new();
    private static bool _started;
    private static string _logPath = "kotorcoop-plugin.log";

    /// <summary>Per-subsystem logs are this + channel + ".log" (see <see cref="Log"/>).</summary>
    private static string _channelPathPrefix = "kotorcoop-";
    private static SharedBlock* _block;
    private static SharedBlockView? _view;
    private static PhantomController? _controller;
    private static ItemController? _items;
    private static FocusKeeper? _focus;
    private static CombatController? _combat;
    private static EnemyPresenceController? _enemyPresence;
    private static PartyPresenceController? _partyPresence;
    private static IncomingDamageController? _incomingDamage;

    private static PeerDamageController? _peerDamage;
    private static TestDriveController? _testDrive;
    private static DoorController? _doors;
    private static PlaceableController? _placeables;
    private static LootController? _loot;
    private static TravelController? _travel;
    private static PauseController? _pause;
    private static CombatStateController? _combatState;
    private static PauseKeyController? _pauseKey;
    private static JournalController? _journal;
    private static MapMarkerController? _mapMarkers;
    private static PortraitController? _portraits;
    private static BarkController? _barks;
    private static InteractionLockController? _interactionLocks;
    private static PartyRosterController? _partyRoster;
    private static ControlReturnController? _controlReturn;
    private static MenuLoadController? _menuLoad;
    private static ConnectMenuController? _connectMenu;
    private static ChatInputController? _chatInput;
    private static PasswordPromptController? _passwordPrompt;
    private static ServerBrowserController? _browser;
    private static NativeBrowserController? _nativeBrowser;
    private static DeathController? _death;
    private static SpectateController? _spectate;
    private static ChatOverlayController? _chatOverlay;
    private static ConsoleAssistController? _consoleAssist;
    private static DialogueSpectateController? _dialogueSpectate;
    private static EncounterController? _encounters;
    private static Thread? _worker;
    private static volatile bool _running = true;

    /// <summary>The swap-hook frame count last seen from MenuFrame, and how many MainLoop
    /// ticks it has sat unchanged — the "is the swap hook dormant?" probe the seamless
    /// hold's fallback heartbeat uses.</summary>
    private static long _heldFrameSample;
    private static int _heldStallTicks;

    public static nint BlockAddress => (nint)_block;

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
        }

        try
        {
            string directory = NativeMethods.GetModuleDirectory("dinput8.dll");
            _logPath = Path.Combine(directory, "kotorcoop-plugin.log");
            _channelPathPrefix = Path.Combine(directory, "kotorcoop-");

            // Before any controller is constructed: several read their settings from coop.ini
            // in their field initialisers.
            PluginConfig.UseDirectory(directory);

            Log($"=== KotorCoop plugin loaded into pid {NativeMethods.GetCurrentProcessId()} ===");

            // FIRST, before a single hook or controller exists. A fault during startup is
            // exactly as fatal as one during play, and the probe is what turns "the game
            // vanished" into a line naming the exception.
            CrashProbe.Install();

            // The in-game ~ console needs the engine's dev console, which is gated by
            // EnableCheats in swkotor.ini. Enabling it is the game's own sanctioned surface.
            EnsureCheatsEnabled(directory);

            if (!AllocateBlock())
            {
                Log("FATAL: could not allocate the shared block");
                return;
            }

            _view = new SharedBlockView((byte*)_block);
            _controller = new PhantomController(_view);
            _items = new ItemController(_view);
            _focus = new FocusKeeper(_view);
            _combat = new CombatController(_view);
            _enemyPresence = new EnemyPresenceController(_view);
            _partyPresence = new PartyPresenceController(_view);
            _incomingDamage = new IncomingDamageController(_view);
            _peerDamage = new PeerDamageController(_view);
            _testDrive = new TestDriveController(_view);
            _doors = new DoorController(_view);
            _placeables = new PlaceableController(_view);
            _loot = new LootController(_view);
            _travel = new TravelController(_view);
            _menuLoad = new MenuLoadController(_view);
            _connectMenu = new ConnectMenuController(_view);
            _chatInput = new ChatInputController();
            _passwordPrompt = new PasswordPromptController(_view);
            _browser = new ServerBrowserController(_view);
            _nativeBrowser = new NativeBrowserController(_view);
            _death = new DeathController(_view);
            _spectate = new SpectateController(_view);
            _chatOverlay = new ChatOverlayController(_view);
            _consoleAssist = new ConsoleAssistController();
            _dialogueSpectate = new DialogueSpectateController(_view);
            _encounters = new EncounterController(_view);
            _pause = new PauseController(_view);
            _combatState = new CombatStateController(_view);
            _pauseKey = new PauseKeyController();
            _journal = new JournalController(_view);
            _mapMarkers = new MapMarkerController(_view);
            _portraits = new PortraitController(_view);
            _barks = new BarkController(_view);
            _interactionLocks = new InteractionLockController(_view);
            _partyRoster = new PartyRosterController(_view);
            _controlReturn = new ControlReturnController(_view);

            // The minimap draw runs on the render path and has no other route to peer
            // positions, so it reads the block directly.
            MinimapPeerHook.Bind(_view);

            // Install the frame hook from here because DirectInput8Create - our caller - is
            // verified to run on the game's main thread during startup. Installing from the
            // worker thread would patch an import from the wrong thread.
            if (FrameHook.Install(OnFrame, out string hookDiagnostic))
            {
                Log($"frame hook installed: {hookDiagnostic}");
            }
            else
            {
                Log($"frame hook NOT installed: {hookDiagnostic}");
            }

            // Intercept the console command interpreter so co-op verbs route to the block and
            // are swallowed (no "function <x> not found"), while real typos still get flagged.
            // Independent of the frame hook, so the co-op console works at the menu too.
            if (FuncInterpHook.Install(_view, out string interpDiagnostic))
            {
                Log($"console interp hooked: {interpDiagnostic}");
            }
            else
            {
                Log($"console interp NOT hooked: {interpDiagnostic}");
            }

            // A per-frame callback that also fires at the main menu (the SwapBuffers hook is
            // dormant there behind the menu movie), used to drive the Co-op menu button.
            if (MainLoopHook.Install(MenuFrame, out string menuHookDiagnostic))
            {
                Log($"menu frame hook installed: {menuHookDiagnostic}");
            }
            else
            {
                Log($"menu frame hook NOT installed: {menuHookDiagnostic}");
            }

            // The world-up party message crashes on a half-initialised player creature's
            // null faction; the guard routes that one case onto the handler's own
            // default-faction path. Required for the plugin-driven load (the browser's
            // click-to-join lands through it).
            if (PartyFactionGuard.Install(out string guardDiagnostic))
            {
                Log($"party faction guard: {guardDiagnostic}");
            }
            else
            {
                Log($"party faction guard NOT installed: {guardDiagnostic}");
            }

            // One-shot animations (draws, swings, flourishes, item use) are dispatched and
            // finished between polls, so they are captured by hooking the engine's own
            // dispatch rather than sampled.
            if (AnimationHook.Install(out string animationDiagnostic))
            {
                Log($"animations: {animationDiagnostic}");
            }
            else
            {
                Log($"animation hook NOT installed: {animationDiagnostic}");
            }

            // An NPC's one-line bark never enters the dialogue system, so nothing else in the
            // mod can see it. Opt-out because it patches a hot engine entry.
            if (PluginConfig.ReadBool("bark_sync", true))
            {
                if (BarkHook.Install(out string barkDiagnostic))
                {
                    Log($"barks: {barkDiagnostic}");
                }
                else
                {
                    Log($"bark hook NOT installed: {barkDiagnostic}");
                }
            }

            // Which dialogue reply the player picks is dispatched and gone between frames,
            // so it is captured by hooking the engine's own reply entry rather than sampled.
            if (DialogueReplyHook.Install(out string replyDiagnostic))
            {
                Log($"dialogue: {replyDiagnostic}");
            }
            else
            {
                Log($"dialogue reply hook NOT installed: {replyDiagnostic}");
            }

            // One conversation for the whole session (the BG3 rule): while a peer holds the
            // token, this instance cannot start a dialogue at all. The spectate follow is
            // the one sanctioned exception (DialogueGateHook.BypassGrants).
            // Damage landing on a peer's phantom, so it can be forwarded to their real
            // character. Read-only and filtered to phantom ids, but it sits on every
            // damage event in the game, so it is verify-bytes like the rest.
            if (PhantomHitHook.Install(out string hitDiagnostic))
            {
                Log($"combat: {hitDiagnostic}");
            }
            else
            {
                Log($"phantom-hit watch NOT installed: {hitDiagnostic}");
            }

            DialogueGateHook.Enabled = PluginConfig.ReadBool("dialogue_exclusive", true);
            if (DialogueGateHook.Install(out string gateDiagnostic))
            {
                Log($"dialogue: {gateDiagnostic}"
                    + (DialogueGateHook.Enabled ? string.Empty : " (disabled by coop.ini)"));
            }
            else
            {
                Log($"dialogue gate NOT installed: {gateDiagnostic}");
            }

            // Opt-in diagnostic: records who writes CGuiInGame+0xB4, which is the one thing
            // that would settle why a wedged instance's conversation flag comes straight back.
            DialogStateWatchHook.Enabled = PluginConfig.ReadBool("dialog_state_watch", false);
            if (DialogStateWatchHook.Enabled)
            {
                Log(DialogStateWatchHook.Install(out string watchDiagnostic)
                    ? $"dialogue: {watchDiagnostic}"
                    : $"dialogue: dialog-state watch NOT installed: {watchDiagnostic}");
            }

            // A conversation ended from our frame hook can be followed by one last reply list
            // the server had already queued, and the dialogue camera resolves its participants
            // with no null check. Odyssey.CloseDialogue removes the source; this is the net.
            if (ShotCameraGuardHook.Install(out string shotDiagnostic))
            {
                Log($"dialogue: {shotDiagnostic}");
            }
            else
            {
                Log($"shot-camera guard NOT installed: {shotDiagnostic}");
            }

            // The minimap draws exactly one arrow and has no note or party loop, so a peer
            // gets there by drawing that same arrow again, inside the viewport the engine
            // has already opened. coop.ini minimap_peers=0 opts out.
            if (PluginConfig.ReadBool("minimap_peers", true))
            {
                if (MinimapPeerHook.Install(out string minimapDiagnostic))
                {
                    Log($"minimap: {minimapDiagnostic}");
                }
                else
                {
                    Log($"minimap peers NOT installed: {minimapDiagnostic}");
                }
            }

            // The engine reverts any pause that lacks a valid "PausedBy" record within one
            // tick, which in a loopback session is every pause there is — space bar, HUD
            // button and mirror alike. See PauseSanityPatch.
            if (PauseSanityPatch.Install(out string pausePatchDiagnostic))
            {
                Log($"pause: {pausePatchDiagnostic}");
            }
            else
            {
                Log($"pause sanity patch NOT installed: {pausePatchDiagnostic}");
            }

            // The HUD pause button toggles on server mask bit 0x02, which is ALSO the
            // focus-loss reason FocusKeeper clears every 30 frames — so the button's "am I
            // paused?" read is always false and it can pause but never unpause. Measured:
            // PausesCleared went 0 -> 1 -> 2, one per click. See PauseButtonHook.
            if (PauseButtonHook.Install(out string pauseButtonDiagnostic))
            {
                Log($"pause: {pauseButtonDiagnostic}");
            }
            else
            {
                Log($"pause button NOT hooked: {pauseButtonDiagnostic}");
            }

            // Co-op death handling: suppress the control auto-swap and the game-over screen.
            // Both patches stay inert until the agent turns them on, so a plugin with no
            // session behaves exactly like stock KOTOR.
            if (DeathController.Install(out string deathDiagnostic))
            {
                Log($"death handling: {deathDiagnostic}");
            }
            else
            {
                Log($"death handling NOT installed: {deathDiagnostic}");
            }

            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "KotorCoop plugin",
            };

            _worker.Start();
            Log("worker thread started");

            // Last, once everything the agent looks for exists: start the agent for this
            // instance so installing the plugin is the whole installation. Opt-out in
            // coop.ini; a missing agent executable is simply skipped.
            AgentLauncher.EnsureRunning(directory);
        }
        catch (Exception ex)
        {
            // Never let plugin failure take the game down: the export must still forward.
            Log($"FATAL during startup: {ex}");
        }
    }

    private static bool AllocateBlock()
    {
        // VirtualAlloc rather than a managed allocation: the block must sit at a fixed
        // address in a MEM_PRIVATE committed region for the whole session, because the
        // external agent locates it by scanning that memory for the signature.
        nint memory = NativeMethods.VirtualAlloc(
            0,
            SharedBlock.TotalSize,
            NativeMethods.MemCommit | NativeMethods.MemReserve,
            NativeMethods.PageReadWrite);

        if (memory == 0)
        {
            return false;
        }

        _block = (SharedBlock*)memory;
        Unsafe.InitBlockUnaligned(_block, 0, SharedBlock.TotalSize);

        _block->Magic = SharedBlock.MagicValue;
        // ⚠ ONE NUMBER IN TWO FILES. KotorGameBridge.PluginSignature embeds this byte in the
        // pattern the agent scans for, so bumping only one end reads as "plugin shared block
        // not found" — which looks like a broken install rather than a version skew.
        // 37 = the load-screen texture in the browser details array (SharedBlock, version 37).
        _block->Version = 38;

        // A guard dword after the version. Magic plus version alone was not selective
        // enough: a scan once matched something else at a lower address and the agent
        // silently anchored on it. 16 bytes makes a coincidental match implausible.
        *(uint*)((byte*)_block + 0x0C) = SharedBlock.GuardValue;

        // ⚠ The dialogue nodes start at -1, not at the zero the block was just filled with.
        // Zero is a VALID entry index — the first line of a conversation — so a block that has
        // never held one would otherwise be indistinguishable from one sitting at the top of a
        // dialogue. Caught by the node probe on the first live run: an instance that had never
        // talked to anybody reported node 0 with an empty partner tag.
        *(int*)((byte*)_block + SharedBlock.OffsetLocalDialogueNode) = -1;
        *(int*)((byte*)_block + SharedBlock.OffsetRemoteDialogueNode) = -1;

        WriteModuleName("kotor");

        Log($"shared block at 0x{memory:X} ({SharedBlock.TotalSize} bytes)");
        return true;
    }

    /// <summary>World identity last frame; 0 = no stable world was seen.</summary>
    private static ulong _epoch;

    /// <summary>True while the pre-store flush window is open (trans_pending set).</summary>
    private static bool _transitionPending;

    /// <summary>
    /// Called once per frame on the GAME'S thread by <see cref="FrameHook"/>.
    /// Allocation-free and exception-swallowing by construction.
    ///
    /// The epoch gate runs before anything else, because a module transition destroys the
    /// entire object array and resets the id counters: every cached creature pointer is
    /// freed memory and every remembered object id names a stranger. Both live bugs this
    /// fixes were of exactly that shape — CombatController suppressing kills on recycled
    /// ids, and PhantomController calling through a vtable read from a freed object.
    /// </summary>
    /// <summary>
    /// Per-frame callback from <see cref="MainLoopHook"/>, which — unlike the SwapBuffers
    /// <see cref="FrameHook"/> — also fires at the main menu (behind the menu movie the swap hook
    /// is dormant). Used only to keep the Co-op menu button un-hidden and bound.
    /// </summary>
    private static void MenuFrame()
    {
        // The native browser first: its IsOpen gate tells the others the main-menu object
        // is currently freed (a shown child panel reclaims the menu until close rebuilds it).
        _nativeBrowser?.OnFrame();
        _connectMenu?.OnFrame();
        _browser?.OnFrame();

        // The menu-load replay lives HERE, not on the SwapBuffers hook: at the main menu the
        // back-buffer swap is movie-gated and never fires (measured live: the frame counter
        // stays 0 at a settled menu), so a load request serviced from OnFrame sat forever.
        // MainLoop fires at the menu and in-world alike, on the same game thread.
        _menuLoad?.OnFrame();

        // A locked server's password prompt. Here rather than on the swap hook for the same
        // reason the menu-load replay is: the ask lands while the player is at the main menu,
        // having just pressed Connect in the browser, where the swap hook does not fire. It is
        // a no-op unless the agent has bumped the request serial.
        _passwordPrompt?.OnFrame();

        // Insurance for the seamless-arrival hold: the world pipeline (phantom spawn, the
        // very thing the hold waits for) lives on the SwapBuffers hook, and if the load
        // screen renders the way the menu movie does, that hook is dormant — the phantom
        // could never spawn and every hold would run to its deadline. So while the hold is
        // active and the swap counter is visibly stalled, drive the pipeline from here at
        // ~10 Hz (30 MainLoop ticks ≈ 100 ms). Its own epoch gate keeps it safe: the hold
        // only starts after the module and player creature exist.
        if (_menuLoad is { HoldingPresentation: true })
        {
            if (FrameHook.FrameCount == _heldFrameSample && ++_heldStallTicks >= 30)
            {
                _heldStallTicks = 0;
                OnFrame();
            }
            else if (FrameHook.FrameCount != _heldFrameSample)
            {
                _heldFrameSample = FrameHook.FrameCount;
                _heldStallTicks = 0;
            }
        }

        // Console history/completion follows the ~ console itself, which opens at the menu
        // AND in-world — so it lives on this hook, not the (menu-dormant) SwapBuffers one.
        _consoleAssist?.OnFrame();
    }

    private static void OnFrame()
    {
        // Before the epoch gate, deliberately: these run at the main menu too, which is exactly
        // where the gate returns early (no server) — the Enter chat prompt and the chat
        // overlay. (The Co-op button, browsers and menu-load run on MainLoopHook — the swap
        // hook is dormant at the menu; the ~ console routes through FuncInterpHook.)
        _chatInput?.OnFrame();
        _chatOverlay?.OnFrame();

        // Before the epoch gate, and before anything else touches the world: during a module
        // transition there is a frame in which the server's module is destroyed but the
        // client still holds one, and the engine's own map init dereferences the server
        // module every frame without a null check. Closing the client's world here is what
        // the engine's dump-player handler would do a frame later — see
        // docs/research/travel-map-init-crash.md.
        if (Odyssey.CloseClientWorldIfServerModuleGone())
        {
            Log("transition: closed the client world ahead of the map init");
        }

        if (!Odyssey.TryGetWorldEpoch(out ulong epoch))
        {
            // No world, or a transition mid-flight. Drop everything now, so nothing runs
            // against freed objects on the first frame of the new module.
            if (_epoch != 0)
            {
                _epoch = 0;
                _transitionPending = false;
                InvalidateAll("world unloading");
            }

            // The agent must not adopt a pointer into a world that no longer exists.
            _view?.PublishEnginePlayer(0, 0);
            return;
        }

        // Every frame, so the agent can adopt the engine's own player instead of trusting
        // a scan that can latch a freed copy (SharedBlock.OffsetEnginePlayerPointer).
        if (_view is { } playerView)
        {
            nint enginePlayer = Odyssey.GetPlayerCreaturePointer();
            playerView.PublishEnginePlayer(
                enginePlayer,
                enginePlayer != 0 ? *(uint*)(enginePlayer + 0x04) : 0);
        }

        if (epoch != _epoch)
        {
            if (_epoch != 0)
            {
                InvalidateAll("module changed");
            }

            _epoch = epoch;
            _transitionPending = false;
        }

        // Focus first: the others do nothing useful in a paused world.
        _focus?.OnFrame();

        if (Odyssey.IsAreaTransitionPending())
        {
            // Pre-store flush: StoreCurrentModule serialises every non-party creature and
            // item on the way out, so anything the plugin spawned must be gone before the
            // area is stored or it becomes a permanent resident of the saved module. One
            // destroy per frame; the window is ~150 frames and there are at most 16 bodies.
            if (!_transitionPending)
            {
                _transitionPending = true;
                Log("area transition pending: flushing spawned objects before the module is stored");
            }

            if (_controller is { } phantoms && phantoms.FlushOneBody())
            {
                return;
            }

            if (_items is { } items && items.FlushOneBody())
            {
                return;
            }

            if (MaterialisedEnemies.FlushOneBody())
            {
                return;
            }

            _mapMarkers?.FlushOneBody();
            return;
        }

        if (_transitionPending)
        {
            // The pending flag cleared without the module changing (an aborted or
            // cancelled transition). The flush destroyed real bodies, so reset state and
            // let the controllers rebuild them.
            _transitionPending = false;
            InvalidateAll("transition flushed but not taken");
        }

        if (_travel is { HasPendingRequest: true } travel)
        {
            // A session-commanded module move. Same rule as a door transition: everything
            // this plugin spawned must be gone before the outgoing area is stored, or it
            // becomes a permanent resident of the saved module. One destroy per frame
            // until clean, then the warp itself.
            if (_controller is { } phantoms && phantoms.FlushOneBody())
            {
                return;
            }

            if (_items is { } items && items.FlushOneBody())
            {
                return;
            }

            if (MaterialisedEnemies.FlushOneBody())
            {
                return;
            }

            if (_mapMarkers is { } markers && markers.FlushOneBody())
            {
                return;
            }

            travel.Execute();
            return;
        }

        // Publish whatever one-shot animation the hook caught since the last frame, so the
        // agent can ship it with this tick's state.
        if (_view is { } view)
        {
            if (AnimationHook.Sequence != view.LocalOneShotSerial)
            {
                view.LocalOneShotAnim = AnimationHook.LastAnimation;
                view.LocalOneShotSerial = AnimationHook.Sequence;
            }

            // And every other creature's, for the enemy channel: the owning agent matches
            // these ids against its census and streams the swings with the presence tick.
            for (int i = 0; i < SharedBlock.EnemyOneShotCapacity; i++)
            {
                AnimationHook.ReadCreatureSlot(i, out uint id, out uint anim, out uint serial);
                view.SetEnemyOneShot(i, id, anim, serial);
            }
        }

        // Before every world controller: if the session ended since the last frame, undo what
        // it did to this game before anything acts on the cleared block (most importantly,
        // before PhantomController starts destroying the bodies the camera may be following).
        PumpSessionLifecycle();

        // Death first among the world controllers: a downed player must be published (and a
        // pending revive applied) before the phantom/combat passes read the same creature.
        _death?.OnFrame();

        // Right after death, on the state it just published: a downed player's camera
        // follows a living peer instead of their own corpse.
        _spectate?.OnFrame();

        _controller?.OnFrame();
        _items?.OnFrame();
        _combat?.OnFrame();

        // After the combat pass so a death applied this frame is respected (the presence
        // slot's dead flag and the kill both come from the same owner tick).
        _enemyPresence?.OnFrame();
        _partyPresence?.OnFrame();

        // After the companion mirror, not before: reconciling the roster can spawn a companion
        // body, and it should be driven by the mirror on the NEXT frame with the block already
        // read rather than half-way through this one.
        _partyRoster?.OnFrame();

        // After death handling has published whether we are down, so damage that would
        // arrive on an already-downed player is skipped rather than fighting the revive.
        _incomingDamage?.OnFrame();

        // After the presence passes, so the watch tests the position the player is actually
        // standing at this frame, and so a fired encounter's spawns are already in the
        // owner's census by the next pump.
        _encounters?.OnFrame();

        // After the combat pass, so the enemy the owner has just killed is already off the
        // dealt ledger before this publishes it — and so a peer's damage lands on a copy
        // whose health the mirror has already brought up to date this frame.
        _peerDamage?.OnFrame();

        _doors?.OnFrame();
        _placeables?.OnFrame();
        _loot?.OnFrame();
        _pause?.OnFrame();
        _combatState?.OnFrame();
        _pauseKey?.OnFrame();
        _journal?.OnFrame();
        _mapMarkers?.OnFrame();
        _portraits?.OnFrame();
        _barks?.OnFrame();
        _interactionLocks?.OnFrame();

        // After the companion mirror and the portraits, because it decides what the player is
        // driving and where the camera points from the state those two have just settled.
        _controlReturn?.OnFrame();

        // Read-only: publishes the owner's current conversation line for the other player to
        // spectate. Runs in-world only (a conversation needs a live server + GUI).
        _dialogueSpectate?.OnFrame();

        // Test hooks last, so a driven conversation is opened against fully-updated state.
        _testDrive?.OnFrame();
    }

    /// <summary>Whether a session was driving this instance on the previous frame; the falling
    /// edge is a disconnect (or a session the agent tore down for any other reason).</summary>
    private static bool _sessionActive;

    /// <summary>
    /// THE ONE PLACE A SESSION IS UNDONE.
    ///
    /// ⚠ THIS EXISTS BECAUSE PER-FIELD TEARDOWN KEPT MISSING ONE. Reported from play: after a
    /// client disconnected, Trask was back in the party but clicking him did nothing — the
    /// agent's control-swap suppression was still raised in the block, so DeathController kept
    /// arming the byte patch that turns <c>ChangeCharacter</c> (Tab, every party portrait, and
    /// the death auto-swap) into an immediate return. Beside it, mirrored companions were left
    /// in the Neutral faction and pinned at min-1-hp, the spectate camera could stay bolted to
    /// a phantom the very next controller was about to destroy, and the party portraits stayed
    /// parked off-screen. Each was a separate omission of the same shape.
    ///
    /// So the rule is now structural: <c>block.Command</c> is the session's presence, and its
    /// FALLING edge releases every controller that holds engine or GUI state, in one ordered
    /// list. Anything a controller does to the world on connect belongs in its
    /// <c>ReleaseSession</c> — not in a second copy of this list somewhere else.
    ///
    /// The agent lowers the command LAST when it clears the block
    /// (KotorGameBridge.SessionScopedBlockLevels), so by the time this runs every other level
    /// is already down and a controller's release reads a clean block.
    /// </summary>
    private static void PumpSessionLifecycle()
    {
        bool active = _view is { } view && view.Command == SharedBlock.CommandSpawnAndDrive;
        if (active == _sessionActive)
        {
            return;
        }

        _sessionActive = active;

        if (active)
        {
            Log("session: a co-op session is driving this instance");
            return;
        }

        Log("session: released — undoing what it did to this game");

        // Camera first, and before PhantomController's despawn pass: the follow behaviour
        // holds a raw client-creature pointer, so a camera left on a phantom outlives the
        // body by however long the give-back takes.
        _spectate?.ReleaseSession();
        _controlReturn?.ReleaseSession();

        // Then the bodies: faction and the min-1-hp pin, which are SAVED state.
        _partyPresence?.ReleaseSession();

        // Then everything that was holding the player's own game hostage.
        _death?.ReleaseSession();
        _interactionLocks?.ReleaseSession();
        _partyRoster?.ReleaseSession();
        _portraits?.ReleaseSession();
    }

    private static void InvalidateAll(string reason)
    {
        Log($"epoch: {reason}; clearing phantom, item, combat and door state");

        // First, before any controller can act on them again: the agent-written world inputs
        // are stale (the agent's reads fail through a transition, so it cannot clear them
        // itself), and a controller acting on them spawns bodies for the wrong world.
        _view?.ClearAgentWorldInputs();

        _controller?.Invalidate();
        _items?.Invalidate();
        _combat?.Invalidate();
        _enemyPresence?.Invalidate();
        MaterialisedEnemies.Invalidate();
        _partyPresence?.Invalidate();
        _incomingDamage?.Invalidate();
        _peerDamage?.Invalidate();
        _testDrive?.Invalidate();
        PhantomHitHook.Invalidate();
        AnimationHook.InvalidateCreatureTable();
        _doors?.Invalidate();
        _placeables?.Invalidate();
        _loot?.Invalidate();
        _travel?.Invalidate();
        _pause?.Invalidate();
        _combatState?.Invalidate();
        _journal?.Invalidate();
        _mapMarkers?.Invalidate();
        _connectMenu?.Invalidate();
        _death?.Invalidate();
        _spectate?.Invalidate();
        _encounters?.Invalidate();
        _barks?.Invalidate();
        _interactionLocks?.Invalidate();
        _partyRoster?.Invalidate();

        // ⚠ THIS LINE WAS MISSING, so the spectate controller's ENTIRE teardown had never run
        // in the history of the mod — every other controller here was reset on a module change
        // and that one was not. A conversation's partner TAG then resolved to a different body
        // in the new module, and a published node index named an entry in a completely
        // different .dlg.
        //
        // The controller no longer depends on this: it watches the same world epoch PluginHost
        // gates its frame on and invalidates itself. Both are kept deliberately. The
        // self-invalidation is what makes it correct when this list is wrong again, and this
        // line is what makes the list mean what it says.
        _dialogueSpectate?.Invalidate();
    }

    private static void WorkerLoop()
    {
        uint lastRemoteCount = 0;
        uint lastPhantomCount = 0;
        long lastFrames = 0;
        int quietTicks = 0;

        while (_running)
        {
            try
            {
                _block->Heartbeat++;

                // The crash probe's handler is machine code and cannot format a log line
                // itself (see CrashProbe: the managed version of it WAS the crash). It leaves
                // a record behind; this is where that record becomes words.
                CrashProbe.DrainReports();

                if (_view is { } view)
                {
                    uint remotes = view.RemoteCount;
                    if (remotes != lastRemoteCount)
                    {
                        lastRemoteCount = remotes;
                        Log($"agent reports {remotes} remote peer(s)");
                    }

                    uint phantoms = view.PhantomCount;
                    if (phantoms != lastPhantomCount)
                    {
                        lastPhantomCount = phantoms;
                        Log($"{phantoms} phantom(s) alive");
                    }

                    // Report a stalled frame hook once rather than every tick. If the hook
                    // is installed but never fires, everything else silently does nothing,
                    // and that is worth saying out loud.
                    long frames = FrameHook.FrameCount;
                    if (FrameHook.IsInstalled && frames == lastFrames)
                    {
                        if (++quietTicks == 120)
                        {
                            Log("WARNING: frame hook installed but has not fired for ~2s");
                        }
                    }
                    else
                    {
                        quietTicks = 0;
                        lastFrames = frames;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"worker error: {ex.Message}");
            }

            Thread.Sleep(16);
        }

        Log("worker stopping");
    }

    private static void WriteModuleName(string value)
    {
        byte* field = (byte*)_block + 0x20;
        Unsafe.InitBlockUnaligned(field, 0, SharedBlock.ModuleNameLength);

        int count = Math.Min(value.Length, SharedBlock.ModuleNameLength - 1);
        for (int i = 0; i < count; i++)
        {
            char c = value[i];
            field[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }
    }

    /// <summary>
    /// Ensures <c>[Game Options] EnableCheats=1</c> in swkotor.ini so the grave/tilde key
    /// opens the engine's developer console (which the co-op ~ overlay reads). Idempotent and
    /// conservative: it touches only that one key, and does nothing if the ini is absent
    /// (the game recreates it with cheats off, but never silently — the log says so).
    /// </summary>
    private static void EnsureCheatsEnabled(string directory)
    {
        try
        {
            string path = Path.Combine(directory, "swkotor.ini");
            if (!File.Exists(path))
            {
                Log("swkotor.ini not found; set [Game Options] EnableCheats=1 to use the ~ console");
                return;
            }

            List<string> lines = [.. File.ReadAllLines(path)];
            int section = -1;
            int keyLine = -1;

            for (int i = 0; i < lines.Count; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    if (trimmed.Equals("[Game Options]", StringComparison.OrdinalIgnoreCase))
                    {
                        section = i;
                    }
                    else if (section >= 0 && keyLine < 0)
                    {
                        break; // left the section without finding the key
                    }
                }
                else if (section >= 0 && trimmed.StartsWith("EnableCheats", StringComparison.OrdinalIgnoreCase))
                {
                    keyLine = i;
                    break;
                }
            }

            if (keyLine >= 0)
            {
                if (lines[keyLine].Trim().EndsWith('1'))
                {
                    return; // already enabled
                }

                lines[keyLine] = "EnableCheats=1";
            }
            else if (section >= 0)
            {
                lines.Insert(section + 1, "EnableCheats=1");
            }
            else
            {
                lines.Add("[Game Options]");
                lines.Add("EnableCheats=1");
            }

            File.WriteAllLines(path, lines);
            Log("enabled the developer console in swkotor.ini ([Game Options] EnableCheats=1)");
        }
        catch (Exception ex)
        {
            Log($"could not enable cheats in swkotor.ini: {ex.Message}");
        }
    }

    /// <summary>
    /// The subsystems that get their own log file.
    ///
    /// One 15 MB file with every subsystem interleaved is unreadable when you are chasing
    /// one of them, which is the whole complaint. Rather than retro-fit a channel argument
    /// onto ~200 call sites, this uses the prefix convention the messages ALREADY follow
    /// ("dialogue: ...", "pause: ...", "combat: ..."), so a line files itself.
    ///
    /// The list is explicit on purpose: a free-form prefix would spawn a new file for every
    /// typo and every message that merely happens to contain a colon.
    /// </summary>
    private static readonly string[] LogChannels =
    [
        "dialogue", "pause", "combat", "bark", "phantom", "item", "loot", "door",
        "journal", "minimap", "death", "transition", "epoch", "party", "enemy",
        "chat", "travel", "portrait", "encounter", "hook", "agent", "session",
    ];

    /// <summary>Prefixes that name the same subsystem as one of the channels above.</summary>
    private static string CanonicalChannel(string prefix) => prefix switch
    {
        "death handling" => "death",
        "phantoms" => "phantom",
        "barks" => "bark",
        "items" => "item",
        "doors" => "door",
        "frame hook" or "menu frame hook" or "console interp" => "hook",
        _ => prefix,
    };

    /// <summary>
    /// Files a line by its own prefix. Returns "general" for anything unprefixed — startup
    /// banners, fatals, and the handful of messages that never adopted the convention.
    /// </summary>
    private static string ChannelFor(string message)
    {
        int colon = message.IndexOf(':');

        // A timestamp or an address would otherwise look like a prefix; a real one is a
        // short lowercase word or two, and never contains a digit.
        if (colon <= 0 || colon > 20)
        {
            return "general";
        }

        string prefix = CanonicalChannel(message[..colon].ToLowerInvariant());

        return Array.IndexOf(LogChannels, prefix) >= 0 ? prefix : "general";
    }

    public static void Log(string message)
    {
        try
        {
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";

            // The combined file stays the timeline: it is what you read when the question is
            // "what happened around 14:48", and what verify.ps1 and every existing note
            // grep. The split files are for reading ONE subsystem end to end.
            File.AppendAllText(_logPath, line, Encoding.ASCII);
            File.AppendAllText(_channelPathPrefix + ChannelFor(message) + ".log", line, Encoding.ASCII);
        }
        catch
        {
            // Logging must never throw into the game's call stack.
        }
    }
}

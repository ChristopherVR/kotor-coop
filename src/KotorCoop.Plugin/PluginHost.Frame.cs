namespace KotorCoop.Plugin;

// The two per-frame schedules: MenuFrame (MainLoopHook, fires at the main menu too) and
// OnFrame (FrameHook/SwapBuffers, dormant at the menu and during a cinematic). Each call
// below is in a hand-picked order — the short comment on a call says WHY it has to come
// before or after its neighbour. This is deliberately not a generic "list of controllers to
// iterate": the order is the point, and a comment that explains it is worth more here than
// anywhere else in the plugin.
internal static unsafe partial class PluginHost
{
    /// <summary>World identity last frame; 0 = no stable world was seen.</summary>
    private static ulong _epoch;

    /// <summary>True while the pre-store flush window is open (trans_pending set).</summary>
    private static bool _transitionPending;

    /// <summary>
    /// True while THIS instance's own <see cref="TravelController"/> warp is in flight — a
    /// session-commanded module move (a peer being pulled along to follow the host, or the
    /// host being pulled along by a peer; also the <c>moviewarp</c> debug verb). Round
    /// (2026-08-23, roadmap item 14 phase 2 audit): <c>TravelController.Execute</c>'s own doc
    /// comment already says a co-op warp like this "never raises trans_pending at all" — which
    /// means, unaudited, it produces EXACTLY the same "world torn down with trans_pending never
    /// having gone true" shape <see cref="WorldLoadRule.IsUnannouncedTeardown"/> was built to
    /// recognise as a Load Game action. A peer-follow warp is one of the most common teardown
    /// events in an active session (it fires on ordinary module travel any time a client was not
    /// already where the host went), so without this flag every such warp misclassifies as an
    /// unannounced load. Set the moment a travel request is first seen pending (well before
    /// <c>Odyssey.TravelToModule</c> actually runs, the same "announce before the teardown"
    /// timing <c>_transitionPending</c> uses for a real door), cleared the one canonical place
    /// every epoch-valid frame passes through — see the reset immediately after the world-epoch
    /// gate below — so a request that turns out to be a same-module no-op (no teardown at all)
    /// does not leave a stale "announced" flag that could mask a LATER, genuinely unannounced
    /// load.
    /// </summary>
    private static bool _travelAnnounced;

    /// <summary>
    /// True once a Door-kind announce (<see cref="TransitionAnnounceWatch.OnTransitionBegan"/>)
    /// has fired since the last epoch reset — the scripted-funnel watch below gates on this so
    /// a door crossing, which per <c>DoorUse.cs</c>'s own doc comment ALSO raises
    /// <c>move_to_module_pending</c> once its fade completes (the identical three engine calls,
    /// just later), never gets a second, misleadingly-tagged "Script" announce for the same
    /// crossing. Reset at the same two points <see cref="_transitionPending"/> is.
    /// </summary>
    private static bool _doorTransitionAnnouncedThisEpoch;

    /// <summary>Rising-edge tracker for <c>Odyssey.IsMoveToModulePending()</c> — see the
    /// scripted-transition-watch call site below for the full gating story. ALSO fed into
    /// <see cref="WorldLoadRule.IsUnannouncedTeardown"/> alongside <see cref="_transitionPending"/>/
    /// <see cref="_travelAnnounced"/> (2026-09-03 audit finding: this instance's own native
    /// StartNewModule() — a galaxy-map leg, the escape pod, a Leviathan/Star Forge stage change —
    /// raises neither of those two, and was misclassified as an unannounced Load without this).
    /// Reset at the same two points <see cref="_transitionPending"/> is.</summary>
    private static bool _moveToModulePendingSeen;

    /// <summary>Party-table probe window (docs/research/transition-crashes.md, 2026-09-03, the
    /// two-companion host warp crash): for the first few seconds after every world change the
    /// frame logs <see cref="Odyssey.DescribeServerPartyTable"/> at 5 Hz, so the state
    /// <c>CSWPartyTable::PositionParty</c> is about to dereference (it runs when the client's
    /// area message lands, seconds after the epoch flips) is in the log BEFORE any fault.</summary>
    private static long _partyProbeUntilMs;
    private static long _partyProbeNextMs;
    private const int PartyProbeWindowMs = 6000;
    private const int PartyProbeIntervalMs = 200;

    /// <summary>
    /// coop.ini <c>transition_announce_scripted</c> (docs/research/transition-smoothness.md,
    /// 2026-08-27) — gates ONLY whether the plugin ever calls
    /// <c>Odyssey.IsMoveToModulePending()</c>/<c>TransitionAnnounceWatch.OnScriptedTransitionBegan</c>
    /// at all; off, nothing new is read and nothing new touches the shared block. Layered UNDER
    /// the pre-existing <c>transition_announce</c> agent-side relay gate, not a replacement for
    /// it: both must be on for a scripted announce to actually reach a peer. Default ON — every
    /// read behind it is guarded the same way the door reader already is (a bad/garbage read
    /// returns 0 length and nothing is published), so the worst case of this being wrong is "no
    /// announce for that one transition", never a bogus one or a crash.
    /// </summary>
    private static readonly bool _transitionAnnounceScriptedEnabled =
        PluginConfig.ReadBool("transition_announce_scripted", true);

    // ---- frame-time histogram (docs/research/lag-audit.md) ------------------------------
    //
    // Cheap and off by default: an EWMA-free accumulator (see FrameTimeHistogram's own doc
    // comment for why not an EWMA) fed one delta per SwapBuffers callback — i.e. once per
    // rendered frame, the same cadence a hitch is actually felt at — and flushed to the log
    // every ~10 s so a live round can measure frame-time hitching instead of guessing at it.
    // `coop.ini frame_stats=1`.

    private static readonly bool _frameStatsEnabled = PluginConfig.ReadBool("frame_stats", false);

    /// <summary>coop.ini <c>transition_close_client_world</c> — see the call site in
    /// <c>OnFrame</c>. ON by default (today's behaviour and
    /// docs/research/travel-map-init-crash.md's own fix); exists so
    /// docs/research/shared-inventory-v2.md §15's deterministic host warp crash can be
    /// bisected against it rather than argued about.</summary>
    private static readonly bool _closeClientWorldOnTransition =
        PluginConfig.ReadBool("transition_close_client_world", true);

    /// <summary>A frame slower than this counts as a "hitch" in the flushed summary. 33 ms is
    /// one dropped frame at 30 fps / two at 60 fps — coarse enough to ignore ordinary jitter,
    /// fine enough to catch a stall a player would notice.</summary>
    private const double FrameHitchThresholdMs = 33.0;

    private const long FrameStatsFlushIntervalMs = 10_000;

    private static FrameTimeHistogram _frameStats;
    private static long _frameStatsLastTicks;
    private static long _frameStatsLastFlushMs;

    /// <summary>Called once per SwapBuffers, before anything else in <see cref="OnFrame"/> —
    /// so a stall inside a world controller is attributed to the frame it happened in, not the
    /// next one this ships a frame late relative to.</summary>
    private static void RecordFrameTime()
    {
        long nowTicks = System.Diagnostics.Stopwatch.GetTimestamp();

        if (_frameStatsLastTicks != 0)
        {
            double deltaMs = (nowTicks - _frameStatsLastTicks) * 1000.0
                / System.Diagnostics.Stopwatch.Frequency;
            _frameStats.Add(deltaMs, FrameHitchThresholdMs);
        }

        _frameStatsLastTicks = nowTicks;

        long nowMs = Environment.TickCount64;
        if (_frameStatsLastFlushMs == 0)
        {
            _frameStatsLastFlushMs = nowMs;
            return;
        }

        if (nowMs - _frameStatsLastFlushMs < FrameStatsFlushIntervalMs || !_frameStats.HasSamples)
        {
            return;
        }

        FrameTimeSnapshot snap = _frameStats.Snapshot();
        Log($"frame stats: {snap.SampleCount} frames, {snap.AverageMs:F2} ms average, "
            + $"{snap.MaxMs:F1} ms worst, {snap.OverThresholdCount} over {FrameHitchThresholdMs:F0} ms");
        _frameStats.Reset();
        _frameStatsLastFlushMs = nowMs;
    }

    /// <summary>
    /// Per-frame callback from <see cref="MainLoopHook"/>, which — unlike the SwapBuffers
    /// <see cref="FrameHook"/> — also fires at the main menu (behind the menu movie the swap hook
    /// is dormant). Used only to keep the Co-op menu button un-hidden and bound.
    /// </summary>
    private static void MenuFrame()
    {
        // FIRST, before anything else here: this is the pre-MainLoop point §10.9 verified is
        // safe to call CClientExoApp::PlayMovie from (docs/research/cinematics.md) — outside
        // the script VM's own call, but also outside render and outside SwapBuffers, unlike the
        // SwapBuffers-nested OnFrame below where §10.8 found the indefinite hang. Blocks for the
        // length of the film when something is armed; everything below simply runs a frame late
        // on that (rare) occasion, same as it already does across a real transition's own stall.
        PendingMoviePlay.RunIfArmed();

        // First of all, and on THIS hook rather than OnFrame: a pre-rendered movie blocks the
        // main thread inside WaitForMovie, so the SwapBuffers callback cannot fire during one
        // and the only evidence a cinematic played is the gap it leaves between two of these
        // callbacks. Measuring it here brackets exactly the client tick, the server tick and
        // the present. See docs/research/cinematics.md.
        CinematicWatch.OnFrame(_view);

        // Next, and unconditionally: whether the agent is even still talking to us has
        // nothing to do with menu/world state, and both failure modes it reports can happen
        // before the player has picked anything (a dead auto-start, a hung agent while still
        // sitting at the menu). This hook is the one guaranteed to run there — the
        // SwapBuffers hook (OnFrame below) is dormant behind the main-menu movie.
        _agentLiveness?.OnFrame();

        // Same hook, same reasoning, right after: the persistent connection indicator (item
        // 18) shows Connecting/Failed while still at the main menu, before a session (or even
        // the SwapBuffers hook) exists at all.
        _connectionIndicator?.OnFrame();

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

        // The post-adopt starting-gear repair, right after: it watches block.MenuLoadApplied for
        // the adopted-world load's own "world is up" ack, which _menuLoad.OnFrame() just wrote
        // (if it was going to this frame) — reading it here rather than on a later hook means at
        // most one MenuFrame tick of latency, not a whole SwapBuffers-dormant menu-load screen's
        // worth. See PostAdoptEquipRepairController's own doc comment.
        _postAdoptEquipRepair?.OnFrame();

        // The new-character capture, for the same reason and in the same place: it watches for
        // the character retail chargen writes, which happens between the main menu and the very
        // first world — the whole span where the swap hook is dormant. A no-op unless the agent
        // has armed it (coop.ini join_mode = newcharacter).
        _newCharacterCapture?.OnFrame();

        // The chargen-drive verbs, here for the same reason: every one of them is only
        // meaningful at the main menu and through the character-creation screens, where the
        // SwapBuffers hook does not fire.
        _chargenDrive?.OnFrame();

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

    /// <summary>How long the worker thread may go unseen before the game thread says so. The
    /// worker sleeps 16 ms a pass, so two seconds is ~125 missed passes — never a scheduling
    /// hiccup.</summary>
    private const long WorkerStallMs = 2000;

    /// <summary>Stall start, or 0 while the worker is being seen normally.</summary>
    private static long _workerStalledSinceMs;

    /// <summary>
    /// The game-thread half of the two-sided stall watchdog (the worker owns the other half:
    /// it watches <c>FrameHook.FrameCount</c> and reports a frame hook that has not fired for
    /// ~2 s). Neither thread can report its OWN death, so each reports the other's.
    ///
    /// ⚠ WHY: a plugin log that simply STOPS is ambiguous, and 2026-09-16 that ambiguity was
    /// read as a dead worker on the host after a <c>topup</c>. It was not — two live
    /// reproduction attempts on 2026-09-17 (fight, both players on the 1-hp floor, <c>topup</c>,
    /// 60 s of watching) found the console still answering and the peer's phantom still being
    /// driven across the whole silent window. The silence was the <c>attack probe</c>
    /// diagnostic closing its own window with nothing else due to log. With these two lines a
    /// reader can tell the three cases apart — worker gone, game thread gone, or simply
    /// quiet — without reproducing anything.
    /// </summary>
    private static void WatchWorkerThread()
    {
        // Interlocked.Read, not a plain load — see WorkerLastTickMs' own doc comment: a bare
        // 64-bit read is not atomic on x86 and could observe a torn value written concurrently
        // by the worker thread.
        long lastTick = Interlocked.Read(ref WorkerLastTickMs);
        if (lastTick == 0)
        {
            // The worker has not had its first pass yet (startup); nothing to judge.
            return;
        }

        long now = Environment.TickCount64;
        if (now - lastTick > WorkerStallMs)
        {
            if (_workerStalledSinceMs == 0)
            {
                _workerStalledSinceMs = lastTick;
                Log($"WARNING: the plugin worker thread has not run for {(now - lastTick) / 1000.0:F1}s "
                    + "while the game thread keeps going - the agent channel, the crash-probe "
                    + "drain and log rotation are all stalled");
            }

            return;
        }

        if (_workerStalledSinceMs != 0)
        {
            Log($"the plugin worker thread is running again after {(now - _workerStalledSinceMs) / 1000.0:F1}s");
            _workerStalledSinceMs = 0;
        }
    }

    private static void OnFrame()
    {
        // Before absolutely everything else: whatever this frame is about to spend its time
        // on, the delta since the LAST callback already happened and belongs to the frame
        // that just finished, not to whatever runs next.
        if (_frameStatsEnabled)
        {
            RecordFrameTime();
        }

        WatchWorkerThread();

        // Before the epoch gate: the delayed-event probe (coop.ini delay_probe, default OFF)
        // records on the game's thread inside four engine splices and must be turned into log
        // lines from somewhere file I/O is safe. Inert — and free — unless the probe installed.
        DelayEventProbeHook.Drain();
        if (ShowInGameGuiProbeHook.IsInstalled)
        {
            ShowInGameGuiProbeHook.Drain();
            FieldWriteWatch.KeepArmedOnGuiBusy();
        }

        // Before the epoch gate, deliberately: these run at the main menu too, which is exactly
        // where the gate returns early (no server) — the Enter chat prompt and the chat
        // overlay. (The Co-op button, browsers and menu-load run on MainLoopHook — the swap
        // hook is dormant at the menu; the ~ console routes through FuncInterpHook.)
        _chatInput?.OnFrame();
        _chatOverlay?.OnFrame();

        // Also before the epoch gate: the pad drives the main menu's cursor too, and its
        // release-everything path must run on the very frame a world goes away.
        _gamepad?.OnFrame();

        // Before the epoch gate, and before anything else touches the world: during a module
        // transition there is a frame in which the server's module is destroyed but the
        // client still holds one, and the engine's own map init dereferences the server
        // module every frame without a null check. Closing the client's world here is what
        // the engine's dump-player handler would do a frame later — see
        // docs/research/travel-map-init-crash.md.
        //
        // ⚠ BISECT LEVER (coop.ini transition_close_client_world, default 1 = today's
        // behaviour), added 2026-08-29 for docs/research/shared-inventory-v2.md §15's own
        // deterministic host crash on ANY relocating warp — 5/5, identical instruction and
        // stack every time, symbolicated to:
        //
        //   CSWGuiInGameEquip::UpdatePortraits+0x48E  (0x006B5FEE, `mov edx,[eax]`)
        //     <- CClientExoApp::GetSWParty 0x005ED8B0, then CSWParty::GetCharacter(1) 0x006346C0
        //        returning NULL, with CSWGuiInGameEquip::Constructor+0xA23 and
        //        CGuiInGame::Initialize+0x680 on the stack.
        //
        // i.e. the CLIENT-side party still reports a member count above 1 while member 1's
        // object no longer resolves, and the new module's GUI construction walks it. The call
        // below is the only thing in this codebase that empties the client's world early, and
        // it fires 60-70 ms before the fault every time, so it is the prime suspect — but
        // "prime suspect" is not evidence, and this project bisects plugin crashes by config
        // flag rather than by guesswork. Setting the key to 0 restores the pre-fix behaviour
        // (and, with it, travel-map-init-crash.md's own map-init fault), which is exactly what
        // a clean A/B needs.
        if (_closeClientWorldOnTransition && Odyssey.CloseClientWorldIfServerModuleGone())
        {
            Log("transition: closed the client world ahead of the map init");
            PluginEvents.RaiseServerModuleGone();
        }

        if (!Odyssey.TryGetWorldEpoch(out ulong epoch))
        {
            // No world, or a transition mid-flight. Drop everything now, so nothing runs
            // against freed objects on the first frame of the new module.
            if (_epoch != 0)
            {
                // Roadmap item 14 phase 2's load-detection primitive (see WorldLoadRule.cs):
                // judged HERE, before _transitionPending/_travelAnnounced/
                // _moveToModulePendingSeen are reset below, because that reset is exactly what
                // would otherwise erase the one fact this decision needs — whether this
                // teardown was ANNOUNCED (a real door/trigger transition, this instance's own
                // TravelController warp — see _travelAnnounced's own doc comment for why a
                // co-op warp needs the same treatment as a door — OR a NATIVE scripted
                // StartNewModule() this instance's own engine originated: a galaxy-map
                // departure, the Endar Spire escape pod, a Leviathan/Star Forge stage change,
                // or any other of the six move_to_module_pending writers named in
                // Odyssey.Travel.cs's own comment) before the world it belonged to went away.
                //
                // ⚠ 2026-09-03 audit finding (docs/research/transition-smoothness.md's real-
                // crossing follow-up): _moveToModulePendingSeen was ADDED (2026-08-27) to gate
                // the scripted-funnel ANNOUNCE (OnScriptedTransitionBegan, so a peer follows a
                // galaxy-map/escape-pod/etc. transition immediately) but was never folded into
                // THIS check. A native scripted transition never raises trans_pending
                // (galaxy-map-and-party-select.md §1.4: "neither leg raises trans_pending at
                // all") and never runs through TravelController (that only drives a PEER being
                // pulled along, not the instance whose own engine originated the script), so
                // without this flag the LEADING side of every galaxy-map leg, the escape pod,
                // and every Leviathan/Star Forge stage change misclassified its own teardown as
                // an unannounced Load Game action — bumping WorldLoadSerial, which
                // SaveOrLoadClassifier then correlates with the transition autosave's digest
                // change into a hard SaveOrLoadVerdict.Load, forcing a REAL rejoin
                // (RejoinTrigger.OwnLoad/HostReload, both gates ON by default) on every such
                // transition — not the softer, already-fixed SaveIdentityMismatch path
                // SaveLineage absorbs, but the harder edge-triggered one it does not touch.
                if (WorldLoadRule.IsUnannouncedTeardown(
                    _transitionPending || _travelAnnounced || _moveToModulePendingSeen))
                {
                    _view?.PublishWorldLoadObserved();
                    Log("world: unannounced teardown - treating as a load, not a transition");
                    PluginEvents.RaiseWorldLoadObserved();
                }

                _epoch = 0;
                _transitionPending = false;
                _travelAnnounced = false;
                _doorTransitionAnnouncedThisEpoch = false;
                _moveToModulePendingSeen = false;
                _travel?.ClearOwnWarpPending();
                InvalidateAll("world unloading");
            }

            // The agent must not adopt a pointer into a world that no longer exists.
            _view?.PublishEnginePlayer(0, 0);
            PhantomHitHook.WatchLocalPlayer(0);
            return;
        }

        // The world is still readable this frame, so whatever _travelAnnounced was carrying
        // did not precede a teardown (either no travel was requested, or a travel request
        // resolved as a same-module no-op with nothing torn down) — consume it here, the one
        // canonical point every epoch-valid frame passes through, so a stale "announced" flag
        // can never survive to mask a LATER, genuinely unannounced load. The travel branch
        // below re-arms it for THIS frame if (and only if) a request is actually pending now.
        _travelAnnounced = false;

        // Every frame, so the agent can adopt the engine's own player instead of trusting
        // a scan that can latch a freed copy (SharedBlock.OffsetEnginePlayerPointer).
        if (_view is { } playerView)
        {
            nint enginePlayer = Odyssey.GetPlayerCreaturePointer();
            uint enginePlayerId = enginePlayer != 0 ? *(uint*)(enginePlayer + 0x04) : 0;
            playerView.PublishEnginePlayer(enginePlayer, enginePlayerId);

            // ROUND 52: same fresh read, handed to the damage hook so it can recognise a hit
            // landing on the LOCAL PC without an engine call inside the hook itself. See
            // PhantomHitHook.WatchLocalPlayer's own doc comment.
            PhantomHitHook.WatchLocalPlayer(enginePlayerId);
        }

        if (epoch != _epoch)
        {
            if (_epoch != 0)
            {
                InvalidateAll("module changed");
            }

            _epoch = epoch;
            _transitionPending = false;
            _doorTransitionAnnouncedThisEpoch = false;
            _moveToModulePendingSeen = false;
            _travel?.ClearOwnWarpPending();
            _partyProbeUntilMs = Environment.TickCount64 + PartyProbeWindowMs;
            _partyProbeNextMs = 0;
        }

        if (_partyProbeUntilMs != 0)
        {
            long probeNow = Environment.TickCount64;
            if (probeNow >= _partyProbeUntilMs)
            {
                _partyProbeUntilMs = 0;
            }
            else if (probeNow >= _partyProbeNextMs)
            {
                _partyProbeNextMs = probeNow + PartyProbeIntervalMs;
                string table = Odyssey.DescribeServerPartyTable();
                if (table.Length != 0)
                {
                    Log($"party table probe: {table}");
                }
            }
        }

        // Focus first: the others do nothing useful in a paused world.
        _focus?.OnFrame();

        // Before either transition branch below, because both of them `return`: a staged
        // cinematic that nothing came to collect has to expire and SAY so. An arm left
        // sitting would attach a movie — and the multi-second main-thread stall that comes
        // with it — to whatever transition happened next, with nothing in the log connecting
        // the two. See docs/research/cinematics.md §7.
        if (TravelController.StagedMovies.Expire(Environment.TickCount64, out string expired))
        {
            Log($"cinematic: {expired}");
        }

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
                PluginEvents.RaiseModuleTransitionStarted();

                // "Announced transitions" (docs/research/transition-smoothness.md): this is
                // the earliest point ANY instance can see a real, engine-native door
                // transition beginning — before the load starts, before the fade finishes.
                // Resolve where it is headed (or refuse it, if this is a client with the
                // door-refusal watch armed) while the door that fired is still the nearest
                // linked one, which is only true on this exact edge.
                if (_view is { } announceView)
                {
                    _doorTransitionAnnouncedThisEpoch = true;
                    TransitionAnnounceWatch.OnTransitionBegan(announceView);
                }
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

        // "Announced transitions", the scripted-funnel edge (docs/research/
        // transition-smoothness.md, 2026-08-27): the rising edge of
        // Odyssey.IsMoveToModulePending() catches every StartNewModule() call this frame's own
        // door check above did NOT (a galaxy-map departure, a Leviathan/Star Forge stage
        // change, a native trigger-driven transition, a debug verb) — see Odyssey.Travel.cs's
        // own comment for the six writers that converge on this one field.
        //
        // Placed here, AFTER the door block and BEFORE the _travel block below, on purpose:
        //   * A door crossing already returned early above while trans_pending was true, so by
        //     the time this line runs for that crossing, _doorTransitionAnnouncedThisEpoch is
        //     already true - skip, the door watch already published the same destination.
        //   * A co-op-commanded warp (TravelController.Execute, below) has not run yet THIS
        //     frame - Odyssey.TravelToModule has not been called, so IsMoveToModulePending()
        //     still reads false here regardless of ordering. On the FRAME Execute() actually
        //     runs, it is the last thing that method does before an unconditional `return`, so
        //     this line is never reached that frame either. Only on a LATER frame (once the
        //     field is observed true for the first time) does this run - by then
        //     TravelController.OwnWarpPending is already true (set the instant TravelToModule
        //     succeeded), so the skip below still holds even though PluginHost's own
        //     single-frame _travelAnnounced has already reset to false again.
        //
        // The same rising edge is also where the writers this plugin does not own (a
        // galaxy-map departure, a stage change, a trigger) get CSWParty+0x00 bounded by its
        // resolvable run before StartNewModule's DoPCAutosave walks the array with no null
        // check (PartyCountRestoreRule.SafeTravelCount has the crash) - best effort, since the
        // flag may already have been drained by the time this frame sees it; our own warps
        // are bounded inside Odyssey.TravelToModule before the raise. Idempotent, and NOT under
        // the announce config gate: a crash guard does not switch off with a feature flag.
        bool moveToModulePending = Odyssey.IsMoveToModulePending();
        if (moveToModulePending)
        {
            if (!_moveToModulePendingSeen)
            {
                _moveToModulePendingSeen = true;

                if (Odyssey.TryBoundClientPartyCountByResolvable(
                    out int countBefore, out int resolvable, out string entries))
                {
                    Log($"party count: bounded {countBefore} -> {resolvable} on the move-to-module "
                        + $"edge (entries {entries}; DoPCAutosave walks the array without a null check)");
                }

                if (_transitionAnnounceScriptedEnabled
                    && !_doorTransitionAnnouncedThisEpoch
                    && !(_travel?.OwnWarpPending ?? false)
                    && _view is { } scriptedAnnounceView)
                {
                    TransitionAnnounceWatch.OnScriptedTransitionBegan(scriptedAnnounceView);
                }
            }
        }
        else
        {
            _moveToModulePendingSeen = false;
        }

        if (_travel is { HasPendingRequest: true } travel)
        {
            // Announce THIS frame, before any flush or the eventual Odyssey.TravelToModule
            // call — the same "before the teardown, not after" timing _transitionPending uses
            // for a real door. Re-armed every frame this branch runs (the canonical reset
            // above already cleared it once this frame if we are only just now discovering the
            // request), so it stays true across however many flush-only frames this request
            // spans before Execute() actually fires the warp.
            _travelAnnounced = true;

            // Task 2b (docs/research/transition-smoothness.md): a best-effort fade to black,
            // the instant a travel request is FIRST seen — before the flush below even starts,
            // so it masks the whole wait rather than just the warp. Self-latched inside
            // MaybeStartFade (one call per serial); a no-op every other frame this branch runs.
            travel.MaybeStartFade();

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

        // Immediately after it, and before any world controller: destroy the bodies the
        // session spawned. Armed by the release above and re-run every frame until clean,
        // because the engine declines a destroy while the script VM is mid-call.
        DrainSessionSpawns();

        // Death first among the world controllers: a downed player must be published (and a
        // pending revive applied) before the phantom/combat passes read the same creature.
        _death?.OnFrame();

        // Read-only, no ordering dependency on anything else here: the local per-frame
        // minigame read (swoop/turret), logged on its own edges. The agent reads the same
        // pointer chain independently for the wire flag (see MinigameController's remarks).
        MinigameController.OnFrame();

        // Right after death, on the state it just published: a downed player's camera
        // follows a living peer instead of their own corpse.
        _spectate?.OnFrame();

        // Right after the down-spectate camera, and checking the same downed state itself so
        // the two never fight for SetCameraFollow on the same frame: a peer publishing a
        // solo-segment owner flag gets this instance parked on their fight instead.
        _soloSegmentSpectate?.OnFrame();

        // After the solo-segment spectator, deliberately: both park the local player's input
        // and both defer to a live conversation, so whichever asserted last would win a frame
        // they overlapped on. A story-forced duel is the rarer and more fragile of the two, so
        // it goes first and this one only ever adds to what it already did.
        _minigameLock?.OnFrame();

        _controller?.OnFrame();

        // Right after the phantom drive, on the block reads/slot state it just processed:
        // the "waiting" notice reads the same RemoteView per slot, purely to decide whether
        // to show a line — it never drives, spawns or destroys anything itself.
        _minigameNotice?.OnFrame();

        _items?.OnFrame();
        _combat?.OnFrame();
        _liveness?.OnFrame();

        // After the combat pass so a death applied this frame is respected (the presence
        // slot's dead flag and the kill both come from the same owner tick).
        _enemyPresence?.OnFrame();
        Odyssey.TickFireScriptWatch(Log);
        _partyPresence?.OnFrame();

        // After the companion mirror, so a release/reclaim decided THIS frame
        // (PartyPresenceController.DriveOne's WatchCompanion calls) is already reflected before
        // this publishes what the watch has measured or applies a peer's owed total. See
        // docs/research/companion-state-sync.md §13.
        _companionDamage?.OnFrame();

        // ROUND 71: re-arms real follow-AI on a genuinely-owned, idle companion — see
        // PartyFollowController's own class comment for why this backs off the instant
        // PartyPresenceController is actively mirroring instead (a client's companions).
        _partyFollow?.OnFrame();

        // After the companion mirror, not before: reconciling the roster can spawn a companion
        // body, and it should be driven by the mirror on the NEXT frame with the block already
        // read rather than half-way through this one.
        _partyRoster?.OnFrame();

        // After the roster reconciliation: this reads the SAME live party-table roster pass
        // just settled, so it never judges a drop against a mid-reconcile transient count.
        _soloSegment?.OnFrame();
        // After the roster poll, not before: suppression only decides whether to close the
        // client's own screen, and never blocks the reconcile pass above from converging.
        _partySelectSuppression?.OnFrame();

        // After death handling has published whether we are down, so damage that would
        // arrive on an already-downed player is skipped rather than fighting the revive.
        _incomingDamage?.OnFrame();
        _peerEffects?.OnFrame();

        // After the presence passes, so the watch tests the position the player is actually
        // standing at this frame, and so a fired encounter's spawns are already in the
        // owner's census by the next pump.
        _encounters?.OnFrame();
        _triggerRelay?.OnFrame();
        _movieRelay?.OnFrame();

        // A peer's interplanetary departure to replay here - same schedule the `galaxyaccept`
        // verb (CheatController) ran the identical accept dispatch from, three times live.
        _galaxyDeparture?.OnFrame();

        // After the combat pass, so the enemy the owner has just killed is already off the
        // dealt ledger before this publishes it — and so a peer's damage lands on a copy
        // whose health the mirror has already brought up to date this frame.
        _peerDamage?.OnFrame();

        _doors?.OnFrame();
        _placeables?.OnFrame();
        _loot?.OnFrame();
        _pause?.OnFrame();
        _pauseKey?.OnFrame();
        _journal?.OnFrame();
        _mapMarkers?.OnFrame();
        _mapReveal?.OnFrame();
        _portraits?.OnFrame();
        _barks?.OnFrame();
        _itemTemplateSpawns?.OnFrame();
        _interactionLocks?.OnFrame();

        // After the companion mirror and the portraits, because it decides what the player is
        // driving and where the camera points from the state those two have just settled.
        _controlReturn?.OnFrame();

        // Read-only: publishes the owner's current conversation line for the other player to
        // spectate. Runs in-world only (a conversation needs a live server + GUI).
        _dialogueSpectate?.OnFrame();

        // Test hooks last, so a driven conversation is opened against fully-updated state.
        _testDrive?.OnFrame();

        // And the staging cheats after them, for the same reason: a slay measures the
        // phantom table, the party roster and the mirror's pin, all of which this frame's
        // passes have just brought up to date.
        _cheats?.OnFrame();

        // Last, after any purse write this frame could have landed: an open store panel's
        // cached gold must match the party table before the next Buy click subtracts from
        // it (Odyssey.TryReconcileStoreGoldCache has the live failure).
        if (Odyssey.TryReconcileStoreGoldCache(out int cachedGold, out int liveGold))
        {
            Log($"store: gold cache {cachedGold} -> {liveGold} (the party table moved while the panel was open)");
        }
    }
}

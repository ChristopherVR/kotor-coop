using System.Diagnostics;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// Builds the wired (SessionManager, CoopCommandDispatcher, AgentDashboard) trio a live
/// `agent run` session drives — everything <c>RunCommand.RunAttachedAsync</c> used to build
/// inline, extracted so the composition itself is testable/readable on its own. The caller
/// (<see cref="RunCommand"/>) still owns creating the bridge, checking whether it resolved, and
/// awaiting the two tasks this returns — see <see cref="Compose"/>'s own doc comment for why
/// the split lands exactly there.
/// </summary>
internal static class AgentComposition
{
    /// <summary>Everything a live session needs once <see cref="Compose"/> returns:
    /// <see cref="DispatchTask"/> is already RUNNING (the dispatcher's tick loop was started as
    /// part of composition, matching the original inline order exactly), so the caller need
    /// only run the dashboard and then await both.</summary>
    public readonly record struct Result(
        SessionManager Manager,
        CoopCommandDispatcher Dispatcher,
        AgentDashboard Dashboard,
        Task DispatchTask);

    /// <summary>
    /// Wires up a session's manager, command dispatcher and dashboard from parsed options and
    /// an already-resolved (or main-menu-waiting) bridge. Everything here used to be inline in
    /// <c>RunAttachedAsync</c> between the bridge's resolve check and the final
    /// <c>await dashboard.RunAsync(...)</c> — moved verbatim, in the same order, including the
    /// pre-existing quirk that the dispatcher's tick loop is started (<c>dispatcher.RunAsync</c>)
    /// before the dashboard object even exists: harmless, because <c>RunAsync</c> returns
    /// immediately (its first await is a 150ms timer tick), so nothing observable depends on
    /// dashboard construction or <c>Log</c> wiring happening before or after that line.
    ///
    /// Returns null for the one failure case buried in here — <c>--join</c> reaching this point
    /// with no resolved host address — after printing the same message the original inline code
    /// did; the caller returns exit code 1 for that case, preserving the exact CLI behaviour.
    /// </summary>
    public static Result? Compose(AgentOptions options, IGameBridge bridge, ProcessMemory memory, CancellationTokenSource cts)
    {
        // Everything a session needs is constructed up front, whether or not a connection
        // has been chosen yet: from the moment the agent attaches, the dispatcher is THE
        // single command path. CLI --host/--join is just an initial dispatch through it,
        // and every in-game surface (~ console, Co-op button, browser panel) feeds the
        // same per-tick loop — so say/servers/connect/host all work at the main menu.
        CoopConfig? config = TryLoadCoopConfig(memory.ProcessId);

        // The PvP arena module (docs/research/pvp-arenas.md): generated from the player's own
        // game files. The installer normally put it there; an install that came any other way
        // (an unpacked zip, the Linux script, a Proton prefix) gets it written now, but the game
        // read its modules folder before this agent started, so it only counts as ready the NEXT
        // time the game starts.
        bool arenaReady = EnsureArenaModule(TryResolveInstallDirectory(memory.ProcessId));

        // The arena boss needs a row in the game's appearance table and a blueprint naming it,
        // both made from this install's own table. Same rule as the arena: written now when
        // missing, counted as ready from the next start (the game read its tables at launch).
        bool bossReady = EnsureBoss(TryResolveInstallDirectory(memory.ProcessId), out int bossRow) && arenaReady;

        // The stand-in's sideways copies of the player's dialogue stunt tracks, so a partner is
        // drawn beside the player in a stunt-driven scene. Before the digest, which counts
        // override: both players make the same copies from the same game files.
        EnsureStuntStandIns(TryResolveInstallDirectory(memory.ProcessId));

        // Fingerprint this install so a mod mismatch with the peer is caught at join time.
        // Best-effort: a digest that could not be computed compares as "unchecked" and
        // never blocks a session.
        ContentDigest digest = ComputeInstallDigest(memory.ProcessId, bridge);

        // This install's stable identity, so a client that relaunches (or issues the console's
        // `reconnect`, which builds a fresh session object) reclaims its existing peer slot
        // instead of joining as a stranger while the old slot ages out. Keyed on the game
        // directory, which is what already separates two instances on one machine.
        string? installDirectory = TryResolveInstallDirectory(memory.ProcessId);
        uint peerIdentity = PeerIdentity.ForDirectory(installDirectory);

        // The player name: coop.ini's wins for menu-driven connects, the command line's for
        // scripted ones. A password typed with an in-game command (connect <ip> <password>,
        // host <password>) beats everything; then --password; then coop.ini's — in EVERY
        // mode, not just menu mode: the file documents one setting, and honouring it in only
        // one of the two ways a session starts is the kind of difference nobody debugs —
        // they conclude the password does not work. (Scripted runs that want a specific
        // password pass --password, which still wins.)
        string effectiveName = options.MenuConnect && config?.Name is { Length: > 0 } iniName
            ? iniName
            : options.Name;
        string fallbackPassword = options.Password.Length > 0
            ? options.Password
            : config?.Password ?? string.Empty;

        if (options.Password.Length == 0 && config is { Password.Length: > 0 })
        {
            Console.WriteLine("password: set from coop.ini");
        }

        // coop.ini can override the two settings that decide how far behind a peer looks —
        // the send rate and the interpolation buffer — plus pause mirroring, so they can be
        // tuned by whoever is actually watching the screen rather than only at build time.
        int tickRate = config is { TickRate: > 0 } ? config.TickRate : options.TickRate;
        int interpolationMs = config is { InterpolationDelayMs: >= 0 }
            ? config.InterpolationDelayMs
            : ProtocolConstants.DefaultInterpolationDelayMs;

        if (tickRate != options.TickRate || interpolationMs != ProtocolConstants.DefaultInterpolationDelayMs)
        {
            Console.WriteLine($"tuning: {tickRate} Hz, {interpolationMs} ms interpolation (from coop.ini)");
        }

        // coop.ini's own `port` wins when the CLI did not explicitly say --port: without this,
        // an auto-started (--menu) agent hosted or dialled on the CLI's silent 27015 default no
        // matter what coop.ini said, so a session tuned via coop.ini's `port = 27140` (the
        // shipped example) never actually took effect. An explicit --port always wins, matching
        // the same "CLI beats coop.ini" rule the password already follows.
        int effectivePort = options.PortExplicit ? options.Port : config?.Port ?? options.Port;

        if (effectivePort != options.Port)
        {
            Console.WriteLine($"port: {effectivePort} (from coop.ini)");
        }

        AgentSettings agentSettings = new()
        {
            PollRate = tickRate,
            FilterByModule = options.FilterByModule,
            FollowHostModule = options.FollowHostModule,
            SyncParty = options.SyncParty,
            SyncProgression = options.SyncProgression,
            SyncPause = config?.PauseSync ?? true,
            PerfTrace = config?.PerfTrace ?? false,
            HidePeersInMinigame = config?.HidePeersInMinigame ?? true,
            LockPeersDuringMinigame = config?.LockPeersDuringMinigame ?? true,
            MinigameLockTimeoutMs = (config?.MinigameLockTimeoutSeconds ?? 600) * 1000,
            ExtraMinigameModules = config?.ExtraMinigameModules ?? [],
            DialogueItemSync = config?.DialogueItemSync ?? true,
            ScriptItemSync = config?.ScriptItemSync ?? true,
            InventoryPersonal = config?.InventoryPersonal ?? true,
            CreditsPersonal = config?.CreditsPersonal ?? true,
            CorpseLootPersonal = config?.CorpseLootPersonal ?? false,
            Pvp = config?.Pvp ?? false,
            PvpArena = config?.PvpArena ?? false,
            // OFF with no coop.ini too: NEEDS-LIVE, matching the plugin's own fallback.
            ClassGrantSync = config?.ClassGrantSync ?? true,
            // OFF with no coop.ini too: NEEDS-LIVE, purely local, no plugin key at all.
            FirstSaberColorSync = config?.FirstSaberColorSync ?? true,
            // OFF with no coop.ini too: NEEDS-LIVE, matching the plugin's own fallback.
            EnemyEffectSync = config?.EnemyEffectSync ?? true,
            // OFF with no coop.ini too: NEEDS-LIVE, matching the plugin's own fallback.
            PlacedMineSync = config?.PlacedMineSync ?? false,
            DialogueFastJoin = config?.DialogueFastJoin ?? true,
            PartyFastPump = config?.PartyFastPump ?? true,
            ContainerFastPump = config?.ContainerFastPump ?? false,
            MineSync = config?.MineSync ?? true,
            LocalVarSync = config?.LocalVarSync ?? true,
            TransitionAnnounceEnabled = config?.TransitionAnnounceEnabled ?? true,
            FollowStunt = config?.FollowStunt ?? true,
            ClientDoorRefusalEnabled = config?.ClientDoorRefusalEnabled ?? true,
            MovieRelayEnabled = config?.MovieRelayEnabled ?? true,
            SyncCompanionXp = config?.CompanionXpSync ?? true,
            SyncCompanionGear = config?.CompanionGearSync ?? true,
            SyncCompanionDamage = config?.CompanionDamageSync ?? true,
            // ON by default at mode 2 (2026-08-29): with no coop.ini at all, the shipped
            // default is what a fresh install would have got from profiles/coop.ini.example.
            PartyPoolSyncEnabled = config?.PartyPoolSyncEnabled ?? true,
            PartyPoolSyncMode = config?.PartyPoolSyncMode ?? 2,
            PartyPoolTrace = config?.PartyPoolTrace ?? false,
            SoloSegmentSpectateEnabled = config?.SoloSegmentSpectateEnabled ?? false,
            StoreSyncEnabled = config?.StoreSyncEnabled ?? true,
            StoreSyncMode = config?.StoreSyncMode ?? 2,
            MenuGuardSyncEnabled = config?.MenuGuardSyncEnabled ?? true,
            MenuGuardCapMs = config?.MenuGuardCapMs ?? MenuGuardRule.DefaultCapMs,
            MenuGuardCooldownMs = config?.MenuGuardCooldownMs ?? MenuGuardRule.DefaultCooldownMs,

            // No coop.ini => the most conservative choice, matching the pattern above: today's
            // exact host-leads/client-follows behaviour, not the new lead-swap default.
            FollowLeadMode = config?.FollowLead ?? FollowLeadMode.Host,

            // Player-count scaling (docs/research/player-count-scaling.md). The fallbacks are
            // the same numbers CoopConfig's record defaults and parse fallbacks carry:
            // health, damage, xp and store stock on the auto curve.
            Scaling = new PlayerCountScalingSettings(
                config?.EnemyHealth ?? PlayerCountScalingRule.Auto,
                config?.EnemyDamage ?? PlayerCountScalingRule.Auto,
                config?.XpRate ?? PlayerCountScalingRule.Auto,
                config?.StoreStock ?? PlayerCountScalingRule.Auto,
                config?.StorePrices ?? PlayerCountScalingRule.Vanilla),
            GameDirectory = installDirectory,

            // CoopAgent is deliberately game-agnostic and has no reader of its own for a
            // module's display name; this is the one place that both has the game directory
            // and already imports KotorCoop.Memory.GameFiles. A failed lookup falls back to
            // the resref, exactly like every other consumer of ModuleNames.TryRead.
            ModuleDisplayName = installDirectory is { Length: > 0 } dir
                ? module => ModuleNames.TryRead(dir, module, out string name) ? name : module
                : null,
        };

        // Surface why a cached player pointer stopped validating. Re-resolving costs a full
        // memory scan, so if this fires repeatedly the validation is wrong, not the pointer.
        if (bridge is KotorGameBridge resolveDiag)
        {
            resolveDiag.ResolveDiagnostic = message =>
            {
                Console.WriteLine($"[resolve] {message}");

                // Known to be needed for a live stall investigation: options.LogFile is known
                // before the dashboard exists, and this diagnostic needs to survive in the log
                // file, not just the console window demo.ps1 never captures.
                if (!string.IsNullOrEmpty(options.LogFile))
                {
                    try
                    {
                        File.AppendAllText(options.LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] [resolve] {message}{Environment.NewLine}");
                    }
                    catch
                    {
                    }
                }
            };

            // And why the enemy census is smaller than the scan. A creature-shaped object the
            // engine's own array does not hold is an id nobody can key work by — the peer
            // damage forwarded against one is owed for ever — so the drop is stated, never
            // silent.
            // Log only (like [resolve] above): on the console it repeated into the player's
            // agent-window transcript every census pass (live 2026-09-25).
            resolveDiag.CensusDiagnostic = message =>
            {
                if (options.LogFile is null)
                {
                    return;
                }

                try
                {
                    File.AppendAllText(options.LogFile, $"[{DateTime.Now:HH:mm:ss.fff}] [census] {message}{Environment.NewLine}");
                }
                catch
                {
                }
            };
        }

        // Behaviour-flag negotiation (docs/roadmap-seamless.md Tier 1 item 7): fingerprint
        // this install's coop.ini/CLI behaviour the same way ComputeInstallDigest already
        // fingerprints its content, so a divergent peer gets a warning instead of two engines
        // silently running different rules. Sourced from `agentSettings` wherever a key
        // already resolved there (the CLI-vs-coop.ini precedence that record embodies), and
        // from `config` directly for the plugin-only keys CoopConfig parses purely for this
        // (see CoopConfig.cs's "Digest-only keys" section) plus the two load-rejoin gates,
        // which live outside AgentSettings entirely.
        BehaviourDigest behaviourDigest = BehaviourDigest.Compute(
            localCombat: config?.LocalCombat ?? true,
            companionLocalCombat: config?.CompanionLocalCombat ?? false,
            partyFastPump: agentSettings.PartyFastPump,
            partySync: agentSettings.SyncParty,
            clientDoorRefusal: agentSettings.ClientDoorRefusalEnabled,
            transitionAnnounce: agentSettings.TransitionAnnounceEnabled,
            clientLoadRejoin: config?.ClientLoadRejoin ?? true,
            hostLoadReadopt: config?.HostLoadReadopt ?? true,
            effectSync: config?.EffectSync ?? true,
            triggerRelay: config?.TriggerRelay ?? true,
            clientEncounters: config?.ClientEncounters ?? true,
            dialogueItemSync: agentSettings.DialogueItemSync,
            dialogueRecruitSync: config?.DialogueRecruitSync ?? true,
            scriptItemSync: agentSettings.ScriptItemSync,
            movieRelay: agentSettings.MovieRelayEnabled,
            livenessSync: config?.LivenessSync ?? true,
            mapRevealSync: config?.MapRevealSync ?? true,
            requireIdenticalSave: options.RequireIdenticalSave,
            interpolationMs: interpolationMs,
            followLead: agentSettings.FollowLeadMode == FollowLeadMode.Host ? "host" : "any",
            enemyHealth: agentSettings.Scaling.EnemyHealth,
            enemyDamage: agentSettings.Scaling.EnemyDamage,
            xpRate: agentSettings.Scaling.XpRate,
            storeStock: agentSettings.Scaling.StoreStock,
            storePrices: agentSettings.Scaling.StorePrices,
            localVarSync: agentSettings.LocalVarSync,
            inventoryPersonal: agentSettings.InventoryPersonal,
            creditsPersonal: agentSettings.CreditsPersonal,
            corpseLootPersonal: agentSettings.CorpseLootPersonal,
            pvp: agentSettings.Pvp,
            pvpArena: agentSettings.PvpArena);

        SessionManager manager = new(bridge, agentSettings);

        SessionSettings SettingsFor(string sessionPassword) => new()
        {
            PlayerName = effectiveName,
            ServerName = config?.ServerName ?? string.Empty,
            Port = effectivePort,
            TickRate = tickRate,
            InterpolationDelayMs = interpolationMs,
            DejitterEnabled = config?.Dejitter ?? false,
            Digest = digest,
            Behaviour = behaviourDigest,
            RequireIdenticalSave = options.RequireIdenticalSave,
            Password = sessionPassword.Length > 0 ? sessionPassword : fallbackPassword,
            PeerIdentity = peerIdentity,
            PvpArenaReady = arenaReady,
            BossReady = bossReady,
            BossAppearanceRow = bossRow,
            ItemContinuationPath = installDirectory is null ? null : Path.Combine(installDirectory, "coop.items.json"),
            ConnectTimeoutMs = config is { ConnectTimeoutMs: > 0 }
                ? config.ConnectTimeoutMs
                : ProtocolConstants.DefaultConnectTimeoutMs,
            HostLossReleaseEnabled = config?.HostLossRelease ?? true,
            HostLossTimeoutMs = config is { HostLossTimeoutMs: > 0 }
                ? config.HostLossTimeoutMs
                : ProtocolConstants.PeerTimeoutMs,
            HostAwayTimeoutMs = config?.HostAwayTimeoutMs ?? HostAwayRule.DefaultTimeoutMs,

            // Roadmap item 24: hosting over the internet without a relay. Both ignored by a
            // client (SessionSettings' own doc comments); a host with neither configured keeps
            // exactly the pre-existing LAN-only behaviour.
            StunServers = PublicAddressDiscovery.ParseServers(
                config?.StunServers ?? PublicAddressDiscovery.DefaultServers),
            UpnpEnabled = config?.Upnp ?? true,

            // DEBUG/TEST-ONLY (coop.ini debug_hello_version) — see
            // SessionSettings.DebugHelloVersionOverride's own doc comment.
            DebugHelloVersionOverride = config?.DebugHelloVersionOverride ?? 0,
        };

        // The dispatcher owns every command surface and the one LAN listener. It re-reads
        // coop.ini per bare button press (so an edit at the menu is honoured without a
        // restart), and a session started from the menu auto-loads the player's save the
        // moment it is up — hosting immediately, joining once the handshake lands, and
        // only if the game is verifiably still at the main menu (never mid-world, never
        // again on a reconnect, never after a refused join).
        string gameDirectory = installDirectory ?? string.Empty;

        CoopCommandDispatcher dispatcher = new(
            manager,
            bridge,
            effectivePort,
            SettingsFor,
            loadConfig: () => TryLoadCoopConfig(memory.ProcessId),
            menuAutoLoad: () => _ = Task.Run(() => EnterTheWorld(), cts.Token),
            gameDirectory: gameDirectory,
            steam: ComposeSteam(config, gameDirectory, options));

        // How a session that started from the game's own menu gets into a world. A host loads
        // its own save; a joiner goes through JoinFlow, which is coop.ini join_mode's
        // production caller — re-read at the moment of the press (not captured at startup) for
        // the same reason every other menu-time setting is: an edit made at the menu must be
        // honoured without restarting the agent.
        //
        // The default (join_mode = auto) sends a first-time joiner through the game's own
        // character creation and carries the mod-owned co-op character on every join after;
        // join_mode = save is the old "load my own save" path, which JoinFlow hands back here.
        //
        // ⚠ The flow's lines go through manager.Log — the dashboard's Note once it exists, so
        // they land under the status line AND in --log. Console.WriteLine here is the trap the
        // manager.Log assignment below describes: the 2026-09-03 join_mode = auto live pass
        // had to be read from the PLUGIN's log because "join: ..." went only to a window.
        void SayJoin(string line) => (manager.Log ?? Console.WriteLine)(line);

        async Task EnterTheWorld()
        {
            if (manager.Current is CoopHost)
            {
                LoadIntoTheWorld(memory);
                return;
            }

            CoopConfig? current = TryLoadCoopConfig(memory.ProcessId);

            await JoinFlow.RunAsync(
                manager,
                bridge,
                TryResolveInstallDirectory(memory.ProcessId) ?? string.Empty,
                current?.JoinMode ?? JoinMode.Auto,
                current?.AutoAdoptWorld ?? true,
                loadOwnSave: () => LoadIntoTheWorld(memory),
                SayJoin,
                cts.Token,
                current?.WorldTransferUdpFallback ?? true).ConfigureAwait(false);
        }

        // Roadmap item 14 phase 1's "OR on clean disconnect" trigger: the dispatcher's own
        // save-identity watch gets one more chance to capture a client's progressed
        // character the moment ANY teardown starts, while the session is still readable.
        manager.OnDisconnecting = dispatcher.CaptureCharacterOnDisconnect;

        // Push the coop.ini chat-overlay settings to the plugin (best-effort; defaults when
        // there is no coop.ini). Independent of connection mode — the overlay is useful the
        // moment a session is up.
        ApplyChatOverlayConfig(bridge, memory.ProcessId);

        // pazaak_detect (docs/research/minigames.md, item 8 task 1): NEEDS-LIVE, off unless
        // coop.ini opts in. This is what actually makes KotorGameBridge.IsInMinigame — and
        // therefore the PlayerState.FlagInMinigame wire bit peers park on — true for a
        // Pazaak hand; setting the plugin's own pazaak_detect key only affects that
        // process's local log/HUD state, not this instance's outgoing flag.
        if (bridge is KotorGameBridge pazaakBridge)
        {
            pazaakBridge.PazaakDetectEnabled = config?.PazaakDetect ?? true;
        }

        // One-time startup for the in-game surfaces. Only the KOTOR bridge exposes the
        // plugin block the Co-op button lives in; the enable write also snapshots the
        // request serial so a stale press never fires on the first poll.
        if (bridge is KotorGameBridge kotor && kotor.EnableConnectMenu(config?.MenuButton ?? true))
        {
            if (options.MenuConnect)
            {
                // The button is default-ON (plugin-side too); menu_button = 0 is the opt-out.
                Console.WriteLine(config?.MenuButton == false
                    ? "waiting for a connect request (menu_button=0 hides the button; the ~ console still works)..."
                    : "press the Co-op button in the game's main menu to connect (or use the ~ console: servers / connect <ip|#n> / host)...");
            }
        }
        else if (options.MenuConnect)
        {
            // No plugin block: nothing in-game can ever signal a connect, so --menu would
            // wait forever. Behave as a bare button press instead — coop.ini decides,
            // exactly as it always has for this case.
            Console.WriteLine("in-game Co-op button unavailable (no plugin); connecting from coop.ini.");
            dispatcher.OnConnectRequest(string.Empty);
        }

        // demo.ps1 / session.ps1 compatibility: --host and --join start their session
        // immediately, through exactly the path an in-game command would take. --menu makes
        // no initial dispatch — the dispatcher just runs and the player acts in-game.
        if (options.Mode == CoopMode.Host)
        {
            dispatcher.Dispatch(new CoopCommand(CoopCommandKind.Host));
        }
        else if (options.Mode == CoopMode.Join)
        {
            if (options.HostEndPoint is null)
            {
                Console.Error.WriteLine("join requested but no host address is set.");
                return null;
            }

            dispatcher.Dispatch(new CoopCommand(
                CoopCommandKind.Connect, Address: options.HostEndPoint.ToString()));

            // A host still loading answers nothing; keep dialing until it does (JoinRedial).
            string joinAddress = options.HostEndPoint.ToString();
            _ = Task.Run(
                () => JoinRedial.RunAsync(
                    manager,
                    () => dispatcher.Dispatch(new CoopCommand(CoopCommandKind.Connect, Address: joinAddress)),
                    SayJoin,
                    cts.Token),
                cts.Token);

            // ⚠ The CLI join does NOT arm the dispatcher's menu auto-load — that is a
            // menu-BUTTON affordance, and `--join` has always left "get into a world" to the
            // caller (`agent load`, demo.ps1's own -Save). The character-creation and co-op-
            // character adoptions are the cases where there is nothing for the caller to load,
            // so for THOSE the flow is started here explicitly, waiting for the handshake first
            // through the same MenuAutoLoad gate the button path uses (a REFUSED join must not
            // send the player through character creation for a session they were never
            // admitted to) and only if the game is verifiably still at the main menu by then —
            // a caller that already loaded a save (demo3.ps1, session.ps1) keeps its world.
            string joinDirectory = installDirectory ?? string.Empty;
            JoinMode joinMode = config?.JoinMode ?? JoinMode.Auto;
            bool autoAdopt = config?.AutoAdoptWorld ?? true;

            if (JoinFlow.Decide(joinDirectory, joinMode, autoAdopt, out _)
                is JoinAction.CreateCharacterThenAdopt or JoinAction.AdoptWithCoopCharacter)
            {
                _ = Task.Run(
                    () => MenuAutoLoad.RunAsync(
                        manager,
                        host: false,
                        load: () =>
                        {
                            if (bridge.TryReadLocalState(out _))
                            {
                                SayJoin("join: already in a world; leaving it as it is.");
                                return;
                            }

                            _ = JoinFlow.RunAsync(
                                manager, bridge, joinDirectory, joinMode, autoAdopt,
                                loadOwnSave: null, SayJoin, cts.Token,
                                config?.WorldTransferUdpFallback ?? true);
                        },
                        cts.Token,
                        SayJoin),
                    cts.Token);
            }
        }

        Task dispatchTask = dispatcher.RunAsync(cts.Token);

        // The dispatcher already exists by construction order (built above), so the
        // dashboard's own command line can be wired straight to it: Enter on that line runs
        // <see cref="CoopCommandDispatcher.RunTypedLine"/>, the exact same entry point the
        // shared block's console channel uses for a line typed in-game.
        // ⚠ requestShutdown is what a Ctrl+C typed INSIDE the TUI uses to tear down the rest
        // of the agent (dispatcher tick loop, session, sockets). Terminal.Gui puts the
        // console in raw input mode, so the OS-level Console.CancelKeyPress RunCommand
        // registers on `cts` never fires once the TUI is up — this closes that gap by giving
        // the dashboard its own way to reach the SAME cts.
        AgentDashboard dashboard = new(manager, bridge, options.DropFile, options.LogFile, dispatcher, cts.Cancel);

        // ⚠ The agent's event voice. SessionManager.Log and CoopAgent.Log existed for the
        // life of the project with NOTHING assigned to them — every "X revived you",
        // "disabled N local encounters" and pump-failure line was discarded, which made a
        // misfiring rescue undiagnosable from the logs. The manager forwards its agents'
        // lines; the dashboard writes them under the status line and into --log.
        // The perf_trace line (once a second) is log-file only: in the transcript it buried
        // every real event and made each redraw heavier (live 2026-09-26).
        manager.Log = line =>
        {
            if (line.StartsWith("perf: ", StringComparison.Ordinal))
            {
                dashboard.LogOnly(line);
            }
            else
            {
                dashboard.Note(line);
            }
        };

        // ⚠ And so does every CONSOLE REPLY, for exactly the same reason. The dispatcher's
        // answers — `items` rows, a drop's refusal, a cheat's verdict — went only to
        // Console.WriteLine, which in a demo.ps1 run is a visible window with no reader and
        // no file behind it. That made every multi-line answer unusable to an unattended
        // check: the in-game reply field is 96 bytes and holds one line, so the log was the
        // only place the rest could have gone, and nothing was sending it there.
        dispatcher.Log = dashboard.Note;

        return new Result(manager, dispatcher, dashboard, dispatchTask);
    }

    /// <summary>
    /// The directory the attached game runs from, or null when it cannot be resolved. The
    /// same lookup <see cref="TryLoadCoopConfig"/> does, named separately because it is also
    /// where per-install state that is not configuration lives (see <c>PeerIdentity</c>).
    /// </summary>
    /// <summary>Whether the arena boss was already installed (his appearance row and blueprint),
    /// installing him when he was not. Never throws.</summary>
    private static bool EnsureBoss(string? installDirectory, out int appearanceRow)
    {
        appearanceRow = 0;
        if (installDirectory is null)
        {
            return false;
        }

        ChungusInstallResult result = ChungusInstaller.Install(installDirectory);
        appearanceRow = Math.Max(result.AppearanceRow, 0);
        switch (result.Outcome)
        {
            case ChungusInstallOutcome.UpToDate:
                return true;
            case ChungusInstallOutcome.Installed:
                Console.WriteLine($"chungus: installed ({result.Message}) - restart the game once before the boss fight can work");
                break;
            default:
                Console.WriteLine($"chungus: not installed ({result.Message})");
                break;
        }

        return false;
    }

    /// <summary>Writes the stand-in's copies of every player stunt track the install's dialogues
    /// use (StuntStandInModels), when missing or stale. Never throws; a copy written now is seen
    /// from the next launch, since the game reads its override folder at start.</summary>
    private static void EnsureStuntStandIns(string? installDirectory)
    {
        if (installDirectory is null)
        {
            return;
        }

        try
        {
            long started = Environment.TickCount64;
            int written = StuntStandInModels.Install(installDirectory, out int stunts);
            long tookMs = Environment.TickCount64 - started;
            Console.WriteLine(written > 0
                ? $"scene stunts: wrote {written} stand-in track file(s) for {stunts} player stunt(s) in {tookMs} ms - seen from the next launch"
                : $"scene stunts: {stunts} player stunt(s), stand-in tracks current ({tookMs} ms)");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"scene stunts: stand-in tracks not written ({ex.Message})");
        }
    }

    /// <summary>Whether the arena module was already in the install's modules folder, writing it
    /// when it was not (and the game's own files can make one). Never throws: an install
    /// that cannot have an arena simply has no arena fights.</summary>
    private static bool EnsureArenaModule(string? installDirectory)
    {
        if (installDirectory is null)
        {
            return false;
        }

        try
        {
            // Regenerated on every start, not only when missing: a mod installed afterwards that
            // replaces the Taris cantina (modules\tar_m02ae.mod) makes an old arena stale, and
            // Install writes only when the bytes differ.
            ArenaInstallResult result = ArenaModuleInstaller.Install(installDirectory);
            switch (result.Outcome)
            {
                case ArenaInstallOutcome.UpToDate:
                    return true;
                case ArenaInstallOutcome.Installed:
                case ArenaInstallOutcome.Refreshed:
                    Console.WriteLine("arena: wrote modules\\k_coop_arena.mod from this game's own cantina - "
                        + "restart the game once before arena fights can work");
                    break;
                default:
                    Console.WriteLine($"arena: not installed ({result.Message})");
                    break;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.WriteLine($"arena: could not write the arena module ({ex.Message})");
        }

        return false;
    }

    private static string? TryResolveInstallDirectory(int processId)
    {
        try
        {
            using Process game = Process.GetProcessById(processId);
            return Path.GetDirectoryName(game.MainModule?.FileName);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Builds the Steam friends-list service, or returns null when there is nothing for it to
    /// do. Null and "constructed but never activates" are both silent no-ops downstream; the
    /// difference is only that null skips writing <c>steam_appid.txt</c> into the game
    /// directory, which is worth avoiding for a player who has turned the feature off.
    ///
    /// ⚠ `?? true` — <c>steam_invites</c> is ON by default, and an absent coop.ini must mean
    /// the default, not OFF. (docs/CONFIG.md footnote ³ is about exactly this class of
    /// mistake: a key documented ON whose composition path silently falls back to false.)
    /// ON is safe because every layer below is inert without a player-supplied
    /// <c>steam_api64.dll</c> — see docs/research/steam-support.md §12.
    /// </summary>
    private static KotorCoop.Steam.SteamInviteService? ComposeSteam(
        CoopConfig? config, string installDirectory, AgentOptions options)
    {
        if (!(config?.SteamInvites ?? true))
        {
            return null;
        }

        KotorCoop.Steam.SteamInviteService steam = new(enabled: true, Console.WriteLine);

        // Only stamp steam_appid.txt into the game folder when there is a redistributable that
        // could read it. `uninstall` removes KotorCoop.* and nothing else, so a file written
        // for a feature the player never enabled would simply stay there forever.
        if (installDirectory.Length > 0
            && KotorCoop.Steam.SteamInviteService.IsRedistributablePresent(installDirectory))
        {
            KotorCoop.Steam.SteamInviteService.TryEnsureAppIdFile(
                installDirectory, AgentOptions.SteamAppId, Console.WriteLine);
        }

        // A JOINER must be listening for callbacks before an invite can arrive, so bring Steam
        // up eagerly for anything that is not already a host. A host activates lazily instead,
        // on its first Publish — by which point it has an address worth publishing.
        //
        // A run started BY a Steam invite (--steam-join) says so, because "how did I get into
        // this session" is the first question a confusing join raises.
        if (options.SteamConnect is { Length: > 0 } connect)
        {
            Console.WriteLine($"steam: joining from an invite ({connect})");
        }

        if (options.Mode != CoopMode.Host)
        {
            steam.TryActivate();
        }

        return steam;
    }

    /// <summary>
    /// Reads coop.ini beside the attached game's own executable, or null when it is absent
    /// or unreadable — the command line and the built-in defaults stand in that case.
    /// </summary>
    private static CoopConfig? TryLoadCoopConfig(int processId)
    {
        try
        {
            using Process game = Process.GetProcessById(processId);
            string? gameDir = Path.GetDirectoryName(game.MainModule?.FileName);
            return gameDir is null ? null : CoopConfig.TryLoad(CoopConfig.PathFor(gameDir));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Fingerprints the attached game's install for the compatibility handshake. Derives
    /// the game directory from the process's own executable path, so it is correct whether
    /// the game is GOG, Steam, or a second co-op copy. Returns <see
    /// cref="ContentDigest.Unknown"/> if anything is unreadable — the session then treats
    /// compatibility as unchecked rather than failing.
    ///
    /// The executable half comes from the PLUGIN, which hashes the decrypted, not-yet-hooked
    /// image it is loaded into. That is what lets a Steam player and a DRM-free player share
    /// a session: their swkotor.exe FILES differ (SteamStub encrypts .text at rest), the code
    /// they are both about to hook does not. docs/research/steam-support.md §8.
    /// </summary>
    private static ContentDigest ComputeInstallDigest(int processId, IGameBridge bridge)
    {
        try
        {
            using Process game = Process.GetProcessById(processId);
            string? exePath = game.MainModule?.FileName;
            string? gameDir = exePath is null ? null : Path.GetDirectoryName(exePath);

            if (exePath is null || gameDir is null)
            {
                return ContentDigest.Unknown;
            }

            uint? liveImage = bridge is KotorGameBridge kotor && kotor.TryReadExeImageDigest(out uint image)
                ? image
                : null;

            ContentDigest digest = InstallDigest.Compute(gameDir, exePath, liveImage);

            // The file hash is no longer compared, but it is still the number that tells a
            // wrapped Steam install from an unwrapped one in a log, so it is printed as a
            // label beside the one that IS compared.
            string fileLabel = InstallDigest.TryHashExecutableFile(exePath, out uint fileHash)
                ? $"exe file {fileHash:X8} ({SteamBuild.InspectFile(exePath, out _)})"
                : "exe file unreadable";

            Console.WriteLine($"content digest: {DescribeDigest(digest)} [{fileLabel}]");

            if (liveImage is null)
            {
                Console.WriteLine(
                    "the plugin published no executable-image digest; executable identity is UNCHECKED "
                    + "for this session (an older plugin, or no plugin at all)");
            }

            return digest;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"content digest unavailable ({ex.Message}); compatibility unchecked");
            return ContentDigest.Unknown;
        }
    }

    private static string DescribeDigest(ContentDigest d)
    {
        if (d.IsEmpty)
        {
            return "none (compatibility unchecked)";
        }

        List<string> parts = [];
        if (d.Knows(ContentDigest.Field.Exe))
        {
            parts.Add($"exe image {d.ExeDigest:X8}");
        }

        if (d.Knows(ContentDigest.Field.Appearance))
        {
            parts.Add($"{d.AppearanceRows} appearance rows");
        }

        if (d.Knows(ContentDigest.Field.Override))
        {
            parts.Add($"{d.OverrideCount} override file(s)");
        }

        if (d.Knows(ContentDigest.Field.Tlk))
        {
            parts.Add($"{d.TlkEntryCount} tlk entries");
        }

        if (d.Knows(ContentDigest.Field.Gameplay))
        {
            parts.Add($"gameplay {d.GameplayDigest:X8}");
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Pushes the always-on chat-overlay settings from coop.ini into the plugin block. Uses
    /// defaults (overlay on, bottom-left) when coop.ini is absent. Only the KOTOR bridge
    /// exposes the block, so it is a no-op otherwise.
    /// </summary>
    private static void ApplyChatOverlayConfig(IGameBridge bridge, int processId)
    {
        if (bridge is not KotorGameBridge kotor)
        {
            return;
        }

        CoopConfig config = new();
        try
        {
            using Process game = Process.GetProcessById(processId);
            string? gameDir = Path.GetDirectoryName(game.MainModule?.FileName);
            if (gameDir is not null)
            {
                config = CoopConfig.TryLoad(CoopConfig.PathFor(gameDir)) ?? config;
            }
        }
        catch
        {
            // No coop.ini or no access: the defaults stand.
        }

        kotor.SetChatOverlayConfig(
            config.ChatOverlay,
            (uint)config.ChatOverlayOpacity,
            config.ChatOverlayAnchorCode,
            (uint)config.ChatOverlayLines,
            (uint)config.ChatOverlayFadeMs);
    }

    /// <summary>
    /// After a server is picked in the in-game browser: load the player's save, so "click a
    /// server, arrive in the world" is one action.
    ///
    /// Uses the loader's default route, which is now the plugin's in-process replay: no
    /// window focus, no cursor, no clicks. That matters here more than anywhere else,
    /// because the player has just clicked a button in the game's own menu and the window
    /// may well lose focus to the agent's own console a moment later.
    ///
    /// Best-effort: on any refusal the player just loads a save by hand, exactly as before
    /// this existed, and the session is already connected either way.
    /// </summary>
    private static void LoadIntoTheWorld(ProcessMemory memory)
    {
        try
        {
            using Process game = Process.GetProcessById(memory.ProcessId);

            if (SaveLoader.TryLoadSave(game, memory, 0, TimeSpan.FromSeconds(180), out string diagnostic))
            {
                Console.WriteLine($"auto-load: {diagnostic}");
            }
            else
            {
                Console.WriteLine($"auto-load skipped ({diagnostic}); load a save yourself and the session continues.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"auto-load skipped: {ex.Message}");
        }
    }
}

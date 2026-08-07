using System.Diagnostics;
using System.Net;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        AgentOptions options = AgentOptions.Parse(args);

        try
        {
            return options.Command switch
            {
                AgentCommand.Launch => GameLauncher.Launch(options),
                AgentCommand.Load => LoadSave(options),
                AgentCommand.Profiles => ListProfiles(),
                AgentCommand.InstallDataMods => InstallDataMods(options),
                AgentCommand.Info => Info(options),
                AgentCommand.Scan => Scan(options),
                AgentCommand.Track => Track(options),
                AgentCommand.AutoTrack => AutoTrack(options),
                AgentCommand.Run => await RunAsync(options).ConfigureAwait(false),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine(AgentOptions.HelpText);
        return 0;
    }

    private static int ListProfiles()
    {
        IReadOnlyList<string> profiles = ProfileLocator.ListAvailable();

        if (profiles.Count == 0)
        {
            Console.WriteLine("No profiles found.");
            return 1;
        }

        Console.WriteLine("Available profiles:");

        foreach (string name in profiles)
        {
            string status;
            try
            {
                GameProfile profile = GameProfile.Load(ProfileLocator.Resolve(name));
                status = profile.Remotes is null ? "loads (read-only)" : "loads (read/write)";
            }
            catch (Exception ex)
            {
                // Splitting on '.' would cut the message at the file path, not the
                // sentence, so trim by length instead.
                string message = ex.Message.ReplaceLineEndings(" ");
                status = "incomplete: " + (message.Length > 90 ? message[..90] + "..." : message);
            }

            Console.WriteLine($"  {name,-12} {status}");
        }

        return 0;
    }

    private static ProcessMemory OpenTarget(AgentOptions options, bool writable)
    {
        if (options.Pid is { } pid)
        {
            return ProcessMemory.Attach(pid, writable);
        }

        string? name = options.ProcessName;

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Specify a target with --pid <n> or --process <name>.");
        }

        if (options.Wait && Process.GetProcessesByName(name).Length == 0)
        {
            Console.WriteLine($"waiting for {name} ...");
            Process? found = GameLauncher.WaitForProcess(name, TimeSpan.FromMinutes(5));

            if (found is null)
            {
                throw new InvalidOperationException($"'{name}' did not start within 5 minutes.");
            }

            found.Dispose();
        }

        return ProcessMemory.AttachByName(name, writable);
    }

    private static int Info(AgentOptions options)
    {
        using ProcessMemory memory = OpenTarget(options, writable: false);

        Console.WriteLine($"pid       {memory.ProcessId}");
        Console.WriteLine($"name      {memory.ProcessName}");
        Console.WriteLine($"bitness   {(memory.Is32Bit ? "32-bit (WOW64)" : "64-bit")}, pointer size {memory.PointerSize}");
        Console.WriteLine();
        Console.WriteLine("modules:");

        foreach (ModuleRegion module in memory.GetModules().Take(40))
        {
            Console.WriteLine($"  {module.Name,-24} base 0x{module.BaseAddress:X8}  size 0x{module.Size:X}");
        }

        long privateBytes = 0;
        int privateRegions = 0;

        foreach (MemoryRegion region in memory.EnumerateRegions(ScanScope.PrivateCommitted))
        {
            privateBytes += region.Size;
            privateRegions++;
        }

        Console.WriteLine();
        Console.WriteLine($"private committed: {privateRegions} regions, {privateBytes / (1024.0 * 1024.0):F1} MB (this is the scan surface)");
        return 0;
    }

    /// <summary>
    /// Drives an already-running instance from the main menu into a save. Separate from
    /// launch so a caller can start several instances, move their windows apart, and only
    /// then load: menu clicks go to whichever window is in front, so overlapping copies
    /// steal each other's input.
    /// </summary>
    private static int LoadSave(AgentOptions options)
    {
        if (options.LoadSave <= 0)
        {
            Console.Error.WriteLine("Specify which save with --load-save <n> (1 = top of the list).");
            return 1;
        }

        // Writable: the plugin-driven load route writes a request into the shared block.
        // Menu clicking (the fallback) never needed it, which is why this was read-only.
        using ProcessMemory memory = OpenTarget(options, writable: true);
        using Process game = Process.GetProcessById(memory.ProcessId);

        bool loaded = SaveLoader.TryLoadSave(
            game, memory, options.LoadSave - 1, TimeSpan.FromSeconds(180), out string diagnostic,
            options.LoadViaMenuClicks);

        Console.WriteLine(diagnostic);
        return loaded ? 0 : 1;
    }

    /// <summary>
    /// Runs the no-RE 2DA merge (repute.2da coop_player faction, appearance.2da coop_p2
    /// body) against the install named by --game-path, which may be the game directory
    /// or the exe inside it — the same flag launch already takes. Explicitly on demand
    /// only: nothing else calls the installer, and the plugin does not use the new
    /// faction row yet (that switch is gated on a live pass).
    /// </summary>
    private static int InstallDataMods(AgentOptions options)
    {
        string path = options.GamePath;
        string? gameDirectory = Directory.Exists(path) ? path : Path.GetDirectoryName(path);

        if (gameDirectory is null || !Directory.Exists(gameDirectory))
        {
            Console.Error.WriteLine($"game directory not found: {path} (use --game-path <dir or exe>)");
            return 1;
        }

        DataModInstallResult result = DataModInstaller.Install(gameDirectory);

        Console.WriteLine($"override:        {result.OverrideDirectory}");
        Console.WriteLine(
            $"repute.2da:      '{DataModInstaller.FactionLabel}' faction is row {result.FactionRowIndex}"
            + (result.ReputeWritten ? " (merged and written)" : " (already present, unchanged)"));
        Console.WriteLine(
            $"appearance.2da:  '{DataModInstaller.AppearanceLabel}' body is row {result.AppearanceRowIndex}"
            + (result.AppearanceWritten ? " (merged and written)" : " (already present, unchanged)"));
        return 0;
    }

    private static int Scan(AgentOptions options)
    {
        using ProcessMemory memory = OpenTarget(options, writable: false);
        new ScanConsole(memory).Run();
        return 0;
    }

    private static int Track(AgentOptions options)
    {
        using ProcessMemory memory = OpenTarget(options, writable: false);
        return new TrackConsole(memory).Run();
    }

    /// <summary>
    /// Chooses between the KOTOR-specific bridge and a JSON offset profile.
    ///
    /// The KOTOR bridge exists because an offset profile cannot express what KOTOR needs:
    /// enumerate every creature by vtable, pick the one flagged is_pc as the local player,
    /// and drive a selection of the others as remote avatars. Those are three different
    /// objects at unrelated heap addresses, not fields at fixed offsets from one anchor.
    /// </summary>
    private static IGameBridge CreateBridge(
        AgentOptions options, ProcessMemory memory, out string diagnostic, out bool resolved)
    {
        if (options.UseKotorBridge)
        {
            KotorGameBridge kotor = new(memory, options.Phantoms, ownsMemory: false);
            resolved = kotor.TryResolve(out diagnostic);

            // Co-op means at most one of the two windows is focused, and KOTOR pauses the
            // moment it loses focus, so the unfocused player would freeze for everybody.
            if (resolved && kotor.PluginAttached && kotor.SetKeepAliveUnfocused(true))
            {
                diagnostic += "; unfocused-pause suppression on";
            }

            return kotor;
        }

        string profilePath = ProfileLocator.Resolve(options.Profile);
        GameProfile profile = GameProfile.Load(profilePath);
        Console.WriteLine($"profile: {profile.Name} ({profilePath})");

        ProfileGameBridge bridge = new(memory, profile, ownsMemory: false);
        resolved = bridge.TryResolveAnchor(out diagnostic);
        return bridge;
    }

    private static int AutoTrack(AgentOptions options)
    {
        using ProcessMemory memory = OpenTarget(options, writable: false);

        using Process game = Process.GetProcessById(memory.ProcessId);
        return new AutoTrackConsole(memory, game).Run();
    }

    /// <summary>
    /// Fingerprints the attached game's install for the compatibility handshake. Derives
    /// the game directory from the process's own executable path, so it is correct whether
    /// the game is GOG, Steam, or a second co-op copy. Returns <see
    /// cref="ContentDigest.Unknown"/> if anything is unreadable — the session then treats
    /// compatibility as unchecked rather than failing.
    /// </summary>
    private static ContentDigest ComputeInstallDigest(int processId)
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

            ContentDigest digest = InstallDigest.Compute(gameDir, exePath);
            Console.WriteLine($"content digest: {DescribeDigest(digest)}");
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
            parts.Add($"exe {d.ExeDigest:X8}");
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

        return string.Join(", ", parts);
    }

    /// <summary>
    /// Enables the in-game Co-op button and waits for the player to press it, then resolves
    /// the connection from coop.ini (the button may also carry a typed host IP, which wins).
    /// Returns go=false if cancelled before a connect was chosen.
    /// </summary>
    private static async Task<(bool Go, CoopMode Mode, IPEndPoint? Host, string Name, string Password)>
        WaitForMenuConnectAsync(IGameBridge bridge, int processId, AgentOptions options, CancellationToken token)
    {
        CoopMode mode = options.Mode;
        IPEndPoint? host = options.HostEndPoint;
        string name = options.Name;
        string password = options.Password;

        CoopConfig? config = null;
        try
        {
            using Process game = Process.GetProcessById(processId);
            string? gameDir = Path.GetDirectoryName(game.MainModule?.FileName);
            if (gameDir is not null)
            {
                config = CoopConfig.TryLoad(CoopConfig.PathFor(gameDir));
            }
        }
        catch
        {
            // No coop.ini: the command-line options stand in for it.
        }

        // Only the KOTOR bridge exposes the plugin block the button lives in.
        if (bridge is not KotorGameBridge kotor || !kotor.EnableConnectMenu(config?.MenuButton ?? false))
        {
            Console.WriteLine("in-game Co-op button unavailable (no plugin); waiting for a connect signal is skipped.");
            // Without the plugin there is nothing to wait on — connect immediately from
            // whatever config we have.
            return ApplyConfig(true);
        }

        Console.WriteLine(config?.MenuButton == true
            ? "press the Co-op button in the game's main menu to connect (or use the ~ console: servers / connect <ip|#n> / host)..."
            : "waiting for a connect request (coop.ini menu_button=1 adds the menu button; the ~ console also works)...");

        // Listen for LAN beacons while we wait, so `servers` can list hosts at the menu.
        using ServerDiscovery browser = new();
        browser.StartListening();
        IReadOnlyList<ServerInfo> lastServers = [];

        while (!token.IsCancellationRequested)
        {
            // Keep the in-game browser panel fed with what the LAN listener has found, so the
            // list is live while the player is looking at it.
            lastServers = browser.Servers;
            kotor.PublishBrowserServers(
                [.. lastServers.Select(s =>
                    (s.Name, s.Address, (byte)s.PlayerCount, (byte)s.MaxPlayers, s.Locked, s.Module))]);

            // The player chose to host from the panel rather than join a listed server.
            if (kotor.TryTakeBrowserHost())
            {
                MenuReply(kotor, "hosting; the other player will see you in their list");
                return (true, CoopMode.Host, null, name, password);
            }

            // The player picked a row in the panel and pressed Enter.
            if (kotor.TryTakeBrowserConnect(out int picked))
            {
                if (picked >= 0 && picked < lastServers.Count)
                {
                    ServerInfo chosen = lastServers[picked];
                    MenuReply(kotor, $"connecting to {chosen.Name} ({chosen.Address})");
                    return (true, CoopMode.Join, chosen.EndPoint, name, password);
                }

                MenuReply(kotor, "that server is no longer listed");
            }

            if (kotor.TryTakeConnectRequest(out string typedHost))
            {
                kotor.SetConnectStatus(SharedBlockConnectStatusConnecting);
                (bool go, CoopMode m, IPEndPoint? h, string n, string p) = ApplyConfig(true);

                // A host IP typed in-game overrides coop.ini's.
                if (typedHost.Length > 0 && IPEndPoint.TryParse(
                        typedHost.Contains(':') ? typedHost : $"{typedHost}:{options.Port}", out IPEndPoint? typed))
                {
                    m = CoopMode.Join;
                    h = typed;
                }

                return (go, m, h, n, p);
            }

            // The ~ console also works while waiting at the menu — this is exactly where a
            // server browser is most useful, and the FuncInterp hook routes console commands at
            // the menu. Without this, `servers` / `connect <ip>` / `host` did nothing until a
            // session already existed, because CoopCommandDispatcher only starts afterwards.
            if (kotor.TryTakeConsoleCommand(out string consoleLine))
            {
                CoopCommand command = ConsoleCommand.Parse(consoleLine);
                switch (command.Kind)
                {
                    case CoopCommandKind.Servers:
                        IReadOnlyList<ServerInfo> found = browser.Servers;
                        lastServers = found;
                        if (found.Count == 0)
                        {
                            MenuReply(kotor, "no co-op servers found on the LAN (host one, or connect by IP)");
                            break;
                        }

                        MenuReply(kotor, $"servers on the LAN ({found.Count}) - connect #n to join:");
                        for (int i = 0; i < found.Count; i++)
                        {
                            MenuReply(kotor, $"  {i + 1}) {found[i].Name}  {found[i].Address}  ({found[i].PlayerCount}/{found[i].MaxPlayers})");
                        }

                        break;

                    case CoopCommandKind.Host:
                        MenuReply(kotor, "hosting...");
                        return (true, CoopMode.Host, null, name,
                            command.Password is { Length: > 0 } hp ? hp : password);

                    case CoopCommandKind.Connect:
                        // `connect #n` picks from the last listing; otherwise a typed address.
                        if (command.Address is { Length: > 1 } pick && pick[0] == '#'
                            && int.TryParse(pick[1..], out int index))
                        {
                            if (index < 1 || index > lastServers.Count)
                            {
                                MenuReply(kotor, $"no server #{index} - run `servers` first");
                                break;
                            }

                            ServerInfo chosen = lastServers[index - 1];
                            MenuReply(kotor, $"connecting to {chosen.Name} ({chosen.Address})");
                            return (true, CoopMode.Join, chosen.EndPoint, name,
                                command.Password is { Length: > 0 } cp1 ? cp1 : password);
                        }

                        if (command.TryResolveEndpoint(options.Port, out IPEndPoint typedEndpoint))
                        {
                            MenuReply(kotor, $"connecting to {typedEndpoint}");
                            return (true, CoopMode.Join, typedEndpoint, name,
                                command.Password is { Length: > 0 } cp2 ? cp2 : password);
                        }

                        MenuReply(kotor, $"could not resolve host '{command.Address}'");
                        break;

                    case CoopCommandKind.Status:
                        MenuReply(kotor, "at the main menu - not connected");
                        break;

                    case CoopCommandKind.Help:
                        MenuReply(kotor, ConsoleCommand.HelpText);
                        break;

                    case CoopCommandKind.Invalid:
                        MenuReply(kotor, command.Error);
                        break;
                }
            }

            try
            {
                await Task.Delay(200, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return (false, CoopMode.None, null, name, password);

        (bool, CoopMode, IPEndPoint?, string, string) ApplyConfig(bool go)
        {
            if (config is null)
            {
                return (go, mode, host, name, password);
            }

            return (
                go,
                config.Host ? CoopMode.Host : CoopMode.Join,
                config.Host ? null : config.ResolveEndPoint(),
                config.Name ?? name,
                config.Password.Length > 0 ? config.Password : password);
        }
    }

    /// <summary>
    /// Echoes a menu-time console reply to stdout, the in-game console reply line and the chat
    /// overlay — the same three places <see cref="CoopCommandDispatcher"/> uses once a session
    /// exists, so the console behaves identically before and after connecting.
    /// </summary>
    private static void MenuReply(KotorGameBridge kotor, string text)
    {
        Console.WriteLine($"[coop] {text}");
        kotor.PostConsoleReply(text);
        kotor.PostChatLine(text);
    }

    /// <summary>Mirror of SharedBlock.ConnectStatusConnecting for the agent side.</summary>
    private const uint SharedBlockConnectStatusConnecting = 1;

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
    private static void AutoLoadAfterMenuConnect(ProcessMemory memory)
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

    private static async Task<int> RunAsync(AgentOptions options)
    {
        if (options.Mode == CoopMode.None)
        {
            if (!options.MenuConnect)
            {
                Console.Error.WriteLine("Specify --host or --join <host[:port]>, or --menu to connect from the game.");
                return 1;
            }
        }

        using ProcessMemory memory = OpenTarget(options, writable: !options.ReadOnlyAttach);

        // ⚠ ONE AGENT PER GAME PROCESS, enforced rather than assumed.
        //
        // Two things start an agent — the plugin's AgentLauncher (coop.ini
        // auto_start_agent, on by default) and whatever the human ran — and until now
        // nothing stopped both from attaching to the same swkotor.exe. Both then find the
        // same shared block and write it, both join the session as separate players, and the
        // symptoms are indistinguishable from a protocol bug: a phantom of yourself, peer
        // counts that do not match the number of games open, and two writers racing over
        // OffsetPlayerCreature and the remote slots. This is how a third peer appeared in a
        // two-instance session.
        //
        // Keyed on the target pid, not on the agent, so running the agent by hand against a
        // second game is unaffected. Held for the process lifetime; abandoning it (a crash)
        // releases it, which is the behaviour we want.
        using Mutex attachment = new(initiallyOwned: false, $"Local\\kotorcoop-agent-{memory.ProcessId}");
        bool attached;
        try
        {
            attached = attachment.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            attached = true; // the previous agent died; the game is ours
        }

        if (!attached)
        {
            Console.Error.WriteLine(
                $"another KotorCoop agent is already attached to pid {memory.ProcessId}; "
                + "refusing to be a second one. Set auto_start_agent = 0 in coop.ini if you "
                + "want to drive the agent yourself.");
            return 3;
        }

        try
        {
            return await RunAttachedAsync(options, memory).ConfigureAwait(false);
        }
        finally
        {
            attachment.ReleaseMutex();
        }
    }

    private static async Task<int> RunAttachedAsync(AgentOptions options, ProcessMemory memory)
    {
        using IGameBridge bridge = CreateBridge(options, memory, out string diagnostic, out bool resolved);

        if (!resolved)
        {
            // --menu waits at the MAIN MENU, where by definition no module is loaded and there
            // is no player creature to find — so an unresolved bridge is the expected state, not
            // a failure. The bridge re-resolves lazily once a save is loaded. Any other mode
            // genuinely needs the player now.
            if (!options.MenuConnect)
            {
                Console.Error.WriteLine($"could not locate player state: {diagnostic}");
                return 2;
            }

            Console.WriteLine($"waiting at the main menu ({diagnostic}); player state resolves when a game loads.");
        }
        else
        {
            Console.WriteLine(diagnostic);
        }

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        // The in-game Connect flow: enable the menu button and wait for the player to press
        // it, taking the host/join details from coop.ini. Falls through to the command-line
        // options when coop.ini is absent.
        CoopMode mode = options.Mode;
        IPEndPoint? hostEndPoint = options.HostEndPoint;
        string playerName = options.Name;
        string password = options.Password;

        if (options.MenuConnect)
        {
            (bool go, mode, hostEndPoint, playerName, password) =
                await WaitForMenuConnectAsync(bridge, memory.ProcessId, options, cts.Token).ConfigureAwait(false);

            if (!go)
            {
                return 0; // cancelled before connecting
            }
        }

        if (mode == CoopMode.None)
        {
            Console.Error.WriteLine("no connection was chosen.");
            return 1;
        }

        if (mode == CoopMode.Join && hostEndPoint is null)
        {
            Console.Error.WriteLine("join requested but no host address is set (coop.ini host=...).");
            return 1;
        }

        // Fingerprint this install so a mod mismatch with the peer is caught at join time.
        // Best-effort: a digest that could not be computed compares as "unchecked" and
        // never blocks a session.
        ContentDigest digest = ComputeInstallDigest(memory.ProcessId);

        // The player name is fixed for the process; the in-game console can vary only the
        // password (per connect), so the settings factory closes over the resolved name.
        string effectiveName = playerName;

        // coop.ini can override the two settings that decide how far behind a peer looks —
        // the send rate and the interpolation buffer — plus pause mirroring, so they can be
        // tuned by whoever is actually watching the screen rather than only at build time.
        CoopConfig? tuning = null;
        try
        {
            using Process game = Process.GetProcessById(memory.ProcessId);
            string? gameDir = Path.GetDirectoryName(game.MainModule?.FileName);
            if (gameDir is not null)
            {
                tuning = CoopConfig.TryLoad(CoopConfig.PathFor(gameDir));
            }
        }
        catch
        {
            // No coop.ini: the command line and the built-in defaults stand.
        }

        int tickRate = tuning is { TickRate: > 0 } ? tuning.TickRate : options.TickRate;
        int interpolationMs = tuning is { InterpolationDelayMs: >= 0 }
            ? tuning.InterpolationDelayMs
            : ProtocolConstants.DefaultInterpolationDelayMs;

        if (tickRate != options.TickRate || interpolationMs != ProtocolConstants.DefaultInterpolationDelayMs)
        {
            Console.WriteLine($"tuning: {tickRate} Hz, {interpolationMs} ms interpolation (from coop.ini)");
        }

        AgentSettings agentSettings = new()
        {
            PollRate = tickRate,
            FilterByModule = options.FilterByModule,
            FollowHostModule = options.FollowHostModule,
            SyncParty = options.SyncParty,
            SyncProgression = options.SyncProgression,
            SyncPause = tuning?.PauseSync ?? true,
        };

        // Surface why a cached player pointer stopped validating. Re-resolving costs a full
        // memory scan, so if this fires repeatedly the validation is wrong, not the pointer.
        if (bridge is KotorGameBridge resolveDiag)
        {
            resolveDiag.ResolveDiagnostic = message => Console.WriteLine($"[resolve] {message}");
        }

        using SessionManager manager = new(bridge, agentSettings);

        SessionSettings SettingsFor(string sessionPassword) => new()
        {
            PlayerName = effectiveName,
            Port = options.Port,
            TickRate = tickRate,
            InterpolationDelayMs = interpolationMs,
            Digest = digest,
            AllowDifferentSave = options.AllowDifferentSave,
            Password = sessionPassword,
        };

        CoopCommandDispatcher dispatcher = new(manager, bridge, options.Port, SettingsFor);

        // Push the coop.ini chat-overlay settings to the plugin (best-effort; defaults when
        // there is no coop.ini). Independent of connection mode — the overlay is useful the
        // moment a session is up.
        ApplyChatOverlayConfig(bridge, memory.ProcessId);

        // The initial connection goes through the manager directly; every later connect,
        // disconnect and reconnect arrives through the dispatcher (the ~ console or the menu
        // button) and swaps the session under the running dashboard.
        manager.Connect(mode == CoopMode.Host, mode == CoopMode.Host ? null : hostEndPoint, SettingsFor(password));

        // A session started from the game's own menu — hosting or joining — should land the
        // player in the world without another human step. The browser guarantees its panel
        // is closed before the choice is handed over, so the menu is a plain, freshly
        // rebuilt main menu by the time this runs.
        if (options.MenuConnect && !resolved)
        {
            _ = Task.Run(() => AutoLoadAfterMenuConnect(memory), cts.Token);
        }

        Task dispatchTask = dispatcher.RunAsync(cts.Token);
        AgentDashboard dashboard = new(manager, bridge, options.DropFile);
        await dashboard.RunAsync(cts.Token).ConfigureAwait(false);
        await dispatchTask.ConfigureAwait(false);

        return 0;
    }
}

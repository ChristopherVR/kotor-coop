using System.Diagnostics;
using System.Net;
using KotorCoop.Memory;
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
            ? "press the Co-op button in the game's main menu to connect..."
            : "waiting for a connect request (coop.ini menu_button=1 adds the menu button)...");

        while (!token.IsCancellationRequested)
        {
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
        using IGameBridge bridge = CreateBridge(options, memory, out string diagnostic, out bool resolved);

        if (!resolved)
        {
            Console.Error.WriteLine($"could not locate player state: {diagnostic}");
            return 2;
        }

        Console.WriteLine(diagnostic);

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

        AgentSettings agentSettings = new()
        {
            PollRate = options.TickRate,
            FilterByModule = options.FilterByModule,
            FollowHostModule = options.FollowHostModule,
            SyncParty = options.SyncParty,
            SyncProgression = options.SyncProgression,
        };

        using SessionManager manager = new(bridge, agentSettings);

        SessionSettings SettingsFor(string sessionPassword) => new()
        {
            PlayerName = effectiveName,
            Port = options.Port,
            TickRate = options.TickRate,
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

        Task dispatchTask = dispatcher.RunAsync(cts.Token);
        AgentDashboard dashboard = new(manager, bridge, options.DropFile);
        await dashboard.RunAsync(cts.Token).ConfigureAwait(false);
        await dispatchTask.ConfigureAwait(false);

        return 0;
    }
}

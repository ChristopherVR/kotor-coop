using System.Diagnostics;
using KotorCoop.Memory;
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

        using ProcessMemory memory = OpenTarget(options, writable: false);
        using Process game = Process.GetProcessById(memory.ProcessId);

        bool loaded = SaveLoader.TryLoadSave(
            game, memory, options.LoadSave - 1, TimeSpan.FromSeconds(180), out string diagnostic);

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

    private static async Task<int> RunAsync(AgentOptions options)
    {
        if (options.Mode == CoopMode.None)
        {
            Console.Error.WriteLine("Specify --host or --join <host[:port]>.");
            return 1;
        }

        using ProcessMemory memory = OpenTarget(options, writable: !options.ReadOnlyAttach);
        using IGameBridge bridge = CreateBridge(options, memory, out string diagnostic, out bool resolved);

        if (!resolved)
        {
            Console.Error.WriteLine($"could not locate player state: {diagnostic}");
            return 2;
        }

        Console.WriteLine(diagnostic);

        SessionSettings settings = new()
        {
            PlayerName = options.Name,
            Port = options.Port,
            TickRate = options.TickRate,
        };

        using ICoopSession session = options.Mode == CoopMode.Host
            ? new CoopHost(settings)
            : new CoopClient(settings, options.HostEndPoint!);

        CoopAgent agent = new(bridge, session, new AgentSettings
        {
            PollRate = options.TickRate,
            FilterByModule = options.FilterByModule,
        });

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        Task agentTask = agent.RunAsync(cts.Token);
        AgentDashboard dashboard = new(agent, options.DropFile);
        await dashboard.RunAsync(cts.Token).ConfigureAwait(false);
        await agentTask.ConfigureAwait(false);

        return 0;
    }
}

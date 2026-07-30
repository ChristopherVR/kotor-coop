using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.MockGame;

internal static class Program
{
    private const int FrameDelayMs = 33;

    private static async Task<int> Main(string[] args)
    {
        MockGameOptions options = MockGameOptions.Parse(args);

        if (options.ShowHelp)
        {
            Console.WriteLine(MockGameOptions.HelpText);
            return 0;
        }

        ConsoleSupport.EnableAnsi();

        using GameStateBlock block = new();
        MockWorld world = new(options.Module, options.OriginX, options.OriginY);
        block.SetLocal(world.Position, world.Facing, world.Module, world.Health, world.Animation);

        Console.WriteLine($"KotorCoop.MockGame  pid={Environment.ProcessId}  state block at 0x{block.Address:X}");

        ICoopSession? session = null;
        CoopAgent? agent = null;
        ProcessMemory? memory = null;
        ProfileGameBridge? bridge = null;

        using CancellationTokenSource cts = new();
        Task agentTask = Task.CompletedTask;

        try
        {
            if (options.Mode != CoopMode.None)
            {
                SessionSettings settings = new()
                {
                    PlayerName = options.Name,
                    Port = options.Port,
                    TickRate = options.TickRate,
                    InterpolationDelayMs = options.InterpolationDelayMs,
                };

                session = options.Mode == CoopMode.Host
                    ? new CoopHost(settings)
                    : new CoopClient(settings, options.HostEndPoint!);

                string profilePath = ProfileLocator.Resolve(options.Profile);
                GameProfile profile = GameProfile.Load(profilePath);
                Console.WriteLine($"profile: {profile.Name} ({profilePath})");

                // Attach to ourselves. Same OpenProcess/ReadProcessMemory path the agent
                // uses against swkotor.exe, so nothing here is a special case.
                memory = ProcessMemory.Attach(Environment.ProcessId);
                bridge = new ProfileGameBridge(memory, profile, ownsMemory: false);

                if (!bridge.TryResolveAnchor(out string diagnostic))
                {
                    Console.Error.WriteLine($"Failed to locate the state block: {diagnostic}");
                    return 2;
                }

                Console.WriteLine(diagnostic);

                if (bridge.AnchorAddress != block.Address)
                {
                    // Do not carry on with a bad anchor. The agent would read unrelated
                    // memory as "the local player" and publish garbage coordinates to every
                    // peer, which is exactly what happened before the signature was widened:
                    // a false match at a lower address won because the scanner takes the
                    // first hit. Fail loudly rather than corrupt the session.
                    Console.Error.WriteLine(
                        $"FATAL: signature scan found 0x{bridge.AnchorAddress:X} but the real block is at "
                        + $"0x{block.Address:X}. The pattern matched something else, so the local player "
                        + "would be read from unrelated memory. Refusing to start.");
                    return 3;
                }

                Console.WriteLine("scanner found the state block by signature; read/write path verified.");

                agent = new CoopAgent(bridge, session, new AgentSettings { PollRate = options.TickRate });
                agentTask = agent.RunAsync(cts.Token);

                await Task.Delay(250).ConfigureAwait(false);
            }

            await RunGameLoopAsync(block, world, session, agent, options).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex.Message}");
            return 1;
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);

            try
            {
                await agentTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            bridge?.Dispose();
            memory?.Dispose();
            session?.Dispose();
        }
    }

    private static async Task RunGameLoopAsync(
        GameStateBlock block,
        MockWorld world,
        ICoopSession? session,
        CoopAgent? agent,
        MockGameOptions options)
    {
        MockRenderer renderer = new();
        renderer.PrepareConsole();

        string banner = options.Mode switch
        {
            CoopMode.Host => $"\u001b[1mKotorCoop mock  [{options.Name}]  HOST :{options.Port}\u001b[0m",
            CoopMode.Join => $"\u001b[1mKotorCoop mock  [{options.Name}]  JOIN {options.HostEndPoint}\u001b[0m",
            _ => $"\u001b[1mKotorCoop mock  [{options.Name}]  standalone\u001b[0m",
        };

        long lastMs = SessionClock.NowMs;
        long startedMs = lastMs;
        bool running = true;

        try
        {
            while (running)
            {
                long now = SessionClock.NowMs;
                float delta = Math.Clamp((now - lastMs) / 1000f, 0f, 0.25f);
                lastMs = now;

                if (options.ExitAfterSeconds > 0 && now - startedMs > options.ExitAfterSeconds * 1000L)
                {
                    running = false;
                }

                MoveInput input = ReadInput(world, session, ref running);

                if (options.AutoWalk)
                {
                    // A slow arc, so an unattended instance still produces motion for a
                    // peer to interpolate.
                    input = new MoveInput(1f, 0f, 0.35f);
                }

                world.Step(delta, input);

                block.SetLocal(world.Position, world.Facing, world.Module, world.Health, world.Animation);
                renderer.Render(block, world, session, agent, banner);

                await Task.Delay(FrameDelayMs).ConfigureAwait(false);
            }
        }
        finally
        {
            renderer.RestoreConsole();
        }
    }

    private static MoveInput ReadInput(MockWorld world, ICoopSession? session, ref bool running)
    {
        float forward = 0f;
        float strafe = 0f;
        float turn = 0f;

        // Console.KeyAvailable throws when stdin is redirected, which is exactly what
        // happens under a test harness or when piping output to a file.
        if (Console.IsInputRedirected)
        {
            return MoveInput.None;
        }

        // The console gives keypresses, not key state, so held keys arrive as repeats.
        while (Console.KeyAvailable)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.W or ConsoleKey.UpArrow:
                    forward += 1f;
                    break;

                case ConsoleKey.S or ConsoleKey.DownArrow:
                    forward -= 1f;
                    break;

                case ConsoleKey.A:
                    strafe -= 1f;
                    break;

                case ConsoleKey.D:
                    strafe += 1f;
                    break;

                case ConsoleKey.Q or ConsoleKey.LeftArrow:
                    turn += 1f;
                    break;

                case ConsoleKey.E or ConsoleKey.RightArrow:
                    turn -= 1f;
                    break;

                case ConsoleKey.M:
                    world.CycleModule();
                    break;

                case ConsoleKey.X:
                    world.Damage(7);
                    break;

                case ConsoleKey.F:
                    ToggleDoorFlag(session);
                    break;

                case ConsoleKey.Escape:
                    running = false;
                    break;

                default:
                    break;
            }
        }

        return new MoveInput(Math.Clamp(forward, -1f, 1f), Math.Clamp(strafe, -1f, 1f), Math.Clamp(turn, -1f, 1f));
    }

    private static void ToggleDoorFlag(ICoopSession? session)
    {
        if (session is null)
        {
            return;
        }

        session.Flags.TryGet(DemoFlags.BulkheadDoor, out sbyte current);
        session.SetFlag(DemoFlags.BulkheadDoor, current != 0 ? (sbyte)0 : (sbyte)1);
    }
}

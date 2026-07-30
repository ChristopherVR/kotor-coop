using System.Globalization;
using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.MockGame;

public enum CoopMode
{
    None,
    Host,
    Join,
}

public sealed record MockGameOptions
{
    public string Name { get; init; } = "player";

    public CoopMode Mode { get; init; } = CoopMode.None;

    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    public IPEndPoint? HostEndPoint { get; init; }

    public string Module { get; init; } = MockWorld.Modules[0];

    public string Profile { get; init; } = "mockgame";

    public int TickRate { get; init; } = ProtocolConstants.DefaultTickRate;

    public int InterpolationDelayMs { get; init; } = ProtocolConstants.DefaultInterpolationDelayMs;

    public bool ShowHelp { get; init; }

    /// <summary>Quit after this many seconds. For scripted verification runs.</summary>
    public int ExitAfterSeconds { get; init; }

    /// <summary>Walk the avatar automatically, so an unattended run still produces motion.</summary>
    public bool AutoWalk { get; init; }

    /// <summary>
    /// Centre of the roaming area. Set these to a real game's coordinates so the mock
    /// shares its coordinate space, which is what makes a bridged phantom appear next to
    /// the other player rather than 50 units away.
    /// </summary>
    public float OriginX { get; init; } = MockWorld.Size / 2f;

    public float OriginY { get; init; } = MockWorld.Size / 2f;

    public static MockGameOptions Parse(string[] args)
    {
        string name = $"player{Random.Shared.Next(100, 999)}";
        CoopMode mode = CoopMode.None;
        int port = ProtocolConstants.DefaultPort;
        IPEndPoint? hostEndPoint = null;
        string module = MockWorld.Modules[0];
        string profile = "mockgame";
        int tickRate = ProtocolConstants.DefaultTickRate;
        int interpolation = ProtocolConstants.DefaultInterpolationDelayMs;
        bool help = false;
        int exitAfter = 0;
        bool autoWalk = false;
        float originX = MockWorld.Size / 2f;
        float originY = MockWorld.Size / 2f;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            string? Next() => i + 1 < args.Length ? args[++i] : null;

            switch (arg)
            {
                case "--host":
                    mode = CoopMode.Host;
                    break;

                case "--join":
                    mode = CoopMode.Join;
                    hostEndPoint = ParseEndPoint(Next() ?? "127.0.0.1", ProtocolConstants.DefaultPort);
                    break;

                case "--name":
                    name = Next() ?? name;
                    break;

                case "--port":
                    port = int.TryParse(Next(), out int parsedPort) ? parsedPort : port;
                    break;

                case "--module":
                    module = Next() ?? module;
                    break;

                case "--profile":
                    profile = Next() ?? profile;
                    break;

                case "--tick":
                    tickRate = int.TryParse(Next(), out int parsedTick) ? Math.Clamp(parsedTick, 1, 60) : tickRate;
                    break;

                case "--interp":
                    interpolation = int.TryParse(Next(), out int parsedInterp) ? Math.Clamp(parsedInterp, 0, 1000) : interpolation;
                    break;

                case "--exit-after":
                    exitAfter = int.TryParse(Next(), out int parsedExit) ? parsedExit : exitAfter;
                    break;

                case "--auto":
                    autoWalk = true;
                    break;

                case "--origin":
                {
                    // "--origin 106,92"
                    string[] parts2 = (Next() ?? string.Empty).Split(',', StringSplitOptions.TrimEntries);
                    if (parts2.Length == 2
                        && float.TryParse(parts2[0], CultureInfo.InvariantCulture, out float ox)
                        && float.TryParse(parts2[1], CultureInfo.InvariantCulture, out float oy))
                    {
                        originX = ox;
                        originY = oy;
                    }

                    break;
                }

                case "-h":
                case "--help":
                    help = true;
                    break;

                default:
                    Console.Error.WriteLine($"Unknown argument '{arg}'.");
                    help = true;
                    break;
            }
        }

        // A client that was given no explicit port still needs to know where to send.
        if (mode == CoopMode.Join && hostEndPoint is not null && port != ProtocolConstants.DefaultPort)
        {
            hostEndPoint = new IPEndPoint(hostEndPoint.Address, port);
        }

        return new MockGameOptions
        {
            Name = name,
            Mode = mode,
            Port = port,
            HostEndPoint = hostEndPoint,
            Module = module,
            Profile = profile,
            TickRate = tickRate,
            InterpolationDelayMs = interpolation,
            ShowHelp = help,
            ExitAfterSeconds = exitAfter,
            AutoWalk = autoWalk,
            OriginX = originX,
            OriginY = originY,
        };
    }

    private static IPEndPoint ParseEndPoint(string text, int defaultPort)
    {
        if (IPEndPoint.TryParse(text, out IPEndPoint? parsed) && parsed.Port != 0)
        {
            return parsed;
        }

        if (IPAddress.TryParse(text, out IPAddress? address))
        {
            return new IPEndPoint(address, defaultPort);
        }

        string host = text;
        int port = defaultPort;

        int colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out int explicitPort))
        {
            host = text[..colon];
            port = explicitPort;
        }

        IPAddress[] resolved = Dns.GetHostAddresses(host);
        if (resolved.Length == 0)
        {
            throw new FormatException($"Could not resolve host '{host}'.");
        }

        return new IPEndPoint(resolved[0], port);
    }

    public static string HelpText =>
        """
        KotorCoop.MockGame - a stand-in game process for testing the co-op stack.

        It allocates a player state block in unmanaged memory, moves an avatar around a
        small world, and renders whatever remote avatars the agent has written back into
        that block. The agent attaches to this process with OpenProcess/ReadProcessMemory,
        the same way it would attach to swkotor.exe.

          --host                 host a session on --port
          --join <host[:port]>   join a session
          --name <text>          player name shown to peers
          --port <n>             listen port when hosting, target port when joining (default 27015)
          --module <name>        starting module (end_m01aa or tar_m02aa)
          --profile <name|path>  offset profile to use (default: mockgame)
          --tick <n>             network/poll rate in Hz (default 20)
          --interp <ms>          interpolation delay (default 100)
          -h, --help             this text

        Two-instance test, one terminal each:
          dotnet run --project src/KotorCoop.MockGame -- --host --name Revan
          dotnet run --project src/KotorCoop.MockGame -- --join 127.0.0.1 --name Malak

        Controls: WASD move, Q/E turn, M change module, F toggle the shared door flag,
        X take damage, Esc quit.
        """;
}

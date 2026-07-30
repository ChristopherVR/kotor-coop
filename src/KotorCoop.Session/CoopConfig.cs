using System.Net;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Connection settings read from <c>coop.ini</c> beside the game executable, for the
/// in-game Connect flow: the player presses a menu button and the session starts from
/// these values. A file rather than typed-in-game input because KOTOR's edit control has
/// no password masking (see docs/research/connect-flow.md) — the IP can be typed in-game,
/// but the password should not be shown, so it lives here.
///
/// Format is a plain <c>key = value</c> INI, optionally under a <c>[coop]</c> header:
/// <code>
///   [coop]
///   mode = join        ; or host
///   host = 192.168.1.20
///   port = 27140
///   password = swordfish
///   name = Malak
///   menu_button = 1    ; enable the in-game Co-op menu button
/// </code>
/// </summary>
public sealed record CoopConfig
{
    public bool Host { get; init; }

    public string HostAddress { get; init; } = "127.0.0.1";

    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    public string Password { get; init; } = string.Empty;

    public string? Name { get; init; }

    /// <summary>Whether the plugin should rebind the main-menu button to start co-op. Off
    /// by default — the GUI rebind is engine-call code that wants live verification.</summary>
    public bool MenuButton { get; init; }

    /// <summary>The client endpoint implied by <see cref="HostAddress"/> and
    /// <see cref="Port"/>, or null if the address does not resolve.</summary>
    public IPEndPoint? ResolveEndPoint()
    {
        if (IPAddress.TryParse(HostAddress, out IPAddress? ip))
        {
            return new IPEndPoint(ip, Port);
        }

        try
        {
            IPAddress[] resolved = Dns.GetHostAddresses(HostAddress);
            return resolved.Length > 0 ? new IPEndPoint(resolved[0], Port) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The conventional path: coop.ini in the game directory.</summary>
    public static string PathFor(string gameDirectory) => Path.Combine(gameDirectory, "coop.ini");

    /// <summary>Reads coop.ini if present. Returns null when the file does not exist, so the
    /// caller can fall back to command-line options.</summary>
    public static CoopConfig? TryLoad(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        Dictionary<string, string> kv = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in File.ReadAllLines(path))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';' or '[')
            {
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            // Strip an inline comment, but not one inside a value that has no comment char.
            string value = line[(eq + 1)..].Trim();
            int comment = value.IndexOfAny([';', '#']);
            if (comment >= 0)
            {
                value = value[..comment].Trim();
            }

            kv[line[..eq].Trim()] = value;
        }

        bool host = kv.TryGetValue("mode", out string? mode)
            && mode.Equals("host", StringComparison.OrdinalIgnoreCase);

        return new CoopConfig
        {
            Host = host,
            HostAddress = kv.GetValueOrDefault("host", "127.0.0.1"),
            Port = int.TryParse(kv.GetValueOrDefault("port"), out int port) ? port : ProtocolConstants.DefaultPort,
            Password = kv.GetValueOrDefault("password", string.Empty),
            Name = kv.GetValueOrDefault("name"),
            MenuButton = IsTruthy(kv.GetValueOrDefault("menu_button")),
        };
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}

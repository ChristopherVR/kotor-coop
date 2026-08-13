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
///   menu_button = 1    ; the in-game Co-op menu button (default on; 0 hides it)
/// </code>
/// </summary>
public sealed record CoopConfig
{
    public bool Host { get; init; }

    public string HostAddress { get; init; } = "127.0.0.1";

    /// <summary>
    /// Whether the file actually named a host, as opposed to <see cref="HostAddress"/>
    /// holding its 127.0.0.1 default. A bare Co-op button press with no session to
    /// reconnect to needs this to tell "join the configured server" apart from "nothing
    /// is configured — point the player at the server browser".
    /// </summary>
    public bool HasHostAddress { get; init; }

    public int Port { get; init; } = ProtocolConstants.DefaultPort;

    public string Password { get; init; } = string.Empty;

    public string? Name { get; init; }

    /// <summary>
    /// What this host calls itself in other players' server browsers. Distinct from
    /// <see cref="Name"/>, which is the CHARACTER's name on the wire and in the selection
    /// band: a server wants a name for the session ("Chris's Tuesday game"), not for the
    /// person. Unset falls back to <see cref="Name"/>, which is what every existing install
    /// already advertises.
    /// </summary>
    public string? ServerName { get; init; }

    /// <summary>Whether the main-menu Co-op button shows. ON by default — the button is part
    /// of the install, and the plugin shows it on its own (it reads this same key from
    /// coop.ini in-process); the agent's write of this value into the shared block is a
    /// force-on override, kept for diagnostics. <c>menu_button = 0</c> hides it.</summary>
    public bool MenuButton { get; init; } = true;

    /// <summary>
    /// How often state is sent, in Hz. Every millisecond here is visible latency on the
    /// peer's movement, so it is exposed for tuning: raise it if peers feel behind, lower it
    /// if the network or CPU cannot keep up. 0 keeps the built-in default.
    /// </summary>
    public int TickRate { get; init; }

    /// <summary>
    /// How far in the past peers are rendered, in milliseconds. This is the OTHER half of
    /// perceived lag and the one worth trying first: it buys smoothness by always having a
    /// later snapshot to interpolate toward, which a LAN barely needs. 0 means "no buffer,
    /// show the newest position" — lowest latency, and jittery only if packets arrive
    /// unevenly. Negative means "keep the built-in default".
    /// </summary>
    public int InterpolationDelayMs { get; init; } = -1;

    /// <summary>
    /// Mirror a peer's pause into this game. On by default. Turn it off if a pause ever
    /// sticks: without the mirror each game pauses only for its own player, which is worse
    /// co-op but can never wedge.
    /// </summary>
    public bool PauseSync { get; init; } = true;

    /// <summary>Show the always-on in-game chat overlay.</summary>
    public bool ChatOverlay { get; init; } = true;

    /// <summary>Chat overlay opacity, 0..100. 75 stays readable on the game's bright interiors;
    /// lower it for a fainter overlay.</summary>
    public int ChatOverlayOpacity { get; init; } = 75;

    /// <summary>Chat overlay corner: top-left, top-right, bottom-left, bottom-right.</summary>
    public string ChatOverlayAnchor { get; init; } = "bottom-left";

    /// <summary>Lines shown at once.</summary>
    public int ChatOverlayLines { get; init; } = 5;

    /// <summary>Milliseconds a line stays before it fades; 0 keeps it forever.</summary>
    public int ChatOverlayFadeMs { get; init; } = 9000;

    /// <summary>The anchor as the plugin's numeric code (0=TL, 1=TR, 2=BL, 3=BR).</summary>
    public uint ChatOverlayAnchorCode => ChatOverlayAnchor.Trim().ToLowerInvariant() switch
    {
        "top-right" or "topright" or "tr" => 1,
        "bottom-left" or "bottomleft" or "bl" => 2,
        "bottom-right" or "bottomright" or "br" => 3,
        _ => 0,
    };

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
            HasHostAddress = kv.TryGetValue("host", out string? hostValue) && hostValue.Length > 0,
            Port = int.TryParse(kv.GetValueOrDefault("port"), out int port) ? port : ProtocolConstants.DefaultPort,
            Password = kv.GetValueOrDefault("password", string.Empty),
            Name = kv.GetValueOrDefault("name"),
            ServerName = kv.GetValueOrDefault("server_name"),
            MenuButton = !kv.ContainsKey("menu_button") || IsTruthy(kv.GetValueOrDefault("menu_button")),
            TickRate = int.TryParse(kv.GetValueOrDefault("tick_rate"), out int tick)
                ? Math.Clamp(tick, 5, 120)
                : 0,
            InterpolationDelayMs = int.TryParse(kv.GetValueOrDefault("interp_ms"), out int interp)
                ? Math.Clamp(interp, 0, 500)
                : -1,
            PauseSync = !kv.ContainsKey("pause_sync") || IsTruthy(kv.GetValueOrDefault("pause_sync")),
            ChatOverlay = !kv.ContainsKey("chat_overlay") || IsTruthy(kv.GetValueOrDefault("chat_overlay")),
            ChatOverlayOpacity = int.TryParse(kv.GetValueOrDefault("chat_overlay_opacity"), out int op)
                ? Math.Clamp(op, 0, 100)
                : 75,
            ChatOverlayAnchor = kv.GetValueOrDefault("chat_overlay_anchor", "bottom-left"),
            ChatOverlayLines = int.TryParse(kv.GetValueOrDefault("chat_overlay_lines"), out int cl)
                ? Math.Clamp(cl, 1, 12)
                : 5,
            ChatOverlayFadeMs = int.TryParse(kv.GetValueOrDefault("chat_overlay_fade_ms"), out int fm)
                ? Math.Max(fm, 0)
                : 9000,
        };
    }

    private static bool IsTruthy(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase));
}

using System.Net;

namespace KotorCoop.Session;

/// <summary>What an in-game console (or dashboard) line asks the session layer to do.</summary>
public enum CoopCommandKind
{
    /// <summary>Blank line or comment; do nothing.</summary>
    None,

    /// <summary>Join a host: <see cref="CoopCommand.Address"/> is the typed host.</summary>
    Connect,

    /// <summary>Begin hosting.</summary>
    Host,

    /// <summary>Tear down the current session.</summary>
    Disconnect,

    /// <summary>Restart the last session with the same parameters.</summary>
    Reconnect,

    /// <summary>Print the current session status.</summary>
    Status,

    /// <summary>Send a chat line to the other players; the text is in <see cref="CoopCommand.Address"/>.</summary>
    Say,

    /// <summary>Wipe the on-screen overlay log.</summary>
    Clear,

    /// <summary>List the co-op servers discovered on the LAN.</summary>
    Servers,

    /// <summary>List the available commands.</summary>
    Help,

    /// <summary>
    /// Answer a password prompt: retry the join that is waiting on one, with the text in
    /// <see cref="CoopCommand.Password"/>. Carries no address — the address is whichever join
    /// asked, which is the whole point of the verb (the player picked a row in the browser and
    /// should not have to retype an IP they never saw).
    /// </summary>
    Password,

    /// <summary>The line was understood as a command but is malformed; see <see cref="CoopCommand.Error"/>.</summary>
    Invalid,

    /// <summary>Not one of ours (some other console command); leave it alone.</summary>
    Unknown,

    /// <summary>Move this instance to another module through the plugin's travel channel;
    /// the module resref is in <see cref="CoopCommand.Address"/> and an optional waypoint
    /// tag rides in <see cref="CoopCommand.Password"/>.</summary>
    Warp,

    /// <summary>List the connected players and which module each is in.</summary>
    Who,

    /// <summary>Print the local player's module and position.</summary>
    Where,
}

/// <summary>
/// A parsed console command. <see cref="Address"/> and <see cref="Password"/> carry the
/// arguments for <see cref="CoopCommandKind.Connect"/>/<see cref="CoopCommandKind.Host"/>;
/// <see cref="Error"/> explains an <see cref="CoopCommandKind.Invalid"/> line.
/// </summary>
public readonly record struct CoopCommand(
    CoopCommandKind Kind,
    string? Address = null,
    string? Password = null,
    string Error = "")
{
    /// <summary>
    /// Resolves <see cref="Address"/> to an endpoint, applying <paramref name="defaultPort"/>
    /// when the typed host omits one. Returns false for a host-mode or malformed command.
    /// </summary>
    public bool TryResolveEndpoint(int defaultPort, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Loopback, defaultPort);

        if (string.IsNullOrWhiteSpace(Address))
        {
            return false;
        }

        string candidate = Address.Contains(':') ? Address : $"{Address}:{defaultPort}";
        if (IPEndPoint.TryParse(candidate, out IPEndPoint? parsed))
        {
            endpoint = parsed;
            return true;
        }

        // A bare hostname (not an IP literal): resolve it.
        string hostPart = Address;
        int port = defaultPort;
        int colon = Address.LastIndexOf(':');
        if (colon > 0 && int.TryParse(Address[(colon + 1)..], out int typedPort))
        {
            hostPart = Address[..colon];
            port = typedPort;
        }

        try
        {
            IPAddress[] resolved = Dns.GetHostAddresses(hostPart);
            if (resolved.Length > 0)
            {
                endpoint = new IPEndPoint(resolved[0], port);
                return true;
            }
        }
        catch
        {
            // Unresolvable; fall through to false.
        }

        return false;
    }
}

/// <summary>
/// Parses console/overlay command lines into <see cref="CoopCommand"/>. The grammar is
/// deliberately tiny and forgiving — this is typed by a player mid-game, not scripted:
///   <c>connect 1.2.3.4</c> · <c>connect 1.2.3.4:7777 secret</c> · <c>host</c> ·
///   <c>disconnect</c> · <c>reconnect</c> · <c>status</c> · <c>help</c>.
/// A leading <c>coop</c> (or <c>/coop</c>) prefix is accepted and stripped, so the same
/// parser serves both a dedicated overlay and a shared console.
/// </summary>
public static class ConsoleCommand
{
    public static CoopCommand Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new CoopCommand(CoopCommandKind.None);
        }

        string[] parts = line.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int index = 0;

        // Optional "coop"/"/coop" prefix so it works as a sub-command of a shared console.
        string first = parts[index].TrimStart('/');
        if (first.Equals("coop", StringComparison.OrdinalIgnoreCase))
        {
            index++;
            if (index >= parts.Length)
            {
                return new CoopCommand(CoopCommandKind.Help);
            }
        }

        string verb = parts[index].TrimStart('/').ToLowerInvariant();
        string[] rest = parts[(index + 1)..];

        switch (verb)
        {
            case "connect" or "join":
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: "connect needs a host address, e.g. connect 1.2.3.4");
                }

                return new CoopCommand(
                    CoopCommandKind.Connect,
                    Address: rest[0],
                    Password: rest.Length > 1 ? string.Join(' ', rest[1..]) : null);

            case "host":
                return new CoopCommand(
                    CoopCommandKind.Host,
                    Password: rest.Length > 0 ? string.Join(' ', rest) : null);

            // The whole rest of the line is the password, spaces and all: a passphrase is a
            // perfectly ordinary thing to set, and silently keeping only its first word would
            // look exactly like a wrong password.
            case "password" or "pw":
                return rest.Length == 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "password needs the password, e.g. password hoth")
                    : new CoopCommand(CoopCommandKind.Password, Password: string.Join(' ', rest));

            case "disconnect" or "dc" or "leave":
                return new CoopCommand(CoopCommandKind.Disconnect);

            case "reconnect" or "rc":
                return new CoopCommand(CoopCommandKind.Reconnect);

            case "say" or "chat" or "msg":
                return rest.Length == 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "say needs a message, e.g. say on my way")
                    : new CoopCommand(CoopCommandKind.Say, Address: string.Join(' ', rest));

            case "clear" or "cls":
                return new CoopCommand(CoopCommandKind.Clear);

            case "servers" or "browse" or "list":
                return new CoopCommand(CoopCommandKind.Servers);

            case "status" or "stat":
                return new CoopCommand(CoopCommandKind.Status);

            case "help" or "?" or "commands":
                return new CoopCommand(CoopCommandKind.Help);

            case "warp":
                // Validated here rather than in the dispatcher so a typo is caught with a
                // message instead of a silent no-op travel request. A module resref is at
                // most 16 chars of [a-z0-9_]; a waypoint TAG is looser but rides the same
                // 32-byte block field, so it gets the same charset with the block's cap.
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: "warp needs a module, e.g. warp danm13");
                }

                if (!IsResRef(rest[0], maxLength: 16))
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not a module name (letters, digits, _; max 16)");
                }

                if (rest.Length > 1 && !IsResRef(rest[1], maxLength: 31))
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[1]}' is not a waypoint tag (letters, digits, _; max 31)");
                }

                return new CoopCommand(
                    CoopCommandKind.Warp,
                    Address: rest[0].ToLowerInvariant(),
                    Password: rest.Length > 1 ? rest[1] : null);

            case "who" or "peers":
                return new CoopCommand(CoopCommandKind.Who);

            case "where":
                return new CoopCommand(CoopCommandKind.Where);

            default:
                return new CoopCommand(CoopCommandKind.Unknown);
        }
    }

    /// <summary>True for a resref-shaped token: 1..maxLength chars of ASCII letter/digit/underscore.</summary>
    private static bool IsResRef(string value, int maxLength)
    {
        if (value.Length < 1 || value.Length > maxLength)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>One-line help listing the commands, for the overlay and dashboard.</summary>
    public const string HelpText =
        "commands: servers | connect <ip|#n> [password] | password <pw> | host | disconnect | reconnect | say <msg> | clear | status | warp <module> | who | where | help";
}

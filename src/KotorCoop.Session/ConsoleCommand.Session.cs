namespace KotorCoop.Session;

/// <summary>
/// The connection/session-lifecycle verbs: connect, host, password, disconnect, reconnect,
/// say, clear, servers, status, lagstats, help, who, where, warp, goto. Every method here is
/// referenced from the verb table in <c>ConsoleCommand.cs</c> and returns exactly what the
/// original inline switch case returned.
/// </summary>
public static partial class ConsoleCommand
{
    private static CoopCommand ParseConnect(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: "connect needs a host address, e.g. connect 1.2.3.4");
        }

        return new CoopCommand(
            CoopCommandKind.Connect,
            Address: rest[0],
            Password: rest.Length > 1 ? string.Join(' ', rest[1..]) : null);
    }

    private static CoopCommand ParseHost(string[] rest) =>
        new(CoopCommandKind.Host, Password: rest.Length > 0 ? string.Join(' ', rest) : null);

    // The whole rest of the line is the password, spaces and all: a passphrase is a
    // perfectly ordinary thing to set, and silently keeping only its first word would
    // look exactly like a wrong password.
    private static CoopCommand ParsePassword(string[] rest) =>
        rest.Length == 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "password needs the password, e.g. password hoth")
            : new CoopCommand(CoopCommandKind.Password, Password: string.Join(' ', rest));

    private static CoopCommand ParseDisconnect(string[] rest) => new(CoopCommandKind.Disconnect);

    private static CoopCommand ParseReconnect(string[] rest) => new(CoopCommandKind.Reconnect);

    private static CoopCommand ParseSay(string[] rest) =>
        rest.Length == 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "say needs a message, e.g. say on my way")
            : new CoopCommand(CoopCommandKind.Say, Address: string.Join(' ', rest));

    private static CoopCommand ParseClear(string[] rest) => new(CoopCommandKind.Clear);

    private static CoopCommand ParseServers(string[] rest) => new(CoopCommandKind.Servers);

    private static CoopCommand ParseStatus(string[] rest) => new(CoopCommandKind.Status);

    private static CoopCommand ParseLagStats(string[] rest) => new(CoopCommandKind.LagStats);

    private static CoopCommand ParseHelp(string[] rest) => new(CoopCommandKind.Help);

    // Validated here rather than in the dispatcher so a typo is caught with a message instead
    // of a silent no-op travel request. A module resref is at most 16 chars of [a-z0-9_]; a
    // waypoint TAG is looser but rides the same 32-byte block field, so it gets the same
    // charset with the block's cap.
    private static CoopCommand ParseWarp(string[] rest)
    {
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
    }

    private static CoopCommand ParseWho(string[] rest) => new(CoopCommandKind.Who);

    private static CoopCommand ParseWhere(string[] rest) => new(CoopCommandKind.Where);

    private static CoopCommand ParseGoTo(string[] rest) =>
        rest.Length == 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "goto needs a player, e.g. goto Malak (or `who` to list)")
            : new CoopCommand(CoopCommandKind.GoTo, Address: string.Join(' ', rest));
}

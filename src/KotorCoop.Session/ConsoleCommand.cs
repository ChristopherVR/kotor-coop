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

    /// <summary>
    /// Inject a transport fault on the live session so a disconnect can be reproduced with
    /// nobody touching a cable. The sub-verb is in <see cref="CoopCommand.Address"/> and its
    /// argument (percentage, milliseconds, seconds, direction) in
    /// <see cref="CoopCommand.Password"/>.
    /// </summary>
    NetSim,

    // ---- staging cheats ---------------------------------------------------------
    //
    // Only the verbs whose ANSWER DEPENDS ON WHO TYPED THEM, plus the two that stage a
    // co-op scenario nothing else reaches. KOTOR's own console already ships giveitem,
    // givecredits, addexp, addlevel, revealmap and heal, all of which touch only the
    // typist's single-player state and are therefore already correct in co-op; duplicating
    // them here would add a second spelling and no correctness. See
    // docs/research/console-cheats.md.

    /// <summary>Toggle or report the local player's damage floor.
    /// <see cref="CoopCommand.Amount"/>: 1 on, 0 off, -1 report.</summary>
    God,

    /// <summary>Set the local player's hit points; <see cref="CoopCommand.Amount"/> 0 = to
    /// maximum.</summary>
    TopUp,

    /// <summary>Stand the local player up out of the co-op downed state.</summary>
    StandUp,

    /// <summary>Kill a creature through whichever authority path this instance owns. The
    /// target token (tag or id) is in <see cref="CoopCommand.Address"/>; empty means
    /// "the nearest hostile", resolved by the agent from its own enemy census.</summary>
    Slay,

    /// <summary>Move the local player to <see cref="CoopCommand.X"/>/<see cref="CoopCommand.Y"/>
    /// /<see cref="CoopCommand.Z"/> inside the current area.</summary>
    Teleport,

    /// <summary>Move the local player to a peer, named in <see cref="CoopCommand.Address"/>.</summary>
    GoTo,

    /// <summary>Put a blueprint on the floor at the local player's feet, where the item
    /// channel will replicate it to everyone.</summary>
    DropItem,

    /// <summary>List the staging cheats and the retail console cheats worth knowing.</summary>
    Cheats,

    /// <summary>
    /// List the live items this instance can see, with their object ids and per-instance
    /// state. <see cref="CoopCommand.Amount"/> caps how many rows are printed.
    ///
    /// The point is picking a target: item identity can only be exercised by sending an item
    /// that genuinely differs from its blueprint, and nothing before this could say which of
    /// the hundreds of items in a loaded module those are.
    /// </summary>
    Items,

    /// <summary>Print one item's per-instance state; the id is in
    /// <see cref="CoopCommand.Address"/>. This is the READ-BACK half of an identity test —
    /// run it on the receiving instance against the object id its plugin log names.</summary>
    InspectItem,

    /// <summary>Poke one live item's per-instance state, so a sender has something worth
    /// sending. The id is in <see cref="CoopCommand.Address"/> and the fields in
    /// <see cref="CoopCommand.Item"/>.</summary>
    SetItem,
}

/// <summary>
/// Which of an item's per-instance fields a <see cref="CoopCommandKind.SetItem"/> line named.
/// Null means "leave it alone", which is a first-class answer here: a `setitem` that named
/// nothing is a pure read, and one that named a stack must not silently reset the charges.
/// </summary>
public readonly record struct ItemFields(
    uint? StackSize = null,
    uint? Charges = null,
    uint? MaxCharges = null,
    uint? Upgrades = null,
    bool? Identified = null)
{
    public bool Any =>
        StackSize is not null || Charges is not null || MaxCharges is not null
        || Upgrades is not null || Identified is not null;
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
    string Error = "",
    int Amount = 0,
    float X = 0f,
    float Y = 0f,
    float Z = 0f,
    ItemFields Item = default)
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

            // netsim off | status | loss <pct> | latency <ms> [jitter] | blackout <secs> [in|out|both]
            //
            // Validated here rather than in the dispatcher for the same reason `warp` is: a
            // typo in a fault-injection command must not silently arm nothing (or, worse,
            // something else) on a live session.
            case "netsim" or "net":
                return ParseNetSim(rest);

            case "who" or "peers":
                return new CoopCommand(CoopCommandKind.Who);

            case "where":
                return new CoopCommand(CoopCommandKind.Where);

            // ---- staging cheats -------------------------------------------------

            // A cheat that only ever applies to your own character REFUSES A TARGET rather
            // than ignoring one. "god malak" is a reasonable thing for a player to try, and
            // silently arming their own invulnerability instead would be a lie about what
            // just happened — so the argument is validated and the message names the rule.
            case "god":
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.God, Amount: -1);
                }

                return rest[0].ToLowerInvariant() switch
                {
                    "on" or "1" or "true" or "yes" => new CoopCommand(CoopCommandKind.God, Amount: 1),
                    "off" or "0" or "false" or "no" => new CoopCommand(CoopCommandKind.God, Amount: 0),
                    _ => new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "god applies to your own character only - use: god [on|off]"),
                };

            case "topup":
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.TopUp);
                }

                if (!int.TryParse(rest[0], out int hitPoints) || hitPoints <= 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a hit-point count - use: topup [n], or topup for full");
                }

                return new CoopCommand(CoopCommandKind.TopUp, Amount: hitPoints);

            case "standup" or "getup":
                if (rest.Length == 0)
                {
                    return new CoopCommand(CoopCommandKind.StandUp);
                }

                if (!int.TryParse(rest[0], out int reviveHitPoints) || reviveHitPoints <= 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a hit-point count - use: standup [n]");
                }

                return new CoopCommand(CoopCommandKind.StandUp, Amount: reviveHitPoints);

            case "slay":
                if (rest.Length == 0)
                {
                    // No target: the agent picks the nearest hostile from its own census.
                    return new CoopCommand(CoopCommandKind.Slay);
                }

                // Aiming this at a person is the mistake worth catching in the parser: the
                // plugin refuses it too (a phantom and a companion are both recognised
                // there), but a player who types "slay me" deserves the reason immediately
                // rather than a round trip.
                if (IsSelfWord(rest[0]))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "slay is for enemies - your own character is not a target");
                }

                if (!IsTargetToken(rest[0]))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is not a creature tag or object id");
                }

                return new CoopCommand(CoopCommandKind.Slay, Address: rest[0]);

            case "tp":
                if (rest.Length < 2)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "tp needs coordinates, e.g. tp 12.5 -30.0 (z optional)");
                }

                if (!TryParseCoordinate(rest[0], out float x) || !TryParseCoordinate(rest[1], out float y))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]} {rest[1]}' is not a coordinate pair");
                }

                float z = 0f;
                if (rest.Length > 2 && !TryParseCoordinate(rest[2], out z))
                {
                    return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[2]}' is not a height");
                }

                return new CoopCommand(CoopCommandKind.Teleport, X: x, Y: y, Z: z);

            case "goto":
                return rest.Length == 0
                    ? new CoopCommand(CoopCommandKind.Invalid, Error: "goto needs a player, e.g. goto Malak (or `who` to list)")
                    : new CoopCommand(CoopCommandKind.GoTo, Address: string.Join(' ', rest));

            case "dropitem":
                if (rest.Length == 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "dropitem needs a blueprint or a live item id, "
                            + "e.g. dropitem g_w_blstrpstl001 or dropitem 0x131");
                }

                // Two forms, and only the second can exercise item identity. A blueprint name
                // conjures a stock item — there is no instance to read a stack size or an
                // upgrade mask off, and inventing one would be indistinguishable from having
                // read a genuinely stock item. An object id names a LIVE item here, whose
                // real state travels with it.
                //
                // ⚠ An id must be spelled "#131" or "0x131", never a bare "131". Both a resref
                // and a decimal id can be digits, and guessing between them would silently
                // drop the wrong thing; the two forms that cannot be a resref are the two we
                // accept. ItemDropRequest does the parsing so the drop FILE and this verb
                // cannot disagree about what "#131" means (it is hex, because every id this
                // project logs is printed in hex).
                if (rest[0].StartsWith('#')
                    || rest[0].StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    return new CoopCommand(
                        CoopCommandKind.DropItem, Address: rest[0].ToLowerInvariant());
                }

                if (!IsResRef(rest[0], maxLength: 16))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"'{rest[0]}' is neither a blueprint resref (letters, digits, _; "
                            + "max 16) nor an object id (write one as 0x131 or #131)");
                }

                return new CoopCommand(CoopCommandKind.DropItem, Address: rest[0].ToLowerInvariant());

            case "items":
                // A cap rather than a page: the listing goes to the agent's log, where a
                // hundred rows costs nothing, but the on-screen overlay is a handful of lines
                // and a full module would scroll everything else off it.
                return new CoopCommand(
                    CoopCommandKind.Items,
                    Amount: rest.Length > 0 && int.TryParse(rest[0], out int cap) ? cap : 0);

            case "item":
                if (rest.Length == 0 || !TryParseItemId(rest[0], out uint inspectId))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: "item needs an object id written as 0x131 or #131 "
                            + "(`items` lists them)");
                }

                return new CoopCommand(CoopCommandKind.InspectItem, Address: $"#{inspectId:x}");

            case "setitem":
                return ParseSetItem(rest);

            case "cheats":
                return new CoopCommand(CoopCommandKind.Cheats);

            default:
                return new CoopCommand(CoopCommandKind.Unknown);
        }
    }

    /// <summary>
    /// Parses the <c>netsim</c> sub-grammar. Every branch either produces a fully-specified
    /// command or an <see cref="CoopCommandKind.Invalid"/> naming the correct shape — there is
    /// no "close enough" reading of a line that deliberately breaks the network.
    /// </summary>
    private static CoopCommand ParseNetSim(string[] rest)
    {
        const string usage =
            "netsim off | status | loss <pct> | latency <ms> [jitter] | blackout <seconds> [in|out|both]";

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.NetSim, Address: "status");
        }

        string sub = rest[0].ToLowerInvariant();
        string[] args = rest[1..];

        switch (sub)
        {
            case "off" or "none" or "clear":
                return new CoopCommand(CoopCommandKind.NetSim, Address: "off");

            case "status" or "stat":
                return new CoopCommand(CoopCommandKind.NetSim, Address: "status");

            case "loss" or "drop":
                if (args.Length == 0 || !double.TryParse(args[0], out double percent)
                    || percent < 0 || percent > 100)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim loss needs a percentage 0-100, e.g. netsim loss 30");
                }

                return new CoopCommand(CoopCommandKind.NetSim, Address: "loss", Password: string.Join(' ', args));

            case "latency" or "lag" or "delay":
                if (args.Length == 0 || !int.TryParse(args[0], out int latency) || latency < 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim latency needs milliseconds, e.g. netsim latency 120 40");
                }

                if (args.Length > 1 && (!int.TryParse(args[1], out int jitter) || jitter < 0))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim latency jitter must be milliseconds, e.g. netsim latency 120 40");
                }

                return new CoopCommand(CoopCommandKind.NetSim, Address: "latency", Password: string.Join(' ', args));

            case "blackout" or "cut" or "drop-all":
                if (args.Length == 0 || !double.TryParse(args[0], out double seconds) || seconds <= 0)
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim blackout needs seconds, e.g. netsim blackout 10");
                }

                if (args.Length > 1 && args[1].ToLowerInvariant() is not ("in" or "out" or "both"))
                {
                    return new CoopCommand(
                        CoopCommandKind.Invalid, Error: "netsim blackout direction must be in, out or both");
                }

                return new CoopCommand(CoopCommandKind.NetSim, Address: "blackout", Password: string.Join(' ', args));

            default:
                return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
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

    /// <summary>Words a player uses to mean themselves. Recognised so a self-aimed
    /// <c>slay</c> gets the rule explained instead of "that is not a tag".</summary>
    private static bool IsSelfWord(string value) =>
        value.Equals("me", StringComparison.OrdinalIgnoreCase)
        || value.Equals("self", StringComparison.OrdinalIgnoreCase)
        || value.Equals("myself", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a token that could name a creature: a resref-shaped tag, or an object id
    /// written as <c>0x2A</c>, <c>#42</c> or <c>42</c>. Ids are accepted as text and resolved
    /// later — the parser's job is to reject a line that cannot possibly name anything.
    /// </summary>
    private static bool IsTargetToken(string value) => TryParseObjectId(value, out _) || IsResRef(value, maxLength: 31);

    /// <summary>
    /// Parses an object id in the three spellings a person types. Public because the agent
    /// resolves the same token against the world and must agree with the parser about what
    /// counts as an id.
    /// </summary>
    public static bool TryParseObjectId(string value, out uint objectId)
    {
        objectId = 0;
        if (value.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<char> span = value.AsSpan();
        if (span[0] == '#')
        {
            span = span[1..];
        }

        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                span[2..], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out objectId);
        }

        return uint.TryParse(span, out objectId);
    }

    /// <summary>
    /// Parses an ITEM object id, which — unlike a creature target — must be written in hex
    /// with an explicit marker: <c>0x131</c> or <c>#131</c>.
    ///
    /// The strictness is deliberate and it is about copy-paste. Every id these verbs deal in
    /// arrives from a log line that printed it as <c>0x131</c>, and a bare <c>131</c> read as
    /// decimal names object 0x83 — a real, different object that will usually resolve. A verb
    /// whose whole job is to write into an object must not have a spelling that silently aims
    /// at the wrong one.
    /// </summary>
    public static bool TryParseItemId(string value, out uint objectId)
    {
        objectId = 0;

        ReadOnlySpan<char> span = value.AsSpan();
        if (span.Length > 0 && span[0] == '#')
        {
            span = span[1..];
        }

        if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            span = span[2..];
        }
        else if (value.Length == 0 || value[0] != '#')
        {
            // Neither marker: refuse rather than guess a base.
            return false;
        }

        return span.Length > 0
            && uint.TryParse(
                span, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out objectId)
            && objectId != 0;
    }

    /// <summary>
    /// Parses <c>setitem &lt;id&gt; [stack=N] [charges=N] [max=N] [upgrades=0xN]
    /// [identified=0|1]</c>.
    ///
    /// Named arguments rather than positional ones because "leave this field alone" has to be
    /// expressible: the verb's other job is to poke ONE field of an item and see it change,
    /// and a positional form would make every unnamed field a silent overwrite. A bare
    /// <c>setitem &lt;id&gt;</c> names nothing and is therefore a pure read.
    /// </summary>
    private static CoopCommand ParseSetItem(string[] rest)
    {
        if (rest.Length == 0 || !TryParseItemId(rest[0], out uint objectId))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "setitem needs an object id written as 0x131 or #131, then "
                    + "stack=N charges=N max=N upgrades=0xN identified=0|1");
        }

        ItemFields fields = default;

        for (int i = 1; i < rest.Length; i++)
        {
            string token = rest[i];
            int equals = token.IndexOf('=');
            if (equals <= 0 || equals == token.Length - 1)
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{token}' is not a field=value pair "
                        + "(stack, charges, max, upgrades, identified)");
            }

            string name = token[..equals].ToLowerInvariant();
            string value = token[(equals + 1)..];

            if (!TryParseFieldValue(value, out uint number))
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid, Error: $"'{value}' is not a number (or 0x-number)");
            }

            switch (name)
            {
                case "stack" or "stacksize":
                    // Zero is refused HERE rather than clamped later: the engine never makes a
                    // stack of nothing, and an item with one is unpickable. A caller who typed
                    // it meant something else.
                    if (number is 0 or > ushort.MaxValue)
                    {
                        return new CoopCommand(
                            CoopCommandKind.Invalid,
                            Error: $"stack={number} is outside 1..{ushort.MaxValue}");
                    }

                    fields = fields with { StackSize = number };
                    break;

                case "charges":
                    if (number > byte.MaxValue)
                    {
                        return new CoopCommand(
                            CoopCommandKind.Invalid, Error: "charges is a byte (0..255)");
                    }

                    fields = fields with { Charges = number };
                    break;

                case "max" or "maxcharges":
                    if (number > byte.MaxValue)
                    {
                        return new CoopCommand(
                            CoopCommandKind.Invalid, Error: "max charges is a byte (0..255)");
                    }

                    fields = fields with { MaxCharges = number };
                    break;

                case "upgrades" or "upgrade":
                    fields = fields with { Upgrades = number };
                    break;

                case "identified" or "id":
                    fields = fields with { Identified = number != 0 };
                    break;

                default:
                    return new CoopCommand(
                        CoopCommandKind.Invalid,
                        Error: $"unknown field '{name}' "
                            + "(stack, charges, max, upgrades, identified)");
            }
        }

        return new CoopCommand(
            CoopCommandKind.SetItem, Address: $"#{objectId:x}", Item: fields);
    }

    /// <summary>A field value: decimal, or hex with an explicit 0x. Unlike an object id a
    /// bare number here is unambiguous — a stack of 20 is 20 — so decimal is the default.</summary>
    private static bool TryParseFieldValue(string value, out uint number)
    {
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return uint.TryParse(
                value.AsSpan(2), System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out number);
        }

        return uint.TryParse(value, out number);
    }

    /// <summary>Invariant-culture float parse: a console line is typed with a dot whatever
    /// the machine's locale says, and a comma-decimal locale would otherwise read "12.5" as
    /// 125.</summary>
    private static bool TryParseCoordinate(string value, out float result) =>
        float.TryParse(
            value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out result);

    /// <summary>One-line help listing the commands, for the overlay and dashboard.</summary>
    public const string HelpText =
        "commands: servers | connect <ip|#n> [password] | password <pw> | host | disconnect | reconnect | say <msg> | clear | status | warp <module> | who | where | cheats | netsim <off|status|loss|latency|blackout> | help";

    /// <summary>
    /// The staging-cheat listing. Two halves on purpose: what co-op adds, and what the
    /// retail console already does correctly in co-op — because the second half is the part
    /// a player cannot discover on their own and the part we deliberately did not rewrite.
    /// </summary>
    public static readonly string[] CheatsText =
    [
        "co-op staging cheats (authority-aware):",
        "  god [on|off]        your damage floors at 1 hp; yours only",
        "  topup [n]           your hit points to n, or to full",
        "  standup [n]         stand yourself up out of the downed state",
        "  slay [tag|id]       kill an enemy; a client routes it to the host",
        "  tp <x> <y> [z]      move yourself inside this area",
        "  goto <player>       move yourself to a peer (same module)",
        "  dropitem <resref>   drop a blueprint; the item channel shares it",
        "  dropitem 0x131      drop a copy of a LIVE item, carrying its real state",
        "  items [n]           list live items here: id, resref, holder, stack/charges/upgrades",
        "  item 0x131          what one item's per-instance state actually reads",
        "  setitem 0x131 stack=20 charges=3 upgrades=0x5 identified=0",
        "                      poke those fields, so there is something worth sending",
        // Listed here because it is the same family — a verb that exists to stage a test —
        // and discoverability is the point of this listing. It is owned by the network
        // fault-injection work, which registers its own catalog entry and parser case; this
        // is only the line that tells a player it is there.
        "test harness:",
        "  netsim <off|status|loss <pct>|latency <ms> <jitter>|blackout <s> [in|out|both]>",
        "retail console cheats, already co-op-safe (they touch only your game):",
        "  giveitem <resref> | givecredits <n> | addexp <n> | addlevel | heal",
        "  revealmap | infiniteuses | invulnerability | whereami | bright | turbo",
    ];
}

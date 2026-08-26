namespace KotorCoop.Session;

/// <summary>
/// The read/diagnostic verbs used to localise a live desync: netsim (fault injection), census,
/// resolve, firescript, movenear.
/// </summary>
public static partial class ConsoleCommand
{
    // An optional id turns the dump into a single question about one creature, which is what
    // an argument about a specific id actually needs.
    private static CoopCommand ParseCensus(string[] rest) =>
        new(CoopCommandKind.Census, Address: rest.Length > 0 ? rest[0] : null);

    private static CoopCommand ParseResolve(string[] rest) =>
        rest.Length == 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "resolve <id>  (e.g. resolve 0x35)")
            : new CoopCommand(CoopCommandKind.ResolveObject, Address: rest[0]);

    // firescript <id>              - peek slot +0x260, does not fire
    // firescript <id> fire         - fires +0x260 (OnCombatRoundEnd, k_def_combend01)
    // firescript <id> fire 270     - fires +0x270 instead (an OnNotice candidate - ties Amount
    //                                to the slot offset directly rather than adding a new field)
    private static CoopCommand ParseFireScript(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: "firescript <id> [fire [270]]  (e.g. firescript 0x35)");
        }

        bool fire = rest.Length > 1 && rest[1].Equals("fire", StringComparison.OrdinalIgnoreCase);
        int slotAmount = fire ? 1 : 0;
        if (fire && rest.Length > 2 && rest[2] == "270")
        {
            slotAmount = 0x270;
        }

        return new CoopCommand(CoopCommandKind.FireScript, Address: rest[0], Amount: slotAmount);
    }

    // movenear <id>  - TEMPORARY DIAGNOSTIC, snaps <id> near a known hostile.
    private static CoopCommand ParseMoveNear(string[] rest) =>
        rest.Length == 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: "movenear <id>  (e.g. movenear 0x127)")
            : new CoopCommand(CoopCommandKind.MoveNear, Address: rest[0]);

    // netsim off | status | loss <pct> | latency <ms> [jitter] | blackout <secs> [in|out|both]
    //
    // Validated here rather than in the dispatcher for the same reason `warp` is: a typo in a
    // fault-injection command must not silently arm nothing (or, worse, something else) on a
    // live session.
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
}

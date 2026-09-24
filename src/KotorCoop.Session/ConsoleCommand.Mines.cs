namespace KotorCoop.Session;

/// <summary>Developer-only real-engine floor-mine action request.</summary>
public static partial class ConsoleCommand
{
    private static CoopCommand ParseMineAction(string[] rest)
    {
        const string usage = "mineaction <recover|disarm> <GIT mine id (0xN or #N)>";
        if (rest.Length != 2)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        string action = rest[0].ToLowerInvariant();
        if (action is not ("recover" or "disarm"))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not recover or disarm; usage: {usage}");
        }

        if (!TryParseItemId(rest[1], out uint objectId) || objectId is 0 or > 0xFFFF)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[1]}' is not a deterministic GIT object id in 1..0xFFFF; usage: {usage}");
        }

        return new CoopCommand(CoopCommandKind.MineAction, Address: action, Amount: (int)objectId);
    }

    /// <summary>Default DCs for <c>placemine</c>: low enough that an unskilled actor can see and
    /// disarm the test mine, so the live recipe does not depend on Demolitions ranks.</summary>
    public const int PlaceMineDefaultDc = 10;

    // placemine <traps.2da row 0-254> [detectDC] [disarmDC]
    private static CoopCommand ParsePlaceMine(string[] rest)
    {
        const string usage = "placemine <traps.2da row> [detect DC] [disarm DC]  e.g. placemine 3 (minor fragmentation mine)";
        if (rest.Length is < 1 or > 3
            || !int.TryParse(rest[0], out int row) || row is < 0 or > 254)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        int detect = PlaceMineDefaultDc;
        int disarm = PlaceMineDefaultDc;
        if ((rest.Length >= 2 && (!int.TryParse(rest[1], out detect) || detect is < 0 or > 255))
            || (rest.Length == 3 && (!int.TryParse(rest[2], out disarm) || disarm is < 0 or > 255)))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"DCs are 0-255; usage: {usage}");
        }

        return new CoopCommand(
            CoopCommandKind.PlaceMine, Amount: row, EffectMagnitude: (uint)detect | ((uint)disarm << 16));
    }

    private static CoopCommand ParseMineState(string[] rest) =>
        rest.Length == 0
            ? new CoopCommand(CoopCommandKind.MineState)
            : new CoopCommand(CoopCommandKind.Invalid, Error: "minestate takes no arguments");

    // placedmineaction <recover|disarm> <session serial>
    private static CoopCommand ParsePlacedMineAction(string[] rest)
    {
        const string usage = "placedmineaction <recover|disarm> <session mine serial (see minestate)>";
        if (rest.Length != 2)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        string action = rest[0].ToLowerInvariant();
        if (action is not ("recover" or "disarm"))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not recover or disarm; usage: {usage}");
        }

        string serialText = rest[1].TrimStart('#');
        if (!int.TryParse(serialText, out int serial) || serial <= 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[1]}' is not a session mine serial; usage: {usage}");
        }

        return new CoopCommand(CoopCommandKind.PlacedMineAction, Address: action, Amount: serial);
    }
}

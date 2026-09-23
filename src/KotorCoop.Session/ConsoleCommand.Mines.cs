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
}

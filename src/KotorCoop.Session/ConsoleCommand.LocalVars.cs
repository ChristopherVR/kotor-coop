using System.Globalization;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

public static partial class ConsoleCommand
{
    // localvar <get|set|watch> <tag|0xid> <b|n> <slot> [value]  - LIVE-TEST PRIMITIVE for
    // local_var_sync, see CoopCommandKind.LocalVar and docs/research/quest-sync.md §8.3.
    private static CoopCommand ParseLocalVar(string[] rest)
    {
        const string usage = "localvar <get|set|watch> <tag|0xid> <b|n> <slot> [value]  e.g. "
            + "localvar get dan13_belaya b 10, localvar set 0x4 b 10 1, localvar watch dan13_belaya b 10";

        if (rest.Length < 4)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        string op = rest[0].ToLowerInvariant();
        if (op is not ("get" or "set" or "watch"))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not get, set or watch - {usage}");
        }

        int expected = op == "set" ? 5 : 4;
        if (rest.Length != expected)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        string target = rest[1];
        uint objectId = 0;
        string tag = string.Empty;
        if (target.StartsWith('#') || target.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseItemId(target, out objectId) || !LocalVarSyncRule.IsGitObjectId(objectId))
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{target}' is not a deterministic GIT object id in 1..0xFFFF - {usage}");
            }
        }
        else if (IsResRef(target, maxLength: 31))
        {
            tag = target;
        }
        else
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{target}' is not a tag or 0xN id - {usage}");
        }

        LocalVarKind kind;
        switch (rest[2].ToLowerInvariant())
        {
            case "b":
                kind = LocalVarKind.Boolean;
                break;
            case "n":
                kind = LocalVarKind.Number;
                break;
            default:
                return new CoopCommand(CoopCommandKind.Invalid, Error: $"'{rest[2]}' is not b (boolean) or n (number) - {usage}");
        }

        if (!int.TryParse(rest[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out int slot)
            || !LocalVarSyncRule.IsValidSlot(kind, slot))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[3]}' is not a valid slot (booleans 0-95, numbers 0-7) - {usage}");
        }

        int value = 0;
        if (op == "set")
        {
            string token = rest[4];
            if (token.Equals("true", StringComparison.OrdinalIgnoreCase) || token.Equals("on", StringComparison.OrdinalIgnoreCase))
            {
                value = 1;
            }
            else if (token.Equals("false", StringComparison.OrdinalIgnoreCase) || token.Equals("off", StringComparison.OrdinalIgnoreCase))
            {
                value = 0;
            }
            else if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
                || (kind == LocalVarKind.Number && value is < sbyte.MinValue or > sbyte.MaxValue))
            {
                return new CoopCommand(
                    CoopCommandKind.Invalid,
                    Error: $"'{token}' is not a value (booleans true/false/1/0, numbers -128..127) - {usage}");
            }
        }

        return new CoopCommand(
            CoopCommandKind.LocalVar,
            Address: LocalVarSyncRule.FormatRequest(op, kind, slot, tag),
            Password: objectId.ToString(CultureInfo.InvariantCulture),
            Amount: value);
    }
}

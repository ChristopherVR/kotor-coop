namespace KotorCoop.Session;

/// <summary>The item-identity staging verbs: dropitem, items, item, setitem, giveloot.</summary>
public static partial class ConsoleCommand
{
    private static CoopCommand ParseDropItem(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "dropitem needs a blueprint or a live item id, "
                    + "e.g. dropitem g_w_blstrpstl001 or dropitem 0x131");
        }

        // Two forms, and only the second can exercise item identity. A blueprint name
        // conjures a stock item — there is no instance to read a stack size or an upgrade
        // mask off, and inventing one would be indistinguishable from having read a genuinely
        // stock item. An object id names a LIVE item here, whose real state travels with it.
        //
        // ⚠ An id must be spelled "#131" or "0x131", never a bare "131". Both a resref and a
        // decimal id can be digits, and guessing between them would silently drop the wrong
        // thing; the two forms that cannot be a resref are the two we accept.
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
    }

    private static CoopCommand ParseItems(string[] rest) =>
        // A cap rather than a page: the listing goes to the agent's log, where a hundred rows
        // costs nothing, but the on-screen overlay is a handful of lines and a full module
        // would scroll everything else off it.
        new(CoopCommandKind.Items,
            Amount: rest.Length > 0 && int.TryParse(rest[0], out int cap) ? cap : 0);

    private static CoopCommand ParseItem(string[] rest)
    {
        if (rest.Length == 0 || !TryParseItemId(rest[0], out uint inspectId))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "item needs an object id written as 0x131 or #131 "
                    + "(`items` lists them)");
        }

        return new CoopCommand(CoopCommandKind.InspectItem, Address: $"#{inspectId:x}");
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

    /// <summary>
    /// Parses <c>giveloot &lt;owner id&gt; &lt;resref&gt; [stack]</c>. The id is the OWNER's
    /// enemy object id — the one every peer's census agrees on — not whatever local id this
    /// instance's own materialisation table spawned the stand-in under; only the receiving
    /// plugin can translate one to the other, which is why it takes the id rather than a name.
    /// </summary>
    private static CoopCommand ParseGiveLoot(string[] rest)
    {
        if (rest.Length < 2 || !TryParseItemId(rest[0], out uint ownerId)
            || !IsResRef(rest[1], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "giveloot needs the owner's enemy id and a resref, e.g. "
                    + "giveloot 0x35 g_w_blstrpstl001 [stack]");
        }

        int stack = 1;
        if (rest.Length > 2 && (!int.TryParse(rest[2], out stack) || stack <= 0))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"stack '{rest[2]}' is not a positive number");
        }

        return new CoopCommand(
            CoopCommandKind.GiveLoot, Address: $"#{ownerId:x}", Password: rest[1], Amount: stack);
    }
}

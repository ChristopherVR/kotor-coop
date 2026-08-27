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

    // scriptgive <resref> [stack]  - LIVE-TEST PRIMITIVE, see CoopCommandKind.ScriptGive.
    // Deliberately simpler than giveloot: always the local player, never an owner id.
    private static CoopCommand ParseScriptGive(string[] rest)
    {
        if (rest.Length < 1 || !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "scriptgive needs a resref, e.g. scriptgive g_w_blstrpstl001 [stack]");
        }

        int stack = 1;
        if (rest.Length > 1 && (!int.TryParse(rest[1], out stack) || stack <= 0))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"stack '{rest[1]}' is not a positive number");
        }

        return new CoopCommand(CoopCommandKind.ScriptGive, Address: rest[0], Amount: stack);
    }

    // pooladd <resref> [stack]  - LIVE-TEST PRIMITIVE, see CoopCommandKind.PoolAdd.
    // Same shape as scriptgive: a bare resref/stack, always the shared party pool.
    private static CoopCommand ParsePoolAdd(string[] rest)
    {
        if (rest.Length < 1 || !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "pooladd needs a resref, e.g. pooladd g_w_blstrpstl001 [stack]");
        }

        int stack = 1;
        if (rest.Length > 1 && (!int.TryParse(rest[1], out stack) || stack <= 0))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"stack '{rest[1]}' is not a positive number");
        }

        return new CoopCommand(CoopCommandKind.PoolAdd, Address: rest[0], Amount: stack);
    }

    // poolremove <resref> [stack]  - LIVE-TEST PRIMITIVE, see CoopCommandKind.PoolRemove.
    private static CoopCommand ParsePoolRemove(string[] rest)
    {
        if (rest.Length < 1 || !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "poolremove needs a resref, e.g. poolremove g_w_blstrpstl001 [stack]");
        }

        int stack = 1;
        if (rest.Length > 1 && (!int.TryParse(rest[1], out stack) || stack <= 0))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"stack '{rest[1]}' is not a positive number");
        }

        return new CoopCommand(CoopCommandKind.PoolRemove, Address: rest[0], Amount: stack);
    }

    // grantxp <id|me> <amount>  - LIVE-TEST PRIMITIVE, see CoopCommandKind.GrantXp.
    private static CoopCommand ParseGrantXp(string[] rest)
    {
        const string usage = "grantxp <id|me> <amount>  e.g. grantxp me 500, or grantxp 0x42 -100";

        if (rest.Length != 2)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        string token = IsSelfWord(rest[0]) ? "0" : rest[0];
        if (!TryParseObjectId(token, out _))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not an object id or 'me' - {usage}");
        }

        if (!int.TryParse(rest[1], out int amount))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[1]}' is not a signed xp amount - {usage}");
        }

        return new CoopCommand(CoopCommandKind.GrantXp, Address: token, Amount: amount);
    }

    // equip <id|me> <resref> [slot]  - LIVE-TEST PRIMITIVE, see CoopCommandKind.Equip.
    private static CoopCommand ParseEquip(string[] rest)
    {
        const string usage = "equip <id|me> <resref> [slot]  slot: 1 body, 4 right weapon "
            + "(default), 5 left weapon - e.g. equip 0x42 g_w_dblsbr01 4";

        if (rest.Length is < 2 or > 3)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        string token = IsSelfWord(rest[0]) ? "0" : rest[0];
        if (!TryParseObjectId(token, out _))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[0]}' is not an object id or 'me' - {usage}");
        }

        if (!IsResRef(rest[1], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[1]}' is not a resref - {usage}");
        }

        int slot = 0;
        if (rest.Length == 3 && (!int.TryParse(rest[2], out slot) || slot <= 0))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid, Error: $"'{rest[2]}' is not a valid inventory slot - {usage}");
        }

        return new CoopCommand(CoopCommandKind.Equip, Address: token, Password: rest[1], Amount: slot);
    }

    // grantgold <amount>  - LIVE-TEST PRIMITIVE, see CoopCommandKind.GrantGold.
    private static CoopCommand ParseGrantGold(string[] rest)
    {
        if (rest.Length != 1 || !int.TryParse(rest[0], out int amount))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "grantgold needs one signed amount, e.g. grantgold 500 or grantgold -200");
        }

        return new CoopCommand(CoopCommandKind.GrantGold, Amount: amount);
    }
}

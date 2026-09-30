namespace KotorCoop.Session;

/// <summary>The door/placeable staging verbs: usedoor, crossdoor, opendoor, useplaceable,
/// closeplaceable.</summary>
public static partial class ConsoleCommand
{
    // The other half of the movie-staging instrument, and spelled in full for the same
    // reason: it asks the ENGINE to start a module transition, which stores the outgoing
    // area. Anything the plugin has spawned and not destroyed by then is written into the
    // save permanently.
    private static CoopCommand ParseUseDoor(string[] rest)
    {
        if (rest.Length == 0)
        {
            // No argument is a legitimate form — "the nearest module transition door" — and
            // it is SAFE because the nearest-door search refuses every door that is not a
            // transition. A bare usedoor can do nothing else.
            return new CoopCommand(CoopCommandKind.UseDoor);
        }

        if (rest.Length > 1)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "usedoor takes one door, e.g. usedoor end_door10 (or bare, for the nearest)");
        }

        if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a door tag or object id");
        }

        return new CoopCommand(CoopCommandKind.UseDoor, Address: rest[0]);
    }

    // The event-posting instrument, same target shape as usedoor: posts the door-crossing
    // event directly rather than queuing a walk — see CoopCommandKind.CrossDoor.
    private static CoopCommand ParseCrossDoor(string[] rest)
    {
        if (rest.Length == 0)
        {
            // Same "nearest transition door" default as usedoor, and safe for the same
            // reason: the nearest-door search refuses every door that is not a transition.
            return new CoopCommand(CoopCommandKind.CrossDoor);
        }

        if (rest.Length > 1)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "crossdoor takes one door, e.g. crossdoor end_door10 (or bare, for the nearest)");
        }

        if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a door tag or object id");
        }

        return new CoopCommand(CoopCommandKind.CrossDoor, Address: rest[0]);
    }

    // An ordinary door, deliberately not gated to LinkedToModule the way usedoor is — see
    // CoopCommandKind.OpenDoor.
    private static CoopCommand ParseOpenDoor(string[] rest)
    {
        if (rest.Length > 1)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "opendoor takes one door, e.g. opendoor end_door01 (or bare, for the nearest)");
        }

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.OpenDoor);
        }

        if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a door tag or object id");
        }

        return new CoopCommand(CoopCommandKind.OpenDoor, Address: rest[0]);
    }

    private static CoopCommand ParseCloseDoor(string[] rest)
    {
        CoopCommand parsed = ParseOpenDoor(rest);
        return parsed.Kind == CoopCommandKind.Invalid
            ? parsed with { Error = parsed.Error.Replace("opendoor", "closedoor", StringComparison.Ordinal) }
            : parsed with { Kind = CoopCommandKind.CloseDoor };
    }

    private static CoopCommand ParseSetLock(string[] rest)
    {
        if (rest.Length != 2 || !TryParseObjectId(rest[0], out uint id) || id is 0 or uint.MaxValue
            || !(rest[1].Equals("on", StringComparison.OrdinalIgnoreCase) || rest[1].Equals("off", StringComparison.OrdinalIgnoreCase)))
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: "setlock needs a real object id and on|off, e.g. setlock 0xC6 on");
        }

        return new CoopCommand(CoopCommandKind.SetLock, Address: rest[0], Amount: rest[1].Equals("on", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
    }

    // CONTAINER-SYNC STAGING VERB: the same shape as opendoor, for a placeable — see
    // CoopCommandKind.UsePlaceable.
    private static CoopCommand ParseUsePlaceable(string[] rest)
    {
        if (rest.Length > 1)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "useplaceable takes one placeable, e.g. useplaceable end_chest01 "
                    + "(or bare, for the nearest container)");
        }

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.UsePlaceable);
        }

        if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a placeable tag or object id");
        }

        return new CoopCommand(CoopCommandKind.UsePlaceable, Address: rest[0]);
    }

    // loottake [tag|id]  - LIVE-TEST PRIMITIVE, same target shape as useplaceable — see
    // CoopCommandKind.LootTake.
    private static CoopCommand ParseLootTake(string[] rest)
    {
        if (rest.Length > 1)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "loottake takes one container, e.g. loottake end_chest01 "
                    + "(or bare, for the nearest container)");
        }

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.LootTake);
        }

        if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a placeable tag or object id");
        }

        return new CoopCommand(CoopCommandKind.LootTake, Address: rest[0]);
    }

    // CONTAINER-SYNC LIVE-PASS ADDITION: the close-side counterpart, same shape — see
    // CoopCommandKind.ClosePlaceable.
    private static CoopCommand ParseClosePlaceable(string[] rest)
    {
        if (rest.Length > 1)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: "closeplaceable takes one placeable, e.g. closeplaceable end_chest01 "
                    + "(or bare, for the nearest container)");
        }

        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.ClosePlaceable);
        }

        if (!TryParseObjectId(rest[0], out _) && !IsResRef(rest[0], maxLength: 31))
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not a placeable tag or object id");
        }

        return new CoopCommand(CoopCommandKind.ClosePlaceable, Address: rest[0]);
    }
}

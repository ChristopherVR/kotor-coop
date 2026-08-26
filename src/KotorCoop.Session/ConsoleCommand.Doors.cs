namespace KotorCoop.Session;

/// <summary>The door/placeable staging verbs: usedoor, opendoor, useplaceable, closeplaceable.</summary>
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

namespace KotorCoop.Session;

/// <summary>
/// The <c>attack</c> live-test primitive (docs/research/combat-participation.md §15): makes
/// the LOCAL PLAYER'S own creature attack a target through the engine's own action path — a
/// real queued <c>ActionAttack</c>, the same route a player's click on a hostile produces.
/// Built because the live rig cannot click (the sticky-aggro test, "the enemy should stay on
/// the joiner", has never run) and because a queued Force power cast (<c>cast</c>,
/// effect-sync.md §14.8/§14.9) is only promoted to a real Force-point-spending cast by the
/// caster's OWN active combat round, so a rig that cannot fight cannot verify a cast either.
/// </summary>
public static partial class ConsoleCommand
{
    /// <summary>
    /// Parses <c>attack &lt;0xid&gt;</c> (order the local player to attack that object) or
    /// <c>attack stop</c> (clear the local player's queued attack). See
    /// <see cref="CoopCommandKind.Attack"/>.
    /// </summary>
    private static CoopCommand ParseAttack(string[] rest)
    {
        const string usage = "attack <0xid>  (dev-only: local player attacks via a real queued "
            + "ActionAttack) | attack ranged <0xid> (native client attack without range staging) | attack stop";

        if (rest.Length == 2 && rest[0].Equals("ranged", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseObjectId(rest[1], out uint rangedTarget) || rangedTarget == 0)
                return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
            return new CoopCommand(CoopCommandKind.Attack, Address: rest[1], Amount: 1);
        }

        if (rest.Length != 1)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (string.Equals(rest[0], "stop", StringComparison.OrdinalIgnoreCase))
        {
            return new CoopCommand(CoopCommandKind.Attack, Address: "stop");
        }

        if (!TryParseObjectId(rest[0], out uint objectId) || objectId == 0)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not an object id (try 0x35) or 'stop' - {usage}");
        }

        return new CoopCommand(CoopCommandKind.Attack, Address: rest[0]);
    }
}

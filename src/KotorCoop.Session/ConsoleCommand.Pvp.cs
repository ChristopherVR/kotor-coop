namespace KotorCoop.Session;

/// <summary>
/// The duel verbs (docs/research/pvp-plan.md): <c>duel &lt;name&gt;</c>, <c>duel off</c>,
/// <c>duel on</c>, <c>accept</c>, <c>decline</c>, <c>yield</c>. Plain words a player would type
/// in answer to "Aria challenges you to a duel. Type accept or decline."
/// </summary>
public static partial class ConsoleCommand
{
    private static CoopCommand ParseDuel(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: PvpText.UsageLine);
        }

        // `duel off` / `duel on` are the opt-out; anything else is a name (which may have
        // spaces: the rest of the line, like goto and autofollow).
        string name = string.Join(' ', rest);
        return new CoopCommand(CoopCommandKind.Duel, Address: name);
    }

    private static CoopCommand ParseArena(string[] rest) =>
        rest.Length == 0
            ? new CoopCommand(CoopCommandKind.Invalid, Error: PvpText.ArenaUsageLine)
            : new CoopCommand(CoopCommandKind.DuelArena, Address: string.Join(' ', rest));

    private static CoopCommand ParseDuelAccept(string[] rest) => new(CoopCommandKind.DuelAccept);

    private static CoopCommand ParseDuelDecline(string[] rest) => new(CoopCommandKind.DuelDecline);

    private static CoopCommand ParseDuelYield(string[] rest) => new(CoopCommandKind.DuelYield);
}

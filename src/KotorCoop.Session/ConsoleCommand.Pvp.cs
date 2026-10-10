using System.Globalization;

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

    /// <summary>
    /// <c>chungus</c> starts the boss fight. The other forms are staging, for trying one piece on
    /// this game alone: <c>chungus spawn</c> puts him in front of the player, <c>chungus anim
    /// &lt;id&gt;</c> plays an animation on him, <c>chungus sound &lt;name&gt;</c> plays a sound
    /// file, <c>chungus power &lt;1-10&gt;</c> plays a power out on this player, <c>chungus
    /// remove</c> takes him away. The piece is in <see cref="CoopCommand.Address"/>.
    /// </summary>
    private static CoopCommand ParseChungus(string[] rest)
    {
        if (rest.Length == 0)
        {
            return new CoopCommand(CoopCommandKind.Chungus);
        }

        string piece = rest[0].ToLowerInvariant();
        switch (piece)
        {
            case "spawn" or "remove" when rest.Length == 1:
                return new CoopCommand(CoopCommandKind.Chungus, Address: piece);

            case "anim" or "power" when rest.Length == 2
                && int.TryParse(rest[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                && number is > 0 and <= ushort.MaxValue:
                return new CoopCommand(CoopCommandKind.Chungus, Address: piece, Amount: number);

            case "sound" when rest.Length == 2 && rest[1].Length <= 16:
                return new CoopCommand(CoopCommandKind.Chungus, Address: piece, Password: rest[1]);

            default:
                return new CoopCommand(CoopCommandKind.Invalid, Error: PvpText.BossUsageLine);
        }
    }

    private static CoopCommand ParseDuelAccept(string[] rest) => new(CoopCommandKind.DuelAccept);

    private static CoopCommand ParseDuelDecline(string[] rest) => new(CoopCommandKind.DuelDecline);

    private static CoopCommand ParseDuelYield(string[] rest) => new(CoopCommandKind.DuelYield);
}

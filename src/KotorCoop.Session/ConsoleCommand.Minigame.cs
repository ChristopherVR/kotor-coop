namespace KotorCoop.Session;

/// <summary>The <c>mgkill</c> live-test primitive (docs/research/minigames.md): a forced
/// turret/gunnery minigame ambush the unattended rig cannot finish by actually shooting.</summary>
public static partial class ConsoleCommand
{
    /// <summary>
    /// Parses <c>mgkill &lt;all|n&gt;</c>. <c>all</c> kills every currently-alive minigame
    /// enemy fighter (repeat the command if a later wave spawns more); a bare count kills at
    /// most that many, oldest slot first.
    /// </summary>
    private static CoopCommand ParseMgKill(string[] rest)
    {
        const string usage = "mgkill <all|n>  (kills up to n - or all - currently alive minigame "
            + "enemy fighters, e.g. the m12ab Ebon Hawk turret ambush; repeat per wave)";

        if (rest.Length != 1)
        {
            return new CoopCommand(CoopCommandKind.Invalid, Error: usage);
        }

        if (string.Equals(rest[0], "all", StringComparison.OrdinalIgnoreCase))
        {
            return new CoopCommand(CoopCommandKind.MgKill, Amount: -1);
        }

        if (!int.TryParse(rest[0], out int count) || count < 1)
        {
            return new CoopCommand(
                CoopCommandKind.Invalid,
                Error: $"'{rest[0]}' is not 'all' or a positive count; usage: {usage}");
        }

        return new CoopCommand(CoopCommandKind.MgKill, Amount: count);
    }
}

using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>The remaining staging cheats that do not belong to a more specific area:
/// destroyobject, leader, recruit, and the `cheats` listing itself.</summary>
public sealed partial class CoopCommandDispatcher
{
    private void CheatsCommand(CoopCommand command)
    {
        foreach (string line in ConsoleCommand.CheatsText)
        {
            Reply(line);
        }
    }

    /// <summary>
    /// STAGING VERB: destroys a creature outright, through the SAME engine call
    /// <c>LivenessReconcileController</c>'s own Destroy action already wraps
    /// (docs/research/creature-liveness.md §5). Built because <c>slay</c> only ever calls
    /// <c>Odyssey.KillCreature</c>, and nothing else could put a creature into the "Gone"
    /// state.
    ///
    /// The parser already refused anything that is not a real object id (no tag form, no
    /// "nearest" fallback — see <c>ConsoleCommand</c>'s <c>destroyobject</c> case), so this is
    /// a straight hand-off; the authority decision (host-only, refuses a player, phantom,
    /// companion or plot creature) lives in the plugin, the same split <see cref="Slay"/> uses
    /// and for the same reason — only the plugin can see the phantom table, the party roster
    /// and whether this instance owns the fight.
    /// </summary>
    private void DestroyObject(string token)
    {
        if (!ConsoleCommand.TryParseObjectId(token, out uint objectId))
        {
            Reply($"destroyobject: '{token}' is not an object id");
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.DestroyObject, Target: objectId));
    }

    /// <summary>
    /// DEV-ONLY TEST PRIMITIVE: switches this instance's controlled character to a
    /// party-roster companion, or back to the PC — the parser already refused anything but a
    /// valid slot (0-8) or <c>pc</c>/<c>me</c>, so this is a straight hand-off. Built because
    /// injected keystrokes never reach this game, so a human Tabbing/portrait-clicking onto a
    /// companion before connecting could not be automated any other way. See
    /// docs/research/control-return.md.
    /// </summary>
    private void Leader(int amount) => SendCheat(new CheatRequest(CheatKinds.Leader, Amount: amount));

    private void Recruit(int amount, string tag) =>
        SendCheat(new CheatRequest(CheatKinds.Recruit, Amount: amount, Text: tag));
}

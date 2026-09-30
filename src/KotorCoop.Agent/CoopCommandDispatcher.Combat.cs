using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>The <c>attack</c> live-test verb (docs/research/combat-participation.md §15):
/// orders the local player's own creature to attack a target, or cancels its active combat round and queued attacks.</summary>
public sealed partial class CoopCommandDispatcher
{
    /// <summary>
    /// Runs <c>Odyssey.AttackTarget</c> on the plugin — a real queued <c>ActionAttack</c> for
    /// the local player, the same route a click on a hostile produces. <c>Address == "stop"</c>
    /// (<see cref="CoopCommandKind.Attack"/> parses the literal word) sends Target 0, which the
    /// plugin reads as "cancel active combat and queued attacks" rather than a real id — no live object id is
    /// ever 0.
    /// </summary>
    private void AttackCommand(CoopCommand command)
    {
        if (string.Equals(command.Address, "stop", StringComparison.OrdinalIgnoreCase))
        {
            SendCheat(new CheatRequest(CheatKinds.Attack, Target: 0));
            return;
        }

        if (!ConsoleCommand.TryParseObjectId(command.Address ?? string.Empty, out uint target) || target == 0)
        {
            Reply($"attack: '{command.Address}' is not an object id (try 0x35) or 'stop'");
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.Attack, Target: target));
    }
}

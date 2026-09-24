using System.Globalization;
using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    /// <summary>Forwards <c>localvar get|set|watch</c> to the plugin as
    /// <see cref="CheatKinds.LocalVar"/>. See <c>CoopCommandKind.LocalVar</c> and
    /// docs/research/quest-sync.md §8.3.</summary>
    private void QueueLocalVar(CoopCommand command)
    {
        _ = uint.TryParse(command.Password, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint objectId);
        SendCheat(new CheatRequest(
            CheatKinds.LocalVar, Target: objectId, Amount: command.Amount, Text: command.Address ?? string.Empty));
    }
}

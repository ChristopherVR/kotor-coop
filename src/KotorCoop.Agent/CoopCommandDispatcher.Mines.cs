using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

public sealed partial class CoopCommandDispatcher
{
    private void QueueMineAction(CoopCommand command)
    {
        if (!bridge.TryReadWorldLoadSerial(out uint serial)
            || !bridge.TryReadCurrentModule(out string module)
            || string.IsNullOrWhiteSpace(module)
            || !bridge.TryReadWorldLoadSerial(out uint afterSerial)
            || serial != afterSerial)
        {
            Reply("mineaction refused: current world identity is unavailable");
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.MineAction,
            Target: (uint)command.Amount, Amount: unchecked((int)serial),
            Text: $"{command.Address}|{module}"));
    }
}

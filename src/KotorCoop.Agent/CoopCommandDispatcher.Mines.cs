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
            Log?.Invoke("mineaction refused: current world identity is unavailable");
            Reply("mineaction: not ready yet - try again after the area finishes loading");
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.MineAction,
            Target: (uint)command.Amount, Amount: unchecked((int)serial),
            Text: $"{command.Address}|{module}"));
    }

    /// <summary><c>placedmineaction</c>: the same world-bound request as <c>mineaction</c>, with
    /// the session serial as the target — only this instance's plugin can resolve it.</summary>
    private void QueuePlacedMineAction(CoopCommand command)
    {
        if (!bridge.TryReadWorldLoadSerial(out uint serial)
            || !bridge.TryReadCurrentModule(out string module)
            || string.IsNullOrWhiteSpace(module))
        {
            Log?.Invoke("placedmineaction refused: current world identity is unavailable");
            Reply("placedmineaction: not ready yet - try again after the area finishes loading");
            return;
        }

        SendCheat(new CheatRequest(CheatKinds.PlacedMineAction,
            Target: (uint)command.Amount, Amount: unchecked((int)serial),
            Text: $"{command.Address}|{module}"));
    }
}

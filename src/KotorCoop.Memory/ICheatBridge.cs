// Staging cheats: requesting one and reading the plugin's answer.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public interface ICheatBridge
{
    /// <summary>False when this bridge has no plugin to run staging cheats through.</summary>
    bool SupportsCheats => false;

    /// <summary>
    /// Publishes one staging cheat for the plugin to run on the game thread, returning the
    /// serial it was filed under so the answer can be matched to it.
    ///
    /// Fire-and-forget by design: every one of these ends in an engine call, engine calls
    /// only happen on the game's own thread, and the agent is not on it. The answer comes
    /// back through <see cref="TryReadCheatAnswer"/> a frame or two later.
    /// </summary>
    bool TryRequestCheat(CheatRequest request, out uint serial)
    {
        serial = 0;
        return false;
    }

    /// <summary>
    /// Reads the plugin's answer to the last cheat: which serial it finished, the status,
    /// and the sentence describing what it actually saw. False when there is no plugin.
    /// </summary>
    bool TryReadCheatAnswer(out uint applied, out int status, out string detail)
    {
        applied = 0;
        status = 0;
        detail = string.Empty;
        return false;
    }
}

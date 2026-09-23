namespace KotorCoop.Plugin;

internal sealed unsafe partial class CheatController
{
    private void MineAction(uint localServerPlayerId)
    {
        if (!MineActionRule.TryParseRequest(ReadCheatText(), out bool recover, out string sourceModule))
        {
            Answer(SharedBlock.CheatStatusRefused, "mineaction request must be recover|module or disarm|module");
            return;
        }

        uint objectId = block.CheatTarget;
        uint expectedWorldLoadSerial = unchecked((uint)block.CheatAmount);
        uint currentWorldLoadSerial = block.WorldLoadSerial;
        bool queued = Odyssey.TryQueueMineAction(
            objectId,
            localServerPlayerId,
            sourceModule,
            expectedWorldLoadSerial,
            currentWorldLoadSerial,
            recover,
            out string diagnostic);
        Answer(
            queued ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused,
            diagnostic);
    }
}

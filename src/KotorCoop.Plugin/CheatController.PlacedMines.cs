namespace KotorCoop.Plugin;

// The party-placed mine test verbs (docs/research/mines.md §14.6): placemine, minestate and
// placedmineaction. Cheat kinds 90-92.
internal sealed unsafe partial class CheatController
{
    private void PlaceMine()
    {
        int row = block.CheatAmount;
        uint dcs = block.CheatTarget;
        short detect = unchecked((short)(dcs & 0xFFFF));
        short disarm = unchecked((short)(dcs >> 16));
        if (row is < 0 or >= 0xFF)
        {
            Answer(SharedBlock.CheatStatusRefused, $"placemine: row {row} is not a traps.2da row");
            return;
        }

        if (PluginHost.PlacedMines is not { } mines)
        {
            Answer(SharedBlock.CheatStatusRefused, "placemine: the placed-mine controller is not running");
            return;
        }

        string detail = mines.PlaceTestMine((byte)row, detect, disarm, out bool ok);
        Answer(ok ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused, detail);
    }

    private void MineState()
    {
        if (PluginHost.PlacedMines is not { } mines)
        {
            Answer(SharedBlock.CheatStatusRefused, "minestate: the placed-mine controller is not running");
            return;
        }

        Answer(SharedBlock.CheatStatusApplied, mines.Describe());
    }

    private void PlacedMineAction(uint localServerPlayerId)
    {
        if (!MineActionRule.TryParseRequest(ReadCheatText(), out bool recover, out string sourceModule))
        {
            Answer(SharedBlock.CheatStatusRefused, "placedmineaction request must be recover|module or disarm|module");
            return;
        }

        uint serial = block.CheatTarget;
        if (PluginHost.PlacedMines is not { } mines || !mines.TryResolveSerial(serial, out uint localId))
        {
            Answer(SharedBlock.CheatStatusRefused, $"placedmineaction: session mine #{serial} is not mapped on this instance");
            return;
        }

        bool queued = Odyssey.TryQueueMineAction(
            localId,
            localServerPlayerId,
            sourceModule,
            unchecked((uint)block.CheatAmount),
            block.WorldLoadSerial,
            recover,
            out string diagnostic,
            partyPlaced: true);
        Answer(
            queued ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusRefused,
            $"session mine #{serial} (local 0x{localId:X}): {diagnostic}");
    }
}

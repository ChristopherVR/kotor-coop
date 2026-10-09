namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ⚠ KEEP IN SYNC with SharedBlock.OffsetPvpPhase/OffsetPvpOpponentPeer/OffsetPvpRound.
    private const int BlockPvpPhase = 0xCC80;
    private const int BlockPvpOpponentPeer = 0xCC84;
    private const int BlockPvpRound = 0xCC88;

    /// <inheritdoc />
    public void SetPvpState(uint phase, uint opponentPeer, uint round)
    {
        if (_pluginBlock == 0)
        {
            return;
        }

        // Opponent and round first, phase last: a reader that sees a nonzero phase must see
        // the opponent that goes with it, never the previous duel's.
        _memory.TryWrite(_pluginBlock + BlockPvpOpponentPeer, phase == 0 ? 0u : opponentPeer);
        _memory.TryWrite(_pluginBlock + BlockPvpRound, phase == 0 ? 0u : round);
        _memory.TryWrite(_pluginBlock + BlockPvpPhase, phase);
    }
}

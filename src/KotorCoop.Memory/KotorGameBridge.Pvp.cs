namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ⚠ KEEP IN SYNC with SharedBlock.OffsetPvpPhase/OffsetPvpOpponentPeer/OffsetPvpRound.
    private const int BlockPvpPhase = 0xCC80;
    private const int BlockPvpOpponentPeer = 0xCC84;
    private const int BlockPvpRound = 0xCC88;

    // ⚠ KEEP IN SYNC with SharedBlock.OffsetPvpUi/OffsetPvpUiPeer.
    private const int BlockPvpUi = 0xCC8C;
    private const int BlockPvpUiPeer = 0xCC90;

    /// <inheritdoc />
    public void SetPvpUi(uint flags, uint peer)
    {
        if (_pluginBlock == 0)
        {
            return;
        }

        // Peer first, flags last: a reader that sees the incoming bit must see its challenger.
        _memory.TryWrite(_pluginBlock + BlockPvpUiPeer, flags == 0 ? 0u : peer);
        _memory.TryWrite(_pluginBlock + BlockPvpUi, flags);
    }

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

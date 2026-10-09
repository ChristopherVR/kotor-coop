namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    /// <summary>Agent-written duel phase (<see cref="SharedBlock.PvpPhaseNone"/>,
    /// <see cref="SharedBlock.PvpPhaseArmed"/>, <see cref="SharedBlock.PvpPhaseLive"/>); anything
    /// else reads as none. See <see cref="SharedBlock.OffsetPvpPhase"/>.</summary>
    public uint PvpPhase
    {
        get
        {
            uint phase = *U32(SharedBlock.OffsetPvpPhase);
            return phase is SharedBlock.PvpPhaseArmed or SharedBlock.PvpPhaseLive ? phase : SharedBlock.PvpPhaseNone;
        }
    }

    /// <summary>Agent-written opponent peer id, 0 when there is no duel.</summary>
    public uint PvpOpponentPeer => PvpPhase == SharedBlock.PvpPhaseNone ? 0 : *U32(SharedBlock.OffsetPvpOpponentPeer);

    public uint PvpRound => *U32(SharedBlock.OffsetPvpRound);
}

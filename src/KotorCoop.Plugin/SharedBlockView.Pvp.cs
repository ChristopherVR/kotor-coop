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

    /// <summary>Agent-written duel panel flags (<see cref="PvpMenuRule.UiAllowed"/> and friends);
    /// 0 when duels are off or there is no session.</summary>
    public uint PvpUiFlags => *U32(SharedBlock.OffsetPvpUi);

    /// <summary>Agent-written peer of a pending challenge, 0 when none.</summary>
    public uint PvpUiPeer =>
        (PvpUiFlags & (PvpMenuRule.UiIncoming | PvpMenuRule.UiOutgoing)) == 0 ? 0 : *U32(SharedBlock.OffsetPvpUiPeer);
}

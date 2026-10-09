using KotorCoop.Memory;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Duels, agent side (docs/research/pvp-plan.md): turns the round the host holds into the three
/// levels the plugin acts on (pin and snapshot, hostile opponent, restore on release). The
/// plugin owns every engine write; this only says which phase this player is in and against
/// whom, every pump, and again once a second so a freshly started plugin catches up.
/// </summary>
internal sealed class PvpPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private const long RefreshMs = 1_000;

    private (uint Phase, uint Opponent, uint Round) _written;
    private (uint Flags, uint Peer) _uiWritten;
    private long _writtenAtMs;
    private bool _everWritten;

    /// <summary>True while the local player is a duellist (countdown or live): the cushion
    /// rules read this.</summary>
    public bool Fighting { get; private set; }

    public void Pump()
    {
        PvpView view = session.PvpState;
        (uint phase, uint opponent) = PvpPumpRule.Levels(view, session.LocalPeerId, session.PvpEnabled);
        Fighting = phase != 0;
        uint round = phase == 0 ? 0 : view.RoundId;

        (uint uiFlags, uint uiPeer) = PvpPumpRule.UiLevels(view, session.LocalPeerId, session.PvpEnabled);

        long now = SessionClock.NowMs;
        (uint Phase, uint Opponent, uint Round) next = (phase, opponent, round);
        if (_everWritten && next == _written && (uiFlags, uiPeer) == _uiWritten && now - _writtenAtMs < RefreshMs)
        {
            return;
        }

        if (!_everWritten ? phase != 0 : next != _written)
        {
            log(phase == 0
                ? $"duel: over - releasing the pin{(session.PvpLastEnding.Length > 0 ? $" ({session.PvpLastEnding})" : string.Empty)}"
                : $"duel: round {round} is {(phase == 2 ? "live" : "counting down")} against peer {opponent}");
        }

        bridge.SetPvpUi(uiFlags, uiPeer);
        bridge.SetPvpState(phase, opponent, round);
        _written = next;
        _uiWritten = (uiFlags, uiPeer);
        _writtenAtMs = now;
        _everWritten = true;
    }

    /// <summary>The session ended: forget what was written so the next session writes afresh.
    /// (The block itself is zeroed by <c>ClearCoopState</c>.)</summary>
    public void Reset()
    {
        _everWritten = false;
        Fighting = false;
    }
}

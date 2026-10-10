using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Arena fights, agent side (docs/research/pvp-plan.md, phases 3 and 4): carries out what
/// <see cref="ArenaFlow"/> decides, which is the part of an arena round that touches the game.
/// The host's game asks the plugin for the one warp into the arena and the one warp back; every
/// game moves its own character to its spawn, and back to where it stood. The duel itself (pin,
/// hostile body, restore) is <see cref="PvpPump"/> and the plugin, unchanged.
///
/// Runs every pump and does nothing at all unless the host's round is an arena round, so a
/// session with arenas off never reaches the bridge.
/// </summary>
internal sealed class ArenaPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    private readonly ArenaFlow _flow = new();
    private uint _lastLoggedRound;

    public void Pump(in PlayerState local)
    {
        PvpView view = session.PvpState;
        if (!view.ArenaActive && !_flow.Tracking)
        {
            return;
        }

        long now = SessionClock.NowMs;
        // A boss fight lines the party up; this player's place is its rank among the peer ids.
        uint me = session.LocalPeerId;
        int slot = view.Boss ? session.GetRemotePeers().Count(peer => peer.PeerId < me) : 0;
        ArenaStep step = _flow.Step(view, me, session.IsEnemyAuthority, local.Module, local.Position, now, slot);

        if (view.ArenaActive && _lastLoggedRound != view.RoundId)
        {
            _lastLoggedRound = view.RoundId;
            (string module, Vector3f at, bool known) = _flow.Origin;
            log($"arena: round {view.RoundId} - {(known ? $"will return to {module} at ({at.X:F1}, {at.Y:F1})" : "no way home known")}");
        }

        switch (step.Kind)
        {
            case ArenaStepKind.Travel:
            {
                bool accepted = bridge.TryRequestTravel(step.Module);
                log($"arena: {step.Purpose} - asking the plugin for ONE module change to {step.Module} ({(accepted ? "accepted" : "NOT accepted")})");
                _flow.Confirm(step, accepted, now);
                break;
            }

            case ArenaStepKind.Teleport:
            {
                bool accepted = bridge.TryRequestCheat(
                    new CheatRequest(CheatKinds.Teleport, Amount: ArenaFlow.FacingTowardCentre(step.Position),
                        X: step.Position.X, Y: step.Position.Y, Z: step.Position.Z),
                    out _);
                log($"arena: {step.Purpose} - moving to ({step.Position.X:F1}, {step.Position.Y:F1}) ({(accepted ? "asked" : "NOT accepted")})");
                _flow.Confirm(step, accepted, now);
                break;
            }
        }
    }

    /// <summary>The session ended or the host was lost: forget the round.</summary>
    public void Reset()
    {
        _flow.Reset();
        _lastLoggedRound = 0;
    }
}

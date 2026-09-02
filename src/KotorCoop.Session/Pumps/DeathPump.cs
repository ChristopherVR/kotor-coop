using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Co-op death: nobody drops out of the session alone, and nobody is left alive alone in a
/// wiped one.
///
/// KOTOR downs party members rather than killing them, and stands them up by itself once the
/// area is clear — that is kept. What this adds is the two-instance half:
///
/// - **Rescue.** A downed player whose peer is standing beside them is revived at once,
///   rather than waiting out the engine's own combat-clear timer.
/// - **A wipe is unanimous.** When one instance's whole party falls, its game-over is
///   swallowed and this instance simply lies there. Only when EVERY participant's whole
///   party is down does the suppression lift, and then it lifts on all of them. "Whole
///   party", not "player character": a host whose PC is down while a companion still fights
///   is not wiped — the companion may win, the PC auto-revives once the area is clear, and
///   the host can then walk over and pick the client up. Judging on the PC alone sent a
///   client to the load-game menu while the host played on (live 2026-09-02,
///   docs/research/death-handling.md §6.4). Each instance's engine already knows when its
///   party is gone — that is when it asks for game-over — and
///   <see cref="PlayerState.FlagPartyWiped"/> carries that ask.
///
/// Both suppressions live in the plugin as inert byte patches until turned on here, so a game
/// with no session behaves exactly like stock KOTOR.
/// </summary>
internal sealed class DeathPump(IGameBridge bridge, ICoopSession session, Action<string> log)
{
    /// <summary>How close a standing peer must be to pick a downed player up. Roughly a
    /// couple of body-lengths — close enough to read as "they came to get you".</summary>
    private const float ReviveRangeUnits = 3.5f;

    /// <summary>Hit points a rescued player stands up with: enough to move and fight back,
    /// little enough that being downed still cost something.</summary>
    private const int ReviveHitPoints = 15;

    private bool _deathSuppressionSet;
    private bool _sessionWiped;
    private bool _reviveRequested;

    public void PumpDeath(in PlayerState local, IReadOnlyList<RemotePeer> peers)
    {
        if (!bridge.SupportsDeathHandling)
        {
            return;
        }

        // A wipe needs every party down — including this one. A peer in another module is
        // not part of this fight, but it is also not a corpse: it can still come back for
        // us, so it counts as standing. That is why this takes the UNFILTERED list.
        //
        // ⚠ A peer with NO MODULE is a different thing again: it has not loaded a world at
        // all (sitting at the main menu, or its agent has not resolved its player yet). It
        // cannot be down and it cannot come and rescue anybody, so it is not a participant.
        // Counting it as a standing player is how a full party wipe became impossible: an
        // unpublished avatar is all zeroes, all zeroes is finite, finite is "plausible", and
        // a plausible undowned peer holds the game-over suppression on forever.
        bool everyPeerWiped = true;
        int participants = 0;

        for (int i = 0; i < peers.Count; i++)
        {
            if (peers[i].State.Module.Length == 0)
            {
                continue;
            }

            participants++;

            if (!peers[i].State.IsPartyWiped)
            {
                everyPeerWiped = false;
                break;
            }
        }

        // Wiped implies downed, but the bridge can latch the wipe a tick before it reads the
        // downed level (same plugin frame, serial written first) — insist on both.
        bool allDown = local.IsDowned && local.IsPartyWiped && participants > 0 && everyPeerWiped;

        // Hold the game-over unless the session really is wiped; always hold the control
        // swap (it fights the control lock and hands a client the host's companions).
        if (allDown != _sessionWiped || !_deathSuppressionSet)
        {
            _deathSuppressionSet = true;
            _sessionWiped = allDown;
            // Swap suppression is a CLIENT-side lock, not a global one. It exists so a client
            // cannot take over a companion the host's AI owns — but it blocks Tab and the
            // portraits outright, so applying it to the host too left NOBODY able to switch
            // character (reported from play). The host owns the companions and must stay free
            // to control them.
            bridge.SetDeathSuppression(
                suppressGameOver: !allDown,
                suppressSwap: !session.IsEnemyAuthority);

            if (allDown)
            {
                log("every player is down; releasing the game-over screen for everyone");
            }
        }

        // The engine tried to end the game here. With the suppression on that is only "this
        // instance's party is down" — worth saying once, so a waiting player knows why they
        // are lying on the floor instead of at the menu.
        if (bridge.TryTakeGameOverAttempt() && !allDown)
        {
            log("your party is down — a peer can still revive you");
        }

        // The rescue itself: a standing peer close enough to reach us.
        if (!local.IsDowned)
        {
            _reviveRequested = false;
            return;
        }

        if (_reviveRequested || allDown)
        {
            return; // already asked, or there is nobody left standing to do the reviving
        }

        for (int i = 0; i < peers.Count; i++)
        {
            RemotePeer peer = peers[i];
            if (peer.State.IsDowned || !peer.IsInModule(local.Module))
            {
                continue;
            }

            float distance = peer.State.Position.DistanceTo(local.Position);
            if (distance <= ReviveRangeUnits)
            {
                _reviveRequested = true;
                bridge.RequestRevive(ReviveHitPoints);

                // The distance and both positions are part of the record: a rescue fired
                // at 76 units on tat_m20aa and was undiagnosable without them.
                log($"{peer.Name} revived you ({distance:F1} units: "
                    + $"they {peer.State.Position.X:F1},{peer.State.Position.Y:F1} "
                    + $"you {local.Position.X:F1},{local.Position.Y:F1})");
                return;
            }
        }
    }
}

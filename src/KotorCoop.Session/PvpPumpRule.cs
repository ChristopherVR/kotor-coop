using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// The agent's side of a duel (docs/research/pvp-plan.md), as pure decisions over the round the
/// host holds (<see cref="PvpView"/>) and this player's own id.
/// </summary>
public static class PvpPumpRule
{
    /// <summary>The plugin's phase level for this player: 0 none, 1 countdown (pin and snapshot),
    /// 2 live (the opponent's stand-in is hostile). A session with duels off, or a round this
    /// player is not in, is 0 — a challenge still waiting for an answer pins nobody.</summary>
    public static (uint Phase, uint Opponent) Levels(in PvpView view, uint localPeerId, bool enabled)
    {
        if (!enabled || !view.IsFighting(localPeerId))
        {
            return (0, 0);
        }

        return (view.Phase == PvpPhase.Live ? 2u : 1u, view.OpponentOf(localPeerId));
    }

    /// <summary>
    /// Whether <paramref name="peerId"/> may be counted into the "every player's party is down"
    /// vote that lifts the game-over screen (docs/research/death-handling.md). A duellist who is
    /// down has LOST A DUEL, not been wiped: it must never help end the campaign, and a duellist
    /// who is themselves down must never be able to read the vote as passed.
    /// </summary>
    public static bool CountsTowardWipe(in PvpView view, uint peerId) => !view.IsFighting(peerId);

    /// <summary>Whether this player's own down state may start a wipe at all.</summary>
    public static bool LocalMayWipe(in PvpView view, uint localPeerId) => !view.IsFighting(localPeerId);

    /// <summary>The menu-guard cushion ("cannot be hit while a menu is open") is an exploit in a
    /// duel; it is off for a duellist (docs/research/pvp-product.md section 1).</summary>
    public static bool MenuGuardAllowed(in PvpView view, uint localPeerId) => !view.IsFighting(localPeerId);

    /// <summary>Saves are refused for a duellist: the 1 hp pin rides a save as a persistent
    /// creature field (docs/research/min-one-hitpoint.md).</summary>
    public static bool SaveAllowed(in PvpView view, uint localPeerId) => !view.IsFighting(localPeerId);
}

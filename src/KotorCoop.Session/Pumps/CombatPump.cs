using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session.Pumps;

/// <summary>
/// Everything that makes a fight shared: enemy presence and ownership (<see cref="PumpEnemies"/>,
/// split into CombatPump.Enemies.cs for size), damage and non-damage effects landing on a
/// peer's phantom (CombatPump.PhantomAndEffects.cs), and whether this engine should be in
/// combat at all because a nearby peer is. <see cref="TriggerPump"/> is a collaborator rather
/// than owned state: <see cref="PumpEnemies"/> calls into it for the client-triggered
/// encounter/generic-trigger sub-steps that only make sense in the context of who owns
/// enemies this tick.
/// </summary>
internal sealed partial class CombatPump(
    IGameBridge bridge, ICoopSession session, Action<string> log, TriggerPump triggerPump)
{
    /// <summary>Last value written to the plugin, so the block is only touched on an edge.</summary>
    private bool _sessionCombatHeld;

    /// <summary>
    /// Publishes "a remote peer in your module is fighting" so the plugin can put this
    /// engine into combat too.
    ///
    /// Deliberately NOT range-gated the way the shared pause is. Combat state is not a
    /// courtesy to whoever is standing next to you: it changes round timing, what the HUD
    /// shows, whether abilities queue as combat actions and whether auto-pause fires. Two
    /// players in the same module during the same fight must run the same ruleset, whether
    /// they are ten units apart or across the room.
    ///
    /// The local player is excluded by construction — <paramref name="peersHere"/> is remote
    /// peers only — because the plugin treats this as a claim it imposes and later releases,
    /// and a host fighting alone would otherwise force itself out of its own fight.
    /// </summary>
    public void PumpSharedCombat(IReadOnlyList<RemotePeer> peersHere)
    {
        if (!bridge.SupportsCombatSync)
        {
            return;
        }

        bool anyPeerFighting = false;
        for (int i = 0; i < peersHere.Count; i++)
        {
            if (peersHere[i].State.IsInCombat)
            {
                anyPeerFighting = true;
                break;
            }
        }

        if (anyPeerFighting != _sessionCombatHeld && bridge.TrySetSessionInCombat(anyPeerFighting))
        {
            _sessionCombatHeld = anyPeerFighting;
            log(anyPeerFighting
                ? "a peer here is fighting; entering combat with them"
                : "the peers here have left combat");
        }
    }
}

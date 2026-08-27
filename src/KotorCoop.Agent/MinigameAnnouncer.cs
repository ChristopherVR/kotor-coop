using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Turns a peer's <see cref="PlayerState.FlagInMinigame"/> edge into a line on screen —
/// "Malak is in a minigame — waiting" / "Malak is back" — the same diff-per-tick shape as
/// <see cref="AwayAnnouncer"/>, and for the same reason it lives on the AGENT side: under
/// the shipped <c>minigame_hide_peer=1</c> the agent filters a racing peer OUT of the shared
/// block in the very tick its flag turns true (CoopAgent.PumpOnce's peer filter), so the
/// plugin never sees a slot that is both active and in a minigame and its own
/// <c>MinigameNoticeController</c> can only fire with the hide gate off. The toast reaches
/// the player through the chat overlay whichever way the gate is set. See
/// docs/research/minigames.md §23.3.
///
/// Keyed by peer id, like <see cref="PeerAnnouncer"/>: a rejoin under a new id starts fresh.
/// Pure bookkeeping so it can be tested without a session.
/// </summary>
public sealed class MinigameAnnouncer
{
    private readonly Dictionary<uint, bool> _known = [];

    /// <summary>
    /// Diffs <paramref name="peers"/>' <see cref="PlayerState.IsInMinigame"/> against the last
    /// observation and calls <paramref name="announce"/> once per edge. A peer's first
    /// appearance is recorded silently; a peer leaving the roster is dropped without a line
    /// of its own (<see cref="PeerAnnouncer"/> already says "left the game").
    /// </summary>
    public void Observe(IReadOnlyList<RemotePeer> peers, Action<string> announce)
    {
        HashSet<uint> present = new(peers.Count);

        foreach (RemotePeer peer in peers)
        {
            present.Add(peer.PeerId);
            bool inMinigame = peer.State.IsInMinigame;

            if (!_known.TryGetValue(peer.PeerId, out bool was))
            {
                _known[peer.PeerId] = inMinigame;
                continue;
            }

            if (inMinigame == was)
            {
                continue;
            }

            _known[peer.PeerId] = inMinigame;
            announce(inMinigame
                ? $"{peer.Name} is in a minigame - waiting"
                : $"{peer.Name} is back");
        }

        if (_known.Count > present.Count)
        {
            List<uint> gone = [.. _known.Keys.Where(id => !present.Contains(id))];
            foreach (uint id in gone)
            {
                _known.Remove(id);
            }
        }
    }

    /// <summary>The session went away: forget everyone, matching <see cref="AwayAnnouncer.Reset"/>.</summary>
    public void Reset() => _known.Clear();
}

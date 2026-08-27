using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Turns a peer's <see cref="PlayerState.FlagAway"/> edge into an announcement — the same
/// shape <see cref="PeerAnnouncer"/> already uses for join/leave, and deliberately a SEPARATE
/// diff rather than folded into it: a peer going away is not a roster change (they are still
/// admitted, still counted in the peer total), it is a presence change, and the two edges
/// answer different questions ("who is in the session" vs. "who is actually standing in the
/// world right now"). Without this a partner watching an away peer's phantom simply vanish
/// (CoopAgent.PumpOnce's module/minigame-style peer filter — see its remarks) had no way to
/// tell "they quit to the main menu" from "the link died" or "a bug ate their body". See
/// docs/research/disconnect-reconnect.md.
///
/// Keyed by peer id, like <see cref="PeerAnnouncer"/>, for the same reason: two players may
/// share a name, and a rejoin under a new id starts this peer's away bookkeeping fresh rather
/// than inheriting whatever the departed holder of that name last had. Pure bookkeeping so it
/// can be tested without a session.
/// </summary>
public sealed class AwayAnnouncer
{
    private readonly Dictionary<uint, bool> _known = [];

    /// <summary>
    /// Diffs <paramref name="peers"/>' <see cref="PlayerState.IsAway"/> against the last
    /// observation and calls <paramref name="announce"/> once per edge — a peer's FIRST
    /// appearance is recorded silently (there is nothing to "become" away from yet, and a
    /// player already away when this instance joins would otherwise announce on the very
    /// first tick for no edge at all), and a peer leaving the roster entirely is dropped with
    /// no announcement of its own — <see cref="PeerAnnouncer"/> already covers "left the
    /// game", and firing "is back" on the way out reads backwards.
    /// </summary>
    public void Observe(IReadOnlyList<RemotePeer> peers, Action<string> announce)
    {
        HashSet<uint> present = new(peers.Count);

        foreach (RemotePeer peer in peers)
        {
            present.Add(peer.PeerId);
            bool away = peer.State.IsAway;

            if (!_known.TryGetValue(peer.PeerId, out bool wasAway))
            {
                _known[peer.PeerId] = away;
                continue;
            }

            if (away == wasAway)
            {
                continue;
            }

            _known[peer.PeerId] = away;
            announce(away
                ? $"{peer.Name} left the world (main menu / loading)"
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

    /// <summary>The session went away: forget everyone, so a fresh session (a reconnect)
    /// tracks its roster's away state from scratch — matching <see cref="PeerAnnouncer.Reset"/>.</summary>
    public void Reset() => _known.Clear();
}

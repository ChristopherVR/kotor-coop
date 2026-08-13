using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Turns roster changes into announcements: a peer appearing in the session's admitted list
/// is "joined", one vanishing is "left". This was the host's missing half of a join — it
/// admitted the peer, its readout ticked to "peers 1", and nothing in the game said a word;
/// a host mid-fight simply never knew another player was there.
///
/// Keyed by peer id, not name: two players may share a name, and a rejoin under a new id is
/// a real join. Pure bookkeeping so it can be tested without a session.
/// </summary>
public sealed class PeerAnnouncer
{
    private readonly Dictionary<uint, string> _known = [];

    /// <summary>
    /// Diffs <paramref name="peers"/> against the last observation and calls
    /// <paramref name="announce"/> once per change. Runs on the ADMITTED list, so a joiner
    /// mid-handshake (or one refused for its password) announces nothing.
    /// </summary>
    public void Observe(IReadOnlyList<RemotePeer> peers, Action<string> announce)
    {
        foreach (RemotePeer peer in peers)
        {
            if (_known.TryAdd(peer.PeerId, peer.Name))
            {
                announce($"{peer.Name} joined the game");
            }
        }

        if (_known.Count > peers.Count)
        {
            List<uint> gone = [.. _known.Keys.Where(id => !peers.Any(p => p.PeerId == id))];
            foreach (uint id in gone)
            {
                announce($"{_known[id]} left the game");
                _known.Remove(id);
            }
        }
    }

    /// <summary>The session went away: forget everyone, so a fresh session (a reconnect)
    /// re-announces its roster — which is what a fresh session should do.</summary>
    public void Reset() => _known.Clear();
}

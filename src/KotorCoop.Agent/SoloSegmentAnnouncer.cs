using KotorCoop.Protocol;

namespace KotorCoop.Agent;

/// <summary>
/// Turns a peer's <see cref="PlayerState.FlagSoloSegment"/> edge into an announcement —
/// "Malak is fighting alone — spectating" / "Malak's fight is over" — the same diff-per-tick
/// shape <see cref="MinigameAnnouncer"/>/<see cref="AwayAnnouncer"/> already use, and for the
/// same reason it lives on the AGENT side rather than only the plugin's own HUD notice
/// (<c>SoloSegmentSpectateController</c>'s own row): the chat overlay reaches the player
/// whichever side of the peer/owner split they are on, where the plugin's notice only ever
/// draws for the SPECTATOR (the owner has no reason to see their own flag reflected back at
/// them as a notice about themselves). See docs/research/star-forge-finale.md §3.3/§3.5.
///
/// Keyed by peer id, like <see cref="PeerAnnouncer"/>: a rejoin under a new id starts fresh.
/// Pure bookkeeping so it can be tested without a session.
/// </summary>
public sealed class SoloSegmentAnnouncer
{
    private readonly Dictionary<uint, bool> _known = [];

    /// <summary>
    /// Diffs <paramref name="peers"/>' <see cref="PlayerState.IsInSoloSegment"/> against the
    /// last observation and calls <paramref name="announce"/> once per edge. A peer's first
    /// appearance is recorded silently (nothing to "become" the owner of yet, and a segment
    /// already active when this instance joins would otherwise announce on the very first tick
    /// for no edge at all); a peer leaving the roster is dropped without a line of its own —
    /// <see cref="PeerAnnouncer"/> already says "left the game".
    /// </summary>
    public void Observe(IReadOnlyList<RemotePeer> peers, Action<string> announce)
    {
        HashSet<uint> present = new(peers.Count);

        foreach (RemotePeer peer in peers)
        {
            present.Add(peer.PeerId);
            bool inSegment = peer.State.IsInSoloSegment;

            if (!_known.TryGetValue(peer.PeerId, out bool was))
            {
                _known[peer.PeerId] = inSegment;
                continue;
            }

            if (inSegment == was)
            {
                continue;
            }

            _known[peer.PeerId] = inSegment;
            announce(inSegment
                ? $"{peer.Name} is fighting alone - spectating"
                : $"{peer.Name}'s fight is over");
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

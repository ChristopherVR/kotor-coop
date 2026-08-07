using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>One client's request that the host fire an encounter on its behalf.</summary>
/// <param name="EncounterId">The encounter's engine object id — the same number in both
/// instances (Handoff 4.5: <c>CSWSArea::LoadGIT</c> hands out ids from a monotonic counter
/// walking the GIT lists in a fixed order, and the Encounter List is one of them).</param>
/// <param name="PeerId">Who is standing in it, so the host can find that peer's phantom —
/// the body the engine's own eligibility test is run against.</param>
public readonly record struct EncounterFireRequest(uint EncounterId, uint PeerId);

/// <summary>
/// The host's record of which encounters each client is currently standing inside.
///
/// Clients cannot fire their own encounters: an encounter spawn on a client would advance
/// that instance's object-id counter and break the id identity every other channel keys on
/// (the reason <c>DisableLocalEncounters</c> exists at all). So a client reports where it
/// is standing and the host's engine does the spawn — one engine ever spawns, ids stay
/// identical, and the creatures ride the enemy channel like any other.
///
/// A LEVEL, not an event, in both directions. A client re-sends its current set every tick,
/// and this store keeps only recent reports: a peer that walks out, disconnects or crashes
/// stops refreshing and simply ages out, with no "left the area" message to lose. Repeats
/// cost nothing because firing is idempotent in the engine itself —
/// <c>CSWSEncounter::SpawnIfAppropriate 0x00593BE0</c> returns immediately when
/// <c>started</c> is already 1, which is what firing sets.
/// </summary>
public sealed class EncounterTriggerStore
{
    /// <summary>
    /// How long a report stays actionable. Clients send at the session tick rate (30 Hz by
    /// default), so this is many ticks of slack for a dropped datagram while still meaning
    /// "the player is there NOW" rather than "was, once".
    /// </summary>
    public const long DefaultFreshnessMs = 2000;

    /// <summary>
    /// Encounters the host will act on in one pass. The whole base game has at most 7
    /// encounters in a module, and the plugin's request region holds 8 slots; this matches.
    /// </summary>
    public const int MaxRequests = 8;

    private readonly record struct PeerReport(string Module, uint[] EncounterIds, long ReceivedAtMs);

    private readonly Dictionary<uint, PeerReport> _byPeer = [];
    private readonly Lock _gate = new();
    private readonly long _freshnessMs;

    public EncounterTriggerStore(long freshnessMs = DefaultFreshnessMs) => _freshnessMs = freshnessMs;

    /// <summary>Records a client's current set. An empty set is stored, not ignored: it is
    /// how "I walked back out" arrives, and storing it stops the previous set counting.</summary>
    public void Report(EncounterTriggerPacket packet, long nowMs)
    {
        if (packet.PeerId == 0)
        {
            return;
        }

        uint[] ids = [.. packet.EncounterIds
            .Where(id => id is not 0 and not 0x7F000000)
            .Distinct()
            .Take(ProtocolConstants.MaxEncounterTriggers)];

        lock (_gate)
        {
            _byPeer[packet.PeerId] = new PeerReport(packet.Module ?? string.Empty, ids, nowMs);
        }
    }

    /// <summary>Drops a peer's reports outright — used when it disconnects, so nothing it
    /// last said can still fire after it has gone.</summary>
    public void Forget(uint peerId)
    {
        lock (_gate)
        {
            _byPeer.Remove(peerId);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _byPeer.Clear();
        }
    }

    /// <summary>
    /// What the host should ask its plugin to fire right now: fresh reports only, from peers
    /// standing in <paramref name="module"/>, deduplicated by encounter and capped.
    ///
    /// Deduplicated by ENCOUNTER, not by (encounter, peer): two players in the same polygon
    /// want one fight, not two, and the first requester is the one the engine's spawn is
    /// anchored on. Order is stable (peer id, then the peer's own reported order) so the
    /// same input always produces the same request list.
    /// </summary>
    public IReadOnlyList<EncounterFireRequest> Collect(string module, long nowMs)
    {
        List<EncounterFireRequest> requests = [];
        HashSet<uint> seen = [];

        lock (_gate)
        {
            foreach (uint peerId in _byPeer.Keys.Order())
            {
                PeerReport report = _byPeer[peerId];

                // Ids reset on a module transition, so a report from a peer standing
                // somewhere else names an encounter that is not the one it means.
                if (nowMs - report.ReceivedAtMs > _freshnessMs
                    || !string.Equals(report.Module, module, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (uint id in report.EncounterIds)
                {
                    if (requests.Count >= MaxRequests)
                    {
                        return requests;
                    }

                    if (seen.Add(id))
                    {
                        requests.Add(new EncounterFireRequest(id, peerId));
                    }
                }
            }
        }

        return requests;
    }
}

namespace KotorCoop.Session;

/// <summary>
/// Mirror side: finds hostiles that exist only in THIS world. A story trigger's
/// <c>CreateObject</c> runs on the joiner's own engine for its own real character
/// (docs/research/trigger-relay.md section 1), so a spawn the host's world never makes leaves
/// enemies only the joiner can see (live 2026-10-08, Endar Spire: <c>end_reinforce3</c>/<c>4</c>
/// from <c>k_pend_reinfrc06</c>). The host owns enemies; a hostile it has never had is one this
/// world should not have either.
///
/// Judged only against a COMPLETE host view (every page of one publish cycle), and only one
/// that arrived at least <see cref="HostViewLagMs"/> after the id was first missing here: a
/// complete view can still reflect a host scan up to two intervals old (2 s scan, 2 s
/// broadcast), and a creature both worlds spawn together must be in the host's scan before it
/// is judged. This replaced a flat 20 s grace (2026-10-09).
/// </summary>
public sealed class LocalOnlyHostileTracker
{
    /// <summary>The host's scan interval plus its broadcast interval, plus slack.</summary>
    public const long HostViewLagMs = 4_500;

    private readonly Dictionary<uint, long> _missingSince = [];

    /// <param name="hostIds">The host's current view; partial unless
    /// <paramref name="completeViewReceivedAtMs"/> is set.</param>
    /// <param name="completeViewReceivedAtMs">When the last page of the current, complete view
    /// arrived; null while the current cycle is still incomplete.</param>
    public List<uint> Update(
        IReadOnlyCollection<uint> localHostiles, IReadOnlySet<uint> hostIds, long nowMs, long? completeViewReceivedAtMs)
    {
        List<uint> localOnly = [];
        if (hostIds.Count == 0)
        {
            _missingSince.Clear();
            return localOnly;
        }

        HashSet<uint> seenLocally = new(localHostiles);
        foreach (uint id in _missingSince.Keys.Where(id => !seenLocally.Contains(id) || hostIds.Contains(id)).ToList())
        {
            _missingSince.Remove(id);
        }

        foreach (uint id in seenLocally)
        {
            if (hostIds.Contains(id))
            {
                continue;
            }

            if (!_missingSince.TryGetValue(id, out long since))
            {
                _missingSince[id] = nowMs;
                since = nowMs;
            }

            if (completeViewReceivedAtMs is { } receivedAt && receivedAt - since >= HostViewLagMs)
            {
                localOnly.Add(id);
            }
        }

        return localOnly;
    }

    public void Clear() => _missingSince.Clear();
}

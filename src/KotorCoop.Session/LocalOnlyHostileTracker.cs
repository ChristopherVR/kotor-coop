namespace KotorCoop.Session;

/// <summary>
/// Mirror side: finds hostiles that exist only in THIS world. A story trigger's
/// <c>CreateObject</c> runs on the joiner's own engine for its own real character
/// (docs/research/trigger-relay.md section 1), so a spawn the host's world never makes leaves
/// enemies only the joiner can see (live 2026-10-08, Endar Spire: <c>end_reinforce3</c>/<c>4</c>
/// from <c>k_pend_reinfrc06</c>). The host owns enemies; a hostile it has never had is one this
/// world should not have either.
///
/// The host's view arrives in pages and a new cycle starts empty, so one look can miss an id
/// the host does have. An id is reported only after it has been absent from every look for
/// <see cref="GraceMs"/> (about ten host publish cycles), and only while there IS a host view
/// to compare with: with none, nothing is judged and the clock restarts.
/// </summary>
public sealed class LocalOnlyHostileTracker
{
    public const long GraceMs = 20_000;

    private readonly Dictionary<uint, long> _missingSince = [];

    /// <summary>Ids absent from the host's view for at least <see cref="GraceMs"/>.</summary>
    public List<uint> Update(IReadOnlyCollection<uint> localHostiles, IReadOnlySet<uint> hostIds, long nowMs)
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
            }
            else if (nowMs - since >= GraceMs)
            {
                localOnly.Add(id);
            }
        }

        return localOnly;
    }

    public void Clear() => _missingSince.Clear();
}

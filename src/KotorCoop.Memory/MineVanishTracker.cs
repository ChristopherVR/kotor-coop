namespace KotorCoop.Memory;

/// <summary>
/// Decides, from once-a-second observations, which GIT-placed floor mines have GONE — the one
/// fact <see cref="MineFlags"/> publishes. Pure logic with no memory access so every guard is
/// unit-tested; <c>KotorGameBridge.CollectChangedMineState</c> feeds it.
///
/// Why this is an absence detector and not a state read: a trap trigger has no "disarmed" or
/// "detonated" byte (docs/research/mines.md §4). Disarm, recover, key disarm and a one-shot
/// detonation all end with the engine deleting the trigger. Absence is the only signal, and a
/// false "gone" is DESTRUCTIVE (the peer would delete a live mine), so the tracker is built
/// to under-report:
/// <list type="bullet">
/// <item><b>Baseline window.</b> Only mines seen in the first <see cref="BaselinePasses"/>
/// world-alive passes after a module entry are ever eligible. A mine that appears later is
/// player-placed (its object id is a per-instance dynamic id, never the same on the peer) or
/// a late spawn; it stays local and is never published.</item>
/// <item><b>World-alive gate.</b> An area unload frees every object at once. A pass where the
/// caller reports the world dead (its door/placeable spot-check failed) counts nothing and
/// zeroes every absence streak.</item>
/// <item><b>Streak.</b> A mine must be absent <see cref="AbsentPassesToReport"/> consecutive
/// passes; a reappearance zeroes the streak.</item>
/// <item><b>Mass-vanish guard.</b> If a single pass would report
/// <see cref="MassVanishMinimum"/> or more mines AND more than half the baseline, that is an
/// unload the world-alive gate missed, not a coincidence of disarms: the candidates are
/// dropped from the baseline unreported.</item>
/// <item><b>Report once.</b> Each id is reported at most once per module; an id the session
/// already told us is gone (<see cref="MarkKnownGone"/>) is never echoed back.</item>
/// </list>
/// </summary>
public sealed class MineVanishTracker
{
    public const int BaselinePasses = 3;
    public const int AbsentPassesToReport = 3;
    public const int MassVanishMinimum = 3;

    private readonly HashSet<uint> _baseline = [];
    private readonly Dictionary<uint, int> _absentStreak = [];
    private readonly HashSet<uint> _done = [];
    private int _worldAlivePasses;

    /// <summary>Mines still eligible to be reported (baseline minus reported, known-gone or dropped).</summary>
    public int TrackedCount => _baseline.Count(id => !_done.Contains(id));

    public bool BaselineFrozen => _worldAlivePasses >= BaselinePasses;

    public void Reset()
    {
        _baseline.Clear();
        _absentStreak.Clear();
        _done.Clear();
        _worldAlivePasses = 0;
    }

    /// <summary>The session says this mine is already gone (a peer published it, or the plugin
    /// was told to remove it): never report it ourselves.</summary>
    public void MarkKnownGone(uint objectId) => _done.Add(objectId);

    /// <summary>
    /// One observation. <paramref name="aliveMineIds"/> is every trap trigger the caller
    /// verified alive this pass; <paramref name="worldAlive"/> is the caller's independent
    /// "the area is still loaded" check. Returns the ids newly confirmed gone.
    /// </summary>
    public IReadOnlyList<uint> Observe(bool worldAlive, IReadOnlyCollection<uint> aliveMineIds)
    {
        if (!worldAlive)
        {
            _absentStreak.Clear();
            return [];
        }

        if (_worldAlivePasses < BaselinePasses)
        {
            _worldAlivePasses++;
            foreach (uint id in aliveMineIds)
            {
                _baseline.Add(id);
            }

            return [];
        }

        HashSet<uint> alive = aliveMineIds as HashSet<uint> ?? [.. aliveMineIds];
        List<uint> candidates = [];

        foreach (uint id in _baseline)
        {
            if (_done.Contains(id))
            {
                continue;
            }

            if (alive.Contains(id))
            {
                _absentStreak.Remove(id);
                continue;
            }

            int streak = _absentStreak.GetValueOrDefault(id) + 1;
            _absentStreak[id] = streak;
            if (streak >= AbsentPassesToReport)
            {
                candidates.Add(id);
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        bool massVanish = candidates.Count >= MassVanishMinimum && candidates.Count * 2 > TrackedCount;

        foreach (uint id in candidates)
        {
            _done.Add(id);
            _absentStreak.Remove(id);
        }

        if (massVanish)
        {
            return [];
        }

        candidates.Sort();
        return candidates;
    }
}

namespace KotorCoop.Session;

/// <summary>
/// Debounces the player count that drives the auto scaling curve.
///
/// THE FLAP THIS CLOSES (live 2026-09-19, docs/research/player-count-scaling.md): while the
/// joiner arrived, the host's agent read <c>2</c>, then <c>1</c> for about five seconds, then
/// <c>2</c> again. Enemy hit points are scaled ONCE per enemy on first sighting, so an enemy
/// first seen inside the one-player window kept 100% for the whole module. A count that
/// RISES is real the moment it is seen (someone joined). A count that FALLS is not trusted
/// until it has held for <see cref="FallSettleMs"/>: a genuine leave costs a 10 s delay before
/// the curve relaxes, a transient dip costs nothing.
/// </summary>
public sealed class PlayerCountSettleRule
{
    /// <summary>How long a lower count must hold before it replaces the current one.</summary>
    public const long FallSettleMs = 10_000;

    private int _settled = 1;
    private int _candidate;
    private long _candidateSinceMs;

    /// <summary>The count the scaling curve should use.</summary>
    public int Settled => _settled;

    /// <summary>Feeds one reading and returns the settled count.</summary>
    public int Observe(int rawCount, long nowMs)
    {
        if (rawCount < 1)
        {
            rawCount = 1;
        }

        if (rawCount >= _settled)
        {
            _settled = rawCount;
            _candidate = 0;
            return _settled;
        }

        if (_candidate != rawCount)
        {
            _candidate = rawCount;
            _candidateSinceMs = nowMs;
        }
        else if (nowMs - _candidateSinceMs >= FallSettleMs)
        {
            _settled = rawCount;
            _candidate = 0;
        }

        return _settled;
    }

    /// <summary>Forgets everything (a fresh session starts alone).</summary>
    public void Reset()
    {
        _settled = 1;
        _candidate = 0;
        _candidateSinceMs = 0;
    }
}

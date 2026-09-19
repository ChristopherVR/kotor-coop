using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// coop.ini <c>xp_rate</c>'s arithmetic (docs/research/player-count-scaling.md §XP): the HOST
/// scales the experience its OWN engine just granted — the rise between two readings — and
/// writes the bonus on top, BEFORE the max-merge with every client's report.
///
/// Why a delta and not the absolute total: the absolute is max-merged with the clients'
/// absolutes, and every client's engine grants the same kill XP (a mirrored death runs
/// <c>ApplyDeathExperience</c> there too — progression-sync.md). Scaling the absolute would
/// compound on every tick; scaling the delta adds the bonus once, and the host's larger total
/// then wins the merge and flows down to everyone.
///
/// The double-count hazard, and how it is handled: the host's own write (<c>TryWriteProgression</c>)
/// is read back on the next tick as a rise. The caller re-baselines after every write, exactly
/// like <c>PartyPump._lastObservedGold</c>, so only what the ENGINE granted is ever scaled. A
/// reading that went DOWN (a load of an older save) re-baselines without a bonus.
///
/// Rates below 100 cannot take effect while a client is connected — the client's vanilla
/// total wins the max-merge — and are reported as such by the caller, not silently ignored.
///
/// Stateful only in the rounding remainder, so five 3-XP grants at 110% add 1.5 five times
/// (7 XP), not 0 five times.
/// </summary>
public sealed class XpRateRule
{
    private long _lastObserved = -1;
    private int _remainderHundredths;

    /// <summary>The last experience reading this rule accepted as the engine's, -1 before
    /// the first. Exposed for the caller's own log lines and tests.</summary>
    public long LastObserved => _lastObserved;

    /// <summary>The rise the most recent <see cref="Observe"/> measured (0 on a baseline or a
    /// fall), for the caller's log line.</summary>
    public long LastRise { get; private set; }

    /// <summary>
    /// Observes this tick's raw experience reading and returns the bonus to add on top of it
    /// (0 when nothing rose, the rate is 100, or this is the first reading, which only
    /// baselines). The caller must <see cref="Rebaseline"/> after writing a merged total.
    /// </summary>
    public int Observe(uint experience, int percent)
    {
        if (_lastObserved < 0)
        {
            _lastObserved = experience;
            LastRise = 0;
            return 0;
        }

        long delta = (long)experience - _lastObserved;
        _lastObserved = experience;
        LastRise = delta > 0 ? delta : 0;

        if (delta <= 0 || percent == PlayerCountScalingRule.Vanilla)
        {
            return 0;
        }

        // bonus = delta * (percent - 100) / 100, carrying the fractional hundredths forward.
        long hundredths = delta * (percent - PlayerCountScalingRule.Vanilla) + _remainderHundredths;
        long bonus = hundredths / 100;
        _remainderHundredths = (int)(hundredths % 100);

        if (bonus < 0)
        {
            // A rate below 100 would take XP away; the engine already granted it and a client
            // would restore it through the merge anyway. Never write a total below the reading.
            _remainderHundredths = 0;
            return 0;
        }

        return bonus > int.MaxValue ? int.MaxValue : (int)bonus;
    }

    /// <summary>The total the caller just wrote (or adopted) is what the engine now holds:
    /// the next reading must be measured against it, not against the pre-write value.</summary>
    public void Rebaseline(uint experience) => _lastObserved = experience;

    /// <summary>The next reading only baselines again (a session teardown, a re-attach).</summary>
    public void Reset()
    {
        _lastObserved = -1;
        _remainderHundredths = 0;
    }
}

namespace KotorCoop.Session;

/// <summary>
/// A small ring buffer of the gaps between successive timestamped events — successive state
/// sends, successive inbound packets. Built for <c>lagstats</c>: a live round needs to see the
/// REAL send/receive cadence a session is achieving, not just the configured tick rate, because
/// the two can diverge under load (a slow pump, a busy thread pool, GC) in exactly the way that
/// reads as "lag" without ever showing up in packet counts.
///
/// Pure and clock-free by construction: the caller supplies <c>nowMs</c>, nothing in here reads
/// a clock, so it is trivially unit-testable — the same split every other stats/rule helper in
/// this codebase uses.
/// </summary>
public sealed class IntervalTracker
{
    private readonly long[] _samples;
    private int _count;
    private int _next;
    private long _lastEventMs = -1;

    public IntervalTracker(int capacity = 8)
    {
        _samples = new long[Math.Max(1, capacity)];
    }

    /// <summary>
    /// Records one event at <paramref name="nowMs"/>. The FIRST call only starts the clock —
    /// there is nothing to measure an interval against yet, so it reports none.
    /// </summary>
    public void Record(long nowMs)
    {
        if (_lastEventMs >= 0)
        {
            long gap = nowMs - _lastEventMs;
            _samples[_next] = gap < 0 ? 0 : gap; // a clock that ever went backwards is not a real gap
            _next = (_next + 1) % _samples.Length;

            if (_count < _samples.Length)
            {
                _count++;
            }
        }

        _lastEventMs = nowMs;
    }

    /// <summary>How many real intervals have been recorded so far, capped at the buffer's
    /// capacity.</summary>
    public int Count => _count;

    /// <summary>The last <see cref="Count"/> intervals, OLDEST first — the order a log line or
    /// a console readout should print them in.</summary>
    public long[] Snapshot()
    {
        long[] result = new long[_count];
        int start = _count < _samples.Length ? 0 : _next;

        for (int i = 0; i < _count; i++)
        {
            result[i] = _samples[(start + i) % _samples.Length];
        }

        return result;
    }

    /// <summary>Mean of the recorded intervals, or 0 when none have landed yet.</summary>
    public double AverageMs
    {
        get
        {
            if (_count == 0)
            {
                return 0;
            }

            long sum = 0;
            foreach (long sample in Snapshot())
            {
                sum += sample;
            }

            return (double)sum / _count;
        }
    }
}

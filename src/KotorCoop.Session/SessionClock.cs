using System.Diagnostics;

namespace KotorCoop.Session;

/// <summary>
/// A monotonic millisecond clock. Every timing decision (interpolation, timeouts,
/// retransmits) is made against local elapsed time, never wall clock, so the two ends
/// never need their clocks synchronised. Remote timestamps that do cross the wire are
/// used only for round-trip measurement.
/// </summary>
public static class SessionClock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();

    public static long NowMs => Watch.ElapsedMilliseconds;
}

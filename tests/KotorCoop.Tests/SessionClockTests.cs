using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="SessionClock"/> is a thin <c>Stopwatch</c> wrapper: every timing decision in this
/// codebase (interpolation, timeouts, retransmits) is made against local elapsed time rather
/// than wall clock, so the two ends of a session never need synchronised clocks. There is very
/// little to assert beyond "it behaves like a monotonic stopwatch that started at (or before)
/// process/test start" — the interesting behaviour lives in the callers, which all take their
/// own explicit <c>nowMs</c> precisely so THEY don't have to depend on this class's real-time
/// ticking during a test.
/// </summary>
public class SessionClockTests
{
    [Fact]
    public void NowMsIsNeverNegative()
    {
        Assert.True(SessionClock.NowMs >= 0);
    }

    [Fact]
    public void NowMsNeverGoesBackwards()
    {
        long first = SessionClock.NowMs;

        for (int i = 0; i < 1000; i++)
        {
            long next = SessionClock.NowMs;
            Assert.True(next >= first, $"clock went backwards: {next} < {first}");
            first = next;
        }
    }

    [Fact]
    public void NowMsAdvancesWithRealTime()
    {
        long before = SessionClock.NowMs;
        Thread.Sleep(20);
        long after = SessionClock.NowMs;

        Assert.True(after > before, $"expected the clock to advance past {before}, saw {after}");
    }

    [Fact]
    public void NowMsIsSharedAcrossEveryCaller()
    {
        // One process-wide Stopwatch — two callers a moment apart must read values close
        // together, not two independently-started clocks.
        long a = SessionClock.NowMs;
        long b = SessionClock.NowMs;

        Assert.InRange(b - a, 0, 1000);
    }
}

using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PumpFailureTracker"/> — the decision logic <see cref="CoopAgent.RunAsync"/>'s
/// pump-failure handling was pulled out into (2026-09-25, docs/research/disconnect-
/// reconnect.md's bridge-concurrency section), so these can run without a real
/// <see cref="PeriodicTimer"/> or a live game: full-exception logging on the first failure,
/// the one-line summary cadence on repeats, the "same exception type N times in a row" cache
/// reset, and the ~10s shutdown ceiling.
///
/// The live bug this all exists for: a joiner's pump began failing EVERY tick with
/// <c>InvalidOperationException</c> ("A concurrent update was performed on this collection
/// and corrupted its state") right after a module transition, then shut the agent down
/// silently after 300 identical failures. See <c>KotorGameBridge.Sync</c>'s remarks for the
/// fix to the race itself; this class is the second line of defence plus the diagnostics that
/// would have named the collection immediately instead of after the fact.
/// </summary>
public class PumpFailureTrackerTests
{
    private static InvalidOperationException CorruptedCollectionException() =>
        new("A concurrent update was performed on this collection and corrupted its state.");

    [Fact]
    public void FirstFailureAsksForTheFullException()
    {
        PumpFailureTracker tracker = new(pollRate: 30);

        PumpFailureAction action = tracker.RecordFailure(CorruptedCollectionException());

        Assert.True(action.LogFullException);
        Assert.False(action.LogRepeatSummary);
        Assert.Equal(1, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void FailuresBetweenPollRateTicksLogNothing()
    {
        PumpFailureTracker tracker = new(pollRate: 30);
        tracker.RecordFailure(CorruptedCollectionException()); // 1st: full exception

        for (int i = 2; i <= 29; i++)
        {
            PumpFailureAction action = tracker.RecordFailure(CorruptedCollectionException());
            Assert.False(action.LogFullException);
            Assert.False(action.LogRepeatSummary);
        }
    }

    [Fact]
    public void TheThirtiethConsecutiveFailureLogsTheOneLineRepeatSummary()
    {
        PumpFailureTracker tracker = new(pollRate: 30);

        PumpFailureAction? thirtieth = null;
        for (int i = 1; i <= 30; i++)
        {
            PumpFailureAction action = tracker.RecordFailure(CorruptedCollectionException());
            if (i == 30)
            {
                thirtieth = action;
            }
        }

        Assert.NotNull(thirtieth);
        Assert.False(thirtieth!.Value.LogFullException);
        Assert.True(thirtieth.Value.LogRepeatSummary);
        Assert.Equal(30, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void ASuccessBetweenTwoFailuresResetsTheStreak()
    {
        PumpFailureTracker tracker = new(pollRate: 30);
        tracker.RecordFailure(CorruptedCollectionException());
        tracker.RecordFailure(CorruptedCollectionException());
        Assert.Equal(2, tracker.ConsecutiveFailures);

        tracker.RecordSuccess();
        Assert.Equal(0, tracker.ConsecutiveFailures);

        // A fresh streak after a success is a FIRST failure again, not a fourth.
        PumpFailureAction action = tracker.RecordFailure(CorruptedCollectionException());
        Assert.True(action.LogFullException);
        Assert.Equal(1, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void TheSameExceptionTypeFailingRepeatedlyAsksForACacheResetExactlyOnce()
    {
        PumpFailureTracker tracker = new(pollRate: 30);

        PumpFailureAction? resetAction = null;
        int resetCount = 0;

        // Twice PumpFailureTracker.ResetCachesAfterSameFailures, so the "only once per run"
        // rule is actually exercised, not just reached.
        for (int i = 1; i <= PumpFailureTracker.ResetCachesAfterSameFailures * 2; i++)
        {
            PumpFailureAction action = tracker.RecordFailure(CorruptedCollectionException());
            if (action.ResetCaches)
            {
                resetCount++;
                resetAction ??= action;
            }
        }

        Assert.Equal(1, resetCount);
        Assert.NotNull(resetAction);
        Assert.Equal(PumpFailureTracker.ResetCachesAfterSameFailures, resetAction!.Value.ConsecutiveSameExceptionCount);
    }

    [Fact]
    public void ADifferentExceptionTypeInterruptingTheStreakDelaysTheCacheReset()
    {
        PumpFailureTracker tracker = new(pollRate: 30);

        // One short of the threshold...
        for (int i = 1; i < PumpFailureTracker.ResetCachesAfterSameFailures; i++)
        {
            Assert.False(tracker.RecordFailure(CorruptedCollectionException()).ResetCaches);
        }

        // ...then a DIFFERENT exception type breaks the run: the count for THIS type restarts
        // at 1, so the reset must not fire yet.
        PumpFailureAction interrupted = tracker.RecordFailure(new NullReferenceException());
        Assert.False(interrupted.ResetCaches);
        Assert.Equal(1, interrupted.ConsecutiveSameExceptionCount);

        // Confirms the corrupted-collection streak really did restart: it takes the FULL
        // threshold again from here, not just one more.
        for (int i = 1; i < PumpFailureTracker.ResetCachesAfterSameFailures; i++)
        {
            Assert.False(tracker.RecordFailure(CorruptedCollectionException()).ResetCaches);
        }

        Assert.True(tracker.RecordFailure(CorruptedCollectionException()).ResetCaches);
    }

    [Fact]
    public void ShutdownFiresAtTenSecondsWorthOfTicksRegardlessOfPollRate()
    {
        const int pollRate = 47; // an odd rate: the 10s ceiling must not assume a round number.
        PumpFailureTracker tracker = new(pollRate);

        Assert.Equal(pollRate * 10, tracker.MaxConsecutiveFailures);

        PumpFailureAction? last = null;
        for (int i = 1; i <= tracker.MaxConsecutiveFailures; i++)
        {
            last = tracker.RecordFailure(CorruptedCollectionException());
            if (i < tracker.MaxConsecutiveFailures)
            {
                Assert.False(last.Value.Shutdown);
            }
        }

        Assert.NotNull(last);
        Assert.True(last!.Value.Shutdown);
    }

    [Fact]
    public void APollRateOfZeroOrLessNeverDividesByZero()
    {
        // AgentSettings.PollRate is configuration; a bad value (0, or a stale/misconfigured
        // negative) must degrade to "shut down promptly" rather than throw
        // DivideByZeroException out of the pump loop.
        PumpFailureTracker tracker = new(pollRate: 0);

        PumpFailureAction action = tracker.RecordFailure(CorruptedCollectionException());

        Assert.True(action.LogFullException);
        Assert.True(tracker.MaxConsecutiveFailures > 0);
    }
}

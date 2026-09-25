namespace KotorCoop.Session;

/// <summary>
/// What <see cref="CoopAgent.RunAsync"/> should do about one failed pump tick, decided by
/// <see cref="PumpFailureTracker"/>.
/// </summary>
/// <param name="LogFullException">First failure of a run: log the full exception (stack
/// trace included), so a corrupted-collection failure names which collection threw.</param>
/// <param name="LogRepeatSummary">A later failure that lands on the one-line summary cadence
/// (every <c>PollRate</c> ticks) — never both this and <see cref="LogFullException"/>.</param>
/// <param name="ResetCaches">The same exception TYPE has now repeated
/// <see cref="PumpFailureTracker.ResetCachesAfterSameFailures"/> times in a row and the
/// bridge's caches have not already been reset this run: ask it to drop them and retry.</param>
/// <param name="Shutdown">Consecutive failures (of any type) reached the ~10s ceiling: give
/// up and let the agent exit.</param>
/// <param name="ConsecutiveSameExceptionCount">How many times in a row the CURRENT exception
/// type has now failed — only meaningful alongside <see cref="ResetCaches"/>, kept on the
/// result so the log line can name the count without the caller re-deriving it.</param>
public readonly record struct PumpFailureAction(
    bool LogFullException,
    bool LogRepeatSummary,
    bool ResetCaches,
    bool Shutdown,
    int ConsecutiveSameExceptionCount);

/// <summary>
/// Decision logic for <see cref="CoopAgent.RunAsync"/>'s pump-failure handling, pulled out of
/// the timer-driven loop so it is unit-testable without a real <see cref="PeriodicTimer"/> or
/// a live game. Pure bookkeeping: no I/O, no bridge access, no logging — <c>RunAsync</c>
/// executes whatever <see cref="RecordFailure"/> recommends and calls
/// <see cref="RecordSuccess"/> on every tick that pumps cleanly.
///
/// One instance is created per <c>RunAsync</c> call (i.e. per connected session), so its
/// state never needs to be reset externally — a fresh session gets a fresh tracker.
/// </summary>
public sealed class PumpFailureTracker(int pollRate)
{
    /// <summary>How many times in a row the SAME exception type has to fail before the
    /// bridge's caches are reset once. Small on purpose: a genuinely corrupted collection
    /// throws the identical exception on every tick, so this is reached in a fraction of a
    /// second at any realistic poll rate — the cost of trying the reset is one cache rebuild,
    /// and the cost of NOT trying it is the full ~10s shutdown ceiling below.</summary>
    public const int ResetCachesAfterSameFailures = 5;

    /// <summary>How many ticks a pump may fail in a row (of ANY exception type) before
    /// <see cref="RecordFailure"/> recommends shutting down. The multiplier keeps this at
    /// ~10 seconds of wall-clock time regardless of the configured poll rate.</summary>
    public int MaxConsecutiveFailures { get; } = Math.Max(1, pollRate) * 10;

    /// <summary>Ticks failed in a row, of any exception type. 0 immediately after a success.</summary>
    public int ConsecutiveFailures { get; private set; }

    private readonly int _pollRate = Math.Max(1, pollRate);
    private int _consecutiveSameExceptionType;
    private Type? _lastExceptionType;
    private bool _cachesResetThisRun;

    /// <summary>A tick pumped cleanly: every failure streak resets.</summary>
    public void RecordSuccess()
    {
        ConsecutiveFailures = 0;
        _consecutiveSameExceptionType = 0;
        _lastExceptionType = null;
        _cachesResetThisRun = false;
    }

    /// <summary>Records one failed tick and returns what the caller should do about it.</summary>
    public PumpFailureAction RecordFailure(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        ConsecutiveFailures++;

        bool logFull = ConsecutiveFailures == 1;
        bool logRepeat = !logFull && ConsecutiveFailures % _pollRate == 0;

        Type exceptionType = ex.GetType();
        if (exceptionType == _lastExceptionType)
        {
            _consecutiveSameExceptionType++;
        }
        else
        {
            _lastExceptionType = exceptionType;
            _consecutiveSameExceptionType = 1;
        }

        bool resetCaches = !_cachesResetThisRun && _consecutiveSameExceptionType >= ResetCachesAfterSameFailures;
        if (resetCaches)
        {
            _cachesResetThisRun = true;
        }

        bool shutdown = ConsecutiveFailures >= MaxConsecutiveFailures;

        return new PumpFailureAction(logFull, logRepeat, resetCaches, shutdown, _consecutiveSameExceptionType);
    }
}

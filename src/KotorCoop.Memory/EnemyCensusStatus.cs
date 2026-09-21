namespace KotorCoop.Memory;

/// <summary>
/// The size of the engine creature scan and the bounded combat snapshot selected from it.
/// A non-zero <see cref="DroppedCount"/> is an observable census overflow: the combat
/// channel is intentionally only a bounded view, so callers must not present it as a full
/// module census or assume that an omitted creature is dead.
/// </summary>
public readonly record struct EnemyCensusStatus(int CandidateCount, int SelectedCount, int Capacity)
{
    /// <summary>Heartbeat gate used by the live bridge. The line may change every scan, but
    /// an over-cap module must still produce at most one diagnostic per interval.</summary>
    public static bool ShouldReport(long nowMs, long lastReportMs, int intervalMs) =>
        lastReportMs == long.MinValue || nowMs - lastReportMs >= intervalMs;

    /// <summary>True when at least one scanned candidate was outside the snapshot.</summary>
    public bool Overflow => DroppedCount > 0;

    /// <summary>How many candidates were omitted before the snapshot was published.</summary>
    public int DroppedCount => Math.Max(0, CandidateCount - SelectedCount);

    public override string ToString() =>
        $"census: {CandidateCount} candidate(s), {SelectedCount} selected before ID validation, "
        + $"capacity {Capacity}, {DroppedCount} beyond cap";
}

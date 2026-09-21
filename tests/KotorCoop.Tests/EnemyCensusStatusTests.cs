using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed class EnemyCensusStatusTests
{
    [Fact]
    public void CapacitySizedCensusIsNotReportedAsOverflow()
    {
        EnemyCensusStatus status = new(CandidateCount: 32, SelectedCount: 32, Capacity: 32);

        Assert.False(status.Overflow);
        Assert.Equal(0, status.DroppedCount);
        Assert.Contains("0 beyond cap", status.ToString());
    }

    [Fact]
    public void BeyondCapacityIsDetectedWithoutCallingOmittedEnemiesDead()
    {
        EnemyCensusStatus status = new(CandidateCount: 39, SelectedCount: 32, Capacity: 32);

        Assert.True(status.Overflow);
        Assert.Equal(7, status.DroppedCount);
        Assert.Contains("39 candidate(s)", status.ToString());
        Assert.Contains("7 beyond cap", status.ToString());
    }

    [Fact]
    public void MalformedCountsClampDroppedCountAndStayNonOverflowing()
    {
        EnemyCensusStatus status = new(CandidateCount: 2, SelectedCount: 4, Capacity: 32);

        Assert.False(status.Overflow);
        Assert.Equal(0, status.DroppedCount);
    }

    [Fact]
    public void ChangingOverflowCountsStillWaitForHeartbeat()
    {
        const int heartbeatMs = 30_000;

        Assert.True(EnemyCensusStatus.ShouldReport(1_000, long.MinValue, heartbeatMs));
        Assert.False(EnemyCensusStatus.ShouldReport(1_001, 1_000, heartbeatMs));
        // The line would contain a different candidate count here, but the timestamp gate
        // remains authoritative; otherwise a crowded module can log on every combat tick.
        Assert.False(EnemyCensusStatus.ShouldReport(1_002, 1_000, heartbeatMs));
        Assert.True(EnemyCensusStatus.ShouldReport(31_000, 1_000, heartbeatMs));
    }
}

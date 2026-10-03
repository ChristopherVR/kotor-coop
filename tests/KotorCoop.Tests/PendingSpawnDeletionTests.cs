using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PendingSpawnDeletionTests
{
    [Fact]
    public void SaveWaitsForEngineDeletionAfterControllerSlotWasCleared()
    {
        PendingSpawnDeletion pending = new();
        pending.Track(312, 1234);
        SaveFlushRule flush = new();
        Assert.Equal(SaveFlushRequest.Hold, flush.OnSaveRequested(pending.CountStanding(_ => 1234), false));
        Assert.Equal(SaveFlushStep.KeepHolding, flush.OnFrame(pending.CountStanding(_ => 1234) != 0));
        Assert.Equal(0, pending.CountStanding(_ => 0));
        Assert.Equal(SaveFlushStep.KeepHolding, flush.OnFrame(false));
        Assert.Equal(SaveFlushStep.ReissueClean, flush.OnFrame(false));
    }

    [Fact]
    public void ReusedObjectIdDoesNotHoldTheNewBodyOrSave()
    {
        PendingSpawnDeletion pending = new();
        pending.Track(312, 1234);
        Assert.Equal(0, pending.CountStanding(_ => 5678));
        Assert.Equal(0, pending.CountStanding(_ => 1234));
    }

    [Fact]
    public void WorldInvalidationDiscardsOldPendingBodies()
    {
        PendingSpawnDeletion pending = new();
        pending.Track(312, 1234);
        pending.Clear();
        Assert.Equal(0, pending.CountStanding(_ => 1234));
    }
}

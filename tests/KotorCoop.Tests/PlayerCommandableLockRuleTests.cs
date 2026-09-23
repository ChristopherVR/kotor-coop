using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PlayerCommandableLockRuleTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public void SaveSuspensionRestoresAndReassertsTheCapturedValue(int original)
    {
        var state = new PlayerCommandableLockState();

        Assert.True(state.Acquire(PlayerCommandableLockOwner.SoloSegment, original, out int locked));
        Assert.Equal(0, locked);
        Assert.True(state.SuspendForSave(out int duringSave));
        Assert.Equal(original, duringSave);
        Assert.True(state.IsSuspendedForSave);

        Assert.True(state.ResumeAfterSave(out int afterSave));
        Assert.Equal(0, afterSave);
        Assert.True(state.IsOwned);

        Assert.True(state.Release(PlayerCommandableLockOwner.SoloSegment, out int released));
        Assert.Equal(original, released);
        Assert.False(state.IsOwned);
    }

    [Fact]
    public void OverlappingLocksKeepTheOriginalValueUntilTheLastOwnerLeaves()
    {
        var state = new PlayerCommandableLockState();

        Assert.True(state.Acquire(PlayerCommandableLockOwner.SoloSegment, 1, out _));
        Assert.True(state.Acquire(PlayerCommandableLockOwner.Minigame, 0, out _));
        Assert.False(state.Release(PlayerCommandableLockOwner.SoloSegment, out _));

        Assert.True(state.SuspendForSave(out int duringSave));
        Assert.Equal(1, duringSave);
        Assert.True(state.ResumeAfterSave(out int afterSave));
        Assert.Equal(0, afterSave);

        Assert.True(state.Release(PlayerCommandableLockOwner.Minigame, out int finalValue));
        Assert.Equal(1, finalValue);
    }

    [Fact]
    public void ReleasingTheLastOwnerDuringSaveLeavesTheOriginalValueInPlace()
    {
        var state = new PlayerCommandableLockState();
        state.Acquire(PlayerCommandableLockOwner.Minigame, 1, out _);
        Assert.True(state.SuspendForSave(out int duringSave));
        Assert.Equal(1, duringSave);

        Assert.False(state.Release(PlayerCommandableLockOwner.Minigame, out _));
        Assert.False(state.IsOwned);
        Assert.False(state.ResumeAfterSave(out _));
    }

    [Fact]
    public void WorldInvalidationForgetsLockWithoutProducingANativeRestoreWrite()
    {
        var state = new PlayerCommandableLockState();
        state.Acquire(PlayerCommandableLockOwner.SoloSegment, 1, out _);
        state.Forget(PlayerCommandableLockOwner.SoloSegment);

        Assert.False(state.IsOwned);
        Assert.False(state.SuspendForSave(out _));
        Assert.False(state.Release(PlayerCommandableLockOwner.SoloSegment, out _));
    }

    [Fact]
    public void SaveFlushHoldsAnInputLockedSaveThenReassertsOnlyAfterTheStoreWindow()
    {
        var locks = new PlayerCommandableLockState();
        var flush = new SaveFlushRule(quietFrames: 2, settleFrames: 0);
        Assert.True(locks.Acquire(PlayerCommandableLockOwner.Minigame, 1, out int beforeSave));
        Assert.Equal(0, beforeSave);

        Assert.Equal(SaveFlushRequest.Hold, flush.OnSaveRequested(standing: 0, inputLockOwned: locks.IsOwned));
        Assert.True(locks.SuspendForSave(out int serializedValue));
        Assert.Equal(1, serializedValue);
        Assert.Equal(SaveFlushStep.ReissueClean, flush.OnFrame(flushedOne: false));
        Assert.Equal(SaveFlushRequest.PassThrough, flush.OnSaveRequested(standing: 0, inputLockOwned: locks.IsOwned));

        Assert.Equal(SaveFlushStep.KeepQuiet, flush.OnFrame(flushedOne: false));
        Assert.Equal(1, serializedValue); // the native store still sees the captured baseline
        Assert.Equal(SaveFlushStep.Release, flush.OnFrame(flushedOne: false));
        Assert.True(locks.ResumeAfterSave(out int reassertedValue));
        Assert.Equal(0, reassertedValue);
    }
}

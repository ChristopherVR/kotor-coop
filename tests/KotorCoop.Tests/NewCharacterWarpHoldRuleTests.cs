using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class NewCharacterWarpHoldRuleTests
{
    [Fact]
    public void AnArmedCaptureHoldsTheWarp()
    {
        // 2026-10-24: the host's warp beat the new character's save by ~2 s.
        Assert.True(NewCharacterWarpHoldRule.Holds(captureArmed: true, 0, 0, 0, nowMs: 6_700));
    }

    [Fact]
    public void ACaptureKeepsHoldingUntilTheAdoptionLoadIsApplied()
    {
        Assert.True(NewCharacterWarpHoldRule.Holds(false, capturedAtMs: 15_000, 3, 3, nowMs: 29_000));
        Assert.False(NewCharacterWarpHoldRule.Holds(false, capturedAtMs: 15_000, 3, 4, nowMs: 29_000));
    }

    [Fact]
    public void AnAdoptionThatNeverComesIsReleasedAtTheCeiling()
    {
        long captured = 15_000;
        Assert.True(NewCharacterWarpHoldRule.Holds(false, captured, 3, 3, captured + NewCharacterWarpHoldRule.MaxHoldAfterCaptureMs - 1));
        Assert.False(NewCharacterWarpHoldRule.Holds(false, captured, 3, 3, captured + NewCharacterWarpHoldRule.MaxHoldAfterCaptureMs));
    }

    [Fact]
    public void NoCaptureNeverHolds()
    {
        Assert.False(NewCharacterWarpHoldRule.Holds(false, 0, 0, 0, 50_000));
    }
}

using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The spectate camera's pick-next arithmetic. The candidate list is rebuilt from live
/// state by the controller every pass, so these tests pin down the one pure decision:
/// which listed target the camera holds, given the last hold and the cycle key.
/// </summary>
public class SpectateCycleTests
{
    [Fact]
    public void NobodyToWatchIsZero()
    {
        Assert.Equal(0u, SpectateCycle.Pick([], current: 0, advance: false));
        Assert.Equal(0u, SpectateCycle.Pick([], current: 0xBC, advance: true));
    }

    [Fact]
    public void FirstPassSnapsToTheFirstCandidate()
    {
        Assert.Equal(0xA1u, SpectateCycle.Pick([0xA1, 0xB2, 0xC3], current: 0, advance: false));
    }

    [Fact]
    public void AVanishedTargetSnapsToTheFirstSurvivor()
    {
        // The watched body died or despawned, so it is no longer listed: hold the first
        // survivor rather than going dark — the viewer can cycle onward from there.
        Assert.Equal(0xA1u, SpectateCycle.Pick([0xA1, 0xC3], current: 0xB2, advance: false));
    }

    [Fact]
    public void NoAdvanceHoldsTheCurrentTarget()
    {
        Assert.Equal(0xB2u, SpectateCycle.Pick([0xA1, 0xB2, 0xC3], current: 0xB2, advance: false));
    }

    [Fact]
    public void AdvanceMovesToTheNextEntry()
    {
        Assert.Equal(0xC3u, SpectateCycle.Pick([0xA1, 0xB2, 0xC3], current: 0xB2, advance: true));
    }

    [Fact]
    public void AdvanceWrapsPastTheEnd()
    {
        Assert.Equal(0xA1u, SpectateCycle.Pick([0xA1, 0xB2, 0xC3], current: 0xC3, advance: true));
    }

    [Fact]
    public void AdvanceOnASingleCandidateStaysPut()
    {
        // Cycling with one target is a no-op, not a flicker: the wrap lands on itself.
        Assert.Equal(0xA1u, SpectateCycle.Pick([0xA1], current: 0xA1, advance: true));
    }

    [Fact]
    public void AdvanceWhileUnlistedStillSnapsFirstRatherThanSkippingAhead()
    {
        // The target vanished AND the key fired in the same pass: snapping to the first
        // survivor wins — advancing from a position that no longer exists would skip a
        // body the viewer never saw.
        Assert.Equal(0xA1u, SpectateCycle.Pick([0xA1, 0xC3], current: 0xB2, advance: true));
    }
}

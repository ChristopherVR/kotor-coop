using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class SoloReturnRejoinRuleTests
{
    [Fact]
    public void FullSoloLegFiresOnceAfterIndependentSerialBaselines()
    {
        SoloReturnRejoinState state = SoloReturnRejoinState.Initial;
        (state, bool fire) = Step(state, "manm28aa", 2, "manm28aa", 2, 5, 9, 0, true);
        Assert.False(fire);
        (state, fire) = Step(state, "manm28aa", 2, "manm28ab", 2, 5, 9, 100, true);
        Assert.False(fire);
        Assert.True(state.InSoloLeg);
        (state, fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 5, 9, 1000, true, 2);
        Assert.False(fire);
        (state, fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 5, 9, 3000, true, 2);
        Assert.True(fire);
        (_, fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 5, 9, 6000, true);
        Assert.False(fire);
    }

    [Fact]
    public void ALocalExcursionFiresAfterTheReturnWhenTheWorldsDifferWithoutNeedingAHostSave()
    {
        // Joiner-led race: the LOCAL player is on the track while the host holds the origin.
        SoloReturnRejoinState state = Baseline(5, 9);
        (state, bool fire) = Step(state, "manm26mg", 2, "manm28aa", 2, 6, 9, 100, true);
        Assert.False(fire);
        Assert.True(state.InSoloLeg);
        Assert.True(state.LocalLeg);
        (state, fire) = Step(state, "manm26mg", 2, "manm28aa", 2, 6, 9, 500, true);
        Assert.True(state.InSoloLeg);
        (state, fire) = Step(state, "manm28aa", 7, "manm28aa", 2, 6, 9, 1000, true);
        Assert.False(fire);
        (state, fire) = Step(state, "manm28aa", 7, "manm28aa", 2, 6, 9, 3200, true);
        Assert.True(fire);
    }

    [Fact]
    public void ALocalExcursionThatReturnsWithMatchingWorldsDoesNotFire()
    {
        SoloReturnRejoinState state = Baseline(5, 9);
        (state, _) = Step(state, "manm26mg", 2, "manm28aa", 2, 6, 9, 100, true);
        (state, bool fire) = Step(state, "manm28aa", 2, "manm28aa", 2, 6, 9, 3000, true);
        Assert.False(fire);
        Assert.False(state.InSoloLeg);
    }

    [Fact]
    public void ALocalExcursionEndsIfTheHostLeavesTheOrigin()
    {
        SoloReturnRejoinState state = Baseline(5, 9);
        (state, _) = Step(state, "manm26mg", 2, "manm28aa", 2, 6, 9, 100, true);
        (state, bool fire) = Step(state, "manm26mg", 2, "tar_m02aa", 2, 6, 9, 300, true);
        Assert.False(fire);
        Assert.False(state.InSoloLeg);
    }

    [Fact]
    public void MatchingReturnResetsAndAllowsASecondExcursion()
    {
        SoloReturnRejoinState state = Baseline();
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 0, 0, 10, true);
        (state, _) = Step(state, "manm28aa", 2, "manm28aa", 2, 0, 0, 20, true);
        Assert.True(state.InSoloLeg);
        (state, _) = Step(state, "manm28aa", 2, "manm28aa", 2, 0, 0, 2025, true, 2);
        Assert.False(state.InSoloLeg);
        (state, _) = Step(state, "manm28aa", 2, "manm28aa", 2, 0, 0, 2030, true);
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 0, 0, 2040, true);
        Assert.True(state.InSoloLeg);
    }

    [Fact]
    public void PreExistingGitMismatchNeverArms()
    {
        SoloReturnRejoinState state = SoloReturnRejoinState.Initial;
        (state, _) = Step(state, "manm28aa", 2, "manm28aa", 3, 0, 0, 0, true);
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 3, 0, 0, 10, true);
        (state, bool fire) = Step(state, "manm28aa", 2, "manm28aa", 4, 0, 0, 5000, true);
        Assert.False(fire);
        Assert.False(state.InSoloLeg);
    }

    [Fact]
    public void EitherOwnLoadEdgeCancelsUsingIndependentCounters()
    {
        SoloReturnRejoinState state = Baseline(5, 9);
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 5, 9, 10, true);
        (state, bool fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 6, 9, 5000, true);
        Assert.False(fire);
        Assert.False(state.InSoloLeg);
    }

    [Fact]
    public void MissingSerialCannotFireButZeroIsValid()
    {
        SoloReturnRejoinState state = Baseline();
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 0, 0, 10, true);
        (state, bool fire) = SoloReturnRejoinRule.Observe(
            state, "manm28aa", 2, "manm28aa", 3, null, 0, 2, 1000, true);
        Assert.False(fire);
        (state, fire) = SoloReturnRejoinRule.Observe(
            state, "manm28aa", 2, "manm28aa", 3, 0, 0, 2, 10003, true);
        Assert.False(fire);
        (_, fire) = SoloReturnRejoinRule.Observe(
            state, "manm28aa", 2, "manm28aa", 3, 0, 0, 2, 12004, true);
        Assert.True(fire);
    }

    [Fact]
    public void GateOffDoesNotConsumeTheOneShot()
    {
        SoloReturnRejoinState state = Baseline();
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 0, 0, 10, false);
        (state, bool fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 0, 0, 5000, false, 2);
        Assert.False(fire);
        (_, fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 0, 0, 7000, true, 2);
        Assert.True(fire);
    }

    [Fact]
    public void StaleEqualReturnWaitsForFreshIdentityThenFires()
    {
        SoloReturnRejoinState state = Baseline(5, 9);
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 5, 9, 10, true);
        (state, bool fire) = Step(state, "manm28aa", 2, "manm28aa", 2, 5, 9, 1000, true);
        Assert.False(fire);
        (state, fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 5, 9, 2000, true, 2);
        Assert.False(fire);
        (_, fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 5, 9, 4001, true, 2);
        Assert.True(fire);
    }

    [Fact]
    public void GenuineOtherReturnModuleClearsTheExcursion()
    {
        SoloReturnRejoinState state = Baseline(5, 9);
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 5, 9, 10, true);
        (state, _) = Step(state, "manm28aa", 2, "danm13", 2, 5, 9, 100, true);
        Assert.False(state.InSoloLeg);
    }

    [Fact]
    public void UnknownGitAfterFreshReturnWaitsWithoutDroppingTheExcursion()
    {
        SoloReturnRejoinState state = Baseline(5, 9);
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 5, 9, 10, true);
        (state, bool fire) = Step(state, "manm28aa", 0, "manm28aa", 0, 5, 9, 1000, true, 2);
        Assert.False(fire);
        Assert.True(state.InSoloLeg);
        (state, fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 5, 9, 5001, true, 2);
        Assert.False(fire);
        (_, fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 5, 9, 7002, true, 2);
        Assert.True(fire);
    }

    [Fact]
    public void TogetherBaselineTracksLatestIdentityBeforeDeparture()
    {
        SoloReturnRejoinState state = Baseline(5, 9);
        (state, _) = Step(state, "manm28aa", 2, "manm28aa", 2, 5, 9, 10, true, 2);
        (state, _) = Step(state, "manm28aa", 2, "manm28ab", 2, 5, 9, 20, true);
        (state, bool fire) = Step(state, "manm28aa", 2, "manm28aa", 3, 5, 9, 3000, true, 2);
        Assert.False(fire);
        Assert.True(state.InSoloLeg);
    }

    private static SoloReturnRejoinState Baseline(uint localSerial = 0, uint hostSerial = 0)
    {
        (SoloReturnRejoinState state, _) = Step(
            SoloReturnRejoinState.Initial, "manm28aa", 2, "manm28aa", 2,
            localSerial, hostSerial, 0, true);
        return state;
    }

    private static (SoloReturnRejoinState, bool) Step(
        SoloReturnRejoinState state,
        string localModule,
        uint localGit,
        string hostModule,
        uint hostGit,
        uint localSerial,
        uint hostSerial,
        long now,
        bool gate,
        uint hostIdentity = 1) => SoloReturnRejoinRule.Observe(
            state, localModule, localGit, hostModule, hostGit,
            localSerial, hostSerial, hostIdentity, now, gate);
}

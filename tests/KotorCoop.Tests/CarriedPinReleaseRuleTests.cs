using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The decision half of the carried min-1-hp pin release
/// (docs/review-seamless-2026-09-18.md blocker #8): a joiner's character captured on the Endar
/// Spire's command deck carried the tutorial's pin into every later world.
/// </summary>
public sealed class CarriedPinReleaseRuleTests
{
    [Theory]
    [InlineData(false, "tar_m02aa", true)]
    [InlineData(true, "", true)]
    [InlineData(false, "", false)]
    public void WaitsWhileThePlayerOrModuleIsUnreadable(bool playerResolved, string module, bool pinned)
    {
        Assert.Equal(CarriedPinReleaseAction.NotReady, CarriedPinReleaseRule.Decide(playerResolved, module, pinned));
    }

    [Theory]
    [InlineData("end_m01aa")]
    [InlineData("END_M01AA")]
    public void LeavesTheFlagToAModuleThatPinsAtEntry(string module)
    {
        // k_pend_area01 pins the PC 0.1 s after entry; racing it would either strip the
        // tutorial's own pin or be undone by it. Whatever the flag reads there is the module's.
        Assert.Equal(CarriedPinReleaseAction.LeaveModulePin, CarriedPinReleaseRule.Decide(true, module, pinned: true));
        Assert.Equal(CarriedPinReleaseAction.LeaveModulePin, CarriedPinReleaseRule.Decide(true, module, pinned: false));
    }

    [Theory]
    [InlineData("end_m01ab")]
    [InlineData("tar_m02aa")]
    [InlineData("tar_m02ae")] // the duel ring pins around each duel, never at entry
    [InlineData("sta_m45ac")]
    public void ReleasesACarriedPinAnywhereElse(string module)
    {
        Assert.Equal(CarriedPinReleaseAction.Release, CarriedPinReleaseRule.Decide(true, module, pinned: true));
    }

    [Fact]
    public void DoesNothingForAnUnpinnedPlayer()
    {
        Assert.Equal(CarriedPinReleaseAction.NothingToRelease, CarriedPinReleaseRule.Decide(true, "tar_m02aa", pinned: false));
    }

    [Fact]
    public void OnlyTheEndarSpireCommandDeckIsListedAsPinningAtEntry()
    {
        // Pins the list: adding a module here is a research finding (min-one-hitpoint.md),
        // not a convenience.
        Assert.Equal(["end_m01aa"], CarriedPinReleaseRule.ModulesThatPinAtEntry);
    }
}

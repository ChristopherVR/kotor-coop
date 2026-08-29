using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="MinigameModuleRule"/> — "never follow a peer onto a single-seat minigame track"
/// (docs/research/minigames.md §24). The catalogue itself is checked against the four modules
/// a stock GOG v1.0.3 install actually ships with a <c>MiniGame</c> struct in their area file,
/// because a typo here fails SILENTLY: the follow simply happens anyway and the symptom is a
/// dead client, not a failing assertion.
/// </summary>
public class MinigameModuleRuleTests
{
    [Theory]
    [InlineData("tar_m03mg")]  // the Taris swoop track (MiniGame/Type = 1)
    [InlineData("TAR_M03MG")]  // the engine's own module names are inconsistently cased
    [InlineData("manm26mg")]   // Manaan swoop
    [InlineData("tat_m17mg")]  // Tatooine swoop
    [InlineData("m12ab")]      // the Leviathan turret (Type = 2)
    [InlineData("M12ab")]
    public void TheStockTracksAreRecognised(string module) =>
        Assert.True(MinigameModuleRule.IsMinigameModule(module));

    [Theory]
    [InlineData("tar_m03af")]  // the Bek garage the race is entered FROM — a normal module
    [InlineData("tar_m11aa")]
    [InlineData("END_M01AA")]
    [InlineData("")]
    public void OrdinaryModulesAreNot(string module) =>
        Assert.False(MinigameModuleRule.IsMinigameModule(module));

    [Fact]
    public void ExtraModulesFromConfigAreHonoured() =>
        Assert.True(MinigameModuleRule.IsMinigameModule("mod_track01", ["mod_track01"]));

    [Fact]
    public void AnEmptyExtraEntryNeverMatchesAnEmptyModuleName()
    {
        // A trailing comma in coop.ini's minigame_modules yields an empty entry; it must not
        // turn "module name unreadable" into "that's a minigame track".
        Assert.False(MinigameModuleRule.IsMinigameModule(string.Empty, [string.Empty]));
    }

    [Fact]
    public void FollowIsRefusedWhenThePeerPublishesTheMinigameFlag() =>
        Assert.True(MinigameModuleRule.ShouldRefuseFollow(
            enabled: true, ownerInMinigame: true, targetModule: "some_unknown_track"));

    [Fact]
    public void FollowIsRefusedWhenTheDestinationIsAKnownTrackEvenBeforeTheFlagArrives() =>
        Assert.True(MinigameModuleRule.ShouldRefuseFollow(
            enabled: true, ownerInMinigame: false, targetModule: "tar_m03mg"));

    [Fact]
    public void FollowIsAllowedForAnOrdinaryModule() =>
        Assert.False(MinigameModuleRule.ShouldRefuseFollow(
            enabled: true, ownerInMinigame: false, targetModule: "tar_m03af"));

    [Fact]
    public void TheKillSwitchRestoresTheOldBehaviour()
    {
        Assert.False(MinigameModuleRule.ShouldRefuseFollow(
            enabled: false, ownerInMinigame: true, targetModule: "tar_m03mg"));
    }
}

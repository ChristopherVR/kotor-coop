using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Tier 2 item 18 task 2's plugin-side follow-up: whether a client's galaxy-map accept-button
/// click should be refused before it runs the confirm script. See
/// <see cref="GalaxyMapConfirmSuppressionRule"/>'s own doc comment for why this mirrors
/// <c>KotorCoop.Session.GalaxyMapConfirmRefusalRule</c> in decision shape but is restated with
/// plain booleans (the plugin cannot reference that assembly).
/// </summary>
public class GalaxyMapConfirmSuppressionRuleTests
{
    [Fact]
    public void RefusesAClientUnderHostLeadModeWhenEnabled()
    {
        Assert.True(GalaxyMapConfirmSuppressionRule.ShouldRefuse(
            enabled: true, isClientInLiveSession: true, followLeadHost: true));
    }

    [Fact]
    public void LeavesAClientAloneUnderAnyLeadMode()
    {
        Assert.False(GalaxyMapConfirmSuppressionRule.ShouldRefuse(
            enabled: true, isClientInLiveSession: true, followLeadHost: false));
    }

    [Fact]
    public void NeverRefusesTheHostUnderEitherMode()
    {
        Assert.False(GalaxyMapConfirmSuppressionRule.ShouldRefuse(
            enabled: true, isClientInLiveSession: false, followLeadHost: true));
        Assert.False(GalaxyMapConfirmSuppressionRule.ShouldRefuse(
            enabled: true, isClientInLiveSession: false, followLeadHost: false));
    }

    [Fact]
    public void MasterSwitchOffNeverRefusesRegardlessOfEverythingElse()
    {
        Assert.False(GalaxyMapConfirmSuppressionRule.ShouldRefuse(
            enabled: false, isClientInLiveSession: true, followLeadHost: true));
    }
}

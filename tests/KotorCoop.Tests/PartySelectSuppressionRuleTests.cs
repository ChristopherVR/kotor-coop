using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind Tier 2 item 18's party-select suppression: whether THIS instance
/// should tear down its own copy of the retail party-selection screen. See
/// <see cref="PartySelectSuppressionRule"/>'s own doc comment for why
/// <c>isClientInLiveSession</c> — <c>SharedBlockView.TryGetHostPartyRoster</c>'s valid bit — is
/// the plugin's stand-in for "am I a client right now", reused rather than invented fresh.
/// </summary>
public class PartySelectSuppressionRuleTests
{
    [Fact]
    public void SuppressesOnlyWhenEnabledAndAClientWithALiveHostRoster()
    {
        Assert.True(PartySelectSuppressionRule.ShouldSuppress(enabled: true, isClientInLiveSession: true));
    }

    [Fact]
    public void LeavesTheHostsOwnScreenAlone()
    {
        // The host never has a valid TryGetHostPartyRoster read (that field is only ever
        // written for a client) — so even with the feature enabled, this must be false.
        Assert.False(PartySelectSuppressionRule.ShouldSuppress(enabled: true, isClientInLiveSession: false));
    }

    [Fact]
    public void RespectsTheKillSwitchEvenOnAClient()
    {
        Assert.False(PartySelectSuppressionRule.ShouldSuppress(enabled: false, isClientInLiveSession: true));
    }

    [Fact]
    public void OffAndNotAClientIsAlsoFalse()
    {
        Assert.False(PartySelectSuppressionRule.ShouldSuppress(enabled: false, isClientInLiveSession: false));
    }
}

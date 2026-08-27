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

    // IsAlreadyHandled: the 2026-08-28 live-pass fix for CGuiInGame+0x78 never going null after
    // this controller's own sweep-teardown — a re-detected address identical to one already
    // torn down this module/session must not be written through again. See the rule's own doc
    // comment for the full evidence (70,000+ suppress/sweep pairs on one client, ~500/second).

    [Fact]
    public void ANeverBeforeSeenPanelIsNotAlreadyHandled()
    {
        Assert.False(PartySelectSuppressionRule.IsAlreadyHandled(candidate: 0x1000, lastHandled: 0));
    }

    [Fact]
    public void TheIdenticalAddressAlreadyTornDownIsAlreadyHandled()
    {
        Assert.True(PartySelectSuppressionRule.IsAlreadyHandled(candidate: 0x1000, lastHandled: 0x1000));
    }

    [Fact]
    public void ADifferentAddressThanTheOneAlreadyTornDownIsNotAlreadyHandled()
    {
        // A genuinely different panel (e.g. after a module change reset lastHandled to 0 and a
        // fresh CGuiInGame allocated its own party-select object at a new address) must still
        // get suppressed.
        Assert.False(PartySelectSuppressionRule.IsAlreadyHandled(candidate: 0x2000, lastHandled: 0x1000));
    }
}

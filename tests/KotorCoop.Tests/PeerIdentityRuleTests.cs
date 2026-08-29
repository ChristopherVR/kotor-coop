using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The identity-source ordering a peer's phantom is dressed by: peer packet &gt; that peer's
/// last published row &gt; leave the body alone. Nothing local is ever a source — the bug this
/// closes is a peer coming out of a module change wearing something that was not its own.
/// </summary>
public class PeerIdentityRuleTests
{
    [Fact]
    public void ZeroIsNotAnAppearance()
    {
        Assert.False(PeerIdentityRule.IsUsableAppearance(0));
        Assert.True(PeerIdentityRule.IsUsableAppearance(114));
        Assert.True(PeerIdentityRule.IsUsableAppearance(0xFFFF));

        // Wider than the u16 field it lands in: a torn read, not a row.
        Assert.False(PeerIdentityRule.IsUsableAppearance(0x1_0000));
    }

    [Fact]
    public void ThePeersOwnPacketWins()
    {
        Assert.Equal(378u, PeerIdentityRule.ChooseAppearance(published: 378, cached: 114));
    }

    [Fact]
    public void ACachedRowCoversTheTransitionWindowWhereThePeerPublishesNothing()
    {
        // The exact warp case: the peer's agent is re-resolving its player object and publishes
        // 0 for about a second, which is when the phantom is re-spawned.
        Assert.Equal(114u, PeerIdentityRule.ChooseAppearance(published: 0, cached: 114));
    }

    [Fact]
    public void WithNeitherAPacketNorACacheNothingIsApplied()
    {
        Assert.Equal(0u, PeerIdentityRule.ChooseAppearance(published: 0, cached: 0));
        Assert.False(PeerIdentityRule.ShouldApply(0, onBody: 114, twinDressed: false));
    }

    [Fact]
    public void ARowAlreadyOnTheBodyStillHasToReachTheClientTwin()
    {
        // THE REGRESSION. The server field matching is NOT proof the peer renders correctly:
        // at spawn there is no twin to dress, so the twin is built from the blueprint and the
        // old "does the server field differ" check answered "already applied" forever after.
        Assert.True(PeerIdentityRule.ShouldApply(378, onBody: 378, twinDressed: false));
        Assert.False(PeerIdentityRule.ShouldApply(378, onBody: 378, twinDressed: true));
    }

    [Fact]
    public void AChangedRowIsAppliedEvenAfterTheTwinWasDressed()
    {
        Assert.True(PeerIdentityRule.ShouldApply(114, onBody: 378, twinDressed: true));
    }

    [Fact]
    public void TheChoiceIsCompareByTruncatedRowNotByWiderValue()
    {
        // ShouldApply's body comparison is against the u16 the field actually holds.
        Assert.False(PeerIdentityRule.ShouldApply(0x0172, onBody: 0x0172, twinDressed: true));
    }
}

using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The reaction half of solo-segment spectate: what a LOCAL flag decides for the
/// owner (hide every peer) versus what a REMOTE peer's flag decides for an observer (spectate
/// them) — deliberately asymmetric, see <see cref="SoloSegmentRule"/>'s own doc comment.</summary>
public class SoloSegmentRuleTests
{
    [Fact]
    public void HidesAllPeersWhileTheLocalPlayerOwnsAnActiveSegment()
    {
        SoloSegmentRule rule = new(Enabled: true);
        Assert.True(rule.ShouldHideAllPeers(localInSoloSegment: true));
    }

    [Fact]
    public void DoesNotHidePeersWhenTheLocalPlayerIsNotInASegment()
    {
        SoloSegmentRule rule = new(Enabled: true);
        Assert.False(rule.ShouldHideAllPeers(localInSoloSegment: false));
    }

    [Fact]
    public void TheToggleOffNeverHidesPeersEvenWhileOwningASegment()
    {
        SoloSegmentRule rule = new(Enabled: false);
        Assert.False(rule.ShouldHideAllPeers(localInSoloSegment: true));
    }

    [Fact]
    public void SpectatesAPeerPublishingTheFlagInTheSameModule()
    {
        SoloSegmentRule rule = new(Enabled: true);
        Assert.True(rule.ShouldSpectate(peerInSoloSegment: true, sameModule: true));
    }

    [Fact]
    public void DoesNotSpectateAPeerInADifferentModule()
    {
        // A stale wire read, or a peer who has since moved on — must never lock a player's
        // input for content they cannot even see.
        SoloSegmentRule rule = new(Enabled: true);
        Assert.False(rule.ShouldSpectate(peerInSoloSegment: true, sameModule: false));
    }

    [Fact]
    public void DoesNotSpectateAPeerNotInASegment()
    {
        SoloSegmentRule rule = new(Enabled: true);
        Assert.False(rule.ShouldSpectate(peerInSoloSegment: false, sameModule: true));
    }

    [Fact]
    public void TheToggleOffNeverSpectatesEvenInTheSameModule()
    {
        SoloSegmentRule rule = new(Enabled: false);
        Assert.False(rule.ShouldSpectate(peerInSoloSegment: true, sameModule: true));
    }
}

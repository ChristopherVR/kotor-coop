using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The gating rule behind roadmap item 8 (minigames): a player inside the engine's own
/// minigame mode — <c>PlayerState.FlagInMinigame</c>, set from <c>CSWCModule::GetMiniGame</c>
/// — is not meaningfully standing in the shared world, so a peer's instance should park
/// their phantom instead of mirroring it, and this instance should stop applying forwarded
/// damage and its own presence census while the local player is the one racing or manning a
/// turret. Each of the three questions below is checked independently, because they answer
/// to DIFFERENT flags (a peer's vs. the local player's) and a caller wiring the wrong one in
/// is exactly the kind of mistake this rule exists to catch before it is live.
/// </summary>
public class MinigameRuleTests
{
    [Fact]
    public void HidesAPeerInAMinigameWhenTheToggleIsOn()
    {
        MinigameRule rule = new(HidePeersEnabled: true, SpectateEnabled: false);
        Assert.True(rule.ShouldHidePeer(peerInMinigame: true));
    }

    [Fact]
    public void DoesNotHideAPeerNotInAMinigame()
    {
        MinigameRule rule = new(HidePeersEnabled: true, SpectateEnabled: false);
        Assert.False(rule.ShouldHidePeer(peerInMinigame: false));
    }

    [Fact]
    public void TheToggleOffKeepsMirroringEvenInAMinigame()
    {
        // coop.ini minigame_hide_peer = 0: the player asked for the old behaviour back.
        MinigameRule rule = new(HidePeersEnabled: false, SpectateEnabled: false);
        Assert.False(rule.ShouldHidePeer(peerInMinigame: true));
    }

    [Fact]
    public void OnlyThePeersOwnFlagDecidesWhetherTheyAreHidden()
    {
        // The LOCAL player being in a minigame says nothing about whether a DIFFERENT peer's
        // body belongs in this instance's world right now — that is the peer's own flag.
        MinigameRule rule = new(HidePeersEnabled: true, SpectateEnabled: false);
        Assert.False(rule.ShouldHidePeer(peerInMinigame: false));
    }

    [Fact]
    public void SuppressesIncomingDamageOnlyWhileTheLocalPlayerIsInAMinigame()
    {
        MinigameRule rule = new(HidePeersEnabled: true, SpectateEnabled: false);
        Assert.True(rule.ShouldSuppressIncomingDamage(localInMinigame: true));
        Assert.False(rule.ShouldSuppressIncomingDamage(localInMinigame: false));
    }

    [Fact]
    public void IncomingDamageSuppressionDoesNotDependOnTheHidePeerToggle()
    {
        // Unlike ShouldHidePeer, this one is not gated by a coop.ini key — a player who
        // cannot be hit in a turret cannot be hit in a turret regardless of how peers are
        // displayed.
        MinigameRule rule = new(HidePeersEnabled: false, SpectateEnabled: false);
        Assert.True(rule.ShouldSuppressIncomingDamage(localInMinigame: true));
    }

    [Fact]
    public void SuppressesPresencePublishOnlyWhileTheLocalPlayerIsInAMinigame()
    {
        MinigameRule rule = new(HidePeersEnabled: true, SpectateEnabled: false);
        Assert.True(rule.ShouldSuppressPresencePublish(localInMinigame: true));
        Assert.False(rule.ShouldSuppressPresencePublish(localInMinigame: false));
    }
}

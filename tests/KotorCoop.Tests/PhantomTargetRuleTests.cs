using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind <c>PhantomController.ApplyTargetable</c> — whether a peer's
/// stand-in sits in the PLAYER faction this pass. Added with the stealth half of
/// docs/research/effect-sync.md §12 (2026-09-18, <c>PlayerState.FlagStealth</c> /
/// <c>stealth_sync</c>); the first three inputs are the pre-existing rule, unchanged.
/// </summary>
public class PhantomTargetRuleTests
{
    // ---- the pre-existing rule, pinned unchanged ------------------------------------------

    [Fact]
    public void ALivingPeerOnAnOwningInstanceIsTargetable()
    {
        Assert.True(PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false, stealthSync: true, stealthed: false));
    }

    [Theory]
    [InlineData(false, true, false)]   // phantom_targetable off
    [InlineData(true, false, false)]   // peer is down
    [InlineData(true, true, true)]     // the local_combat authority split holds it Neutral
    public void TheOldReasonsStillHoldTheBodyNeutral(bool targetable, bool alive, bool holdNeutral)
    {
        Assert.False(PhantomTargetRule.WantsPlayerFaction(targetable, alive, holdNeutral, stealthSync: true, stealthed: false));
        Assert.False(PhantomTargetRule.IsStealthHold(targetable, alive, holdNeutral, stealthSync: true, stealthed: false));
    }

    // ---- stealth --------------------------------------------------------------------------

    [Fact]
    public void AStealthedPeerIsHeldNeutralWhileTheKeyIsOn()
    {
        Assert.False(PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false, stealthSync: true, stealthed: true));
        Assert.True(PhantomTargetRule.IsStealthHold(
            targetable: true, alive: true, holdNeutral: false, stealthSync: true, stealthed: true));
    }

    [Fact]
    public void StealthSyncOffIgnoresTheBitEntirely()
    {
        // A v52 peer never publishes the bit; a v53 peer whose partner turned the key off gets
        // exactly the pre-2026-09-18 behaviour — targetable, and not logged as a stealth hold.
        Assert.True(PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false, stealthSync: false, stealthed: true));
        Assert.False(PhantomTargetRule.IsStealthHold(
            targetable: true, alive: true, holdNeutral: false, stealthSync: false, stealthed: true));
    }

    [Fact]
    public void DroppingOutOfStealthMakesTheBodyTargetableAgain()
    {
        // FlagStealth is a LEVEL republished every tick, so the edge ApplyTargetable acts on
        // is simply the rule's answer changing between two passes.
        Assert.False(PhantomTargetRule.WantsPlayerFaction(true, true, false, stealthSync: true, stealthed: true));
        Assert.True(PhantomTargetRule.WantsPlayerFaction(true, true, false, stealthSync: true, stealthed: false));
    }

    [Fact]
    public void AStealthHoldIsOnlyNamedWhenStealthIsTheReason()
    {
        // Down AND stealthed: the body is Neutral because the peer is down; the log must not
        // claim stealth for it.
        Assert.False(PhantomTargetRule.IsStealthHold(
            targetable: true, alive: false, holdNeutral: false, stealthSync: true, stealthed: true));

        // The authority split AND stealthed: same — that instance holds every peer Neutral.
        Assert.False(PhantomTargetRule.IsStealthHold(
            targetable: true, alive: true, holdNeutral: true, stealthSync: true, stealthed: true));
    }
}

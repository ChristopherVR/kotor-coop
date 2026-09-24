using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind <c>PhantomController.ApplyTargetable</c> — whether a peer's
/// stand-in sits in the PLAYER faction this pass. Added with the stealth half of
/// docs/research/effect-sync.md §12 (2026-09-18, <c>PlayerState.FlagStealth</c> /
/// <c>stealth_sync</c>); extended with the menu-guard half of §13 (2026-09-24,
/// <c>PlayerState.FlagMenuGuard</c> / <c>menu_guard_sync</c>) — the first three inputs
/// (<c>targetable</c>/<c>alive</c>/<c>holdNeutral</c>) are the original rule, unchanged.
/// </summary>
public class PhantomTargetRuleTests
{
    // ---- the pre-existing rule, pinned unchanged ------------------------------------------

    [Fact]
    public void ALivingPeerOnAnOwningInstanceIsTargetable()
    {
        Assert.True(PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false,
            stealthSync: true, stealthed: false, menuGuardSync: true, menuGuarded: false));
    }

    [Theory]
    [InlineData(false, true, false)]   // phantom_targetable off
    [InlineData(true, false, false)]   // peer is down
    [InlineData(true, true, true)]     // the local_combat authority split holds it Neutral
    public void TheOldReasonsStillHoldTheBodyNeutral(bool targetable, bool alive, bool holdNeutral)
    {
        Assert.False(PhantomTargetRule.WantsPlayerFaction(
            targetable, alive, holdNeutral, stealthSync: true, stealthed: false, menuGuardSync: true, menuGuarded: false));
        Assert.False(PhantomTargetRule.IsStealthHold(targetable, alive, holdNeutral, stealthSync: true, stealthed: false));
        Assert.False(PhantomTargetRule.IsMenuGuardHold(targetable, alive, holdNeutral, menuGuardSync: true, menuGuarded: false));
    }

    // ---- stealth --------------------------------------------------------------------------

    [Fact]
    public void AStealthedPeerIsHeldNeutralWhileTheKeyIsOn()
    {
        Assert.False(PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false,
            stealthSync: true, stealthed: true, menuGuardSync: true, menuGuarded: false));
        Assert.True(PhantomTargetRule.IsStealthHold(
            targetable: true, alive: true, holdNeutral: false, stealthSync: true, stealthed: true));
    }

    [Fact]
    public void StealthSyncOffIgnoresTheBitEntirely()
    {
        // A v52 peer never publishes the bit; a v53 peer whose partner turned the key off gets
        // exactly the pre-2026-09-18 behaviour — targetable, and not logged as a stealth hold.
        Assert.True(PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false,
            stealthSync: false, stealthed: true, menuGuardSync: true, menuGuarded: false));
        Assert.False(PhantomTargetRule.IsStealthHold(
            targetable: true, alive: true, holdNeutral: false, stealthSync: false, stealthed: true));
    }

    [Fact]
    public void DroppingOutOfStealthMakesTheBodyTargetableAgain()
    {
        // FlagStealth is a LEVEL republished every tick, so the edge ApplyTargetable acts on
        // is simply the rule's answer changing between two passes.
        Assert.False(PhantomTargetRule.WantsPlayerFaction(
            true, true, false, stealthSync: true, stealthed: true, menuGuardSync: true, menuGuarded: false));
        Assert.True(PhantomTargetRule.WantsPlayerFaction(
            true, true, false, stealthSync: true, stealthed: false, menuGuardSync: true, menuGuarded: false));
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

    // ---- menu guard (docs/research/effect-sync.md §13, docs/limitations.md's Pausing row) -

    [Fact]
    public void AMenuGuardedPeerIsHeldNeutralWhileTheKeyIsOn()
    {
        Assert.False(PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false,
            stealthSync: true, stealthed: false, menuGuardSync: true, menuGuarded: true));
        Assert.True(PhantomTargetRule.IsMenuGuardHold(
            targetable: true, alive: true, holdNeutral: false, menuGuardSync: true, menuGuarded: true));
    }

    [Fact]
    public void MenuGuardSyncOffIgnoresTheBitEntirely()
    {
        // The protection is an opt-in lever like stealth_sync: turning it off restores the
        // pre-menu-guard behaviour exactly, for a peer whose menu is open right now.
        Assert.True(PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false,
            stealthSync: true, stealthed: false, menuGuardSync: false, menuGuarded: true));
        Assert.False(PhantomTargetRule.IsMenuGuardHold(
            targetable: true, alive: true, holdNeutral: false, menuGuardSync: false, menuGuarded: true));
    }

    [Fact]
    public void ClosingTheMenuMakesTheBodyTargetableAgain()
    {
        // FlagMenuGuard is a LEVEL like FlagStealth: the edge ApplyTargetable acts on is the
        // rule's answer changing between two passes, driven by MenuGuardRule's own timer.
        Assert.False(PhantomTargetRule.WantsPlayerFaction(
            true, true, false, stealthSync: true, stealthed: false, menuGuardSync: true, menuGuarded: true));
        Assert.True(PhantomTargetRule.WantsPlayerFaction(
            true, true, false, stealthSync: true, stealthed: false, menuGuardSync: true, menuGuarded: false));
    }

    [Fact]
    public void AMenuGuardHoldIsOnlyNamedWhenMenuGuardIsTheReason()
    {
        // Down AND menu-guarded: Neutral because the peer is down; the log must not claim
        // menu-guard for it.
        Assert.False(PhantomTargetRule.IsMenuGuardHold(
            targetable: true, alive: false, holdNeutral: false, menuGuardSync: true, menuGuarded: true));

        // The authority split AND menu-guarded: same — that instance holds every peer Neutral.
        Assert.False(PhantomTargetRule.IsMenuGuardHold(
            targetable: true, alive: true, holdNeutral: true, menuGuardSync: true, menuGuarded: true));
    }

    [Fact]
    public void StealthAndMenuGuardTogetherStillHoldTheBodyNeutralExactlyOnce()
    {
        // A player can open a menu while stealthed - either reason alone, or both together,
        // must hold the body Neutral (not require BOTH, and not double-release on drop of
        // just one).
        Assert.False(PhantomTargetRule.WantsPlayerFaction(
            true, true, false, stealthSync: true, stealthed: true, menuGuardSync: true, menuGuarded: true));

        // Menu closes first, still stealthed: still Neutral, for the stealth reason.
        Assert.False(PhantomTargetRule.WantsPlayerFaction(
            true, true, false, stealthSync: true, stealthed: true, menuGuardSync: true, menuGuarded: false));
        Assert.True(PhantomTargetRule.IsStealthHold(true, true, false, stealthSync: true, stealthed: true));

        // Then stealth ends too: only now does the body become targetable.
        Assert.True(PhantomTargetRule.WantsPlayerFaction(
            true, true, false, stealthSync: true, stealthed: false, menuGuardSync: true, menuGuarded: false));
    }

    // ---- partner_buffs (docs/research/partner-buffs.md) ------------------------------------

    [Theory]
    [InlineData(false, false, 0)]   // Kind.Neutral
    [InlineData(false, true, 0)]    // a hold stays Neutral whatever the key says
    [InlineData(true, false, 1)]    // Kind.Player: key OFF is byte-for-byte the old behaviour
    [InlineData(true, true, 2)]     // Kind.Friendly
    public void FactionForIsAFourRowTable(bool wantsPlayerFaction, bool partnerBuffs, int expected)
    {
        Assert.Equal((PhantomTargetRule.Kind)expected, PhantomTargetRule.FactionFor(wantsPlayerFaction, partnerBuffs));
    }

    [Theory]
    [InlineData(false, true, false, false, false)]  // phantom_targetable off
    [InlineData(true, false, false, false, false)]  // peer is down
    [InlineData(true, true, true, false, false)]    // local_combat authority split
    [InlineData(true, true, false, true, false)]    // stealthed peer
    [InlineData(true, true, false, false, true)]    // menu-guarded peer
    [InlineData(true, true, false, true, true)]     // both at once
    public void EveryHoldReasonKeepsTheBodyNeutralEvenWithPartnerBuffsOn(
        bool targetable, bool alive, bool holdNeutral, bool stealthed, bool menuGuarded)
    {
        bool want = PhantomTargetRule.WantsPlayerFaction(
            targetable, alive, holdNeutral, stealthSync: true, stealthed, menuGuardSync: true, menuGuarded);
        Assert.False(want);
        Assert.Equal(PhantomTargetRule.Kind.Neutral, PhantomTargetRule.FactionFor(want, partnerBuffs: true));
    }

    [Fact]
    public void ALivingUnheldPeerBecomesFriendlyOnlyWhenPartnerBuffsIsOn()
    {
        bool want = PhantomTargetRule.WantsPlayerFaction(
            targetable: true, alive: true, holdNeutral: false,
            stealthSync: true, stealthed: false, menuGuardSync: true, menuGuarded: false);
        Assert.Equal(PhantomTargetRule.Kind.Player, PhantomTargetRule.FactionFor(want, partnerBuffs: false));
        Assert.Equal(PhantomTargetRule.Kind.Friendly, PhantomTargetRule.FactionFor(want, partnerBuffs: true));
    }
}

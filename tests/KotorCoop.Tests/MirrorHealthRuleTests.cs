using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What a mirroring instance writes onto its copy of an enemy, given the owner's value.
///
/// Three designs have been through this line and each failure is a test below. Absolute in
/// both directions erased the local player's damage (measured: a soldier pinned at 11 hit
/// points for a ninety-second fight). Lower-only fixed that and lost owner-side healing
/// entirely. What ships now is lower-absolutely, raise-by-delta — the owner's damage still
/// drags the copy down, and the owner's HEALING is applied as the amount it healed rather
/// than as a jump to its number, which would also undo local damage the owner has not
/// spent yet.
/// </summary>
public class MirrorHealthRuleTests
{
    [Fact]
    public void TheFirstSightingAdoptsTheOwnersValue()
    {
        Assert.Equal(9, MirrorHealthRule.Target(
            current: 15, max: 15, ownerHealth: 9, lastOwnerHealth: 0, primed: false));
    }

    [Fact]
    public void TheOwnersDamagePullsOurCopyDown()
    {
        Assert.Equal(6, MirrorHealthRule.Target(
            current: 12, max: 15, ownerHealth: 6, lastOwnerHealth: 12, primed: true));
    }

    [Fact]
    public void ALocalHitTheOwnerHasNotSpentYetIsNotUndone()
    {
        // ⚠ The measured failure. We hit it to 8; the owner still says 15 because our
        // damage is a tick or two from being applied over there. Writing 15 back is what
        // used to erase the fight — and now that the damage travels as its own cumulative
        // ledger, there is nothing to gain by doing it.
        Assert.Equal(8, MirrorHealthRule.Target(
            current: 8, max: 15, ownerHealth: 15, lastOwnerHealth: 15, primed: true));
    }

    [Fact]
    public void OwnerSideHealingPropagatesAsTheAmountHealed()
    {
        // Our copy is at 8 (we hit it), the owner heals its copy from 15 to 20. Ours should
        // gain the 5 it was healed, not jump to 20 and swallow our own damage on the way.
        Assert.Equal(13, MirrorHealthRule.Target(
            current: 8, max: 30, ownerHealth: 20, lastOwnerHealth: 15, primed: true));
    }

    [Fact]
    public void AHealNeverExceedsOurMaximum()
    {
        Assert.Equal(15, MirrorHealthRule.Target(
            current: 14, max: 15, ownerHealth: 40, lastOwnerHealth: 20, primed: true));
    }

    [Fact]
    public void AnUnknownMaximumDoesNotClampTheHeal()
    {
        // A failed read of max hit points must not silently cap the creature at 1.
        Assert.Equal(19, MirrorHealthRule.Target(
            current: 14, max: 0, ownerHealth: 40, lastOwnerHealth: 35, primed: true));
    }

    [Fact]
    public void NetMovementIsWhatCounts()
    {
        // The owner healed to 20 and was then hit back down to 17 between our passes. We
        // only see the endpoints, and +2 is the right answer for both readings of it.
        Assert.Equal(10, MirrorHealthRule.Target(
            current: 8, max: 30, ownerHealth: 17, lastOwnerHealth: 15, primed: true));
    }

    [Fact]
    public void TheCopyIsNeverWrittenToZero()
    {
        // ⚠ A creature at 0 hit points that no death path has run for reads as ALIVE to
        // targeting and AI and as dead to nothing — the zombie class of bug. Death arrives
        // from the owner as a flag and is executed through the engine, never as a health
        // value of zero.
        Assert.Equal(1, MirrorHealthRule.Target(
            current: 4, max: 15, ownerHealth: 0, lastOwnerHealth: 4, primed: true));

        Assert.Equal(1, MirrorHealthRule.Target(
            current: 1, max: 15, ownerHealth: 1, lastOwnerHealth: 5, primed: true));
    }

    [Fact]
    public void TheCopiesConvergeOnceTheDamageHasRoundTripped()
    {
        // Our copy dips to 8 while the owner still reads 15; the owner then spends our
        // damage and reads 8 too. No bounce in between, and the same number at the end.
        int current = MirrorHealthRule.Target(8, 15, ownerHealth: 15, lastOwnerHealth: 15, primed: true);
        Assert.Equal(8, current);

        current = MirrorHealthRule.Target(current, 15, ownerHealth: 8, lastOwnerHealth: 15, primed: true);
        Assert.Equal(8, current);
    }

    [Fact]
    public void AHealFollowedByASteadyOwnerSettlesOnTheOwnersNumber()
    {
        // The raise-by-delta leg can briefly put us above the owner (it healed 5 from 15,
        // we were at 14, so we go to 19 while it reads 20 — fine). If the owner then holds
        // still below us for any reason, the lower-absolutely leg pulls us back into line
        // rather than leaving two numbers drifting apart.
        int current = MirrorHealthRule.Target(14, 30, ownerHealth: 20, lastOwnerHealth: 15, primed: true);
        Assert.Equal(19, current);

        current = MirrorHealthRule.Target(current, 30, ownerHealth: 18, lastOwnerHealth: 20, primed: true);
        Assert.Equal(18, current);
    }
}

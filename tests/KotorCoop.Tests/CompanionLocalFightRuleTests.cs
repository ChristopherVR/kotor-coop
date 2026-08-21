using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The decision half of the companion local-combat release gate (roadmap item 4) —
/// <see cref="PartyPresenceController"/>'s answer to "whose companions fight on a client, and
/// against what," recorded in docs/research/party-reconciliation.md's dated 2026-08-21 entry.
///
/// A companion may only be handed its own engine AI when ALL of: the flag is on, the roster is
/// not frozen by a script, `local_combat`'s own exclusion has a real fight running, and the
/// companion's host-driven position is within range of BOTH the fighting enemy and this
/// instance's own player — "the players are actually fighting side by side," not merely both
/// individually near something hostile. Any single failing gate must fall back to mirroring, and
/// the release/reclaim decision must be edge-triggered exactly like <see cref="PartyFreezeRule"/>
/// so a fight lasting many seconds produces two log lines, not one per frame.
/// </summary>
public class CompanionLocalFightRuleTests
{
    private const bool Enabled = true;
    private const bool NotFrozen = false;

    [Fact]
    public void AllGatesOpenAndNotReleasedBeforeReleasesAndLogs()
    {
        Assert.Equal(
            CompanionLocalFightAction.ReleaseAndLog,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: true, nearEnemy: true, nearPlayer: true, releasedBefore: false));
    }

    [Fact]
    public void AllGatesOpenAndAlreadyReleasedStaysReleasedSilently()
    {
        Assert.Equal(
            CompanionLocalFightAction.StayReleased,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: true, nearEnemy: true, nearPlayer: true, releasedBefore: true));
    }

    [Fact]
    public void NeverReleasedAndGatesClosedMirrorsSilently()
    {
        Assert.Equal(
            CompanionLocalFightAction.Mirror,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: false, nearEnemy: false, nearPlayer: false, releasedBefore: false));
    }

    [Fact]
    public void WasReleasedAndTheFightEndsReclaimsAndLogs()
    {
        Assert.Equal(
            CompanionLocalFightAction.ReclaimAndLog,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: false, nearEnemy: true, nearPlayer: true, releasedBefore: true));
    }

    [Fact]
    public void TheFlagOffNeverReleasesEvenIfEveryOtherGateIsOpen()
    {
        Assert.Equal(
            CompanionLocalFightAction.Mirror,
            CompanionLocalFightRule.Decide(
                enabled: false, NotFrozen, localFightActive: true, nearEnemy: true, nearPlayer: true, releasedBefore: false));
    }

    [Fact]
    public void TheFlagGoingOffWhileReleasedReclaimsAndLogs()
    {
        Assert.Equal(
            CompanionLocalFightAction.ReclaimAndLog,
            CompanionLocalFightRule.Decide(
                enabled: false, NotFrozen, localFightActive: true, nearEnemy: true, nearPlayer: true, releasedBefore: true));
    }

    [Fact]
    public void ARosterFreezeNeverReleasesEvenIfEveryOtherGateIsOpen()
    {
        Assert.Equal(
            CompanionLocalFightAction.Mirror,
            CompanionLocalFightRule.Decide(
                Enabled, rosterFrozen: true, localFightActive: true, nearEnemy: true, nearPlayer: true, releasedBefore: false));
    }

    [Fact]
    public void ARosterFreezeWhileReleasedReclaimsAndLogs()
    {
        Assert.Equal(
            CompanionLocalFightAction.ReclaimAndLog,
            CompanionLocalFightRule.Decide(
                Enabled, rosterFrozen: true, localFightActive: true, nearEnemy: true, nearPlayer: true, releasedBefore: true));
    }

    [Fact]
    public void NoLocalFightNeverReleasesEvenWhenSomehowMarkedNear()
    {
        Assert.Equal(
            CompanionLocalFightAction.Mirror,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: false, nearEnemy: true, nearPlayer: true, releasedBefore: false));
    }

    [Fact]
    public void FarFromTheEnemyKeepsMirroring()
    {
        // Near the player, side by side with them, but the local fight is somewhere else
        // entirely — a materialised body swapped mid-exclusion, or a stale reading.
        Assert.Equal(
            CompanionLocalFightAction.Mirror,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: true, nearEnemy: false, nearPlayer: true, releasedBefore: false));
    }

    [Fact]
    public void FarFromThisInstancesOwnPlayerKeepsMirroring()
    {
        // Near the enemy (host-driven position happens to sit close to it) but this instance's
        // own player is somewhere else — the exact "companion anchored to the host, not this
        // player" coherence break the design doc calls out.
        Assert.Equal(
            CompanionLocalFightAction.Mirror,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: true, nearEnemy: true, nearPlayer: false, releasedBefore: false));
    }

    [Fact]
    public void FarFromBothKeepsMirroring()
    {
        Assert.Equal(
            CompanionLocalFightAction.Mirror,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: true, nearEnemy: false, nearPlayer: false, releasedBefore: false));
    }

    [Fact]
    public void DriftingOutOfRangeWhileReleasedReclaimsAndLogs()
    {
        Assert.Equal(
            CompanionLocalFightAction.ReclaimAndLog,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: true, nearEnemy: false, nearPlayer: true, releasedBefore: true));
    }

    [Fact]
    public void TheFightEndingWhileReleasedReclaimsAndLogs()
    {
        // local_combat's own exclusion has already cleared (the enemy died, disengaged, or timed
        // out) — the companion has nothing left to fight alongside.
        Assert.Equal(
            CompanionLocalFightAction.ReclaimAndLog,
            CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: false, nearEnemy: false, nearPlayer: false, releasedBefore: true));
    }

    [Fact]
    public void EveryFrameOfARealisticFightProducesExactlyTwoEdges()
    {
        bool releasedBefore = false;
        int releases = 0, reclaims = 0, mirrored = 0, silent = 0;

        // Ten frames apart, twenty frames clustered and released, ten frames apart again.
        bool[] nearReadings =
        [
            false, false, false, false, false, false, false, false, false, false,
            .. Enumerable.Repeat(true, 20),
            false, false, false, false, false, false, false, false, false, false,
        ];

        foreach (bool near in nearReadings)
        {
            CompanionLocalFightAction action = CompanionLocalFightRule.Decide(
                Enabled, NotFrozen, localFightActive: true, nearEnemy: near, nearPlayer: near, releasedBefore);
            releasedBefore = action is CompanionLocalFightAction.ReleaseAndLog or CompanionLocalFightAction.StayReleased;

            switch (action)
            {
                case CompanionLocalFightAction.ReleaseAndLog: releases++; break;
                case CompanionLocalFightAction.ReclaimAndLog: reclaims++; break;
                case CompanionLocalFightAction.Mirror: mirrored++; break;
                case CompanionLocalFightAction.StayReleased: silent++; break;
            }
        }

        Assert.Equal(1, releases);
        Assert.Equal(1, reclaims);
        // Nineteen mirrored: the first ten apart frames plus nine of the last ten (the tenth is
        // the reclaim edge itself, counted separately above).
        Assert.Equal(19, mirrored);
        Assert.Equal(19, silent); // twenty clustered frames, minus the one that is the release edge
    }
}

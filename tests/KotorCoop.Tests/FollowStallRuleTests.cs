using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="FollowStallRule"/> against the ACTUAL live capture the fix exists for —
/// docs/research/party-reconciliation.md round 97's own 30-second bit-identical sample of Trask
/// (0x127) wedged in <c>PathWaitDest</c> on the client after
/// <c>warp end_m01aa</c>/<c>tp 20 22 -1.3</c>/<c>walk 10 15</c>:
///
/// <code>
/// phase=7 attempts=0 pathNode=19 destNode=19 writeCursor=20 moveState=3
/// pos=(10.51,16.78) leaderPos=(10.78,15.70) target=(10.78,15.70)
/// targetVsSnapshotDist=1.105 pathWaitDestAccept=False distSqToTarget=0.0000 distSqToPrior=0.0000
/// </code>
///
/// Every "the real numbers" test below feeds those exact values, so a future round that retunes a
/// threshold finds out immediately whether it has stopped covering the one stall this rule was
/// built for. The rest pin each gate individually, including the anti-spin budget that keeps a
/// per-frame controller from writing an engine field 500 times a second.
/// </summary>
public sealed class FollowStallRuleTests
{
    // Round 97's live capture, field for field.
    private const int LivePhase = 7;
    private const int LiveAttempts = 0;
    private const int LivePathNode = 19;
    private const int LiveDestNode = 19;
    private const float LiveDistSqToTarget = 0.0f;
    private const float LiveDistSqToPrior = 0.0f;

    /// <summary>pos=(10.51,16.78) vs leaderPos=(10.78,15.70): 0.27² + 1.08² = 1.2393.</summary>
    private const float LiveLeaderDistanceSquared = 1.2393f;

    private static bool ShouldRecover(
        int phase = LivePhase,
        int attempts = LiveAttempts,
        int pathNode = LivePathNode,
        int destNode = LiveDestNode,
        float distSqToTarget = LiveDistSqToTarget,
        float distSqToPrior = LiveDistSqToPrior,
        double frozenSeconds = 30.0,
        float leaderDistanceSquared = LiveLeaderDistanceSquared,
        bool leaderMoving = false,
        bool suppressed = false,
        int consecutiveRecoveries = 0,
        double secondsSinceLastRecovery = double.PositiveInfinity) =>
        FollowStallRule.ShouldRecover(
            phase,
            attempts,
            pathNode,
            destNode,
            distSqToTarget,
            distSqToPrior,
            frozenSeconds,
            leaderDistanceSquared,
            leaderMoving,
            suppressed,
            consecutiveRecoveries,
            secondsSinceLastRecovery,
            FollowStallRule.StallThresholdSeconds,
            FollowStallRule.MinLeaderDistanceSquared,
            FollowStallRule.MaxConsecutiveRecoveries,
            FollowStallRule.RecoveryCooldownSeconds);

    [Fact]
    public void Round97sOwnLiveCaptureIsRecognisedAsAWedge()
    {
        Assert.True(FollowStallRule.IsWedgedShape(
            LivePhase, LiveAttempts, LivePathNode, LiveDestNode, LiveDistSqToTarget, LiveDistSqToPrior));
    }

    [Fact]
    public void Round97sOwnLiveCaptureFiresARecovery()
    {
        Assert.True(ShouldRecover());
    }

    [Fact]
    public void TheStrictAcceptTestCannotFireOnATie()
    {
        // PathWaitDest's fcompp/test ah,5/jp at 0x00512BF2 rejects on ST0 >= ST1 AND on unordered
        // — only a STRICT ST0 < ST1 falls through to the accept. Round 97's capture is 0 vs 0.
        Assert.True(FollowStallRule.CannotAccept(0f, 0f));
        Assert.True(FollowStallRule.CannotAccept(1.5f, 0.25f));
        Assert.False(FollowStallRule.CannotAccept(0.25f, 1.5f));
    }

    [Fact]
    public void AnAcceptableGeometryIsLeftAloneEvenWhenEverythingElseLooksStalled()
    {
        // The engine's own test would pass on its own next call — never interfere with that.
        Assert.False(FollowStallRule.IsWedgedShape(
            LivePhase, LiveAttempts, LivePathNode, LiveDestNode, distSqToTarget: 0.05f, distSqToPrior: 1.2f));
        Assert.False(ShouldRecover(distSqToTarget: 0.05f, distSqToPrior: 1.2f));
    }

    [Theory]
    [InlineData(FollowStallRule.PathWaitDestPhase, true)]
    [InlineData(FollowStallRule.PathWaitPredecessorPhase, true)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(5, false)]
    [InlineData(10, false)]
    public void OnlyTheTwoHandlersThatShareTheStrictAcceptTestAreWatched(int phase, bool watched)
    {
        Assert.Equal(watched, FollowStallRule.IsStallPhase(phase));
        Assert.Equal(watched, ShouldRecover(phase: phase));
    }

    [Fact]
    public void APhase2StallIsLeftToPhase2StallRule()
    {
        // Deliberately disjoint from Phase2StallRule.MovingToPathPhase — two rules, two shapes,
        // no overlap, so a live A/B of one is never confounded by the other firing too.
        Assert.False(FollowStallRule.IsStallPhase(Phase2StallRule.MovingToPathPhase));
    }

    [Fact]
    public void ACyclingFsmIsNeverPoked()
    {
        // attempts is structurally pinned at 0 for this wedge (the outer dispatcher's per-call
        // gate never reopens). Anything else means the FSM is genuinely being re-entered.
        Assert.False(FollowStallRule.IsWedgedShape(
            LivePhase, attempts: 1, LivePathNode, LiveDestNode, LiveDistSqToTarget, LiveDistSqToPrior));
        Assert.False(ShouldRecover(attempts: 3));
    }

    [Fact]
    public void ADestNodeTheSearchCanStillWalkTowardIsNotAWedge()
    {
        // pathNode != destNode means PathWaitDest's forward search has somewhere to go and takes
        // its OTHER, unconditional accept branch — the shape TryAdvanceStalledPathDestination
        // already manufactures on purpose.
        Assert.False(FollowStallRule.IsWedgedShape(
            LivePhase, LiveAttempts, pathNode: 19, destNode: 21, LiveDistSqToTarget, LiveDistSqToPrior));
        Assert.False(ShouldRecover(pathNode: 19, destNode: 21));
    }

    [Fact]
    public void AWedgeThatHasNotHeldLongEnoughIsLeftAlone()
    {
        Assert.False(ShouldRecover(frozenSeconds: FollowStallRule.StallThresholdSeconds - 0.01));
        Assert.True(ShouldRecover(frozenSeconds: FollowStallRule.StallThresholdSeconds));
    }

    [Fact]
    public void ACompanionStandingInFormationBesideAStationaryLeaderIsNeverPoked()
    {
        // The whole reason for a distance gate: a caught-up companion parked next to an idle
        // leader must not be shoved around, whatever its follow record happens to read.
        Assert.False(ShouldRecover(leaderDistanceSquared: 0.25f));
        Assert.False(ShouldRecover(leaderDistanceSquared: FollowStallRule.MinLeaderDistanceSquared));
    }

    [Fact]
    public void AWedgedCompanionIsRecoveredAtAnyDistanceWhileTheLeaderIsWalkingAway()
    {
        // "I walked off and it never followed" is the actual complaint — a leader in motion makes
        // the separation a certainty, so distance stops being the deciding input.
        Assert.True(ShouldRecover(leaderDistanceSquared: 0.01f, leaderMoving: true));
    }

    [Fact]
    public void NothingIsPokedWhileSomethingElseOwnsTheCompanion()
    {
        Assert.False(ShouldRecover(suppressed: true));
    }

    [Fact]
    public void TheCooldownSeparatesTwoFires()
    {
        Assert.False(ShouldRecover(
            consecutiveRecoveries: 1,
            secondsSinceLastRecovery: FollowStallRule.RecoveryCooldownSeconds - 0.01));
        Assert.True(ShouldRecover(
            consecutiveRecoveries: 1,
            secondsSinceLastRecovery: FollowStallRule.RecoveryCooldownSeconds));
    }

    [Fact]
    public void TheBudgetLatchesOffAfterEnoughFailedAttempts()
    {
        for (int fired = 0; fired < FollowStallRule.MaxConsecutiveRecoveries; fired++)
        {
            Assert.True(ShouldRecover(consecutiveRecoveries: fired));
            Assert.False(FollowStallRule.ShouldLatchOff(fired, FollowStallRule.MaxConsecutiveRecoveries));
        }

        Assert.False(ShouldRecover(consecutiveRecoveries: FollowStallRule.MaxConsecutiveRecoveries));
        Assert.True(FollowStallRule.ShouldLatchOff(
            FollowStallRule.MaxConsecutiveRecoveries, FollowStallRule.MaxConsecutiveRecoveries));
    }

    [Fact]
    public void RealMovementAfterAPokeCountsAsARecovery()
    {
        Assert.True(FollowStallRule.RecoveryConfirmed(
            0.6f * 0.6f, FollowStallRule.RecoveryConfirmedDistanceSquared));

        // Float noise and the sub-centimetre jitter PartyFollowController's own MovedEpsilon
        // already tolerates must never read as "it worked."
        Assert.False(FollowStallRule.RecoveryConfirmed(
            0.05f * 0.05f, FollowStallRule.RecoveryConfirmedDistanceSquared));
        Assert.False(FollowStallRule.RecoveryConfirmed(
            0f, FollowStallRule.RecoveryConfirmedDistanceSquared));
    }

    [Theory]
    [InlineData(FollowStallRule.MethodPhaseReset, true)]
    [InlineData(FollowStallRule.MethodFollowLeader, true)]
    [InlineData(FollowStallRule.MethodPositionParty, true)]
    [InlineData(FollowStallRule.MethodBreakPriorTie, true)]
    [InlineData(FollowStallRule.MethodAdvanceDestNode, true)]
    [InlineData(FollowStallRule.MethodPushPriorPastNode, true)]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(-1, false)]
    public void OnlyImplementedRecoveryMethodsAreAccepted(int method, bool known)
    {
        // A typo in coop.ini must degrade to "do nothing", never to "write an engine field with a
        // value nobody chose."
        Assert.Equal(known, FollowStallRule.IsKnownMethod(method));
    }

    [Fact]
    public void TheFourMethodIdsAreDistinctAndStable()
    {
        // These travel through coop.ini and the followstall dev verb; renumbering them silently
        // would repoint a documented config value at a different engine write.
        Assert.Equal(1, FollowStallRule.MethodPhaseReset);
        Assert.Equal(2, FollowStallRule.MethodFollowLeader);
        Assert.Equal(3, FollowStallRule.MethodPositionParty);
        Assert.Equal(4, FollowStallRule.MethodBreakPriorTie);
        Assert.Equal(5, FollowStallRule.MethodAdvanceDestNode);
        Assert.Equal(6, FollowStallRule.MethodPushPriorPastNode);
    }

    [Fact]
    public void TheShippedDefaultIsTheOneMethodTheLiveAbActuallyRecovered()
    {
        // Round 105's rig round ran all six against the same reproduced wedge; only
        // positionparty ended with the companion back beside its leader (16.5 units behind ->
        // 0.88 units, one call). If a future round changes this, it needs its own live A/B — see
        // FollowStallRule.DefaultMethod's own remarks for what each of the other five did.
        Assert.Equal(FollowStallRule.MethodPositionParty, FollowStallRule.DefaultMethod);
        Assert.True(FollowStallRule.IsKnownMethod(FollowStallRule.DefaultMethod));
    }

    [Fact]
    public void ThePriorPushMultipleGuaranteesTheStrictAcceptCanFire()
    {
        // Method 6's whole point: |node - Prior| becomes PriorPushMultiple x |node - Target|, so
        // the STRICT compare at 0x00512BF2 has real headroom instead of landing on the tie
        // method 4 was live-measured converging onto. Squared distances, so the ratio is the
        // multiple squared.
        Assert.True(FollowStallRule.PriorPushMultiple > 1.0f);

        const float distSqToTarget = 0.0207f; // round 105's own live capture
        float distSqToPrior = FollowStallRule.PriorPushMultiple
            * FollowStallRule.PriorPushMultiple * distSqToTarget;

        Assert.False(FollowStallRule.CannotAccept(distSqToTarget, distSqToPrior));
    }

    [Fact]
    public void TheGatesEveryPriorCandidateFailedOnAreTheOnesThisRuleDoesNotUse()
    {
        // Regression guard for the four "why did nothing ever fire" findings this rule closes:
        //   * Phase2StallRule needs phase 2 and > 3 units — the wedge is phase 7 at ~1.11 units.
        //   * TryAdvanceStalledPathDestination needs writeCursor - pathNode >= 2 — it is 1.
        //   * PartyFollowController's reissue check needs > 3 units squared > 9 — it is 1.2393.
        // This rule must still fire on exactly that data.
        Assert.True(LiveLeaderDistanceSquared < 3.0f * 3.0f);
        Assert.True(LiveLeaderDistanceSquared > FollowStallRule.MinLeaderDistanceSquared);
        Assert.True(ShouldRecover());
    }
}

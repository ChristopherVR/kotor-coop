using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="FinalNodeGeometryRule"/> is the pure decision logic behind round 90's
/// <c>Odyssey.TryRefreshFinalNodeTarget</c> (<c>docs/research/party-reconciliation.md</c>) — a
/// geometry-only alternative to round 88's <c>Odyssey.TryAcceptFinalPathNode</c> that refreshes
/// the engine's own <c>TargetPoint</c>/<c>PriorPoint</c> inputs at the exact terminal follow
/// stall, instead of forcing the accept/reject test's output. Pinned here rather than inside
/// <c>PartyFollowController</c>'s unsafe engine-pointer code, mirroring
/// <see cref="PartyFollowTransitionRuleTests"/>/<see cref="PartyFreezeRuleTests"/>'s own shape.
/// </summary>
public sealed class FinalNodeGeometryRuleTests
{
    [Fact]
    public void TheExactTerminalShapeIsRecognised()
    {
        // phase 7 (PathWaitDest), current node already equal to dest node, exactly one recorded
        // trail entry ahead — the live shape rounds 88/89 both describe.
        Assert.True(FinalNodeGeometryRule.IsTerminalStallShape(phase: 7, pathNode: 30, destNode: 30, gap: 1));
    }

    [Fact]
    public void AnyOtherPhaseIsNotTheShape()
    {
        // Round 88's own fix (TryAdvanceStalledMovingToPath) already handles phase 2; phase 3
        // (Moving_To_Path) is genuinely mid-flight. Neither is this function's business.
        Assert.False(FinalNodeGeometryRule.IsTerminalStallShape(phase: 2, pathNode: 30, destNode: 30, gap: 1));
        Assert.False(FinalNodeGeometryRule.IsTerminalStallShape(phase: 3, pathNode: 30, destNode: 30, gap: 1));
        Assert.False(FinalNodeGeometryRule.IsTerminalStallShape(phase: 0, pathNode: 30, destNode: 30, gap: 1));
    }

    [Fact]
    public void AMismatchedPathAndDestNodeIsNotTheShape()
    {
        // Genuinely still mid-search — PathWaitDest's own forward search has not yet caught the
        // current node up to the dest node.
        Assert.False(FinalNodeGeometryRule.IsTerminalStallShape(phase: 7, pathNode: 28, destNode: 30, gap: 3));
    }

    [Fact]
    public void GapZeroIsGenuinelyCaughtUpNotTheTerminalShape()
    {
        // party-reconciliation.md round 88: "gap==0 is genuinely caught up, nothing recorded" —
        // TryAcceptFinalPathNode's own remarks draw the identical line; this rule must match it.
        Assert.False(FinalNodeGeometryRule.IsTerminalStallShape(phase: 7, pathNode: 30, destNode: 30, gap: 0));
    }

    [Fact]
    public void GapTwoOrMoreIsTryAdvanceStalledPathDestinationsShapeNotThisOnes()
    {
        // Round 83's own TryAdvanceStalledPathDestination handles gap>=2 by poking DestNode
        // forward; this rule must not also claim that shape.
        Assert.False(FinalNodeGeometryRule.IsTerminalStallShape(phase: 7, pathNode: 30, destNode: 30, gap: 2));
    }

    [Fact]
    public void ANegativeGapBeforeWrapCorrectionIsNeverThisRulesConcern()
    {
        // The caller wraps a negative writeCursor-pathNode delta into 0..99 before calling this
        // rule (the circular-buffer convention every sibling fix in this file follows) — a raw
        // negative value reaching here at all would be a caller bug, and must not be silently
        // treated as the terminal shape.
        Assert.False(FinalNodeGeometryRule.IsTerminalStallShape(phase: 7, pathNode: 30, destNode: 30, gap: -99));
    }

    [Fact]
    public void ARealDriftBeyondTheEpsilonShouldRefresh()
    {
        // The leader has genuinely moved a meaningful distance (well beyond the ~1cm epsilon)
        // since TargetPoint was last written — the exact staleness round 89's hypothesis needs
        // ruled out.
        Assert.True(FinalNodeGeometryRule.ShouldRefresh(
            targetX: 20f, targetY: 22f, leaderX: 45f, leaderY: 35f));
    }

    [Fact]
    public void ATinySubEpsilonDriftShouldNotRefresh()
    {
        // Within ~1cm — already effectively fresh; writing again would be a no-op at best and a
        // needless SnapshotPendingFlag stomp at worst (see TryDriveFollowTarget's own remarks on
        // why that flag should not be set every frame once caught up).
        Assert.False(FinalNodeGeometryRule.ShouldRefresh(
            targetX: 45f, targetY: 35f, leaderX: 45.001f, leaderY: 35.001f));
    }

    [Fact]
    public void AnExactMatchShouldNotRefresh()
    {
        // Zero drift — the boundary case; must not treat "identical" as "needs refreshing" (that
        // would immediately re-collapse Prior/Target into the exact degenerate point round 83's
        // fix exists to avoid).
        Assert.False(FinalNodeGeometryRule.ShouldRefresh(
            targetX: 12.5f, targetY: -3.25f, leaderX: 12.5f, leaderY: -3.25f));
    }

    [Fact]
    public void ADriftJustPastTheEpsilonBoundaryShouldRefresh()
    {
        // The epsilon is ~1cm squared (0.0001). A drift of exactly 0.011 units on one axis
        // (0.011^2 = 0.000121 > 0.0001) must cross it.
        Assert.True(FinalNodeGeometryRule.ShouldRefresh(
            targetX: 0f, targetY: 0f, leaderX: 0.011f, leaderY: 0f));
    }

    [Fact]
    public void DriftOnEitherAxisAloneIsEnoughToTriggerARefresh()
    {
        // Mirrors every other planar distance test this whole follow-FSM investigation uses
        // (stall detection, FollowKickRange, MovedEpsilon in PartyFollowController) — the
        // signature takes no Z at all, and a meaningful drift on just one axis must still cross
        // the epsilon on its own, not require both axes to move together.
        Assert.True(FinalNodeGeometryRule.ShouldRefresh(targetX: 0f, targetY: 5f, leaderX: 2f, leaderY: 5f));
        Assert.True(FinalNodeGeometryRule.ShouldRefresh(targetX: 5f, targetY: 0f, leaderX: 5f, leaderY: 2f));
    }

    [Fact]
    public void AnOrdinaryIndexIsUnchanged()
    {
        // Well below the 100-slot boundary — the common case, every frame the terminal shape is
        // NOT active at index 99.
        Assert.Equal(31, FinalNodeGeometryRule.WrapTrailIndex(31));
    }

    [Fact]
    public void IndexOneHundredWrapsToZero()
    {
        // The exact boundary Odyssey.TryExtendFinalNodeTrail hits when the terminal node sits at
        // the last slot (writeCursor==99, pathNode==98) — the one case a naive "just add one"
        // caller would silently write past the array's own 100-slot bound.
        Assert.Equal(0, FinalNodeGeometryRule.WrapTrailIndex(100));
    }

    [Fact]
    public void IndexNinetyNineIsStillInRangeAndUnchanged()
    {
        // The last valid slot before the wrap — must NOT be treated as needing correction.
        Assert.Equal(99, FinalNodeGeometryRule.WrapTrailIndex(99));
    }

    [Fact]
    public void PhaseSevenIsExtendable()
    {
        // The originally-documented shape (round 88/89) — IsExtendableTerminalStallShape must
        // still recognise it, not just the new phase-6 case.
        Assert.True(FinalNodeGeometryRule.IsExtendableTerminalStallShape(
            phase: 7, pathNode: 30, destNode: 30, gap: 1));
    }

    [Fact]
    public void PhaseSixIsAlsoExtendable()
    {
        // Live finding (2026-08-24 A/B round): phase 6's handler (0x00512A60) is structurally
        // identical to PathWaitDest (0x00512B40) — same search, same fcompp test, same gap==1
        // trigger — confirmed by fresh disassembly. TryExtendFinalNodeTrail's own fix mechanism
        // applies to it unmodified, unlike TryAcceptFinalPathNode/TryRefreshFinalNodeTarget which
        // stay phase-7-only via IsTerminalStallShape.
        Assert.True(FinalNodeGeometryRule.IsExtendableTerminalStallShape(
            phase: 6, pathNode: 32, destNode: 32, gap: 1));
    }

    [Fact]
    public void NeitherPhaseSixNorSevenIsNotExtendable()
    {
        Assert.False(FinalNodeGeometryRule.IsExtendableTerminalStallShape(
            phase: 5, pathNode: 30, destNode: 30, gap: 1));
        Assert.False(FinalNodeGeometryRule.IsExtendableTerminalStallShape(
            phase: 3, pathNode: 30, destNode: 30, gap: 1));
    }

    [Fact]
    public void ExtendableShapeStillRequiresGapOfExactlyOne()
    {
        // gap==0 is genuinely caught up; gap>=2 is TryAdvanceStalledPathDestination's own,
        // already-working shape — this fix candidate must not double-claim either.
        Assert.False(FinalNodeGeometryRule.IsExtendableTerminalStallShape(
            phase: 6, pathNode: 32, destNode: 32, gap: 0));
        Assert.False(FinalNodeGeometryRule.IsExtendableTerminalStallShape(
            phase: 7, pathNode: 30, destNode: 30, gap: 2));
    }

    [Fact]
    public void ExtendableShapeStillRequiresPathNodeEqualsDestNode()
    {
        Assert.False(FinalNodeGeometryRule.IsExtendableTerminalStallShape(
            phase: 6, pathNode: 28, destNode: 32, gap: 5));
    }
}

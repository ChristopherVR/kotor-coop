namespace KotorCoop.Plugin;

/// <summary>
/// Pure decision logic behind <see cref="Odyssey.TryRefreshFinalNodeTarget"/> (round 90,
/// <c>docs/research/party-reconciliation.md</c>) — pulled out of that unsafe engine-pointer
/// function so the terminal-stall shape detection and the "would this write actually change
/// anything" check can be pinned by a unit test, mirroring
/// <see cref="PartyFollowTransitionRule"/>/<see cref="PartyFreezeRule"/>/
/// <see cref="CompanionLocalFightRule"/>'s own established pattern of keeping the branching
/// logic testable and the raw pointer arithmetic thin.
///
/// Both fields this rule reasons about — the terminal shape (round 88's
/// <c>Odyssey.TryAcceptFinalPathNode</c>) and the "already fresh" epsilon (round 90's new
/// geometry-only companion to it) — are shared with that sibling function so a future round
/// changing one does not silently diverge from the other; if the shape or the epsilon needs to
/// change, it should change once, here.
/// </summary>
internal static class FinalNodeGeometryRule
{
    /// <summary>~1cm squared. Below this, the stored <c>TargetPoint</c> is already effectively at
    /// the live leader position: writing again would either be a no-op or, worse, risk producing
    /// a near-zero <c>Prior-to-Target</c> segment — the exact degenerate shape round 83's own fix
    /// (<see cref="Odyssey.TryDriveFollowTarget"/>) was built to avoid.</summary>
    private const float AlreadyFreshDistanceSquared = 0.0001f;

    /// <summary>
    /// The exact terminal stall shape <see cref="Odyssey.TryAcceptFinalPathNode"/> (round 88) and
    /// <see cref="Odyssey.TryRefreshFinalNodeTarget"/> (round 90) both restrict themselves to:
    /// phase 7 (<c>PathWaitDest</c>), the companion's current recorded path node already equal to
    /// its own dest node, and exactly one recorded trail entry ahead of it in the circular buffer
    /// (<paramref name="gap"/> — <c>writeCursor - pathNode</c>, already wrapped into
    /// <c>0..99</c> by the caller) — genuinely the LAST node ever recorded for this leg, nothing
    /// further to walk toward even once accepted.
    /// </summary>
    public static bool IsTerminalStallShape(int phase, int pathNode, int destNode, int gap) =>
        phase == 7 && pathNode == destNode && gap == 1;

    /// <summary>
    /// Whether refreshing <c>TargetPoint</c>/<c>PriorPoint</c> to the live leader position is
    /// actually worth doing — the currently-stored target has drifted more than the "already
    /// fresh" epsilon above from where the leader genuinely is right now. Planar (X/Y) only,
    /// matching every other distance test this follow-FSM investigation has used (Z is stage
    /// geometry, not follow-relevant drift).
    /// </summary>
    public static bool ShouldRefresh(float targetX, float targetY, float leaderX, float leaderY)
    {
        float dx = targetX - leaderX;
        float dy = targetY - leaderY;
        return ((dx * dx) + (dy * dy)) >= AlreadyFreshDistanceSquared;
    }
}

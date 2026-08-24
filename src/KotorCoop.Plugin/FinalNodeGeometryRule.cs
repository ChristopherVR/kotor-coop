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

    /// <summary>
    /// Fix candidate 1 (2026-08-24 follow-up round, <c>docs/research/party-reconciliation.md</c>'s
    /// "the <c>fcompp</c> at 0x512BF2, read without a debugger" entry): the arithmetic behind
    /// <see cref="Odyssey.TryExtendFinalNodeTrail"/> — appending one synthetic node to the shared,
    /// 100-slot circular trail buffer (<c>CSWPartyFollowDataArray</c>, embedded at
    /// <c>party+0x2DC</c>) at exactly the terminal-stall shape <see cref="IsTerminalStallShape"/>
    /// already detects.
    ///
    /// Round 83's own disassembly of <c>PathWaitDest</c>'s forward search (0x00512B60-0x512B6E,
    /// <see cref="Odyssey.TryAdvanceStalledPathDestination"/>'s own remarks) established that a
    /// <c>DestNode</c> set to the SAME value as the array's write cursor is structurally
    /// unreachable: the search's own "give up" exit tests <c>index == WriteCursor</c> immediately
    /// AFTER incrementing, one iteration before it could ever re-test that incremented index
    /// against a <c>DestNode</c> set to that same value — so the new node's index must become
    /// strictly less than the write cursor BEFORE <c>PathWaitDest</c> next runs, not merely equal
    /// to what the write cursor used to be. This wraps the write cursor forward by exactly one slot
    /// (the same 100-slot circular-buffer boundary <c>TryAdvanceStalledPathDestination</c>'s own
    /// wrap-on-write already uses) so the caller can write the synthetic node at the OLD cursor
    /// value and then commit the NEW cursor value, leaving <c>DestNode = oldCursor &lt;
    /// newCursor</c> — reachable, not the unreachable equal-to-cursor shape round 83 already ruled
    /// out.
    /// </summary>
    public static int WrapTrailIndex(int index) => index >= 100 ? index - 100 : index;

    /// <summary>
    /// LIVE FINDING (2026-08-24, same follow-up round, first live A/B of fix candidate 1): the
    /// planned repro (round 88/89/90's own <c>tp 20 22 -1.3</c> / <c>walk 10 15</c> recipe) hit a
    /// DIFFERENT, previously-undocumented permanent stall before Trask ever reached phase 7 at
    /// all — parked at <c>phase=6</c>, <c>pathNode==destNode==32</c>, <c>gap==1</c> (writeCursor
    /// 33), position frozen, for 44+ continuous seconds. Fresh disassembly of phase 6's own
    /// handler (dispatch index 7, <c>tools/disasm.py 0x51C7A0 260</c> resolves the jump table;
    /// <c>tools/disasm.py 0x512A60 220</c> disassembles the handler itself, never examined before
    /// this round) finds it BYTE-FOR-BYTE STRUCTURALLY IDENTICAL to <c>PathWaitDest</c>
    /// (0x00512B40, phase 7): the exact same forward search from <c>CurrentPathNode</c> toward
    /// <c>DestNode</c>, the exact same <c>fcompp</c>-based "is the recorded node strictly closer to
    /// <c>Target</c> than to <c>Prior</c>" geometric test, gated on the exact same
    /// <c>DestNode==CurrentPathNode</c>-on-first-comparison shape — the ONLY difference is the
    /// unconditional-accept tail writes <c>phase=1</c> (0x00512B2A) instead of <c>phase=3</c>
    /// (PathWaitDest's own 0x00512C0A). Live data for this stall matched the shape exactly:
    /// <c>target==prior==recordedDestPoint</c>, all three bit-identical to the live leader position
    /// — <c>distSqToTarget=0</c> AND <c>distSqToPrior=0</c> (both exactly zero, an even more
    /// degenerate case than round 89's own phase-7 data, where only <c>distSqToPrior</c> was ~0)
    /// — so the strict <c>ST0&lt;ST1</c> test rejects on a tie forever, the identical mechanism
    /// round 89 already proved for phase 7, one phase earlier in the same dispatch chain.
    ///
    /// Since <see cref="Odyssey.TryExtendFinalNodeTrail"/>'s own fix never writes the phase field
    /// itself — it only manufactures a reachable <c>DestNode</c>/write-cursor state and lets
    /// whichever handler is CURRENTLY dispatched perform its own accept — the exact same fix
    /// mechanism applies unmodified to phase 6 as to phase 7: pointing <c>DestNode</c> at a newly
    /// appended node makes phase 6's search take its own unconditional-accept branch (0x512AA0)
    /// exactly the way phase 7's does (0x512B80), just landing on <c>phase=1</c> instead of
    /// <c>phase=3</c> — a real, disassembly-confirmed forward transition in the same 12-entry
    /// dispatch chain, not a guess. This check is DELIBERATELY separate from
    /// <see cref="IsTerminalStallShape"/> (phase 7 only) rather than widening that one in place:
    /// <see cref="Odyssey.TryAcceptFinalPathNode"/>/<see cref="Odyssey.TryRefreshFinalNodeTarget"/>
    /// share that method and were live-tested ONLY against phase 7 — silently letting them also
    /// fire on phase 6 would extend their own already-recorded (disproven/insufficient) live
    /// results to a shape neither was ever tested against.
    /// </summary>
    public static bool IsExtendableTerminalStallShape(int phase, int pathNode, int destNode, int gap) =>
        (phase == 6 || phase == 7) && pathNode == destNode && gap == 1;
}

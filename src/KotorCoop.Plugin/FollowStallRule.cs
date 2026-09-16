namespace KotorCoop.Plugin;

/// <summary>
/// ROUND 105 (docs/research/party-reconciliation.md) — the RUNTIME companion-follow stall
/// detector, and the first one in this investigation that watches the record while the game is
/// running rather than at a single lifecycle edge.
///
/// **Why a runtime detector at all.** Every prior fix candidate in this file's family fires from
/// a precondition that the live-reproduced stall never satisfies:
/// <list type="bullet">
/// <item><see cref="Phase2StallRule"/> needs <c>phase == 2</c> AND the leader more than 3 units
/// away; round 96/97's five-for-five repro is <c>phase == 7</c> with the leader ~1.1 units
/// away.</item>
/// <item><see cref="FollowPhaseReleaseRule"/> (<c>party_follow_phase7_release</c>) evaluates
/// <c>phase != -1</c> exactly once, at <c>PartyPresenceController.ReleaseSession</c> — and rounds
/// 102/103 measured that by that moment the phase has legitimately returned to <c>-1</c> on its
/// own, so its corrective branch has never had anything to correct in any rig rep.</item>
/// <item><see cref="Odyssey.TryAdvanceStalledPathDestination"/> (default ON) refuses at
/// <c>gap &lt; 2</c>, and the live stall is <c>pathNode=19 destNode=19 writeCursor=20</c> —
/// <c>gap == 1</c> exactly.</item>
/// <item><see cref="PartyFollowController"/>'s own generic "reissue ActionFollowLeader" stall
/// check refuses below <c>FollowKickRangeSquared</c> (3 units squared); the stall sits at ~1.24
/// squared.</item>
/// </list>
///
/// **The shape this rule detects, from round 97's own 30-second bit-identical capture**:
/// <code>
/// phase=7 attempts=0 pathNode=19 destNode=19 writeCursor=20 moveState=3
/// pos=(10.51,16.78) leaderPos=(10.78,15.70) target=(10.78,15.70)
/// targetVsSnapshotDist=1.105 pathWaitDestAccept=False distSqToTarget=0.0000 distSqToPrior=0.0000
/// </code>
/// <c>PathWaitDest</c> (0x00512B40, phase 7) accepts only when the recorded trail node is
/// STRICTLY closer to <c>TargetPoint</c> (<c>record+0x28</c>) than to <c>PriorPoint</c>
/// (<c>record+0x18</c>) — the <c>fcompp</c>/<c>test ah,5</c>/<c>jp</c> idiom at 0x00512BF2 that
/// <see cref="PartyFollowController"/>'s own fine-log already reproduces in managed arithmetic as
/// <c>pathWaitDestAccept</c>. <c>WaitingForPath</c> (0x005127D0) re-collapses <c>DestNode</c> onto
/// <c>CurrentPathNode</c> and <c>Prior</c> onto <c>Target</c> on every commit, so both squared
/// distances read exactly <c>0</c> and a STRICT <c>&lt;</c> can never be true. That is a permanent
/// wedge, not a slow convergence: the same values held bit-identically for 30+ continuous seconds,
/// with <c>attempts</c> (<c>record+0x44</c>) pinned at 0 the whole time because the outer
/// dispatcher's own per-call gate never reopens.
///
/// **What this rule deliberately does NOT gate on.** Not <c>attempts &gt; 0</c> (it is structurally
/// pinned at 0 for this shape — the same reasoning <see cref="Phase2StallRule"/> already documents
/// for its own sibling), and not the 3-unit <c>FollowKickRangeSquared</c> (that threshold exists to
/// avoid interrupting a companion that is merely "caught up", and this stall happens well inside
/// it). Instead it requires the degenerate accept condition ITSELF to be observable
/// (<see cref="CannotAccept"/>) — a signature a healthily-following companion never presents for
/// seconds on end, because a healthy one either accepts and advances or is genuinely moving.
///
/// **Anti-spin.** <see cref="MaxConsecutiveRecoveries"/> consecutive fires without an intervening
/// confirmed recovery (<see cref="RecoveryConfirmed"/> — the companion genuinely moved after the
/// poke) latch the detector off for the rest of the module, and
/// <see cref="RecoveryCooldownSeconds"/> separates any two fires. This project has already paid
/// for the missing version of this discipline once: docs/research/party-reconciliation.md's own
/// per-frame-write lesson (a torn-down panel written through 500x/s). Every counter here is reset
/// on a module change by the caller, alongside its existing epoch-skip bookkeeping.
/// </summary>
internal static class FollowStallRule
{
    /// <summary>Dispatch phase for <c>PathWaitDest</c> (0x00512B40) — the phase the live stall
    /// parks in, five reproductions out of five (rounds 96, 97).</summary>
    public const int PathWaitDestPhase = 7;

    /// <summary>Dispatch phase for the handler at 0x00512A60 —
    /// <see cref="FinalNodeGeometryRule.IsExtendableTerminalStallShape"/>'s own remarks establish
    /// it is byte-for-byte structurally identical to <c>PathWaitDest</c> in every respect this
    /// rule depends on (same forward search, same strict <c>fcompp</c> accept test), differing
    /// only in which phase its unconditional-accept tail commits. A live A/B has already caught
    /// the identical wedge here, one phase earlier, so this rule covers both.</summary>
    public const int PathWaitPredecessorPhase = 6;

    /// <summary>How long the companion's position must have held still, in a stall phase, before
    /// this rule will act. Deliberately the same 2.0s bar
    /// <see cref="PartyFollowController"/>'s own <c>StallThresholdSeconds</c> uses for its
    /// sibling "reissue ActionFollowLeader" check — long enough that this is not a normal
    /// between-steps pause, short enough that a co-op partner does not stand watching a frozen
    /// companion.</summary>
    public const double StallThresholdSeconds = 2.0;

    /// <summary>Squared planar distance from the leader below which a motionless companion is
    /// simply standing in formation and must never be poked. 1.0 = one game unit. Round 97's own
    /// captured stall sits at <c>(10.51,16.78)</c> vs. <c>(10.78,15.70)</c> — 1.2393 squared —
    /// so the recorded shape clears this with real margin while an actually-caught-up companion
    /// does not.</summary>
    /// <para>2026-09-17: was 1.0 (one unit). A companion standing in FORMATION beside a still
    /// leader is 1.6-1.9 units away (live: leaderDistSq 2.74 and 3.60), so the old gate let the
    /// detector fire five PositionParty teleports at a companion that had simply arrived, then
    /// latch off for the module - and each PositionParty left the host's party in solo mode
    /// (see Odyssey.GatherParty), which is the party AI script's "do not fight" switch. The
    /// host's Trask stood through a whole fight because of it. Three units is the same
    /// "caught up" range the sibling reissue check (PartyFollowController.FollowKickRangeSquared)
    /// already uses. A leader who is WALKING AWAY still fires at any distance, which is the case
    /// rounds 96/97 built this for.</para>
    public const float MinLeaderDistanceSquared = 3.0f * 3.0f;

    /// <summary>How many consecutive fires without a confirmed recovery are allowed before this
    /// companion's detector latches off for the rest of the module.</summary>
    public const int MaxConsecutiveRecoveries = 5;

    /// <summary>Minimum wall-clock separation between two fires for the same companion — also the
    /// window <see cref="RecoveryConfirmed"/> is given to observe real movement.</summary>
    public const double RecoveryCooldownSeconds = 3.0;

    /// <summary>How far the companion must move after a fire for it to count as genuinely
    /// recovered (squared; 0.5 units). Comfortably above
    /// <c>PartyFollowController.MovedEpsilon</c>'s own 0.05 float-noise floor, so a jitter of a
    /// couple of centimetres never reads as a recovery.</summary>
    public const float RecoveryConfirmedDistanceSquared = 0.5f * 0.5f;

    /// <summary>The recovery methods <c>party_follow_stall_recovery_method</c> selects between —
    /// see <see cref="Odyssey.TryRecoverFollowStall"/> for what each one actually writes and
    /// docs/research/party-reconciliation.md round 105 for the live A/B that picked
    /// <see cref="DefaultMethod"/> out of them.</summary>
    public const int MethodPhaseReset = 1;

    /// <inheritdoc cref="MethodPhaseReset"/>
    public const int MethodFollowLeader = 2;

    /// <inheritdoc cref="MethodPhaseReset"/>
    public const int MethodPositionParty = 3;

    /// <inheritdoc cref="MethodPhaseReset"/>
    public const int MethodBreakPriorTie = 4;

    /// <summary>
    /// The recovery round 105's own live A/B settled on — see
    /// <see cref="Odyssey.TryRecoverFollowStall"/> for what it writes. It is the ONLY one of the
    /// five that never touches the phase field, never moves the body, and never manufactures a
    /// geometric verdict: it repoints <c>DestNode</c> so the wedged handler's own forward search
    /// reaches its OTHER, UNCONDITIONAL accept branch, and lets the unmodified engine handler
    /// decide and commit from there.
    /// </summary>
    public const int MethodAdvanceDestNode = 5;

    /// <summary>
    /// <inheritdoc cref="MethodPhaseReset"/>
    /// </summary>
    public const int MethodPushPriorPastNode = 6;

    /// <summary>Whether <paramref name="method"/> names a recovery this codebase implements —
    /// a typo in coop.ini must degrade to "do nothing", never to "poke an engine field with a
    /// value nobody chose".</summary>
    public static bool IsKnownMethod(int method) =>
        method is MethodPhaseReset
            or MethodFollowLeader
            or MethodPositionParty
            or MethodBreakPriorTie
            or MethodAdvanceDestNode
            or MethodPushPriorPastNode;

    /// <summary>
    /// How far past the recorded trail node <see cref="MethodPushPriorPastNode"/> places
    /// <c>PriorPoint</c>, as a multiple of the node-to-target distance it is mirroring. Any value
    /// strictly greater than 1 makes <c>|node - Prior|</c> strictly greater than
    /// <c>|node - Target|</c>, which is exactly what <c>PathWaitDest</c>'s <c>fcompp</c> needs to
    /// fall through to its accept; 2 is the smallest round number that leaves real headroom for
    /// float rounding at the sub-centimetre distances this wedge actually presents
    /// (<c>distSqToTarget</c> live-measured as low as 0.0207).
    /// </summary>
    public const float PriorPushMultiple = 2.0f;

    /// <summary>Below this squared length a direction vector is float noise, not a direction —
    /// used by <see cref="MethodPushPriorPastNode"/> to decide whether the node-to-target vector
    /// is usable or whether it must fall back to the node-to-companion one.</summary>
    public const float DirectionEpsilonSquared = 1e-6f;

    /// <summary>
    /// The recovery <c>party_follow_stall_recovery</c> applies unless coop.ini says otherwise.
    ///
    /// Round 105's live A/B ran all six against the same reproduced wedge on the rig, one at a
    /// time through the <c>followstall</c> dev verb, and this is the only one that actually ended
    /// with the companion back with its leader. The decisive case was an instant 16.5-unit
    /// separation (the leader teleported away — a "transition/separation" in the plainest
    /// possible form) with the companion frozen at <c>phase=7 attempts=0 pathNode=destNode=19
    /// writeCursor=20 distSqToPrior=0</c>:
    /// <list type="bullet">
    /// <item><b>1 phasereset</b> — the FSM restarted and re-wedged on the identical geometry
    /// within 8s, zero position change.</item>
    /// <item><b>2 followleader</b> — the VM dispatch succeeded and changed literally nothing.</item>
    /// <item><b>3 positionparty</b> — 16.5 units behind to 0.88 units, in one call. Chosen.</item>
    /// <item><b>4 priortie</b> — real walking (3.44 units from a 6.6-unit separation) but it
    /// converges onto the exact <c>|node - me| == |node - Target|</c> tie the STRICT compare
    /// rejects, and re-wedges there.</item>
    /// <item><b>5 destnode</b> — advanced the trail index and moved the companion 0.76 units, in
    /// the wrong direction, into a different frozen state.</item>
    /// <item><b>6 priorpush</b> — 1.6 units of real progress, then a NEW frozen shape
    /// (<c>phase=3</c>, <c>pathNode != destNode</c>) this detector does not even watch.</item>
    /// </list>
    ///
    /// The honest reading: 4, 5 and 6 all prove the geometric diagnosis is right — poking exactly
    /// the fields <c>PathWaitDest</c>'s own test reads DOES restart genuine walking — but the
    /// engine's own long-distance pathfinding then stalls again downstream, on the separate
    /// limit round 88 already documented and this round reconfirmed. Chasing that would be a
    /// different investigation; <c>PositionParty</c> (0x00565B00) sidesteps it entirely by being
    /// the engine's OWN answer to "the party is scattered, gather it" — the exact call
    /// <c>MovePlayerToArea</c> makes at every module transition, walkmesh snap, formation and
    /// follow-data reset included. It relocates rather than walks the companion, which is why it
    /// is gated behind a 2-second wedge, a real separation, a 3-second cooldown and a
    /// per-module budget rather than run freely.
    /// </summary>
    public const int DefaultMethod = MethodPositionParty;

    /// <summary>The two dispatch phases whose handlers share the strict-inequality accept test
    /// this rule's whole detection rests on.</summary>
    public static bool IsStallPhase(int phase) =>
        phase == PathWaitDestPhase || phase == PathWaitPredecessorPhase;

    /// <summary>
    /// The engine's own accept test, in the negative: <c>PathWaitDest</c> falls through to its
    /// phase-advancing write ONLY when <paramref name="distSqToTarget"/> is STRICTLY less than
    /// <paramref name="distSqToPrior"/> (0x00512BF2's <c>fcompp</c>/<c>test ah,5</c>/<c>jp</c>
    /// — PF is set, and the reject branch taken, for both "greater or equal" and "unordered").
    /// True here means the handler is structurally incapable of accepting on this data, however
    /// long it is left alone — including the exact <c>0 vs. 0</c> tie round 97 captured.
    /// </summary>
    public static bool CannotAccept(float distSqToTarget, float distSqToPrior) =>
        !(distSqToTarget < distSqToPrior);

    /// <summary>
    /// Whether the companion's follow record is wedged in the shape this rule targets, ignoring
    /// timing and anti-spin entirely — the geometry/index half of the decision, split out so a
    /// test (and a live log line) can assert the shape independently of how long it has held.
    /// </summary>
    /// <param name="phase">The dispatch phase at <c>record+4</c>.</param>
    /// <param name="attempts">The stall counter at <c>record+0x44</c>. Pinned at 0 for this shape
    /// (the outer dispatcher's gate never reopens), so a non-zero value means the FSM is genuinely
    /// cycling and must be left alone.</param>
    /// <param name="pathNode">The current path node at <c>record+0x24</c>.</param>
    /// <param name="destNode">The forward-search target at <c>record+0x34</c>. Equal to
    /// <paramref name="pathNode"/> is what makes the search resolve on its first comparison and
    /// fall into the fragile geometric test at all.</param>
    /// <param name="distSqToTarget">|RecordedDestPoint - TargetPoint|².</param>
    /// <param name="distSqToPrior">|RecordedDestPoint - PriorPoint|².</param>
    public static bool IsWedgedShape(
        int phase, int attempts, int pathNode, int destNode, float distSqToTarget, float distSqToPrior) =>
        IsStallPhase(phase)
        && attempts == 0
        && pathNode == destNode
        && CannotAccept(distSqToTarget, distSqToPrior);

    /// <summary>
    /// The whole runtime decision: is this companion wedged, has it been wedged long enough, is
    /// there any point moving it, and is the anti-spin budget still open.
    /// </summary>
    /// <param name="phase">See <see cref="IsWedgedShape"/>.</param>
    /// <param name="attempts">See <see cref="IsWedgedShape"/>.</param>
    /// <param name="pathNode">See <see cref="IsWedgedShape"/>.</param>
    /// <param name="destNode">See <see cref="IsWedgedShape"/>.</param>
    /// <param name="distSqToTarget">See <see cref="IsWedgedShape"/>.</param>
    /// <param name="distSqToPrior">See <see cref="IsWedgedShape"/>.</param>
    /// <param name="frozenSeconds">How long the companion's own world position has held still,
    /// as the caller already tracks for its sibling stall check.</param>
    /// <param name="leaderDistanceSquared">Planar squared distance to the leader.</param>
    /// <param name="leaderMoving">Whether the leader itself moved this frame — a wedged companion
    /// with a MOVING leader is falling behind right now and is worth recovering at any distance,
    /// which is precisely the "walked away and it never resumed" complaint.</param>
    /// <param name="suppressed">The caller's own "something else owns this companion" verdict
    /// (conversation, roster freeze, movie, mirrored session, module-change frame). Kept as one
    /// boolean so this rule stays free of engine calls.</param>
    /// <param name="consecutiveRecoveries">Fires so far, this module, without a confirmed
    /// recovery in between.</param>
    /// <param name="secondsSinceLastRecovery">Wall-clock since the last fire for this companion;
    /// pass <see cref="double.PositiveInfinity"/> when there has not been one.</param>
    /// <param name="stallThresholdSeconds">Normally <see cref="StallThresholdSeconds"/>.</param>
    /// <param name="minLeaderDistanceSquared">Normally <see cref="MinLeaderDistanceSquared"/>.</param>
    /// <param name="maxConsecutiveRecoveries">Normally <see cref="MaxConsecutiveRecoveries"/>.</param>
    /// <param name="recoveryCooldownSeconds">Normally <see cref="RecoveryCooldownSeconds"/>.</param>
    public static bool ShouldRecover(
        int phase,
        int attempts,
        int pathNode,
        int destNode,
        float distSqToTarget,
        float distSqToPrior,
        double frozenSeconds,
        float leaderDistanceSquared,
        bool leaderMoving,
        bool suppressed,
        int consecutiveRecoveries,
        double secondsSinceLastRecovery,
        double stallThresholdSeconds,
        float minLeaderDistanceSquared,
        int maxConsecutiveRecoveries,
        double recoveryCooldownSeconds)
    {
        if (suppressed)
        {
            return false;
        }

        if (!IsWedgedShape(phase, attempts, pathNode, destNode, distSqToTarget, distSqToPrior))
        {
            return false;
        }

        if (frozenSeconds < stallThresholdSeconds)
        {
            return false;
        }

        // Either the companion is genuinely behind, or the leader is actively walking away from
        // it right now. A motionless companion standing in formation beside a motionless leader
        // is not a stall worth poking, whatever the record says.
        if (!leaderMoving && leaderDistanceSquared <= minLeaderDistanceSquared)
        {
            return false;
        }

        if (consecutiveRecoveries >= maxConsecutiveRecoveries)
        {
            return false;
        }

        return secondsSinceLastRecovery >= recoveryCooldownSeconds;
    }

    /// <summary>
    /// Whether the last fire actually worked: the companion has moved
    /// <paramref name="movedSinceRecoverySquared"/> away from where it stood when the recovery
    /// was written. Resets the consecutive counter, so an oscillating stall that keeps making
    /// real progress is never latched off — only one that keeps failing is.
    /// </summary>
    public static bool RecoveryConfirmed(float movedSinceRecoverySquared, float confirmedDistanceSquared) =>
        movedSinceRecoverySquared >= confirmedDistanceSquared;

    /// <summary>
    /// Whether the detector should be latched off for this companion for the rest of the module —
    /// the anti-spin backstop. Reported separately from <see cref="ShouldRecover"/> so the caller
    /// can log the give-up exactly once instead of silently going quiet.
    /// </summary>
    public static bool ShouldLatchOff(int consecutiveRecoveries, int maxConsecutiveRecoveries) =>
        consecutiveRecoveries >= maxConsecutiveRecoveries;
}

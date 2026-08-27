namespace KotorCoop.Plugin;

/// <summary>
/// Pure decision logic behind <see cref="Odyssey.TryKickStalledMovingToPathToTeleportForward"/>
/// (round 95, <c>docs/research/party-reconciliation.md</c>) — the phase=2
/// (<c>CSWSCreature::WalkUpdateLocation_QuickWalk_FollowLeader_Moving_To_Path</c>, 0x00511860 —
/// see that method's own remarks for the corrected DB name; four prior live rounds' prose called
/// this phase "Following_Path" and phase 3 "Moving_To_Path," backwards from
/// <c>tools/kotor1_0_3.db</c>'s own function names, though every shipped write already used the
/// correct hex addresses) idle stall four independent live A/B rounds (90, 91, 94, and the
/// 2026-08-28 re-run) found downstream of every previously-shipped fix, itself never
/// root-caused. Pulled into its own pure, unit-tested class — mirroring
/// <see cref="FinalNodeGeometryRule"/>/<see cref="PartyFollowTransitionRule"/>/
/// <see cref="PartyFreezeRule"/>'s own established pattern — so the stall/distance thresholds
/// that decide whether to fire the engine's own <c>TeleportForward</c> recovery phase can be
/// pinned by a test independent of the unsafe pointer arithmetic that reads them live.
///
/// Deliberately time-gated, not attempts-gated. The already-shipped, default-ON
/// <see cref="Odyssey.TryAdvanceStalledMovingToPath"/> requires <c>attempts &gt; 0</c> before it
/// will act, specifically so a companion's genuinely first pass through phase 2 is left to the
/// engine's own normal resolution. Live data across all four A/B rounds shows the OPPOSITE
/// failure shape for the stall this rule targets: <c>attempts</c> (<c>record+0x44</c>) reads
/// exactly <c>0</c> for the ENTIRE stall — 13+ continuous minutes in the 2026-08-24 round, 45+
/// seconds in the 2026-08-28 re-run — because the outer dispatcher's own per-call gate
/// (<c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360, 0x0051C7D0-0x0051C7DF: a
/// computed <c>StepAccumulator * rate * time-scale</c> product compared against the binary's own
/// float-zero constant at 0x73D700) is never open long enough to re-enter the phase-dispatch jump
/// table at all, so <c>attempts</c> literally cannot climb off zero no matter how long the
/// companion waits — the existing fix's own "first visit" guard therefore never expires. A
/// wall-clock/distance gate, entirely independent of <c>attempts</c>, is the only way to detect
/// and act on this specific shape.
/// </summary>
internal static class Phase2StallRule
{
    /// <summary>Dispatch phase value for <c>Moving_To_Path</c> (0x00511860) — the phase this rule
    /// watches for a stall.</summary>
    public const int MovingToPathPhase = 2;

    /// <summary>Dispatch phase value for <c>TeleportForward</c> (0x00512DE0) — the phase this
    /// rule's fix commits. VERIFIED by fresh disassembly this round: the handler walks
    /// <c>CurrentPathNode</c> forward through the recorded 100-slot trail
    /// (<c>CSWPartyFollowDataArray::GetPathData</c>, 0x00634CC0, named directly from
    /// <c>tools/kotor1_0_3.db</c>), safety-testing each candidate point (0x004CB120
    /// <c>CSWSObject::GetArea</c> + 0x004BE5E0 <c>CSWSArea::TestSafeLocationPoint</c> — a second,
    /// distinct check from the <c>CSWSArea::TestDirectLine</c> phase 2's own handler calls) until
    /// it finds one that fails or reaches the write cursor, then commits that node as the new
    /// <c>CurrentPathNode</c>, writes fresh <c>TargetPoint</c>/<c>Snapshot</c> from it, and —
    /// critically — re-enters phase 2 itself (0x00512EF3: <c>mov dword ptr [ebp+4], 2</c>), not
    /// phase 3 or phase 0. Unlike <c>Odyssey.TryAcceptFinalPathNode</c> (which forced phase 3 onto
    /// <c>PathWaitDest</c>'s own handler without first re-establishing the geometry that handler's
    /// preconditions assume, and was live-disproven — party-reconciliation.md round 88 Finding 4),
    /// this hands control to a handler whose entire job is establishing fresh, obstacle-checked
    /// preconditions before resuming — the same category of fix as the already-shipped watchdog
    /// reset (<c>TryResetStuckFollowPhase</c>, phase -1/5) rather than a forced-output guess.</summary>
    public const int TeleportForwardPhase = 10;

    /// <summary>
    /// Whether a phase=2 stall has gone on long enough, with the leader far enough away, that this
    /// project's own bar for "worth interrupting" (matching <see cref="PartyFollowController"/>'s
    /// own established <c>StallThresholdSeconds</c>/<c>FollowKickRangeSquared</c> shape for its
    /// sibling "reissue ActionFollowLeader" stall check) is met.
    /// </summary>
    /// <param name="phase">The companion's current dispatch phase (<c>record+4</c>).</param>
    /// <param name="phase2StallSeconds">How many consecutive seconds <paramref name="phase"/> has
    /// continuously read <see cref="MovingToPathPhase"/>, as tracked by the caller (reset to 0 the
    /// moment the phase reads anything else).</param>
    /// <param name="leaderDistanceSquared">Planar (X/Y) squared distance between the companion and
    /// its leader.</param>
    /// <param name="stallThresholdSeconds">How long the phase must have held before this rule will
    /// consider acting — must be positive.</param>
    /// <param name="kickRangeSquared">Below this squared distance, the companion is already "close
    /// enough" and this rule never fires, matching every other stall check in this controller.</param>
    public static bool ShouldKick(
        int phase,
        double phase2StallSeconds,
        float leaderDistanceSquared,
        double stallThresholdSeconds,
        float kickRangeSquared)
    {
        if (phase != MovingToPathPhase)
        {
            return false;
        }

        if (phase2StallSeconds < stallThresholdSeconds)
        {
            return false;
        }

        return leaderDistanceSquared > kickRangeSquared;
    }
}

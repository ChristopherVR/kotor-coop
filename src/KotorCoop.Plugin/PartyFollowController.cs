using System.Diagnostics;

namespace KotorCoop.Plugin;

/// <summary>
/// Detects a genuinely-owned party companion that has stopped following its leader, and
/// reissues the engine's own <c>ActionFollowLeader</c> through the same safe VM-dispatch route
/// <see cref="Odyssey.AttackTarget"/>/<see cref="Odyssey.PickUpItem"/> already use.
///
/// ROUND 71 (2026-08-23), shared-state-audit.md round 68's "Trask's follow-AI never engaged"
/// finding, and docs/research/party-reconciliation.md's own "Recommended design" (item 4,
/// 2026-07-31, never shipped): "when an owned companion's action queue is empty, issue
/// ActionFollowLeader."
///
/// **Honest status, live-measured (round 71).** The dispatch mechanism itself is built and
/// verified correct and safe: <see cref="Odyssey.QueuePartyFollowLeader"/> reliably transitions
/// the companion's <c>CreMoveState</c> (<c>+0xA8C</c>; 1 idle, 2 requested, 3 follow, -1
/// walking) to 3 ("follow" — the mode is genuinely entered, confirmed in-process every time it
/// is called), and an earlier version of this controller's own "clear the queue first" step was
/// found and removed as a real, self-inflicted bug (see the call site's own remarks — it was
/// repeatedly invalidating the exact pathing target-cache fields a creature does not recover on
/// its own). What round 71 could NOT get live: <c>CreMoveState</c> reaching -1 ("walking," a
/// route genuinely being stepped) for Trask specifically. Ruled out as causes, each with its own
/// live test: an empty vs. stuck queue (his own head type is a stable, undocumented <c>0x3D</c>,
/// not the verified <c>0x3C PartyFollowLeader</c> id — see <see cref="HeadActionPartyFollowLeader"/>);
/// this controller's own harmful clear (removed, no change); perception range (a live control —
/// ordering a plain enemy to attack from outside its ~11m <c>PerceptionRange</c> also produced
/// no movement, but the SAME order from within range closed real distance correctly, so queued
/// AI movement is not universally broken in this rig); and the party table's own leader slot
/// (<c>CSWPartyTable+0xEC</c> read -1 for the whole session; writing 0 as a live experiment,
/// then reverting it, changed nothing). The mechanism this controller builds is real and shipped
/// because it is a correct, verified, no-regression improvement over the complete absence of any
/// follow-re-arming that existed before this round — but "Trask genuinely walks behind the host"
/// is NOT yet closed. Flagged for a future round with the above ruled out.
///
/// Applies ONLY to companions THIS instance's own engine genuinely owns. A client mirroring a
/// peer's party already puppeteers those bodies directly, every pass
/// (<see cref="PartyPresenceController"/>, gated on <c>block.PartyPresenceCount != 0</c> — the
/// field only <c>KotorGameBridge.TryWritePartyMembers</c> ever writes, and only on a client) —
/// handing one of those a real follow order at the same time would contest the puppeteering and
/// reopen the exact drift solo mode exists to prevent, so this controller backs off whenever
/// that channel is active. On the host, and in solo play with no session at all, this is exactly
/// what a real recruitment conversation's own ending script would have done.
///
/// Kill switch: <c>party_follow=0</c> in coop.ini.
/// </summary>
internal sealed unsafe class PartyFollowController(SharedBlockView block)
{
    private const int PartyNpcSlots = 9;

    // The APP_MANAGER -> server -> internal walk, same chain PartyPresenceController and the
    // agent's EngineGlobals use (no ASLR; the plugin runs inside the image these are absolute
    // in).
    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;
    private const int InternalOffset = 0x04;

    /// <summary>CServerExoAppInternal + this = the embedded CSWPartyTable.</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = int[9] spawned object id per roster slot.</summary>
    private const int PartyObjectIds = 0x0C;

    /// <summary>CSWPartyTable + this = solo mode (party-reconciliation.md's own finding: "the
    /// game's shipped 'companions stand still' switch"). ROUND 72 DIAGNOSTIC: read alongside the
    /// follow-data record to test whether solo mode being held on for the session is also
    /// suppressing the engine's own per-frame recompute of each companion's follow TARGET point
    /// (a hypothesis raised by <c>target</c>/<c>prior</c> staying frozen at the leader's
    /// load-time position in every sample taken so far).</summary>
    private const int PartyTableSoloMode = 0x190;

    /// <summary>The verified RunActions id for a genuinely-running follow order
    /// (<see cref="Odyssey"/>'s own <c>IsMoveAction</c> table comment).</summary>
    private const int HeadActionPartyFollowLeader = 0x3C;

    /// <summary>Head-of-queue ids that mean "leave it alone, it is doing something real" —
    /// combat, casting, waiting on a round — the same set <see cref="Odyssey"/>'s own
    /// <c>QueueAttackAction</c> remarks name as "already fighting."</summary>
    private static bool IsBusyHead(int headType) =>
        headType is 0x0B or 0x3E or 0x0E or 0x31 or 0x0F;

    /// <summary>How long a companion may sit motionless, away from its leader, before this
    /// controller concludes whatever is at the head of its queue is not really moving it and
    /// forces a fresh follow order.</summary>
    private const double StallThresholdSeconds = 2.0;

    /// <summary>Below this planar distance from the leader, "not moving" is just "caught up" —
    /// never worth interrupting.</summary>
    private const float FollowKickRangeSquared = 3.0f * 3.0f;

    /// <summary>How far a companion must move between passes to count as "not stalled."</summary>
    private const float MovedEpsilon = 0.05f;

    private readonly bool _enabled = PluginConfig.ReadBool("party_follow", true);

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Vec3[] _lastPos = new Vec3[PartyNpcSlots];
    private readonly double[] _stationarySince = new double[PartyNpcSlots];
    private readonly bool[] _tracked = new bool[PartyNpcSlots];

    private int _issuedLogged;

    /// <summary>ROUND 80 (party-reconciliation.md): a fine-grained, UNTHROTTLED poll of the same
    /// FollowDiag fields, sampled far more often than the once-per-reissue log below (which only
    /// fires roughly every <see cref="StallThresholdSeconds"/>) — built to answer round 74/75's
    /// still-open question, "why does the phase-dispatch block stop being re-entered for a parked
    /// companion," by watching `attempts` (record+0x44) at ~10Hz to see whether it ever ticks even
    /// once between reissue samples, and whether WalkRate/RunRate/StepAccumulator ever go
    /// negative/zero/stuck while parked. This round's own live rig work is what this was built
    /// for and is now done, so it defaults OFF for a normal play session (log volume, not
    /// correctness — every field here is also read-only) — opt in with
    /// <c>party_follow_finelog=1</c>; capped at <see cref="FineLogCap"/> lines total either way so
    /// a long-parked companion cannot grow the log unbounded.</summary>
    private readonly bool _fineLogEnabled = PluginConfig.ReadBool("party_follow_finelog", false);

    /// <summary>ROUND 80 EXPERIMENT (party-reconciliation.md): see
    /// Odyssey.TryPokeStepAccumulator's own remarks — creature+0xD0 (the step accumulator
    /// WalkUpdateLocation_QuickWalk_FollowLeader's own first gate requires to be nonzero) is
    /// live-measured pinned at 0 for a parked companion, and no producer that increments it
    /// (rather than periodically zeroing it) was found anywhere in the binary. Kill switch:
    /// <c>party_follow_pokestep=0</c>.</summary>
    private readonly bool _pokeStepAccumulator = PluginConfig.ReadBool("party_follow_pokestep", true);

    /// <summary>ROUND 83 (party-reconciliation.md): see
    /// Odyssey.TryAdvanceStalledPathDestination's own remarks — WaitingForPath's own per-frame
    /// re-commit resets DestNode back to CurrentPathNode every call, permanently defeating
    /// PathWaitDest's forward search; this pokes DestNode ahead to the already-recorded write
    /// cursor so the search can escape the degenerate self-comparison. Kill switch:
    /// <c>party_follow_advance_destnode=0</c>.</summary>
    private readonly bool _advanceStalledDestNode = PluginConfig.ReadBool("party_follow_advance_destnode", true);

    /// <summary>LIVE FINDING (2026-08-24, fix-candidate-1 A/B round): see
    /// Odyssey.TryAdvanceStalledPathDestinationPhase6's own remarks — the phase-6 analog of
    /// TryAdvanceStalledPathDestination, discovered live when this round's own candidate-1 repro
    /// found Trask permanently parked at phase 6 (not 7) with gap==2, a shape neither candidate 1
    /// (gap==1 only) nor the phase-7-only TryAdvanceStalledPathDestination reaches. NOT YET
    /// LIVE-TESTED IN ISOLATION. Kill switch (to enable): <c>party_follow_advance_destnode_phase6=1</c>
    /// (default OFF).</summary>
    private readonly bool _advanceStalledDestNodePhase6 =
        PluginConfig.ReadBool("party_follow_advance_destnode_phase6", false);

    /// <summary>ROUND 88 (party-reconciliation.md): see
    /// Odyssey.TryAdvanceStalledMovingToPath's own remarks — phase 2's own handler
    /// (0x00511860) never commits its "close enough" acceptance (phase -> 3) unless
    /// CSWSArea::TestDirectLine also returns exactly 1, and live data shows that gate never
    /// resolving for a companion parked with a genuinely zero-length PriorPoint/Snapshot delta,
    /// stalling permanently. Kill switch: <c>party_follow_advance_movingtopath=0</c>.</summary>
    private readonly bool _advanceStalledMovingToPath =
        PluginConfig.ReadBool("party_follow_advance_movingtopath", true);

    /// <summary>ROUND 88 (party-reconciliation.md): see
    /// Odyssey.TryForceWalkingWhenMovingToPathStalled's own remarks — round 84's own originally
    /// -described mechanism (CreMoveState reverts from -1 to 3 mid-leg), traced this round to
    /// FollowMoveGateCheckFn/0x00511130, a SEPARATE per-frame dispatch this controller does not
    /// drive and cannot suppress at the source; this re-asserts CreMoveState=-1 whenever
    /// Moving_To_Path is confirmed still running (its own accumulator advancing) with real
    /// unconsumed trail remaining. Kill switch: <c>party_follow_force_walking=0</c>.</summary>
    private readonly bool _forceWalkingWhenStalled =
        PluginConfig.ReadBool("party_follow_force_walking", true);

    /// <summary>Per-slot previous-frame read of <c>record+0x6C</c> — see
    /// Odyssey.TryForceWalkingWhenMovingToPathStalled's own remarks for why this needs to be
    /// sampled every frame, not just on the throttled fine-log pass.</summary>
    private readonly int[] _lastMovingToPathAccumulator = new int[PartyNpcSlots];

    /// <summary>ROUND 88 (party-reconciliation.md): see Odyssey.TryAcceptFinalPathNode's own
    /// remarks — LIVE-TESTED AND DISPROVEN: forcing phase 3 on this exact terminal shape does
    /// not unstick real motion; instead it opens a NEW unproductive cycle with
    /// TryForceWalkingWhenMovingToPathStalled (phase 7 -&gt; 3 -&gt; 0/FindPath -&gt; 7 -&gt;
    /// ... every ~0.3-0.5s, moveState settling at 1/idle rather than 3/follow, position still
    /// frozen) — confirmed live, 30+ seconds, zero position change. Default OFF; the function is
    /// kept, named, and documented as a ruled-out avenue rather than removed, matching this
    /// project's own established convention (see round 80's TryPokeStepAccumulator). Kill switch
    /// (to enable, for whoever wants to re-test it against a fix for the underlying oscillation):
    /// <c>party_follow_accept_final_node=1</c>.</summary>
    private readonly bool _acceptFinalPathNode = PluginConfig.ReadBool("party_follow_accept_final_node", false);
    private readonly int[] _finalNodeStallFrames = new int[PartyNpcSlots];
    private readonly int[] _lastFinalNodeSeen = new int[PartyNpcSlots];

    /// <summary>ROUND 90 (party-reconciliation.md): see
    /// Odyssey.TryRefreshFinalNodeTarget's own remarks — builds on round 89's own falsifiable
    /// hypothesis that PathWaitDest's geometric accept/reject test at the very last recorded
    /// trail node is correctly rejecting a STALE Target/Prior pair, not spuriously blocking a
    /// healthy one. Refreshes only the geometry the real engine test reads and lets that
    /// unmodified test decide, rather than forcing its output the way
    /// Odyssey.TryAcceptFinalPathNode does. NOT live-tested this round — no rig access. Kill
    /// switch (to enable): <c>party_follow_refresh_final_node=1</c> (default off).</summary>
    private readonly bool _refreshFinalNodeTarget =
        PluginConfig.ReadBool("party_follow_refresh_final_node", false);

    /// <summary>FIX CANDIDATE 1 (2026-08-24 follow-up round, party-reconciliation.md): see
    /// Odyssey.TryExtendFinalNodeTrail's own remarks — appends one synthetic trail node past the
    /// recorded end once the terminal stall shape is observed, turning it into the same
    /// already-working <c>gap&gt;=2</c> shape Odyssey.TryAdvanceStalledPathDestination already
    /// handles, rather than forcing the accept test's output or merely refreshing its inputs.
    /// NOT YET LIVE-TESTED as of shipping this switch — see the doc entry this round adds for the
    /// actual A/B result. Kill switch (to enable): <c>party_follow_extend_final_node=1</c> (default
    /// OFF pending live A/B).</summary>
    private readonly bool _extendFinalNodeTrail =
        PluginConfig.ReadBool("party_follow_extend_final_node", false);

    /// <summary>ROUND 95 (2026-08-28 static pass, party-reconciliation.md): see
    /// Odyssey.TryKickStalledMovingToPathToTeleportForward's and Phase2StallRule's own remarks —
    /// the phase=2 idle stall four independent live A/B rounds (90, 91, 94, the 2026-08-28
    /// re-run) found downstream of every previously-shipped fix, itself never root-caused. Forces
    /// the engine's own TeleportForward recovery phase once phase=2 has held continuously for
    /// <see cref="Phase2KickStallSeconds"/> while the leader is genuinely far away — a
    /// wall-clock/distance gate, not an <c>attempts</c>-gate, since live data shows <c>attempts</c>
    /// never leaves 0 for this exact shape. NOT YET LIVE-TESTED — no rig access this round. Kill
    /// switch (to enable): <c>party_follow_phase2_kick=1</c> (default OFF).</summary>
    private readonly bool _phase2Kick = PluginConfig.ReadBool("party_follow_phase2_kick", false);

    /// <summary>How long <c>phase</c> must continuously read <see cref="Phase2StallRule.MovingToPathPhase"/>
    /// before <see cref="_phase2Kick"/> is allowed to fire — long enough that this is clearly not
    /// a companion's genuine, still-legitimate first pass through the phase (round 88's own
    /// "engine's own normal path run once" reasoning for its attempts-gated sibling), short
    /// enough not to leave a co-op partner watching a frozen companion for very long. Deliberately
    /// longer than <see cref="StallThresholdSeconds"/> (the general "reissue ActionFollowLeader"
    /// stall check) — this pokes an actual engine phase field, a stronger intervention that
    /// deserves a higher bar than reissuing an action the queue may already hold.</summary>
    private const double Phase2KickStallSeconds = 5.0;

    private readonly bool[] _inPhase2Run = new bool[PartyNpcSlots];
    private readonly double[] _phase2RunStart = new double[PartyNpcSlots];

    /// <summary>ROUND 105 (party-reconciliation.md): the RUNTIME follow-stall detector — see
    /// <see cref="FollowStallRule"/>'s own remarks for the disassembly-grounded wedge signature
    /// it watches for, and <see cref="Odyssey.TryRecoverFollowStall"/> for the four candidate
    /// recoveries it selects between. Unlike every earlier candidate in this controller this one
    /// is ON by default: rounds 96/97 reproduced its exact target shape five times out of five,
    /// and rounds 100-104 established that the release-time fix
    /// (<c>party_follow_phase7_release</c>) can never reach it because the phase has legitimately
    /// returned to -1 by the time <c>ReleaseSession</c> runs. Kill switch:
    /// <c>party_follow_stall_recovery=0</c>.</summary>
    private readonly bool _stallRecovery = PluginConfig.ReadBool("party_follow_stall_recovery", true);

    /// <summary>Which of <see cref="FollowStallRule"/>'s recoveries
    /// <see cref="Odyssey.TryRecoverFollowStall"/> applies — <c>party_follow_stall_recovery_method</c>.
    /// Exists so a live A/B can isolate one write at a time in a single game session (this
    /// project's standing rule for engine writes) rather than needing a rebuild per candidate.
    /// See docs/CONFIG.md for the value table.</summary>
    private readonly int _stallRecoveryMethod =
        PluginConfig.ReadInt("party_follow_stall_recovery_method", FollowStallRule.DefaultMethod);

    /// <summary>Consecutive recoveries fired for this slot, this module, with no confirmed
    /// recovery in between — <see cref="FollowStallRule"/>'s own anti-spin budget.</summary>
    private readonly int[] _stallFires = new int[PartyNpcSlots];

    /// <summary>Wall-clock (on <see cref="_clock"/>) of this slot's last recovery fire, or -1
    /// for "never fired" — which the cooldown check reads as an infinite gap, so the very first
    /// genuine wedge is never made to wait out a cooldown that has not happened.</summary>
    private readonly double[] _stallLastFireTime = NeverFired();

    private static double[] NeverFired()
    {
        double[] times = new double[PartyNpcSlots];
        Array.Fill(times, -1.0);
        return times;
    }

    /// <summary>Where the companion stood at its last recovery fire — the baseline
    /// <see cref="FollowStallRule.RecoveryConfirmed"/> measures real movement against.</summary>
    private readonly Vec3[] _stallFirePos = new Vec3[PartyNpcSlots];

    /// <summary>Whether this slot's "budget exhausted, giving up for this module" line has already
    /// been logged — logged exactly once, not once per frame.</summary>
    private readonly bool[] _stallLatchLogged = new bool[PartyNpcSlots];

    /// <summary>Whether this slot has already had <c>party+0x00</c> restored once this module —
    /// see <see cref="Odyssey.TryGetFollowStallState"/>'s own <c>allowCountRestore</c> parameter.
    /// This is the per-object latch docs/research/party-reconciliation.md's own per-frame-write
    /// rule demands: the restore is an ENGINE FIELD WRITE, and this detector runs every frame, so
    /// it must be able to happen at most once rather than fighting a re-clamp 500 times a
    /// second.</summary>
    private readonly bool[] _stallCountRestored = new bool[PartyNpcSlots];

    /// <summary>Last frame's leader position, and whether it is meaningful yet — a wedged
    /// companion whose leader is actively walking away is worth recovering at any distance (see
    /// <see cref="FollowStallRule.ShouldRecover"/>'s <c>leaderMoving</c> parameter).</summary>
    private Vec3 _lastLeaderPos;
    private bool _leaderTracked;

    /// <summary>How many consecutive frames the exact terminal shape must persist before
    /// Odyssey.TryAcceptFinalPathNode is allowed to fire — enough to rule out a single,
    /// possibly-still-legitimate visit, short enough not to add a perceptible extra pause.</summary>
    private const int FinalNodeStallFrameThreshold = 10;

    private const int FineLogCap = 20000;

    /// <summary>Tightened from 0.2s to 0.04s (round 81, party-reconciliation.md) — round 80's
    /// own open question was whether Trask's catch-up walk is smooth per-frame motion or a
    /// coarse multi-node snap, and 0.2s sampling was too coarse to tell the two apart (every
    /// prior round's samples showed her jump straight from stalled to "already there"). 25Hz
    /// is fast enough to catch intermediate path nodes during a single walk leg without
    /// materially growing the log (still capped by <see cref="FineLogCap"/>).</summary>
    private const double FineLogIntervalSeconds = 0.04;
    private int _fineLogged;
    private readonly double[] _lastFineLogTime = new double[PartyNpcSlots];

    /// <summary>Wall-clock timestamp (on <see cref="_clock"/>) of the last
    /// <see cref="Odyssey.PumpPartyUpdate"/> call, or -1 before the first frame.</summary>
    private double _lastUpdateTime = -1;

    // Fractional milliseconds carried between rendered callbacks. At uncapped rates a single
    // callback may be below one millisecond; rounding each callback would over- or under-drive
    // the native follow continuation.
    private double _followStepFractionalMilliseconds;

    /// <summary>ROUND 87 (party-reconciliation.md): see <see cref="PartyFollowTransitionRule"/>'s
    /// own remarks — the last epoch THIS controller has actually driven follow logic against.
    /// Starts at 0, which is not a valid epoch (<c>Odyssey.TryGetWorldEpoch</c> hashes a
    /// non-empty object-array pointer with an FNV seed that never lands on exactly 0 in
    /// practice), so the very first live frame is deliberately treated the same as a module
    /// change: skipped once, harmlessly, before driving begins.</summary>
    private ulong _lastSeenEpoch;

    private void ResetFrameTimingBaselines()
    {
        _lastUpdateTime = -1;
        _followStepFractionalMilliseconds = 0;
        _leaderTracked = false;
        Array.Clear(_tracked);
        Array.Clear(_inPhase2Run);
        Array.Clear(_phase2RunStart);
        Array.Clear(_finalNodeStallFrames);
        Array.Clear(_lastFinalNodeSeen);
        Array.Clear(_lastMovingToPathAccumulator);
    }

    public void OnFrame()
    {
        if (!_enabled || block.Command != SharedBlock.CommandSpawnAndDrive)
        {
            ResetFrameTimingBaselines();
            return;
        }

        // A client mirroring a peer's party drives every companion body itself, every pass —
        // see the class comment. Nothing to do here while that channel is active.
        if (block.PartyPresenceCount != 0)
        {
            ResetFrameTimingBaselines();
            return;
        }

        // Same two guards PartyPresenceController applies before touching a companion: a local
        // conversation is positioning the party itself, and a roster freeze means a script owns
        // one specific companion directly (Leviathan brig, Rakatan temple, any forced-solo
        // beat) — issuing a follow order over either would fight the thing actually in control.
        if (Odyssey.IsConversationActive || Odyssey.IsPartyRosterFrozen())
        {
            ResetFrameTimingBaselines();
            return;
        }

        // Native follow continuation is a simulation step, not a render-only operation. Do not
        // advance it while either world pause mask is held or while the client owns a cinematic;
        // otherwise a manual step can move a companion during a pause even though the engine's
        // ordinary party update is stopped. Reset the measured-time carry so the first resumed
        // frame cannot emit a catch-up step for the paused interval.
        bool moviePlaying = Odyssey.IsMoviePlaying();
        if ((Odyssey.GetPauseMask() | Odyssey.GetClientPauseMask()) != 0 || moviePlaying)
        {
            ResetFrameTimingBaselines();
            return;
        }

        // ROUND 87 (party-reconciliation.md): closes round 85's own "not fixed this round" warp
        // crash (AV at 0x4AA984, inside the engine's own Moving_To_Path follow continuation —
        // see PartyFollowTransitionRule's own remarks for the full disassembly-grounded finding).
        // This must run BEFORE the PumpPartyUpdate/RecalculateFollowPoint calls below, not just
        // before the per-companion loop — both are unconditional per-frame engine calls into the
        // same CSWParty machinery, and round 85's own evidence does not narrow the crash to the
        // per-companion loop specifically, only to "the first frame this controller's engine
        // calls run against a newly-valid epoch."
        bool worldEpochReadable = Odyssey.TryGetWorldEpoch(out ulong epoch);
        if (PartyFollowTransitionRule.ShouldSkipThisFrame(worldEpochReadable, epoch, _lastSeenEpoch))
        {
            if (worldEpochReadable)
            {
                _lastSeenEpoch = epoch;
            }

            // Every per-slot tracked position/stall timer belongs to whatever was true before
            // this module change (or is simply unstarted) — never compare it against the first
            // real reading the settled frame after this one takes.
            Array.Clear(_tracked);

            // ROUND 105: the stall detector's anti-spin budget is explicitly per-module (see
            // FollowStallRule's own remarks) — a fresh module is a fresh trail buffer, a fresh
            // set of follow records, and a fresh right to intervene. Cleared here, alongside the
            // tracking this branch already resets, so there is exactly one place that decides
            // what "a new module" invalidates.
            Array.Clear(_stallFires);
            Array.Clear(_stallLatchLogged);
            Array.Clear(_stallCountRestored);
            Array.Fill(_stallLastFireTime, -1);
            ResetFrameTimingBaselines();
            return;
        }

        // ROUND 73: CSWParty::Update — the engine's own per-frame driver of the party-level
        // reference position CSWParty::RecaulateFollowPoint reads (see Odyssey.PumpPartyUpdate's
        // own remarks for the full disassembly finding). Its one real caller lives inside a
        // per-frame CClientExoAppInternal dispatch this plugin's drive loop never reaches, so
        // nothing was refreshing that reference position — round 72's frozen follow-target point
        // was a symptom of THIS being missing, not of RecaulateFollowPoint itself being wrong.
        // Called unconditionally, once per rendered frame (this method's own FrameHook cadence),
        // with a real measured delta time — the same cadence and argument shape the engine's own
        // caller uses.
        double nowForUpdate = _clock.Elapsed.TotalSeconds;
        double elapsedSeconds = _lastUpdateTime < 0 ? 0.0 : nowForUpdate - _lastUpdateTime;
        float dt = (float)elapsedSeconds;
        _lastUpdateTime = nowForUpdate;
        int followStepMilliseconds = FollowStepTimingRule.Consume(
            ref _followStepFractionalMilliseconds, elapsedSeconds);
        if (dt <= 0f || dt >= 1f) // skip the first frame and any huge stall/load gap
        {
            // The native party consumer is not the only unsafe work below: the per-member
            // index lookup, follow-target writes, and stall recovery all walk the same raw
            // CSWParty array.  A first frame or a load gap therefore skips the whole pass,
            // rather than merely skipping CSWParty::Update and continuing with stale timing.
            ResetFrameTimingBaselines();
            // Keep this frame as the new timing origin.  ResetFrameTimingBaselines clears the
            // previous origin by design; leaving it at -1 here would make every following
            // frame look like the first frame and permanently suppress the pump.
            _lastUpdateTime = nowForUpdate;
            return;
        }

        if (!Odyssey.PumpPartyUpdate(dt))
        {
            // PumpPartyUpdate validates the client-side resolution of every exposed member
            // before entering the native function.  Its false result is a whole-frame
            // readiness barrier: RecalculateFollowPoint and every per-member native helper
            // below use the same array and must not run until it resolves consistently.
            ResetFrameTimingBaselines();
            return;
        }

        // ROUND 73 CONTINUED: live-measured that PumpPartyUpdate alone refreshes the
        // PARTY-LEVEL reference position (CSWParty+0x2B0..0x2B8) correctly — it now tracks
        // a teleported leader within one frame — but does NOT, by itself, unstick a
        // long-stalled member's PER-MEMBER target/prior fields
        // (record+0x28/record+0x18, WalkUpdateLocation_QuickWalk_FollowLeader's own
        // input). CSWParty::Update's own body only forwards the refreshed reference
        // position into RecaulateFollowPoint conditionally, gated behind
        // CSWParty::TestAndResetPath() returning zero (see PartyUpdateFn's remarks) — and
        // live data shows that gate staying shut for a companion that has been stalled
        // this long. Round 72 already built and verified-safe an UNCONDITIONAL direct
        // call to the same RecaulateFollowPoint (bypassing that gate entirely, 0 stack
        // args) but measured zero effect BEFORE this round's fix existed, because its own
        // input (the reference position) was itself still frozen at that time. Retried now
        // that PumpPartyUpdate keeps the reference position genuinely live.
        Odyssey.RecalculateFollowPoint();

        nint appManager = *(nint*)AppManagerPtr;
        if (appManager == 0)
        {
            ResetFrameTimingBaselines();
            return;
        }

        nint server = *(nint*)(appManager + AppServerOffset);
        if (server == 0)
        {
            ResetFrameTimingBaselines();
            return;
        }

        nint serverInternal = *(nint*)(server + InternalOffset);
        if (serverInternal == 0)
        {
            ResetFrameTimingBaselines();
            return;
        }

        nint partyTable = serverInternal + PartyTableOffset;

        // Never touch whichever body the player is actually driving right now — Tab onto a
        // companion and this would otherwise try to make it "follow" itself.
        nint controlled = Odyssey.GetControlledCreaturePointer();
        nint player = Odyssey.GetPlayerCreaturePointer();
        Vec3 playerPos = player == 0 ? default : Odyssey.GetPosition(player);
        double now = _clock.Elapsed.TotalSeconds;

        // ROUND 105: is the leader itself walking right now? A companion wedged in the follow
        // FSM while its leader keeps moving is falling further behind every frame — that is the
        // literal "walked away and it never resumed following" complaint, and it is worth
        // recovering at any separation, not only past the 3-unit gate the sibling reissue check
        // uses. Measured against the same MovedEpsilon float-noise floor as the per-companion
        // motion test below, so "moving" means the same thing for both.
        bool leaderMoving = false;
        if (player != 0)
        {
            if (_leaderTracked)
            {
                float ldx = playerPos.X - _lastLeaderPos.X;
                float ldy = playerPos.Y - _lastLeaderPos.Y;
                leaderMoving = ((ldx * ldx) + (ldy * ldy)) > MovedEpsilon * MovedEpsilon;
            }

            // Keep the last significant sample as the baseline. At high callback rates a normal
            // walk moves less than MovedEpsilon per callback; replacing this baseline every time
            // made a genuinely moving leader look stationary to the stall gate.
            if (!_leaderTracked || leaderMoving)
            {
                _lastLeaderPos = playerPos;
            }
            _leaderTracked = true;
        }
        else
        {
            _leaderTracked = false;
        }

        // "Something else is genuinely in charge of this companion right now." The conversation /
        // roster-freeze / mirrored-session / module-change-frame cases are all already hard
        // early-returns above, so the only one left to fold in here is a playing movie — the one
        // engine-owned state that can hold the world still without any of those firing.
        bool stallSuppressed = moviePlaying;

        for (int slot = 0; slot < PartyNpcSlots; slot++)
        {
            uint objectId = *(uint*)(partyTable + PartyObjectIds + (slot * 4));
            if (objectId is 0 or Odyssey.ObjectInvalid)
            {
                _tracked[slot] = false;
                continue;
            }

            nint creature = Odyssey.ResolveServerObject(objectId);
            if (creature == 0 || creature == controlled)
            {
                _tracked[slot] = false;
                continue;
            }

            int headType = Odyssey.GetHeadActionType(creature);
            Vec3 pos = Odyssey.GetPosition(creature);

            // Update the movement baseline before any recovery decision. At the previous
            // location this ran after DriveStallRecovery, so a companion that moved on this
            // frame could still be recovered using the old stationary timer. Keep the flag
            // until the original stall gate below, while allowing the follow writes to run.
            bool movementBaselineFresh = false;
            if (!_tracked[slot])
            {
                _tracked[slot] = true;
                _lastPos[slot] = pos;
                _stationarySince[slot] = now;
                movementBaselineFresh = true;
            }
            else
            {
                float dx = pos.X - _lastPos[slot].X;
                float dy = pos.Y - _lastPos[slot].Y;
                if ((dx * dx) + (dy * dy) > MovedEpsilon * MovedEpsilon)
                {
                    _lastPos[slot] = pos;
                    _stationarySince[slot] = now;
                    movementBaselineFresh = true;
                }
            }

            // ROUND 73 FALLBACK: keep this companion's own follow-data record driven straight
            // off the real leader position every frame — see Odyssey.TryDriveFollowTarget's own
            // remarks for why the engine's own CSWParty::Update/RecaulateFollowPoint calls above
            // do not do this on their own for a companion in this state. Skipped while genuinely
            // busy (combat, casting, a round in progress) so this never fights something real.
            Odyssey.FollowDiag preStep = default;
            bool logThisSlot = _fineLogEnabled && _fineLogged < FineLogCap && player != 0
                && !IsBusyHead(headType) && now - _lastFineLogTime[slot] >= FineLogIntervalSeconds;

            if (player != 0 && !IsBusyHead(headType))
            {
                Odyssey.TryDriveFollowTarget(objectId, playerPos);

                // ROUND 83 DIAGNOSTIC: sample target/prior IMMEDIATELY after our own write,
                // before TryDriveFollowLeaderStep's engine call runs — see the finelog line's own
                // remarks below for what this is testing.
                if (logThisSlot)
                {
                    Odyssey.TryGetFollowDiagnostics(creature, objectId, out preStep);
                }

                // ROUND 74 FIX, narrowed in round 84 to phase == -1 only (see Odyssey.
                // TryResetStuckFollowPhase's own remarks for the disasm this is based on):
                // unwedge the step FSM's phase field when it is caught in the -1/5 ping-pong
                // that never calls FindPath. Shipped and live-verified; the phase-6/7 geometric
                // wedge it cannot see is DriveStallRecovery's job (round 105).
                Odyssey.TryResetStuckFollowPhase(objectId, out _);

                // ROUND 80 EXPERIMENT: see Odyssey.TryDriveFollowLeaderStep's own remarks — a
                // bare TryPokeStepAccumulator poke was live-tested and found insufficient (the
                // engine's own outer caller re-zeroes the field before ever consuming a poke left
                // for it); this drives the real FollowLeader step function directly instead. The
                // measured, fractional-carry value is in the same millisecond unit as the
                // cached-time-pair fields this record exposes.
                if (_pokeStepAccumulator && followStepMilliseconds > 0)
                {
                    // The native field is a millisecond step budget. A fixed 16 ms overdrives
                    // an uncapped render loop; the measured value above is fractional-carry
                    // accumulated and bounded across callbacks.
                    Odyssey.TryDriveFollowLeaderStep(objectId, followStepMilliseconds);
                }

                // ROUND 83: break the phase=7 permanent stall at its real, disassembly-grounded
                // root — see Odyssey.TryAdvanceStalledPathDestination's own remarks. Called AFTER
                // TryDriveFollowLeaderStep so it acts on this frame's post-WaitingForPath-commit
                // state, ahead of WaitingForPath getting another chance to re-collapse it next
                // frame.
                if (_advanceStalledDestNode)
                {
                    Odyssey.TryAdvanceStalledPathDestination(objectId, out _);
                }

                // LIVE FINDING (2026-08-24, fix-candidate-1 A/B round): the phase-6 analog of the
                // call directly above — see Odyssey.TryAdvanceStalledPathDestinationPhase6's own
                // remarks.
                if (_advanceStalledDestNodePhase6)
                {
                    Odyssey.TryAdvanceStalledPathDestinationPhase6(objectId, _fineLogEnabled, out _);
                }

                // ROUND 88: a third, independent permanent-stall shape — see Odyssey.
                // TryAdvanceStalledMovingToPath's own remarks. Called last, after both of the
                // above, so it only ever acts on whatever phase this frame's earlier calls
                // could not themselves resolve.
                if (_advanceStalledMovingToPath)
                {
                    Odyssey.TryAdvanceStalledMovingToPath(objectId, out _);
                }

                // ROUND 88: round 84's own originally-described "CreMoveState reverts from -1 to
                // 3" mechanism — see Odyssey.TryForceWalkingWhenMovingToPathStalled's own
                // remarks. Uses last frame's own accumulator sample (below) to tell "the handler
                // ran this frame" apart from "nothing touched it" before deciding to poke.
                if (_forceWalkingWhenStalled)
                {
                    Odyssey.TryForceWalkingWhenMovingToPathStalled(
                        objectId, _lastMovingToPathAccumulator[slot], out _);
                }

                if (Odyssey.TryGetMovingToPathAccumulator(objectId, out int currentAccumulator))
                {
                    _lastMovingToPathAccumulator[slot] = currentAccumulator;
                }

                // ROUND 90: see Odyssey.TryRefreshFinalNodeTarget's own remarks — a geometry-only
                // alternative to TryAcceptFinalPathNode below, tried first (when enabled) since it
                // never forces the engine's own accept/reject test, only its inputs. Called every
                // frame while the terminal shape holds; the function's own ~1cm epsilon prevents
                // repeat writes once genuinely fresh, so no stall-frame gating is needed here the
                // way TryAcceptFinalPathNode's forced poke requires.
                if (_refreshFinalNodeTarget)
                {
                    Odyssey.TryRefreshFinalNodeTarget(objectId, playerPos, out _);
                }

                // FIX CANDIDATE 1 (2026-08-24 follow-up round): see
                // Odyssey.TryExtendFinalNodeTrail's own remarks — appends a synthetic trail node
                // past the recorded end once the terminal shape is observed, letting
                // PathWaitDest's own forward search reach the SAME unconditional accept branch
                // Odyssey.TryAdvanceStalledPathDestination already uses for gap>=2, rather than
                // requiring its fragile geometric test to pass. Self-limiting via shape (see that
                // function's own remarks) — no consecutive-frame gate needed here either.
                if (_extendFinalNodeTrail)
                {
                    Odyssey.TryExtendFinalNodeTrail(objectId, playerPos, _fineLogEnabled, out _);
                }

                // ROUND 88: see Odyssey.TryAcceptFinalPathNode's own remarks — track how many
                // consecutive frames the exact terminal shape (phase 7, parked on the same
                // pathNode) has persisted before allowing that poke to fire.
                if (_acceptFinalPathNode
                    && Odyssey.TryGetPhaseAndPathNode(objectId, out int phaseNow, out int pathNodeNow))
                {
                    if (phaseNow == 7 && pathNodeNow == _lastFinalNodeSeen[slot])
                    {
                        _finalNodeStallFrames[slot]++;
                    }
                    else
                    {
                        _finalNodeStallFrames[slot] = 0;
                    }

                    _lastFinalNodeSeen[slot] = pathNodeNow;

                    if (_finalNodeStallFrames[slot] >= FinalNodeStallFrameThreshold)
                    {
                        Odyssey.TryAcceptFinalPathNode(objectId, out _);
                        _finalNodeStallFrames[slot] = 0;
                    }
                }

                // ROUND 95: the phase=2 idle stall — see Odyssey.
                // TryKickStalledMovingToPathToTeleportForward's and Phase2StallRule's own remarks.
                // Tracks how many CONSECUTIVE seconds phase has continuously read
                // Phase2StallRule.MovingToPathPhase, independent of _acceptFinalPathNode's own
                // (phase-7-only) frame counter above.
                if (_phase2Kick
                    && Odyssey.TryGetPhaseAndPathNode(objectId, out int phase2Now, out _))
                {
                    if (phase2Now == Phase2StallRule.MovingToPathPhase)
                    {
                        if (!_inPhase2Run[slot])
                        {
                            _inPhase2Run[slot] = true;
                            _phase2RunStart[slot] = now;
                        }

                        float p2dx = pos.X - playerPos.X;
                        float p2dy = pos.Y - playerPos.Y;
                        float leaderDistSq = (p2dx * p2dx) + (p2dy * p2dy);
                        double phase2StallSeconds = now - _phase2RunStart[slot];

                        if (Phase2StallRule.ShouldKick(
                            phase2Now, phase2StallSeconds, leaderDistSq,
                            Phase2KickStallSeconds, FollowKickRangeSquared))
                        {
                            Odyssey.TryKickStalledMovingToPathToTeleportForward(
                                objectId, _fineLogEnabled, out _);

                            // Restart the run: if TeleportForward's own re-entry into phase 2
                            // stalls again, it needs to accumulate its own fresh duration before
                            // this fires a second time, rather than firing every frame thereafter.
                            _phase2RunStart[slot] = now;
                        }
                    }
                    else
                    {
                        _inPhase2Run[slot] = false;
                    }
                }

                // ROUND 105: the runtime stall detector — see FollowStallRule's own remarks for
                // why none of the calls above can reach this wedge, and
                // Odyssey.TryRecoverFollowStall's for what each recovery writes. Runs LAST, after
                // every already-shipped per-frame fix has had its chance this frame, so it only
                // ever acts on a record none of them could resolve.
                if (_stallRecovery)
                {
                    DriveStallRecovery(slot, objectId, pos, playerPos, now, leaderMoving, stallSuppressed);
                }
            }

            // ROUND 80: fine-grained unthrottled poll — see _fineLogEnabled's own remarks.
            if (logThisSlot)
            {
                _lastFineLogTime[slot] = now;
                _fineLogged++;

                Odyssey.TryGetFollowDiagnostics(creature, objectId, out Odyssey.FollowDiag fd);
                if (fd.Valid)
                {
                    float dtx = fd.TargetPoint.X - fd.Snapshot.X;
                    float dty = fd.TargetPoint.Y - fd.Snapshot.Y;
                    float targetVsSnapshot = MathF.Sqrt((dtx * dtx) + (dty * dty));

                    // ROUND (2026-08-24): the in-process substitute for "a live debugger on the
                    // fcompp at 0x512BF2" that round 89/90's own verdict named as the next step.
                    // Full FPU-stack trace of PathWaitDest's accept/reject test (0x512B86-0x512C03,
                    // tools/disasm.py 0x512B40 220), instruction by instruction:
                    //   distSqToTarget = |RecordedDestPoint - TargetPoint|^2   (record+0x28)
                    //   distSqToPrior  = |RecordedDestPoint - PriorPoint|^2   (record+0x18)
                    //   fcompp compares ST0=distSqToTarget against ST1=distSqToPrior, pops both;
                    //   fnstsw ax; test ah,5; jp <reject-return> — the standard unordered-compare
                    //   idiom. TEST AH,5 reads C0 (ST0<ST1) and C2 (unordered) into PF; PF=1 (jp
                    //   taken, REJECT) for ST0>=ST1 or unordered; PF=0 (fall through to the
                    //   phase=3 accept write at 0x512C09) only for ST0<ST1 STRICTLY. I.e. the real
                    //   engine test accepts iff the recorded node is strictly CLOSER to Target than
                    //   to Prior. This is a pure computation on fields TryGetFollowDiagnostics
                    //   already reads every frame (no new engine call, no write) — it directly
                    //   answers "why does the accept branch never fire" without attaching any
                    //   debugger to the game's own x86 code.
                    float dtgx = fd.RecordedDestPoint.X - fd.TargetPoint.X;
                    float dtgy = fd.RecordedDestPoint.Y - fd.TargetPoint.Y;
                    float dtgz = fd.RecordedDestPoint.Z - fd.TargetPoint.Z;
                    float distSqToTarget = (dtgx * dtgx) + (dtgy * dtgy) + (dtgz * dtgz);

                    float dprx = fd.RecordedDestPoint.X - fd.PriorPoint.X;
                    float dpry = fd.RecordedDestPoint.Y - fd.PriorPoint.Y;
                    float dprz = fd.RecordedDestPoint.Z - fd.PriorPoint.Z;
                    float distSqToPrior = (dprx * dprx) + (dpry * dpry) + (dprz * dprz);

                    bool pathWaitDestWouldAccept = distSqToTarget < distSqToPrior;

                    // ROUND 83: preStep is target/prior read IMMEDIATELY after our own
                    // TryDriveFollowTarget write, before TryDriveFollowLeaderStep's engine call
                    // runs this same frame — comparing it to fd (read after that engine call)
                    // tells apart "our write never took" from "our write took, then something
                    // inside the engine's own per-frame continuation clobbered it before we could
                    // observe it standing."
                    PluginHost.Log(
                        $"party follow finelog: 0x{objectId:X} t={now:F2} phase={fd.PhaseIndex} "
                        + $"attempts={fd.PhaseAttempts} stepAccum={fd.StepAccumulator} "
                        + $"walkRate={fd.WalkRate:F4} runRate={fd.RunRate:F4} "
                        + $"pathNode={fd.CurrentPathNode} destNode={fd.DestNode} writeCursor={fd.WriteCursor} "
                        + $"moveState={fd.CreMoveState} m2pAccum={fd.MovingToPathAccumulator} "
                        + $"headType=0x{headType:X} pos=({pos.X:F2},{pos.Y:F2}) "
                        + $"leaderPos=({playerPos.X:F2},{playerPos.Y:F2}) "
                        + $"target=({fd.TargetPoint.X:F2},{fd.TargetPoint.Y:F2}) "
                        + $"prior=({fd.PriorPoint.X:F2},{fd.PriorPoint.Y:F2}) "
                        + $"preStepTarget=({preStep.TargetPoint.X:F2},{preStep.TargetPoint.Y:F2}) "
                        + $"preStepPrior=({preStep.PriorPoint.X:F2},{preStep.PriorPoint.Y:F2}) "
                        + $"preStepPhase={preStep.PhaseIndex} "
                        + $"snapshot=({fd.Snapshot.X:F2},{fd.Snapshot.Y:F2}) "
                        + $"targetVsSnapshotDist={targetVsSnapshot:F3} enabledFlag={fd.EnabledFlag} "
                        // ROUND 88: live test of FollowMoveGateCheckFn/0x00511130 — the only
                        // other CreMoveState=3 writer in the binary. See Odyssey.
                        // FollowMoveGateCheckFn's own remarks for what each field means.
                        + $"gateAreaId=0x{fd.GateAreaId:X} gateFires={fd.GateFires} "
                        + $"partySlot0Id=0x{fd.PartySlot0Id:X} selfIsPartySlot0={fd.SelfMatchesPartySlot0} "
                        + $"moveBudget={fd.MoveBudget:F6} "
                        // ROUND 89: the recorded trail node at CurrentPathNode's own index — see
                        // Odyssey.TryAcceptFinalPathNode's own remarks for why this is here (testing
                        // whether PathWaitDest's geometric accept/reject test is correctly rejecting
                        // a stale Target/Prior pair, not spuriously blocking a healthy one).
                        + $"recordedDestPoint=({fd.RecordedDestPoint.X:F2},{fd.RecordedDestPoint.Y:F2}) "
                        // The computed PathWaitDest verdict — see the derivation comment above.
                        + $"pathWaitDestAccept={pathWaitDestWouldAccept} "
                        + $"distSqToTarget={distSqToTarget:F4} distSqToPrior={distSqToPrior:F4}");
                }
            }

            if (movementBaselineFresh)
            {
                continue; // first sighting or genuinely moving — nothing to recover
            }

            if (headType == HeadActionPartyFollowLeader || IsBusyHead(headType))
            {
                // Actually following (just between path steps) or genuinely doing something
                // else real — never interrupt either.
                continue;
            }

            bool stalled = now - _stationarySince[slot] > StallThresholdSeconds;
            if (headType != -1 && !stalled)
            {
                // Something is queued and it has not had time to prove itself stuck yet.
                continue;
            }

            float pdx = pos.X - playerPos.X;
            float pdy = pos.Y - playerPos.Y;
            if (player != 0 && ((pdx * pdx) + (pdy * pdy)) <= FollowKickRangeSquared)
            {
                // Close enough to its leader that standing still is just "caught up," whatever
                // the queue says.
                continue;
            }

            // ROUND 71: deliberately NOT clearing the queue first. ClearQueuedCombatActions
            // wraps ClearAllActionsFn, which CaptureClaimState's own remarks document as
            // unconditionally invalidating a creature's pathing target-cache fields
            // (CreLastAttackTarget, pathInfo's own last-enemy id) every time its full body
            // runs — fields "neither... is something a creature normally recovers from on its
            // own." Live-measured: calling it here on every stalled retry (originally every
            // ~2s) kept moveState (CreMoveState, +0xA8C) latched at 3 ("follow" — the mode is
            // entered) but NEVER -1 ("walking" — a route is actually being stepped), for over a
            // minute straight, on both a fresh companion and a plain enemy given the same
            // "attack from 23m away" order. Dropping the clear is the fix: ActionFollowLeader's
            // own dispatch (like every other action this codebase queues through the VM) is
            // safe to issue against a non-empty queue without help — that is the entire point
            // of RunActions being a priority queue, not a single slot.
            // ROUND 72: two follow-on experiments tried here and LIVE-DISPROVEN, not shipped —
            // Odyssey.RecalculateFollowPoint() alone, and Odyssey.TryPokeFollowPriorPosition()
            // (writing the per-member record's own cached leader-position field) immediately
            // before it. Both are real, verified-safe calls (0 stack args; a plain field write
            // to a field this same record already exposes for reading) and neither is a guess at
            // an unverified ABI. Live-measured, several cycles: the WRITE sticks (record+0x18
            // reads back the poked position, confirmed via the diagnostic log below, and nothing
            // else claws it back) but the follow TARGET point (record+0x28) the walk-continuation
            // actually reads never moves off its module-load value regardless — ruling out both
            // "the recompute just needs calling" and "the recompute reads record+0x18." See
            // Odyssey.RecalculateFollowPoint's and TryPokeFollowPriorPosition's own remarks for
            // the full finding and the still-unidentified prime suspect
            // (CSWParty::Update 0x00637050's own un-determined stack argument). Left uncalled
            // here rather than shipped as machinery with a measured zero benefit.
            bool ok = Odyssey.QueuePartyFollowLeader(objectId, out int failure);
            _stationarySince[slot] = now; // do not re-trigger every pass while the order lands

            if (_issuedLogged < 40)
            {
                _issuedLogged++;
                int headAfter = Odyssey.GetHeadActionType(creature);
                int commandable = *(int*)(creature + 0xE8);
                int inParty = *(int*)(creature + 0xA88);
                int followFlag = *(int*)(creature + 0xA8C);

                // ROUND 75 DIAGNOSTIC (2026-08-23, party-reconciliation.md): the creature-side
                // CSWParty per-member-record backlink EnemyPresenceController's own
                // CrePartyFollowInfo already names ("non-zero means the party system queues
                // follow actions on it") — party-reconciliation.md's own "Party follow... gates
                // on +0xA88, +0x4C0, +0x9F0 & 2" citation, never read back here before. A
                // force-recruited second companion (docs/research/party-reconciliation.md round
                // 75's dev-only `recruit` test primitive) is the first case that can tell zero
                // from nonzero here against a companion NOT populated by the normal save-load/
                // recruit-conversation path.
                int partyFollowInfo = *(int*)(creature + 0x4C0);

                // ROUND 72: does this companion even have a resolvable CSWParty index, and — if
                // so — what does the engine's own per-frame follow continuation
                // (WalkUpdateLocation_QuickWalk_FollowLeader 0x0051C360) see in its own
                // follow-data record? See Odyssey.TryGetFollowDiagnostics's own remarks.
                bool indexOk = Odyssey.TryGetClientPartyIndex(objectId, out int partyIndex);
                Odyssey.TryGetFollowDiagnostics(creature, objectId, out Odyssey.FollowDiag diag);
                Vec3 leaderPos = playerPos;
                int soloMode = *(int*)(partyTable + PartyTableSoloMode);

                // ROUND 73: the party-level fields CSWParty::Update itself reads/writes — see
                // Odyssey.TryGetPartyReferenceState's own remarks. refAccum climbing frame over
                // frame is direct proof PumpPartyUpdate is genuinely being called; refPos moving
                // toward leaderFieldPos (NOT necessarily the same as leaderPos above — that is a
                // DIFFERENT lookup, see the field's own remarks) is proof the function's own
                // leader-refresh block is doing real work.
                Odyssey.TryGetPartyReferenceState(out Odyssey.PartyRefState refState);
                string refText = refState.Valid
                    ? $" refId=0x{refState.LeaderRecordId:X} refPos=({refState.ReferencePos.X:F1},{refState.ReferencePos.Y:F1},{refState.ReferencePos.Z:F1}) "
                        + $"refAccum={refState.TimeAccumulator:F2} leaderCreature=0x{refState.LeaderCreaturePtr:X} "
                        + $"leaderFieldPos=({refState.LeaderFieldPos.X:F1},{refState.LeaderFieldPos.Y:F1},{refState.LeaderFieldPos.Z:F1})"
                    : " refState=unreadable";

                string diagText = diag.Valid
                    ? $" target=({diag.TargetPoint.X:F1},{diag.TargetPoint.Y:F1},{diag.TargetPoint.Z:F1}) "
                        + $"prior=({diag.PriorPoint.X:F1},{diag.PriorPoint.Y:F1},{diag.PriorPoint.Z:F1}) "
                        + $"snapshot=({diag.Snapshot.X:F1},{diag.Snapshot.Y:F1},{diag.Snapshot.Z:F1}) "
                        + $"snapPending={diag.SnapshotPendingFlag} stepAccum={diag.StepAccumulator} "
                        + $"cachedPos=({diag.CachedPosPairLo:X},{diag.CachedPosPairHi:X}) "
                        + $"enabledFlag={diag.EnabledFlag} leaderPos=({leaderPos.X:F1},{leaderPos.Y:F1},{leaderPos.Z:F1}) "
                        + $"soloMode={soloMode} phase={diag.PhaseIndex} attempts={diag.PhaseAttempts} "
                        + $"otherSlot={diag.OtherSlotIndex}:0x{diag.OtherSlotStoredId:X} "
                        + $"pathNode={diag.CurrentPathNode} destNode={diag.DestNode} writeCursor={diag.WriteCursor} "
                        + $"walkRate={diag.WalkRate:F4} runRate={diag.RunRate:F4} "
                        + $"followData=0x{diag.FollowDataArray:X}{refText}"
                    : $" soloMode={soloMode}{refText}";

                PluginHost.Log(ok
                    ? $"party follow: 0x{objectId:X} was idle/stalled (head 0x{headType:X}) — "
                        + $"reissued ActionFollowLeader, head now 0x{headAfter:X} "
                        + $"commandable={commandable} inParty={inParty} followFlag={followFlag} "
                        + $"partyFollowInfo=0x{partyFollowInfo:X} "
                        + $"cswPartyIndex={(indexOk ? partyIndex.ToString() : "unreadable")}{diagText}"
                    : $"party follow: could not reissue follow for 0x{objectId:X} (failure {failure}) "
                        + $"head was 0x{headType:X}");
            }
        }
    }

    /// <summary>Human-readable name for a <c>party_follow_stall_recovery_method</c> value — used
    /// in every log line this detector writes so a live A/B never has to map a bare integer back
    /// to a behaviour by hand.</summary>
    private static string MethodName(int method) => method switch
    {
        FollowStallRule.MethodPhaseReset => "phasereset",
        FollowStallRule.MethodFollowLeader => "followleader",
        FollowStallRule.MethodPositionParty => "positionparty",
        FollowStallRule.MethodBreakPriorTie => "priortie",
        FollowStallRule.MethodAdvanceDestNode => "destnode",
        FollowStallRule.MethodPushPriorPastNode => "priorpush",
        _ => $"unknown({method})",
    };

    /// <summary>
    /// ROUND 105 (docs/research/party-reconciliation.md): the per-companion half of the runtime
    /// follow-stall detector. Three things happen here, in this order, and the order matters:
    ///
    /// <list type="number">
    /// <item><b>Confirm the previous fire.</b> If this companion has moved
    /// <see cref="FollowStallRule.RecoveryConfirmedDistanceSquared"/> since the last recovery was
    /// written, the recovery WORKED — log it, clear the anti-spin budget, and stop. Real movement
    /// also means the record cannot currently be wedged, so nothing below needs to run.</item>
    /// <item><b>Read the wedge signature</b> (<see cref="Odyssey.TryGetFollowStallState"/> —
    /// plain loads, no engine calls, safe to do every frame for every companion) and hand it,
    /// plus the timing/anti-spin bookkeeping this class owns, to
    /// <see cref="FollowStallRule.ShouldRecover"/>.</item>
    /// <item><b>Fire exactly one recovery</b> and record when/where, so step 1 has a baseline and
    /// the cooldown has a start.</item>
    /// </list>
    ///
    /// The frozen-duration input is deliberately the SAME <c>_stationarySince</c> bookkeeping this
    /// controller's older "reissue ActionFollowLeader" stall check already maintains, updated
    /// before this call — one motion tracker, not two that can disagree about what "still" means.
    /// </summary>
    private void DriveStallRecovery(
        int slot, uint objectId, Vec3 pos, Vec3 leaderPos, double now, bool leaderMoving, bool suppressed)
    {
        // 1. Did the last fire work?
        if (_stallFires[slot] > 0 && _stallLastFireTime[slot] >= 0)
        {
            float mdx = pos.X - _stallFirePos[slot].X;
            float mdy = pos.Y - _stallFirePos[slot].Y;
            float movedSquared = (mdx * mdx) + (mdy * mdy);

            if (FollowStallRule.RecoveryConfirmed(
                    movedSquared, FollowStallRule.RecoveryConfirmedDistanceSquared))
            {
                PluginHost.Log(
                    $"party follow stall: recovered 0x{objectId:X} via {MethodName(_stallRecoveryMethod)} "
                    + $"— moved {MathF.Sqrt(movedSquared):F2}u in "
                    + $"{now - _stallLastFireTime[slot]:F2}s after {_stallFires[slot]} attempt(s)");

                _stallFires[slot] = 0;
                _stallLastFireTime[slot] = -1;
                _stallLatchLogged[slot] = false;
                return;
            }
        }

        // 2. Read the record. allowCountRestore is latched per slot per module — see
        // _stallCountRestored's own remarks (an engine field write must never be a per-frame one).
        bool allowRestore = !_stallCountRestored[slot];
        Odyssey.FollowStallState state;
        bool resolved = Odyssey.TryGetFollowStallState(objectId, allowRestore, out state);

        // ⚠ 2026-08-29: latch on WRITTEN, not on succeeded — and before the early return, so a
        // restore that did not resolve anything still spends this slot's one allowance for the
        // module. Latching on success alone left a failing restore re-arming itself every single
        // frame, which is how an engine field write ended up on the per-frame path across a
        // module load (the project's own per-frame-write rule) and how the host's
        // UpdatePortraits crash got its count. See docs/research/transition-crashes.md.
        if (state.CountRestoreWritten)
        {
            _stallCountRestored[slot] = true;
        }

        if (!resolved || !state.Valid)
        {
            return;
        }

        if (state.CountWasRestored)
        {
            PluginHost.Log(
                $"party follow stall: 0x{objectId:X} follow record was hidden by party_together_fix's "
                + $"own clamp on party+0x00 — restored CSWParty's member count once, index {state.Index} "
                + "now resolves (round 101's finding, applied to the runtime detector)");
        }

        double frozenSeconds = _tracked[slot] ? now - _stationarySince[slot] : 0.0;
        float pdx = pos.X - leaderPos.X;
        float pdy = pos.Y - leaderPos.Y;
        float leaderDistanceSquared = (pdx * pdx) + (pdy * pdy);
        double secondsSinceLastRecovery = _stallLastFireTime[slot] < 0
            ? double.PositiveInfinity
            : now - _stallLastFireTime[slot];

        if (!FollowStallRule.ShouldRecover(
                state.Phase,
                state.Attempts,
                state.PathNode,
                state.DestNode,
                state.DistSqToTarget,
                state.DistSqToPrior,
                frozenSeconds,
                leaderDistanceSquared,
                leaderMoving,
                suppressed,
                _stallFires[slot],
                secondsSinceLastRecovery,
                FollowStallRule.StallThresholdSeconds,
                FollowStallRule.MinLeaderDistanceSquared,
                FollowStallRule.MaxConsecutiveRecoveries,
                FollowStallRule.RecoveryCooldownSeconds))
        {
            // The one refusal worth saying out loud: the budget is spent and this companion is
            // still wedged. Logged once per module, so "the detector gave up" is never silent.
            if (!_stallLatchLogged[slot]
                && FollowStallRule.ShouldLatchOff(_stallFires[slot], FollowStallRule.MaxConsecutiveRecoveries)
                && FollowStallRule.IsWedgedShape(
                    state.Phase, state.Attempts, state.PathNode, state.DestNode,
                    state.DistSqToTarget, state.DistSqToPrior))
            {
                _stallLatchLogged[slot] = true;
                PluginHost.Log(
                    $"party follow stall: 0x{objectId:X} still wedged after "
                    + $"{FollowStallRule.MaxConsecutiveRecoveries} × {MethodName(_stallRecoveryMethod)} "
                    + $"— giving up for this module (phase={state.Phase} pathNode={state.PathNode} "
                    + $"destNode={state.DestNode} writeCursor={state.WriteCursor} "
                    + $"distSqToTarget={state.DistSqToTarget:F4} distSqToPrior={state.DistSqToPrior:F4})");
            }

            return;
        }

        // 3. Fire.
        bool applied = Odyssey.TryRecoverFollowStall(
            objectId, _stallRecoveryMethod, leaderPos, allowCountRestore: false, out bool wrote, out string detail);

        // Count a refusal exactly like a fire: a method that cannot apply here must not be retried
        // on every cooldown for the rest of the session either.
        _stallFires[slot]++;
        _stallLastFireTime[slot] = now;
        _stallFirePos[slot] = pos;

        PluginHost.Log(
            $"party follow stall: 0x{objectId:X} wedged {frozenSeconds:F1}s at phase={state.Phase} "
            + $"attempts={state.Attempts} pathNode={state.PathNode} destNode={state.DestNode} "
            + $"writeCursor={state.WriteCursor} distSqToTarget={state.DistSqToTarget:F4} "
            + $"distSqToPrior={state.DistSqToPrior:F4} leaderDistSq={leaderDistanceSquared:F2} "
            + $"leaderMoving={leaderMoving} — recovery {_stallFires[slot]}/"
            + $"{FollowStallRule.MaxConsecutiveRecoveries} {MethodName(_stallRecoveryMethod)}: "
            + (applied && wrote ? detail : $"NOT APPLIED ({detail})"));
    }
}

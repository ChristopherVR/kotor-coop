// Odyssey.DoorTransitions.PathNodes.cs - part 3: driving a stalled follower along
// its moving-to path and reading its phase/path-node state.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    public static bool TryAdvanceStalledMovingToPath(uint companionServerObjectId, out bool wrote)
    {
        wrote = false;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            return false;
        }

        nint record = party + 0x24 + (index * 0x88);
        int phase = *(int*)(record + 4);
        if (phase != 2)
        {
            return true; // not parked in the stall shape this targets — nothing to do
        }

        int attempts = *(int*)(record + 0x44);
        if (attempts <= 0)
        {
            return true; // first visit this cycle — let the engine's own normal path run once
        }

        Vec3 prior = *(Vec3*)(record + 0x18);
        Vec3 snapshot = *(Vec3*)(record + 0x5C);
        float dx = prior.X - snapshot.X;
        float dy = prior.Y - snapshot.Y;
        float dz = prior.Z - snapshot.Z;
        const float Epsilon = 0.001f;
        if ((dx * dx) + (dy * dy) + (dz * dz) > Epsilon * Epsilon)
        {
            return true; // genuinely still has distance to close — TestDirectLine's own gate is
                          // the engine's normal path for that case, not this stall shape
        }

        *(int*)(record + 4) = 3; // Moving_To_Path — the same value the engine's own "close
                                  // enough" branch commits, just without TestDirectLine's gate
        wrote = true;
        return true;
    }

    /// <summary>
    /// ROUND 88: cheap, read-only accessor for <c>record+0x6C</c>
    /// (<see cref="FollowDiag.MovingToPathAccumulator"/>) alone, without the rest of
    /// <see cref="TryGetFollowDiagnostics"/>'s work — cheap enough to call every frame so
    /// <see cref="TryForceWalkingWhenMovingToPathStalled"/> can tell "the handler ran since last
    /// frame" apart from "nothing touched it." Returns false when the index does not resolve.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryGetMovingToPathAccumulator(uint companionServerObjectId, out int accumulator)
    {
        accumulator = 0;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            return false;
        }

        nint record = party + 0x24 + (index * 0x88);
        accumulator = *(int*)(record + 0x6C);
        return true;
    }

    /// <summary>
    /// ROUND 88 (2026-08-24, party-reconciliation.md): round 84's own originally-described
    /// mechanism, reproduced live THIS round one layer past <see cref="TryAdvanceStalledMovingToPath"/>'s
    /// own fix: phase settles at 3 (<c>Moving_To_Path</c>, handler 0x00511B60), <c>record+0x6C</c>
    /// (<see cref="FollowDiag.MovingToPathAccumulator"/>) climbs every call (direct, cheap proof
    /// the handler genuinely runs, every frame — round 84's own established diagnostic), yet the
    /// companion's real position never changes and <c>CurrentPathNode</c> never advances, while
    /// <see cref="FollowDiag.CreMoveState"/> (creature+0xA8C) reads 3 ("follow requested") rather
    /// than -1 ("walking"). Round 84 named the cause but did not find it: "whatever real,
    /// engine-native <c>AddMoveToPointAction</c> FindPath originally queued is no longer active
    /// ... for a reason not yet found."
    ///
    /// This round found the reason: <see cref="FollowMoveGateCheckFn"/> (0x004CB120, called from
    /// 0x00511130 — see that delegate's own remarks) is the ONLY other <c>CreMoveState=3</c>
    /// writer in the whole binary, and it is reached from a SEPARATE per-frame dispatch
    /// (0x0057F79E, inside a generic per-action-type "OnUpdate" jump table, ebx==5) than the one
    /// this plugin drives directly — the engine's own natural processing of whichever generic
    /// <c>AddMoveToPointAction</c> FindPath queued, running independently of and interleaved with
    /// this controller's own <see cref="TryDriveFollowLeaderStep"/> calls. Live-measured this
    /// round: that check's own gate (<see cref="FollowDiag.GateFires"/>) reads <c>true</c> on
    /// every single sample taken, including during genuinely smooth walking — i.e. it does not
    /// discriminate WHEN the revert happens, only that the underlying condition (this creature is
    /// never party slot 0) is permanently satisfied, so the revert fires on essentially every
    /// tick the generic dispatcher's own per-object schedule happens to reach this creature's
    /// queued action, independent of this controller's own frame cadence.
    ///
    /// Rather than trying to suppress or race that separate dispatch (not controllable from this
    /// hook — it runs on the engine's own per-object update schedule, not a call this plugin
    /// makes), the FIRST version of this fix re-asserted the one thing it clobbers directly
    /// (<c>CreMoveState = -1</c>) — LIVE-TESTED and MEASURED INSUFFICIENT ALONE: the poke sticks
    /// (moveState reads -1 continuously afterward, confirmed in the fine-log) but real position
    /// never resumes and <c>CurrentPathNode</c> stays frozen — toggling the flag back does not
    /// resurrect whatever real, engine-native queued move action Moving_To_Path's own geometric
    /// check (0x4AA980, round 84's own citation) needs to have something to measure progress
    /// against. Only <c>FindPath</c> (0x51AC10, phase 0, dispatch index 1) actually calls
    /// <c>AddMoveToPointActionToFront</c> AND sets <c>CreMoveState = -1</c> as part of the SAME
    /// shared epilogue (0x51AF5A) — so this instead forces PHASE back to 0, letting the engine's
    /// own FindPath handler do both jobs itself on this controller's very next
    /// <see cref="TryDriveFollowLeaderStep"/> call, the same call already made every frame.
    ///
    /// Fires only when <c>Moving_To_Path</c>'s own handler is confirmed genuinely running (its
    /// accumulator has advanced since the last read — passed in by the caller, which already
    /// samples it every frame) AND real, unconsumed trail remains
    /// (<c>CurrentPathNode &lt; WriteCursor</c>, so this never fires once she has genuinely caught
    /// up) AND <c>CreMoveState</c> currently reads something other than -1 (the live signature of
    /// round 84's own "reverted away from walking" shape).
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether the
    /// poke fired this call. Kill switch: <c>party_follow_force_walking=0</c> (default on).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryForceWalkingWhenMovingToPathStalled(
        uint companionServerObjectId, int lastMovingToPathAccumulator, out bool wrote)
    {
        wrote = false;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            return false;
        }

        nint record = party + 0x24 + (index * 0x88);
        int phase = *(int*)(record + 4);
        if (phase != 3)
        {
            return true; // only Moving_To_Path's own stall shape — nothing to do elsewhere
        }

        int accumulator = *(int*)(record + 0x6C);
        if (accumulator == lastMovingToPathAccumulator)
        {
            return true; // handler is not even being reached right now — a genuine reissue,
                          // not this stall; forcing anything here would not be informed
        }

        int pathNode = *(int*)(record + 0x24);
        nint followData = party + 0x2DC;
        if (!NativeMethods.IsReadable(followData + 0xFA4))
        {
            return true;
        }

        int writeCursor = *(int*)(followData + 0xFA4);
        if (pathNode == writeCursor)
        {
            return true; // genuinely caught up — nothing left to walk toward
        }

        nint creature = ResolveServerObject(companionServerObjectId);
        if (creature == 0)
        {
            return false;
        }

        if (*(int*)(creature + CreMoveState) == -1)
        {
            return true; // already walking — nothing clobbered it since the last sample
        }

        // ROUND 88 REVISION: force phase to 0 (FindPath), not CreMoveState directly — see this
        // method's own remarks for why the CreMoveState-only version measured insufficient.
        // record+0x44 (attempts) is left alone: FindPath's own dispatch epilogue (0x51c95c, per
        // round 84's table) does not zero it either, and this is a resubmission, not a reset.
        *(int*)(record + 4) = 0;
        wrote = true;
        return true;
    }

    /// <summary>
    /// ROUND 88: cheap, read-only accessor for <c>record+4</c> (phase) and <c>record+0x24</c>
    /// (CurrentPathNode) together — lets <see cref="PartyFollowController"/> track "how many
    /// consecutive frames has this exact shape persisted" without calling the heavier
    /// <see cref="TryGetFollowDiagnostics"/> every frame. Returns false when the index does not
    /// resolve.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryGetPhaseAndPathNode(uint companionServerObjectId, out int phase, out int pathNode)
    {
        phase = 0;
        pathNode = 0;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            return false;
        }

        nint record = party + 0x24 + (index * 0x88);
        phase = *(int*)(record + 4);
        pathNode = *(int*)(record + 0x24);
        return true;
    }

    /// <summary>
    /// ROUND 88 (2026-08-24, party-reconciliation.md): a FOURTH, narrower stall found live
    /// downstream of the previous three fixes in this same round — with all of
    /// <see cref="TryAdvanceStalledMovingToPath"/>/<see cref="TryForceWalkingWhenMovingToPathStalled"/>
    /// active, a companion now closes the ENTIRE distance of a real leg down to the very LAST
    /// recorded trail node (live-confirmed: <c>writeCursor - pathNode == 1</c>, ~1.4 units short
    /// of the leader) before parking permanently at phase 7 (<c>PathWaitDest</c>, 0x00512B40).
    ///
    /// Full disassembly of PathWaitDest this round (superseding round 83's own partial read):
    /// its forward search from <c>CurrentPathNode</c> tests against <c>DestNode</c> BEFORE ever
    /// incrementing, so when they are ALREADY equal (this exact live shape) it matches on the
    /// very first comparison and falls into an accept/reject test ONLY when
    /// <c>DestNode == CurrentPathNode</c> (0x512B7E: <c>cmp ebx,edx; jne 0x512c09</c> — UNEQUAL
    /// skips straight to the unconditional accept at 0x512c09, <c>mov [esi+4],3</c>). The test
    /// itself (0x512B86-0x512C03): looks up the recorded point at that index
    /// (<c>CSWPartyFollowDataArray::GetPoint</c>-shaped call, 0x00634CC0) and accepts only when
    /// that point is STRICTLY closer to <c>TargetPoint</c> (<c>record+0x28</c>) than to
    /// <c>PriorPoint</c> (<c>record+0x18</c>) — a "has this waypoint been passed yet" geometric
    /// test. Live-confirmed this round: it never resolves favourably here — same rejected result
    /// across 3+ minutes of continuous observation, no natural recovery.
    ///
    /// <see cref="TryAdvanceStalledPathDestination"/>'s own round-83 fix cannot reach this case:
    /// it requires <c>gap &gt;= 2</c> specifically because <c>DestNode == WriteCursor</c> is
    /// structurally unreachable by this same search loop (the loop's own increment-then-test-
    /// WriteCursor exit fires one iteration before a re-test against a DestNode set to that same
    /// value could ever happen) — and here <c>gap == 1</c>, so <c>WriteCursor</c> is the ONLY
    /// other in-range candidate DestNode could be set to, which is exactly the unreachable one.
    /// <c>DestNode</c> already sits at its only workable value (equal to <c>CurrentPathNode</c>);
    /// there is no poke-the-index fix available here, only the geometric accept/reject test
    /// itself, which this round did not have budget to determine WHY it rejects (the recorded
    /// node's own coordinates were not logged this round — a concrete next diagnostic for
    /// whoever picks this up, if a fix short of the one below is wanted).
    ///
    /// This instead commits phase 3 directly — the exact value the unconditional-accept branch
    /// commits — but ONLY once this specific terminal shape (<c>pathNode == destNode</c>,
    /// <c>writeCursor - pathNode == 1</c>, i.e. genuinely the LAST recorded node, nothing further
    /// to walk toward even if accepted) has been observed for several consecutive frames (passed
    /// in by the caller, which already tracks per-slot stall duration for its own reissue logic)
    /// — never on a single, possibly-still-legitimate visit.
    ///
    /// LIVE-TESTED AND DISPROVEN (round 87, same session): forcing phase 3 here does not produce
    /// real motion. Instead it opens a NEW, DIFFERENT unproductive cycle together with
    /// <see cref="TryForceWalkingWhenMovingToPathStalled"/>: phase visibly cycles
    /// 7 -&gt; 3 -&gt; 0 (FindPath) -&gt; 7 -&gt; ... roughly every 0.3-0.5s, <c>CreMoveState</c>
    /// settles at 1 ("idle") rather than 3 ("follow") or -1 ("walking"), and real position stays
    /// bit-identical for 30+ continuous seconds — worse than the shape this was meant to fix, not
    /// better. <see cref="PartyFollowController"/> defaults this OFF
    /// (<c>party_follow_accept_final_node=0</c>) for that reason; the function is kept, named, and
    /// documented as a ruled-out avenue (this project's own established convention — see round
    /// 80's <c>TryPokeStepAccumulator</c>) rather than deleted.
    ///
    /// ROUND 89 (2026-08-24, party-reconciliation.md): round 88's own leading theory above — that
    /// the accept branch "may do more than just write the phase" — is now DISASSEMBLY-REFUTED, not
    /// just untraced. Full disassembly of 0x00512C09 onward: <c>pop edi; mov [esi+4],3; mov eax,
    /// [0x745dbc]; pop esi; pop ebx; add esp,0xc; ret 0x10</c> — <c>[0x745dbc]</c> is DATA (the
    /// int constant <c>1</c>, confirmed by reading the raw bytes, not a function), loaded into
    /// <c>eax</c> purely as this function's OWN return value. There is no other field write and no
    /// call, on EITHER path that reaches 0x512C09 (the unconditional "search advanced past
    /// CurrentPathNode" branch at 0x512B80, and the geometric-test PASS branch at 0x512C03) — this
    /// is byte-for-byte what this function already does: <c>*(int*)(record+4) = 3</c>, nothing
    /// more.
    ///
    /// The return value of <c>1</c> IS real and consumed, but by the CALLER, not by anything this
    /// function itself arms: <c>WalkUpdateLocation_QuickWalk_FollowLeader</c>'s own internal
    /// dispatch loop (0x0051C955/0x51C95C: <c>ebp = eax</c>, then 0x0051C972:
    /// <c>cmp ebp,[0x745dbc]; je 0x51C7D0</c>) treats a handler returning exactly <c>1</c> as
    /// "phase changed, please redispatch immediately" — it loops back to 0x51C7D0 and, subject to
    /// a per-call frame-time-budget float compare against a global (0x73D700) and the
    /// <c>attempts&lt;50</c> watchdog, re-enters the SAME 11-entry jump table with the NEW phase
    /// value and calls that handler too, in the SAME engine call. This is a generic mechanism
    /// applying to every handler in the switch (each handler that returns 1 gets this same
    /// same-call cascade), not something unique to PathWaitDest's accept path. Since this function
    /// pokes <c>record+4</c> from OUTSIDE that dispatcher — and, per
    /// <see cref="PartyFollowController"/>'s own call order, strictly AFTER this frame's own
    /// <c>TryDriveFollowLeaderStep</c> call already returned — <c>Moving_To_Path</c>'s handler does
    /// not get this same-call cascade; it only runs on the FOLLOWING frame's normal dispatch. This
    /// is a real, disassembly-confirmed difference from the genuine accept path, but is very likely
    /// NOT what actually causes the disproven cycling: <see cref="TryAdvanceStalledMovingToPath"/>
    /// and <see cref="TryForceWalkingWhenMovingToPathStalled"/> poke <c>record+4</c> the EXACT same
    /// way (also from outside the dispatcher, also strictly after that frame's
    /// <c>TryDriveFollowLeaderStep</c> call, in the same per-companion loop) and are live-verified
    /// to produce real, sustained motion — a one-frame dispatch deferral evidently is not fatal in
    /// general.
    ///
    /// Sharper, still-unverified hypothesis for why THIS poke specifically fails where the other
    /// two succeed: PathWaitDest's geometric accept/reject test (0x512B86-0x512C03) may be
    /// correctly rejecting, not spuriously blocking. It compares the recorded node's own point
    /// (<see cref="FollowDiag.RecordedDestPoint"/>, added this round, read via
    /// <c>CSWPartyFollowDataArray::GetPoint</c>'s own disassembled addressing —
    /// <c>followData + index*40</c>, no indirection) against <c>TargetPoint</c>
    /// (<c>record+0x28</c>) and <c>PriorPoint</c> (<c>record+0x18</c>) — both of which round 73's
    /// own finding already established go STALE (frozen, not refreshed by the engine's own
    /// <c>CSWParty::Update</c>/<c>RecaulateFollowPoint</c>) for exactly a companion parked in this
    /// stall shape. If the frozen operands make the "has this waypoint been passed" test correctly
    /// say "no, not yet" (because <c>TargetPoint</c> genuinely has not caught up to where the
    /// leader has since walked), forcing the accept anyway hands <c>Moving_To_Path</c> a segment
    /// with no honest remaining distance to walk — which would explain the observed
    /// 7 -&gt; 3 -&gt; 0 -&gt; 7 cycle (arrive instantly, nothing to do, fall back to FindPath, land
    /// on the same degenerate node again) better than a missing side effect does, since this round
    /// confirmed no side effect is being skipped at all. NOT live-tested this round (no rig
    /// access) — <see cref="FollowDiag.RecordedDestPoint"/> exists so a future live round can read
    /// it alongside <c>TargetPoint</c>/<c>PriorPoint</c> at the exact stall moment and settle this
    /// directly, without another disassembly pass.
    ///
    /// This function's own behaviour is UNCHANGED this round (still exactly
    /// <c>*(int*)(record+4) = 3</c>) — the original "missing side effect" premise it was written
    /// to eventually complete does not exist to complete, so extending the poke itself would be
    /// building on a disproven premise rather than a confirmed one.
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether the
    /// poke fired this call. Kill switch (to enable): <c>party_follow_accept_final_node=1</c>
    /// (default off).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryAcceptFinalPathNode(uint companionServerObjectId, out bool wrote)
    {
        wrote = false;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            return false;
        }

        nint record = party + 0x24 + (index * 0x88);
        int phase = *(int*)(record + 4);
        if (phase != 7)
        {
            return true;
        }

        int pathNode = *(int*)(record + 0x24);
        int destNode = *(int*)(record + 0x34);
        if (pathNode != destNode)
        {
            return true; // not the trivial-match shape this fires on
        }

        nint followData = party + 0x2DC;
        if (!NativeMethods.IsReadable(followData + 0xFA4))
        {
            return true;
        }

        int writeCursor = *(int*)(followData + 0xFA4);
        int gap = writeCursor - pathNode;
        if (gap < 0)
        {
            gap += 100;
        }

        if (gap != 1)
        {
            return true; // gap==0 is "genuinely caught up, nothing recorded"; gap>=2 is
                          // TryAdvanceStalledPathDestination's own, different shape
        }

        *(int*)(record + 4) = 3;
        wrote = true;
        return true;
    }

    /// <summary>
    /// ROUND 90 (2026-08-24, party-reconciliation.md): builds directly on round 89's own
    /// falsifiable hypothesis — that <c>PathWaitDest</c>'s geometric accept/reject test
    /// (0x512B86-0x512C03) may be REJECTING CORRECTLY at the very last recorded trail node
    /// because <c>TargetPoint</c>/<c>PriorPoint</c> (record+0x28/record+0x18) are stale, not
    /// because the test itself is broken. <see cref="TryAcceptFinalPathNode"/> already tried
    /// forcing the TEST'S OWN OUTPUT (phase=3) directly and was live-disproven (round 88) — this
    /// instead refreshes the TEST'S INPUTS and lets the engine's own accept/reject logic decide
    /// on its own, on the theory that a forced phase bypasses whatever else the real accept path
    /// might depend on downstream (a possibility round 89's own disassembly could not fully rule
    /// out — see that function's remarks), while a pure geometry refresh cannot, because it
    /// changes nothing but the two float triples the real engine code already reads. Round 89's
    /// own disassembly of 0x512C09 onward found nothing else the accept branch writes besides
    /// <c>record+4=3</c> itself, so refreshing the INPUTS and letting the REAL branch fire is
    /// strictly narrower than forcing its output — if the geometry was already correct and the
    /// stall persists anyway, this changes nothing (round 89's hypothesis is refuted); if the
    /// geometry was stale, this lets the unmodified engine code reach its own correct phase=3
    /// (or correctly keep rejecting, if the leader genuinely has not passed this waypoint yet).
    ///
    /// Uses the exact same non-degenerate Prior/Target write <see cref="TryDriveFollowTarget"/>
    /// already ships (round 83) — Prior &lt;- the OLD target, Target &lt;- the fresh leader
    /// position, never collapsing the two into an identical, direction-less point — but bypasses
    /// that function's own ~10cm "meaningfully different" gate, and ONLY inside the same narrow
    /// terminal shape <see cref="TryAcceptFinalPathNode"/> already detects (phase 7,
    /// <c>pathNode == destNode</c>, <c>writeCursor - pathNode == 1</c>: genuinely the last
    /// recorded node, nothing further to walk toward). That 10cm gate exists in the general case
    /// to avoid stomping <c>record+0x68</c> (the dirty-snapshot flag) every frame once a
    /// companion is genuinely caught up and walking normally (see that function's own remarks) —
    /// but a companion parked in THIS exact terminal shape is, by definition, not walking
    /// normally; the residual risk this targets is the leader's own final, small steps each
    /// individually landing under that 10cm threshold relative to whatever was last written,
    /// leaving <c>TargetPoint</c> up to just under 10cm behind the leader's true final rest
    /// position — not the dominant driver of the observed ~1.3-1.4 unit stall gap itself (that
    /// gap is the COMPANION's distance from the leader, an unrelated quantity), but exactly the
    /// kind of small, silent staleness round 89's own hypothesis needs ruled out before
    /// concluding the geometric test is rejecting correctly rather than merely under-fed.
    ///
    /// LIVE-TESTED 2026-08-24 (docs/research/party-reconciliation.md, "round 90's
    /// TryRefreshFinalNodeTarget fix candidate ... REFUTED"): reproduced round 88's own recipe
    /// (<c>tp 20 22 -1.3</c>, <c>walk 10 15</c>, END_M01AA) with this and
    /// <c>party_follow_finelog=1</c> both on. Result: <c>target</c> was ALREADY bit-identical to
    /// <c>leaderPos</c> on the very first finelog sample after entering the terminal shape — not
    /// stale by up to 10cm as round 89's hypothesis required — and stayed bit-identical,
    /// unchanged, for 76+ continuous seconds (2244 samples). This function's own ~1cm epsilon
    /// therefore never found anything worth writing in that repro: it did not fire, and
    /// <c>phase</c> never advanced past 7 regardless. Round 89's staleness hypothesis is refuted
    /// for the standard short-leg repro; the search moves to the <c>fcompp</c> at 0x512BF2 under a
    /// live debugger, exactly as that round's own verdict named as the next step. No regression
    /// observed (moveState held a stable 3 the whole time, no phase-cycling) — kept, and left
    /// default OFF, per this project's own "leave it off and record what was observed" rule for a
    /// fix that measures insufficient rather than harmful. A longer leg where the leader is still
    /// taking small steps as the companion arrives at phase 7 was not tried and remains a possible
    /// confound (see the doc entry for the caveat).
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether
    /// the refresh actually fired this call (false when not in the terminal shape, or when the
    /// target was already within ~1cm of <paramref name="leaderPos"/> and a write would have been
    /// degenerate). Kill switch (to enable): <c>party_follow_refresh_final_node=1</c> (default
    /// OFF — live-tested, measured insufficient against round 89's hypothesis, not a default-flip
    /// candidate).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

}

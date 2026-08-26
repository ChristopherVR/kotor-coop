// Odyssey.DoorTransitions.cs - part 1 of the follow-leader logic that keeps the
// party together while a door module transition is pending: follow-point
// recalculation, client party index lookup, and follow diagnostics.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    public static bool RecalculateFollowPoint()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        RecalculateFollowPointFn(party);
        return true;
    }

    /// <summary>
    /// ROUND 72 DIAGNOSTIC (docs/research/phantom-locomotion.md, party-reconciliation.md):
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360 — the engine's own PER-FRAME
    /// continuation for a companion whose <c>CreMoveState</c> (+0xA8C) already reads 3
    /// ("follow") — opens by resolving <c>this</c> creature's own index within CSWParty via
    /// exactly this chain (<c>ServerToClientObjectId</c> then <c>CSWParty::GetIndex</c>) and
    /// bails out for the rest of the frame, without ever queuing a step, if that index comes
    /// back -1 (not found) or 0 (this creature IS the party's own leader slot — never follows
    /// itself). <see cref="PartyFollowController"/> calls this to tell "the index lookup itself
    /// is the blocker" apart from "the index resolves fine and something deeper in the same
    /// function's own path/offset computation is."
    /// </summary>
    public static bool TryGetClientPartyIndex(uint serverObjectId, out int index)
    {
        index = -2;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, serverObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        index = GetPartyIndexFn(party, clientObjectId);
        return true;
    }

    /// <summary>
    /// ROUND 80 DIAGNOSTIC (party-reconciliation.md): <c>CSWSCreature::GetWalkRate</c> 0x004F1B20
    /// — <c>__thiscall</c>, no stack args, returns a clamped rate (<c>[creature+0xA08]</c> clamped
    /// to [0, 1.5] times <c>[[creature+0xA74]+0x19C]</c> times 1000.0, sanity-clamped to 0.0 if the
    /// product exceeds 100.0). Round 80's own disasm of
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360 found a SECOND gate past the
    /// step-accumulator one round 72 already named: at 0x51C7B9-0x51C7DF, a per-frame computed
    /// scalar (built from this rate, a distance-bucket multiplier, and the step accumulator) is
    /// compared against 0.0 and the entire phase-dispatch block (and its <c>attempts</c> counter
    /// increment) is skipped whenever that scalar reads negative OR unordered (NaN) — never when
    /// it is exactly zero. Read here alongside <see cref="FollowDiag.RunRate"/> to see whether
    /// either rate itself is ever negative/NaN for a parked companion (which would explain
    /// <c>attempts</c> freezing without any watchdog ever firing).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float> GetWalkRateFn =
        (delegate* unmanaged[Thiscall]<nint, float>)0x004F1B20;

    /// <summary>CSWSCreature::GetRunRate 0x004F1BE0 — same shape as <see cref="GetWalkRateFn"/>,
    /// reading <c>[creature+0xA74]+0x198</c> instead of <c>+0x19C</c>.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float> GetRunRateFn =
        (delegate* unmanaged[Thiscall]<nint, float>)0x004F1BE0;

    /// <summary>
    /// ROUND 88 (2026-08-24, party-reconciliation.md): the ONLY other site in the whole binary
    /// that writes a literal 3 into <c>CreMoveState</c> (<c>creature+0xA8C</c>), found by a full
    /// <c>tools/fieldwrites.py 0xa8c</c> sweep — 0x005111A1, inside a small, previously
    /// undocumented, thiscall function at 0x00511130 (no stack args, returns a bool-shaped
    /// engine constant at 0x745dbc/0x745dc4). Full disassembly this round: after several
    /// liveness/commandability gates (<c>creature+0xA88</c>/<c>+0x4C0</c>[<c>party_follow_info</c>,
    /// per <c>tools/kotor1_0_3.db</c>]/vtbl+0x94 GetDead/0x63E7F0/GetDeadTemp 0x4EF890/
    /// <c>creature+0x340</c>/<c>creature+0x9F0</c>&amp;2, none logged separately here — all
    /// already known-true for a live, commandable companion mid-follow) it calls
    /// <see cref="FollowMoveGateCheckFn"/> (0x004CB120) and, ONLY if that returns nonzero, compares
    /// <see cref="ResolvePartySlot0Fn"/>'s result against the creature's OWN self id
    /// (<c>[creature+4]</c>, the same field <c>KotorGameBridge.OffObjectId</c> already names) — if
    /// they differ (always true for a companion, which is never party slot 0 itself), it sets
    /// <c>CreMoveState = 3</c>, unconditionally reverting "walking" back to "follow requested."
    /// This is the single most direct static candidate found for round 84's still-open
    /// "<c>CreMoveState</c> reverts from -1 to 3 partway through a leg, for a reason not yet
    /// found" mystery — everything gates on whether <see cref="FollowMoveGateCheckFn"/> fires.
    /// See that delegate's own remarks for what it tests and why. Called ONLY from one site,
    /// 0x0057F79E, inside a large per-action-type jump-table dispatcher (0x5801E4, ~0x41
    /// entries) gated on a separate "phase/reason" value (<c>ebx==5</c>) this round did not
    /// further name — consistent with this being the specific queued action type's own
    /// per-tick "OnUpdate" handler, i.e. genuinely reached every frame the move action is live,
    /// not a rare edge case.
    ///
    /// What this function ITSELF tests, fully disassembled: resolves <c>[creature+0x8C]</c> (the
    /// creature's own AREA id; <see cref="GetAreaId"/> already names this field,
    /// "module-lifetime stable") through <c>CServerExoApp::GetObjectArray</c> +
    /// <c>CGameObjectArray::GetGameObject</c> (the same two-step <see cref="ResolveServerObject"/>
    /// collapses into one call); if that id does not resolve to a live object, returns 0 (false —
    /// the caller then takes 0x511130's no-revert exit). If it DOES resolve, calls the resolved
    /// object's own vtable slot +0x28 — <c>tools/kotor1_0_3.db</c>'s <c>GameObjectMethods</c>
    /// table names this slot <c>AsSWCModule</c> (offset 28; cross-checked against the SAME
    /// table's offset 16 = <c>AsSWSObject</c> and offset 48 = <c>AsSWSCreature</c>, both of which
    /// match this project's own independently live-verified vtable citations round 32 already
    /// established — the table is trustworthy for this row too) — and returns whatever that call
    /// returns. Read plainly: "does my current area's own object id currently resolve to
    /// something that casts as a MODULE" — an odd, narrow condition to be gating a per-frame
    /// move-completion check on, and the leading candidate for why the revert is INTERMITTENT
    /// (round 81/84's own "closes ~90% then stalls" shape) rather than immediate: most frames
    /// this presumably reads false, some frames — not yet understood why — it does not. NOT yet
    /// correlated against a live repro; see <see cref="FollowDiag.GateAreaId"/>/
    /// <see cref="FollowDiag.GateFires"/> for the live diagnostic this round wires in to test it.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> FollowMoveGateCheckFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x004CB120;

    /// <summary>
    /// ROUND 72 DIAGNOSTIC: the raw per-member follow-data record CSWParty holds at
    /// <c>this+0x24+index*0x88</c> (party-reconciliation.md's own stride, re-confirmed against
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360's own disasm this round), plus
    /// the creature's own <c>+0xD0</c> (an angle/step accumulator the same function tests before
    /// ever touching the follow-data record) and <c>+0x1F8</c> (must be non-zero for
    /// <c>WalkUpdateLocation_QuickWalk</c> 0x0051D9C0 — the caller that dispatches into 0x0051C360
    /// on <c>CreMoveState==3</c> — to run AT ALL; see 0x0051D9E0's own gate). Returns false when
    /// the index itself does not resolve (<see cref="TryGetClientPartyIndex"/> already covers
    /// that case on its own).
    /// </summary>
    public static bool TryGetFollowDiagnostics(nint creature, uint serverObjectId, out FollowDiag diag)
    {
        diag = default;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, serverObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        diag.Index = index;

        if (index <= 0)
        {
            // <=0: no record to read (index 0 is the leader slot 0x0051C360 itself bails on;
            // -1 is "not found"). Whatever the caller already logs about the index covers this.
            return true;
        }

        nint record = party + 0x24 + (index * 0x88);
        diag.TargetPoint = *(Vec3*)(record + 0x28);
        diag.PriorPoint = *(Vec3*)(record + 0x18);
        diag.Snapshot = *(Vec3*)(record + 0x5c);
        diag.SnapshotPendingFlag = *(int*)(record + 0x68);

        diag.StepAccumulator = *(int*)(creature + 0xd0);
        diag.CachedPosPairLo = *(int*)(creature + 0xb0);
        diag.CachedPosPairHi = *(int*)(creature + 0xb4);
        diag.EnabledFlag = *(int*)(creature + 0x1f8);

        // ROUND 80: see GetWalkRateFn's own remarks — the phase-dispatch gate at 0x51C7B9 skips
        // the whole phase dispatch (attempts never increments) whenever a scalar built partly
        // from these rates reads negative or NaN.
        diag.WalkRate = GetWalkRateFn(creature);
        diag.RunRate = GetRunRateFn(creature);

        // ROUND 74: the step FSM this round is chasing, per WalkUpdateLocation_QuickWalk_
        // FollowLeader's own disasm (0x0051C7E5-0x0051C7F9) — record+4 is the phase index the
        // 11-entry jump table at 0x51CCEC dispatches on (incremented BEFORE the jump: the
        // dispatch uses phase+1, so a phase that has never left its module-load value reads
        // back here as whatever that raw value was, not necessarily 0), and record+0x44 is a
        // SEPARATE attempt/stall counter incremented every call regardless of phase — once it
        // exceeds 0x32 (50) the function force-resets phase to -1 and the counter to 0
        // (0x51C95E-0x51C96B). Sampled together, these say whether the phase dispatch is being
        // reached and cycling AT ALL (proves/disproves this round's leading theory that an
        // EARLIER gate — the edi+0xD0 step-accumulator throttle at 0x51C458-0x51C486 — returns
        // early every frame and never reaches the phase dispatch, vs. the phase dispatch being
        // reached but stalling once inside it, e.g. on the empty "other party slot" read at
        // 0x51C744-0x51C7AC).
        diag.PhaseIndex = *(int*)(record + 4);
        diag.PhaseAttempts = *(int*)(record + 0x44);

        // The "other party member" slot 0x0051C744 resolves against (this creature's own
        // index 1 <-> 2, or 2 <-> 1) — its raw stored id (record2+0x0) read directly the same
        // way, so a genuinely empty/never-populated slot 2 in a one-companion party shows up
        // here as whatever value it actually holds (0 if the array is zero-initialized,
        // garbage otherwise) rather than being inferred.
        int otherIndex = index == 1 ? 2 : index == 2 ? 1 : 0;
        nint otherRecord = party + 0x24 + (otherIndex * 0x88);
        diag.OtherSlotIndex = otherIndex;
        diag.OtherSlotStoredId = *(uint*)otherRecord;

        // ROUND 75 (2026-08-23, party-reconciliation.md): record+0x24 is the "current path
        // node" index PathWaitDest (0x0051C7E5's dispatch, phase 6 -> 0x00512B40) compares
        // against the write cursor — confirmed by disasm cross-reference:
        // CSWPartyFollowDataArray::FindFurthestPartyMemeber_Current (0x00634D40) reads the SAME
        // field through a different path (party+0x24+index*0x88+0x24, i.e. [CSWParty-backptr
        // +0xD0] for index 1 = record+0x24 for index 1) when computing which companion is
        // furthest behind on the shared trail. party+0x2DC is CSWParty's own `follow_data`
        // pointer (CSWPartyFollowDataArray*, tools/kotor1_0_3.db offsets table); +0xFA4 on THAT
        // object is the write cursor PathWaitDest polls (tools/fieldwrites.py 0xFA4 finds its 8
        // write sites, entirely inside CSWPartyFollowDataArray::Reset/SmoothFollowPath/
        // ProcessPoint — never inside PathWaitDest itself, confirming it is a CONSUMER, not the
        // producer). Read together, these say whether the trail is genuinely being recorded
        // (cursor advancing) while a stalled companion's own current-node index sits frozen.
        diag.CurrentPathNode = *(int*)(record + 0x24);

        // ROUND 83 (2026-08-23, party-reconciliation.md): record+0x34 — read (never written) by
        // PathWaitDest (0x0051512B40's own disasm, full trace this round) as the loop's OWN exit
        // target: it walks the 100-entry circular buffer forward from CurrentPathNode looking for
        // an index that equals THIS field, returning "not ready" if it wraps all the way to
        // WriteCursor first without finding a match. A full binary sweep for every writer of
        // record+0x34 (FindPath 0x51AC10, phases 1-4, WaitingForPath 0x5127D0, PathWaitDest
        // itself, phases 8-9, CSWPartyFollowDataArray::Reset/SmoothFollowPath/ProcessPoint) found
        // exactly ONE write site in the whole binary: inside WaitingForPath (0x00512865,
        // `mov [esi+0x34], eax`), where it is set to the SAME value just written into
        // CurrentPathNode (record+0x24) at that exact moment (0x00512852). Two OTHER handlers
        // (phase1 at 0x00511847, phase3/"Moving_To_Path" at 0x00511E5C) independently advance
        // CurrentPathNode further as real walking progresses — but NOTHING ever advances this
        // field to match. If CurrentPathNode is later advanced past this value by one of those
        // other handlers before PathWaitDest is re-entered, the forward-only search can never
        // find it again (it is now BEHIND the search start, not ahead), and PathWaitDest returns
        // "not ready" every single call, forever — a plausible, disassembly-grounded explanation
        // for the phase=7 permanent stall rounds 74/75/80/81 already observed live. Read here,
        // read-only, to test that prediction against real stall data before writing anything.
        diag.DestNode = *(int*)(record + 0x34);

        // ROUND 83 (2026-08-23, party-reconciliation.md): CreMoveState (creature+0xA8C, this
        // document's own established offset since round 71) — the second phase=3 ("Moving_To_Path",
        // handler 0x00511B60) stall this round found (see PartyFollowController's own remarks)
        // needs telling apart "still nominally walking, just not making geometric progress" from
        // "quietly reverted out of the walking state while the follow-FSM's own phase field never
        // noticed." record+0x6c is a running accumulator 0x00511B60 adds creature+0xD0 (the
        // step-accumulator this document already tracks) into, every single call — since this
        // plugin pokes creature+0xD0 unconditionally every frame, this field climbing is a direct,
        // cheap proof the handler is still being reached and running its own body, independent of
        // whether it ever advances CurrentPathNode.
        diag.CreMoveState = *(int*)(creature + 0xA8C);
        diag.MovingToPathAccumulator = *(int*)(record + 0x6C);

        // ROUND 88 (2026-08-24, party-reconciliation.md): live test of the static hypothesis
        // built around FollowMoveGateCheckFn/0x00511130 (see those delegates' own remarks) — the
        // only other CreMoveState=3 writer in the whole binary, gated on this exact condition.
        // Read-only: FollowMoveGateCheckFn (0x004CB120) only resolves and downcasts, never
        // writes; ResolvePartySlot0Fn is the same bare zero-argument call round 30 already
        // proved safe and pure.
        diag.GateAreaId = *(uint*)(creature + 0x8C);
        diag.GateFires = FollowMoveGateCheckFn(creature) != 0;
        diag.PartySlot0Id = ResolvePartySlot0Fn();
        diag.SelfMatchesPartySlot0 = diag.PartySlot0Id == serverObjectId;

        // ⚠ CRASHED THE HOST LIVE the first attempt at this read (2026-08-23): this code
        // originally treated party+0x2DC as a POINTER FIELD (mirroring
        // tools/kotor1_0_3.db's "CSWParty.follow_data: CSWPartyFollowDataArray*" offset row)
        // and dereferenced it — which read a genuinely garbage value (0x41A01AD9, which
        // decodes as a plausible position float, ~20.03 — clearly stray struct data, not a
        // heap pointer) and then crashed dereferencing THAT as a pointer's own +0xFA4.
        // Disassembling CSWParty::Update's own call into
        // CSWPartyFollowDataArray::FindFurthestPartyMemeber_Current (0x006372E1:
        // `lea ebx, [edi + 0x2dc]; mov ecx, ebx; call 0x634da0`) settles it: that is an LEA,
        // not a MOV — CSWPartyFollowDataArray is EMBEDDED INLINE inside CSWParty at +0x2DC,
        // not stored behind a pointer there. The DB's "*" suffix on this one offset row is
        // wrong (this project's own established caveat: ~310 DB rows do not match the
        // binary); CSWParty's own real size is therefore at least 0x2DC + 0xFB4 (the embedded
        // array's own 4020-byte size) = 0x1290, not the DB's claimed 736. followData is
        // therefore the ADDRESS party+0x2DC itself, never dereferenced as a pointer — still
        // range-checked before the final field read purely as post-crash discipline.
        nint followData = party + 0x2DC;
        diag.FollowDataArray = followData;
        diag.WriteCursor = NativeMethods.IsReadable(followData + 0xFA4)
            ? *(int*)(followData + 0xFA4)
            : -1;

        // ROUND 88: live test of a SECOND, independent stall shape found this round —
        // distinct from round 84's "CreMoveState reverts -1->3" (that mechanism, see
        // FollowMoveGateCheckFn's own remarks, was live-measured NOT the active blocker for
        // THIS reproduction: moveState pins at 3 continuously, never touching -1, once a
        // companion is left within ~1-2 path nodes of a now-stationary leader). Full disasm of
        // handler 0x00511860 (phase=2, previously unexamined) this round: it computes the
        // distance between PriorPoint (record+0x18) and Snapshot (record+0x5c) and compares its
        // SQUARE against a threshold built from `*(float*)followData * [0x746f4c]`, squared —
        // i.e. followData+0x0 (the FIRST dword of the whole, party-wide, embedded
        // CSWPartyFollowDataArray struct) is read as a float and used as THIS tick's
        // "close enough to advance" radius. Live data shows PriorPoint==Snapshot (a
        // zero-length segment, trivially "close enough" under any sane positive threshold) yet
        // phase never advances past 2 — consistent with this threshold-source field itself
        // reading zero or a degenerate value, the same class of "self-inflicted, our own direct
        // engine call bypasses whatever the OUTER caller normally refreshes" bug rounds 80/84
        // already found twice for creature+0xD0 and record+0x34. Read-only.
        diag.MoveBudget = NativeMethods.IsReadable(followData) ? *(float*)followData : float.NaN;

        // ROUND 89 (2026-08-24, party-reconciliation.md): CSWPartyFollowDataArray::GetPoint's own
        // disasm (0x00634CC0 -> in-range path at 0x00634D1E: `lea ecx,[esi+esi*4]; lea eax,
        // [edi+ecx*8]`, i.e. `this + index*40`) settles the recorded-trail-node layout precisely —
        // node i's position is a plain 12-byte float3 at `followData + i*40`, no indirection, the
        // SAME pointer arithmetic PathWaitDest itself uses right before its own accept/reject test
        // (see TryAcceptFinalPathNode's own remarks). Read here, read-only, purely so a live round
        // can compare this against TargetPoint/PriorPoint above at the exact moment PathWaitDest's
        // geometric test is rejecting, without needing another disassembly pass first.
        diag.RecordedDestPoint = diag.CurrentPathNode is >= 0 and < 100
            && NativeMethods.IsReadable(followData + (diag.CurrentPathNode * 40) + 8)
            ? *(Vec3*)(followData + (diag.CurrentPathNode * 40))
            : default;

        diag.Valid = true;
        return true;
    }

    public struct FollowDiag
    {
        public bool Valid;
        public int Index;
        public Vec3 TargetPoint;
        public Vec3 PriorPoint;
        public Vec3 Snapshot;
        public int SnapshotPendingFlag;
        public int StepAccumulator;
        public int CachedPosPairLo;
        public int CachedPosPairHi;
        public int EnabledFlag;
        public int PhaseIndex;
        public int PhaseAttempts;
        public int OtherSlotIndex;
        public uint OtherSlotStoredId;
        public int CurrentPathNode;
        public int DestNode;
        public int CreMoveState;
        public int MovingToPathAccumulator;
        public nint FollowDataArray;
        public int WriteCursor;
        public float WalkRate;
        public float RunRate;

        // ROUND 88: see FollowMoveGateCheckFn's own remarks — the live test of the "only other
        // CreMoveState=3 writer in the binary" hypothesis.
        public uint GateAreaId;
        public bool GateFires;
        public uint PartySlot0Id;
        public bool SelfMatchesPartySlot0;
        public float MoveBudget;

        // ROUND 89: see TryGetFollowDiagnostics's own remarks — the recorded trail node at
        // CurrentPathNode's own index, read directly via CSWPartyFollowDataArray::GetPoint's own
        // disassembled addressing (followData + index*40), for comparing against TargetPoint/
        // PriorPoint at the exact moment PathWaitDest's geometric accept/reject test is rejecting.
        public Vec3 RecordedDestPoint;
    }

    /// <summary>
    /// ROUND 72 EXPERIMENT, LIVE-DISPROVEN: writes the per-member follow-data record's OWN
    /// cached leader position (<c>record+0x18</c>, <see cref="FollowDiag.PriorPoint"/>) directly
    /// — testing, combined with an immediate <see cref="RecalculateFollowPoint"/> call, whether
    /// <c>CSWParty::RecaulateFollowPoint</c> computes the follow TARGET point (<c>record+0x28</c>)
    /// FROM this cached field. The WRITE itself is confirmed live to work and to stick (read back
    /// correctly on the very next diagnostic pass, nothing else claws it back) — but the recompute
    /// that followed it still left <c>record+0x28</c> exactly at its module-load value, so this
    /// field is NOT the recompute's input, or is not sufficient on its own. NOT called from
    /// <see cref="PartyFollowController"/>'s shipped path — a field write with a measured zero
    /// benefit is not something to leave mutating engine state on every stall pass. Kept here,
    /// with its own live result recorded, as a ruled-out avenue for whoever picks this up next
    /// (see <see cref="RecalculateFollowPointFn"/>'s own remarks for the current prime suspect,
    /// <c>CSWParty::Update</c> 0x00637050).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryPokeFollowPriorPosition(uint companionServerObjectId, Vec3 leaderPos)
    {
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
        *(Vec3*)(record + 0x18) = leaderPos;
        return true;
    }

    /// <summary>
    /// ROUND 74 EXPERIMENT: <c>WalkUpdateLocation_QuickWalk_FollowLeader</c>'s own step FSM
    /// (0x0051C360, jump table at 0x51CCEC) reads <c>record+4</c> as its phase index (dispatch
    /// index = phase+1) and <c>record+0x44</c> as an attempt counter that, once it passes 0x32
    /// (50) WITHOUT the phase changing, force-resets phase to -1 (0x51C964-0x51C96B). Dispatch
    /// index 0 (phase == -1) is the inline block at 0x51C800: it re-snapshots the creature's
    /// current position into <c>record+0x5c</c> and sets phase DIRECTLY to 5 — WITHOUT ever
    /// calling <c>WalkUpdateLocation_QuickWalk_FollowLeader_FindPath</c> (phase 0, dispatch
    /// index 1, 0x51AC10), the ONLY handler that actually submits a fresh pathfinding request
    /// (via <c>CSWSCreature::AddMoveToPointActionToFront</c>) and unconditionally writes
    /// <c>CreMoveState</c> (creature+0xA8C) to -1 ("walking") on its way out
    /// (0x51AF5A, the shared epilogue both of FindPath's branches funnel into). Phase 5 is
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader_WaitingForPath</c> (0x5127D0), which does
    /// nothing but poll an async job-status field (creature+0x2DC, then +0xFA8 off THAT
    /// pointer) for &gt;= 5 and return "not ready" otherwise — a job that was never submitted,
    /// because FindPath was skipped. Once phase lands on -1 this way there is no natural way
    /// back into FindPath: -1 always redispatches to entry 0, which always re-enters phase 5
    /// without resubmitting anything. Live-observed round 74: a companion stalled long enough
    /// to have hit this cycle once (plausible after many minutes of round 71-73's repeated
    /// stall-reissues, each re-entering this same per-frame continuation) reads phase=5,
    /// attempts cycling through 0x32 and wrapping back down — the FSM genuinely running, every
    /// frame, permanently unable to leave the WaitingForPath/-1 ping-pong.
    ///
    /// This writes <c>record+4</c> to 0 (dispatch index 1 next call — FindPath, not the
    /// entry-0 shortcut) and <c>record+0x44</c> to 0 (clear the attempt counter so the
    /// watchdog does not immediately re-force -1 on the very next call), ONLY when the phase
    /// is presently stuck at -1 — the one state this round's disasm shows can never
    /// self-recover on its own. A companion in any other phase is left alone: those are
    /// mid-flight in the FSM's own real progression and a phase stomp there would be actively
    /// destructive, not a nudge.
    ///
    /// ROUND 83 CORRECTION (party-reconciliation.md): originally also fired on phase == 5
    /// (WaitingForPath), on the theory that round 74's own disasm showed it "can never
    /// self-recover" either. That was true only in the SAME broken context round 74 measured it
    /// in — before round 80's step-accumulator fix existed, phase 5 was reached ONLY via the -1
    /// ping-pong's own inline reset (which really can never resubmit a job on its own), so every
    /// phase-5 sighting really was the pathological case. Once round 80's fix lets the FSM
    /// genuinely progress, phase 5 is ALSO the normal, legitimate state WaitingForPath parks in
    /// for the few frames its own async path-job status field
    /// (<c>[followData]+0xFA8</c> &gt;= 5, per <see cref="TryGetFollowDiagnostics"/>'s own
    /// remarks) needs to catch up — and this method, called every frame, was resetting phase to 0
    /// (forcing a FRESH FindPath resubmission, which resets that SAME status field back to
    /// "pending") before the job it had JUST submitted the previous frame ever got a chance to
    /// resolve. Live-confirmed round 83: a companion's own per-frame diagnostic sampled
    /// IMMEDIATELY before this call fired showed phase reading 5 on essentially every single
    /// frame, forever — not because WaitingForPath was stuck, but because this method kept
    /// interrupting it on a permanent one-frame duty cycle, never letting its own internal
    /// watchdog (record+0x44, which WaitingForPath's own dispatch route does NOT zero — see
    /// <see cref="TryGetFollowDiagnostics"/>'s epilogue remarks) accumulate the 50 real dispatches
    /// it needs to detect a GENUINE stall and self-recover via the engine's own -1 force-reset.
    /// Narrowing this to phase == -1 only lets phase 5 run its own natural course, including its
    /// own built-in watchdog if it is ever truly stuck.
    ///
    /// Returns false when the index does not resolve. Returns true whether or not a write
    /// happened (the phase-guard is the caller-visible "did I actually intervene" signal via
    /// <paramref name="wrote"/>).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

}

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
    /// Looks up this creature's own party index using the same lookup chain the engine's
    /// per-frame follow continuation (<c>WalkUpdateLocation_QuickWalk_FollowLeader</c>,
    /// 0x0051C360) uses when <c>CreMoveState</c> (+0xA8C) reads 3 ("follow"): -1 means "not
    /// found", 0 means this creature IS the party leader (never follows itself).
    /// <see cref="PartyFollowController"/> uses this to tell an index-lookup failure apart from
    /// a deeper stall further down that same engine function. See
    /// docs/research/phantom-locomotion.md and party-reconciliation.md.
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
    /// <c>CSWSCreature::GetWalkRate</c> (0x004F1B20, <c>__thiscall</c>): returns
    /// <c>[creature+0xA08]</c> clamped to [0, 1.5], times <c>[[creature+0xA74]+0x19C]</c>, times
    /// 1000.0, clamped to 0.0 if the product exceeds 100.0. Sampled alongside
    /// <see cref="FollowDiag.RunRate"/> because the engine's follow-continuation phase dispatch
    /// (<c>WalkUpdateLocation_QuickWalk_FollowLeader</c>, 0x51C7B9-0x51C7DF) skips its whole
    /// phase-dispatch block whenever a scalar built partly from this rate reads negative or NaN.
    /// See docs/research/party-reconciliation.md.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float> GetWalkRateFn =
        (delegate* unmanaged[Thiscall]<nint, float>)0x004F1B20;

    /// <summary>CSWSCreature::GetRunRate 0x004F1BE0 — same shape as <see cref="GetWalkRateFn"/>,
    /// reading <c>[creature+0xA74]+0x198</c> instead of <c>+0x19C</c>.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float> GetRunRateFn =
        (delegate* unmanaged[Thiscall]<nint, float>)0x004F1BE0;

    /// <summary>
    /// <c>FollowMoveGateCheckFn</c> (0x004CB120, thiscall): resolves the creature's own area id
    /// (<c>creature+0x8C</c>) to a live object and returns whether that object casts as a
    /// SWCModule (vtable slot +0x28, <c>AsSWCModule</c> per <c>tools/kotor1_0_3.db</c>). It gates
    /// the only other call site in the binary (0x00511130, called from 0x0057F79E's per-action
    /// jump-table dispatcher) that reverts <c>CreMoveState</c> (<c>creature+0xA8C</c>) back to 3
    /// ("follow requested") once a companion has left the party-leader slot — the leading static
    /// candidate for the intermittent "<c>CreMoveState</c> reverts from -1 to 3 partway through a
    /// leg" stall. See <see cref="FollowDiag.GateAreaId"/>/<see cref="FollowDiag.GateFires"/> for
    /// the live diagnostic and docs/research/party-reconciliation.md for the full disassembly
    /// trace and live-repro status.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> FollowMoveGateCheckFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x004CB120;

    /// <summary>
    /// Reads the per-member follow-data record CSWParty holds at
    /// <c>this+0x24+index*0x88</c>, plus the creature's own step accumulator
    /// (<c>+0xD0</c>) and the flag at <c>+0x1F8</c> that must be non-zero for
    /// <c>WalkUpdateLocation_QuickWalk</c> (0x0051D9C0) — the caller that dispatches into
    /// 0x0051C360 on <c>CreMoveState==3</c> — to run at all. Returns false when the index itself
    /// does not resolve (<see cref="TryGetClientPartyIndex"/> already covers that case). See
    /// docs/research/party-reconciliation.md.
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

        // See GetWalkRateFn's own remarks for how these rates feed the phase-dispatch gate at
        // 0x51C7B9 (skips the whole dispatch, attempts never increments, on negative/NaN).
        diag.WalkRate = GetWalkRateFn(creature);
        diag.RunRate = GetRunRateFn(creature);

        // record+4 is the phase index the engine's 11-entry jump table (0x51CCEC) dispatches on
        // (dispatch uses phase+1); record+0x44 is a separate stall counter, incremented every
        // call regardless of phase, that force-resets phase to -1 and the counter to 0 once it
        // exceeds 0x32 (50) (0x51C95E-0x51C96B). Sampled together, these say whether the phase
        // dispatch is being reached and cycling at all. See docs/research/party-reconciliation.md.
        diag.PhaseIndex = *(int*)(record + 4);
        diag.PhaseAttempts = *(int*)(record + 0x44);

        // The "other party member" slot 0x0051C744 resolves against (this creature's own index
        // 1 <-> 2, or 2 <-> 1), read the same way, so a genuinely empty/never-populated slot
        // shows up as whatever value it actually holds rather than being inferred.
        int otherIndex = index == 1 ? 2 : index == 2 ? 1 : 0;
        nint otherRecord = party + 0x24 + (otherIndex * 0x88);
        diag.OtherSlotIndex = otherIndex;
        diag.OtherSlotStoredId = *(uint*)otherRecord;

        // record+0x24 is the "current path node" index the engine's PathWaitDest phase (phase
        // 6, dispatch off 0x0051C7E5 -> 0x00512B40) compares against the shared trail's write
        // cursor. party+0x2DC is CSWParty's embedded follow-data array (CSWPartyFollowDataArray,
        // see the ⚠ note on FollowDataArray below); +0xFA4 on it is the write cursor
        // PathWaitDest polls, written only by CSWPartyFollowDataArray::Reset/SmoothFollowPath/
        // ProcessPoint (never by PathWaitDest itself, confirming it is a consumer, not the
        // producer). Read together with WriteCursor below to see whether the trail is genuinely
        // being recorded while a stalled companion's own current-node index sits frozen. See
        // docs/research/party-reconciliation.md.
        diag.CurrentPathNode = *(int*)(record + 0x24);

        // record+0x34 (DestNode) is read-only for PathWaitDest: its own forward-only search
        // target. PathWaitDest walks the 100-entry circular trail buffer forward from
        // CurrentPathNode looking for an index that equals this field, returning "not ready" if
        // it wraps back to WriteCursor first without finding a match. The only write site in the
        // binary is inside WaitingForPath (0x00512865), which sets it to the same value just
        // written into CurrentPathNode — but two other phase handlers independently advance
        // CurrentPathNode further as real walking progresses, with nothing keeping this field in
        // sync. If CurrentPathNode is advanced past this value before PathWaitDest re-enters, the
        // forward-only search can never find it again — a disassembly-grounded explanation for
        // the phase=7 permanent stall. See docs/research/party-reconciliation.md.
        diag.DestNode = *(int*)(record + 0x34);

        // CreMoveState (creature+0xA8C) distinguishes "still nominally walking, not making
        // geometric progress" from "quietly reverted out of the walking state while the
        // follow-FSM's own phase field never noticed." record+0x6c is a running accumulator the
        // phase=3 ("Moving_To_Path", handler 0x00511B60) adds the step accumulator
        // (creature+0xD0) into on every call — since this plugin pokes creature+0xD0 every
        // frame, this field climbing is a direct, cheap proof the handler is still being
        // reached, independent of whether it ever advances CurrentPathNode. See
        // docs/research/party-reconciliation.md.
        diag.CreMoveState = *(int*)(creature + 0xA8C);
        diag.MovingToPathAccumulator = *(int*)(record + 0x6C);

        // Live test of the FollowMoveGateCheckFn hypothesis above (see that delegate's own
        // remarks). Read-only: FollowMoveGateCheckFn only resolves and downcasts, never writes;
        // ResolvePartySlot0Fn is a bare zero-argument call, safe and pure.
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

        // MoveBudget: handler 0x00511860 (phase=2) computes the squared distance between
        // PriorPoint (record+0x18) and Snapshot (record+0x5c) and compares it against a
        // threshold built from followData+0x0 (the first dword of the whole, party-wide,
        // embedded CSWPartyFollowDataArray struct) read as a float — this tick's "close enough
        // to advance" radius. A distinct, independent stall shape from the CreMoveState
        // -1->3 revert (see FollowMoveGateCheckFn's own remarks): live data showed
        // PriorPoint==Snapshot (trivially "close enough") yet phase never advanced past 2,
        // consistent with this threshold-source field reading zero/degenerate. Read-only. See
        // docs/research/party-reconciliation.md.
        diag.MoveBudget = NativeMethods.IsReadable(followData) ? *(float*)followData : float.NaN;

        // RecordedDestPoint: the recorded trail node's position, per
        // CSWPartyFollowDataArray::GetPoint's own addressing (0x00634CC0/0x00634D1E:
        // `this + index*40`, a plain 12-byte float3, no indirection) — the same pointer
        // arithmetic PathWaitDest itself uses right before its own accept/reject test (see
        // TryAcceptFinalPathNode's own remarks). Read-only, for comparing against
        // TargetPoint/PriorPoint at the exact moment PathWaitDest's geometric test rejects. See
        // docs/research/party-reconciliation.md.
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

        // See FollowMoveGateCheckFn's own remarks — the live test of the "only other
        // CreMoveState=3 writer in the binary" hypothesis.
        public uint GateAreaId;
        public bool GateFires;
        public uint PartySlot0Id;
        public bool SelfMatchesPartySlot0;
        public float MoveBudget;

        // See TryGetFollowDiagnostics's own remarks — the recorded trail node at CurrentPathNode,
        // for comparing against TargetPoint/PriorPoint at the moment PathWaitDest's geometric
        // accept/reject test rejects.
        public Vec3 RecordedDestPoint;
    }

    /// <summary>
    /// LIVE-DISPROVEN: writes the per-member follow record's cached leader position
    /// (<c>record+0x18</c>, <see cref="FollowDiag.PriorPoint"/>) directly, then calls
    /// <see cref="RecalculateFollowPoint"/> — testing whether <c>CSWParty::RecaulateFollowPoint</c>
    /// computes the follow target (<c>record+0x28</c>) from this cached field. The write itself
    /// is confirmed to stick, but the recompute that followed left <c>record+0x28</c> unchanged,
    /// so this field is not (solely) the recompute's input. NOT called from
    /// <see cref="PartyFollowController"/>'s shipped path. Kept as a ruled-out avenue; see
    /// <see cref="RecalculateFollowPointFn"/>'s own remarks for the current prime suspect,
    /// <c>CSWParty::Update</c> 0x00637050. See docs/research/party-reconciliation.md.
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
    /// Historical (no longer wired to a live method): an experiment that reset a stalled
    /// follow-FSM's phase index (<c>record+4</c>) and attempt counter (<c>record+0x44</c>) to 0
    /// whenever phase was stuck at -1, to force a fresh <c>FindPath</c> resubmission instead of
    /// leaving the engine's step FSM (<c>WalkUpdateLocation_QuickWalk_FollowLeader</c>,
    /// 0x0051C360) stuck in an unrecoverable WaitingForPath/-1 ping-pong. An earlier version of
    /// this fix also fired on phase == 5 (WaitingForPath), on the theory that it could never
    /// self-recover either; that was only true in the broken context it was first measured in —
    /// once a related step-accumulator fix let the FSM genuinely progress, phase 5 became a
    /// normal, legitimate wait state too, and resetting it on every frame kept interrupting a
    /// path job before it could resolve, live-confirmed to pin phase at 5 forever. It was
    /// narrowed to phase == -1 only, letting phase 5 run its own course (including its own
    /// watchdog). See docs/research/party-reconciliation.md.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

}

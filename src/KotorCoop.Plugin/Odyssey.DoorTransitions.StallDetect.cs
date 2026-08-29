// Odyssey.DoorTransitions.StallDetect.cs - part 6: the RUNTIME follow-stall detector's own
// engine reads and its four candidate recoveries. See FollowStallRule for the pure decision
// logic and docs/research/party-reconciliation.md round 105 for the live A/B.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary>
    /// Everything <see cref="FollowStallRule.ShouldRecover"/> needs out of a companion's
    /// follow-data record, read with plain loads and no engine calls at all — unlike
    /// <see cref="TryGetFollowDiagnostics"/>, which additionally invokes
    /// <c>GetWalkRate</c>/<c>GetRunRate</c>/<c>FollowMoveGateCheckFn</c>/<c>ResolvePartySlot0Fn</c>
    /// and is therefore only ever called on the throttled fine-log path. This one runs for every
    /// companion on every frame, so it stays a pure read.
    /// </summary>
    public struct FollowStallState
    {
        public bool Valid;
        public int Index;
        public int Phase;
        public int Attempts;
        public int PathNode;
        public int DestNode;
        public int WriteCursor;
        public Vec3 TargetPoint;
        public Vec3 PriorPoint;
        public Vec3 RecordedDestPoint;

        /// <summary>|RecordedDestPoint - TargetPoint|² — <c>ST0</c> at <c>PathWaitDest</c>'s own
        /// <c>fcompp</c> (0x00512BF2).</summary>
        public float DistSqToTarget;

        /// <summary>|RecordedDestPoint - PriorPoint|² — <c>ST1</c> at the same compare.</summary>
        public float DistSqToPrior;

        /// <summary>True when the party member count at <c>party+0x00</c> had to be restored
        /// before <see cref="GetPartyIndexFn"/> would find this companion at all — see
        /// <see cref="TryResolveFollowRecord"/>'s own remarks. Surfaced so a live pass can tell
        /// "no record" apart from "a record the default-ON <c>party_together_fix</c> clamp was
        /// hiding."</summary>
        public bool CountWasRestored;

        /// <summary>True when this pass actually WROTE <c>party+0x00</c>, whether or not the
        /// lookup then succeeded (a write that did not help is reverted immediately, but it still
        /// happened). This — not <see cref="CountWasRestored"/> — is what
        /// <c>PartyFollowController</c>'s per-slot latch keys on: an engine field write must be
        /// at most once per slot per module, and latching only on SUCCESS left a failing restore
        /// re-arming itself every frame, which is how it came to be written 500x/s across a
        /// module load. See docs/research/transition-crashes.md's host-warp-crash section.</summary>
        public bool CountRestoreWritten;
    }

    /// <summary>
    /// Resolves a companion's follow-data record (<c>party+0x24 + index*0x88</c>), applying the
    /// SAME bounded member-count restore round 101 added to
    /// <see cref="TryReleaseFollowPhaseOnUnmirror"/>.
    ///
    /// That round's own disassembly of <c>CSWParty::FindMemberIndexById</c> (0x006349C0) is the
    /// reason this exists: the search is bounded by <c>party+0x00</c>, the exact field the
    /// default-ON, client-only <c>party_together_fix</c> clamps DOWN to the companions-only count
    /// for as long as this client mirrors anything — and the clamp only ever writes DOWN, so its
    /// last (low) value keeps standing after mirroring stops. Every OTHER follow function in this
    /// family (<see cref="TryAdvanceStalledPathDestination"/>,
    /// <see cref="TryDriveFollowLeaderStep"/>, <see cref="TryGetFollowDiagnostics"/>, ...) does the
    /// plain lookup and silently returns on <c>index &lt;= 0</c>, so on a client that has just
    /// stopped mirroring they can ALL be no-ops for the very companion they exist to fix. The
    /// restore is arithmetic, not a guess: <c>CSWPartyTable</c>'s own companions-only count (a
    /// different structure the clamp never touches) plus one for the PC, exactly what
    /// <see cref="GetClientPartyMemberCount"/> documents CSWParty's count should read.
    ///
    /// ⚠ 2026-08-29 — round 101's "the restored value is deliberately left standing, because
    /// <c>SyncClientPartyTogetherState</c> re-clamps it on its own very next pass" was WRONG, and
    /// deterministically fatal on a host. That re-clamp is CLIENT-ONLY
    /// (<c>PartyRosterController.SyncClientPartyTogetherState</c> returns early unless the
    /// host-roster valid bit is set, which is never set on a host), and this method's own gating
    /// caller reaches here on a host every module load — so on a host a speculative raise was
    /// left standing with nothing in the process able to take it back. Combined with the
    /// arithmetic being blind to whether the members it was counting had been populated yet, that
    /// is the host's deterministic <c>CSWGuiInGameEquip::UpdatePortraits+0x48E</c> crash on any
    /// relocating warp (docs/research/transition-crashes.md; shared-inventory-v2.md §15.6). Two
    /// changes close it, both here:
    ///
    /// <list type="number">
    /// <item>the raise is bounded by how many members actually RESOLVE right now
    /// (<see cref="PartyCountRestoreRule"/> + <see cref="CountResolvableClientPartyMembers"/>),
    /// never by arithmetic alone — the count is an array bound and may never name an entry a
    /// consumer would dereference NULL;</item>
    /// <item>a raise that did not make the lookup succeed is REVERTED on the spot rather than
    /// left behind, so no speculative value can ever outlive the frame that guessed it.</item>
    /// </list>
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    private static bool TryResolveFollowRecord(
        uint companionServerObjectId,
        bool allowCountRestore,
        out nint party,
        out nint record,
        out int index,
        out bool countWasRestored,
        out bool countRestoreWritten)
    {
        party = 0;
        record = 0;
        index = -1;
        countWasRestored = false;
        countRestoreWritten = false;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            if (allowCountRestore && TryGetPartyTableMemberCount(out int tableCount) && tableCount is >= 0 and <= 16)
            {
                int truePopulation = tableCount + 1; // + the PC, matching CSWParty's own layout
                int* countPtr = (int*)party;
                int before = *countPtr;
                int restoreTo = PartyCountRestoreRule.SafeRestoreCount(
                    before,
                    truePopulation,
                    CountResolvableClientPartyMembers(client, party),
                    SwPartyMemberCapacity);

                if (restoreTo > 0)
                {
                    *countPtr = restoreTo;
                    countRestoreWritten = true;
                    index = GetPartyIndexFn(party, clientObjectId);
                    countWasRestored = index > 0;

                    if (!countWasRestored)
                    {
                        // It did not buy the lookup anything, so it is a guess — and a standing
                        // guess about this field is the host's UpdatePortraits crash. Put it back.
                        *countPtr = before;
                    }
                }
            }

            if (index <= 0)
            {
                return false;
            }
        }

        record = party + 0x24 + (index * 0x88);
        return true;
    }

    /// <summary>
    /// Reads the wedge signature <see cref="FollowStallRule"/> decides on. Returns false when the
    /// companion has no resolvable follow record (no client world, no CSWParty, or genuinely not a
    /// party member) — <paramref name="state"/> is then <c>default</c>, <c>Valid == false</c>.
    ///
    /// The two squared distances are computed here, in managed code, exactly the way
    /// <c>PathWaitDest</c>'s own FPU sequence does (0x00512B86-0x00512C03, transcribed in
    /// <see cref="PartyFollowController"/>'s fine-log): both against the RECORDED trail node at
    /// <c>CurrentPathNode</c> (<c>CSWPartyFollowDataArray::GetPoint</c>'s own <c>this +
    /// index*40</c> addressing, 0x00634CC0), never against the creature's live position.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryGetFollowStallState(
        uint companionServerObjectId, bool allowCountRestore, out FollowStallState state)
    {
        state = default;

        if (!TryResolveFollowRecord(
                companionServerObjectId,
                allowCountRestore,
                out nint party,
                out nint record,
                out int index,
                out bool restored,
                out bool restoreWritten))
        {
            state.CountRestoreWritten = restoreWritten;
            return false;
        }

        state.Index = index;
        state.CountWasRestored = restored;
        state.CountRestoreWritten = restoreWritten;
        state.Phase = *(int*)(record + 4);
        state.Attempts = *(int*)(record + 0x44);
        state.PathNode = *(int*)(record + 0x24);
        state.DestNode = *(int*)(record + 0x34);
        state.TargetPoint = *(Vec3*)(record + 0x28);
        state.PriorPoint = *(Vec3*)(record + 0x18);

        // CSWPartyFollowDataArray is EMBEDDED inline at party+0x2DC — see
        // TryGetFollowDiagnostics's own ⚠ note for the live crash that settled that, and why this
        // is never dereferenced as a pointer.
        nint followData = party + 0x2DC;
        state.WriteCursor = NativeMethods.IsReadable(followData + 0xFA4) ? *(int*)(followData + 0xFA4) : -1;

        if (state.PathNode is >= 0 and < 100 && NativeMethods.IsReadable(followData + (state.PathNode * 40) + 8))
        {
            state.RecordedDestPoint = *(Vec3*)(followData + (state.PathNode * 40));

            float dtx = state.RecordedDestPoint.X - state.TargetPoint.X;
            float dty = state.RecordedDestPoint.Y - state.TargetPoint.Y;
            float dtz = state.RecordedDestPoint.Z - state.TargetPoint.Z;
            state.DistSqToTarget = (dtx * dtx) + (dty * dty) + (dtz * dtz);

            float dpx = state.RecordedDestPoint.X - state.PriorPoint.X;
            float dpy = state.RecordedDestPoint.Y - state.PriorPoint.Y;
            float dpz = state.RecordedDestPoint.Z - state.PriorPoint.Z;
            state.DistSqToPrior = (dpx * dpx) + (dpy * dpy) + (dpz * dpz);
        }
        else
        {
            // An unreadable/out-of-range node index cannot be reasoned about — leave both
            // distances at 0, which reads as CannotAccept (a tie) but is combined with the rest of
            // the wedge shape before anything is written, and never on its own.
            state.RecordedDestPoint = default;
        }

        state.Valid = true;
        return true;
    }

    /// <summary>
    /// Applies one of <see cref="FollowStallRule"/>'s four candidate recoveries to a companion
    /// whose follow record <see cref="FollowStallRule.ShouldRecover"/> has already judged wedged.
    /// The caller owns the decision; this owns the writes. Each method is deliberately a separate,
    /// independently-selectable branch (<c>party_follow_stall_recovery_method</c>) so a live A/B
    /// can isolate one at a time, this project's own standing rule for engine writes.
    ///
    /// <list type="number">
    /// <item><b>1, <c>phasereset</c></b> — round 98's own step 6, finally acted on. Writes the
    /// engine's OWN watchdog sentinel <c>-1</c> into <c>record+4</c> (0x0051C964's exact value),
    /// zeroes <c>attempts</c> (<c>record+0x44</c>) to match that same engine write, and — the part
    /// round 98 explicitly left untried — re-points <c>CurrentPathNode</c> (<c>record+0x24</c>)
    /// and <c>DestNode</c> (<c>record+0x34</c>) at the trail's live write cursor, so the restart
    /// is not resumed against indices into an already-overwritten section of the 100-slot circular
    /// buffer. The party-wide write cursor itself is NOT moved: it is shared by every member, and
    /// nothing about one companion's wedge justifies rewriting the other members' view of the
    /// trail. <see cref="TryResetStuckFollowPhase"/> (default ON, already in the per-frame path)
    /// picks the sentinel up on the very next frame and turns it into a clean phase-0
    /// <c>FindPath</c> — the recovery round 84 already live-confirmed works when the engine itself
    /// writes it.</item>
    /// <item><b>2, <c>followleader</c></b> — <see cref="QueuePartyFollowLeader"/>, issued
    /// unconditionally. <see cref="PartyFollowController"/> already owns this call but only
    /// reaches it past three gates the wedge never opens (head type not <c>0x3D</c>, not a busy
    /// head, leader further than 3 units); this is the same VM dispatch with those gates
    /// bypassed.</item>
    /// <item><b>3, <c>positionparty</c></b> — <see cref="GatherParty"/>, i.e. the engine's own
    /// <c>CSWPartyTable::PositionParty</c> (0x00565B00), which calls
    /// <c>CSWParty::ResetAllFollowData</c> (0x00637890) on the way in and re-snaps every member
    /// into formation. The heaviest of the four by far: it MOVES the companion rather than letting
    /// it walk, and it also clears <c>pt_solomode</c> and unstealths the party. Last resort.</item>
    /// <item><b>4, <c>priortie</c></b> — the narrowest possible answer to round 97's own closing
    /// question ("whatever fixes this needs to address <c>PathWaitDest</c>'s accept condition
    /// directly"). The wedge is a STRICT <c>&lt;</c> against a tie: <c>Prior == Target</c>, so
    /// both squared distances read exactly 0. This writes <c>PriorPoint</c> (<c>record+0x18</c>)
    /// = the companion's own live position and sets the "target changed" dirty flag
    /// (<c>record+0x68</c>) the same way <see cref="TryDriveFollowTarget"/> does, which makes the
    /// recorded node strictly closer to <c>Target</c> than to <c>Prior</c> whenever the companion
    /// is genuinely short of it — the engine's own unmodified test then accepts on its own, and
    /// commits its own phase. One field plus a flag; no phase forced, no body moved.</item>
    /// <item><b>5, <c>destnode</c></b> — the union of two previously-shipped-but-OFF candidates
    /// (<c>party_follow_advance_destnode_phase6</c> and <c>party_follow_extend_final_node</c>),
    /// now driven by a real wedge detector with anti-spin instead of firing unconditionally on
    /// every frame the shape happens to hold — which is precisely why their own live A/Bs came
    /// back "inconclusive". It never touches the phase field and never manufactures a geometric
    /// verdict: it makes <c>DestNode</c> DIFFER from <c>CurrentPathNode</c>, so the wedged
    /// handler's own forward search walks forward and takes its OTHER, UNCONDITIONAL accept
    /// branch, and the unmodified engine handler decides and commits from there. With real
    /// unconsumed trail ahead (<c>gap &gt;= 2</c>) it repoints <c>DestNode</c> at
    /// <c>writeCursor - 1</c>; with the trail exhausted (<c>gap == 1</c>) it appends exactly one
    /// node at the leader's own position, advances the shared cursor, and points <c>DestNode</c>
    /// at the new node.</item>
    /// <item><b>6, <c>priorpush</c></b> — round 105's own answer to round 97's closing question
    /// ("whatever fixes this needs to address <c>PathWaitDest</c>'s accept condition directly").
    /// Method 4 established live that moving <c>PriorPoint</c> alone genuinely restarts real
    /// walking, but it converges to the point where <c>|node - me| == |node - Target|</c> and
    /// re-wedges on that tie, because the STRICT compare rejects equality. This places
    /// <c>PriorPoint</c> at <c>node + PriorPushMultiple * (node - Target)</c> — mirrored through
    /// the node, directly AWAY from the target — so <c>|node - Prior|</c> is a fixed multiple of
    /// <c>|node - Target|</c> and the strict inequality holds by construction rather than by
    /// luck of geometry. When node and target coincide (the degenerate <c>0 vs 0</c> tie round 97
    /// captured) the same push is built from the node-to-companion vector instead, which is
    /// non-zero exactly when the companion has not arrived. Still one field plus the dirty flag:
    /// no phase forced, no body moved, no index rewritten — the unmodified engine handler
    /// performs its own accept and commits its own phase.</item>
    /// </list>
    ///
    /// Returns false when the record does not resolve, or when
    /// <paramref name="method"/> is not one <see cref="FollowStallRule.IsKnownMethod"/> knows.
    /// <paramref name="wrote"/> reports whether the recovery actually committed anything.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryRecoverFollowStall(
        uint companionServerObjectId,
        int method,
        Vec3 leaderPos,
        bool allowCountRestore,
        out bool wrote,
        out string detail)
    {
        wrote = false;
        detail = string.Empty;

        if (!FollowStallRule.IsKnownMethod(method))
        {
            detail = $"unknown method {method}";
            return false;
        }

        if (!TryResolveFollowRecord(
                companionServerObjectId,
                allowCountRestore,
                out nint party,
                out nint record,
                out int index,
                out bool restored,
                out bool _))
        {
            detail = "no follow record (GetIndex refused"
                + (allowCountRestore ? ", even after restoring CSWParty's own member count)" : ")");
            return false;
        }

        string restoredNote = restored ? " (member count restored first)" : string.Empty;

        switch (method)
        {
            case FollowStallRule.MethodPhaseReset:
            {
                int phaseBefore = *(int*)(record + 4);
                int pathBefore = *(int*)(record + 0x24);
                int destBefore = *(int*)(record + 0x34);

                nint followData = party + 0x2DC;
                int writeCursor = NativeMethods.IsReadable(followData + 0xFA4)
                    ? *(int*)(followData + 0xFA4)
                    : -1;

                *(int*)(record + 4) = FollowPhaseReleaseRule.EngineWatchdogResetPhase;
                *(int*)(record + 0x44) = 0;

                if (writeCursor is >= 0 and < 100)
                {
                    *(int*)(record + 0x24) = writeCursor;
                    *(int*)(record + 0x34) = writeCursor;
                }

                wrote = true;
                detail = $"phasereset phase {phaseBefore} -> "
                    + $"{FollowPhaseReleaseRule.EngineWatchdogResetPhase}, attempts -> 0, "
                    + $"pathNode {pathBefore} -> {writeCursor}, destNode {destBefore} -> {writeCursor}"
                    + restoredNote;
                return true;
            }

            case FollowStallRule.MethodFollowLeader:
            {
                bool ok = QueuePartyFollowLeader(companionServerObjectId, out int failure);
                wrote = ok;
                detail = ok
                    ? $"followleader ActionFollowLeader dispatched (index {index}){restoredNote}"
                    : $"followleader refused by the VM dispatch (failure {failure}){restoredNote}";
                return ok;
            }

            case FollowStallRule.MethodPositionParty:
            {
                bool ok = GatherParty(out int members, out bool skipFlagWasSet);
                wrote = ok;
                detail = ok
                    ? $"positionparty PositionParty ran for {members} member(s) "
                        + $"(skipFlagWasSet={skipFlagWasSet}){restoredNote}"
                    : $"positionparty refused - PositionParty's own preconditions not met{restoredNote}";
                return ok;
            }

            case FollowStallRule.MethodBreakPriorTie:
            {
                nint creature = ResolveServerObject(companionServerObjectId);
                if (creature == 0)
                {
                    detail = "priortie refused - companion body does not resolve here";
                    return false;
                }

                Vec3 priorBefore = *(Vec3*)(record + 0x18);
                Vec3 self = GetPosition(creature);
                *(Vec3*)(record + 0x18) = self;
                *(int*)(record + 0x68) = 1;

                wrote = true;
                detail = $"priortie prior ({priorBefore.X:F2},{priorBefore.Y:F2},{priorBefore.Z:F2}) -> "
                    + $"({self.X:F2},{self.Y:F2},{self.Z:F2}), snapshotPending -> 1" + restoredNote;
                return true;
            }

            case FollowStallRule.MethodPushPriorPastNode:
            {
                nint creature = ResolveServerObject(companionServerObjectId);
                if (creature == 0)
                {
                    detail = "priorpush refused - companion body does not resolve here";
                    return false;
                }

                int pathNode = *(int*)(record + 0x24);
                nint followData = party + 0x2DC;
                if (pathNode is < 0 or >= 100 || !NativeMethods.IsReadable(followData + (pathNode * 40) + 8))
                {
                    detail = $"priorpush refused - trail node {pathNode} is not readable";
                    return false;
                }

                Vec3 node = *(Vec3*)(followData + (pathNode * 40));
                Vec3 target = *(Vec3*)(record + 0x28);
                Vec3 priorBefore = *(Vec3*)(record + 0x18);

                float ax = node.X - target.X;
                float ay = node.Y - target.Y;
                float az = node.Z - target.Z;
                string basis = "node-to-target";

                if ((ax * ax) + (ay * ay) + (az * az) < FollowStallRule.DirectionEpsilonSquared)
                {
                    // The node sits exactly on the target — round 97's own 0-vs-0 capture. Mirror
                    // through the node away from the COMPANION instead; that vector is non-zero
                    // exactly when the companion has not arrived, which is the only case this
                    // recovery is ever asked to act on.
                    Vec3 self = GetPosition(creature);
                    ax = node.X - self.X;
                    ay = node.Y - self.Y;
                    az = node.Z - self.Z;
                    basis = "node-to-companion";
                }

                if ((ax * ax) + (ay * ay) + (az * az) < FollowStallRule.DirectionEpsilonSquared)
                {
                    detail = "priorpush refused - companion, trail node and target all coincide "
                        + "(genuinely arrived; nothing to recover)";
                    return false;
                }

                Vec3 pushed = default;
                pushed.X = node.X + (FollowStallRule.PriorPushMultiple * ax);
                pushed.Y = node.Y + (FollowStallRule.PriorPushMultiple * ay);
                pushed.Z = node.Z + (FollowStallRule.PriorPushMultiple * az);

                *(Vec3*)(record + 0x18) = pushed;
                *(int*)(record + 0x68) = 1;

                wrote = true;
                detail = $"priorpush prior ({priorBefore.X:F2},{priorBefore.Y:F2}) -> "
                    + $"({pushed.X:F2},{pushed.Y:F2}) pushed {FollowStallRule.PriorPushMultiple:F0}x "
                    + $"past node {pathNode} ({node.X:F2},{node.Y:F2}) along {basis}, "
                    + "snapshotPending -> 1" + restoredNote;
                return true;
            }

            case FollowStallRule.MethodAdvanceDestNode:
            {
                int destBefore = *(int*)(record + 0x34);
                int pathNode = *(int*)(record + 0x24);

                nint followData = party + 0x2DC;
                if (!NativeMethods.IsReadable(followData + 0xFA4))
                {
                    detail = "destnode refused - the trail's write cursor is not readable";
                    return false;
                }

                int writeCursor = *(int*)(followData + 0xFA4);
                if (pathNode is < 0 or >= 100 || writeCursor is < 0 or >= 100)
                {
                    detail = $"destnode refused - pathNode {pathNode} / writeCursor {writeCursor} "
                        + "outside the 100-slot trail";
                    return false;
                }

                int gap = writeCursor - pathNode;
                if (gap < 0)
                {
                    gap += 100;
                }

                if (gap is >= 2 and <= 90)
                {
                    // Round 83's own live-disproven pitfall: a DestNode set to the SAME value as
                    // the write cursor is structurally unreachable, because the search's "give
                    // up" exit tests index == WriteCursor immediately AFTER the increment, one
                    // iteration before it could re-test that index against DestNode. WriteCursor-1
                    // is always found first.
                    int newDest = FinalNodeGeometryRule.WrapTrailIndex(writeCursor - 1 + 100);
                    *(int*)(record + 0x34) = newDest;
                    wrote = true;
                    detail = $"destnode destNode {destBefore} -> {newDest} "
                        + $"(pathNode {pathNode}, writeCursor {writeCursor}, gap {gap} - the "
                        + "search now reaches its unconditional accept branch)" + restoredNote;
                    return true;
                }

                if (gap == 1)
                {
                    // Nothing further is recorded to walk toward, so manufacture exactly one
                    // reachable node at the leader's own position and point DestNode at it. Order
                    // matters: the cursor must advance BEFORE DestNode is written, or DestNode
                    // lands on the unreachable equal-to-cursor value above.
                    nint newNodeAddress = followData + (writeCursor * 40);
                    if (!NativeMethods.IsReadable(newNodeAddress + 8))
                    {
                        detail = $"destnode refused - trail slot {writeCursor} is not writable";
                        return false;
                    }

                    *(Vec3*)newNodeAddress = leaderPos;
                    int newWriteCursor = FinalNodeGeometryRule.WrapTrailIndex(writeCursor + 1);
                    *(int*)(followData + 0xFA4) = newWriteCursor;
                    *(int*)(record + 0x34) = writeCursor;

                    wrote = true;
                    detail = $"destnode appended node {writeCursor} at "
                        + $"({leaderPos.X:F2},{leaderPos.Y:F2},{leaderPos.Z:F2}), writeCursor "
                        + $"{writeCursor} -> {newWriteCursor}, destNode {destBefore} -> "
                        + $"{writeCursor} (pathNode {pathNode}, gap was 1)" + restoredNote;
                    return true;
                }

                detail = $"destnode refused - gap {gap} is outside the sane 1..90 range "
                    + $"(pathNode {pathNode}, writeCursor {writeCursor})";
                return false;
            }

            default:
                detail = $"unknown method {method}";
                return false;
        }
    }
}

// Odyssey.DoorTransitions.FinalNode.cs - part 4: accepting/refreshing/extending the
// final path node a follower walks to, and pumping the party update.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    public static bool TryRefreshFinalNodeTarget(uint companionServerObjectId, Vec3 leaderPos, out bool wrote)
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
        int pathNode = *(int*)(record + 0x24);
        int destNode = *(int*)(record + 0x34);

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

        // See FinalNodeGeometryRule's own remarks — the shape/epsilon logic is pulled out to a
        // pure, unit-tested class shared with TryAcceptFinalPathNode's own shape constant.
        if (!FinalNodeGeometryRule.IsTerminalStallShape(phase, pathNode, destNode, gap))
        {
            return true;
        }

        Vec3 existingTarget = *(Vec3*)(record + 0x28);
        if (!FinalNodeGeometryRule.ShouldRefresh(existingTarget.X, existingTarget.Y, leaderPos.X, leaderPos.Y))
        {
            return true; // already fresh, or a write here would be degenerate
        }

        Vec3 oldTarget = existingTarget;
        *(Vec3*)(record + 0x28) = leaderPos;
        *(Vec3*)(record + 0x18) = oldTarget;
        *(int*)(record + 0x68) = 1;
        wrote = true;
        return true;
    }

    /// <summary>
    /// FIX CANDIDATE 1: at the terminal stall shape (<c>gap==1</c>, <c>DestNode==CurrentPathNode</c>),
    /// <c>PathWaitDest</c>'s geometric accept/reject test degenerates because the recorded trail
    /// node has converged onto <c>PriorPoint</c> while <c>TargetPoint</c> keeps tracking the live,
    /// moving leader, so the test effectively never passes. This sidesteps the test entirely: it
    /// appends a new trail node at the current write cursor (party-wide, shared
    /// <c>CSWPartyFollowDataArray</c> embedded at <c>party+0x2DC</c>), advances the write cursor,
    /// and only then points <c>DestNode</c> at the new node — making <c>DestNode != CurrentPathNode</c>,
    /// which takes <c>PathWaitDest</c>'s OTHER, unconditional accept branch instead of the
    /// geometric one, the same mechanism <see cref="TryAdvanceStalledPathDestination"/> already
    /// uses for already-recorded nodes. Order matters: the write cursor must advance before
    /// <c>DestNode</c> is set, or <c>DestNode</c> can point at a not-yet-reachable slot (a
    /// previously live-disproven pitfall — see <see cref="TryAdvanceStalledPathDestination"/>'s
    /// own remarks).
    ///
    /// Gated on <see cref="FinalNodeGeometryRule.IsExtendableTerminalStallShape"/> (phase 6 OR
    /// 7 — a live A/B found the identical structural bug one phase earlier, in a handler
    /// byte-for-byte structurally identical to <c>PathWaitDest</c>, than the narrower
    /// phase-7-only <see cref="FinalNodeGeometryRule.IsTerminalStallShape"/> the two sibling fix
    /// candidates use). Self-limiting by shape, not a frame counter: once written,
    /// <c>DestNode</c> no longer equals <c>CurrentPathNode</c>, so the gate stops matching until
    /// the engine's own <c>WaitingForPath</c> handler genuinely re-arms it.
    ///
    /// Kill switch (default OFF pending live A/B): <c>party_follow_extend_final_node=1</c>.
    /// <paramref name="logWrites"/> (caller's cached <c>party_follow_finelog</c> flag; this
    /// function reads no config itself) logs the node index/coordinates and before/after write
    /// cursor/<c>DestNode</c> when a write happens. See docs/research/party-reconciliation.md for
    /// the full disassembly trace and live-test status.
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether
    /// the synthetic node was actually written this call (false when not in the terminal shape,
    /// or when the write-cursor field is not currently readable).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryExtendFinalNodeTrail(
        uint companionServerObjectId, Vec3 leaderPos, bool logWrites, out bool wrote)
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
        int pathNode = *(int*)(record + 0x24);
        int destNode = *(int*)(record + 0x34);

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

        // See FinalNodeGeometryRule.IsExtendableTerminalStallShape's own remarks — deliberately
        // NOT the narrower phase-7-only IsTerminalStallShape the two sibling fix candidates share:
        // this function's live A/B found the identical structural bug one phase earlier (phase 6,
        // handler 0x00512A60), and this fix's own mechanism (never writes the phase field) applies
        // to both unmodified.
        if (!FinalNodeGeometryRule.IsExtendableTerminalStallShape(phase, pathNode, destNode, gap))
        {
            return true;
        }

        // The new node's own index: the current write cursor is, by definition, the next slot the
        // engine's own recorder has not yet filled.
        int newNodeIndex = writeCursor;
        nint newNodeAddress = followData + (newNodeIndex * 40);
        if (!NativeMethods.IsReadable(newNodeAddress + 8))
        {
            return true; // out-of-range/unmapped slot — should not happen for a valid 0..99
                          // index into a live followData array, but never write blind.
        }

        *(Vec3*)newNodeAddress = leaderPos;

        // Advance the shared write cursor BEFORE writing DestNode — see this function's own
        // remarks for why the order matters (round 83's own live-disproven "DestNode ==
        // stale-writeCursor" pitfall).
        int newWriteCursor = FinalNodeGeometryRule.WrapTrailIndex(writeCursor + 1);
        *(int*)(followData + 0xFA4) = newWriteCursor;
        *(int*)(record + 0x34) = newNodeIndex;

        wrote = true;

        if (logWrites)
        {
            PluginHost.Log(
                $"party follow extend: 0x{companionServerObjectId:X} fired "
                + $"phase={phase} pathNode={pathNode} gap={gap} "
                + $"newNode={newNodeIndex} at ({leaderPos.X:F2},{leaderPos.Y:F2},{leaderPos.Z:F2}) "
                + $"writeCursor {writeCursor} -> {newWriteCursor} destNode {destNode} -> {newNodeIndex}");
        }

        return true;
    }

    /// <summary>
    /// Writes <c>record+0x28</c> (target) and <c>record+0x18</c> (cached leader position, see
    /// <see cref="TryPokeFollowPriorPosition"/>) together, and sets <c>record+0x68</c>
    /// (<see cref="FollowDiag.SnapshotPendingFlag"/>) to 1 — the flag
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> (0x0051C360) reads as the "target
    /// changed" dirty event that makes it re-snapshot the creature's position
    /// (<c>creature+0x90</c> into <c>record+0x5c</c>) and compute a fresh step; a raw field
    /// write alone never sets it, so nothing downstream notices. Calling
    /// <c>CSWParty::Update</c>/<c>RecaulateFollowPoint</c> (see <see cref="PartyUpdateFn"/>'s and
    /// <see cref="RecalculateFollowPointFn"/>'s own remarks) does not reach these per-member
    /// fields either. Only fires when the target has moved more than a small epsilon from its
    /// current value, so it does not fight the function's own snapshot/step bookkeeping once the
    /// companion is caught up and walking normally. Returns false when the index does not
    /// resolve; returns true (having written nothing) when the target is already within epsilon
    /// of <paramref name="leaderPos"/>. See docs/research/party-reconciliation.md.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryDriveFollowTarget(uint companionServerObjectId, Vec3 leaderPos)
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
        Vec3 existingTarget = *(Vec3*)(record + 0x28);
        float dx = existingTarget.X - leaderPos.X;
        float dy = existingTarget.Y - leaderPos.Y;

        // PathWaitDest's own accept/reject test (0x512B98-0x512C03, phase=7 handler) compares
        // (node - Prior) and (node - Target) — the same Prior/Target fields this function
        // writes. Writing Prior <- the OLD target and Target <- the new leaderPos (rather than
        // collapsing both to the identical new value) guarantees Prior != Target on every write
        // that happens, so PathWaitDest is never handed a degenerate, zero-length segment with
        // no direction to measure "past" against. Throttled to only fire past a ~10cm threshold
        // (not every frame). See docs/research/party-reconciliation.md.
        if ((dx * dx) + (dy * dy) > 0.01f) // ~10cm — "meaningfully different," not float noise
        {
            Vec3 oldTarget = existingTarget;
            *(Vec3*)(record + 0x28) = leaderPos;
            *(Vec3*)(record + 0x18) = oldTarget;
            *(int*)(record + 0x68) = 1;
        }

        return true;
    }

    /// <summary>
    /// <c>CSWParty::Update</c> (0x00637050, <c>__thiscall</c>, one stack arg: a delta-time
    /// float). Its single caller in the binary (0x0060339A, inside
    /// <c>CClientExoAppInternal</c>'s per-frame update dispatch) computes
    /// <c>deltaTime * &lt;global time-scale multiplier at 0x7439BC&gt;</c> and passes that. This
    /// function accumulates elapsed time at <c>this+0x2BC</c>, refreshes a PARTY-LEVEL (not
    /// per-member) leader reference position at <c>this+0x2B0..0x2B8</c> from the leader's live
    /// position once it has moved past a threshold, and — if the party's furthest member has
    /// drifted far enough and the path is not already resetting — calls
    /// <c>CSWParty::RecaulateFollowPoint()</c> itself. Calling <c>RecaulateFollowPoint</c>
    /// directly without ever calling this function first is a no-op: the reference position it
    /// reads is only ever refreshed here, and this function's one real caller lives inside a
    /// per-frame dispatch this plugin's own drive loop does not reach on its own (see
    /// <see cref="PumpPartyUpdate"/>).
    ///
    /// The caller's own gate (<c>esi+0x18</c> non-null and <c>[esi+0x18+0x48]</c> non-zero) is
    /// NOT reproduced here — <see cref="PumpPartyUpdate"/> calls this unconditionally, once per
    /// rendered frame, guarded only by <see cref="PartyFollowController"/>'s own preconditions
    /// (spawned and driving, not mid-conversation, roster not frozen, no peer party-presence
    /// channel active). See docs/research/party-reconciliation.md.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, void> PartyUpdateFn =
        (delegate* unmanaged[Thiscall]<nint, float, void>)0x00637050;

    /// <summary>
    /// Calls the engine's own per-frame <c>CSWParty::Update</c> — see
    /// <see cref="PartyUpdateFn"/>'s own remarks for what this drives and why round 72's fix
    /// attempts had no effect without it. Returns false when there is no client/party object to
    /// call it on.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PumpPartyUpdate(float deltaTimeSeconds)
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

        PartyUpdateFn(party, deltaTimeSeconds);
        return true;
    }

}

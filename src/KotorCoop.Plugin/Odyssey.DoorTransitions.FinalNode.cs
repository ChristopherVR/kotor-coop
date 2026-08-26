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
    /// FIX CANDIDATE 1 (2026-08-24 follow-up round, <c>docs/research/party-reconciliation.md</c>'s
    /// "the <c>fcompp</c> at 0x512BF2, read without a debugger" entry): that round's own
    /// in-process diagnostic settled — directly observed, not inferred — exactly why
    /// <c>PathWaitDest</c>'s geometric accept/reject test never resolves at the terminal stall
    /// shape <see cref="TryAcceptFinalPathNode"/>/<see cref="TryRefreshFinalNodeTarget"/> both
    /// detect: <c>RecordedDestPoint</c> (the recorded trail node's own FIXED position, looked up at
    /// <c>CurrentPathNode</c>'s index) has converged onto <c>PriorPoint</c>, while <c>TargetPoint</c>
    /// tracks the LIVE, continuously-moving leader — the test (strictly <c>|node-Target|² &lt;
    /// |node-Prior|²</c>) then reduces to requiring the leader to stand almost exactly on that
    /// fixed node, a race ordinary movement essentially never wins. Both prior fix candidates
    /// worked around this test's OUTPUT (<see cref="TryAcceptFinalPathNode"/>, forcing phase=3
    /// directly — live-disproven, round 88) or its INPUTS (<see cref="TryRefreshFinalNodeTarget"/>,
    /// refreshing Target/Prior — live-measured insufficient, round 90, because Target was already
    /// bit-identical to the live leader and the test still never passed). This instead gives the
    /// test a genuinely DIFFERENT node to evaluate, the same shape that already lets every
    /// INTERMEDIATE trail node pass (round 89's own live data: mid-path samples showed
    /// <c>pathWaitDestAccept=True</c> because there is a "next" node ahead with its own distinct
    /// recorded position for the test to discriminate against — see that round's doc entry).
    ///
    /// A closer disassembly of the search this round's own PathWaitDest re-read
    /// (<c>tools/disasm.py 0x00512B40 220</c>, cross-checked instruction-for-instruction against
    /// round 89's already-verified fcompp derivation) actually finds the fix operates one step
    /// EARLIER than "let the geometric test pass": the search's very first comparison
    /// (0x00512B60: <c>cmp eax,ebx</c>, <c>eax</c>=<c>CurrentPathNode</c>, <c>ebx</c>=<c>DestNode</c>)
    /// is what selects whether the geometric test (0x512B86 onward) runs AT ALL — it only runs when
    /// <c>DestNode == CurrentPathNode</c> is true on that FIRST comparison, before any increment.
    /// Once this function makes <c>DestNode</c> genuinely different from <c>CurrentPathNode</c> (by
    /// appending a new node and pointing <c>DestNode</c> at it), the search instead increments past
    /// the degenerate first comparison and, on reaching the new <c>DestNode</c>, falls into
    /// <c>PathWaitDest</c>'s OTHER accept branch (0x512B7E: <c>cmp ebx,edx</c> — the ORIGINAL
    /// <c>CurrentPathNode</c>, unchanged by the search's own local increment — <c>jne 0x512c09</c>,
    /// the SAME unconditional <c>phase=3</c> write <see cref="TryAcceptFinalPathNode"/> forces
    /// directly) WITHOUT the fragile geometric comparison running at all — exactly the mechanism
    /// round 83's own, already-shipped <see cref="TryAdvanceStalledPathDestination"/> already uses
    /// for a genuinely different reason (catching a companion up to ALREADY-recorded trail nodes
    /// the engine's own <c>WaitingForPath</c> handler re-collapsed <c>DestNode</c> away from).
    /// <see cref="TryAdvanceStalledPathDestination"/> cannot reach the terminal shape itself — its
    /// own remarks explain <c>DestNode == WriteCursor</c> is structurally UNREACHABLE by the same
    /// search (the "give up" exit at <c>eax==WriteCursor</c> fires one iteration before a re-test
    /// against a <c>DestNode</c> set to that exact value ever could), and at the terminal shape
    /// (<c>gap==1</c>) <c>WriteCursor</c> is the only in-range value <c>DestNode</c> could be set
    /// to. This function's whole contribution is manufacturing a NEW node so the write cursor
    /// itself advances past that unreachable value, turning the terminal shape into exactly the
    /// <c>gap&gt;=2</c>, already-working shape <see cref="TryAdvanceStalledPathDestination"/>
    /// handles — rather than requiring the geometric test to somehow pass, which is a materially
    /// different (and, per the above disassembly, unnecessary) mechanism than what candidate 1's
    /// own name might suggest.
    ///
    /// <c>followData</c> (<c>CSWPartyFollowDataArray</c>, embedded at <c>party+0x2DC</c>) is
    /// PARTY-WIDE, shared across every companion's follow record — not per-member — confirmed by
    /// <see cref="TryGetFollowDiagnostics"/>'s own round-88 remarks. The new node is written at the
    /// CURRENT write cursor (the next slot the engine's own recorder — <c>CSWPartyFollowDataArray::
    /// Reset/SmoothFollowPath/ProcessPoint</c> — has not yet filled), so this never overwrites an
    /// already-recorded node any OTHER party member's own gap/search might still depend on; bumping
    /// the cursor only ever grows the amount of available trail, which is harmless for a companion
    /// further behind (a larger <c>gap</c> for them is the same "more has been recorded" state the
    /// engine's own recorder already produces during ordinary play). <c>CSWPartyFollowDataArray::
    /// GetPoint</c>'s own disassembled addressing (<c>followData + index*40</c>, round 89's own
    /// finding, reconfirmed this round) is the only documented field of a 40-byte node record — the
    /// 12-byte float3 position at offset 0. Only that Vec3 is written; the remaining 28 bytes of the
    /// slot are left exactly as the ring buffer's own prior contents leave them (undocumented, and —
    /// per the disassembly above — never read by the specific accept path this fix exercises, since
    /// <c>DestNode != CurrentPathNode</c> after this write takes the UNCONDITIONAL accept branch,
    /// which never calls <c>GetPoint</c> at all for the new node in the same pass; a LATER pass
    /// reaching the new node via the trivial-match geometric-test path would read only that same
    /// 12-byte position back, exactly as written here).
    ///
    /// Order matters: the array's write cursor (<c>followData+0xFA4</c>) must be advanced BEFORE
    /// (or atomically alongside, as this function does — both within a single call, well before the
    /// engine's own next per-frame dispatch) <c>DestNode</c> is set to the new node's index. Setting
    /// <c>DestNode</c> to a value equal to a STALE (not-yet-advanced) write cursor reproduces round
    /// 83's own live-disproven first attempt exactly (see <see cref="TryAdvanceStalledPathDestination"/>'s
    /// own remarks) — this function writes the node, advances the cursor, and only then writes
    /// <c>DestNode</c>, in that order, so no intermediate state the caller could observe ever has
    /// <c>DestNode</c> pointing at a not-yet-reachable slot.
    ///
    /// Gating: uses <see cref="FinalNodeGeometryRule.IsExtendableTerminalStallShape"/> — phase 6 OR
    /// phase 7, NOT the narrower phase-7-only <see cref="FinalNodeGeometryRule.IsTerminalStallShape"/>
    /// <see cref="TryAcceptFinalPathNode"/>/<see cref="TryRefreshFinalNodeTarget"/> both use. See
    /// that rule's own remarks — a live A/B attempt this round found the planned phase-7 repro
    /// stalling one phase EARLIER instead, at phase 6, in a handler (0x00512A60) fresh disassembly
    /// this round found is byte-for-byte structurally identical to <c>PathWaitDest</c> (same search,
    /// same <c>fcompp</c> test, same <c>gap==1</c> trigger — differing only in which phase its own
    /// unconditional-accept branch commits). Since this function never writes the phase field
    /// itself, the identical mechanism applies to both without any extra branching here — only the
    /// shape gate differs. No consecutive-frame gate (unlike
    /// <see cref="TryAcceptFinalPathNode"/>'s own <c>FinalNodeStallFrameThreshold</c>): this
    /// function is naturally self-limiting the same way <see cref="TryRefreshFinalNodeTarget"/> is,
    /// but through shape rather than epsilon — the moment it writes, <c>DestNode</c> no longer
    /// equals <c>CurrentPathNode</c>, so <see cref="FinalNodeGeometryRule.IsExtendableTerminalStallShape"/>
    /// stops matching on every subsequent frame until the engine's own <c>WaitingForPath</c>
    /// handler re-collapses <c>DestNode</c> back to <c>CurrentPathNode</c> — which only happens on
    /// a genuine new re-arm of this exact shape, not a runaway repeat write of the same slot.
    ///
    /// NOT YET LIVE-TESTED — this round builds and ships the function with its own kill switch
    /// defaulting OFF pending a live A/B; see the doc entry this round adds for the actual result.
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether the
    /// synthetic node was actually written this call (false when not in the terminal shape, or when
    /// the write-cursor field is not currently readable). Kill switch (to enable):
    /// <c>party_follow_extend_final_node=1</c> (default OFF pending live A/B).
    ///
    /// <paramref name="logWrites"/> (the caller's own cached <c>party_follow_finelog</c> flag —
    /// this function reads no config itself, since it runs every frame): when true and the
    /// synthetic node actually gets written, logs the exact node index/coordinates and the
    /// before/after write cursor and <c>DestNode</c> directly (round-90 A/B follow-up — the
    /// previous round shipped this function live-enabled for a full session but could never
    /// directly attribute the session's own successful leg completion to it firing; this closes
    /// that gap with a single <see cref="PluginHost.Log"/> call gated on the write itself, zero
    /// cost when the flag is off).
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
    /// ROUND 73 FALLBACK (per the round's own brief, step 3: "if CSWParty::Update doesn't move
    /// the frozen point either, directly write the follow-target point / cached leader position
    /// fields to the actual current leader position each step"). It does not — see
    /// <see cref="PartyUpdateFn"/>'s and <see cref="RecalculateFollowPointFn"/>'s own remarks:
    /// this round confirmed live that <c>CSWParty::Update</c> genuinely refreshes the
    /// PARTY-LEVEL reference position (<c>party+0x2B0..0x2B8</c>) from the leader's live
    /// position every frame, and that an unconditional <c>RecaulateFollowPoint</c> call fed by
    /// that fresh reference STILL leaves a stalled companion's PER-MEMBER
    /// <c>record+0x28</c>/<c>record+0x18</c> exactly where they were. Neither engine call
    /// reaches those two fields for a companion in this state, for a reason this round's
    /// disassembly of <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360 suggests but
    /// did not fully settle: the function treats a "target changed" transition as a DIRTY EVENT
    /// — <c>record+0x68</c> (<see cref="FollowDiag.SnapshotPendingFlag"/>) is read as a flag
    /// that, when set, makes the function copy the creature's own current position
    /// (<c>creature+0x90</c>) into <c>record+0x5c</c> (the "snapshot" <see cref="FollowDiag"/>
    /// already exposes) and then clear the flag — i.e. it looks like the SIGNAL that tells this
    /// per-frame continuation "the target moved, re-snapshot and compute a fresh step," not
    /// merely a byproduct of moving the target. A raw write to <c>record+0x28</c> alone (as
    /// round 72 tried for <c>record+0x18</c>) never sets it, so nothing downstream ever notices.
    ///
    /// This writes <c>record+0x28</c> (target) AND <c>record+0x18</c> (the cached leader
    /// position <see cref="TryPokeFollowPriorPosition"/> already covers) together, and ALSO
    /// sets <c>record+0x68</c> to 1 — but ONLY when the target has moved more than a small
    /// epsilon from its current value, so this does not stomp the flag every single frame once
    /// the companion is genuinely caught up and walking normally (which would fight the
    /// function's own snapshot/step bookkeeping instead of helping it). Returns false when the
    /// index does not resolve; returns true (having written nothing) when the target is already
    /// within the epsilon of <paramref name="leaderPos"/>.
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

        // ROUND 74 TRIED AND REVERTED: WalkUpdateLocation_QuickWalk_FollowLeader's own gate at
        // 0x51C4D5 computes target-minus-prior and branches on whether that is "close to zero"
        // (0x51C61F — live-confirmed round 74 to park the step FSM at whatever phase it last
        // reached and never re-trigger the phase dispatch again) or "a real delta" (0x51C4E6 —
        // the path this round's disasm confirms genuinely reaches and advances the phase
        // dispatch). Round 74 tried writing prior <- the OLD target UNCONDITIONALLY, every call,
        // and reverted it: measured against active tp-hop movement only, it showed no effect and
        // cost a real increase in write frequency.
        //
        // ROUND 83 (2026-08-23, party-reconciliation.md): full disassembly of PathWaitDest
        // (0x00512B40, the phase=7 handler every stall rounds 74/75/80/81 land in) finds its own
        // accept/reject test at 0x512B98-0x512C03 computes TWO vectors — (node - Prior) and
        // (node - Target), the SAME Prior/Target fields this function writes — and compares them
        // (an fcompp + parity-flag test, the idiom for an UNORDERED/NaN-sensitive comparison).
        // Live-reproduced this round: a companion stalled at phase=7 with CurrentPathNode ==
        // DestNode (both frozen at the same value) has Prior and Target ALSO frozen at the exact
        // same value — because this method's own >10cm gate, once the leader stops moving
        // relative to the last-written target, never fires again, and the ORIGINAL round-73
        // write set BOTH fields to the identical new leaderPos every time it did fire. Two
        // IDENTICAL points make (node-Prior) and (node-Target) identical vectors — a degenerate,
        // zero-length Prior-to-Target segment with no direction for PathWaitDest's own test to
        // measure "past" against, which the live data is consistent with never being accepted.
        //
        // Fix: when the gate fires, write Prior <- the OLD target (a real prior waypoint) and
        // Target <- the new leaderPos, instead of collapsing both to the same new value. This
        // keeps round 73's original throttle (still only writes past the 10cm threshold, not
        // every frame — round 74's own objection does not apply here) while guaranteeing Prior
        // != Target on every write that actually happens, so the segment PathWaitDest tests
        // against is never degenerate. Once the leader stops for good, the LAST such write leaves
        // a genuine, non-zero, static Prior->Target segment aimed at the leader's final position
        // for PathWaitDest to evaluate every subsequent frame — rather than round 73's own
        // Prior==Target dead end.
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
    /// <c>CSWParty::Update</c> 0x00637050 — <c>__thiscall</c>, ONE stack argument (4 bytes,
    /// matching <c>param_size_bytes=4</c> in <c>tools/kotor1_0_3.db</c>): a delta-time FLOAT,
    /// verified live round 73 via this function's single caller in the whole binary (
    /// <c>tools/xrefs.py 0x637050</c> finds exactly one, at 0x0060339A). That caller sits inside
    /// <c>CClientExoAppInternal</c>'s own per-frame update dispatch — the same routine that also
    /// calls <c>CClientExoAppInternal::ProcessInput</c> and <c>CExoResMan::Update</c> every
    /// frame — and computes <c>deltaTime * &lt;a global time-scale multiplier at 0x7439BC&gt;</c>
    /// into a scratch float (0x78E574) that it pushes as this function's only argument. This
    /// function's own body (round 73 disasm) opens with
    /// <c>fld dword ptr [ebp+8]; fadd dword ptr [this+0x2BC]; fstp dword ptr [this+0x2BC]</c> —
    /// an elapsed-time ACCUMULATOR at <c>this+0x2BC</c>, consistent with nothing but a delta
    /// time.
    ///
    /// ROUND 73: this is the missing per-frame driver round 72 named as the prime suspect but
    /// did not call. Past the accumulator, this function refreshes a PARTY-LEVEL (not
    /// per-member) reference position at <c>this+0x2B0..0x2B8</c> from the leader creature's own
    /// LIVE position (read fresh via <c>GetCreatureByGameObjectID</c> off the id at
    /// <c>this+0x24</c>, the leader/member-0 slot) whenever the leader has moved more than a
    /// threshold (the float at 0x741838) since the last call — then, if the party's own
    /// "furthest member" (<c>CSWPartyFollowDataArray::FindFurthestPartyMemeber_Current</c>,
    /// <c>this+0x2DC</c>) has drifted far enough
    /// (<c>PointOnLineDistanceFromLeader</c> vs the float at 0x747C3C) and
    /// <c>CSWParty::TestAndResetPath()</c> reports the path is not already being reset, it calls
    /// <c>CSWParty::RecaulateFollowPoint()</c> itself — the exact function round 72 already tried
    /// calling directly and found to have ZERO effect on a companion's frozen follow target.
    /// That null result is now explained: round 72's manual <c>RecaulateFollowPoint</c> call read
    /// <c>this+0x2B0..0x2B8</c> as its own "current leader position" input, and that field is
    /// ONLY ever refreshed by THIS function — which was never being called at all in the
    /// coop-driven session (its one real caller lives inside a per-frame dispatch this plugin's
    /// own drive loop does not reach for the reasons rounds 60-67 already found for animation),
    /// so the manual recompute was working from a reference position frozen at module-load, same
    /// as the per-member target it produced.
    ///
    /// The caller's own gate (<c>esi+0x18</c> non-null and <c>[esi+0x18+0x48]</c> non-zero, both
    /// read immediately before the real engine's push/call) is NOT reproduced here —
    /// <see cref="PumpPartyUpdate"/> calls this unconditionally, once per rendered frame, while a
    /// party object exists, guarded only by the same preconditions
    /// <see cref="PartyFollowController"/> already applies before touching anything (spawned and
    /// driving, not mid-conversation, roster not frozen, no peer party-presence channel active).
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

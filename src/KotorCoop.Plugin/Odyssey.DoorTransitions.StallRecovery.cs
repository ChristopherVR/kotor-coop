// Odyssey.DoorTransitions.StallRecovery.cs - part 2: recovering a follow leader
// that has stalled (stuck-phase reset, step accumulator, stalled path destination).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    public static bool TryResetStuckFollowPhase(uint companionServerObjectId, out bool wrote)
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
        if (phase == -1) // phase == 5 is deliberately excluded here — see docs/research/party-reconciliation.md for why
        {
            *(int*)(record + 4) = 0;
            *(int*)(record + 0x44) = 0;
            wrote = true;
        }

        return true;
    }

    /// <summary>
    /// <c>creature+0xD0</c> (the "step accumulator") is read, never written, by
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> (0x0051C360) at its own first gate: the
    /// whole function returns immediately, before ever reaching the phase-dispatch block, whenever
    /// this reads exactly 0 — and for a parked companion it reads 0 in essentially every sample,
    /// including immediately after module load. The only writer found anywhere in the binary is
    /// the outer caller <c>WalkUpdateLocation_QuickWalk</c> (0x0051D9C0), which unconditionally
    /// zeroes it as part of a periodic commit gated on a world-timer check — nothing accumulates a
    /// nonzero value into it between commits, the same "async producer commits but nothing fills
    /// the gap" shape as <c>CSWParty::Update</c> (see <see cref="PumpPartyUpdate"/>'s own
    /// remarks) — so this pokes a plausible per-frame delta directly.
    ///
    /// Writes <paramref name="ticks"/> (an unsigned dword, nominally milliseconds) directly into
    /// <c>creature+0xD0</c>. Returns false only when the index does not resolve. See
    /// docs/research/party-reconciliation.md.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryPokeStepAccumulator(uint companionServerObjectId, int ticks)
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

        nint creature = ResolveServerObject(companionServerObjectId);
        if (creature == 0)
        {
            return false;
        }

        *(int*)(creature + 0xd0) = ticks;
        return true;
    }

    /// <summary>CSWSCreature::WalkUpdateLocation_QuickWalk_FollowLeader 0x0051C360 —
    /// <c>__thiscall</c>, no stack args beyond <c>this</c> (a CSWSCreature*). See
    /// <see cref="TryDriveFollowLeaderStep"/>'s own remarks for why this is called directly
    /// rather than relying on the engine's own per-frame dispatch.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> WalkUpdateLocationQuickWalkFollowLeaderFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x0051C360;

    /// <summary>
    /// <see cref="TryPokeStepAccumulator"/> alone (write, return, let the engine's own per-frame
    /// call chain pick it up later) was live-tested and measured insufficient: the outer caller
    /// <c>WalkUpdateLocation_QuickWalk</c> (0x0051D9C0) unconditionally re-zeroes
    /// <c>creature+0xD0</c> every time the engine's own per-frame call chain reaches this
    /// creature — at a different point in the frame than this plugin's own hook — so a poke made
    /// on a prior plugin frame is already reset before the real
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> call the engine makes on its own ever
    /// consumes it.
    ///
    /// This pokes <c>creature+0xD0</c> and then calls
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> directly in the same call — the same "poke
    /// the input, then call the real engine function immediately so nothing else can claw it back
    /// first" pattern <see cref="PumpPartyUpdate"/> established for <c>CSWParty::Update</c>. This
    /// is the same function the engine's own <c>WalkUpdateLocation_QuickWalk</c> calls when
    /// <c>CreMoveState==3</c> (follow) — calling it directly does not skip any state validation
    /// FollowLeader itself performs; it only skips the OUTER function's own re-zeroing of the
    /// field this call needs to be nonzero. See docs/research/party-reconciliation.md.
    ///
    /// Returns false when the index does not resolve (mirroring <see cref="TryResetStuckFollowPhase"/>).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryDriveFollowLeaderStep(uint companionServerObjectId, int ticks)
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

        nint creature = ResolveServerObject(companionServerObjectId);
        if (creature == 0)
        {
            return false;
        }

        *(int*)(creature + 0xd0) = ticks;
        WalkUpdateLocationQuickWalkFollowLeaderFn(creature);
        return true;
    }

    /// <summary>
    /// The disassembly-grounded root cause of the phase=7 permanent stall at the
    /// <c>pathNode == DestNode &lt; WriteCursor</c> shape:
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader_WaitingForPath</c> (0x005127D0, the phase=5
    /// handler) is the only writer of <c>record+0x34</c> (<see cref="FollowDiag.DestNode"/>) in
    /// the binary, and every time its commit runs it resets DestNode to EQUAL CurrentPathNode —
    /// never advanced toward the write cursor (<see cref="FollowDiag.WriteCursor"/>) even when the
    /// trail already holds further, unconsumed points — re-collapsing on every single frame this
    /// plugin drives the step function directly.
    ///
    /// <c>PathWaitDest</c> (0x00512B40) is fatal to this: its forward search from CurrentPathNode
    /// finds a match on the FIRST comparison whenever DestNode == CurrentPathNode, and only in
    /// that case falls into a fragile accept/reject test comparing (node - Prior) against
    /// (node - Target), which needs Prior != Target to be meaningful (see
    /// <see cref="TryDriveFollowTarget"/>'s own remarks — WaitingForPath's re-commit clobbers
    /// Prior/Target back to identical values on the same frame regardless). If DestNode differs
    /// from CurrentPathNode instead, the search walks forward and takes PathWaitDest's OTHER,
    /// unconditional accept branch — skipping the fragile test entirely.
    ///
    /// This writes <c>record+0x34</c> (DestNode) to the current WriteCursor value — "catch up to
    /// everything already recorded" — only when the FSM is genuinely parked in this shape (phase
    /// 7, CurrentPathNode == DestNode, WriteCursor genuinely ahead by a small, circular-buffer-sane
    /// amount). Called after <see cref="TryDriveFollowLeaderStep"/> in the same frame, so it acts
    /// on this frame's post-WaitingForPath-commit state before WaitingForPath gets another chance
    /// to re-collapse it. See docs/research/party-reconciliation.md.
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether the
    /// stall shape was actually observed and corrected this call.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryAdvanceStalledPathDestination(uint companionServerObjectId, out bool wrote)
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
            return true; // not parked in the stall shape this targets — nothing to do
        }

        int pathNode = *(int*)(record + 0x24);
        int destNode = *(int*)(record + 0x34);
        if (pathNode != destNode)
        {
            return true; // DestNode already differs — PathWaitDest's search can already progress
        }

        nint followData = party + 0x2DC;
        if (!NativeMethods.IsReadable(followData + 0xFA4))
        {
            return true;
        }

        int writeCursor = *(int*)(followData + 0xFA4);
        if (writeCursor == pathNode)
        {
            return true; // genuinely nothing new recorded yet — not a stall, just caught up
        }

        // Circular buffer, 100 slots (0x0051C700's own wrap-at-0x64 constant, and PathWaitDest's
        // own search loop). A sane "ahead by" gap is small in ordinary play; cap generously to
        // avoid ever acting on a bogus wraparound read.
        int gap = writeCursor - pathNode;
        if (gap < 0)
        {
            gap += 100;
        }

        // PathWaitDest's own search loop (0x00512B60-0x00512B6E) tests `eax == DestNode` BEFORE
        // incrementing `eax`, then tests `eax == WriteCursor` AFTER incrementing to decide whether
        // to keep looping — so a DestNode set to the SAME value as WriteCursor is structurally
        // unreachable: the loop's own "give up" exit fires one iteration before it would ever
        // re-test that value against DestNode (live-confirmed: setting DestNode = WriteCursor
        // advanced DestNode but PathWaitDest kept returning "not ready" forever, CurrentPathNode
        // never moved). Needs `gap >= 2` — DestNode = WriteCursor - 1 is always found by the loop
        // before the increment can reach WriteCursor.
        if (gap is < 2 or > 90)
        {
            return true;
        }

        int newDest = writeCursor - 1;
        if (newDest < 0)
        {
            newDest += 100;
        }

        *(int*)(record + 0x34) = newDest;
        wrote = true;
        return true;
    }

    /// <summary>
    /// The phase-6 analog of <see cref="TryAdvanceStalledPathDestination"/> — same fix, same
    /// mechanism, applied to the handler at 0x00512A60 (dispatch index 7, phase 6) instead of
    /// <c>PathWaitDest</c> (0x00512B40, phase 7). Discovered live: an A/B repro of fix candidate 1
    /// (<see cref="TryExtendFinalNodeTrail"/>, which targets the narrower <c>gap==1</c> terminal
    /// shape) found a companion parked permanently at phase=6 with <c>gap==2</c> — the exact
    /// <c>gap&gt;=2</c> shape <see cref="TryAdvanceStalledPathDestination"/> already fixes for
    /// phase 7, but that function's own <c>phase != 7</c> guard refuses to act here.
    ///
    /// Disassembly confirms the handler at 0x00512A60 is byte-for-byte structurally identical to
    /// <c>PathWaitDest</c> in every respect this fix depends on — same forward search, same
    /// "<c>DestNode == WriteCursor</c> is structurally unreachable, needs <c>gap&gt;=2</c>" defect
    /// <see cref="TryAdvanceStalledPathDestination"/>'s own remarks derive for phase 7 — the only
    /// difference is which phase its own unconditional-accept tail commits (1 here vs. 3 for
    /// PathWaitDest).
    ///
    /// A deliberately separate function and kill switch from the already-shipped, phase-7-only
    /// <see cref="TryAdvanceStalledPathDestination"/>, rather than widening that function's own
    /// phase check in place — this project's convention is to verify a researched offset live
    /// before shipping the write, and broadening already-tested behaviour without a fresh live A/B
    /// would violate that just as much as shipping new, untested code would. See
    /// docs/research/party-reconciliation.md.
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether the
    /// stall shape was actually observed and corrected this call. Kill switch (to enable):
    /// <c>party_follow_advance_destnode_phase6=1</c> (default OFF).
    ///
    /// <paramref name="logWrites"/> (the caller's own cached <c>party_follow_finelog</c> flag;
    /// this function reads no config itself) logs the before/after <c>DestNode</c> repoint
    /// directly when a write fires.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryAdvanceStalledPathDestinationPhase6(
        uint companionServerObjectId, bool logWrites, out bool wrote)
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
        if (phase != 6)
        {
            return true; // not parked in the stall shape this targets — nothing to do
        }

        int pathNode = *(int*)(record + 0x24);
        int destNode = *(int*)(record + 0x34);
        if (pathNode != destNode)
        {
            return true; // DestNode already differs — the phase-6 search can already progress
        }

        nint followData = party + 0x2DC;
        if (!NativeMethods.IsReadable(followData + 0xFA4))
        {
            return true;
        }

        int writeCursor = *(int*)(followData + 0xFA4);
        if (writeCursor == pathNode)
        {
            return true; // genuinely nothing new recorded yet — not a stall, just caught up
        }

        int gap = writeCursor - pathNode;
        if (gap < 0)
        {
            gap += 100;
        }

        // Same reachability constraint as TryAdvanceStalledPathDestination's own remarks —
        // DestNode = WriteCursor is unreachable; needs gap >= 2 so DestNode = WriteCursor - 1 is
        // found by the search before it can give up.
        if (gap is < 2 or > 90)
        {
            return true;
        }

        int newDest = writeCursor - 1;
        if (newDest < 0)
        {
            newDest += 100;
        }

        *(int*)(record + 0x34) = newDest;
        wrote = true;

        if (logWrites)
        {
            PluginHost.Log(
                $"party follow phase6advance: 0x{companionServerObjectId:X} fired "
                + $"phase={phase} pathNode={pathNode} writeCursor={writeCursor} gap={gap} "
                + $"destNode {destNode} -> {newDest}");
        }

        return true;
    }

    /// <summary>
    /// Historical: documents <see cref="TryAdvanceStalledMovingToPath"/> (in
    /// Odyssey.DoorTransitions.PathNodes.cs) — a third, independent permanent-stall shape at
    /// phase 2, distinct from the <c>CreMoveState</c> revert <see cref="FollowMoveGateCheckFn"/>
    /// causes (that check was measured to fire constantly, even during smooth walking, so it does
    /// not time this specific stall).
    ///
    /// Handler 0x00511860 (phase 2) computes the distance between <c>PriorPoint</c>
    /// (<c>record+0x18</c>) and <c>Snapshot</c> (<c>record+0x5c</c>) and compares it against a
    /// budget threshold (<see cref="FollowDiag.MoveBudget"/>). When the two points are already
    /// equal (a zero-length remaining segment), that comparison is trivially satisfied, but the
    /// engine does not commit phase 3 directly from it — it only sets a local variable, and the
    /// real commit is gated behind <c>CSWSArea::TestDirectLine</c> (0x004BCB70) returning exactly
    /// 1. When TestDirectLine returns anything else — which happens on essentially every call once
    /// parked in this exact shape — the function instead queues an async re-check
    /// (<c>CServerAIMaster::AddEventDeltaTime</c>) that never resolves favourably, leaving phase
    /// stuck at 2 until the watchdog force-resets to -1 and FindPath re-derives the same
    /// degenerate input, in a permanent cycle.
    ///
    /// The fix commits phase 3 directly — the same value the engine's own "close enough" branch
    /// would have written had TestDirectLine returned 1 — only when PriorPoint and Snapshot are
    /// already equal and the watchdog has made at least one real pass (<c>attempts &gt; 0</c>).
    /// This does not bypass movement/collision validation: <c>Moving_To_Path</c>'s own "close
    /// enough to the next recorded node" gate (0x4AA980) still runs on every subsequent call and
    /// is what actually decides whether <c>CurrentPathNode</c> advances. See
    /// docs/research/party-reconciliation.md.
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether the
    /// stall shape was actually observed and corrected this call. Kill switch:
    /// <c>party_follow_advance_movingtopath=0</c> (default on).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

}

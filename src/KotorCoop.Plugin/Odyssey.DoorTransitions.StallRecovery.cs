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

    /// <summary>
    /// ROUND 98 (party-reconciliation.md): the mirror-side counterpart to
    /// <see cref="TryResetStuckFollowPhase"/> — fired ONCE, at the moment THIS instance STOPS
    /// mirroring a companion (<c>PartyPresenceController.ReleaseSession</c>, the session's falling
    /// edge), not every frame the way every other function in this file runs.
    ///
    /// See <see cref="FollowPhaseReleaseRule"/>'s own remarks for the full disassembly-grounded
    /// chain this is built on: a mirrored companion's follow record is a frozen leftover for the
    /// entire mirrored session (<c>PartyPresenceController.DriveOne</c>'s own
    /// <see cref="ClaimCreature(nint)"/> call empties the queue and forces <c>CreMoveState=1</c>
    /// every pass, which keeps the AI master's own per-tick gate from ever entering
    /// <c>WalkUpdateLocation_QuickWalk</c> for this creature at all — so nothing advances or reads
    /// <c>record+4</c> while mirroring holds). Left alone, the moment mirroring stops and
    /// <see cref="PartyFollowController"/> starts driving this same record again, it resumes
    /// against whatever <c>Target</c>/<c>Prior</c>/<c>Snapshot</c>/phase the record held the
    /// INSTANT mirroring began — potentially many units from wherever
    /// <see cref="PartyPresenceController"/> has since teleported the body via its own per-frame
    /// <c>SetPositionFn</c> writes, since nothing about mirroring ever touches this record. That is
    /// the same degenerate shape rounds 88-97 spent ten rounds characterizing for a companion that
    /// was NEVER mirrored at all (frozen <c>Target == Prior == RecordedDestPoint</c>,
    /// <c>PathWaitDest</c>'s own strict-inequality accept test at 0x00512BF2 permanently false),
    /// except worse: an in-session freeze there is stale by centimeters; a record frozen through a
    /// whole mirrored session can be stale by however far the companion has since been teleported.
    ///
    /// Rather than reconstruct a fully-correct reset of every stale field by hand
    /// (<c>CurrentPathNode</c>/<c>DestNode</c>/<c>WriteCursor</c> all index a shared, engine-owned
    /// circular trail buffer this codebase has never had reason to rebuild from scratch), this
    /// writes ONLY <see cref="FollowPhaseReleaseRule.EngineWatchdogResetPhase"/> (-1) — the exact
    /// sentinel the engine's own 50-attempt watchdog already writes on an unrelated stall
    /// (0x0051C964) — and zeroes <c>attempts</c> (<c>record+0x44</c>) alongside it, matching that
    /// same engine write exactly. The very next time anything dispatches this record (the following
    /// <see cref="PartyFollowController"/> frame), the ALREADY-shipped, default-ON
    /// <see cref="TryResetStuckFollowPhase"/> recognizes phase==-1 and forces phase=0
    /// (<c>FindPath</c>) with a real, from-scratch pathfind — reusing machinery rounds 71-97 have
    /// already live-verified extensively, rather than inventing a new recovery path for the mirror
    /// boundary specifically.
    ///
    /// Gated by <see cref="FollowPhaseReleaseRule.ShouldRelease"/> — a no-op when the record
    /// already reads the reset sentinel, so a companion already idle (or one this instance never
    /// actually got to mirror before the session ended) is never logged or written pointlessly.
    ///
    /// ROUND 101 (party-reconciliation.md): round 100's own live A/B ran this with every
    /// precondition satisfied (config on, a genuinely non-reset phase captured pre-disconnect,
    /// <c>_trackedId</c> populated) and got NO <c>phase7release</c> line and no effect — narrowed
    /// to "one of this function's own early returns refused, silently." ROOT-CAUSED this round,
    /// by direct disassembly (<c>tools/disasm.py 0x006349C0 40</c>) of
    /// <c>CSWParty::FindMemberIndexById</c> (<see cref="GetPartyIndexFn"/>, 0x006349C0):
    ///
    /// <code>
    /// 0x006349CD  mov edx, [ecx]        ; edx = party+0x00, CSWParty's own live member count
    /// 0x006349D1  test edx, edx
    /// 0x006349D3  jle 0x6349e7          ; count &lt;= 0 -&gt; not found
    /// 0x006349D5  add ecx, 0x24         ; ecx = first record (party+0x24)
    /// 0x006349D8  cmp [ecx], esi        ; record's own stored id == the id we are searching for?
    /// 0x006349DA  je 0x6349ea           ; found -&gt; return the current index
    /// 0x006349DC  inc eax
    /// 0x006349DD  add ecx, 0x88         ; next record
    /// 0x006349E3  cmp eax, edx          ; index == count?
    /// 0x006349E5  jl 0x6349d8           ; loop while index &lt; count
    /// 0x006349E7  or eax, 0xffffffff    ; exhausted the count without a match -&gt; -1
    /// </code>
    ///
    /// The search is bounded by <c>party+0x00</c> — the EXACT SAME field
    /// <c>PartyTogetherClampRule</c>/<c>PartyRosterController.SyncClientPartyTogetherState</c>
    /// (<c>party_together_fix</c>, default ON, CLIENT-ONLY) deliberately clamps DOWN to
    /// <c>CSWPartyTable</c>'s companions-only count, EVERY frame, for as long as this client
    /// mirrors at least one companion (<c>PartyTogetherClampRule</c>'s own remarks: "In the
    /// ordinary case the array is ordered [PC, companion, companion], so 'the last one' is always
    /// a companion" — i.e. this is the everyday steady state for a mirroring client, not an edge
    /// case). That clamp is what lets a client cross a door while a mirrored companion stands
    /// wherever the host's copy is, but its side effect is that <see cref="GetPartyIndexFn"/>'s own
    /// loop bound EXCLUDES the trimmed (always-last, always-the-mirrored-companion) record
    /// entirely — <c>GetIndex</c> returns -1 for exactly the companion this function exists to
    /// find, not because the record is gone, but because the count that bounds the search says to
    /// stop one slot short of it. Nothing un-clamps <c>party+0x00</c> on its own: the clamp only
    /// ever writes DOWN (<see cref="TryClampClientPartyCount"/>'s own contract), and
    /// <c>SyncClientPartyTogetherState</c> stops re-clamping the moment the host roster is cleared
    /// (which happens well before <c>ReleaseSession</c> runs — <c>KotorGameBridge
    /// .SessionScopedBlockLevels</c> clears "companion presence"/"peer party roster" long before
    /// the LAST entry, "command") — but its LAST write stands. So at the exact moment this
    /// function runs, the clamp's last (low) value is still sitting in <c>party+0x00</c>, and
    /// <see cref="GetPartyIndexFn"/> reliably, deterministically fails — this is not a rare race,
    /// it reproduces every time a client releases a mirrored session with exactly one companion
    /// (round 100's own recipe).
    ///
    /// THE FIX: on a failed lookup, retry ONCE with <c>party+0x00</c> restored to its true,
    /// un-clamped population (<c>CSWPartyTable</c>'s own companions-only count, from
    /// <see cref="TryGetPartyTableMemberCount"/> — a DIFFERENT structure the clamp never touches
    /// — plus one for the PC, matching <see cref="GetClientPartyMemberCount"/>'s own doc comment
    /// on what CSWParty's count should read). The restored value is deliberately left standing
    /// rather than reverted: it is the mathematically correct population, costs nothing to leave,
    /// and <c>SyncClientPartyTogetherState</c> will re-clamp it right back down on its own very
    /// next pass if a session is still actually mirroring — which, at the point
    /// <c>ReleaseSession</c> calls this, it is not.
    ///
    /// Kill switch (to enable): <c>party_follow_phase7_release=1</c> (default OFF). See
    /// docs/research/party-reconciliation.md round 98/100/101 for the live-verification recipe.
    ///
    /// Every refusal path logs its own distinct reason (round 101) so a live pass can tell "the
    /// call never happened" from "the call happened and this specific guard refused it, for this
    /// specific reason" — <paramref name="wrote"/> still separately reports whether the reset
    /// actually fired for callers that only care about the outcome.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryReleaseFollowPhaseOnUnmirror(uint companionServerObjectId, out bool wrote)
    {
        wrote = false;

        nint client = GetClient();
        if (client == 0)
        {
            PluginHost.Log($"party follow phase7release: 0x{companionServerObjectId:X} refused "
                + "- no client (engine not ready / no module loaded)");
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            PluginHost.Log($"party follow phase7release: 0x{companionServerObjectId:X} refused "
                + "- no CSWParty (client-side party struct unresolved)");
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            // ROUND 101: see this method's own remarks above — party_together_fix's clamp on
            // party+0x00 is the routine, deterministic cause of a failed lookup here. Retry once
            // with the count restored to its true (un-clamped) value before concluding the
            // companion genuinely has no record.
            if (TryGetPartyTableMemberCount(out int tableCount) && tableCount is >= 0 and <= 16)
            {
                int truePopulation = tableCount + 1; // + the PC, matching CSWParty's own layout
                int* countPtr = (int*)party;
                if (*countPtr < truePopulation)
                {
                    *countPtr = truePopulation;
                    index = GetPartyIndexFn(party, clientObjectId);
                }
            }

            if (index <= 0)
            {
                PluginHost.Log($"party follow phase7release: 0x{companionServerObjectId:X} refused "
                    + $"- no follow record found (GetIndex returned {index}; not a current party "
                    + "member, even after restoring CSWParty's own member count)");
                return false;
            }
        }

        nint record = party + 0x24 + (index * 0x88);
        int phase = *(int*)(record + 4);
        if (!FollowPhaseReleaseRule.ShouldRelease(phase))
        {
            // Already at the engine's own reset sentinel — nothing to do. Not an error; logged at
            // the same detail level as a real refusal so a live pass can tell the two apart from
            // "silence" without needing round 100's own indirect proof-by-ReleaseAllHeld chain.
            PluginHost.Log($"party follow phase7release: 0x{companionServerObjectId:X} nothing to "
                + "do - phase already at the reset sentinel "
                + $"({FollowPhaseReleaseRule.EngineWatchdogResetPhase})");
            return true;
        }

        *(int*)(record + 4) = FollowPhaseReleaseRule.EngineWatchdogResetPhase;
        *(int*)(record + 0x44) = 0;
        wrote = true;

        PluginHost.Log(
            $"party follow phase7release: 0x{companionServerObjectId:X} reset phase "
            + $"{phase} -> {FollowPhaseReleaseRule.EngineWatchdogResetPhase} on unmirror "
            + "(frozen follow record handed back to PartyFollowController)");

        return true;
    }
}

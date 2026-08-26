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
        if (phase == -1) // ROUND 83: phase == 5 removed — see this method's own remarks
        {
            *(int*)(record + 4) = 0;
            *(int*)(record + 0x44) = 0;
            wrote = true;
        }

        return true;
    }

    /// <summary>
    /// ROUND 80 EXPERIMENT (party-reconciliation.md): <c>creature+0xD0</c> — the "step
    /// accumulator" round 72 already named — is read (never written) by
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360 at its own first gate
    /// (0x51C458-0x51C486): the raw dword is unsigned-wrapped, compared against 0.0, and the
    /// ENTIRE function returns immediately (before ever reaching the phase-dispatch block whose
    /// <c>attempts</c> counter round 74 was tracking) whenever it reads exactly 0. Live-measured
    /// this round: for a parked companion this reads 0 in every one of 400 consecutive samples
    /// (~60s, ~150ms apart, via <see cref="Odyssey.FollowDiag.StepAccumulator"/>), including the
    /// very first sample taken right after module load — this field appears to never leave 0 in a
    /// coop session, which alone accounts for <c>attempts</c> freezing at 0 with no watchdog ever
    /// firing (round 74's own still-open question).
    ///
    /// The producer: disassembling the OUTER caller <c>WalkUpdateLocation_QuickWalk</c> 0x0051D9C0
    /// (0x51DA5D-0x51DAB4) finds the SAME field written exactly once, unconditionally to LITERAL
    /// ZERO (<c>mov [creature+0xD0], 0</c>), gated behind a GetWorldTimer/SubtractWorldTimes
    /// comparison against a cached "last checked" time pair at <c>creature+0xB0/+0xB4</c> (which
    /// DOES advance live — see the existing diagText's own <c>cachedPos</c> field, confirmed
    /// climbing by ~1000/sec, consistent with a millisecond world clock). No instruction anywhere
    /// in the .text section (a full capstone sweep, not just <c>tools/fieldwrites.py</c>'s
    /// MOV-only scan, to also catch FADD/FSTP/ADD) writes a genuinely INCREMENTING value into this
    /// field for CSWSCreature specifically — the periodic commit above is the only writer found,
    /// and it always zeroes. This is the same class of bug as round 73's
    /// <c>CSWParty::Update</c>: an async producer's "commit" fires (the cache advances, the
    /// counter is zeroed as the new baseline) but whatever is meant to accumulate a nonzero value
    /// into it on the frames IN BETWEEN commits was never isolated by static analysis alone — so
    /// this pokes a plausible per-frame delta directly, the established pattern for this exact
    /// situation (see <see cref="PumpPartyUpdate"/>'s own round 73 precedent).
    ///
    /// Writes <paramref name="ticks"/> (interpreted the same way the consumer does: an unsigned
    /// dword, nominally milliseconds given the cached-time-pair unit above) directly into
    /// <c>creature+0xD0</c>. Returns false only when the index does not resolve.
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
    /// ROUND 80 EXPERIMENT (party-reconciliation.md): <see cref="TryPokeStepAccumulator"/> alone
    /// (write <c>creature+0xD0</c>, return, let the engine's own per-frame call chain pick it up
    /// later) was live-tested first and measured NOT sufficient — <c>attempts</c>
    /// (<c>record+0x44</c>) stayed at 0 even with the poked value reading back correctly on the
    /// very next diagnostic sample, 650+ samples across ~90s. The likely reason, found by
    /// re-reading <c>WalkUpdateLocation_QuickWalk</c> 0x0051D9C0's own disasm: that OUTER caller
    /// unconditionally re-zeroes <c>creature+0xD0</c> itself (0x51DAA6, gated behind its own
    /// GetWorldTimer/SubtractWorldTimes check, which — per the existing diagText's own
    /// <c>cachedPos</c> field, confirmed climbing live — genuinely fires) EVERY time the ENGINE's
    /// own per-frame call chain reaches this creature, which happens at a different point in the
    /// frame than this plugin's own hook — so by the time the real
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> call the engine makes on its own actually
    /// runs, the poke from a prior plugin frame has already been reset back to 0 by the engine's
    /// own zeroing, before ever being consumed.
    ///
    /// This pokes <c>creature+0xD0</c> AND THEN calls
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> directly, in the same call, from this
    /// plugin's own drive loop — the exact same "poke the input, then call the real engine
    /// function immediately so nothing else can claw it back first" pattern round 73's
    /// <see cref="PumpPartyUpdate"/> already established for <c>CSWParty::Update</c>. This is the
    /// SAME function the engine's own <c>WalkUpdateLocation_QuickWalk</c> calls when
    /// <c>CreMoveState==3</c> (follow) — calling it directly does not skip any state validation
    /// FollowLeader itself performs (its own index/dead/commandable checks still run inside it);
    /// it only skips the OUTER function's own re-zeroing of the very field this call needs to be
    /// nonzero.
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
    /// ROUND 83 (2026-08-23, party-reconciliation.md): the real, disassembly-grounded root cause
    /// of the phase=7 permanent stall rounds 74/75/80/81 kept finding at the SAME `pathNode ==
    /// DestNode &lt; WriteCursor` shape.
    ///
    /// Full disassembly of <c>WalkUpdateLocation_QuickWalk_FollowLeader_WaitingForPath</c>
    /// (0x005127D0, the phase=5 handler) this round found it is the ONLY writer of
    /// <c>record+0x34</c> (<see cref="FollowDiag.DestNode"/>) in the whole binary, and it sets it
    /// to the SAME value it writes into <c>record+0x24</c> (<see cref="FollowDiag.CurrentPathNode"/>)
    /// — i.e. every time this handler's own commit runs, DestNode is reset to EQUAL CurrentPathNode,
    /// never advanced toward the write cursor
    /// (<c>[followData]+0xFA4</c>, <see cref="FollowDiag.WriteCursor"/>) even when the trail
    /// already holds further, already-recorded, unconsumed points. Live-confirmed this round: a
    /// companion parked with the leader stationary shows CurrentPathNode == DestNode, both frozen,
    /// while WriteCursor sits 3 higher (already-recorded nodes going unconsumed) — and a new
    /// "sample right before vs. right after this frame's engine call" diagnostic
    /// (<see cref="PartyFollowController"/>'s own round-83 preStep trace) shows WaitingForPath's
    /// commit re-firing and re-collapsing DestNode back to CurrentPathNode on EVERY SINGLE FRAME
    /// this plugin drives the step function directly, not as a rare edge case.
    ///
    /// <c>PathWaitDest</c> 0x00512B40's own full disasm explains why this is fatal: its forward
    /// search from CurrentPathNode toward WriteCursor looking for an index equal to DestNode finds
    /// it on the FIRST comparison whenever DestNode == CurrentPathNode (trivially true right after
    /// WaitingForPath's commit) — and ONLY in that exact case does it fall into a fragile
    /// accept/reject test comparing (node - Prior) against (node - Target), which requires Prior
    /// != Target to be meaningful (see <see cref="TryDriveFollowTarget"/>'s own round-83 remarks
    /// for why that degenerate-input problem is real but insufficient alone to explain the live
    /// data — WaitingForPath's own re-commit clobbers Prior/Target back to identical values on the
    /// very same frame, faster than either side of that fix can win). If DestNode is instead
    /// DIFFERENT from CurrentPathNode when PathWaitDest runs, the search walks forward past the
    /// degenerate first comparison and, on finding DestNode reached, takes PathWaitDest's OTHER,
    /// UNCONDITIONAL accept branch (0x00512C09: <c>mov [record+4],3</c>, "Moving_To_Path") —
    /// skipping the fragile line test entirely.
    ///
    /// This writes <c>record+0x34</c> (DestNode) to the current WriteCursor value — i.e. "catch up
    /// to everything already recorded" — ONLY when the FSM is genuinely parked in exactly this
    /// shape (phase 7, CurrentPathNode == DestNode, WriteCursor genuinely ahead by a small,
    /// circular-buffer-sane amount), so it never touches a companion that is mid-flight in a
    /// healthy cycle. Called AFTER <see cref="TryDriveFollowLeaderStep"/> in the same frame (so it
    /// acts on this frame's post-WaitingForPath-commit state, and its effect is visible to NEXT
    /// frame's dispatch before WaitingForPath gets another chance to re-collapse it).
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
        // own search loop). A sane "ahead by" gap is small in ordinary play (rounds 75/80 observed
        // 2-3); cap generously to avoid ever acting on a bogus wraparound read.
        int gap = writeCursor - pathNode;
        if (gap < 0)
        {
            gap += 100;
        }

        // ROUND 83 LIVE-DISPROVEN FIRST ATTEMPT, FIXED: PathWaitDest's own search loop
        // (0x00512B60-0x00512B6E) tests `eax == DestNode` BEFORE incrementing `eax`, then tests
        // `eax == WriteCursor` AFTER incrementing to decide whether to keep looping — so a
        // DestNode set to the SAME value as WriteCursor is structurally unreachable: the loop's
        // own "give up" exit fires the instant the incremented eax reaches WriteCursor, one
        // iteration before it would ever re-test that value against DestNode. Live-confirmed:
        // setting DestNode = WriteCursor advanced DestNode (visibly, in the diagnostic) but
        // PathWaitDest kept returning "not ready" forever anyway, CurrentPathNode never moved.
        // Needs `gap >= 2` (WriteCursor strictly ahead of a DestNode that is itself strictly
        // ahead of CurrentPathNode) — DestNode = WriteCursor - 1 is always found by the loop
        // BEFORE the increment can reach WriteCursor.
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
    /// LIVE FINDING (2026-08-24, fix-candidate-1 A/B round, <c>docs/research/party-reconciliation.md</c>):
    /// the phase-6 analog of <see cref="TryAdvanceStalledPathDestination"/> — same fix, same
    /// mechanism, applied to the handler at 0x00512A60 (dispatch index 7, phase 6) instead of
    /// <c>PathWaitDest</c> (0x00512B40, phase 7). Discovered live, not planned: this round's own
    /// A/B repro of fix candidate 1 (<see cref="TryExtendFinalNodeTrail"/>, which targets the
    /// narrower <c>gap==1</c> terminal shape) found Trask parked PERMANENTLY at
    /// <c>phase=6, pathNode==destNode==83, writeCursor=85</c> (<c>gap==2</c>) for 3700+ consecutive
    /// finelog samples, real position frozen — the exact <c>gap&gt;=2</c> shape
    /// <see cref="TryAdvanceStalledPathDestination"/> already fixes for phase 7, but that function's
    /// own <c>phase != 7</c> guard refuses to act here.
    ///
    /// Fresh disassembly this round (<c>tools/disasm.py 0x512A60 220</c>, cross-checked against
    /// <c>tools/disasm.py 0x512B40 220</c> instruction-for-instruction) confirms the handler at
    /// 0x00512A60 is byte-for-byte structurally identical to <c>PathWaitDest</c> in every respect
    /// this fix depends on: the same forward search from <c>CurrentPathNode</c>, the same
    /// "<c>DestNode == WriteCursor</c> is structurally unreachable, needs <c>gap&gt;=2</c>" defect
    /// <see cref="TryAdvanceStalledPathDestination"/>'s own remarks derive for phase 7 (the search's
    /// "give up" exit at <c>eax==WriteCursor</c> fires one iteration before a re-test against a
    /// <c>DestNode</c> set to that exact value could happen — identical instruction sequence at
    /// 0x512A84-0x512A8E vs. phase 7's 0x512B64-0x512B6E) — the only difference anywhere in the
    /// function is which phase its own unconditional-accept tail commits (1 here, 0x512B2A; 3 for
    /// PathWaitDest, 0x512C0A).
    ///
    /// A deliberately SEPARATE function and kill switch from the already-shipped, phase-7-only
    /// <see cref="TryAdvanceStalledPathDestination"/> (default ON, live-tested only against phase 7)
    /// rather than widening that function's own phase check in place — broadening already-shipped,
    /// already-tested behaviour without its own fresh live A/B would violate this project's own
    /// "verify a researched offset live before shipping the write" rule just as much as shipping an
    /// entirely new function untested would.
    ///
    /// NOT YET LIVE-TESTED IN ISOLATION as of writing — enabled together with fix candidate 1 in
    /// the SAME A/B session that found the need for it (this repro could not otherwise reach
    /// phase 7 at all to test candidate 1 against its own intended shape); see the doc entry this
    /// round adds for what enabling both together actually produced.
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether the
    /// stall shape was actually observed and corrected this call. Kill switch (to enable):
    /// <c>party_follow_advance_destnode_phase6=1</c> (default OFF).
    ///
    /// <paramref name="logWrites"/> (the caller's own cached <c>party_follow_finelog</c> flag —
    /// this function reads no config itself, since it runs every frame): when true and a write
    /// actually fires, logs the exact before/after <c>DestNode</c> repoint directly (round-90
    /// A/B follow-up — the previous round shipped this function without ANY direct proof it ever
    /// fires; this closes that gap the cheapest possible way, a single <see cref="PluginHost.Log"/>
    /// call gated on the write itself, zero cost when the flag is off since the whole body above
    /// already returns before this point on every non-firing call).
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
    /// ROUND 88 (2026-08-24, party-reconciliation.md): a THIRD, independent permanent-stall
    /// shape, found live-reproducing round 84's own "CreMoveState reverts from -1 to 3" recipe
    /// again this round — but the ACTUAL live data showed a DIFFERENT mechanism than round 84
    /// described: <c>CreMoveState</c> never reverted from -1 at all in this round's own repro
    /// (it stayed pinned at 3 for the entire ~35-second stalled tail; a genuine, single-frame
    /// -1 blip WAS reached, went straight to phase=2, and never touched -1 again). Round 84's
    /// own "0x00511130" ownership-check mechanism (see <see cref="FollowMoveGateCheckFn"/>) was
    /// live-measured this round to fire (<c>gateFires=true</c>) CONSTANTLY, even during genuine
    /// smooth walking, so it is not what is timing this specific stall — a real mechanism, and
    /// possibly still the cause of round 84's own separately-observed symptom, but not the one
    /// this round's own live data implicates.
    ///
    /// Full disassembly of handler 0x00511860 (dispatch phase 2, never examined by this document
    /// before this round) instead: it computes the distance between <c>PriorPoint</c>
    /// (<c>record+0x18</c>) and <c>Snapshot</c> (<c>record+0x5c</c>), squares it, and compares
    /// against a threshold built from <c>*(float*)followData</c> (the FIRST dword of the whole,
    /// party-wide, embedded <c>CSWPartyFollowDataArray</c> struct — read here as
    /// <see cref="FollowDiag.MoveBudget"/>) times the constant at 0x746f4c (0.7), squared. When
    /// PriorPoint == Snapshot (a zero-length remaining segment — confirmed live: reads bit-identical
    /// across 30+ consecutive 25 Hz samples spanning the entire stall) this comparison is
    /// trivially satisfied (0 &lt;= any positive threshold), which looked, at first, like it should
    /// unconditionally commit phase 3 (<c>Moving_To_Path</c>) — but does NOT: the "close enough"
    /// branch (0x51191B) only sets a LOCAL variable (<c>ebp = 3</c>) that is not actually written
    /// into <c>record+4</c> until much later (0x511ACD), gated behind
    /// <c>CSWSArea::TestDirectLine</c> (0x004BCB70, called via <c>CSWSObject::GetArea</c>
    /// 0x004CB120's returned area pointer as <c>this</c>) returning EXACTLY 1. If TestDirectLine
    /// returns anything else (live data: this happens on essentially every call once parked in
    /// this exact PriorPoint==Snapshot shape), the function instead pool-allocates a small object
    /// and queues a re-check via <c>CServerAIMaster::AddEventDeltaTime</c> (0x004B08D0) —
    /// deferring to a LATER engine tick — and <c>record+4</c> (phase) is left completely
    /// untouched. The requeued event apparently never resolves favourably either: live-measured,
    /// this cycles (phase 2, <c>attempts</c> climbing 0-50, the engine's own watchdog forcing
    /// phase -1, <see cref="TryResetStuckFollowPhase"/> immediately restarting FindPath, which
    /// re-derives the SAME degenerate phase-2 input) for 30+ seconds straight with zero position
    /// change and zero further engine progress, in one live repro.
    ///
    /// This targets exactly that degenerate shape: phase parked at 2 with PriorPoint and Snapshot
    /// already equal (this tick's own "close enough" condition is provably already satisfied —
    /// the ENGINE's own 0.7x-budget threshold squared against a genuinely ZERO distance can never
    /// be the reason this fails) and the watchdog already having made at least one real pass
    /// (<c>attempts &gt; 0</c>, so this never fires on the very first, still-legitimate visit to
    /// phase 2). Commits phase 3 directly — the SAME value the engine's own "close enough" branch
    /// would have written had TestDirectLine returned 1 — WITHOUT calling TestDirectLine or
    /// touching followData's own budget field (unnecessary: the engine's own budget subtraction is
    /// <c>sqrt(distance) * 1.4285714</c>, and distance is already zero). This does not bypass any
    /// actual movement/collision validation: Moving_To_Path's OWN "close enough to the next
    /// recorded node" gate (round 84's own citation, 0x4AA980) still runs on every subsequent
    /// call and is what decides whether <c>CurrentPathNode</c> is really allowed to advance — this
    /// fix only unblocks the FSM from being stuck one phase EARLIER than that check, on a shortcut
    /// optimisation test whose own failure path leads nowhere for a companion in this exact
    /// coop-driven state (round 87's own leading theory, matching round 80/84's own established
    /// pattern: this plugin's own direct-call drive loop most likely bypasses whatever the async
    /// AddEventDeltaTime callback needs from the engine's normal per-frame tick to eventually
    /// resolve favourably — not chased further this round).
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether the
    /// stall shape was actually observed and corrected this call. Kill switch:
    /// <c>party_follow_advance_movingtopath=0</c> (default on).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

}

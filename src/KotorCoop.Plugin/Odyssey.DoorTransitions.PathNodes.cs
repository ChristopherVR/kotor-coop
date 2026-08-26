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
    /// Cheap, read-only accessor for <c>record+0x6C</c>
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
    /// When a companion is parked at phase 3 (<c>Moving_To_Path</c>, handler 0x00511B60) with its
    /// own accumulator (<c>record+0x6C</c>) genuinely advancing, but real position never changes
    /// and <see cref="FollowDiag.CreMoveState"/> (creature+0xA8C) reads 3 ("follow requested")
    /// rather than -1 ("walking"): the revert is <see cref="FollowMoveGateCheckFn"/> (0x004CB120,
    /// called from 0x00511130 — see that delegate's own remarks) firing from a separate per-object
    /// engine dispatch (0x0057F79E) this plugin does not control and cannot suppress or race.
    /// Re-asserting <c>CreMoveState = -1</c> directly was live-tested and measured insufficient
    /// alone: the poke sticks but real position never resumes. Only <c>FindPath</c> (0x51AC10,
    /// phase 0, dispatch index 1) both resubmits a real move action and sets
    /// <c>CreMoveState = -1</c> together, in the same shared epilogue, so this instead forces
    /// phase back to 0, letting the engine's own FindPath handler do both jobs on the controller's
    /// very next <see cref="TryDriveFollowLeaderStep"/> call.
    ///
    /// Fires only when <c>Moving_To_Path</c>'s own handler is confirmed genuinely running (its
    /// accumulator has advanced since the caller's last sample) AND real, unconsumed trail remains
    /// (<c>CurrentPathNode &lt; WriteCursor</c>) AND <c>CreMoveState</c> currently reads something
    /// other than -1. See docs/research/party-reconciliation.md.
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

        // Force phase to 0 (FindPath), not CreMoveState directly — see this method's own
        // remarks for why the CreMoveState-only version measured insufficient. record+0x44
        // (attempts) is left alone: this is a resubmission, not a reset.
        *(int*)(record + 4) = 0;
        wrote = true;
        return true;
    }

    /// <summary>
    /// Cheap, read-only accessor for <c>record+4</c> (phase) and <c>record+0x24</c>
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
    /// A narrower stall found downstream of
    /// <see cref="TryAdvanceStalledMovingToPath"/>/<see cref="TryForceWalkingWhenMovingToPathStalled"/>:
    /// a companion closes the entire distance of a real leg down to the very last recorded trail
    /// node (<c>writeCursor - pathNode == 1</c>) before parking permanently at phase 7
    /// (<c>PathWaitDest</c>, 0x00512B40). <c>PathWaitDest</c>'s forward search tests
    /// <c>CurrentPathNode</c> against <c>DestNode</c> before incrementing, so when they are
    /// already equal (this shape) it falls into an accept/reject test that looks up the recorded
    /// point at that index and accepts only when it is strictly closer to <c>TargetPoint</c>
    /// (<c>record+0x28</c>) than to <c>PriorPoint</c> (<c>record+0x18</c>) — a "has this waypoint
    /// been passed yet" test that was live-confirmed to never resolve favourably here.
    /// <see cref="TryAdvanceStalledPathDestination"/> cannot reach this case: at
    /// <c>gap == 1</c>, <c>WriteCursor</c> is the only other in-range value <c>DestNode</c> could
    /// be set to, and that value is structurally unreachable by <c>PathWaitDest</c>'s own search
    /// loop.
    ///
    /// This instead commits phase 3 directly — the same value the unconditional-accept branch
    /// commits — once the shape has held for several consecutive frames.
    ///
    /// LIVE-TESTED AND DISPROVEN: forcing phase 3 here does not produce real motion. It opens a
    /// new, different unproductive cycle together with
    /// <see cref="TryForceWalkingWhenMovingToPathStalled"/> — phase cycles 7 -&gt; 3 -&gt; 0
    /// (FindPath) -&gt; 7 roughly every 0.3-0.5s, <c>CreMoveState</c> settles at 1 ("idle"), and
    /// real position stays frozen for 30+ seconds — worse than the shape this was meant to fix.
    /// <see cref="PartyFollowController"/> defaults this OFF; the function is kept, named, and
    /// documented as a ruled-out avenue rather than deleted.
    ///
    /// A follow-up disassembly pass confirmed the accept branch writes nothing besides
    /// <c>record+4=3</c> — its return value of <c>1</c> only tells the engine's OWN dispatcher to
    /// redispatch same-call, a generic mechanism this out-of-dispatcher poke does not get, but two
    /// sibling pokes that also miss that same-call cascade are live-verified to work fine, so that
    /// is not the cause of the disproven cycling. The leading remaining hypothesis is that
    /// <c>PathWaitDest</c>'s own geometric accept/reject test is correctly rejecting — not
    /// spuriously blocking — because <c>TargetPoint</c>/<c>PriorPoint</c> go stale (frozen, not
    /// refreshed by the engine's own <c>CSWParty::Update</c>/<c>RecaulateFollowPoint</c>) for a
    /// companion parked in this stall shape, so forcing the accept hands <c>Moving_To_Path</c> a
    /// segment with no honest remaining distance to walk. See
    /// <see cref="FollowDiag.RecordedDestPoint"/> (added to let a live round compare the recorded
    /// node against <c>TargetPoint</c>/<c>PriorPoint</c> at the exact stall moment) and
    /// docs/research/party-reconciliation.md for the full disassembly trace, the refuted
    /// alternative theories, and live-repro data.
    ///
    /// This function's own behaviour is unchanged (still exactly <c>*(int*)(record+4) = 3</c>).
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
    /// Historical (no longer wired to a live method here — see
    /// <see cref="TryRefreshFinalNodeTarget"/> in Odyssey.DoorTransitions.FinalNode.cs):
    /// documents a fix candidate built on the theory that <c>PathWaitDest</c>'s geometric
    /// accept/reject test may be rejecting correctly at the last recorded trail node because
    /// <c>TargetPoint</c>/<c>PriorPoint</c> are stale, not because the test itself is broken.
    /// Rather than forcing the test's output (phase=3, live-disproven — see
    /// <see cref="TryAcceptFinalPathNode"/>), it refreshed the test's inputs — the same
    /// non-degenerate Prior/Target write <see cref="TryDriveFollowTarget"/> ships, but bypassing
    /// that function's ~10cm gate — and let the engine's own accept/reject logic decide, only
    /// inside the same narrow terminal shape <see cref="TryAcceptFinalPathNode"/> detects.
    ///
    /// LIVE-TESTED, REFUTED: in the standard short-leg repro, <c>target</c> was already
    /// bit-identical to the live leader position before this could ever fire, and stayed that way
    /// for 76+ continuous seconds — so staleness was not the blocker in that repro, and phase
    /// never advanced past 7 regardless. No regression observed. Kept, default OFF. A longer leg
    /// where the leader is still taking small steps as the companion arrives at phase 7 was not
    /// tried and remains a possible confound. See docs/research/party-reconciliation.md.
    ///
    /// Returns false when the index does not resolve. <paramref name="wrote"/> reports whether
    /// the refresh actually fired this call. Kill switch (to enable):
    /// <c>party_follow_refresh_final_node=1</c> (default OFF).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

}

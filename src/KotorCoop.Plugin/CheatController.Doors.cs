using System.Text;

using static KotorCoop.Plugin.Vec3;

namespace KotorCoop.Plugin;

// Door verbs: UseDoor (walk-through transition instrument), CrossDoor (the no-walk
// event-posting instrument) and OpenDoor (ordinary open), plus the walk retry/watch machinery
// that carries a queued walk through a door and reports where it actually ended up, the
// crossdoor watch that reports whether a posted event ever triggered a transition, and the
// door-sweep/resolve/open/gather helper all three transition-adjacent verbs share.
internal sealed unsafe partial class CheatController
{
    /// <summary>
    /// How many frames a refused walk keeps being retried. `Odyssey.MoveToLocation` declines
    /// with failure 4 while the script VM is mid-call — transient, frame-shaped, and the same
    /// refusal that orphaned a phantom on teardown until destroys were retried. Two seconds of
    /// frames is far more than a VM call lasts and still bounded.
    /// </summary>
    private const int WalkRetryFrames = 120;

    /// <summary>
    /// How long to watch a queued walk before reporting where it actually ended up. Ten
    /// seconds at 60 fps: far longer than a four-metre walk, and the watch is cancelled the
    /// moment the transition it was staging happens.
    /// </summary>
    private const int WalkWatchFrames = 600;

    /// <summary>
    /// How far into the watch to check whether the walk has stalled and, if so, reissue it
    /// once. Three seconds at 60 fps: comfortably longer than the walk itself should take, so
    /// this fires only once the accepted walk has had every reasonable chance to have already
    /// crossed. Repeated live runs at coordinates that had worked before still measured the
    /// walk stopping 0.9-2.4 m short of the door despite <see cref="DoorUse.WalkThroughOvershoot"/>
    /// being enlarged for exactly this — the path-arrival tolerance (or a corner the
    /// pathfinder cuts near the doorway) can eat a queued walk before the crossing event
    /// fires, and nothing else re-issues it. This is the retry that does.
    /// </summary>
    private const int WalkStallCheckFrame = 180;

    private uint _walkDoorId;
    private Vec3 _walkTarget;
    private int _walkFramesLeft;
    private int _walkWatchFrames;
    private float _walkStartDistance;

    /// <summary>
    /// How long to watch a posted crossing event before reporting whether it ever moved the
    /// engine's own <c>trans_pending</c> flag. Two seconds at 60 fps: <c>CSWSDoor::EventHandler</c>
    /// tests <c>IsPartyTogether</c> and raises <c>SetTransitionPending</c> synchronously inside
    /// the SAME <c>AddEventDeltaTime</c> drain that services the posted event — there is no
    /// walk to wait on here, so this window exists purely to catch a delayed AI-master drain
    /// rather than a slow crossing.
    /// </summary>
    private const int CrossWatchFrames = 120;

    private uint _crossDoorId;
    private int _crossWatchFramesLeft;
    private bool _crossTransitionSeenPending;

    /// <summary>
    /// Queues the walk that carries the player through the door, and reports the engine's own
    /// refusal code rather than a boolean.
    /// </summary>
    private bool TryWalkThroughDoor(out int failure)
    {
        failure = 0;

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            failure = 1;
            return false;
        }

        return Odyssey.MoveToLocation(playerId, _walkTarget, run: true, out failure);
    }

    /// <summary>
    /// Retries a walk the VM was too busy to accept. Runs every frame, does nothing on almost
    /// all of them, and — this is the point — SAYS SO when it gives up. A staging verb that
    /// reports "walking" and then quietly stops trying is the exact silent-guard shape that has
    /// cost this project several rounds.
    /// </summary>
    private void RetryPendingDoorWalk()
    {
        if (_walkFramesLeft <= 0)
        {
            return;
        }

        _walkFramesLeft--;

        if (TryWalkThroughDoor(out int failure))
        {
            PluginHost.Log(
                $"door: the walk through 0x{_walkDoorId:X} was accepted after "
                + $"{WalkRetryFrames - _walkFramesLeft} retries");
            _walkFramesLeft = 0;
            return;
        }

        if (_walkFramesLeft == 0)
        {
            PluginHost.Log(
                $"door: gave up queueing the walk through 0x{_walkDoorId:X} after "
                + $"{WalkRetryFrames} frames (last failure {failure}) - nobody is walking anywhere");
            _walkWatchFrames = 0;
        }
    }

    /// <summary>
    /// Says where the walk actually ENDED, once, some seconds after it was queued.
    ///
    /// This exists because of a specific ambiguity that cost a live round: the verb reported
    /// "walking through door 0x4A" and the run produced no transition, and nothing anywhere
    /// could distinguish "it walked into a shut door and stopped at the face" from "it mis-aimed
    /// and walked somewhere else". The answer turned out to be the first, and it took a manual
    /// `where` and a coordinate subtraction to find out. A staging verb that stages something
    /// unobservable has staged nothing.
    ///
    /// It reports only on the frame the watch expires, and it is cancelled by
    /// <see cref="Invalidate"/> — so the success case, where the module changes and the epoch
    /// resets everything, prints nothing at all.
    /// </summary>
    private void WatchDoorWalk()
    {
        if (_walkWatchFrames <= 0)
        {
            return;
        }

        if (_walkWatchFrames == WalkWatchFrames - WalkStallCheckFrame)
        {
            RetryStalledDoorWalk();
        }

        if (--_walkWatchFrames > 0)
        {
            return;
        }

        nint door = Odyssey.ResolveDoor(_walkDoorId);
        nint player = Odyssey.GetPlayerCreaturePointer();

        if (door == 0 || player == 0)
        {
            PluginHost.Log(
                $"door: {WalkWatchFrames} frames after the walk, door 0x{_walkDoorId:X} or the "
                + "player no longer resolves here - something moved worlds");
            return;
        }

        Vec3 here = Odyssey.GetPosition(player);
        float now = Distance(here, Odyssey.GetPosition(door));

        PluginHost.Log(
            $"door: {WalkWatchFrames} frames after the walk the player is {now:F1}m from "
            + $"0x{_walkDoorId:X} (was {_walkStartDistance:F1}m), at "
            + $"{here.X:F1},{here.Y:F1}; aimed at {_walkTarget.X:F1},{_walkTarget.Y:F1}. "
            + "No transition happened - the door is still an obstacle, or the path stopped short.");
    }

    /// <summary>
    /// Fires once, partway through the watch: if the walk has not carried the player near the
    /// door by then, reissue it to the same target. A transition that already happened has
    /// already zeroed <see cref="_walkWatchFrames"/> via <see cref="Invalidate"/>, so this
    /// never fires after a success — only while something queued is still, apparently, going
    /// nowhere.
    /// </summary>
    private void RetryStalledDoorWalk()
    {
        nint door = Odyssey.ResolveDoor(_walkDoorId);
        nint player = Odyssey.GetPlayerCreaturePointer();

        if (door == 0 || player == 0)
        {
            // Already gone from this world one way or another; the frame-0 report (if the
            // watch is still running at all) says so.
            return;
        }

        float now = Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(door));
        if (now <= 1.0f)
        {
            // Close enough that the walk is either about to cross or already did and the
            // transition just has not landed on this frame yet - do not fight it.
            return;
        }

        uint playerId = Odyssey.GetPlayerObjectId();
        if (playerId == 0)
        {
            return;
        }

        bool accepted = Odyssey.MoveToLocation(playerId, _walkTarget, run: true, out int failure);
        PluginHost.Log(accepted
            ? $"door: {WalkStallCheckFrame} frames in, still {now:F1}m from 0x{_walkDoorId:X} - "
              + "reissued the walk to the same point"
            : $"door: {WalkStallCheckFrame} frames in, still {now:F1}m from 0x{_walkDoorId:X}, "
              + $"and the reissue was refused (failure {failure})");
    }

    /// <summary>
    /// Says whether a posted crossing event ever moved the engine — the negative report
    /// <see cref="CrossDoor"/>'s own doc comment requires, mirroring <see cref="WatchDoorWalk"/>'s
    /// reasoning exactly: a staging verb that stages something unobservable has staged nothing.
    ///
    /// A SUCCESSFUL transition never reaches the bottom of this function at all — the module
    /// change runs <see cref="Invalidate"/>, which zeroes <see cref="_crossWatchFramesLeft"/>
    /// first, so this prints nothing on success, exactly like <see cref="WatchDoorWalk"/>.
    /// Reaching the end of the window is therefore itself informative: either the engine's own
    /// <c>trans_pending</c> flag was raised and then reverted (the party-together gate can do
    /// this — see <see cref="GatherPartyIfScattered"/>'s own doc comment for
    /// <c>k_trg_transfail</c>) or it never moved at all, and the two are told apart.
    /// </summary>
    private void WatchDoorCrossing()
    {
        if (_crossWatchFramesLeft <= 0)
        {
            return;
        }

        if (Odyssey.IsAreaTransitionPending())
        {
            _crossTransitionSeenPending = true;
        }

        if (--_crossWatchFramesLeft > 0)
        {
            return;
        }

        PluginHost.Log(_crossTransitionSeenPending
            ? $"door: {CrossWatchFrames} frames after crossdoor posted event 0x1E for "
              + $"0x{_crossDoorId:X}, IsAreaTransitionPending() DID go true at some point but "
              + "the module never changed - the transition started and then reverted or stalled"
            : $"door: {CrossWatchFrames} frames after crossdoor posted event 0x1E for "
              + $"0x{_crossDoorId:X}, IsAreaTransitionPending() never went true - the event was "
              + "posted but no transition followed");
    }

    /// <summary>Door open state 0 = closed. 1 and 2 are the two swing directions, 3 is
    /// destroyed (see <c>Odyssey.GetDoorOpenState</c>).</summary>
    private const byte DoorClosedState = 0;

    /// <summary>Doors one module can hold. The Endar Spire's busiest area has a dozen; a
    /// hundred and twenty-eight is a ceiling, not an expectation, and the sweep stops there
    /// rather than writing past the buffer.</summary>
    private const int DoorSweepCapacity = 128;

    /// <summary>
    /// Resolves the door <c>usedoor</c>/<c>crossdoor</c> was asked for (by id, by tag, or "the
    /// nearest transition door"), runs it through <see cref="DoorUse.Decide"/>, logs the entry
    /// line, opens it if it is shut, and gathers the party if it is scattered — everything both
    /// verbs need identically before they diverge into "queue a walk THROUGH the door" and
    /// "post the door's own crossing event directly". Refactored out of what used to be
    /// <c>UseDoor</c>'s own body once <c>crossdoor</c> needed the exact same hundred lines: a
    /// second copy of a door-resolution guard is a guard that can drift out of sync with the
    /// one that is actually tested.
    ///
    /// A CLOSED DOOR IS A WALL either way. Measured live for the walk case: it ran and stopped
    /// 1.8 m short of the door face, because the closed door is a pathing obstacle and the path
    /// ended at it. <c>crossdoor</c> needs the door open for an entirely different reason — see
    /// <see cref="Odyssey.PostDoorCrossingEvent"/>'s own header, which builds event **0x1E**
    /// specifically from AIActionMoveToPoint's `[door+0x2CC] != 0` (already-open) branch; a
    /// closed door takes the OTHER branch and builds a different event (0x1F) entirely. So both
    /// verbs open it first, through the engine's own OpenDoor — which picks the swing side from
    /// where the opener stands, moves the linked twin, and runs the OnOpen script — and commit
    /// the swing at once so the walkmesh (and the open-state byte the primitive reads) is
    /// current on this very frame.
    ///
    /// <paramref name="purpose"/> only flavours the two open/refuse-to-open log lines (e.g.
    /// "the walk" vs. "the crossing"); everything else is caller-agnostic. Answers and returns
    /// false when the guard refuses — the caller does nothing further in that case, matching
    /// how the old single-verb version returned directly from inside its own guard.
    /// </summary>
    private bool ResolveOpenAndGatherForDoorTransition(
        nint player,
        uint playerId,
        string purpose,
        out nint door,
        out uint doorId,
        out string module,
        out string entry,
        out float distance,
        out bool partyTogether)
    {
        door = 0;
        doorId = 0;
        module = string.Empty;
        entry = string.Empty;
        distance = 0f;
        partyTogether = false;

        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        string what;
        int doorsSeen = 0;
        int linkedSeen = 0;

        if (requestedId != 0)
        {
            // ResolveDoor is vtable-checked, so a non-door id comes back as 0 — which would
            // read as "missing". Resolve generically first so the answer can tell a wrong id
            // from a right id naming the wrong kind of thing.
            door = Odyssey.ResolveDoor(requestedId);
            what = $"0x{requestedId:X}";

            if (door == 0 && Odyssey.ResolveServerObject(requestedId) != 0)
            {
                Answer(
                    SharedBlock.CheatStatusNoTarget,
                    DoorUse.Explain(DoorUseVerdict.RefuseNotADoor, what, 0f));
                return false;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            door = Odyssey.FindDoorByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            door = FindNearestTransitionDoor(player, out doorsSeen, out linkedSeen);
            what = "the nearest transition door";
        }

        distance = door != 0 ? Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(door)) : 0f;

        DoorUseVerdict verdict = DoorUse.Decide(
            hasPlayer: player != 0,
            found: door != 0,
            isDoor: door != 0,
            linked: door != 0 && Odyssey.DoorIsLinked(door),
            openState: door != 0 ? Odyssey.GetDoorOpenState(door) : 0,
            transitionPending: Odyssey.IsAreaTransitionPending(),
            moviePlaying: Odyssey.IsMoviePlaying(),
            distance: distance);

        if (verdict != DoorUseVerdict.Use)
        {
            string detail = DoorUse.Explain(verdict, what, distance);

            // A failed sweep has to say what it swept. "No transition door here" is the wrong
            // module; "no doors at all" is a broken sweep, and a reader with neither number
            // cannot tell them apart.
            if (verdict == DoorUseVerdict.RefuseMissing && requestedId == 0 && text.Length == 0)
            {
                detail = DoorUse.DescribeSweep(doorsSeen, linkedSeen);
            }

            // The link flags are the one refusal worth the extra byte: 0 means the module data
            // never linked this door, which is a fact about the module and not about us.
            if (verdict == DoorUseVerdict.RefuseNotLinked)
            {
                PluginHost.Log(
                    $"door: refusing {purpose} of {what} - linked_to_flags reads "
                    + $"{Odyssey.GetDoorLinkFlags(door)} (1 or 2 would be a module transition)");
            }

            Answer(SharedBlock.CheatStatusRefused, detail);
            return false;
        }

        doorId = *(uint*)(door + 0x04);

        byte* destination = stackalloc byte[SharedBlock.TravelNameLength];
        int destinationLength = Odyssey.ReadDoorLinkedModule(door, destination, SharedBlock.TravelNameLength);
        module = destinationLength > 0
            ? Encoding.ASCII.GetString(destination, destinationLength)
            : "(unreadable)";

        byte* waypoint = stackalloc byte[SharedBlock.TravelNameLength];
        int waypointLength = Odyssey.ReadDoorLinkedWaypoint(door, waypoint, SharedBlock.TravelNameLength);
        entry = waypointLength > 0
            ? Encoding.ASCII.GetString(waypoint, waypointLength)
            : "(none)";

        // Logged BEFORE anything is done, because what follows may take the main thread away:
        // if a movie is queued, the transition parks it inside WaitForMovie and nothing written
        // afterwards would survive the window to be written at all. The OPEN STATE is part of
        // the line for a reason paid for live - a walk that stopped short was indistinguishable
        // from a walk that mis-aimed, and that ambiguity cost a whole round.
        byte openState = Odyssey.GetDoorOpenState(door);
        PluginHost.Log(
            $"door: 0x{doorId:X} at {distance:F1}m links to module '{module}' at waypoint "
            + $"'{entry}'; open state {openState}"
            + (Odyssey.IsDoorLocked(door) ? ", LOCKED" : string.Empty));

        if (openState == DoorClosedState)
        {
            byte opened = Odyssey.OpenDoorFully(door, playerId);

            PluginHost.Log(opened != DoorClosedState
                ? $"door: opened 0x{doorId:X} (state {openState} -> {opened}) so {purpose} has "
                  + "somewhere to go"
                : $"door: 0x{doorId:X} REFUSED to open (still state {opened}) - {purpose} will "
                  + "fail at its face, as it did before");
        }

        partyTogether = GatherPartyIfScattered();
        return true;
    }

    /// <summary>
    /// Makes the local player USE a door, so the ENGINE starts a module transition.
    ///
    /// This is the other half of the cinematic instrument, and the half that can actually
    /// answer the question. <c>moviewarp</c> stages a movie onto a transition the PLUGIN
    /// starts — and on that path <see cref="PluginHost"/> has already destroyed every spawned
    /// body before it raises the move-to-module flag, so the pre-store flush window is never
    /// needed and never tested. A door is the opposite: <c>CSWSDoor::EventHandler</c> raises
    /// <c>CSWSArea::SetTransitionPending(1, id)</c> and starts a half-second fade FIRST
    /// (0x0058C2A0), and only when that fade completes does it do the move-to-module calls
    /// (0x0058C185). Those intervening frames are the entire flush window, and a movie playing
    /// across them is the case docs/research/cinematics.md has never been able to stage.
    ///
    /// ⚠ **THE VERB WALKS THROUGH THE DOOR; IT DOES NOT USE IT**, and the name is a historical
    /// artefact kept because live runs are staged against it. Using a transition door provably
    /// cannot transition, and the binary is unambiguous about why. `CSWSDoor::EventHandler`
    /// switches on the event's type word through the jump table at 0x0058C594/0x0058C5C8, and
    /// only **event 0x1E** reaches the transition arm at 0x0058C0BE (GetIsLinked, then
    /// `linked_to_module` length, then the `trans_pending` test that forks into phase 1 or
    /// phase 2). Using an object posts **0x1A** — both `AIActionUseObject` (0x0057ECC0) and
    /// `AIActionOpenDoor` (0x0057D661) build that type — which lands at 0x0058BD62, the arm
    /// that runs the door's OnUsed script and nothing else.
    ///
    /// Event 0x1E is built in exactly five functions, and every one of them is MOVEMENT:
    /// `CSWSCreature::HandleSubAreaStateChanges` (0x00516020, three sites),
    /// `WalkUpdateLocation` (0x0051BB10), `DriveUpdateLocation` (0x0051CD20),
    /// `WalkUpdateLocation_QuickWalk` (0x0051D9C0) and `AIActionMoveToPoint` (0x0051F4F0).
    /// A KOTOR module transition is a creature CROSSING INTO the door's sub-area, noticed by
    /// its own movement update — which is also why `tp` onto the door does nothing: a
    /// `SetPosition` snap runs no movement update, so nothing ever notices the crossing.
    /// (`HandleSubAreaStateChanges` additionally builds the event only for party index 0, i.e.
    /// the leader — 0x00516263 — which agrees with the door's own `GetIsLeader` check.)
    ///
    /// So this queues `ActionMoveToLocation` to a point just PAST the door
    /// (<see cref="DoorUse.WalkThroughPoint"/>) and lets `AIActionMoveToPoint` post the event
    /// as the player crosses. Everything before it is guard: <see cref="DoorUse.Decide"/> gets
    /// facts measured on this frame and nothing else, and every refusal names what it saw.
    ///
    /// ⚠ Live-verified stalling: the walk stops 1.8-5.9 m short of the door across three
    /// separate rounds, so this leaves the door's OWN transition arm still unexercised by a
    /// real crossing. <see cref="CrossDoor"/> is the instrument built to close that gap.
    /// </summary>
    private void UseDoor(nint player, uint playerId)
    {
        if (!ResolveOpenAndGatherForDoorTransition(
                player, playerId, "the walk",
                out nint door, out uint doorId, out string module, out string entry,
                out float distance, out bool together))
        {
            return;
        }

        // WALK THROUGH IT. Not "use" it — see this method's own doc comment for why using a
        // transition door provably cannot transition.
        Vec3 through = DoorUse.WalkThroughPoint(
            Odyssey.GetPosition(player),
            Odyssey.GetPosition(door),
            Odyssey.GetOrientation(door),
            DoorUse.WalkThroughOvershoot);

        _walkDoorId = doorId;
        _walkTarget = through;
        _walkFramesLeft = WalkRetryFrames;
        _walkWatchFrames = WalkWatchFrames;
        _walkStartDistance = distance;

        if (!TryWalkThroughDoor(out int failure))
        {
            // Failure 4 is the script VM being mid-call, which is transient and frame-shaped —
            // the same refusal the teardown destroys hit. Keep the retry armed and say so
            // rather than reporting a walk that never started.
            Answer(
                failure == 4 ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
                failure == 4
                    ? $"door 0x{doorId:X}: VM busy, retrying the walk for {WalkRetryFrames} frames"
                    : $"the engine refused the walk to door 0x{doorId:X} (failure {failure})");

            if (failure != 4)
            {
                _walkFramesLeft = 0;
                _walkWatchFrames = 0;
            }

            return;
        }

        // Accepted first time: nothing left to retry.
        _walkFramesLeft = 0;

        Answer(
            SharedBlock.CheatStatusApplied,
            together
                ? $"walking through door 0x{doorId:X} ({distance:F1}m) -> '{module}'"
                : $"door 0x{doorId:X} -> '{module}' BUT the party is not together");
    }

    /// <summary>
    /// Posts the door's own crossing event (type <c>0x1E</c>) DIRECTLY, instead of queuing a
    /// walk and hoping the pathfinder actually reaches the threshold — the exact dependency
    /// that has stalled <see cref="UseDoor"/>'s walk 1.8-5.9 m short of the door face on every
    /// live attempt across three separate rounds (docs/research/cinematics.md §8.3b). Same
    /// resolution, same open-if-shut step, same party gather as <see cref="UseDoor"/> (through
    /// <see cref="ResolveOpenAndGatherForDoorTransition"/>) — the two verbs diverge only at the
    /// very last step: this one calls <see cref="Odyssey.PostDoorCrossingEvent"/> instead of
    /// queuing <c>ActionMoveToLocation</c>.
    ///
    /// The point is to exercise <c>CSWSDoor::EventHandler</c>'s own transition arm
    /// (0x0058C0BE) — GetIsLinked, then `linked_to_module` length, then the `trans_pending`
    /// fork — the SAME arm a real crossing reaches, without depending on the walk ever landing.
    /// The door's transition arm additionally requires the posting creature to be the party
    /// LEADER (`GetIsLeader` at 0x0058C206); the local player normally is, and the party index
    /// is logged (via <see cref="Odyssey.TryGetClientPartyIndex"/>) so a refusal traced back to
    /// that check has the evidence already in the log rather than needing a second round to
    /// gather it.
    ///
    /// Logged BEFORE posting, matching <see cref="UseDoor"/>'s own reasoning: the event may
    /// take the main thread away the instant it is serviced, and anything written after that
    /// point would not survive to be written at all.
    /// </summary>
    private void CrossDoor(nint player, uint playerId)
    {
        if (!ResolveOpenAndGatherForDoorTransition(
                player, playerId, "the crossing",
                out _, out uint doorId, out string module, out string entry,
                out float distance, out bool together))
        {
            return;
        }

        bool haveIndex = Odyssey.TryGetClientPartyIndex(playerId, out int partyIndex);
        PluginHost.Log(
            $"door: crossing 0x{doorId:X} ({distance:F1}m) -> '{module}' (waypoint '{entry}') "
            + $"posted as event 0x1E from the PC (party index "
            + $"{(haveIndex ? partyIndex.ToString() : "unreadable")}), not walked");

        if (!Odyssey.PostDoorCrossingEvent(playerId, doorId, out string detail))
        {
            Answer(SharedBlock.CheatStatusEngineRefused, detail);
            return;
        }

        _crossDoorId = doorId;
        _crossWatchFramesLeft = CrossWatchFrames;
        _crossTransitionSeenPending = false;

        Answer(
            SharedBlock.CheatStatusApplied,
            together
                ? $"posted the crossing event for door 0x{doorId:X} -> '{module}' (waypoint '{entry}')"
                : $"door 0x{doorId:X} -> '{module}' BUT the party is not together");
    }

    /// <summary>
    /// Opens an ORDINARY door — one <c>usedoor</c> refuses by design (see its own doc comment:
    /// a bare <c>usedoor</c> must be incapable of doing anything except starting a module
    /// transition). Built because no working <c>walk</c>/click primitive could otherwise reach
    /// a non-transition door for testing (docs/research/shared-state-audit.md, round 56/57).
    ///
    /// Calls <see cref="Odyssey.OpenDoorFully"/> — the SAME engine door-use action
    /// <see cref="UseDoor"/> already calls before its walk — so the ordinary open-state mirror
    /// (<c>DoorController</c>) is what actually gets exercised, not a poke of the open-state
    /// byte. Per <see cref="UseDoor"/>'s own finding, an engine "open" call posts event 0x1A
    /// (OnUsed), never 0x1E (the movement-crossing transition event) — so this is safe to
    /// point at a LINKED door too; it will only ever open it, never transition it.
    ///
    /// Deliberately does not walk the player through it or gather the party: an ordinary door
    /// has nowhere to transition to, so neither concern applies.
    /// </summary>
    private void OpenDoor(nint player, uint playerId)
    {
        uint requestedId = block.CheatTarget;
        string text = ReadCheatText();

        nint door;
        string what;
        int doorsSeen = 0;

        if (requestedId != 0)
        {
            door = Odyssey.ResolveDoor(requestedId);
            what = $"0x{requestedId:X}";

            if (door == 0 && Odyssey.ResolveServerObject(requestedId) != 0)
            {
                Answer(
                    SharedBlock.CheatStatusNoTarget,
                    DoorUse.Explain(DoorUseVerdict.RefuseNotADoor, what, 0f));
                return;
            }
        }
        else if (text.Length > 0)
        {
            byte* tag = block.CheatText;
            int length = 0;
            while (length < SharedBlock.CheatTextLength - 1 && tag[length] != 0)
            {
                length++;
            }

            door = Odyssey.FindDoorByTag(tag, length);
            what = $"'{text}'";
        }
        else
        {
            door = FindNearestAnyDoor(player, out doorsSeen);
            what = "the nearest door";
        }

        float distance = door != 0 ? Distance(Odyssey.GetPosition(player), Odyssey.GetPosition(door)) : 0f;

        // Same decision table usedoor uses, with `linked` forced true: that gate exists
        // specifically to keep a bare `usedoor` incapable of opening an ordinary door, and
        // opening an ordinary door is this verb's entire purpose.
        DoorUseVerdict verdict = DoorUse.Decide(
            hasPlayer: player != 0,
            found: door != 0,
            isDoor: door != 0,
            linked: true,
            openState: door != 0 ? Odyssey.GetDoorOpenState(door) : 0,
            transitionPending: Odyssey.IsAreaTransitionPending(),
            moviePlaying: Odyssey.IsMoviePlaying(),
            distance: distance);

        if (verdict != DoorUseVerdict.Use)
        {
            string detail = DoorUse.Explain(verdict, what, distance);

            if (verdict == DoorUseVerdict.RefuseMissing && requestedId == 0 && text.Length == 0)
            {
                detail = doorsSeen == 0
                    ? "the sweep found no doors at all in this module"
                    : $"the sweep found {doorsSeen} door(s) but none close enough to open";
            }

            Answer(SharedBlock.CheatStatusRefused, detail);
            return;
        }

        uint doorId = *(uint*)(door + 0x04);
        byte before = Odyssey.GetDoorOpenState(door);
        byte after = Odyssey.OpenDoorFully(door, playerId);

        PluginHost.Log(
            $"door: opened 0x{doorId:X} (state {before} -> {after}) at {distance:F1}m"
            + (Odyssey.IsDoorLocked(door) ? ", LOCKED" : string.Empty));

        Answer(
            after != before ? SharedBlock.CheatStatusApplied : SharedBlock.CheatStatusEngineRefused,
            after != before
                ? $"door 0x{doorId:X}: opened (state {before} -> {after})"
                : $"door 0x{doorId:X}: REFUSED to open (still state {after})");
    }

    /// <summary>Sweeps the module for doors and returns the nearest one, with no linked-to-
    /// module filter — the opposite of <see cref="FindNearestTransitionDoor"/>, and the whole
    /// reason <see cref="OpenDoor"/> exists.</summary>
    private static nint FindNearestAnyDoor(nint player, out int doorsSeen)
    {
        nint* doors = stackalloc nint[DoorSweepCapacity];
        doorsSeen = Odyssey.CollectDoors(doors, DoorSweepCapacity);

        Vec3 here = Odyssey.GetPosition(player);
        nint best = 0;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < doorsSeen; i++)
        {
            float distance = Distance(here, Odyssey.GetPosition(doors[i]));
            if (distance >= bestDistance)
            {
                continue;
            }

            best = doors[i];
            bestDistance = distance;
        }

        return best;
    }

    /// <summary>
    /// Brings the party to the player when the engine says it is scattered, and reports
    /// whether that worked. Returns the state of the gate as it stands after the attempt.
    ///
    /// WHY THIS IS PART OF USING A DOOR. `CSWSDoor::EventHandler` tests
    /// `CSWParty::IsPartyTogether` at 0x0058C228 and, when it is false, runs the script
    /// `k_trg_transfail` INSTEAD of transitioning — a failure with no trace at all: the door
    /// is used, no fade starts, no module is stored. Measured live once: the PC teleported to
    /// the door, Trask stayed a hundred metres back at the module start, and the transition
    /// never happened.
    ///
    /// Gathering here rather than leaving it to the caller is what makes the verb
    /// self-sufficient, and it is also what a player does — you walk to a door WITH your
    /// party. It goes through the engine's own `PositionParty`, the very call a module
    /// transition makes, so the walkmesh snap and the formation are the engine's problem.
    ///
    /// ⚠ Host-side staging only, and deliberately unconditional about that: companions are
    /// host-authoritative, so on a mirroring instance this moves bodies whose owner will
    /// republish them. That is acceptable for a debug verb and would not be for a feature.
    /// </summary>
    private static bool GatherPartyIfScattered()
    {
        bool wasTogether = Odyssey.IsPartyTogether();
        bool gatherRan = false;
        int members = 0;
        bool skipFlagWasSet = false;

        if (!wasTogether)
        {
            PluginHost.Log(
                "door: the engine says the party is NOT together, and a door transition is gated "
                + "on that (0x0058C228) - gathering it first");

            gatherRan = Odyssey.GatherParty(out members, out skipFlagWasSet);
        }

        PartyGateOutcome outcome = DoorUse.ClassifyGate(
            wasTogether, gatherRan, togetherNow: !wasTogether && Odyssey.IsPartyTogether());

        switch (outcome)
        {
            case PartyGateOutcome.GatheredAndTogether:
                PluginHost.Log(
                    $"door: gathered {members} party member(s) to you"
                    + (skipFlagWasSet
                        ? " (the table's one-shot skip flag was set, so it was positioned twice)"
                        : string.Empty));
                break;

            case PartyGateOutcome.GatheredButStillApart:
                PluginHost.Log(
                    $"door: gathered {members} party member(s) and the engine STILL says the "
                    + "party is not together - this use will run k_trg_transfail and move nobody");
                break;

            case PartyGateOutcome.CouldNotGather:
                PluginHost.Log(
                    "door: could not gather the party (no player, no party object, or no table) "
                    + "- this use will run k_trg_transfail and move nobody");
                break;
        }

        return DoorUse.GateWillPass(outcome);
    }

    /// <summary>
    /// Sweeps the module for doors and returns the nearest one that is a module transition,
    /// with the counts so a failure can describe itself.
    ///
    /// Linked-only is the guard, not a convenience: a bare <c>usedoor</c> has to be incapable
    /// of doing anything except starting a transition, and an ordinary door two metres away
    /// would otherwise win the distance comparison and merely open — which looks exactly like
    /// the command working.
    /// </summary>
    private static nint FindNearestTransitionDoor(nint player, out int doorsSeen, out int linkedSeen)
    {
        nint* doors = stackalloc nint[DoorSweepCapacity];
        doorsSeen = Odyssey.CollectDoors(doors, DoorSweepCapacity);
        linkedSeen = 0;

        Vec3 here = Odyssey.GetPosition(player);
        nint best = 0;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < doorsSeen; i++)
        {
            if (!Odyssey.DoorIsLinked(doors[i]))
            {
                continue;
            }

            linkedSeen++;

            float distance = Distance(here, Odyssey.GetPosition(doors[i]));
            if (distance >= bestDistance)
            {
                continue;
            }

            best = doors[i];
            bestDistance = distance;
        }

        return best;
    }
}

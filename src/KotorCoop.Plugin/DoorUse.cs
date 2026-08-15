namespace KotorCoop.Plugin;

/// <summary>What the staging layer is allowed to do about a request to use a door.</summary>
internal enum DoorUseVerdict
{
    /// <summary>Send the click.</summary>
    Use,

    /// <summary>No world, or no local player creature to do the using.</summary>
    RefuseNoWorld,

    /// <summary>Nothing here answers to that tag or id.</summary>
    RefuseMissing,

    /// <summary>Something answered, but it is not a door — the vtable says otherwise.</summary>
    RefuseNotADoor,

    /// <summary>
    /// A door, but not a module transition: <c>CSWSDoor::GetIsLinked 0x005890D0</c> reads
    /// <c>linked_to_flags</c> (+0x384) and accepts only 1 or 2. An ordinary door opens; it
    /// never stores the outgoing area, so using one measures nothing.
    /// </summary>
    RefuseNotLinked,

    /// <summary>The door is in open state 3 — destroyed. Its transition is gone with it.</summary>
    RefuseDestroyed,

    /// <summary>A transition is already in flight. Starting a second one inside the window the
    /// first one opened is the shape of every defect in transition-window-races.md.</summary>
    RefuseTransitionPending,

    /// <summary>A movie is up. In-world this should be unreachable; seeing it is a finding.</summary>
    RefuseMoviePlaying,

    /// <summary>The door is real and linked, but far enough away that the walk is its own
    /// experiment. A mistyped tag must not send the PC on a cross-map hike.</summary>
    RefuseTooFar,
}

/// <summary>
/// How the party stood when the door was used — the gate that decides whether the transition
/// happens at all, reduced to the four outcomes a caller must tell apart.
/// </summary>
internal enum PartyGateOutcome
{
    /// <summary>The engine already said the party was together; nothing was moved.</summary>
    AlreadyTogether,

    /// <summary>It was scattered, we gathered it, and the engine now says it is together.</summary>
    GatheredAndTogether,

    /// <summary>We gathered it and the engine STILL says it is not. The transition will be
    /// cancelled into <c>k_trg_transfail</c> and nobody will move.</summary>
    GatheredButStillApart,

    /// <summary>The gather could not run at all — no player, no party object, no table.</summary>
    CouldNotGather,
}

/// <summary>One door the sweep found, reduced to the facts the choice needs.</summary>
/// <param name="ObjectId">The engine object id.</param>
/// <param name="Linked">True when <c>GetIsLinked</c> says it is a module transition.</param>
/// <param name="Distance">Metres from the local player.</param>
internal readonly record struct DoorCandidate(uint ObjectId, bool Linked, float Distance);

/// <summary>
/// The rules for making the local player USE a door, as pure arithmetic over facts the caller
/// has already measured — no engine pointers, no shared block — so it compiles into the
/// 64-bit test project and the guard can be asserted rather than described. Same shape and
/// the same reason as <see cref="CheatAuthority"/> and <see cref="MovieStaging"/>.
///
/// WHY THIS EXISTS, and why it is not "warp with extra steps". A co-op warp
/// (<c>Odyssey.TravelToModule</c>) and the engine's own door transition end in the SAME three
/// calls — read out of <c>CSWSDoor::EventHandler</c> at 0x0058C185:
/// <c>SetMoveToModulePending(1)</c>, <c>SetMoveToModuleString(door-&gt;linked_to_module)</c>,
/// <c>SetMoveToModuleStartWaypoint(door-&gt;linked_to)</c>. What differs is everything BEFORE
/// that. The door path is two phases:
///
/// <list type="number">
/// <item><b>on use</b> — <c>GetNextTransitionPendingID 0x00506AC0</c>,
///   <c>CGuiInGame::StartGlobalFade 0x0062ABF0</c> (0.5 s), then
///   <c>CSWSArea::SetTransitionPending(1, id) 0x00506B30</c> and
///   <c>SetPauseState(2, 0)</c> (0x0058C2A0-0x0058C315);</item>
/// <item><b>when the fade completes</b> — the three move-to-module calls above, then
///   <c>SetTransitionPending(0, 0)</c>.</item>
/// </list>
///
/// Phase 1 writes <c>CSWSArea::trans_pending</c> (+0x2C4) — the exact byte
/// <c>Odyssey.IsAreaTransitionPending</c> reads, and therefore the exact flag that opens
/// <c>PluginHost.OnFrame</c>'s pre-store flush branch. So a door transition gives the plugin a
/// half-second window in which it must destroy every body it spawned, and a plugin-initiated
/// warp gives it none because the warp already flushed before raising anything. That is the
/// whole difference between the two, and it is why only this verb can test what a movie does
/// to the flush window (docs/research/cinematics.md §7.4, §8).
/// </summary>
internal static class DoorUse
{
    /// <summary>
    /// How far the player may be from the door. The engine walks the PC there on its own
    /// (<c>AIActionUseObject 0x0057E8C0</c> paths to the door's action point), so a large
    /// number is not dangerous — it is unmeasurable. Forty metres is comfortably more than a
    /// module's own rooms and small enough that a wrong tag is refused rather than acted on
    /// forty seconds later, when nobody is still watching the log for a cause.
    /// </summary>
    public const float MaxUseDistance = 40f;

    /// <summary>Open state 3 is "destroyed" (see <c>Odyssey.GetDoorOpenState</c>).</summary>
    public const int DoorStateDestroyed = 3;

    /// <summary>
    /// Decides whether this door may be used. Every argument is something the caller read on
    /// THIS frame; nothing here re-reads and nothing is remembered, because a fact about the
    /// engine from an earlier frame is precisely what a transition window turns into poison.
    /// </summary>
    public static DoorUseVerdict Decide(
        bool hasPlayer,
        bool found,
        bool isDoor,
        bool linked,
        int openState,
        bool transitionPending,
        bool moviePlaying,
        float distance)
    {
        if (!hasPlayer)
        {
            return DoorUseVerdict.RefuseNoWorld;
        }

        if (!found)
        {
            return DoorUseVerdict.RefuseMissing;
        }

        if (!isDoor)
        {
            return DoorUseVerdict.RefuseNotADoor;
        }

        // Before the state and distance checks, deliberately: "that door goes nowhere" is the
        // answer a caller most needs, and it is true regardless of where they are standing.
        if (!linked)
        {
            return DoorUseVerdict.RefuseNotLinked;
        }

        if (openState == DoorStateDestroyed)
        {
            return DoorUseVerdict.RefuseDestroyed;
        }

        if (transitionPending)
        {
            return DoorUseVerdict.RefuseTransitionPending;
        }

        if (moviePlaying)
        {
            return DoorUseVerdict.RefuseMoviePlaying;
        }

        if (distance > MaxUseDistance)
        {
            return DoorUseVerdict.RefuseTooFar;
        }

        return DoorUseVerdict.Use;
    }

    /// <summary>The sentence a refusal answers with — what was SEEN, never just "refused".</summary>
    public static string Explain(DoorUseVerdict verdict, string what, float distance) => verdict switch
    {
        DoorUseVerdict.Use => $"using {what}",
        DoorUseVerdict.RefuseNoWorld => "no player loaded (not in a module yet)",
        DoorUseVerdict.RefuseMissing => $"no door {what} in this module",
        DoorUseVerdict.RefuseNotADoor => $"{what} is not a door (its vtable says otherwise)",
        // ⚠ Kept under 63 characters, all of them: this sentence is the cheat channel's
        // detail field (SharedBlock.CheatDetailLength) and anything longer is TRUNCATED, so a
        // refusal whose reason runs past the cap arrives as half a reason. The long form
        // belongs in the plugin log, which has no cap.
        DoorUseVerdict.RefuseNotLinked => $"{what}: no LinkedToModule - an ordinary door",
        DoorUseVerdict.RefuseDestroyed => $"{what} is destroyed; its transition went too",
        DoorUseVerdict.RefuseTransitionPending =>
            "a module transition is already pending; refusing a second",
        DoorUseVerdict.RefuseMoviePlaying =>
            "a movie is playing - and the frame hook still ran, which is news",
        DoorUseVerdict.RefuseTooFar =>
            $"{what} is {distance:F1}m away, past the {MaxUseDistance:F0}m limit",
        _ => $"refused ({verdict})",
    };

    /// <summary>
    /// Picks the nearest door that is actually a module transition, or -1 when none of the
    /// candidates is one.
    ///
    /// Linked-only, and that is the guard rather than a convenience: a bare <c>usedoor</c>
    /// must be incapable of doing anything except starting a module transition, because that
    /// is the only thing the verb exists for. An ordinary door two metres away would otherwise
    /// win the distance comparison and open, which looks like the command worked.
    /// </summary>
    public static int ChooseNearest(ReadOnlySpan<DoorCandidate> candidates)
    {
        int best = -1;
        float bestDistance = float.MaxValue;

        for (int i = 0; i < candidates.Length; i++)
        {
            DoorCandidate candidate = candidates[i];
            if (!candidate.Linked || candidate.Distance >= bestDistance)
            {
                continue;
            }

            best = i;
            bestDistance = candidate.Distance;
        }

        return best;
    }

    /// <summary>
    /// How far PAST the door to aim the walk.
    ///
    /// The transition fires while the creature is MOVING, from the movement update that
    /// notices it entered the door's sub-area — not on arrival at a destination. So the
    /// destination only has to make the path cross the threshold, and aiming a little beyond
    /// the door is what guarantees that: aiming AT it lets the walk stop short (the engine's
    /// own use-action stopped 1.5 m away, measured) and never cross anything.
    /// </summary>
    public const float WalkThroughOvershoot = 2.5f;

    /// <summary>
    /// The point to walk to so the player passes THROUGH <paramref name="door"/>: the door,
    /// carried on by <see cref="WalkThroughOvershoot"/> to the side the player is NOT on.
    ///
    /// The direction comes from the DOOR'S OWN FACING (<paramref name="facing"/>,
    /// <c>CSWSObject::Orientation</c> at +0x9C) rather than from the approach vector, and the
    /// side is chosen by the sign of <c>dot(player − door, facing)</c> — the identical test
    /// <c>CSWSDoor::OpenDoor</c> uses at 0x00589C97 to decide which way the door swings. A
    /// doorway has one axis you can cross it on; approaching it diagonally does not change
    /// that, and an approach-vector overshoot would aim at the wall beside the opening. (The
    /// two happen to agree when you walk in dead square, which is how an approach-only version
    /// survived its first test.)
    ///
    /// Falls back to the approach direction when the facing is degenerate, and to the door
    /// itself when the player is standing on it — a zero-length vector has no direction to
    /// extend, and normalising it yields NaN, which the engine accepts and then walks
    /// somewhere undefined.
    /// </summary>
    public static Vec3 WalkThroughPoint(Vec3 player, Vec3 door, Vec3 facing, float overshoot)
    {
        float axisX = facing.X;
        float axisY = facing.Y;
        float axisLength = MathF.Sqrt((axisX * axisX) + (axisY * axisY));

        if (axisLength >= 0.01f)
        {
            axisX /= axisLength;
            axisY /= axisLength;

            // Which side is the player on? Cross to the other one.
            float side = ((player.X - door.X) * axisX) + ((player.Y - door.Y) * axisY);

            if (MathF.Abs(side) >= 0.01f)
            {
                float sign = side > 0f ? -1f : 1f;
                return new Vec3
                {
                    X = door.X + (axisX * overshoot * sign),
                    Y = door.Y + (axisY * overshoot * sign),
                    Z = door.Z,
                };
            }
        }

        // No usable facing, or the player is exactly in the door's plane: carry on the way
        // they were walking.
        float dx = door.X - player.X;
        float dy = door.Y - player.Y;
        float length = MathF.Sqrt((dx * dx) + (dy * dy));

        if (length < 0.01f)
        {
            return door;
        }

        return new Vec3
        {
            X = door.X + (dx / length * overshoot),
            Y = door.Y + (dy / length * overshoot),

            // Z is the engine's problem: the move action snaps to the walkmesh, and a height
            // guessed from two points either side of a doorway is worse than none.
            Z = door.Z,
        };
    }

    /// <summary>
    /// Classifies the party gate from the three facts around a gather attempt.
    ///
    /// The rule that matters is the last one: <see cref="PartyGateOutcome.GatheredButStillApart"/>
    /// and <see cref="PartyGateOutcome.CouldNotGather"/> are NOT successes, and must never be
    /// reported as one. A door used with the party scattered runs <c>k_trg_transfail</c> and
    /// moves nobody, leaving no trace whatsoever — so a verb that answered "walking to the
    /// door" in that state would be lying in exactly the way that costs a live round, which
    /// is how this rule was found.
    /// </summary>
    public static PartyGateOutcome ClassifyGate(bool wasTogether, bool gatherRan, bool togetherNow)
    {
        if (wasTogether)
        {
            return PartyGateOutcome.AlreadyTogether;
        }

        if (!gatherRan)
        {
            return PartyGateOutcome.CouldNotGather;
        }

        return togetherNow ? PartyGateOutcome.GatheredAndTogether : PartyGateOutcome.GatheredButStillApart;
    }

    /// <summary>True when the transition can actually be expected to happen.</summary>
    public static bool GateWillPass(PartyGateOutcome outcome) =>
        outcome is PartyGateOutcome.AlreadyTogether or PartyGateOutcome.GatheredAndTogether;

    /// <summary>
    /// What the sweep saw, for the answer when it found nothing usable. "No transition door
    /// here" and "no doors here at all" are different problems — the first is the wrong
    /// module, the second is a broken sweep — and a caller with neither number cannot tell
    /// them apart.
    /// </summary>
    public static string DescribeSweep(int doors, int linked) => doors == 0
        ? "the sweep found no doors at all in this module"
        : $"the sweep found {doors} door(s), {linked} of them module transitions";
}

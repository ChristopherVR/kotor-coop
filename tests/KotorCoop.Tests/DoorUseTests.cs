using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The door-use staging verb: the line a person types, the guard that decides whether the
/// engine is touched, and the choice of which door a bare <c>usedoor</c> means.
///
/// This verb exists for one reason, and the reason is what makes the guard worth testing.
/// Asking the engine to start a module transition makes it STORE the outgoing area: anything
/// the plugin has spawned and not destroyed by then is written into the save as a permanent
/// resident and the object-id counter shifts for the rest of the playthrough. A co-op warp
/// never has that problem because the plugin flushes before it raises anything — which is
/// precisely why the warp cannot test the flush, and why this verb had to exist at all
/// (docs/research/cinematics.md §8).
/// </summary>
public class DoorUseTests
{
    // ---- the typed line ------------------------------------------------------------

    [Fact]
    public void ABareUseDoorMeansTheNearestTransitionDoor()
    {
        CoopCommand command = ConsoleCommand.Parse("usedoor");

        Assert.Equal(CoopCommandKind.UseDoor, command.Kind);
        Assert.Null(command.Address);
    }

    [Theory]
    [InlineData("usedoor end_door10", "end_door10")]
    [InlineData("usedoor 0x1F", "0x1F")]
    [InlineData("usedoor #31", "#31")]
    public void ANamedDoorTravelsAsTypedAndIsResolvedInTheGame(string line, string token)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.UseDoor, command.Kind);
        Assert.Equal(token, command.Address);
    }

    [Theory]
    [InlineData("usedoor end door 10")]           // more than one door
    [InlineData("usedoor end-door-10")]           // not a tag and not an id
    public void AMalformedDoorLineIsRefusedWithAReason(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.NotEqual(string.Empty, command.Error);
    }

    // ---- crossdoor: the same grammar as usedoor, a different kind ------------------
    //
    // crossdoor shares its parse shape with usedoor deliberately (docs/research/cinematics.md
    // §8.3c) - the two verbs diverge only inside the plugin, at the very last step.

    [Fact]
    public void ABareCrossDoorMeansTheNearestTransitionDoor()
    {
        CoopCommand command = ConsoleCommand.Parse("crossdoor");

        Assert.Equal(CoopCommandKind.CrossDoor, command.Kind);
        Assert.Null(command.Address);
    }

    [Theory]
    [InlineData("crossdoor end_door10", "end_door10")]
    [InlineData("crossdoor 0x4A", "0x4A")]
    [InlineData("crossdoor #31", "#31")]
    public void ACrossDoorNamedDoorTravelsAsTypedAndIsResolvedInTheGame(string line, string token)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.CrossDoor, command.Kind);
        Assert.Equal(token, command.Address);
    }

    [Theory]
    [InlineData("crossdoor a b")]                  // more than one door
    [InlineData("crossdoor end-door-10")]           // not a tag and not an id
    public void AMalformedCrossDoorLineIsRefusedWithAReason(string line)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(CoopCommandKind.Invalid, command.Kind);
        Assert.NotEqual(string.Empty, command.Error);
    }

    // ---- the guard -----------------------------------------------------------------

    private static DoorUseVerdict Decide(
        bool hasPlayer = true,
        bool found = true,
        bool isDoor = true,
        bool linked = true,
        int openState = 0,
        bool transitionPending = false,
        bool moviePlaying = false,
        float distance = 2f) =>
        DoorUse.Decide(hasPlayer, found, isDoor, linked, openState, transitionPending, moviePlaying, distance);

    [Fact]
    public void ALinkedDoorInReachIsUsed()
    {
        Assert.Equal(DoorUseVerdict.Use, Decide());
    }

    /// <summary>
    /// Each refusal is a different fact about the world, and each answer has to name the one
    /// it saw. This verb's silent failure is the worst kind available: "the door did nothing"
    /// and "the door stored the area with a phantom still standing in it" leave the same
    /// evidence on screen and completely different evidence in the save.
    /// </summary>
    [Fact]
    public void TheGuardRefusesEachDangerousStateByName()
    {
        (DoorUseVerdict Expected, DoorUseVerdict Actual)[] cases =
        [
            (DoorUseVerdict.RefuseNoWorld, Decide(hasPlayer: false)),
            (DoorUseVerdict.RefuseMissing, Decide(found: false)),
            (DoorUseVerdict.RefuseNotADoor, Decide(isDoor: false)),
            (DoorUseVerdict.RefuseNotLinked, Decide(linked: false)),
            (DoorUseVerdict.RefuseDestroyed, Decide(openState: DoorUse.DoorStateDestroyed)),
            (DoorUseVerdict.RefuseTransitionPending, Decide(transitionPending: true)),
            (DoorUseVerdict.RefuseMoviePlaying, Decide(moviePlaying: true)),
            (DoorUseVerdict.RefuseTooFar, Decide(distance: DoorUse.MaxUseDistance + 0.1f)),
        ];

        foreach ((DoorUseVerdict expected, DoorUseVerdict actual) in cases)
        {
            Assert.Equal(expected, actual);
            Assert.NotEqual(string.Empty, DoorUse.Explain(actual, "'end_door10'", 1f));
        }
    }

    /// <summary>
    /// The refusal the coordinator asked for by name, and the one a live run will hit most:
    /// an ordinary door. Using one opens it, stores nothing, and would look exactly like the
    /// test running — so it is refused, and the sentence says WHY rather than "no".
    /// </summary>
    [Fact]
    public void ADoorWithNoLinkedModuleIsRefusedByName()
    {
        DoorUseVerdict verdict = Decide(linked: false);

        Assert.Equal(DoorUseVerdict.RefuseNotLinked, verdict);

        string explanation = DoorUse.Explain(verdict, "'end_door01'", 3f);
        Assert.Contains("end_door01", explanation);
        Assert.Contains("LinkedToModule", explanation);
    }

    /// <summary>"Not linked" is answered before "too far" and before the open state, because
    /// it is true wherever the caller happens to be standing.</summary>
    [Fact]
    public void AnUnlinkedDoorIsRefusedForBeingUnlinkedRatherThanForBeingFar()
    {
        Assert.Equal(
            DoorUseVerdict.RefuseNotLinked,
            Decide(linked: false, distance: 500f, openState: DoorUse.DoorStateDestroyed));
    }

    /// <summary>A wrong tag must not send the player on a forty-second walk whose cause
    /// nobody is still watching the log for by the time it lands.</summary>
    [Fact]
    public void ADoorAcrossTheMapIsRefusedWithTheDistance()
    {
        DoorUseVerdict verdict = Decide(distance: 120f);

        Assert.Equal(DoorUseVerdict.RefuseTooFar, verdict);
        Assert.Contains("120.0m", DoorUse.Explain(verdict, "'far_door'", 120f));
    }

    [Fact]
    public void ADoorExactlyAtTheLimitIsStillUsed()
    {
        Assert.Equal(DoorUseVerdict.Use, Decide(distance: DoorUse.MaxUseDistance));
    }

    // ---- walking THROUGH the door --------------------------------------------------

    /// <summary>
    /// The destination is past the door, not at it. A KOTOR module transition is posted by the
    /// creature's own MOVEMENT update as it crosses into the door's sub-area (event 0x1E,
    /// built only in `HandleSubAreaStateChanges`, `WalkUpdateLocation`, `DriveUpdateLocation`,
    /// `WalkUpdateLocation_QuickWalk` and `AIActionMoveToPoint`) — so the walk only has to
    /// cross the threshold, and aiming AT the door lets it stop short. Measured: the engine's
    /// own use-action stopped 1.5 m away and nothing happened.
    /// </summary>
    private static readonly Vec3 NoFacing = new() { X = 0f, Y = 0f, Z = 0f };

    /// <summary>
    /// The destination is on the far side of the door, chosen by the DOOR'S OWN facing — the
    /// same axis, and the same sign test, that `CSWSDoor::OpenDoor` uses at 0x00589C97 to pick
    /// its swing direction. A doorway has exactly one axis you can cross it on.
    /// </summary>
    [Fact]
    public void TheWalkCrossesTheDoorOnItsOwnAxis()
    {
        Vec3 door = new() { X = 10f, Y = 0f, Z = -1f };
        Vec3 facing = new() { X = 1f, Y = 0f, Z = 0f };
        Vec3 player = new() { X = 0f, Y = 0f, Z = 0f };   // on the -X side

        Vec3 through = DoorUse.WalkThroughPoint(player, door, facing, 2.5f);

        Assert.Equal(12.5f, through.X, 3);   // pushed to the +X side
        Assert.Equal(0f, through.Y, 3);

        // Z is the door's, never interpolated: the move action snaps to the walkmesh, and a
        // height guessed from two points either side of a doorway is worse than none.
        Assert.Equal(-1f, through.Z, 3);
    }

    /// <summary>Approach from the other side and the target flips with it — otherwise the walk
    /// would aim back the way the player came and never cross anything.</summary>
    [Fact]
    public void TheSideIsChosenFromWhereThePlayerStands()
    {
        Vec3 door = new() { X = 10f, Y = 0f, Z = 0f };
        Vec3 facing = new() { X = 1f, Y = 0f, Z = 0f };

        Vec3 fromMinus = DoorUse.WalkThroughPoint(new Vec3 { X = 0f }, door, facing, 2.5f);
        Vec3 fromPlus = DoorUse.WalkThroughPoint(new Vec3 { X = 20f }, door, facing, 2.5f);

        Assert.Equal(12.5f, fromMinus.X, 3);
        Assert.Equal(7.5f, fromPlus.X, 3);
    }

    /// <summary>
    /// The case the approach-vector version got wrong and its first test could not catch: a
    /// diagonal approach. The door's axis still decides, so the target stays in the opening
    /// instead of drifting into the wall beside it.
    /// </summary>
    [Fact]
    public void ADiagonalApproachStillCrossesSquare()
    {
        Vec3 door = new() { X = 0f, Y = 0f, Z = 0f };
        Vec3 facing = new() { X = 0f, Y = 1f, Z = 0f };     // the doorway opens along Y
        Vec3 player = new() { X = -8f, Y = -1f, Z = 0f };   // well off to one side

        Vec3 through = DoorUse.WalkThroughPoint(player, door, facing, 2f);

        Assert.Equal(0f, through.X, 3);
        Assert.Equal(2f, through.Y, 3);
    }

    /// <summary>With no usable facing it falls back to carrying on the way the player was
    /// walking, which is better than refusing to move at all.</summary>
    [Fact]
    public void ADegenerateFacingFallsBackToTheApproach()
    {
        Vec3 player = new() { X = 4f, Y = 4f, Z = 0f };
        Vec3 door = new() { X = 1f, Y = 0f, Z = 0f };   // 5 m away, direction (-0.6, -0.8)

        Vec3 through = DoorUse.WalkThroughPoint(player, door, NoFacing, 5f);

        Assert.Equal(1f + (-0.6f * 5f), through.X, 3);
        Assert.Equal(0f + (-0.8f * 5f), through.Y, 3);
    }

    /// <summary>A zero-length vector has no direction to extend, and normalising it would
    /// produce NaN — which the engine would accept and then walk somewhere undefined.</summary>
    [Fact]
    public void StandingOnTheDoorAimsAtTheDoorRatherThanNowhere()
    {
        Vec3 door = new() { X = 7f, Y = -3f, Z = 2f };

        Vec3 through = DoorUse.WalkThroughPoint(door, door, NoFacing, DoorUse.WalkThroughOvershoot);

        Assert.Equal(door.X, through.X, 3);
        Assert.Equal(door.Y, through.Y, 3);
        Assert.Equal(door.Z, through.Z, 3);
        Assert.False(float.IsNaN(through.X) || float.IsNaN(through.Y));
    }

    /// <summary>Standing exactly in the door's plane gives the sign test nothing to work with,
    /// so it falls through to the approach rather than picking a side by rounding error.</summary>
    [Fact]
    public void StandingInTheDoorsPlaneFallsBackToTheApproach()
    {
        Vec3 door = new() { X = 0f, Y = 0f, Z = 0f };
        Vec3 facing = new() { X = 1f, Y = 0f, Z = 0f };
        Vec3 player = new() { X = 0f, Y = -5f, Z = 0f };   // dot with facing is 0

        Vec3 through = DoorUse.WalkThroughPoint(player, door, facing, 2f);

        Assert.Equal(0f, through.X, 3);
        Assert.Equal(2f, through.Y, 3);
    }

    // ---- the party gate ------------------------------------------------------------

    /// <summary>
    /// The gate that silently cancels a door transition. `CSWSDoor::EventHandler` tests
    /// `CSWParty::IsPartyTogether` at 0x0058C228 and, when it is false, runs `k_trg_transfail`
    /// INSTEAD of transitioning — no fade, no store, no trace. Measured live on 2026-08-15:
    /// the PC was teleported to the door, Trask stayed 100 m back at the module start, and
    /// nothing happened.
    /// </summary>
    [Fact]
    public void TheGateIsClassifiedFromWhatWasActuallyMeasured()
    {
        Assert.Equal(
            PartyGateOutcome.AlreadyTogether,
            DoorUse.ClassifyGate(wasTogether: true, gatherRan: false, togetherNow: false));

        Assert.Equal(
            PartyGateOutcome.GatheredAndTogether,
            DoorUse.ClassifyGate(wasTogether: false, gatherRan: true, togetherNow: true));

        Assert.Equal(
            PartyGateOutcome.GatheredButStillApart,
            DoorUse.ClassifyGate(wasTogether: false, gatherRan: true, togetherNow: false));

        Assert.Equal(
            PartyGateOutcome.CouldNotGather,
            DoorUse.ClassifyGate(wasTogether: false, gatherRan: false, togetherNow: false));
    }

    /// <summary>
    /// The rule the whole verb rests on: only the two outcomes where the engine will really
    /// transition count as passing. Reporting "walking to the door" while the gate is shut is
    /// a lie of exactly the kind that costs a live round — the door IS used, and nothing at
    /// all happens.
    /// </summary>
    [Fact]
    public void OnlyAGenuinelyGatheredPartyCountsAsPassingTheGate()
    {
        Assert.True(DoorUse.GateWillPass(PartyGateOutcome.AlreadyTogether));
        Assert.True(DoorUse.GateWillPass(PartyGateOutcome.GatheredAndTogether));

        Assert.False(DoorUse.GateWillPass(PartyGateOutcome.GatheredButStillApart));
        Assert.False(DoorUse.GateWillPass(PartyGateOutcome.CouldNotGather));
    }

    /// <summary>A party that was already together is never moved — the gather is a repair,
    /// not a ritual, and teleporting companions that did not need it would be a side effect
    /// nobody asked a door for.</summary>
    [Fact]
    public void APartyAlreadyTogetherIsLeftAlone()
    {
        PartyGateOutcome outcome =
            DoorUse.ClassifyGate(wasTogether: true, gatherRan: false, togetherNow: false);

        Assert.Equal(PartyGateOutcome.AlreadyTogether, outcome);
        Assert.True(DoorUse.GateWillPass(outcome));
    }

    // ---- choosing the nearest ------------------------------------------------------

    [Fact]
    public void TheNearestLinkedDoorWins()
    {
        DoorCandidate[] doors =
        [
            new(0x10, Linked: true, Distance: 12f),
            new(0x11, Linked: true, Distance: 3f),
            new(0x12, Linked: true, Distance: 40f),
        ];

        Assert.Equal(1, DoorUse.ChooseNearest(doors));
    }

    /// <summary>
    /// The guard inside the choice: an ordinary door standing closer than the transition must
    /// not win. It would merely open, which looks exactly like the command working — and the
    /// run would be recorded as a result.
    /// </summary>
    [Fact]
    public void ACloserOrdinaryDoorNeverWins()
    {
        DoorCandidate[] doors =
        [
            new(0x10, Linked: false, Distance: 0.5f),
            new(0x11, Linked: true, Distance: 9f),
        ];

        Assert.Equal(1, DoorUse.ChooseNearest(doors));
    }

    [Fact]
    public void AModuleWithNoTransitionDoorChoosesNothing()
    {
        DoorCandidate[] doors =
        [
            new(0x10, Linked: false, Distance: 1f),
            new(0x11, Linked: false, Distance: 2f),
        ];

        Assert.Equal(-1, DoorUse.ChooseNearest(doors));
        Assert.Equal(-1, DoorUse.ChooseNearest([]));
    }

    /// <summary>
    /// "No transition door here" is the wrong module; "no doors at all" is a broken sweep.
    /// A caller with neither number cannot tell them apart, and the second one would be read
    /// as the first for as long as it took somebody to doubt the tooling.
    /// </summary>
    [Fact]
    public void AFailedSweepSaysWhichKindOfFailureItWas()
    {
        Assert.Contains("no doors at all", DoorUse.DescribeSweep(doors: 0, linked: 0));
        Assert.Contains("14 door(s), 0 of them", DoorUse.DescribeSweep(doors: 14, linked: 0));
    }
}

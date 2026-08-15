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

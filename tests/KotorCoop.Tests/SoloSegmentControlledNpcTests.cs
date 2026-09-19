using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The Leviathan brig trigger (docs/research/leviathan.md §5.3 #1): a script hands the PLAYER to a
/// companion (<c>SwitchPlayerCharacter</c>, party table +0xF0 from -1 to a slot) and the OTHER
/// human must spectate. The roster count is meaningless here (0 from the strip, refilled by the
/// rescuer's own party), so the segment follows +0xF0 alone.
/// </summary>
public class SoloSegmentControlledNpcTests
{
    private static readonly SoloSegmentCatalogue Catalogue = new();

    private static SoloSegmentInputs Roster(string module, int companions) => new(module, companions, true, false);

    [Fact]
    public void TheEdgeIntoControlInTheBrigBeginsAndTheEdgeOutEnds()
    {
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None, machine.NoteControlledNpc("lev_m40aa", controlled: false));
        Assert.False(machine.Active);

        Assert.Equal(SoloSegmentEvent.Began, machine.NoteControlledNpc("lev_m40aa", controlled: true));
        Assert.True(machine.Active);

        Assert.Equal(SoloSegmentEvent.None, machine.NoteControlledNpc("lev_m40aa", controlled: true));
        Assert.True(machine.Active);

        Assert.Equal(SoloSegmentEvent.Ended, machine.NoteControlledNpc("lev_m40aa", controlled: false));
        Assert.False(machine.Active);
    }

    [Fact]
    public void ModuleNamesMatchCaseInsensitively()
    {
        SoloSegmentMachine machine = new();
        machine.NoteControlledNpc("LEV_M40AA", controlled: false);
        Assert.Equal(SoloSegmentEvent.Began, machine.NoteControlledNpc("LEV_M40AA", controlled: true));
    }

    [Theory]
    [InlineData("tar_m02aa")]
    [InlineData("sta_m45ac")] // catalogued for the roster trigger, not for this one
    [InlineData("lev_m40ab")] // the command deck: Saul's pairs are cutscene RemovePartyMember/AddPartyMember, no swap
    [InlineData("")]
    public void AnyOtherModuleNeverBegins(string module)
    {
        SoloSegmentMachine machine = new();
        machine.NoteControlledNpc(module, controlled: false);
        Assert.Equal(SoloSegmentEvent.None, machine.NoteControlledNpc(module, controlled: true));
        Assert.False(machine.Active);
    }

    [Fact]
    public void ARosterRefillDuringTheSegmentDoesNotEndIt()
    {
        // The regression this design exists to prevent: Step's own "roster refilled" end rule would
        // otherwise end a controlled segment the moment the rescuer's party (1-2 companions) shows up.
        SoloSegmentMachine machine = new();
        machine.NoteControlledNpc("lev_m40aa", controlled: false);
        machine.NoteControlledNpc("lev_m40aa", controlled: true);

        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Roster("lev_m40aa", 0)));
        Assert.True(machine.Active);
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Roster("lev_m40aa", 2)));
        Assert.True(machine.Active);
    }

    [Fact]
    public void ArrivingMidSegmentCountsAsTheEdge()
    {
        // A joiner (or a rejoin) whose first observation already has +0xF0 set must park too.
        SoloSegmentMachine machine = new();
        Assert.Equal(SoloSegmentEvent.Began, machine.NoteControlledNpc("lev_m40aa", controlled: true));
    }

    [Fact]
    public void AModuleChangeEndsItEvenWhileStillControlled()
    {
        SoloSegmentMachine machine = new();
        machine.NoteControlledNpc("lev_m40aa", controlled: true);

        Assert.Equal(SoloSegmentEvent.Ended, machine.NoteControlledNpc("lev_m40ab", controlled: true));
        Assert.False(machine.Active);

        // ...and the still-set +0xF0 in the new module (not catalogued) must not re-begin it.
        Assert.Equal(SoloSegmentEvent.None, machine.NoteControlledNpc("lev_m40ab", controlled: true));
        Assert.False(machine.Active);
    }

    [Fact]
    public void InvalidateForgetsTheControlledState()
    {
        SoloSegmentMachine machine = new();
        machine.NoteControlledNpc("lev_m40aa", controlled: true);
        machine.Invalidate();

        Assert.False(machine.Active);
        // After a world change a fresh observation of an already-set +0xF0 is an edge again.
        Assert.Equal(SoloSegmentEvent.Began, machine.NoteControlledNpc("lev_m40aa", controlled: true));
    }

    [Fact]
    public void TheRosterTriggersStillWorkAlongsideIt()
    {
        SoloSegmentMachine machine = new();
        machine.Step(Catalogue, Roster("sta_m45ac", 2));
        Assert.Equal(SoloSegmentEvent.Began, machine.Step(Catalogue, Roster("sta_m45ac", 0)));
        Assert.Equal(SoloSegmentEvent.None, machine.NoteControlledNpc("sta_m45ac", controlled: true));
        Assert.Equal(SoloSegmentEvent.Ended, machine.Step(Catalogue, Roster("sta_m45ac", 1)));
    }
}

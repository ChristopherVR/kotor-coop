using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The detection rule behind <c>solo_segment_spectate</c>: a story-forced solo segment begins
/// when the local roster drops from a companion to none, IN a catalogued module, via a real
/// drop edge — not because the party-select screen was used, not because nobody was ever
/// recruited yet, and not because the departed companions were downed or dead. See
/// docs/research/star-forge-finale.md §3.3/§3.5.
/// </summary>
public class SoloSegmentMachineTests
{
    private static readonly SoloSegmentCatalogue Catalogue = new();

    private static SoloSegmentInputs Inputs(
        string module, int companionCount, bool removedAlive = true, bool partySelectOpen = false) =>
        new(module, companionCount, removedAlive, partySelectOpen);

    [Fact]
    public void ADropFromTwoToZeroInACataloguedModuleBegins()
    {
        SoloSegmentMachine machine = new();

        // Baseline pass: two companions, nothing happens yet.
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 2)));
        Assert.False(machine.Active);

        // The Bastila-chamber door fires nine RemovePartyMember calls: the roster empties.
        Assert.Equal(SoloSegmentEvent.Began, machine.Step(Catalogue, Inputs("sta_m45ac", 0)));
        Assert.True(machine.Active);
    }

    [Fact]
    public void ADropFromOneToZeroAlsoBegins()
    {
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 1)));
        Assert.Equal(SoloSegmentEvent.Began, machine.Step(Catalogue, Inputs("sta_m45ac", 0)));
        Assert.True(machine.Active);
    }

    [Fact]
    public void EndsWhenTheRosterRefills()
    {
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("sta_m45ac", 1));
        machine.Step(Catalogue, Inputs("sta_m45ac", 0));
        Assert.True(machine.Active);

        Assert.Equal(SoloSegmentEvent.Ended, machine.Step(Catalogue, Inputs("sta_m45ac", 1)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void EndsWhenTheModuleChangesEvenIfTheRosterStaysEmpty()
    {
        // The Bastila door's own strip carries across the ordinary sta_m45ac -> sta_m45ad door
        // crossing (§3.5's "continuation of the same forced-solo state") — but the detection
        // machine's own contract is per-module, and a module change with the roster still empty
        // must end the OLD segment (a new Began, if the new module also qualifies, is a
        // SEPARATE concern the caller re-evaluates on the very next pass).
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("sta_m45ac", 1));
        machine.Step(Catalogue, Inputs("sta_m45ac", 0));
        Assert.True(machine.Active);

        Assert.Equal(SoloSegmentEvent.Ended, machine.Step(Catalogue, Inputs("sta_m45ad", 0)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void StaysActiveWhileTheRosterRemainsEmptyInTheSameModule()
    {
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("sta_m45ac", 1));
        machine.Step(Catalogue, Inputs("sta_m45ac", 0));
        Assert.True(machine.Active);

        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 0)));
        Assert.True(machine.Active);
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 0)));
        Assert.True(machine.Active);
    }

    // ---- false positives -------------------------------------------------------------

    [Fact]
    public void EmptyRosterAtGameStartNeverBeginsANewMachine()
    {
        // The very FIRST observation this machine ever sees can never be a drop edge — there
        // is no "before" to have dropped from. Even in a catalogued module (a live-test warp
        // straight into sta_m45ac with no companions recruited yet), the baseline pass alone
        // must not fire.
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 0)));
        Assert.False(machine.Active);

        // And staying at zero on the next pass still does not begin one: there was no
        // OBSERVED drop, only an unchanging zero.
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 0)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void NeverHavingRecruitedAnyoneOutsideACataloguedModuleNeverBegins()
    {
        // The Endar Spire before Trask joins: companion count 0 throughout, an ordinary module.
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("end_m01aa", 0)));
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("end_m01aa", 0)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void ADropInAnUncataloguedModuleNeverBegins()
    {
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("tar_m03aa", 1));
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("tar_m03aa", 0)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void ADropWhereTheRemovedCompanionsWereNotAliveNeverBegins()
    {
        // A death, not a story strip: the companions leaving the roster were already downed.
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("sta_m45ac", 1));
        Assert.Equal(SoloSegmentEvent.None,
            machine.Step(Catalogue, Inputs("sta_m45ac", 0, removedAlive: false)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void ADropWhileThePartySelectionScreenIsOpenNeverBegins()
    {
        // The player benched their own companions by hand, not a story-forced strip.
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("sta_m45ac", 1));
        Assert.Equal(SoloSegmentEvent.None,
            machine.Step(Catalogue, Inputs("sta_m45ac", 0, partySelectOpen: true)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void ARefusedBeginDoesNotConsumeTheDropEdgeForALaterCataloguedModule()
    {
        // A drop that fails every gate (wrong module) still updates the baseline, so a LATER,
        // genuinely qualifying drop in a different module is judged on its own — not skipped
        // because "we already saw a drop this session".
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("tar_m03aa", 1));
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("tar_m03aa", 0)));

        // Roster refills, player travels to the Star Forge, roster drops again — this time a
        // real drop edge, in a catalogued module.
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 1)));
        Assert.Equal(SoloSegmentEvent.Began, machine.Step(Catalogue, Inputs("sta_m45ac", 0)));
        Assert.True(machine.Active);
    }

    // ---- Invalidate ---------------------------------------------------------------

    [Fact]
    public void InvalidateDropsTheBaselineSoTheNextPassCanNeverReadAsADrop()
    {
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("sta_m45ac", 2));
        machine.Invalidate();

        // A different module, straight to zero companions — must not be misread as a drop from
        // the old module's baseline of 2.
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ad", 0)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void InvalidateWhileActiveClearsTheLevelWithoutAnEndedEvent()
    {
        // Invalidate is a hard reset (module change / session teardown), not a decided end —
        // it returns void, and the caller (SoloSegmentController) logs its own line for this
        // path rather than treating it as a SoloSegmentEvent.
        SoloSegmentMachine machine = new();

        machine.Step(Catalogue, Inputs("sta_m45ac", 1));
        machine.Step(Catalogue, Inputs("sta_m45ac", 0));
        Assert.True(machine.Active);

        machine.Invalidate();
        Assert.False(machine.Active);
    }
}

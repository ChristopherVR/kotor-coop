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

    // ---- verified Taris duel-player pin -------------------------------------------

    [Fact]
    public void TarisPlayerPinBeginsWithNoCompanionsAndClearsAtDuelEnd()
    {
        SoloSegmentMachine machine = new();

        // The player can enter the ring already alone, so there is no roster drop edge or
        // two-call RemovePartyMember burst to observe.
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("tar_m02ae", 0)));
        Assert.Equal(SoloSegmentEvent.Began,
            machine.NoteDuelPlayerPin("tar_m02ae", pinned: true, partySelectionOpen: false));
        Assert.True(machine.Active);
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("tar_m02ae", 0)));

        // k_ptar_duelend/k_ptar_duelend2 clear the same PC pin. The empty roster is allowed to
        // remain empty; this boundary does not wait for a companion to be recruited again.
        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteDuelPlayerPin("tar_m02ae", pinned: false, partySelectionOpen: false));
        Assert.Equal(SoloSegmentEvent.Ended, machine.Step(Catalogue, Inputs("tar_m02ae", 0)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void TarisPlayerPinBeginsWithOneCompanionAndEndsOnPinClear()
    {
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("tar_m02ae", 1)));
        Assert.Equal(SoloSegmentEvent.Began,
            machine.NoteDuelPlayerPin("tar_m02ae", pinned: true, partySelectionOpen: false));
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("tar_m02ae", 1)));
        Assert.True(machine.Active);

        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteDuelPlayerPin("tar_m02ae", pinned: false, partySelectionOpen: false));
        Assert.Equal(SoloSegmentEvent.Ended, machine.Step(Catalogue, Inputs("tar_m02ae", 1)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void PlayerPinRiseWhileManualPartySelectionIsOpenDoesNotPark()
    {
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteDuelPlayerPin("tar_m02ae", pinned: true, partySelectionOpen: true));
        Assert.False(machine.Active);

        // The rise was consumed while the optional manual-solo UI was open; closing the UI while
        // the same flag remains set does not replay it as a story event.
        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteDuelPlayerPin("tar_m02ae", pinned: true, partySelectionOpen: false));
        Assert.False(machine.Active);
    }

    [Fact]
    public void NPCPinOrPinInAnotherModuleNeverStartsTheDuelSegment()
    {
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteDuelPlayerPin("korr_m35aa", pinned: true, partySelectionOpen: false));
        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteDuelPlayerPin("end_m01aa", pinned: true, partySelectionOpen: false));
        Assert.False(machine.Active);
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

    // ---- the script-agnostic second trigger: NoteRemovePartyMemberCall -------------------
    //
    // docs/research/star-forge-finale.md §11 / §9.5: a detour on the VM's own
    // ExecuteCommandRemovePartyMember observes every RemovePartyMember call directly, and a
    // burst of >= 2 within ~1s can begin a segment even before the roster-count poll (Step)
    // ever sees a drop.

    [Fact]
    public void ASingleRemovePartyMemberCallNeverBegins()
    {
        // The debounce is the whole point: one call is an ordinary, unremarkable script action
        // (a single companion benched), not a story-forced strip.
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_000));
        Assert.False(machine.Active);
    }

    [Fact]
    public void TwoRemovePartyMemberCallsWithinTheWindowBeginInACataloguedModule()
    {
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_000));
        Assert.Equal(SoloSegmentEvent.Began,
            machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_200));
        Assert.True(machine.Active);
    }

    [Fact]
    public void NineCallsInOneScriptLikeTheRealWipeBeginExactlyOnce()
    {
        // The real wipe fires nine RemovePartyMember calls back to back (§9.2 step 4). Only the
        // SECOND call should read as Began; the remaining seven must be no-ops on an already-
        // active machine — matching §9.5's own concern about "nine redundant solo segment began
        // events" if nothing coalesces them.
        SoloSegmentMachine machine = new();
        int began = 0;

        for (int i = 0; i < 9; i++)
        {
            SoloSegmentEvent evt = machine.NoteRemovePartyMemberCall(
                Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_000 + i);
            if (evt == SoloSegmentEvent.Began)
            {
                began++;
            }
        }

        Assert.Equal(1, began);
        Assert.True(machine.Active);
    }

    [Fact]
    public void TwoCallsMoreThanTheWindowApartNeverBegin()
    {
        // Debounce, the other direction: two calls a long way apart in time are two unrelated
        // single removals, not one burst.
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 0));
        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 5_000));
        Assert.False(machine.Active);
    }

    [Fact]
    public void ABurstInAnUncataloguedModuleNeverBeginsWithoutTheAnyModuleGate()
    {
        SoloSegmentMachine machine = new();

        machine.NoteRemovePartyMemberCall(Catalogue, "tar_m03aa", anyModuleAllowed: false, nowTicks: 1_000);
        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteRemovePartyMemberCall(Catalogue, "tar_m03aa", anyModuleAllowed: false, nowTicks: 1_100));
        Assert.False(machine.Active);
    }

    [Fact]
    public void ABurstInAnUncataloguedModuleBeginsWhenAnyModuleIsAllowed()
    {
        // solo_segment_any_module=1: the module gate is dropped entirely, matching §9.5's own
        // "generalises to ANY future forced-solo content this mod hasn't researched yet" case.
        SoloSegmentMachine machine = new();

        machine.NoteRemovePartyMemberCall(Catalogue, "tar_m03aa", anyModuleAllowed: true, nowTicks: 1_000);
        Assert.Equal(SoloSegmentEvent.Began,
            machine.NoteRemovePartyMemberCall(Catalogue, "tar_m03aa", anyModuleAllowed: true, nowTicks: 1_100));
        Assert.True(machine.Active);
    }

    [Fact]
    public void ABurstBeginIsANoOpWhileAlreadyActive()
    {
        SoloSegmentMachine machine = new();

        machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_000);
        machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_100);
        Assert.True(machine.Active);

        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_200));
    }

    [Fact]
    public void ABurstBeginDoesNotImmediatelyEndOnAStillStaleRosterCount()
    {
        // The race NoteRemovePartyMemberCall's own doc comment describes: the hook fires before
        // the roster-count poll has caught up, so the very next Step() pass can still read the
        // OLD (pre-wipe) companion count. That must not be misread as "the roster refilled".
        SoloSegmentMachine machine = new();

        Assert.Equal(SoloSegmentEvent.Began, NoteBurst(machine, "sta_m45ac"));
        Assert.True(machine.Active);

        // Step() runs the very next frame; the roster-count read has not caught up yet.
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 1)));
        Assert.True(machine.Active);

        // Once the roster read genuinely reflects the wipe...
        Assert.Equal(SoloSegmentEvent.None, machine.Step(Catalogue, Inputs("sta_m45ac", 0)));
        Assert.True(machine.Active);

        // ...a REAL refill afterward still ends the segment normally.
        Assert.Equal(SoloSegmentEvent.Ended, machine.Step(Catalogue, Inputs("sta_m45ac", 1)));
        Assert.False(machine.Active);
    }

    [Fact]
    public void InvalidateClearsTheRecentCallHistoryToo()
    {
        SoloSegmentMachine machine = new();

        machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_000);
        machine.Invalidate();

        // Without the reset, this second call (only 100ms after the pre-Invalidate one) would
        // wrongly read as a burst against stale history from a session/module already left.
        Assert.Equal(SoloSegmentEvent.None,
            machine.NoteRemovePartyMemberCall(Catalogue, "sta_m45ac", anyModuleAllowed: false, nowTicks: 1_100));
        Assert.False(machine.Active);
    }

    private static SoloSegmentEvent NoteBurst(SoloSegmentMachine machine, string module)
    {
        machine.NoteRemovePartyMemberCall(Catalogue, module, anyModuleAllowed: false, nowTicks: 1_000);
        return machine.NoteRemovePartyMemberCall(Catalogue, module, anyModuleAllowed: false, nowTicks: 1_100);
    }
}

using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class KorribanFinalTestMachineTests
{
    private const uint LoadSerial = 17;
    private const uint StartSequence = 40;

    private static KorribanFinalTestMachine Begin(uint partyMask = 0b1)
    {
        KorribanFinalTestMachine machine = new();
        Assert.True(machine.TryBegin(
            KorribanFinalTestMachine.StartModule,
            KorribanFinalTestMachine.StartScript,
            sessionActive: true,
            LoadSerial,
            partyMask,
            StartSequence));
        return machine;
    }

    [Fact]
    public void StartsOnlyOnTheExactDialogueActionInsideAnActiveSession()
    {
        KorribanFinalTestMachine machine = new();

        Assert.False(machine.TryBegin("korr_m35aa", "k_pkor_duelend", true, LoadSerial, 1, 1));
        Assert.False(machine.TryBegin("korr_m34aa", KorribanFinalTestMachine.StartScript, true, LoadSerial, 1, 2));
        Assert.False(machine.TryBegin(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.StartScript, false, LoadSerial, 1, 3));

        Assert.True(machine.TryBegin("KORR_M35AA", "K_PKOR_PARTYLEAV", true, LoadSerial, 0b101, 4));
        Assert.True(machine.Active);
        Assert.Equal(0b101u, machine.InitialPartyMask);
        Assert.False(machine.TryBegin(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.StartScript, true, LoadSerial, 0, 5));
        Assert.Equal(0b101u, machine.InitialPartyMask);
    }

    [Fact]
    public void InitialAreaEntryAndReturnScriptBeforeRouteCannotEndTheSegment()
    {
        KorribanFinalTestMachine machine = Begin(partyMask: 0);

        Assert.False(machine.ObserveAreaScript(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.ReturnScript, true, LoadSerial, 1));
        Assert.False(machine.Step(KorribanFinalTestMachine.StartModule, true, LoadSerial, 0, finalTestValue: 7));
        Assert.True(machine.Active);
    }

    [Fact]
    public void ZeroCompanionPathRequiresRouteExactReturnCueAndProgressionThreshold()
    {
        KorribanFinalTestMachine machine = Begin(partyMask: 0);

        Assert.False(machine.Step("korr_m36aa", true, LoadSerial, 0, finalTestValue: 0));
        Assert.True(machine.ObserveAreaScript(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.ReturnScript, true, LoadSerial, 1));
        Assert.False(machine.Step(KorribanFinalTestMachine.StartModule, true, LoadSerial, 0, finalTestValue: 3));
        Assert.True(machine.Active); // The cue is consumed; a later global change alone is insufficient.

        Assert.False(machine.Step("korr_m36aa", true, LoadSerial, 0, finalTestValue: 3));
        Assert.True(machine.ObserveAreaScript(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.ReturnScript, true, LoadSerial, 2));
        Assert.True(machine.Step(KorribanFinalTestMachine.StartModule, true, LoadSerial, 0, finalTestValue: 4));
        Assert.False(machine.Active);
        Assert.True(machine.CompletedLastStep);
    }

    [Fact]
    public void PendingReturnWaitsForReadableRosterAndGlobalWithoutLosingItsExactCue()
    {
        KorribanFinalTestMachine machine = Begin(partyMask: 0);
        machine.Step("korr_m36aa", true, LoadSerial, 0, finalTestValue: 0);
        Assert.True(machine.ObserveAreaScript(
            KorribanFinalTestMachine.StartModule,
            KorribanFinalTestMachine.ReturnScript,
            true,
            LoadSerial,
            1));

        Assert.False(machine.Step(
            KorribanFinalTestMachine.StartModule,
            true,
            LoadSerial,
            currentPartyMask: 0,
            finalTestValue: 5,
            partyRosterKnown: false,
            finalTestValueKnown: true));
        Assert.True(machine.HasPendingReturnDispatch);

        Assert.False(machine.Step(
            KorribanFinalTestMachine.StartModule,
            true,
            LoadSerial,
            currentPartyMask: 0,
            finalTestValue: 0,
            partyRosterKnown: true,
            finalTestValueKnown: false));
        Assert.True(machine.HasPendingReturnDispatch);

        Assert.True(machine.Step(
            KorribanFinalTestMachine.StartModule,
            true,
            LoadSerial,
            currentPartyMask: 0,
            finalTestValue: 4,
            partyRosterKnown: true,
            finalTestValueKnown: true));
        Assert.True(machine.CompletedLastStep);
    }

    [Fact]
    public void ReturnWaitsForEveryOriginalRosterSlotRatherThanOnlyTheSameCount()
    {
        KorribanFinalTestMachine machine = Begin(partyMask: 0b000000101);
        machine.Step("korr_m36aa", true, LoadSerial, 0, finalTestValue: 0);

        Assert.True(machine.ObserveAreaScript(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.ReturnScript, true, LoadSerial, 1));
        Assert.False(machine.Step(KorribanFinalTestMachine.StartModule, true, LoadSerial, 0b000000110, finalTestValue: 5));
        Assert.True(machine.Active); // Two NPCs returned, but not the two that left.

        machine.Step("korr_m36aa", true, LoadSerial, 0b000000110, finalTestValue: 5);
        Assert.True(machine.ObserveAreaScript(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.ReturnScript, true, LoadSerial, 2));
        Assert.True(machine.Step(KorribanFinalTestMachine.StartModule, true, LoadSerial, 0b000000111, finalTestValue: 5));
        Assert.False(machine.Active);
    }

    [Fact]
    public void WrongReturnModuleScriptAndLoadSerialAreRejected()
    {
        KorribanFinalTestMachine machine = Begin(partyMask: 0);
        machine.Step("korr_m39aa", true, LoadSerial, 0, finalTestValue: 5);

        Assert.False(machine.ObserveAreaScript("korr_m39aa", KorribanFinalTestMachine.ReturnScript, true, LoadSerial, 1));
        Assert.False(machine.ObserveAreaScript(KorribanFinalTestMachine.StartModule, "k_pkor_partyleav", true, LoadSerial, 2));
        Assert.False(machine.ObserveAreaScript(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.ReturnScript, true, LoadSerial + 1, 3));
        Assert.False(machine.HasPendingReturnDispatch);
        Assert.True(machine.Active);
    }

    [Fact]
    public void WorldLoadSessionLossAndUnexpectedAreaCancelEvenWhenTheModuleNameMatchesLater()
    {
        KorribanFinalTestMachine loaded = Begin(partyMask: 0);
        Assert.True(loaded.Step(KorribanFinalTestMachine.StartModule, true, LoadSerial + 1, 0, finalTestValue: 0));
        Assert.False(loaded.Active);

        KorribanFinalTestMachine disconnected = Begin(partyMask: 0);
        Assert.True(disconnected.Step(KorribanFinalTestMachine.StartModule, false, LoadSerial, 0, finalTestValue: 0));
        Assert.False(disconnected.Active);

        KorribanFinalTestMachine leftRoute = Begin(partyMask: 0);
        Assert.True(leftRoute.Step("korr_m34aa", true, LoadSerial, 0, finalTestValue: 0));
        Assert.False(leftRoute.Active);
        Assert.False(leftRoute.ObserveAreaScript(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.ReturnScript, true, LoadSerial, 1));
    }

    [Fact]
    public void TemporarilyUnknownModuleDuringTransitionDoesNotEraseTheLatch()
    {
        KorribanFinalTestMachine machine = Begin(partyMask: 0);

        Assert.False(machine.Step(null, true, LoadSerial, 0, finalTestValue: 0));
        Assert.True(machine.Active);
        Assert.False(machine.Step("korr_m36aa", true, LoadSerial, 0, finalTestValue: 0));
        Assert.True(machine.Active);
    }

    [Fact]
    public void DialogueSequenceDedupSurvivesResetButAllowsTheNextRealDispatch()
    {
        KorribanFinalTestMachine machine = Begin(partyMask: 0);
        Assert.True(machine.Step(KorribanFinalTestMachine.StartModule, true, LoadSerial + 1, 0, finalTestValue: 0));

        Assert.False(machine.TryBegin(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.StartScript, true, LoadSerial + 1, 0, StartSequence));
        Assert.True(machine.TryBegin(KorribanFinalTestMachine.StartModule, KorribanFinalTestMachine.StartScript, true, LoadSerial + 1, 0, StartSequence + 1));
    }

    [Theory]
    [InlineData("korr_m35aa")]
    [InlineData("korr_m36aa")]
    [InlineData("korr_m37aa")]
    [InlineData("korr_m38aa")]
    [InlineData("korr_m38ab")]
    [InlineData("korr_m39aa")]
    public void OnlyVerifiedKorribanFinalTestRouteModulesAreRetained(string module)
    {
        Assert.True(KorribanFinalTestMachine.IsRouteModule(module));
    }

    [Theory]
    [InlineData("korr_m34aa")]
    [InlineData("korr_m33ab")]
    [InlineData("")]
    public void UnrelatedModulesAreOutsideTheRetainedRoute(string module)
    {
        Assert.False(KorribanFinalTestMachine.IsRouteModule(module));
    }
}

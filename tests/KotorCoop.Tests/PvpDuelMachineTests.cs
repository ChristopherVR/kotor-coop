using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The local player's half of a duel (docs/research/pvp-plan.md): the pin, the snapshot, and the
/// restore. The plugin cannot run here, so the parts that can strand a player are pure.
/// </summary>
public class PvpDuelMachineTests
{
    private const uint Armed = 1;
    private const uint Live = 2;

    private static PvpFacts Facts(
        uint phase, bool resolvable = true, bool pinned = false, bool downed = false,
        int hp = 80, int maxHp = 100, int fp = 30, int maxFp = 50) =>
        new(phase, resolvable, pinned, downed, hp, maxHp, fp, maxFp);

    /// <summary>Runs frames with the player already pinned (as the controller would have left it).</summary>
    private static PvpStep ArmAndSettle(PvpDuelMachine machine, int hp = 80, int fp = 30)
    {
        PvpStep arm = machine.Step(Facts(Armed, hp: hp, fp: fp));
        Assert.True(arm.Has(PvpAction.SetPin));
        return arm;
    }

    // ---- arming ---------------------------------------------------------------------------

    [Fact]
    public void ArmingPinsThePlayerAndHoldsFromThenOn()
    {
        PvpDuelMachine machine = new();

        Assert.False(machine.Holding);
        PvpStep arm = machine.Step(Facts(Armed, hp: 80, fp: 30));

        Assert.True(arm.Has(PvpAction.SetPin));
        Assert.True(machine.Holding);
        Assert.Equal(80, machine.SnapshotHitPoints);
        Assert.Equal(30, machine.SnapshotForcePoints);
    }

    [Fact]
    public void NoDuelMeansNothingHappens()
    {
        PvpDuelMachine machine = new();

        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(PvpAction.None, machine.Step(Facts(0)).Actions);
        }

        Assert.False(machine.Holding);
    }

    [Fact]
    public void AModulesOwnPinIsNeverClaimedOrTakenOff()
    {
        PvpDuelMachine machine = new();

        PvpStep arm = machine.Step(Facts(Armed, pinned: true));
        Assert.False(arm.Has(PvpAction.SetPin));

        machine.Step(Facts(0, pinned: true, hp: 80, fp: 30));
        PvpStep release = machine.Step(Facts(0, pinned: true, hp: 80, fp: 30));

        Assert.False(release.Has(PvpAction.ClearPin));
        Assert.False(machine.Holding);
    }

    [Fact]
    public void ArmingWaitsForAnUnresolvablePlayer()
    {
        PvpDuelMachine machine = new();

        Assert.Equal(PvpAction.None, machine.Step(Facts(Armed, resolvable: false)).Actions);
        Assert.False(machine.Holding);

        Assert.True(machine.Step(Facts(Armed)).Has(PvpAction.SetPin));
    }

    [Fact]
    public void ADownedPlayerIsNotSnapshottedAsHowTheyWere()
    {
        PvpDuelMachine machine = new();

        Assert.Equal(PvpAction.None, machine.Step(Facts(Armed, downed: true, hp: 0)).Actions);
        Assert.False(machine.Holding);

        machine.Step(Facts(Armed, hp: 64));
        Assert.Equal(64, machine.SnapshotHitPoints);
    }

    [Fact]
    public void APinTakenOffMidDuelIsPutBackOncePerLoss()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine);

        Assert.Equal(PvpAction.None, machine.Step(Facts(Live, pinned: true)).Actions);
        Assert.True(machine.Step(Facts(Live, pinned: false)).Has(PvpAction.SetPin));
        Assert.Equal(PvpAction.None, machine.Step(Facts(Live, pinned: true)).Actions);
    }

    [Fact]
    public void GoingLiveChangesNothingForThePin()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine);

        Assert.Equal(PvpAction.None, machine.Step(Facts(Live, pinned: true, hp: 30)).Actions);
        Assert.Equal(80, machine.SnapshotHitPoints);
    }

    // ---- release and restore --------------------------------------------------------------

    [Fact]
    public void ReleaseTakesThePinOffAndWritesTheSnapshotBack()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine, hp: 80, fp: 30);
        machine.Step(Facts(Live, pinned: true, hp: 22, fp: 10));

        PvpStep release = machine.Step(Facts(0, pinned: true, hp: 22, fp: 10));

        Assert.True(release.Has(PvpAction.ClearPin));
        Assert.True(release.Has(PvpAction.WriteHitPoints));
        Assert.True(release.Has(PvpAction.WriteForcePoints));
        Assert.Equal(80, release.HitPoints);
        Assert.Equal(30, release.ForcePoints);
        Assert.True(machine.Holding); // until the engine reads it back
    }

    [Fact]
    public void TheRestoreFinishesOnceTheEngineReadsTheSnapshotBack()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine, hp: 80, fp: 30);
        machine.Step(Facts(0, pinned: true, hp: 22, fp: 10));

        machine.Step(Facts(0, pinned: false, hp: 80, fp: 30));

        Assert.False(machine.Holding);
        Assert.False(machine.GaveUp);
        Assert.Equal(PvpAction.None, machine.Step(Facts(0, hp: 80, fp: 30)).Actions);
    }

    [Fact]
    public void AWriteThatDidNotTakeIsRetriedFramesApartAndGivenUpAfterSixTries()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine, hp: 80, fp: 30);

        int writes = 0;
        for (int frame = 0; frame < 200 && machine.Holding; frame++)
        {
            PvpStep step = machine.Step(Facts(0, pinned: false, hp: 22, fp: 30));
            if (step.Has(PvpAction.WriteHitPoints))
            {
                writes++;
            }
        }

        Assert.Equal(PvpDuelMachine.MaxWriteAttempts, writes);
        Assert.True(machine.GaveUp);
        Assert.False(machine.Holding);
    }

    [Fact]
    public void ADownedPlayerIsStoodUpBeforeAnyHitPointWrite()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine, hp: 80, fp: 30);

        PvpStep first = machine.Step(Facts(0, pinned: true, downed: true, hp: 0, fp: 30));
        Assert.True(first.Has(PvpAction.Revive));
        Assert.True(first.Has(PvpAction.ClearPin));
        Assert.False(first.Has(PvpAction.WriteHitPoints));

        PvpStep up = machine.Step(Facts(0, downed: false, hp: 3, fp: 30));
        Assert.True(up.Has(PvpAction.WriteHitPoints));
        Assert.Equal(80, up.HitPoints);
    }

    [Fact]
    public void ANewWorldKeepsTheSnapshotWaiting()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine, hp: 80, fp: 30);

        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(PvpAction.None, machine.Step(Facts(0, resolvable: false)).Actions);
        }

        Assert.True(machine.Holding);
        PvpStep back = machine.Step(Facts(0, pinned: true, hp: 80, fp: 12));
        Assert.True(back.Has(PvpAction.ClearPin));
        Assert.True(back.Has(PvpAction.WriteForcePoints));
        Assert.Equal(30, back.ForcePoints);
    }

    [Fact]
    public void ARestoreThatNeverGetsAPlayerIsDroppedAfterItsBudget()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine);

        for (int i = 0; i <= PvpDuelMachine.RestoreBudgetFrames + 1; i++)
        {
            machine.Step(Facts(0, resolvable: false));
        }

        Assert.False(machine.Holding);
        Assert.True(machine.GaveUp);
    }

    [Fact]
    public void ReArmingBeforeTheRestoreFinishedKeepsTheOldSnapshot()
    {
        PvpDuelMachine machine = new();
        ArmAndSettle(machine, hp: 80, fp: 30);
        machine.Step(Facts(0, pinned: true, hp: 22, fp: 10)); // restoring; engine still shows 22

        machine.Step(Facts(Armed, pinned: false, hp: 22, fp: 10));

        Assert.Equal(80, machine.SnapshotHitPoints);
        Assert.Equal(30, machine.SnapshotForcePoints);
    }

    // ---- the numbers ----------------------------------------------------------------------

    [Theory]
    [InlineData(80, 100, 80)]
    [InlineData(150, 100, 100)]   // the maximum fell (gear came off): never above it
    [InlineData(0, 100, 1)]       // never restored onto the floor
    [InlineData(40, 0, 40)]       // unknown maximum: take the snapshot
    public void HitPointsComeBackWithinTheMaximumAndNeverBelowOne(int snapshot, int max, int expected)
    {
        Assert.Equal(expected, PvpDuelMachine.RestoreHitPoints(snapshot, max));
    }

    [Theory]
    [InlineData(30, 50, 30)]
    [InlineData(60, 50, 50)]
    [InlineData(-3, 50, 0)]
    [InlineData(20, 0, 20)]
    public void ForcePointsComeBackWithinTheMaximumAndNeverBelowZero(int snapshot, int max, int expected)
    {
        Assert.Equal(expected, PvpDuelMachine.RestoreForcePoints(snapshot, max));
    }

    // ---- which hits count -----------------------------------------------------------------

    [Fact]
    public void OnlyThePlayersOwnCharacterFightsInADuel()
    {
        const uint me = 0x7F000001;

        Assert.True(PvpHitRule.CountsInDuel(me, me));
        Assert.False(PvpHitRule.CountsInDuel(0x7F000044, me));   // a companion
        Assert.False(PvpHitRule.CountsInDuel(0, me));            // a trap, a mine, a script
        Assert.False(PvpHitRule.CountsInDuel(me, 0));            // player unknown: nothing counts
    }
}

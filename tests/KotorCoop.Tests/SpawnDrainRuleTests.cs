using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// When the session's spawn drain stops, and what it says.
///
/// The bug behind it, measured on the rig: forty seconds after a disconnect, with
/// `remotes 0` and `command 0`, a `coop_phantom` was still standing in the client's game.
/// A body left behind is not cosmetic — StoreCurrentModule serialises it into the save, so
/// the departed player's corpse-shaped creature becomes a permanent resident of somebody's
/// single-player module.
///
/// Every case here is really the same assertion: the drain never ends silently. "Nothing was
/// standing" is an outcome, not the absence of one, because in a log those two are the same
/// thing — which is exactly why the leak survived a round of teardown work that printed five
/// healthy lines while it happened.
/// </summary>
public class SpawnDrainRuleTests
{
    private const int Budget = 12;

    [Fact]
    public void AnEmptyWorldStillReportsItself()
    {
        (SpawnDrainOutcome outcome, int atRelease) =
            SpawnDrainRule.Decide(standing: 0, atRelease: -1, passesLeft: Budget);

        Assert.Equal(SpawnDrainOutcome.NothingWasStanding, outcome);
        Assert.Equal(0, atRelease);
    }

    [Fact]
    public void TheFirstPassIsWhatEstablishesTheTotalToReport()
    {
        // Measured, not accumulated: later passes each destroy some of the same set, so
        // summing them would over-count the removal.
        (SpawnDrainOutcome outcome, int atRelease) =
            SpawnDrainRule.Decide(standing: 3, atRelease: -1, passesLeft: Budget);

        Assert.Equal(SpawnDrainOutcome.KeepDraining, outcome);
        Assert.Equal(3, atRelease);
    }

    [Fact]
    public void ABodyThatRefusesOnceIsRetriedRatherThanForgotten()
    {
        // The whole reason the drain has passes at all: DestroyObjectById declines outright
        // while the script VM is mid-call, and a disconnect lands on an arbitrary frame.
        (SpawnDrainOutcome outcome, _) =
            SpawnDrainRule.Decide(standing: 1, atRelease: 1, passesLeft: Budget - 1);

        Assert.Equal(SpawnDrainOutcome.KeepDraining, outcome);
    }

    [Fact]
    public void ClearingTheLastBodyEndsTheDrainAndSaysHowManyWent()
    {
        (SpawnDrainOutcome outcome, int atRelease) =
            SpawnDrainRule.Decide(standing: 0, atRelease: 2, passesLeft: 5);

        Assert.Equal(SpawnDrainOutcome.Cleaned, outcome);
        Assert.Equal(2, atRelease);
    }

    [Fact]
    public void RunningOutOfPassesWithABodyStandingIsReportedAsSuch()
    {
        // The one outcome a player needs to act on: their next save would bake it in.
        (SpawnDrainOutcome outcome, int atRelease) =
            SpawnDrainRule.Decide(standing: 1, atRelease: 4, passesLeft: 0);

        Assert.Equal(SpawnDrainOutcome.Refused, outcome);
        Assert.Equal(4, atRelease);
    }

    [Fact]
    public void ADrainThatStartsFullAndEndsCleanNeverGoesSilent()
    {
        // The realistic sequence, end to end: three bodies, one of them refusing twice.
        int atRelease = -1;
        int passes = Budget;
        int[] standingPerPass = [3, 1, 1, 0];
        List<SpawnDrainOutcome> outcomes = [];

        foreach (int standing in standingPerPass)
        {
            passes--;
            (SpawnDrainOutcome outcome, atRelease) =
                SpawnDrainRule.Decide(standing, atRelease, passes);
            outcomes.Add(outcome);
        }

        Assert.Equal(
            [
                SpawnDrainOutcome.KeepDraining,
                SpawnDrainOutcome.KeepDraining,
                SpawnDrainOutcome.KeepDraining,
                SpawnDrainOutcome.Cleaned,
            ],
            outcomes);

        // And the figure reported at the end is what was there at the start, not what the
        // final pass happened to remove.
        Assert.Equal(3, atRelease);
    }

    [Fact]
    public void TheBudgetOutlastsTheDestroyRetryCapWithRoomToSpare()
    {
        // A body is written off after three refusals, so a drain that only ever sees
        // refusals must reach a terminal outcome well inside its budget. Anything else and
        // the "REFUSED and still standing" line would be reporting the budget, not the game.
        int atRelease = -1;
        int passes = Budget;
        SpawnDrainOutcome outcome = SpawnDrainOutcome.KeepDraining;

        for (int pass = 0; pass < 3 && outcome == SpawnDrainOutcome.KeepDraining; pass++)
        {
            passes--;
            (outcome, atRelease) = SpawnDrainRule.Decide(standing: 1, atRelease, passes);
        }

        // Still draining after three passes, with budget to spare: the write-off, not the
        // budget, is what ends it.
        Assert.Equal(SpawnDrainOutcome.KeepDraining, outcome);
        Assert.True(passes > 0);
        Assert.Equal(1, atRelease);
    }
}

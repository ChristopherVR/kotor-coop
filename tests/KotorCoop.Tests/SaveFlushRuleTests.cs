using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/save-ghosts.md §4 (roadmap #36, save-time half): a manual save is held
/// while co-op spawns are destroyed and re-issued exactly once, and nothing here can ever
/// turn a save into a lost save.
/// </summary>
public sealed class SaveFlushRuleTests
{
    [Fact]
    public void ARequestWithNothingStandingPassesStraightThrough()
    {
        var rule = new SaveFlushRule();

        Assert.Equal(SaveFlushRequest.PassThrough, rule.OnSaveRequested(0));
        Assert.Equal(SaveFlushPhase.Idle, rule.Phase);
        Assert.Equal(SaveFlushStep.Idle, rule.OnFrame(false));
    }

    [Fact]
    public void ARequestWithOnlyAPluginOwnedInputLockIsHeldForTheAsyncStoreWindow()
    {
        var rule = new SaveFlushRule(budgetFrames: 10, quietFrames: 2, settleFrames: 0);

        Assert.Equal(SaveFlushRequest.Hold, rule.OnSaveRequested(0, inputLockOwned: true));
        Assert.Equal(SaveFlushStep.ReissueClean, rule.OnFrame(flushedOne: false));
        Assert.Equal(SaveFlushPhase.Quiet, rule.Phase);
        Assert.Equal(SaveFlushRequest.PassThrough, rule.OnSaveRequested(0, inputLockOwned: true));
        Assert.Equal(SaveFlushStep.KeepQuiet, rule.OnFrame(flushedOne: false));
        Assert.Equal(SaveFlushStep.Release, rule.OnFrame(flushedOne: false));
    }

    [Fact]
    public void ARequestWithBodiesStandingIsHeldThenReissuedOnceClean()
    {
        var rule = new SaveFlushRule(budgetFrames: 120, quietFrames: 4, settleFrames: 1);

        Assert.Equal(SaveFlushRequest.Hold, rule.OnSaveRequested(3));
        Assert.Equal(SaveFlushPhase.Holding, rule.Phase);
        Assert.Equal(3, rule.StandingAtHold);

        // Three bodies, one destroyed per frame.
        Assert.Equal(SaveFlushStep.KeepHolding, rule.OnFrame(flushedOne: true));
        Assert.Equal(SaveFlushStep.KeepHolding, rule.OnFrame(flushedOne: true));
        Assert.Equal(SaveFlushStep.KeepHolding, rule.OnFrame(flushedOne: true));

        // First clean frame is the settle frame; the second re-issues.
        Assert.Equal(SaveFlushStep.KeepHolding, rule.OnFrame(flushedOne: false));
        Assert.Equal(SaveFlushStep.ReissueClean, rule.OnFrame(flushedOne: false));
        Assert.Equal(SaveFlushPhase.Quiet, rule.Phase);
        Assert.Equal(5, rule.HeldFrames);
    }

    [Fact]
    public void TheReissueItselfPassesThroughTheHook()
    {
        var rule = new SaveFlushRule(settleFrames: 0);
        rule.OnSaveRequested(1);
        rule.OnFrame(flushedOne: true);
        Assert.Equal(SaveFlushStep.ReissueClean, rule.OnFrame(flushedOne: false));

        // The host now calls the engine's SaveGame, which re-enters the hook: with the rule
        // already in Quiet the request must pass, or the re-issue would hold itself forever.
        Assert.Equal(SaveFlushRequest.PassThrough, rule.OnSaveRequested(0));
        Assert.Equal(SaveFlushRequest.PassThrough, rule.OnSaveRequested(1));
        Assert.Equal(SaveFlushPhase.Quiet, rule.Phase);
    }

    [Fact]
    public void TheQuietWindowParksTheFrameThenReleases()
    {
        var rule = new SaveFlushRule(quietFrames: 3, settleFrames: 0);
        rule.OnSaveRequested(1);
        Assert.Equal(SaveFlushStep.ReissueClean, rule.OnFrame(flushedOne: false));

        Assert.Equal(SaveFlushStep.KeepQuiet, rule.OnFrame(false));
        Assert.Equal(SaveFlushStep.KeepQuiet, rule.OnFrame(false));
        Assert.Equal(SaveFlushStep.Release, rule.OnFrame(false));
        Assert.Equal(SaveFlushPhase.Idle, rule.Phase);

        // Idle again: a fresh request with bodies standing starts a fresh hold.
        Assert.Equal(SaveFlushRequest.Hold, rule.OnSaveRequested(2));
        Assert.Equal(0, rule.HeldFrames);
        Assert.Equal(2, rule.StandingAtHold);
    }

    [Fact]
    public void ABodyThatKeepsRefusingToDieCannotLoseTheSave()
    {
        // The engine refuses a destroy while the script VM is mid-call; a body stuck that way
        // reports "flushed one" every frame. The budget ends the hold with a re-issue, never
        // with silence.
        var rule = new SaveFlushRule(budgetFrames: 10, quietFrames: 2, settleFrames: 1);
        rule.OnSaveRequested(1);

        for (int frame = 1; frame < 10; frame++)
        {
            Assert.Equal(SaveFlushStep.KeepHolding, rule.OnFrame(flushedOne: true));
        }

        Assert.Equal(SaveFlushStep.ReissueUnclean, rule.OnFrame(flushedOne: true));
        Assert.Equal(10, rule.HeldFrames);
        Assert.Equal(SaveFlushPhase.Quiet, rule.Phase);
        Assert.Equal(SaveFlushStep.KeepQuiet, rule.OnFrame(false));
        Assert.Equal(SaveFlushStep.Release, rule.OnFrame(false));
    }

    [Fact]
    public void ASecondRequestDuringTheHoldIsCoalescedNotDoubled()
    {
        // F4 pressed twice inside the hold: one re-issue, the latest arguments (the host
        // replaces its capture on every Hold answer).
        var rule = new SaveFlushRule(settleFrames: 0);
        Assert.Equal(SaveFlushRequest.Hold, rule.OnSaveRequested(2));
        rule.OnFrame(flushedOne: true);
        Assert.Equal(SaveFlushRequest.Hold, rule.OnSaveRequested(1));
        Assert.Equal(2, rule.Requests);
        Assert.Equal(2, rule.StandingAtHold); // the count at the ORIGINAL hold is what the log quotes

        rule.OnFrame(flushedOne: true);
        Assert.Equal(SaveFlushStep.ReissueClean, rule.OnFrame(flushedOne: false));

        int reissues = 0;
        for (int i = 0; i < 10; i++)
        {
            SaveFlushStep step = rule.OnFrame(false);
            if (step is SaveFlushStep.ReissueClean or SaveFlushStep.ReissueUnclean)
            {
                reissues++;
            }
        }

        Assert.Equal(0, reissues);
        Assert.Equal(SaveFlushPhase.Idle, rule.Phase);
    }

    [Fact]
    public void ACleanFrameResetsTheSettleCountWhenABodyTurnsUpAgain()
    {
        // Clean, then a straggler (a refused destroy that succeeded late): the settle count
        // starts over, so the re-issue never lands one frame after a destroy.
        var rule = new SaveFlushRule(settleFrames: 1);
        rule.OnSaveRequested(1);
        Assert.Equal(SaveFlushStep.KeepHolding, rule.OnFrame(flushedOne: false)); // settle 1
        Assert.Equal(SaveFlushStep.KeepHolding, rule.OnFrame(flushedOne: true));  // straggler
        Assert.Equal(SaveFlushStep.KeepHolding, rule.OnFrame(flushedOne: false)); // settle 1 again
        Assert.Equal(SaveFlushStep.ReissueClean, rule.OnFrame(flushedOne: false));
    }

    [Fact]
    public void ResetDropsTheHoldWithoutReissuing()
    {
        var rule = new SaveFlushRule();
        rule.OnSaveRequested(4);
        rule.OnFrame(flushedOne: true);

        rule.Reset();

        Assert.Equal(SaveFlushPhase.Idle, rule.Phase);
        Assert.Equal(SaveFlushStep.Idle, rule.OnFrame(false));
        Assert.Equal(SaveFlushRequest.PassThrough, rule.OnSaveRequested(0));
    }

    [Fact]
    public void TheShippedBudgetIsTheSameOrderAsTheTransitionWindow()
    {
        // The door-transition flush window is ~150 frames for at most 16 bodies; a hold that
        // could outlast it would be a worse hitch than the ghost it prevents.
        Assert.InRange(SaveFlushRule.DefaultBudgetFrames, 60, 150);
        Assert.InRange(SaveFlushRule.DefaultQuietFrames, 1, 10);
        Assert.True(SaveFlushRule.DefaultSettleFrames >= 1);
    }
}

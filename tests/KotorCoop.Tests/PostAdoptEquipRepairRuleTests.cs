using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="PostAdoptEquipRepairRule"/> — the post-adoption starting-gear repair's own
/// wait-for-the-load-then-poll state machine (see <see cref="PostAdoptEquipRepairController"/>'s
/// doc comment for why the repair moved here from the pre-save location rounds 1-3 used, and
/// round 4's live evidence for the correction: <c>0x7FFFFFFF</c> is the engine's permanent player
/// self-id, not a placeholder waiting to resolve — the actual gap was the two candidate
/// repositories never getting linked inside the isolated chargen module).
/// </summary>
public class PostAdoptEquipRepairRuleTests
{
    private const uint Baseline = 5;

    [Fact]
    public void StillWaitingForTheLoadWhenAppliedHasNotMoved() =>
        Assert.Equal(
            PostAdoptEquipRepairAction.Wait,
            PostAdoptEquipRepairRule.Decide(
                waitingForLoad: true, menuLoadApplied: Baseline, baselineMenuLoadApplied: Baseline,
                menuLoadStatus: 0, nowMs: 1000, nextPollMs: 0, pollsLeft: -1));

    /// <summary>A DIFFERENT serial with a nonzero status is the adoption's own load failing —
    /// AdoptAndReconnectAsync already told the player why; there is nothing left to repair.
    /// </summary>
    [Fact]
    public void GivesUpWhenTheAdoptedLoadFailed() =>
        Assert.Equal(
            PostAdoptEquipRepairAction.GiveUpLoadFailed,
            PostAdoptEquipRepairRule.Decide(
                waitingForLoad: true, menuLoadApplied: Baseline + 1, baselineMenuLoadApplied: Baseline,
                menuLoadStatus: 5, nowMs: 1000, nextPollMs: 0, pollsLeft: -1));

    /// <summary>A DIFFERENT serial with status 0 is the world-up ack — the exact moment to start
    /// polling.</summary>
    [Fact]
    public void StartsPollingOnceTheLoadAcksSuccessfully() =>
        Assert.Equal(
            PostAdoptEquipRepairAction.StartPolling,
            PostAdoptEquipRepairRule.Decide(
                waitingForLoad: true, menuLoadApplied: Baseline + 1, baselineMenuLoadApplied: Baseline,
                menuLoadStatus: 0, nowMs: 1000, nextPollMs: 0, pollsLeft: -1));

    /// <summary>SAME serial as baseline, even with status 0 (a stale status field from before
    /// this controller was armed) — not a real ack, keep waiting.</summary>
    [Fact]
    public void AnUnchangedSerialIsNeverTakenAsAnAckEvenWithAZeroStatus() =>
        Assert.Equal(
            PostAdoptEquipRepairAction.Wait,
            PostAdoptEquipRepairRule.Decide(
                waitingForLoad: true, menuLoadApplied: Baseline, baselineMenuLoadApplied: Baseline,
                menuLoadStatus: 0, nowMs: 1000, nextPollMs: 0, pollsLeft: -1));

    [Fact]
    public void WaitsForTheNextPollIntervalOnceAlreadyPolling() =>
        Assert.Equal(
            PostAdoptEquipRepairAction.WaitForNextPoll,
            PostAdoptEquipRepairRule.Decide(
                waitingForLoad: false, menuLoadApplied: Baseline + 1, baselineMenuLoadApplied: Baseline,
                menuLoadStatus: 0, nowMs: 1000, nextPollMs: 1250, pollsLeft: 10));

    [Fact]
    public void PollsOnceTheIntervalHasElapsed() =>
        Assert.Equal(
            PostAdoptEquipRepairAction.Poll,
            PostAdoptEquipRepairRule.Decide(
                waitingForLoad: false, menuLoadApplied: Baseline + 1, baselineMenuLoadApplied: Baseline,
                menuLoadStatus: 0, nowMs: 1250, nextPollMs: 1250, pollsLeft: 10));

    [Fact]
    public void GivesUpOnceThePollBudgetIsExhausted() =>
        Assert.Equal(
            PostAdoptEquipRepairAction.GiveUpPollBudgetExhausted,
            PostAdoptEquipRepairRule.Decide(
                waitingForLoad: false, menuLoadApplied: Baseline + 1, baselineMenuLoadApplied: Baseline,
                menuLoadStatus: 0, nowMs: 5000, nextPollMs: 1250, pollsLeft: 0));

    /// <summary>The budget check comes before the interval check — a poll that just used its last
    /// budgeted attempt and failed must stop, not wait for one more interval that would only
    /// produce a poll with nothing left to spend.</summary>
    [Fact]
    public void TheExhaustedBudgetIsCheckedBeforeTheNextPollInterval() =>
        Assert.Equal(
            PostAdoptEquipRepairAction.GiveUpPollBudgetExhausted,
            PostAdoptEquipRepairRule.Decide(
                waitingForLoad: false, menuLoadApplied: Baseline + 1, baselineMenuLoadApplied: Baseline,
                menuLoadStatus: 0, nowMs: 1000, nextPollMs: 999_999, pollsLeft: 0));
}

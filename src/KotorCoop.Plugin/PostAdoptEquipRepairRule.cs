namespace KotorCoop.Plugin;

/// <summary>What <see cref="PostAdoptEquipRepairController"/> should do this poll, decided by
/// <see cref="PostAdoptEquipRepairRule.Decide"/>.</summary>
public enum PostAdoptEquipRepairAction
{
    /// <summary>The adopted-world load has not acked yet (or nothing is armed) — do nothing.
    /// </summary>
    Wait,

    /// <summary>The load acked with a failure status — disarm; there is nothing to repair.
    /// </summary>
    GiveUpLoadFailed,

    /// <summary>The load just acked successfully — start the repository-population poll.
    /// </summary>
    StartPolling,

    /// <summary>Already polling, but <see cref="PostAdoptEquipRepairController.PollIntervalMs"/>
    /// has not elapsed since the last attempt.</summary>
    WaitForNextPoll,

    /// <summary>Try <see cref="Odyssey.EquipUnequippedStartingGear"/> now.</summary>
    Poll,

    /// <summary>The poll budget ran out without ever repairing anything — disarm.</summary>
    GiveUpPollBudgetExhausted,
}

/// <summary>
/// Pure state-transition logic for <see cref="PostAdoptEquipRepairController"/>, factored out so
/// it can be tested without a live game — the same split this project already applies to
/// <see cref="NewCharacterCaptureRule"/> and <see cref="StartingGearSlotRule"/>. See
/// <see cref="PostAdoptEquipRepairController"/>'s own doc comment for why this exists and what it
/// decides between: waiting for the adopted-world load's own ack (<c>block.MenuLoadApplied</c>/
/// <c>MenuLoadStatus</c>), versus polling the repair itself once that ack lands.
/// </summary>
public static class PostAdoptEquipRepairRule
{
    /// <param name="waitingForLoad">True while this controller has not yet seen the adopted-world
    /// load's own ack (<see cref="PostAdoptEquipRepairController"/>'s <c>_pollsLeft &lt; 0</c>
    /// state).</param>
    /// <param name="menuLoadApplied">The shared block's current <c>MenuLoadApplied</c>.</param>
    /// <param name="baselineMenuLoadApplied">What <c>MenuLoadApplied</c> read at arm time — the
    /// load this controller is NOT interested in.</param>
    /// <param name="menuLoadStatus">The shared block's current <c>MenuLoadStatus</c>, meaningful
    /// only once <paramref name="menuLoadApplied"/> has moved past the baseline.</param>
    /// <param name="nowMs">The current tick count.</param>
    /// <param name="nextPollMs">The tick count the next repair attempt is allowed at, meaningful
    /// only while already polling (<paramref name="waitingForLoad"/> false).</param>
    /// <param name="pollsLeft">Repair attempts still budgeted, meaningful only while already
    /// polling. Non-positive means the budget is exhausted.</param>
    public static PostAdoptEquipRepairAction Decide(
        bool waitingForLoad,
        uint menuLoadApplied,
        uint baselineMenuLoadApplied,
        int menuLoadStatus,
        long nowMs,
        long nextPollMs,
        int pollsLeft)
    {
        if (waitingForLoad)
        {
            if (menuLoadApplied == baselineMenuLoadApplied)
            {
                return PostAdoptEquipRepairAction.Wait;
            }

            return menuLoadStatus != 0
                ? PostAdoptEquipRepairAction.GiveUpLoadFailed
                : PostAdoptEquipRepairAction.StartPolling;
        }

        if (pollsLeft <= 0)
        {
            return PostAdoptEquipRepairAction.GiveUpPollBudgetExhausted;
        }

        return nowMs < nextPollMs ? PostAdoptEquipRepairAction.WaitForNextPoll : PostAdoptEquipRepairAction.Poll;
    }
}

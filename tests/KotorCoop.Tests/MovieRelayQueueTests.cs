using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The decision half of the mid-module movie relay (docs/research/trigger-relay.md §6): given
/// facts about the receiving peer already measured this frame, what should happen to an
/// announced movie. Pure arithmetic, same shape as MovieStagingTests — no engine underneath.
/// </summary>
public class MovieRelayQueueTests
{
    [Fact]
    public void AnIdlePeerWithNothingElseGoingOnPlaysItNow()
    {
        Assert.Equal(
            MovieRelayAction.PlayNow,
            MovieRelayQueue.Decide(
                sameModule: true, hasMovieName: true, isIdle: true,
                inDialogue: false, inCombat: false, alreadyQueued: 0));
    }

    [Fact]
    public void APeerMidDialogueDefersRatherThanInterrupting()
    {
        Assert.Equal(
            MovieRelayAction.DeferToNextTransition,
            MovieRelayQueue.Decide(
                sameModule: true, hasMovieName: true, isIdle: true,
                inDialogue: true, inCombat: false, alreadyQueued: 0));
    }

    [Fact]
    public void APeerMidCombatDefersRatherThanInterrupting()
    {
        Assert.Equal(
            MovieRelayAction.DeferToNextTransition,
            MovieRelayQueue.Decide(
                sameModule: true, hasMovieName: true, isIdle: true,
                inDialogue: false, inCombat: true, alreadyQueued: 0));
    }

    [Fact]
    public void ANotIdlePeerDefersEvenWithoutDialogueOrCombat()
    {
        // Not idle covers mid-transition and mid-minigame — states nothing here has enough
        // information to distinguish further, so the safe answer is the same as dialogue or
        // combat: wait for the next transition instead of guessing it is safe to block.
        Assert.Equal(
            MovieRelayAction.DeferToNextTransition,
            MovieRelayQueue.Decide(
                sameModule: true, hasMovieName: true, isIdle: false,
                inDialogue: false, inCombat: false, alreadyQueued: 0));
    }

    [Fact]
    public void ADifferentModuleIsIgnoredRegardlessOfEverythingElse()
    {
        Assert.Equal(
            MovieRelayAction.Ignore,
            MovieRelayQueue.Decide(
                sameModule: false, hasMovieName: true, isIdle: true,
                inDialogue: false, inCombat: false, alreadyQueued: 0));
    }

    [Fact]
    public void NoMovieNameIsIgnored()
    {
        Assert.Equal(
            MovieRelayAction.Ignore,
            MovieRelayQueue.Decide(
                sameModule: true, hasMovieName: false, isIdle: true,
                inDialogue: false, inCombat: false, alreadyQueued: 0));
    }

    [Fact]
    public void AnEngineOwnedTransitionQueueAlreadyInFlightIsIgnoredNotStacked()
    {
        // The game itself has already queued a transition movie here; piling a mid-module
        // announcement on top would silently change what plays on the next transition, which
        // is not this channel's call to make — see MovieStaging.RefuseQueueBusy for the
        // identical reasoning on the staging side.
        Assert.Equal(
            MovieRelayAction.Ignore,
            MovieRelayQueue.Decide(
                sameModule: true, hasMovieName: true, isIdle: true,
                inDialogue: false, inCombat: false, alreadyQueued: 1));
    }

    [Fact]
    public void ABusyQueueOverridesEvenAnIdlePeer()
    {
        Assert.Equal(
            MovieRelayAction.Ignore,
            MovieRelayQueue.Decide(
                sameModule: true, hasMovieName: true, isIdle: true,
                inDialogue: true, inCombat: true, alreadyQueued: 3));
    }
}

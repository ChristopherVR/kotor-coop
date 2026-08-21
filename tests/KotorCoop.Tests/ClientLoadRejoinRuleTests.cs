using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ClientLoadRejoinRule"/> — the rejoin decision state machine roadmap item 14
/// phase 2 needed: edges (a real trigger starts a rejoin), debounce/idempotence (a second
/// trigger while one is already running changes nothing), and the gate folded into the same
/// decision rather than left to the caller. "Ignore the load that IS the adoption" is proven
/// at the layer below this one (<see cref="SerialEdgeWatchTests"/>/<see cref="SaveIdentityWatchTests"/>'s
/// "first reading is the baseline, not a change") — this state machine never even sees a
/// <see cref="RejoinTrigger"/> for that case, so there is nothing for it to ignore.
/// </summary>
public sealed class ClientLoadRejoinRuleTests
{
    [Fact]
    public void NoTriggerNeverStartsARejoin()
    {
        (bool shouldStart, RejoinDecisionState next) =
            ClientLoadRejoinRule.Decide(RejoinDecisionState.Idle, RejoinTrigger.None, gateEnabled: true);

        Assert.False(shouldStart);
        Assert.Equal(RejoinPhase.Idle, next.Phase);
    }

    [Fact]
    public void ATriggerWithTheGateOffDoesNothing()
    {
        (bool shouldStart, RejoinDecisionState next) =
            ClientLoadRejoinRule.Decide(RejoinDecisionState.Idle, RejoinTrigger.OwnLoad, gateEnabled: false);

        Assert.False(shouldStart);
        Assert.Equal(RejoinPhase.Idle, next.Phase);
    }

    [Fact]
    public void AGenuineTriggerWithTheGateOnStartsARejoinAndMovesToInFlight()
    {
        (bool shouldStart, RejoinDecisionState next) =
            ClientLoadRejoinRule.Decide(RejoinDecisionState.Idle, RejoinTrigger.OwnLoad, gateEnabled: true);

        Assert.True(shouldStart);
        Assert.Equal(RejoinPhase.InFlight, next.Phase);
    }

    [Fact]
    public void AHostReloadTriggerBehavesTheSameWayAsAnOwnLoadTrigger()
    {
        (bool shouldStart, RejoinDecisionState next) =
            ClientLoadRejoinRule.Decide(RejoinDecisionState.Idle, RejoinTrigger.HostReload, gateEnabled: true);

        Assert.True(shouldStart);
        Assert.Equal(RejoinPhase.InFlight, next.Phase);
    }

    [Fact]
    public void ASecondTriggerWhileInFlightIsSwallowedNotQueuedOrRestarted()
    {
        RejoinDecisionState state = new(RejoinPhase.InFlight);

        (bool shouldStart, RejoinDecisionState next) =
            ClientLoadRejoinRule.Decide(state, RejoinTrigger.OwnLoad, gateEnabled: true);

        Assert.False(shouldStart);
        Assert.Equal(RejoinPhase.InFlight, next.Phase); // still in flight, unchanged
    }

    [Fact]
    public void CompletingReturnsToIdleAndFreesTheNextTrigger()
    {
        RejoinDecisionState inFlight = new(RejoinPhase.InFlight);

        RejoinDecisionState idle = ClientLoadRejoinRule.Complete(inFlight);
        Assert.Equal(RejoinPhase.Idle, idle.Phase);

        (bool shouldStart, _) = ClientLoadRejoinRule.Decide(idle, RejoinTrigger.OwnLoad, gateEnabled: true);
        Assert.True(shouldStart);
    }

    [Fact]
    public void CompletingAnAlreadyIdleStateIsANoOp()
    {
        RejoinDecisionState idle = ClientLoadRejoinRule.Complete(RejoinDecisionState.Idle);
        Assert.Equal(RejoinPhase.Idle, idle.Phase);
    }
}

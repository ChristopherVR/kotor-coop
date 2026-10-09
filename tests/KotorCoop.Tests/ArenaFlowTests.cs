using KotorCoop.Memory.GameFiles;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// What one game does about an arena round (<see cref="ArenaFlow"/>): the host's single warp each
/// way, every player's spawn, and the way home. Pure: no game, no clock.
/// </summary>
public class ArenaFlowTests
{
    private const uint Host = 1;
    private const uint Aria = 2;
    private const uint Bao = 3;
    private const string Home = "danm13";
    private const string Arena = PvpDuelRound.ArenaModule;

    private static readonly Vector3f HomeSpot = new(40f, 12f, 1f);

    private static PvpView View(PvpPhase phase, uint challenger = Host, uint target = Aria, uint round = 1) =>
        new(phase, round, challenger, target, Arena: true);

    private static Vector3f InArenaAt(ArenaSpawn spawn) => new(spawn.X, spawn.Y, spawn.Z);

    // ---- the host's warps -------------------------------------------------------------------

    [Fact]
    public void TheHostAsksForOneWarpIntoTheArenaWhenTheRoundEnters()
    {
        ArenaFlow flow = new();

        ArenaStep step = flow.Step(View(PvpPhase.Entering), Host, isHost: true, Home, HomeSpot, 1_000);

        Assert.Equal(ArenaStepKind.Travel, step.Kind);
        Assert.Equal(ArenaPurpose.Enter, step.Purpose);
        Assert.Equal(Arena, step.Module);

        // Accepted: never asked again, however long the load takes.
        flow.Confirm(step, accepted: true, 1_000);
        for (long t = 2_000; t < 60_000; t += 500)
        {
            Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, t).Kind);
        }
    }

    [Fact]
    public void ARefusedWarpIsRetriedAFewTimesThenLeftToTheTimeout()
    {
        ArenaFlow flow = new();
        int asked = 0;

        for (long t = 1_000; t < 200_000; t += 500)
        {
            ArenaStep step = flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, t);
            if (step.Kind == ArenaStepKind.Travel)
            {
                asked++;
                flow.Confirm(step, accepted: false, t);
            }
        }

        Assert.Equal(ArenaFlow.MaxTravelTries, asked);
    }

    [Fact]
    public void ARefusedWarpIsNotRetriedInAHurry()
    {
        ArenaFlow flow = new();
        ArenaStep first = flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, 1_000);
        flow.Confirm(first, accepted: false, 1_000);

        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, 1_500).Kind);
        Assert.Equal(
            ArenaStepKind.Travel,
            flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, 1_000 + ArenaFlow.TravelRetryMs).Kind);
    }

    [Fact]
    public void ANonHostNeverAsksForAWarp()
    {
        ArenaFlow flow = new();

        foreach (PvpPhase phase in new[] { PvpPhase.Entering, PvpPhase.Countdown, PvpPhase.Live, PvpPhase.Leaving })
        {
            Assert.NotEqual(
                ArenaStepKind.Travel,
                flow.Step(View(phase), Aria, isHost: false, Home, HomeSpot, 10_000).Kind);
        }
    }

    [Fact]
    public void TheHostDoesNotWarpWhileItsModuleIsUnreadableOrAlreadyTheArena()
    {
        ArenaFlow mid = new();
        Assert.Equal(
            ArenaStepKind.None,
            mid.Step(View(PvpPhase.Entering), Host, true, "area0123abcd", HomeSpot, 1_000).Kind);

        ArenaFlow there = new();
        Assert.Equal(
            ArenaStepKind.None,
            there.Step(View(PvpPhase.Entering), Host, true, Arena, HomeSpot, 1_000).Kind);
    }

    [Fact]
    public void TheHostWarpsBackToWhereItStoodAfterTheFightHasBeenReadable()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, 1_000);
        flow.Confirm(new ArenaStep(ArenaStepKind.Travel, ArenaPurpose.Enter, Arena), true, 1_000);

        Vector3f here = InArenaAt(ArenaModule.SpawnFor(0, 0));
        flow.Step(View(PvpPhase.Live), Host, true, Arena, here, 20_000);

        // The fight just ended: give the result a moment.
        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Leaving), Host, true, Arena, here, 30_000).Kind);
        Assert.Equal(
            ArenaStepKind.None,
            flow.Step(View(PvpPhase.Leaving), Host, true, Arena, here, 30_000 + ArenaFlow.LeaveDelayMs - 1).Kind);

        ArenaStep back = flow.Step(View(PvpPhase.Leaving), Host, true, Arena, here, 30_000 + ArenaFlow.LeaveDelayMs);
        Assert.Equal(ArenaStepKind.Travel, back.Kind);
        Assert.Equal(ArenaPurpose.Return, back.Purpose);
        Assert.Equal(Home, back.Module);

        // One warp: once the plugin has it, no second request.
        flow.Confirm(back, true, 32_000);
        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Leaving), Host, true, Arena, here, 40_000).Kind);
    }

    [Fact]
    public void WithNoWayHomeKnownTheHostDoesNotWarpAtAll()
    {
        // An agent that started with the round already running in the arena never saw the origin.
        ArenaFlow flow = new();
        Vector3f here = InArenaAt(ArenaModule.SpawnFor(0, 0));

        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Leaving), Host, true, Arena, here, 1_000).Kind);
        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Leaving), Host, true, Arena, here, 60_000).Kind);
        Assert.False(flow.Origin.Known);
    }

    // ---- placement --------------------------------------------------------------------------

    [Theory]
    [InlineData(Host, 0)]   // the challenger: team A
    [InlineData(Aria, 1)]   // the target: team B
    public void EachDuellistIsSentToTheirOwnEndOfTheRing(uint peer, int team)
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), peer, peer == Host, Home, HomeSpot, 1_000);

        ArenaStep step = default;
        for (long t = 10_000; t < 14_000 && step.Kind == ArenaStepKind.None; t += 250)
        {
            step = flow.Step(View(PvpPhase.Countdown), peer, peer == Host, Arena, new Vector3f(1f, 1f, 0f), t);
        }

        ArenaSpawn expected = ArenaModule.SpawnFor(team, 0);
        Assert.Equal(ArenaStepKind.Teleport, step.Kind);
        Assert.Equal(ArenaPurpose.Place, step.Purpose);
        Assert.Equal((expected.X, expected.Y, expected.Z), (step.Position.X, step.Position.Y, step.Position.Z));
    }

    [Fact]
    public void ABystanderIsSentToTheSpectatorMark()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Bao, false, Home, HomeSpot, 1_000);

        ArenaStep step = default;
        for (long t = 10_000; t < 14_000 && step.Kind == ArenaStepKind.None; t += 250)
        {
            step = flow.Step(View(PvpPhase.Countdown), Bao, false, Arena, new Vector3f(1f, 1f, 0f), t);
        }

        Assert.Equal(ArenaStepKind.Teleport, step.Kind);
        Assert.Equal(ArenaModule.Spectator.X, step.Position.X);
        Assert.Equal(ArenaModule.Spectator.Y, step.Position.Y);
    }

    [Fact]
    public void NobodyIsMovedBeforeTheyHaveSettledInTheArena()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);
        Vector3f arrival = InArenaAt(ArenaModule.SpawnFor(0, 0));

        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Countdown), Aria, false, Arena, arrival, 10_000).Kind);
        Assert.Equal(
            ArenaStepKind.None,
            flow.Step(View(PvpPhase.Countdown), Aria, false, Arena, arrival, 10_000 + ArenaFlow.SettleBeforeActMs - 1).Kind);
        Assert.Equal(
            ArenaStepKind.Teleport,
            flow.Step(View(PvpPhase.Countdown), Aria, false, Arena, arrival, 10_000 + ArenaFlow.SettleBeforeActMs).Kind);
    }

    [Fact]
    public void ALandedPlayerIsLeftAlone()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, 1_000);
        Vector3f spot = InArenaAt(ArenaModule.SpawnFor(0, 0));

        flow.Step(View(PvpPhase.Countdown), Host, true, Arena, spot, 10_000);
        for (long t = 11_500; t < 30_000; t += 250)
        {
            Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Live), Host, true, Arena, spot, t).Kind);
        }
    }

    [Fact]
    public void ATeleportThatDoesNotLandIsRetriedThenGivenUp()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);
        int tries = 0;

        for (long t = 10_000; t < 120_000; t += 250)
        {
            ArenaStep step = flow.Step(View(PvpPhase.Live), Aria, false, Arena, new Vector3f(0f, 0f, 0f), t);
            if (step.Kind == ArenaStepKind.Teleport)
            {
                tries++;
                flow.Confirm(step, accepted: true, t);
            }
        }

        Assert.Equal(ArenaFlow.MaxTeleportTries, tries);
    }

    [Fact]
    public void ARetryWaitsAboutASecond()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);
        ArenaStep first = flow.Step(View(PvpPhase.Countdown), Aria, false, Arena, default, 10_000);
        flow.Step(View(PvpPhase.Countdown), Aria, false, Arena, default, 12_000);
        first = flow.Step(View(PvpPhase.Countdown), Aria, false, Arena, default, 12_000);
        Assert.Equal(ArenaStepKind.Teleport, first.Kind);
        flow.Confirm(first, true, 12_000);

        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Countdown), Aria, false, Arena, default, 12_500).Kind);
        Assert.Equal(
            ArenaStepKind.Teleport,
            flow.Step(View(PvpPhase.Countdown), Aria, false, Arena, default, 12_000 + ArenaFlow.TeleportRetryMs).Kind);
    }

    // ---- the way home -----------------------------------------------------------------------

    [Fact]
    public void EveryoneIsPutBackOnTheirSpotOnceBackInTheModule()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);

        // Still in the arena while the host's warp is under way: nothing to do.
        Assert.Equal(
            ArenaStepKind.None,
            flow.Step(View(PvpPhase.Leaving), Aria, false, Arena, InArenaAt(ArenaModule.SpawnFor(1, 0)), 40_000).Kind);

        // Back in the module, but arriving at its entry point rather than the old spot.
        Vector3f atEntry = new(100f, 100f, 0f);
        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Leaving), Aria, false, Home, atEntry, 50_000).Kind);
        ArenaStep restore = flow.Step(View(PvpPhase.Leaving), Aria, false, Home, atEntry, 50_000 + ArenaFlow.SettleBeforeActMs);

        Assert.Equal(ArenaStepKind.Teleport, restore.Kind);
        Assert.Equal(ArenaPurpose.Restore, restore.Purpose);
        Assert.Equal(HomeSpot, restore.Position);

        // Landed: done.
        Assert.Equal(
            ArenaStepKind.None,
            flow.Step(View(PvpPhase.Leaving), Aria, false, Home, HomeSpot, 53_000).Kind);
        Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Leaving), Aria, false, Home, atEntry, 60_000).Kind);
    }

    [Fact]
    public void APlayerWhoNeverLeftIsNotMovedAnywhere()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);

        for (long t = 5_000; t < 60_000; t += 500)
        {
            Assert.Equal(ArenaStepKind.None, flow.Step(View(PvpPhase.Leaving), Aria, false, Home, HomeSpot, t).Kind);
        }
    }

    [Fact]
    public void ARestoreThatDoesNotLandIsRetriedThenGivenUp()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);
        int tries = 0;

        for (long t = 10_000; t < 120_000; t += 250)
        {
            ArenaStep step = flow.Step(View(PvpPhase.Leaving), Aria, false, Home, new Vector3f(0f, 0f, 0f), t);
            if (step.Kind == ArenaStepKind.Teleport)
            {
                tries++;
                flow.Confirm(step, true, t);
            }
        }

        Assert.Equal(ArenaFlow.MaxTeleportTries, tries);
    }

    // ---- after the round --------------------------------------------------------------------

    [Fact]
    public void AHostLeftInTheArenaWhenTheRoundLetGoIsTakenHomeOnceInAWhile()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, 1_000);
        Vector3f here = InArenaAt(ArenaModule.SpawnFor(0, 0));
        flow.Step(View(PvpPhase.Live), Host, true, Arena, here, 20_000);

        PvpView idle = PvpView.Idle;
        Assert.Equal(ArenaStepKind.None, flow.Step(idle, Host, true, Arena, here, 100_000).Kind);
        Assert.Equal(
            ArenaStepKind.None,
            flow.Step(idle, Host, true, Arena, here, 100_000 + ArenaFlow.RescueAfterIdleMs - 1).Kind);

        ArenaStep rescue = flow.Step(idle, Host, true, Arena, here, 100_000 + ArenaFlow.RescueAfterIdleMs);
        Assert.Equal(ArenaStepKind.Travel, rescue.Kind);
        Assert.Equal(ArenaPurpose.Rescue, rescue.Purpose);
        Assert.Equal(Home, rescue.Module);
        flow.Confirm(rescue, true, 105_000);

        Assert.Equal(ArenaStepKind.None, flow.Step(idle, Host, true, Arena, here, 110_000).Kind);
        Assert.Equal(
            ArenaStepKind.Travel,
            flow.Step(idle, Host, true, Arena, here, 105_000 + ArenaFlow.RescueRetryMs).Kind);
    }

    [Fact]
    public void ANonHostNeverRescues()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);
        Vector3f here = InArenaAt(ArenaModule.SpawnFor(1, 0));
        flow.Step(View(PvpPhase.Live), Aria, false, Arena, here, 20_000);

        Assert.Equal(ArenaStepKind.None, flow.Step(PvpView.Idle, Aria, false, Arena, here, 200_000).Kind);
    }

    [Fact]
    public void TheRoundIsForgottenOnceEveryoneIsHomeAndQuiet()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);
        flow.Step(View(PvpPhase.Leaving), Aria, false, Home, HomeSpot, 10_000);
        flow.Step(View(PvpPhase.Leaving), Aria, false, Home, HomeSpot, 12_000);

        Assert.True(flow.Tracking);
        flow.Step(PvpView.Idle, Aria, false, Home, HomeSpot, 20_000);
        flow.Step(PvpView.Idle, Aria, false, Home, HomeSpot, 20_000 + ArenaFlow.RescueAfterIdleMs);

        Assert.False(flow.Tracking);
    }

    [Fact]
    public void ALateArrivalHomeStillGetsPutBackAfterTheRoundLetGo()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Aria, false, Home, HomeSpot, 1_000);
        flow.Step(View(PvpPhase.Leaving), Aria, false, Arena, InArenaAt(ArenaModule.SpawnFor(1, 0)), 10_000);

        // The round went idle while Aria was still loading back.
        Vector3f atEntry = new(100f, 100f, 0f);
        flow.Step(PvpView.Idle, Aria, false, Home, atEntry, 80_000);
        ArenaStep restore = flow.Step(PvpView.Idle, Aria, false, Home, atEntry, 80_000 + ArenaFlow.SettleBeforeActMs);

        Assert.Equal(ArenaPurpose.Restore, restore.Purpose);
        Assert.Equal(HomeSpot, restore.Position);
    }

    [Fact]
    public void ANewRoundStartsFromScratch()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering, round: 1), Host, true, Home, HomeSpot, 1_000);
        flow.Confirm(new ArenaStep(ArenaStepKind.Travel, ArenaPurpose.Enter, Arena), true, 1_000);

        // Round 2 begins elsewhere: the origin is the new spot, and the host asks for its warp again.
        Vector3f other = new(5f, 6f, 0f);
        ArenaStep step = flow.Step(View(PvpPhase.Entering, round: 2), Host, true, "tar_m02aa", other, 100_000);

        Assert.Equal(ArenaStepKind.Travel, step.Kind);
        Assert.Equal(("tar_m02aa", other, true), flow.Origin);
    }

    [Fact]
    public void NothingHappensWithoutAnArenaRound()
    {
        ArenaFlow flow = new();

        Assert.Equal(ArenaStepKind.None, flow.Step(PvpView.Idle, Host, true, Home, HomeSpot, 1_000).Kind);
        Assert.False(flow.Tracking);

        // A plain duel is not an arena round.
        PvpView duel = new(PvpPhase.Live, 1, Host, Aria);
        Assert.Equal(ArenaStepKind.None, flow.Step(duel, Host, true, Home, HomeSpot, 2_000).Kind);
        Assert.False(flow.Tracking);

        // And a challenge that has not been answered yet moves nobody.
        PvpView asked = new(PvpPhase.Challenged, 0, Host, Aria, Arena: true);
        Assert.Equal(ArenaStepKind.None, flow.Step(asked, Host, true, Home, HomeSpot, 3_000).Kind);
        Assert.False(flow.Tracking);
    }

    [Fact]
    public void ResetForgetsEverything()
    {
        ArenaFlow flow = new();
        flow.Step(View(PvpPhase.Entering), Host, true, Home, HomeSpot, 1_000);
        Assert.True(flow.Tracking);

        flow.Reset();

        Assert.False(flow.Tracking);
        Assert.False(flow.Origin.Known);
    }

    // ---- the view ---------------------------------------------------------------------------

    [Theory]
    [InlineData(PvpPhase.Idle, false)]
    [InlineData(PvpPhase.Challenged, false)]
    [InlineData(PvpPhase.Entering, true)]
    [InlineData(PvpPhase.Countdown, true)]
    [InlineData(PvpPhase.Live, true)]
    [InlineData(PvpPhase.Leaving, true)]
    public void SavingStaysBlockedFromTheWalkInUntilEveryoneIsBack(PvpPhase phase, bool blocked)
    {
        PvpView view = View(phase);

        Assert.Equal(blocked, view.ArenaActive);
        Assert.Equal(!blocked, PvpPumpRule.SaveAllowed(view, localPeerId: Bao));
    }

    [Fact]
    public void ALeavingDuellistIsNotPinnedOrHostile()
    {
        PvpView leaving = View(PvpPhase.Leaving);

        Assert.Equal((0u, 0u), PvpPumpRule.Levels(leaving, Aria, enabled: true));
        Assert.True(PvpPumpRule.CountsTowardWipe(leaving, Aria));
        Assert.Equal((0u, 0u), PvpPumpRule.Levels(View(PvpPhase.Entering), Aria, enabled: true));
    }

    [Fact]
    public void ADuelOutsideTheArenaStillBlocksOnlyTheDuellists()
    {
        PvpView plain = new(PvpPhase.Live, 1, Host, Aria);

        Assert.False(plain.ArenaActive);
        Assert.False(PvpPumpRule.SaveAllowed(plain, Aria));
        Assert.True(PvpPumpRule.SaveAllowed(plain, Bao));
    }
}

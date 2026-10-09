using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The arena rounds of the host's duel state machine (docs/research/pvp-plan.md, phases 3 and 4):
/// the extra refusals, the walk into the arena, the same fight there, and the walk back. Nothing
/// here touches a game or a socket.
/// </summary>
public class PvpArenaRoundTests
{
    private const uint Host = 1;
    private const uint Aria = 2;
    private const uint Bao = 3;
    private const string Home = "tar_m02aa";
    private const string Arena = PvpDuelRound.ArenaModule;

    private static PvpParticipant Well(string module = Home, bool arenaReady = true, int hp = 100) =>
        new(Present: true, Aware: true, Busy: false, Module: module, Health: hp, MaxHealth: 100, Downed: false, ArenaReady: arenaReady);

    private static PvpDuelRound Round() => new() { Enabled = true, ArenaEnabled = true, HostPeerId = Host };

    private static PvpNoticeCode[] Codes(PvpDuelRound round, uint forPeer) =>
        [.. round.DrainNotices().Where(n => n.ForPeer == forPeer).Select(n => n.Code)];

    private static Func<uint, PvpParticipant> World(
        PvpParticipant host, PvpParticipant aria, PvpParticipant? bao = null) =>
        id => id switch { Host => host, Aria => aria, Bao => bao ?? default, _ => default };

    private static Func<uint, PvpParticipant> EveryoneIn(int hpAria = 100) =>
        World(Well(Arena), Well(Arena, hp: hpAria));

    private static Func<uint, PvpParticipant> EveryoneHome() => World(Well(), Well());

    /// <summary>Challenge, accept, and tick until the arena has been entered and the countdown ran out.</summary>
    private static PvpDuelRound Live(long start = 0)
    {
        PvpDuelRound round = Entering(start);
        long t = start + 1_000;
        round.Tick(t, EveryoneIn());
        round.Tick(t + PvpDuelRound.ArenaSettleMs, EveryoneIn());
        Assert.Equal(PvpPhase.Countdown, round.Phase);
        round.Tick(t + PvpDuelRound.ArenaSettleMs + PvpDuelRound.CountdownMs, EveryoneIn());
        round.DrainNotices();
        Assert.Equal(PvpPhase.Live, round.Phase);
        return round;
    }

    private static PvpDuelRound Entering(long start = 0)
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), start, arena: true, host: Well());
        round.Accept(Aria, Well(), Well(), start);
        round.DrainNotices();
        Assert.Equal(PvpPhase.Entering, round.Phase);
        return round;
    }

    // ---- refusing an arena challenge --------------------------------------------------------

    [Fact]
    public void ArenaIsRefusedWhenTheHostOnlyAllowsDuels()
    {
        PvpDuelRound round = Round();
        round.ArenaEnabled = false;

        round.Challenge(Host, Aria, Well(), Well(), 0, arena: true, host: Well());

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Equal([PvpNoticeCode.ArenaNotEnabled], Codes(round, Host));

        // A plain duel is still fine.
        round.Challenge(Host, Aria, Well(), Well(), 0);
        Assert.Equal(PvpPhase.Challenged, round.Phase);
        Assert.False(round.Arena);
    }

    [Fact]
    public void ArenaNamesTheOneWhoDoesNotHaveTheArena()
    {
        PvpDuelRound round = Round();

        round.Challenge(Aria, Bao, Well(), Well(arenaReady: false), 0, arena: true, host: Well());
        PvpNotice target = Assert.Single(round.DrainNotices());
        Assert.Equal((Aria, PvpNoticeCode.ArenaMissing, Bao), (target.ForPeer, target.Code, target.Other));

        round.Challenge(Aria, Bao, Well(arenaReady: false), Well(), 0, arena: true, host: Well());
        PvpNotice self = Assert.Single(round.DrainNotices());
        Assert.Equal((Aria, PvpNoticeCode.ArenaMissing, Aria), (self.ForPeer, self.Code, self.Other));

        // Two clients fighting while the host has no arena: the host is the one who warps.
        round.Challenge(Aria, Bao, Well(), Well(), 0, arena: true, host: Well(arenaReady: false));
        PvpNotice host = Assert.Single(round.DrainNotices());
        Assert.Equal((Aria, PvpNoticeCode.ArenaMissing, Host), (host.ForPeer, host.Code, host.Other));
        Assert.Equal(PvpPhase.Idle, round.Phase);
    }

    [Fact]
    public void TheHostHasToBeWhereTheyAreOrOneWarpCannotTakeEveryone()
    {
        PvpDuelRound round = Round();

        round.Challenge(Aria, Bao, Well(), Well(), 0, arena: true, host: Well("tar_m02ab"));

        PvpNotice apart = Assert.Single(round.DrainNotices());
        Assert.Equal((Aria, PvpNoticeCode.ArenaHostApart, Host), (apart.ForPeer, apart.Code, apart.Other));
        Assert.Equal(PvpPhase.Idle, round.Phase);

        // With the host one of the two, the module check is the ordinary same-area rule.
        round.Challenge(Host, Aria, Well(), Well(), 0, arena: true, host: Well());
        Assert.Equal(PvpPhase.Challenged, round.Phase);
        Assert.True(round.Arena);
    }

    [Fact]
    public void AnArenaChallengeStillObeysEveryDuelRefusal()
    {
        PvpDuelRound round = Round();
        round.SetOptOut(Aria, true);
        round.Challenge(Host, Aria, Well(), Well(), 0, arena: true, host: Well());
        Assert.Equal([PvpNoticeCode.NotAccepting], Codes(round, Host));

        round.SetOptOut(Aria, false);
        round.Challenge(Host, Aria, Well(), Well(hp: 40), 0, arena: true, host: Well());
        Assert.Equal([PvpNoticeCode.TooHurt], Codes(round, Host));
    }

    [Fact]
    public void AcceptingIsRefusedIfTheHostWalkedOffWhileTheChallengeWaited()
    {
        PvpDuelRound round = Round();
        round.Challenge(Aria, Bao, Well(), Well(), 0, arena: true, host: Well());
        round.DrainNotices();

        round.Accept(Bao, Well(), Well(), 5_000, host: Well("tar_m02ab"));

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Equal(0u, round.RoundId);
        Assert.Equal([PvpNoticeCode.ArenaHostApart], Codes(round, Aria));
    }

    [Fact]
    public void AcceptingWithTheHostAsOneOfTheTwoNeedsNoSeparateHostCheck()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0, arena: true, host: Well());

        round.Accept(Aria, Well(), Well(), 5_000);

        Assert.Equal(PvpPhase.Entering, round.Phase);
    }

    // ---- the way in --------------------------------------------------------------------------

    [Fact]
    public void AcceptingStartsTheWalkInAndTellsBothPlayers()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0, arena: true, host: Well());
        round.DrainNotices();

        round.Accept(Aria, Well(), Well(), 1_000);

        Assert.Equal(PvpPhase.Entering, round.Phase);
        Assert.Equal(1u, round.RoundId);
        Assert.Equal([PvpNoticeCode.ArenaEntering], Codes(round, Host));

        // Entering pins nobody: that starts with the countdown, in the arena.
        Assert.False(round.IsFighting(Host));
        Assert.False(round.IsFighting(Aria));
    }

    [Fact]
    public void TheCountdownWaitsForEveryoneToBeInTheArenaAndSettled()
    {
        PvpDuelRound round = Entering();

        round.Tick(1_000, EveryoneHome());
        Assert.Equal(PvpPhase.Entering, round.Phase);

        // The host is in, the others are not: still waiting.
        round.Tick(2_000, World(Well(Arena), Well()));
        round.Tick(2_000 + PvpDuelRound.ArenaSettleMs, World(Well(Arena), Well()));
        Assert.Equal(PvpPhase.Entering, round.Phase);

        // Everyone in, but not for long enough yet.
        round.Tick(10_000, EveryoneIn());
        round.Tick(10_000 + PvpDuelRound.ArenaSettleMs - 1, EveryoneIn());
        Assert.Equal(PvpPhase.Entering, round.Phase);

        round.Tick(10_000 + PvpDuelRound.ArenaSettleMs, EveryoneIn());
        Assert.Equal(PvpPhase.Countdown, round.Phase);
        Assert.Equal([PvpNoticeCode.Countdown], Codes(round, Host));
    }

    [Fact]
    public void AnyoneLeavingTheArenaDuringTheSettleStartsTheWaitOver()
    {
        PvpDuelRound round = Entering();

        round.Tick(1_000, EveryoneIn());
        round.Tick(2_500, World(Well(Arena), Well()));   // Aria dips out again
        round.Tick(3_500, EveryoneIn());
        round.Tick(3_500 + PvpDuelRound.ArenaSettleMs - 1, EveryoneIn());

        Assert.Equal(PvpPhase.Entering, round.Phase);
    }

    [Fact]
    public void TheHostCanBeAThirdPartyWhoStillHasToArrive()
    {
        PvpDuelRound round = Round();
        round.Challenge(Aria, Bao, Well(), Well(), 0, arena: true, host: Well());
        round.Accept(Bao, Well(), Well(), 0, host: Well());
        round.DrainNotices();

        Func<uint, PvpParticipant> hostStillHome = World(Well(), Well(Arena), Well(Arena));
        round.Tick(1_000, hostStillHome);
        round.Tick(1_000 + PvpDuelRound.ArenaSettleMs, hostStillHome);
        Assert.Equal(PvpPhase.Entering, round.Phase);

        Func<uint, PvpParticipant> allIn = World(Well(Arena), Well(Arena), Well(Arena));
        round.Tick(9_000, allIn);
        round.Tick(9_000 + PvpDuelRound.ArenaSettleMs, allIn);
        Assert.Equal(PvpPhase.Countdown, round.Phase);
    }

    [Fact]
    public void AMissingStateDuringTheLoadIsNotLeavingButAbsenceForTheWholeGraceIs()
    {
        PvpDuelRound round = Entering();
        Func<uint, PvpParticipant> ariaLoading = World(Well(Arena), default);

        round.Tick(1_000, ariaLoading);
        round.Tick(1_000 + PvpDuelRound.ArenaAbsentGraceMs - 1, ariaLoading);
        Assert.Equal(PvpPhase.Entering, round.Phase);

        round.Tick(1_000 + PvpDuelRound.ArenaAbsentGraceMs, ariaLoading);
        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Contains(PvpNoticeCode.WonByLeave, Codes(round, Host));
    }

    [Fact]
    public void ReturningInTimeResetsTheAbsenceClock()
    {
        PvpDuelRound round = Entering();

        round.Tick(1_000, World(Well(Arena), default));
        round.Tick(15_000, EveryoneIn());
        round.Tick(30_000, World(Well(Arena), default));
        round.Tick(30_000 + PvpDuelRound.ArenaAbsentGraceMs - 1, World(Well(Arena), default));

        Assert.Equal(PvpPhase.Entering, round.Phase);
    }

    [Fact]
    public void ArrivingTooSlowlyCallsItOffAndTakesEveryoneBack()
    {
        PvpDuelRound round = Entering();

        round.Tick(PvpDuelRound.ArenaEnterTimeoutMs - 1, EveryoneHome());
        Assert.Equal(PvpPhase.Entering, round.Phase);

        round.Tick(PvpDuelRound.ArenaEnterTimeoutMs, EveryoneHome());
        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Contains(PvpNoticeCode.ArenaFailed, Codes(round, Host));
        Assert.Contains("did not all reach", round.LastEnding);
    }

    [Fact]
    public void YieldingOnTheWayInGivesTheRoundToTheOther()
    {
        PvpDuelRound round = Entering();

        round.Yield(Aria);

        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Contains(PvpNoticeCode.WonByYield, Codes(round, Host));
    }

    // ---- the fight --------------------------------------------------------------------------

    [Fact]
    public void TheFightInTheArenaIsTheSameFight()
    {
        PvpDuelRound round = Live();

        Assert.True(round.IsFighting(Host));
        Assert.True(round.IsFighting(Aria));

        round.Tick(100_000, EveryoneIn(hpAria: 20));

        Assert.Contains(PvpNoticeCode.Won, Codes(round, Host));
    }

    [Fact]
    public void AnEndedFightGoesToTheWayBackNotToIdle()
    {
        PvpDuelRound round = Live();

        round.Tick(100_000, EveryoneIn(hpAria: 20));

        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.True(round.Arena);
        Assert.Equal(1u, round.RoundId);

        // Nobody is pinned or hostile on the way back.
        Assert.False(round.IsFighting(Host));
        Assert.False(round.IsFighting(Aria));
        Assert.Contains("challenger", round.LastEnding);
    }

    [Fact]
    public void EveryEndOfTheFightTakesTheWayBack()
    {
        // Yield
        PvpDuelRound yielded = Live();
        yielded.Yield(Aria);
        Assert.Equal(PvpPhase.Leaving, yielded.Phase);

        // Draw
        PvpDuelRound draw = Live();
        draw.Tick(100_000, World(Well(Arena, hp: 10), Well(Arena, hp: 10)));
        Assert.Equal(PvpPhase.Leaving, draw.Phase);

        // A player drops out
        PvpDuelRound left = Live();
        left.Tick(100_000, World(Well(Arena), default));
        Assert.Equal(PvpPhase.Leaving, left.Phase);

        // Pulled into a conversation
        PvpDuelRound busy = Live();
        busy.Tick(100_000, World(Well(Arena) with { Busy = true }, Well(Arena)));
        Assert.Equal(PvpPhase.Leaving, busy.Phase);
    }

    [Fact]
    public void LeavingTheArenaMidFightCallsItOff()
    {
        PvpDuelRound round = Live();

        round.Tick(100_000, World(Well(Arena), Well(Home)));

        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Contains(PvpNoticeCode.Cancelled, Codes(round, Host));
    }

    // ---- the way back ------------------------------------------------------------------------

    [Fact]
    public void TheRoundGoesIdleOnceEveryoneIsOutAndSettled()
    {
        PvpDuelRound round = Live();
        round.Tick(100_000, EveryoneIn(hpAria: 20));
        round.DrainNotices();
        Assert.Equal(PvpPhase.Leaving, round.Phase);

        // The host went first, Aria is still there.
        round.Tick(102_000, World(Well(), Well(Arena)));
        round.Tick(120_000, World(Well(), Well(Arena)));
        Assert.Equal(PvpPhase.Leaving, round.Phase);

        round.Tick(130_000, EveryoneHome());
        round.Tick(130_000 + PvpDuelRound.ArenaSettleMs - 1, EveryoneHome());
        Assert.Equal(PvpPhase.Leaving, round.Phase);

        round.Tick(130_000 + PvpDuelRound.ArenaSettleMs, EveryoneHome());
        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.False(round.Arena);
        Assert.Contains("everyone is back", round.LastEnding);
    }

    [Fact]
    public void AMidLoadModuleNameDoesNotCountAsBeingBack()
    {
        PvpDuelRound round = Live();
        round.Tick(100_000, EveryoneIn(hpAria: 20));

        // "area0123abcd" is what the bridge reports while a load is in progress.
        Func<uint, PvpParticipant> loading = World(Well("area0123abcd"), Well("area0123abcd"));
        round.Tick(101_000, loading);
        round.Tick(101_000 + PvpDuelRound.ArenaSettleMs, loading);

        Assert.Equal(PvpPhase.Leaving, round.Phase);
    }

    [Fact]
    public void ThePartyThatNeverComesBackIsLetGoAfterAMinute()
    {
        PvpDuelRound round = Live();
        round.Tick(100_000, EveryoneIn(hpAria: 20));

        round.Tick(100_000 + PvpDuelRound.ArenaLeaveTimeoutMs - 1, EveryoneIn());
        Assert.Equal(PvpPhase.Leaving, round.Phase);

        round.Tick(100_000 + PvpDuelRound.ArenaLeaveTimeoutMs, EveryoneIn());
        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Contains("timed out", round.LastEnding);
    }

    [Fact]
    public void ADisconnectedPlayerDoesNotHoldTheWayBack()
    {
        PvpDuelRound round = Live();
        round.Tick(100_000, EveryoneIn(hpAria: 20));

        Func<uint, PvpParticipant> ariaGone = World(Well(), default);
        round.Tick(101_000, ariaGone);
        round.Tick(101_000 + PvpDuelRound.ArenaAbsentGraceMs, ariaGone);
        round.Tick(101_000 + PvpDuelRound.ArenaAbsentGraceMs + PvpDuelRound.ArenaSettleMs, ariaGone);

        Assert.Equal(PvpPhase.Idle, round.Phase);
    }

    [Fact]
    public void ANewChallengeWaitsForTheWayBackToFinish()
    {
        PvpDuelRound round = Live();
        round.Tick(100_000, EveryoneIn(hpAria: 20));
        round.DrainNotices();

        round.Challenge(Aria, Host, Well(Arena), Well(Arena), 100_500, arena: true, host: Well(Arena));

        Assert.Equal([PvpNoticeCode.DuelInProgress], Codes(round, Aria));
    }

    [Fact]
    public void YieldingOnTheWayBackHasNothingLeftToGiveUp()
    {
        PvpDuelRound round = Live();
        round.Tick(100_000, EveryoneIn(hpAria: 20));
        round.DrainNotices();

        round.Yield(Host);

        Assert.Equal(PvpPhase.Leaving, round.Phase);
        Assert.Equal([PvpNoticeCode.NothingToDo], Codes(round, Host));
    }

    [Fact]
    public void TheWayBackRunsEvenIfDuelsAreSwitchedOffMeanwhile()
    {
        PvpDuelRound round = Live();
        round.Tick(100_000, EveryoneIn(hpAria: 20));
        round.Enabled = false;

        round.Tick(101_000, EveryoneHome());
        round.Tick(101_000 + PvpDuelRound.ArenaSettleMs, EveryoneHome());

        Assert.Equal(PvpPhase.Idle, round.Phase);
    }

    [Fact]
    public void SwitchingDuelsOffInTheArenaTakesThePartyHomeToo()
    {
        PvpDuelRound round = Live();
        round.Enabled = false;

        round.Tick(100_000, EveryoneIn());

        Assert.Equal(PvpPhase.Leaving, round.Phase);
    }

    // ---- what stays the same -----------------------------------------------------------------

    [Fact]
    public void DecliningAnArenaChallengeMovesNobody()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0, arena: true, host: Well());

        round.Decline(Aria);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.False(round.Arena);
    }

    [Fact]
    public void AnUnansweredArenaChallengeTimesOutWithoutAnyMove()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0, arena: true, host: Well());

        round.Tick(PvpDuelRound.ChallengeTimeoutMs, EveryoneHome());

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.False(round.Arena);
    }

    [Fact]
    public void AfterAnArenaRoundAPlainDuelIsAPlainDuel()
    {
        PvpDuelRound round = Live();
        round.Tick(100_000, EveryoneIn(hpAria: 20));
        round.Tick(110_000, EveryoneHome());
        round.Tick(110_000 + PvpDuelRound.ArenaSettleMs, EveryoneHome());
        Assert.Equal(PvpPhase.Idle, round.Phase);

        round.Challenge(Host, Aria, Well(), Well(), 500_000);
        round.Accept(Aria, Well(), Well(), 500_000);

        Assert.False(round.Arena);
        Assert.Equal(PvpPhase.Countdown, round.Phase);
        Assert.Equal(2u, round.RoundId);
    }
}

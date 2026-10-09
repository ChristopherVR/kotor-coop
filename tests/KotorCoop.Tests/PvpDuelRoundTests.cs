using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The host's duel round (docs/research/pvp-plan.md): every refusal, the 30 s answer window, the
/// countdown, and each way a fight ends. Nothing here touches a game or a socket.
/// </summary>
public class PvpDuelRoundTests
{
    private const uint Host = 1;
    private const uint Aria = 2;
    private const uint Bao = 3;

    private static PvpParticipant Well(string module = "tar_m02aa", int hp = 100, int max = 100) =>
        new(Present: true, Aware: true, Busy: false, Module: module, Health: hp, MaxHealth: max, Downed: false);

    private static PvpDuelRound Round() => new() { Enabled = true };

    private static PvpNoticeCode[] Codes(PvpDuelRound round, uint forPeer) =>
        [.. round.DrainNotices().Where(n => n.ForPeer == forPeer).Select(n => n.Code)];

    private static Func<uint, PvpParticipant> World(
        PvpParticipant host, PvpParticipant aria, PvpParticipant? bao = null) =>
        id => id switch { Host => host, Aria => aria, Bao => bao ?? default, _ => default };

    private static PvpDuelRound Live(long start = 0)
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), start);
        round.Accept(Aria, Well(), Well(), start + 1_000);
        round.Tick(start + 1_000 + PvpDuelRound.CountdownMs, World(Well(), Well()));
        round.DrainNotices();
        Assert.Equal(PvpPhase.Live, round.Phase);
        return round;
    }

    // ---- refusing a challenge -----------------------------------------------------------------

    [Fact]
    public void ChallengeIsRefusedWhenTheHostHasDuelsOff()
    {
        PvpDuelRound round = new() { Enabled = false };

        round.Challenge(Host, Aria, Well(), Well(), 0);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Equal([PvpNoticeCode.NotEnabled], Codes(round, Host));
    }

    [Fact]
    public void ChallengeIsRefusedForAnOlderBuild()
    {
        PvpDuelRound round = Round();

        round.Challenge(Host, Aria, Well(), Well() with { Aware = false }, 0);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Equal([PvpNoticeCode.Unsupported], Codes(round, Host));
    }

    [Fact]
    public void ChallengeIsRefusedAfterDuelOff()
    {
        PvpDuelRound round = Round();
        round.SetOptOut(Aria, true);

        round.Challenge(Host, Aria, Well(), Well(), 0);

        Assert.Equal([PvpNoticeCode.NotAccepting], Codes(round, Host));

        round.SetOptOut(Aria, false);
        round.Challenge(Host, Aria, Well(), Well(), 0);
        Assert.Equal(PvpPhase.Challenged, round.Phase);
    }

    [Fact]
    public void ChallengeIsRefusedForYourself()
    {
        PvpDuelRound round = Round();

        round.Challenge(Host, Host, Well(), Well(), 0);

        Assert.Equal([PvpNoticeCode.NoSuchPlayer], Codes(round, Host));
    }

    [Fact]
    public void ChallengeIsRefusedForSomeoneNotThere()
    {
        PvpDuelRound round = Round();

        round.Challenge(Host, Aria, Well(), default, 0);

        Assert.Equal([PvpNoticeCode.NoSuchPlayer], Codes(round, Host));
    }

    [Fact]
    public void ChallengeIsRefusedForABusyTarget()
    {
        PvpDuelRound round = Round();

        round.Challenge(Host, Aria, Well(), Well() with { Busy = true }, 0);

        PvpNotice said = Assert.Single(round.DrainNotices());
        Assert.Equal((PvpNoticeCode.Busy, Aria), (said.Code, said.Other));
    }

    [Fact]
    public void ChallengeIsRefusedWhenTheAskerIsBusyAndSaysItIsAboutThem()
    {
        PvpDuelRound round = Round();

        round.Challenge(Host, Aria, Well() with { Busy = true }, Well(), 0);

        PvpNotice said = Assert.Single(round.DrainNotices());
        Assert.Equal((PvpNoticeCode.Busy, Host), (said.Code, said.Other));
        Assert.Equal(
            "You are in a conversation or a minigame. Try again in a moment.",
            PvpText.Format(said.Code, "x", aboutSelf: said.Other == said.ForPeer));
    }

    [Fact]
    public void ChallengeIsRefusedInAnotherArea()
    {
        PvpDuelRound round = Round();

        round.Challenge(Host, Aria, Well("tar_m02aa"), Well("tar_m03aa"), 0);

        Assert.Equal([PvpNoticeCode.NotHere], Codes(round, Host));
    }

    [Theory]
    [InlineData(50, 100)]   // exactly half is not "more than half"
    [InlineData(10, 100)]
    public void ChallengeIsRefusedForAnyoneTooHurtToStart(int hp, int max)
    {
        PvpDuelRound round = Round();

        round.Challenge(Host, Aria, Well(), Well(hp: hp, max: max), 0);
        PvpNotice theirs = Assert.Single(round.DrainNotices());
        Assert.Equal((PvpNoticeCode.TooHurt, Aria), (theirs.Code, theirs.Other));

        round.Challenge(Host, Aria, Well(hp: hp, max: max), Well(), 0);
        PvpNotice yours = Assert.Single(round.DrainNotices());
        Assert.Equal((PvpNoticeCode.TooHurt, Host), (yours.Code, yours.Other));
    }

    [Fact]
    public void AnUnknownMaximumDoesNotBlockAStart()
    {
        Assert.True(PvpDuelRound.CanStart(1, 0));
        Assert.False(PvpDuelRound.IsBeaten(1, 0));
    }

    [Fact]
    public void OnlyOneRoundRunsAtATime()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.DrainNotices();

        round.Challenge(Aria, Bao, Well(), Well(), 1);

        Assert.Equal([PvpNoticeCode.DuelInProgress], Codes(round, Aria));
        Assert.Equal(Host, round.Challenger);
    }

    [Fact]
    public void TheSameTargetCannotBeChallengedAgainWithinAMinute()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.Decline(Aria);
        round.DrainNotices();

        round.Challenge(Host, Aria, Well(), Well(), PvpDuelRound.RepeatChallengeGapMs - 1);
        Assert.Equal([PvpNoticeCode.TooSoon], Codes(round, Host));

        round.Challenge(Host, Aria, Well(), Well(), PvpDuelRound.RepeatChallengeGapMs);
        Assert.Equal(PvpPhase.Challenged, round.Phase);
    }

    // ---- the answer ---------------------------------------------------------------------------

    [Fact]
    public void AChallengeTellsBothPlayers()
    {
        PvpDuelRound round = Round();

        round.Challenge(Host, Aria, Well(), Well(), 0);

        PvpNotice[] said = [.. round.DrainNotices()];
        Assert.Contains(said, n => n is { ForPeer: Host, Code: PvpNoticeCode.ChallengeSent, Other: Aria });
        Assert.Contains(said, n => n is { ForPeer: Aria, Code: PvpNoticeCode.Challenged, Other: Host });
        Assert.Equal(PvpPhase.Challenged, round.Phase);
        Assert.False(round.IsFighting(Host));
    }

    [Fact]
    public void SilenceForThirtySecondsCountsAsNo()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 1_000);
        round.DrainNotices();

        round.Tick(1_000 + PvpDuelRound.ChallengeTimeoutMs - 1, World(Well(), Well()));
        Assert.Equal(PvpPhase.Challenged, round.Phase);

        round.Tick(1_000 + PvpDuelRound.ChallengeTimeoutMs, World(Well(), Well()));
        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Equal([PvpNoticeCode.TimedOut], Codes(round, Host));
    }

    [Fact]
    public void DeclineEndsItAndTellsTheChallenger()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.DrainNotices();

        round.Decline(Aria);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Equal([PvpNoticeCode.Declined], Codes(round, Host));
    }

    [Fact]
    public void OnlyTheTargetCanAnswer()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.DrainNotices();

        round.Accept(Host, Well(), Well(), 1);
        round.Decline(Bao);

        Assert.Equal(PvpPhase.Challenged, round.Phase);
        Assert.All(round.DrainNotices(), n => Assert.Equal(PvpNoticeCode.NothingToDo, n.Code));
    }

    [Fact]
    public void AcceptWithNothingToAcceptSaysSo()
    {
        PvpDuelRound round = Round();

        round.Accept(Aria, Well(), Well(), 0);

        Assert.Equal([PvpNoticeCode.NothingToDo], Codes(round, Aria));
    }

    [Fact]
    public void TheChallengerCanWithdrawWithYield()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.DrainNotices();

        round.Yield(Host);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Contains(round.DrainNotices(), n => n.Code == PvpNoticeCode.Cancelled);
    }

    [Fact]
    public void AcceptingAfterTheOtherWasPulledIntoAConversationCallsItOff()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.DrainNotices();

        round.Accept(Aria, Well(), Well() with { Busy = true }, 1);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Contains(round.DrainNotices(), n => n.Code == PvpNoticeCode.Cancelled);
    }

    // ---- the countdown and the fight ----------------------------------------------------------

    [Fact]
    public void AcceptStartsAFiveSecondCountdownThatPinsButDoesNotArm()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.DrainNotices();

        round.Accept(Aria, Well(), Well(), 10_000);

        Assert.Equal(PvpPhase.Countdown, round.Phase);
        Assert.Equal(1u, round.RoundId);
        Assert.True(round.IsFighting(Host));
        Assert.True(round.IsFighting(Aria));
        Assert.False(round.IsFighting(Bao));
        Assert.Equal(Aria, round.OpponentOf(Host));
        Assert.Equal(Host, round.OpponentOf(Aria));
        Assert.Contains(round.DrainNotices(), n => n is { ForPeer: Aria, Code: PvpNoticeCode.Countdown, Other: Host });

        round.Tick(10_000 + PvpDuelRound.CountdownMs - 1, World(Well(), Well()));
        Assert.Equal(PvpPhase.Countdown, round.Phase);

        round.Tick(10_000 + PvpDuelRound.CountdownMs, World(Well(), Well()));
        Assert.Equal(PvpPhase.Live, round.Phase);
        Assert.Equal(2, round.DrainNotices().Count(n => n.Code == PvpNoticeCode.Started));
    }

    [Theory]
    [InlineData(26, false)]
    [InlineData(25, true)]
    [InlineData(1, true)]
    public void TheLineIsTwentyFivePercent(int hp, bool beaten)
    {
        Assert.Equal(beaten, PvpDuelRound.IsBeaten(hp, 100));
    }

    [Fact]
    public void TheRoundEndsWhenADuellistIsAtTheLine()
    {
        PvpDuelRound round = Live();

        round.Tick(20_000, World(Well(), Well(hp: 25)));

        Assert.Equal(PvpPhase.Idle, round.Phase);
        PvpNotice[] said = [.. round.DrainNotices()];
        Assert.Contains(said, n => n is { ForPeer: Host, Code: PvpNoticeCode.Won, Other: Aria });
        Assert.Contains(said, n => n is { ForPeer: Aria, Code: PvpNoticeCode.Lost, Other: Host });
    }

    [Fact]
    public void BothAtTheLineInOneLookIsADraw()
    {
        PvpDuelRound round = Live();

        round.Tick(20_000, World(Well(hp: 20), Well(hp: 20)));

        Assert.Equal(PvpPhase.Idle, round.Phase);
        PvpNotice[] said = [.. round.DrainNotices()];
        Assert.Equal(2, said.Count(n => n.Code == PvpNoticeCode.Draw));
        Assert.DoesNotContain(said, n => n.Code is PvpNoticeCode.Won or PvpNoticeCode.Lost);
    }

    [Fact]
    public void ADuellistWhoGoesDownLosesAndNeverCountsAsAWipe()
    {
        PvpDuelRound round = Live();

        round.Tick(20_000, World(Well() with { Downed = true }, Well()));

        Assert.Equal(PvpPhase.Idle, round.Phase);
        PvpNotice[] said = [.. round.DrainNotices()];
        Assert.Contains(said, n => n is { ForPeer: Host, Code: PvpNoticeCode.Lost });
        Assert.Contains(said, n => n is { ForPeer: Aria, Code: PvpNoticeCode.Won });
    }

    [Fact]
    public void HealthDoesNotEndTheCountdown()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.Accept(Aria, Well(), Well(), 0);
        round.DrainNotices();

        round.Tick(100, World(Well(hp: 5), Well()));

        Assert.Equal(PvpPhase.Countdown, round.Phase);
    }

    [Fact]
    public void YieldEndsAFightAndTheYielderLoses()
    {
        PvpDuelRound round = Live();

        round.Yield(Aria);

        Assert.Equal(PvpPhase.Idle, round.Phase);
        PvpNotice[] said = [.. round.DrainNotices()];
        Assert.Contains(said, n => n is { ForPeer: Aria, Code: PvpNoticeCode.YouYielded, Other: Host });
        Assert.Contains(said, n => n is { ForPeer: Host, Code: PvpNoticeCode.WonByYield, Other: Aria });
    }

    [Fact]
    public void YieldWorksDuringTheCountdownToo()
    {
        PvpDuelRound round = Round();
        round.Challenge(Host, Aria, Well(), Well(), 0);
        round.Accept(Aria, Well(), Well(), 0);
        round.DrainNotices();

        round.Yield(Host);

        Assert.Equal(PvpPhase.Idle, round.Phase);
    }

    [Fact]
    public void YieldFromAnOutsiderChangesNothing()
    {
        PvpDuelRound round = Live();

        round.Yield(Bao);

        Assert.Equal(PvpPhase.Live, round.Phase);
        Assert.Equal([PvpNoticeCode.NothingToDo], Codes(round, Bao));
    }

    [Fact]
    public void ADisconnectIsAForfeitAndTheOtherIsToldPlainly()
    {
        PvpDuelRound round = Live();

        round.Tick(20_000, World(Well(), default));

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Equal([PvpNoticeCode.WonByLeave], Codes(round, Host));
    }

    [Theory]
    [InlineData(true, false)]   // pulled into a conversation or a minigame
    [InlineData(false, true)]   // changed area
    public void ConversationMinigameOrAreaChangeCallsItOffAndNothingCounts(bool busy, bool otherArea)
    {
        PvpDuelRound round = Live();
        PvpParticipant moved = Well(module: otherArea ? "tar_m03aa" : "tar_m02aa") with { Busy = busy };

        round.Tick(20_000, World(Well(), moved));

        Assert.Equal(PvpPhase.Idle, round.Phase);
        PvpNotice[] said = [.. round.DrainNotices()];
        Assert.Equal(2, said.Count(n => n.Code == PvpNoticeCode.Cancelled));
        Assert.DoesNotContain(said, n => n.Code is PvpNoticeCode.Won or PvpNoticeCode.Lost);
    }

    [Fact]
    public void TurningDuelsOffMidRoundCallsItOff()
    {
        PvpDuelRound round = Live();
        round.Enabled = false;

        round.Tick(20_000, World(Well(), Well()));

        Assert.Equal(PvpPhase.Idle, round.Phase);
    }

    [Fact]
    public void ARoundIdCountsUpAcrossFights()
    {
        PvpDuelRound round = Live();
        round.Yield(Aria);
        round.DrainNotices();

        round.Challenge(Aria, Host, Well(), Well(), 200_000);
        round.Accept(Host, Well(), Well(), 200_001);

        Assert.Equal(2u, round.RoundId);
    }

    [Fact]
    public void ClearDropsTheRoundWithoutAWord()
    {
        PvpDuelRound round = Live();
        round.SetOptOut(Aria, true);

        round.Clear();

        Assert.Equal(PvpPhase.Idle, round.Phase);
        Assert.Empty(round.DrainNotices());
        Assert.False(round.IsOptedOut(Aria));
    }
}

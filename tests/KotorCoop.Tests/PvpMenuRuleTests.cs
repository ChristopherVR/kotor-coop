using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The in-game duel panel's decisions (docs/research/pvp-plan.md, "In-game surface"): when it
/// shows, which key does what, and that nothing ever fires without a key press.
/// </summary>
public class PvpMenuRuleTests
{
    private static readonly PvpMenuEntry Aria = new(2, "Aria", false);
    private static readonly PvpMenuEntry Bo = new(3, "Bo", true);
    private static readonly PvpMenuEntry[] Two = [Aria, Bo];

    private static PvpMenuKeys Keys(
        bool open = false, bool up = false, bool down = false, bool enter = false, bool escape = false,
        bool accept = false, bool decline = false, int digit = 0) =>
        new(open, up, down, enter, escape, accept, decline, digit);

    // ---- visibility ---------------------------------------------------------------------------

    [Fact]
    public void NothingShowsWhenTheHostHasDuelsOff()
    {
        Assert.Equal(PvpMenuMode.Hidden, PvpMenuRule.Mode(0, 0));
        // Even a stray incoming bit or a phase level cannot raise the panel without "allowed".
        Assert.Equal(PvpMenuMode.Hidden, PvpMenuRule.Mode(PvpMenuRule.UiIncoming, 2));
    }

    [Fact]
    public void TheModeFollowsTheAgentsFlagsAndThePhase()
    {
        Assert.Equal(PvpMenuMode.Idle, PvpMenuRule.Mode(PvpMenuRule.UiAllowed, 0));
        Assert.Equal(PvpMenuMode.Incoming, PvpMenuRule.Mode(PvpMenuRule.UiAllowed | PvpMenuRule.UiIncoming, 0));
        Assert.Equal(PvpMenuMode.Outgoing, PvpMenuRule.Mode(PvpMenuRule.UiAllowed | PvpMenuRule.UiOutgoing, 0));
        Assert.Equal(PvpMenuMode.Fighting, PvpMenuRule.Mode(PvpMenuRule.UiAllowed, 1));
        Assert.Equal(PvpMenuMode.Fighting, PvpMenuRule.Mode(PvpMenuRule.UiAllowed | PvpMenuRule.UiIncoming, 2));
    }

    [Fact]
    public void TheHintShowsOnlyWhenThereIsSomeoneToChallengeAndNothingElseIsUp()
    {
        Assert.True(PvpMenuRule.ShowHint(PvpMenuMode.Idle, listOpen: false, entries: 1));
        Assert.False(PvpMenuRule.ShowHint(PvpMenuMode.Idle, listOpen: false, entries: 0));
        Assert.False(PvpMenuRule.ShowHint(PvpMenuMode.Idle, listOpen: true, entries: 2));
        Assert.False(PvpMenuRule.ShowHint(PvpMenuMode.Hidden, listOpen: false, entries: 2));
        Assert.False(PvpMenuRule.ShowHint(PvpMenuMode.Incoming, listOpen: false, entries: 2));
        Assert.False(PvpMenuRule.ShowHint(PvpMenuMode.Fighting, listOpen: false, entries: 2));
    }

    // ---- the list -----------------------------------------------------------------------------

    [Fact]
    public void F10OpensAndClosesTheListButNeverStartsAnything()
    {
        PvpMenuDecision open = PvpMenuRule.Decide(PvpMenuMode.Idle, false, 0, Two, Keys(open: true));
        Assert.True(open.ListOpen);
        Assert.Equal(PvpMenuAction.None, open.Action);

        PvpMenuDecision close = PvpMenuRule.Decide(PvpMenuMode.Idle, true, 1, Two, Keys(open: true));
        Assert.False(close.ListOpen);
        Assert.Equal(PvpMenuAction.None, close.Action);
    }

    [Fact]
    public void EnterWithTheListClosedDoesNothing()
    {
        PvpMenuDecision d = PvpMenuRule.Decide(PvpMenuMode.Idle, false, 0, Two, Keys(enter: true));

        Assert.Equal(PvpMenuAction.None, d.Action);
        Assert.False(d.ListOpen);
    }

    [Fact]
    public void ArrowKeysMoveTheHighlightAndStopAtTheEnds()
    {
        Assert.Equal(1, PvpMenuRule.Decide(PvpMenuMode.Idle, true, 0, Two, Keys(down: true)).Selection);
        Assert.Equal(1, PvpMenuRule.Decide(PvpMenuMode.Idle, true, 1, Two, Keys(down: true)).Selection);
        Assert.Equal(0, PvpMenuRule.Decide(PvpMenuMode.Idle, true, 1, Two, Keys(up: true)).Selection);
        Assert.Equal(0, PvpMenuRule.Decide(PvpMenuMode.Idle, true, 0, Two, Keys(up: true)).Selection);
    }

    [Fact]
    public void ANumberKeyPicksARowButDoesNotChallenge()
    {
        PvpMenuDecision d = PvpMenuRule.Decide(PvpMenuMode.Idle, true, 0, Two, Keys(digit: 2));

        Assert.Equal(1, d.Selection);
        Assert.True(d.ListOpen);
        Assert.Equal(PvpMenuAction.None, d.Action);
    }

    [Fact]
    public void ANumberKeyPastTheEndOfTheListIsIgnored()
    {
        PvpMenuDecision d = PvpMenuRule.Decide(PvpMenuMode.Idle, true, 1, Two, Keys(digit: 7));

        Assert.Equal(1, d.Selection);
        Assert.Equal(PvpMenuAction.None, d.Action);
    }

    [Fact]
    public void EnterChallengesTheHighlightedPlayerAndClosesTheList()
    {
        PvpMenuDecision d = PvpMenuRule.Decide(PvpMenuMode.Idle, true, 1, Two, Keys(enter: true));

        Assert.Equal(PvpMenuAction.Challenge, d.Action);
        Assert.False(d.ListOpen);
        Assert.Equal("duel Bo", PvpMenuRule.CommandFor(d));
    }

    [Fact]
    public void EnterOnAnEmptyListDoesNothing()
    {
        PvpMenuDecision d = PvpMenuRule.Decide(PvpMenuMode.Idle, true, 0, [], Keys(enter: true));

        Assert.Equal(PvpMenuAction.None, d.Action);
        Assert.Null(PvpMenuRule.CommandFor(d));
    }

    [Fact]
    public void EscapeClosesTheListWithoutAnAction()
    {
        PvpMenuDecision d = PvpMenuRule.Decide(PvpMenuMode.Idle, true, 1, Two, Keys(escape: true));

        Assert.False(d.ListOpen);
        Assert.Equal(PvpMenuAction.None, d.Action);
    }

    [Fact]
    public void ASelectionPastAShrunkListIsClampedBeforeAChallenge()
    {
        // The other player left between frames: the highlight was on row 1, only row 0 remains.
        PvpMenuDecision d = PvpMenuRule.Decide(PvpMenuMode.Idle, true, 1, [Aria], Keys(enter: true));

        Assert.Equal("duel Aria", PvpMenuRule.CommandFor(d));
    }

    [Fact]
    public void NoKeyMeansNoActionInAnyMode()
    {
        foreach (PvpMenuMode mode in Enum.GetValues<PvpMenuMode>())
        {
            foreach (bool open in new[] { false, true })
            {
                PvpMenuDecision d = PvpMenuRule.Decide(mode, open, 0, Two, Keys());

                Assert.Equal(PvpMenuAction.None, d.Action);
                Assert.Null(PvpMenuRule.CommandFor(d));
            }
        }
    }

    // ---- answering ----------------------------------------------------------------------------

    [Fact]
    public void AChallengeIsAnsweredWithF11OrF12AndNothingElse()
    {
        Assert.Equal("accept", PvpMenuRule.CommandFor(
            PvpMenuRule.Decide(PvpMenuMode.Incoming, false, 0, Two, Keys(accept: true))));
        Assert.Equal("decline", PvpMenuRule.CommandFor(
            PvpMenuRule.Decide(PvpMenuMode.Incoming, false, 0, Two, Keys(decline: true))));

        // Enter, Escape, F10 and the number keys are not answers: a stray press cannot say yes.
        PvpMenuKeys[] others = [Keys(enter: true), Keys(escape: true), Keys(open: true), Keys(digit: 1), Keys(up: true)];
        foreach (PvpMenuKeys keys in others)
        {
            Assert.Null(PvpMenuRule.CommandFor(PvpMenuRule.Decide(PvpMenuMode.Incoming, false, 0, Two, keys)));
        }
    }

    [Fact]
    public void AChallengeTakesOverAnOpenList()
    {
        PvpMenuDecision d = PvpMenuRule.Decide(PvpMenuMode.Incoming, true, 1, Two, Keys(up: true));

        Assert.False(d.ListOpen);
        Assert.Equal(PvpMenuAction.None, d.Action);
    }

    [Fact]
    public void WhileWaitingForAnAnswerNoKeyDoesAnything()
    {
        PvpMenuKeys[] all = [Keys(open: true), Keys(enter: true), Keys(accept: true), Keys(decline: true), Keys(digit: 1)];
        foreach (PvpMenuKeys keys in all)
        {
            Assert.Null(PvpMenuRule.CommandFor(PvpMenuRule.Decide(PvpMenuMode.Outgoing, false, 0, Two, keys)));
        }
    }

    // ---- fighting -----------------------------------------------------------------------------

    [Fact]
    public void F12YieldsAFightAndNothingElseDoes()
    {
        Assert.Equal("yield", PvpMenuRule.CommandFor(
            PvpMenuRule.Decide(PvpMenuMode.Fighting, false, 0, Two, Keys(decline: true))));

        PvpMenuKeys[] others = [Keys(accept: true), Keys(enter: true), Keys(escape: true), Keys(open: true), Keys(digit: 3)];
        foreach (PvpMenuKeys keys in others)
        {
            Assert.Null(PvpMenuRule.CommandFor(PvpMenuRule.Decide(PvpMenuMode.Fighting, false, 0, Two, keys)));
        }
    }

    [Fact]
    public void HiddenIgnoresEveryKey()
    {
        PvpMenuKeys[] all =
        [
            Keys(open: true), Keys(enter: true), Keys(accept: true), Keys(decline: true), Keys(digit: 1),
        ];
        foreach (PvpMenuKeys keys in all)
        {
            Assert.Null(PvpMenuRule.CommandFor(PvpMenuRule.Decide(PvpMenuMode.Hidden, true, 0, Two, keys)));
        }
    }

    // ---- the commands are the ones the console takes -------------------------------------------

    [Fact]
    public void ACommandWithoutANameIsNotSent()
    {
        Assert.Null(PvpMenuRule.CommandFor(new PvpMenuDecision(PvpMenuAction.Challenge, 0, false, "  ")));
        Assert.Null(PvpMenuRule.CommandFor(new PvpMenuDecision(PvpMenuAction.Challenge, 0, false, null)));
    }

    [Theory]
    [InlineData("duel Aria", CoopCommandKind.Duel, "Aria")]
    [InlineData("accept", CoopCommandKind.DuelAccept, null)]
    [InlineData("decline", CoopCommandKind.DuelDecline, null)]
    [InlineData("yield", CoopCommandKind.DuelYield, null)]
    public void EveryCommandParsesAsTheDuelConsoleVerb(string line, CoopCommandKind kind, string? name)
    {
        CoopCommand command = ConsoleCommand.Parse(line);

        Assert.Equal(kind, command.Kind);
        Assert.Equal(name, command.Address);
    }

    [Fact]
    public void EveryCommandThePanelSendsIsOneTheDetourRoutes()
    {
        // FuncInterpHook.Verbs is the list of first words the plugin hands to the agent; the panel
        // publishes straight to the mailbox, but the agent parses with the same ConsoleCommand.
        foreach (PvpMenuAction action in new[] { PvpMenuAction.Accept, PvpMenuAction.Decline, PvpMenuAction.Yield })
        {
            string line = PvpMenuRule.CommandFor(new PvpMenuDecision(action, 0, false, null))!;
            Assert.NotEqual(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
        }
    }

    // ---- text ---------------------------------------------------------------------------------

    [Fact]
    public void TheTextNamesThePlayerAndTheKeys()
    {
        Assert.Contains("Aria", PvpMenuRule.IncomingLine1("Aria"));
        Assert.Contains("F11", PvpMenuRule.IncomingLine2);
        Assert.Contains("F12", PvpMenuRule.IncomingLine2);
        Assert.Contains("30 seconds", PvpMenuRule.IncomingLine2);
        Assert.Contains("Aria", PvpMenuRule.OutgoingLine("Aria"));
        Assert.Contains("F12", PvpMenuRule.FightingLine("Aria"));
        Assert.Contains("(busy right now)", PvpMenuRule.ListRow(1, false, Bo));
        Assert.StartsWith("> 1. Aria", PvpMenuRule.ListRow(0, true, Aria));
    }

    [Fact]
    public void ANameThatLeftTheAreaReadsAsSomeone()
    {
        Assert.Equal("Aria", PvpMenuRule.NameOf(2, Two));
        Assert.Equal("Someone", PvpMenuRule.NameOf(9, Two));
    }

    // ---- the agent's half ---------------------------------------------------------------------

    private static PvpView Round(PvpPhase phase, uint challenger = 1, uint target = 2) =>
        new(phase, 1, challenger, target);

    [Fact]
    public void TheFlagValuesMatchOnBothSidesOfTheBlock()
    {
        Assert.Equal(PvpMenuRule.UiAllowed, PvpPumpRule.UiAllowed);
        Assert.Equal(PvpMenuRule.UiIncoming, PvpPumpRule.UiIncoming);
        Assert.Equal(PvpMenuRule.UiOutgoing, PvpPumpRule.UiOutgoing);
    }

    [Fact]
    public void TheAgentRaisesNothingForAHostWithDuelsOff()
    {
        Assert.Equal((0u, 0u), PvpPumpRule.UiLevels(Round(PvpPhase.Challenged), 2, enabled: false));
        Assert.Equal((0u, 0u), PvpPumpRule.UiLevels(PvpView.Idle, 2, enabled: false));
    }

    [Fact]
    public void TheAgentSaysAllowedWhenIdle()
    {
        Assert.Equal((PvpPumpRule.UiAllowed, 0u), PvpPumpRule.UiLevels(PvpView.Idle, 2, enabled: true));
    }

    [Fact]
    public void TheTargetSeesAnIncomingChallengeNamingTheChallenger()
    {
        Assert.Equal(
            (PvpPumpRule.UiAllowed | PvpPumpRule.UiIncoming, 1u),
            PvpPumpRule.UiLevels(Round(PvpPhase.Challenged, challenger: 1, target: 2), 2, enabled: true));
    }

    [Fact]
    public void TheChallengerSeesItIsWaitingOnTheTarget()
    {
        Assert.Equal(
            (PvpPumpRule.UiAllowed | PvpPumpRule.UiOutgoing, 2u),
            PvpPumpRule.UiLevels(Round(PvpPhase.Challenged, challenger: 1, target: 2), 1, enabled: true));
    }

    [Fact]
    public void ABystanderSeesNeitherAChallengeNorAFight()
    {
        Assert.Equal((PvpPumpRule.UiAllowed, 0u),
            PvpPumpRule.UiLevels(Round(PvpPhase.Challenged, challenger: 1, target: 2), 3, enabled: true));
        Assert.Equal((PvpPumpRule.UiAllowed, 0u),
            PvpPumpRule.UiLevels(Round(PvpPhase.Live, challenger: 1, target: 2), 3, enabled: true));
    }

    [Fact]
    public void AnAnsweredChallengeStopsOfferingTheAnswerKeys()
    {
        // Once the round is a countdown the phase level takes over and the prompt is gone.
        Assert.Equal((PvpPumpRule.UiAllowed, 0u),
            PvpPumpRule.UiLevels(Round(PvpPhase.Countdown, challenger: 1, target: 2), 2, enabled: true));
    }
}

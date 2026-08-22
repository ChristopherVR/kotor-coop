using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure join-decision rule <c>InteractionLockController.TryForceOpen</c> (plugin) now
/// defers to, and the parser for <c>coop.ini dialogue_auto_join</c>.
/// </summary>
public class DialogueAutoJoinRuleTests
{
    // ---- ShouldAutoJoin ---------------------------------------------------------------

    [Fact]
    public void OffNeverJoinsRegardlessOfDistance()
    {
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Off,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 0f, rangeLimit: 20f));
    }

    [Fact]
    public void NearJoinsWhenWithinRange()
    {
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 10f, rangeLimit: 20f));
    }

    [Fact]
    public void NearRefusesBeyondRange()
    {
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 20.1f, rangeLimit: 20f));
    }

    [Fact]
    public void NearJoinsExactlyAtTheRangeLimit()
    {
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 20f, rangeLimit: 20f));
    }

    [Fact]
    public void AlwaysJoinsRegardlessOfDistance()
    {
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Always,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 500f, rangeLimit: 20f));
    }

    [Fact]
    public void AlreadyInAConversationRefusesUnderEveryMode()
    {
        foreach (DialogueAutoJoinMode mode in new[]
                 { DialogueAutoJoinMode.Near, DialogueAutoJoinMode.Always })
        {
            Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
                mode,
                localAlreadyInConversation: true, localInCombat: false, localInMinigame: false,
                distanceToPartner: 0f, rangeLimit: 20f));
        }
    }

    [Fact]
    public void InCombatRefusesUnderEveryMode()
    {
        foreach (DialogueAutoJoinMode mode in new[]
                 { DialogueAutoJoinMode.Near, DialogueAutoJoinMode.Always })
        {
            Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
                mode,
                localAlreadyInConversation: false, localInCombat: true, localInMinigame: false,
                distanceToPartner: 0f, rangeLimit: 20f));
        }
    }

    [Fact]
    public void InAMinigameRefusesUnderEveryMode()
    {
        foreach (DialogueAutoJoinMode mode in new[]
                 { DialogueAutoJoinMode.Near, DialogueAutoJoinMode.Always })
        {
            Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
                mode,
                localAlreadyInConversation: false, localInCombat: false, localInMinigame: true,
                distanceToPartner: 0f, rangeLimit: 20f));
        }
    }

    [Fact]
    public void ANaNDistanceRefusesUnderNearRatherThanAcceptingByAccident()
    {
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: float.NaN, rangeLimit: 20f));
    }

    [Fact]
    public void CombatIsCheckedBeforeDistanceUnderNear()
    {
        // Close enough to join, but fighting: combat wins.
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near,
            localAlreadyInConversation: false, localInCombat: true, localInMinigame: false,
            distanceToPartner: 1f, rangeLimit: 20f));
    }

    // ---- symmetry: the rule has no notion of "host" at all -----------------------------

    [Fact]
    public void TheRuleProducesTheSameAnswerRegardlessOfWhoStartedTheConversation()
    {
        // The rule's signature has no "am I the host" or "did I start this" input at all —
        // this test pins that down by calling it twice with identical facts and nothing else,
        // which is the whole point: a client deciding whether to join a HOST-started
        // conversation and a host deciding whether to join a CLIENT-started one run through
        // the exact same code, so if one of the two calls below ever diverges from the other,
        // something has smuggled a direction-specific input into the rule.
        bool clientJoiningHostsConversation = DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 5f, rangeLimit: 20f);

        bool hostJoiningClientsConversation = DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 5f, rangeLimit: 20f);

        Assert.Equal(clientJoiningHostsConversation, hostJoiningClientsConversation);
        Assert.True(clientJoiningHostsConversation);
    }

    // ---- ParseMode ----------------------------------------------------------------------

    [Theory]
    [InlineData("near", DialogueAutoJoinMode.Near)]
    [InlineData("NEAR", DialogueAutoJoinMode.Near)]
    [InlineData(" Near ", DialogueAutoJoinMode.Near)]
    [InlineData("always", DialogueAutoJoinMode.Always)]
    [InlineData("ALWAYS", DialogueAutoJoinMode.Always)]
    [InlineData("off", DialogueAutoJoinMode.Off)]
    [InlineData("OFF", DialogueAutoJoinMode.Off)]
    [InlineData(null, DialogueAutoJoinMode.Near)]
    [InlineData("", DialogueAutoJoinMode.Near)]
    [InlineData("nonsense", DialogueAutoJoinMode.Near)]
    public void ParseModeMatchesEachSettingAndFallsBackToNear(string? raw, DialogueAutoJoinMode expected)
    {
        Assert.Equal(expected, DialogueAutoJoinRule.ParseMode(raw));
    }
}

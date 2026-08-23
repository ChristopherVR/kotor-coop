using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure join-decision rule <c>InteractionLockController.TryForceOpen</c> (plugin) now
/// defers to, and the parser for <c>coop.ini dialogue_auto_join</c>.
///
/// 2026-08-23 (docs/research/dialogue-sync.md's Item 1): the rule gained an <c>isForced</c>
/// input, and the default mode flipped from <c>near</c> to <c>off</c> — the user's own stated
/// preference is no forced joins for an ORDINARY conversation, ever; only a conversation the
/// vanilla engine itself would force auto-joins, and that path is <c>isForced</c>, not the mode.
/// </summary>
public class DialogueAutoJoinRuleTests
{
    // ---- ShouldAutoJoin: the pre-existing mode/range matrix, now with isForced: false --------

    [Fact]
    public void OffNeverJoinsRegardlessOfDistance()
    {
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Off, isForced: false,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 0f, rangeLimit: 20f));
    }

    [Fact]
    public void NearJoinsWhenWithinRange()
    {
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near, isForced: false,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 10f, rangeLimit: 20f));
    }

    [Fact]
    public void NearRefusesBeyondRange()
    {
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near, isForced: false,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 20.1f, rangeLimit: 20f));
    }

    [Fact]
    public void NearJoinsExactlyAtTheRangeLimit()
    {
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near, isForced: false,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 20f, rangeLimit: 20f));
    }

    [Fact]
    public void AlwaysJoinsRegardlessOfDistance()
    {
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Always, isForced: false,
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
                mode, isForced: false,
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
                mode, isForced: false,
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
                mode, isForced: false,
                localAlreadyInConversation: false, localInCombat: false, localInMinigame: true,
                distanceToPartner: 0f, rangeLimit: 20f));
        }
    }

    [Fact]
    public void ANaNDistanceRefusesUnderNearRatherThanAcceptingByAccident()
    {
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near, isForced: false,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: float.NaN, rangeLimit: 20f));
    }

    [Fact]
    public void CombatIsCheckedBeforeDistanceUnderNear()
    {
        // Close enough to join, but fighting: combat wins.
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near, isForced: false,
            localAlreadyInConversation: false, localInCombat: true, localInMinigame: false,
            distanceToPartner: 1f, rangeLimit: 20f));
    }

    // ---- isForced: the new matrix — forced × mode × range ------------------------------------

    [Theory]
    [InlineData(DialogueAutoJoinMode.Off)]
    [InlineData(DialogueAutoJoinMode.Near)]
    [InlineData(DialogueAutoJoinMode.Always)]
    public void ForcedJoinsUnderEveryModeIncludingOff(DialogueAutoJoinMode mode)
    {
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            mode, isForced: true,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 0f, rangeLimit: 20f));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(20f)]
    [InlineData(20.1f)]
    [InlineData(500f)]
    [InlineData(float.PositiveInfinity)]
    public void ForcedJoinsAtAnyDistanceUnderOff(float distance)
    {
        // Off is the default mode: an ORDINARY conversation at this distance would never join
        // (see OffNeverJoinsRegardlessOfDistance) — a FORCED one still does, at every distance
        // tried here, because distance is not this rule's concern once isForced is true.
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Off, isForced: true,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: distance, rangeLimit: 20f));
    }

    [Fact]
    public void ForcedJoinsEvenWithANaNDistance()
    {
        // NaN refuses an ORDINARY near-mode join (ANaNDistanceRefusesUnderNearRatherThanAcceptingByAccident)
        // precisely because NaN never compares true to the range check — isForced bypasses that
        // comparison entirely rather than relying on it, so a forced conversation is never
        // accidentally blocked by an unresolved position.
        Assert.True(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near, isForced: true,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: float.NaN, rangeLimit: 20f));
    }

    [Fact]
    public void ForcedStillRefusesWhenAlreadyInAConversation()
    {
        // A runtime-safety gate, not a policy choice — the engine cannot correctly enter a
        // second conversation regardless of how vanilla-forced the first one is.
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Off, isForced: true,
            localAlreadyInConversation: true, localInCombat: false, localInMinigame: false,
            distanceToPartner: 0f, rangeLimit: 20f));
    }

    [Fact]
    public void ForcedStillRefusesInCombat()
    {
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Off, isForced: true,
            localAlreadyInConversation: false, localInCombat: true, localInMinigame: false,
            distanceToPartner: 0f, rangeLimit: 20f));
    }

    [Fact]
    public void ForcedStillRefusesInAMinigame()
    {
        Assert.False(DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Off, isForced: true,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: true,
            distanceToPartner: 0f, rangeLimit: 20f));
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
            DialogueAutoJoinMode.Near, isForced: false,
            localAlreadyInConversation: false, localInCombat: false, localInMinigame: false,
            distanceToPartner: 5f, rangeLimit: 20f);

        bool hostJoiningClientsConversation = DialogueAutoJoinRule.ShouldAutoJoin(
            DialogueAutoJoinMode.Near, isForced: false,
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
    [InlineData(null, DialogueAutoJoinMode.Off)]
    [InlineData("", DialogueAutoJoinMode.Off)]
    [InlineData("nonsense", DialogueAutoJoinMode.Off)]
    public void ParseModeMatchesEachSettingAndFallsBackToOff(string? raw, DialogueAutoJoinMode expected)
    {
        Assert.Equal(expected, DialogueAutoJoinRule.ParseMode(raw));
    }
}

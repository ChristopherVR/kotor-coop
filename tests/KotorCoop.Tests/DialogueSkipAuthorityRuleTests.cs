using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Pins <see cref="DialogueSkipAuthorityRule"/> — who may skip or advance the conversation on
/// screen.
///
/// The bug being pinned shut, observed live: a client started a conversation, the host joined
/// it as a spectator, and the host's skip key ADVANCED it. Replies had been gated since the
/// dialogue-sync work; skip is a different engine call one branch away in the same switch
/// (<c>CSWGuiDialog::HandleInputEvent 0x006A7230</c>) and nothing checked it.
///
/// The three states that look least like "following" — <c>Standby</c>, <c>Yielding</c>,
/// <c>BackingOff</c> — get their own test each, because they are exactly the holes a narrower
/// rule (<c>IsFollowing</c>) would reopen, and <see cref="DialogueFollowMachine"/>'s own doc
/// comment records each of them as a window that was MEASURED in play.
/// </summary>
public class DialogueSkipAuthorityRuleTests
{
    /// <summary>Idle means "no peer holds the session's conversation token" — which is the
    /// state a player driving their OWN conversation is in. Skipping your own line is what the
    /// key is for.</summary>
    [Fact]
    public void TheOwnerMaySkipTheirOwnConversation()
    {
        Assert.True(DialogueSkipAuthorityRule.MaySkip(DialogueFollowState.Idle));
        Assert.False(DialogueSkipAuthorityRule.ShouldSwallowSkip(DialogueFollowState.Idle));
    }

    [Theory]
    [InlineData(DialogueFollowState.Standby)]
    [InlineData(DialogueFollowState.Yielding)]
    [InlineData(DialogueFollowState.Adopting)]
    [InlineData(DialogueFollowState.Opening)]
    [InlineData(DialogueFollowState.Following)]
    [InlineData(DialogueFollowState.BackingOff)]
    public void ASpectatorMayNotSkipInAnyStateAPeerHoldsTheToken(DialogueFollowState state)
    {
        Assert.True(DialogueSkipAuthorityRule.ShouldSwallowSkip(state));
        Assert.False(DialogueSkipAuthorityRule.MaySkip(state));
    }

    /// <summary>
    /// ⚠ THE WIDTH IS THE POINT. <c>Standby</c> is the 15-second join window, <c>Yielding</c>
    /// the hand-over window and <c>BackingOff</c> the post-failure window — in every one of
    /// them a peer holds the conversation while this instance is not "following" it. A rule
    /// written against <c>IsFollowing</c> (Adopting/Opening/Following only) would let a
    /// spectator skip in all three.
    /// </summary>
    [Fact]
    public void TheThreeNonFollowingStatesAreStillSpectating()
    {
        DialogueFollowMachine machine = new();
        Assert.False(machine.IsFollowing); // Idle

        foreach (DialogueFollowState state in
            (DialogueFollowState[])[DialogueFollowState.Standby, DialogueFollowState.Yielding,
                DialogueFollowState.BackingOff])
        {
            Assert.True(
                DialogueSkipAuthorityRule.ShouldSwallowSkip(state),
                $"{state} is a state a peer holds the conversation in, so skip must be swallowed");
        }
    }

    /// <summary>
    /// The rule agrees with the reply gag in every state, because the two are renewed from the
    /// same branch of the same frame pass and a window where a spectator may skip but not
    /// answer (or the reverse) is not a state anything downstream knows how to be in.
    /// </summary>
    [Theory]
    [InlineData(DialogueFollowState.Idle)]
    [InlineData(DialogueFollowState.Standby)]
    [InlineData(DialogueFollowState.Yielding)]
    [InlineData(DialogueFollowState.Adopting)]
    [InlineData(DialogueFollowState.Opening)]
    [InlineData(DialogueFollowState.Following)]
    [InlineData(DialogueFollowState.BackingOff)]
    public void SkipAndReplyAuthorityNeverDisagree(DialogueFollowState state)
    {
        bool replySuppressed = state != DialogueFollowState.Idle; // DialogueFollowMachine.SuppressReplies
        Assert.Equal(replySuppressed, DialogueSkipAuthorityRule.ShouldSwallowSkip(state));
    }

    /// <summary>
    /// Nothing in the rule reads "am I the host". The reported bug was a HOST skipping a
    /// CLIENT's conversation, and the same code must equally stop a client skipping the host's
    /// — there is no input that could make the two differ.
    /// </summary>
    [Fact]
    public void TheRuleHasNoHostOrClientInputToBeAsymmetricAbout()
    {
        // One argument, and it describes the conversation rather than the peer. Stated as a
        // test so that adding a role argument later has to break something first.
        Assert.Equal(
            DialogueSkipAuthorityRule.ShouldSwallowSkip(DialogueFollowState.Following),
            DialogueSkipAuthorityRule.ShouldSwallowSkip(DialogueFollowState.Following));
    }
}

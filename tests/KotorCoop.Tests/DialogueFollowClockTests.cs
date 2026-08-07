using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The two deadlines a followed conversation lives under. These exist because the content
/// deadline was measured from the wrong clock — from the REQUEST rather than from the OPEN —
/// which tore down working conversations that had merely been slow to arrive, leaving the
/// player with an empty dialogue box: no subtitles, no replies.
/// </summary>
public sealed class DialogueFollowClockTests
{
    private const long OpenTimeout = 15000;
    private const long ContentTimeout = 8000;

    private static DialogueFollowVerdict Evaluate(
        long nowMs, long requestedAtMs, long? openedAtMs, bool isOpen, bool hasContent) =>
        DialogueFollowClock.Evaluate(
            nowMs, requestedAtMs, openedAtMs, isOpen, hasContent, OpenTimeout, ContentTimeout);

    [Fact]
    public void WaitsWhileTheRequestIsStillOnItsWay()
    {
        Assert.Equal(
            DialogueFollowVerdict.Wait,
            Evaluate(nowMs: 9000, requestedAtMs: 0, openedAtMs: null, isOpen: false, hasContent: false));
    }

    [Fact]
    public void GivesUpOnARequestThatNeverBecameAConversation()
    {
        Assert.Equal(
            DialogueFollowVerdict.GiveUpNeverOpened,
            Evaluate(nowMs: 15001, requestedAtMs: 0, openedAtMs: null, isOpen: false, hasContent: false));
    }

    [Fact]
    public void DrivesAsSoonAsContentArrives()
    {
        Assert.Equal(
            DialogueFollowVerdict.Drive,
            Evaluate(nowMs: 3000, requestedAtMs: 0, openedAtMs: 2800, isOpen: true, hasContent: true));
    }

    /// <summary>
    /// The regression. A slow open (2.8 s, well within its own budget) followed by a content
    /// wait that is short on the content clock but long on the request clock. Measured from
    /// the request this is 6 s and would have been torn down; measured from the open it is
    /// 3.2 s and still has time.
    /// </summary>
    [Fact]
    public void ASlowOpenDoesNotSpendTheContentBudget()
    {
        Assert.Equal(
            DialogueFollowVerdict.Wait,
            Evaluate(nowMs: 6000, requestedAtMs: 0, openedAtMs: 2800, isOpen: true, hasContent: false));
    }

    [Fact]
    public void GivesUpOnAShellThatStaysEmptyPastTheContentDeadline()
    {
        Assert.Equal(
            DialogueFollowVerdict.GiveUpNoContent,
            Evaluate(nowMs: 10801, requestedAtMs: 0, openedAtMs: 2800, isOpen: true, hasContent: false));
    }

    /// <summary>
    /// The open deadline stops applying once the conversation has been seen: a conversation
    /// that opened at 14 s is fine at 16 s, even though the request is older than the open
    /// timeout.
    /// </summary>
    [Fact]
    public void TheOpenDeadlineDoesNotApplyAfterItHasOpened()
    {
        Assert.Equal(
            DialogueFollowVerdict.Drive,
            Evaluate(nowMs: 16000, requestedAtMs: 0, openedAtMs: 14000, isOpen: true, hasContent: true));
    }

    /// <summary>
    /// The engine's "conversation active" flag drops for a few frames between entries. That
    /// gap is not an empty shell — there is nothing on screen to escape from — and treating
    /// it as one is what made conversations close mid-line.
    /// </summary>
    [Fact]
    public void AGapBetweenEntriesIsNotAnEmptyShell()
    {
        Assert.Equal(
            DialogueFollowVerdict.Wait,
            Evaluate(nowMs: 30000, requestedAtMs: 0, openedAtMs: 2800, isOpen: false, hasContent: false));
    }

    [Fact]
    public void ContentExactlyAtTheDeadlineIsStillAllowed()
    {
        Assert.Equal(
            DialogueFollowVerdict.Wait,
            Evaluate(nowMs: 2800 + ContentTimeout, requestedAtMs: 0, openedAtMs: 2800, isOpen: true, hasContent: false));
    }
}

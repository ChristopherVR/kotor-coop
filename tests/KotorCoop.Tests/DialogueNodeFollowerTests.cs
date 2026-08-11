using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Which of the owner's positions a follower has already been driven to.
///
/// Both rules here are live defects rather than hypotheticals. Banking the owner's serial on a
/// reported success stranded a follower for a whole conversation, because
/// <c>CSWSObject::SendDialogEntry</c> returns 1 from its DEFER path too. And inheriting a
/// banked serial into the NEXT follow meant a joiner believed it had already been sent where
/// the owner was, and sat still until the owner moved again.
/// </summary>
public sealed class DialogueNodeFollowerTests
{
    [Fact]
    public void AFreshFollowerWantsWhereverTheOwnerIs()
    {
        DialogueNodeFollower follower = new();

        Assert.Equal(0u, follower.AppliedSerial);
        Assert.True(follower.NeedsNode(peerNode: 29, peerSerial: 7, hasContent: true));
    }

    /// <summary>-1 is "the owner has not said where it is". A follower that cannot be told
    /// where to go waits where it is, which is recoverable, rather than being sent to node 0 —
    /// the top of the conversation.</summary>
    [Fact]
    public void AnUnknownOwnerPositionIsNeverDriven()
    {
        DialogueNodeFollower follower = new();

        Assert.False(follower.NeedsNode(peerNode: -1, peerSerial: 7, hasContent: true));
    }

    /// <summary>Driving a shell that has delivered nothing races the open, and the open is what
    /// decides which panel the conversation is even running on.</summary>
    [Fact]
    public void NothingIsDrivenBeforeOurOwnConversationHasALine()
    {
        DialogueNodeFollower follower = new();

        Assert.False(follower.NeedsNode(peerNode: 29, peerSerial: 7, hasContent: false));
    }

    /// <summary>Entries auto-advance identically on both instances through any stretch of a
    /// conversation without a choice in it, so "already there" is the common case. Bank it, or
    /// every such node is re-examined on every frame for as long as the owner sits on it.</summary>
    [Fact]
    public void BeingAlreadyOnTheOwnersNodeBanksTheSerial()
    {
        DialogueNodeFollower follower = new();

        Assert.True(follower.Arrived(peerSerial: 7, localNode: 29, wantedNode: 29));
        Assert.Equal(7u, follower.AppliedSerial);
        Assert.False(follower.NeedsNode(peerNode: 29, peerSerial: 7, hasContent: true));
    }

    [Fact]
    public void ANewOwnerPositionIsWantedAgain()
    {
        DialogueNodeFollower follower = new();
        follower.Arrived(peerSerial: 7, localNode: 29, wantedNode: 29);

        Assert.True(follower.NeedsNode(peerNode: 19, peerSerial: 8, hasContent: true));
    }

    /// <summary>
    /// ⚠ THE STRANDING DEFECT. Measured: the client joined, drove 19 -> 29 three milliseconds
    /// after its conversation delivered content, was told "success" by a call that had merely
    /// QUEUED the entry, and sat on 19 for seventeen seconds with the serial already banked so
    /// it never tried again. The node itself is the acknowledgement.
    /// </summary>
    [Fact]
    public void ADeferredDriveBanksNothingAndIsRetried()
    {
        DialogueNodeFollower follower = new();

        Assert.False(follower.Arrived(peerSerial: 8, localNode: 19, wantedNode: 29));
        Assert.Equal(0u, follower.AppliedSerial);
        Assert.Equal(1, follower.NoteDeferral());
        Assert.True(follower.NeedsNode(peerNode: 29, peerSerial: 8, hasContent: true));
    }

    [Fact]
    public void DeferralsAccumulateUntilTheDriveLands()
    {
        DialogueNodeFollower follower = new();

        for (int i = 1; i <= 5; i++)
        {
            Assert.False(follower.Arrived(peerSerial: 8, localNode: 19, wantedNode: 29));
            Assert.Equal(i, follower.NoteDeferral());
        }

        Assert.True(follower.Arrived(peerSerial: 8, localNode: 29, wantedNode: 29));
        Assert.Equal(0, follower.Deferrals);
        Assert.Equal(8u, follower.AppliedSerial);
    }

    /// <summary>
    /// The old controller reset neither the applied serial nor the deferral count on its
    /// give-up path. A follow that began while the owner's serial was still the banked one
    /// therefore believed it had already been driven there and stood still, and a deferral
    /// count carried over made the "still trying" complaint fire early on a healthy follow.
    /// </summary>
    [Fact]
    public void ResetMakesTheNextFollowStartFromWhereverTheOwnerIs()
    {
        DialogueNodeFollower follower = new();
        follower.Arrived(peerSerial: 7, localNode: 29, wantedNode: 29);
        follower.Arrived(peerSerial: 8, localNode: 19, wantedNode: 29);
        follower.NoteDeferral();

        follower.Reset();

        Assert.Equal(0u, follower.AppliedSerial);
        Assert.Equal(0, follower.Deferrals);
        Assert.True(follower.NeedsNode(peerNode: 29, peerSerial: 7, hasContent: true));
    }

    /// <summary>A serial of zero is "no position published yet" on the wire. It must not be
    /// treated as one this follower has already applied — the applied serial starts at zero
    /// too, and the guard that keeps those apart is the node check, not the serial.</summary>
    [Fact]
    public void AZeroSerialIsNotMistakenForAnAppliedOne()
    {
        DialogueNodeFollower follower = new();

        // Serial 0 with a real node: nothing has been banked, so the two zeros compare equal
        // and nothing is driven. That is correct — a peer that has published a node has also
        // bumped its serial past zero (the publisher writes the serial LAST, after the node),
        // so serial 0 alongside a node only ever means a half-written pair.
        Assert.False(follower.NeedsNode(peerNode: 29, peerSerial: 0, hasContent: true));

        Assert.True(follower.NeedsNode(peerNode: 29, peerSerial: 1, hasContent: true));
    }
}

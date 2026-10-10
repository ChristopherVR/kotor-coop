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

    /// <summary>
    /// ⚠ THE "CATCH-UP WALK" THAT NEVER EXISTED. A joiner arriving after the owner has already
    /// advanced through dozens of entries does not replay each one in turn: node sync publishes
    /// only the owner's CURRENT position (not a history), and <c>Odyssey.DriveDialogueToNode</c>
    /// calls <c>CSWSDialog::SendDialogEntry</c> directly with the TARGET entry index — there is
    /// no intermediate node the engine or this class ever visits on the way. This is the
    /// pure-logic half of that claim: <see cref="DialogueNodeFollower.Arrived"/> only ever
    /// compares the CURRENT wanted node against wherever the engine reports landing NOW, so a
    /// follower forty entries behind confirms arrival with the exact same single call a
    /// follower one entry behind does — see docs/research/dialogue-join-latency.md, which found
    /// no per-node walk to remove and documents this test as the proof.
    /// </summary>
    [Fact]
    public void ALargeCatchUpJumpLandsInOneCallJustLikeASmallOne()
    {
        DialogueNodeFollower nearFollower = new();
        DialogueNodeFollower farFollower = new();

        // A fresh follow wants wherever the owner is, however far that is from node 0.
        Assert.True(nearFollower.NeedsNode(peerNode: 2, peerSerial: 1, hasContent: true));
        Assert.True(farFollower.NeedsNode(peerNode: 40, peerSerial: 1, hasContent: true));

        // One confirming read each — not one per entry skipped — lands both.
        Assert.True(nearFollower.Arrived(peerSerial: 1, localNode: 2, wantedNode: 2));
        Assert.True(farFollower.Arrived(peerSerial: 1, localNode: 40, wantedNode: 40));

        Assert.Equal(1u, nearFollower.AppliedSerial);
        Assert.Equal(1u, farFollower.AppliedSerial);
        Assert.Equal(0, nearFollower.Deferrals);
        Assert.Equal(0, farFollower.Deferrals);
        Assert.False(nearFollower.NeedsNode(peerNode: 2, peerSerial: 1, hasContent: true));
        Assert.False(farFollower.NeedsNode(peerNode: 40, peerSerial: 1, hasContent: true));
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

    /// <summary>Star Forge, 2026-10-10: the follower's engine reached Carth's next line 0.13 s
    /// before the owner's message did, and sending it straight back reran the opening entry's
    /// staging script.</summary>
    [Fact]
    public void AFollowerThatMovedOnByItselfWaitsForTheOwnersNextLine()
    {
        DialogueNodeFollower follower = new();
        Assert.True(follower.Arrived(peerSerial: 2, localNode: 0, wantedNode: 0));

        // Our engine moved 0 -> 1; the owner's serial still names 0.
        Assert.False(follower.NeedsNode(0, 2, true, localNode: 1, nowMs: 10_000));
        Assert.False(follower.NeedsNode(0, 2, true, localNode: 1, nowMs: 10_130));

        // The owner's message arrives: node 1, a new serial. Already there, so nothing to drive.
        Assert.True(follower.NeedsNode(1, 3, true, localNode: 1, nowMs: 10_140));
        Assert.True(follower.Arrived(peerSerial: 3, localNode: 1, wantedNode: 1));
        Assert.False(follower.NeedsNode(1, 3, true, localNode: 1, nowMs: 10_150));
    }

    [Fact]
    public void AFollowerThatWanderedIsSentBackAfterTheGrace()
    {
        DialogueNodeFollower follower = new();
        follower.Arrived(peerSerial: 2, localNode: 5, wantedNode: 5);

        Assert.False(follower.NeedsNode(5, 2, true, localNode: 9, nowMs: 0));
        Assert.True(follower.NeedsNode(5, 2, true, localNode: 9, nowMs: DialogueNodeFollower.LeadGraceMs));
    }

    [Fact]
    public void ANewOwnerPositionIsDrivenAtOnce()
    {
        DialogueNodeFollower follower = new();
        follower.Arrived(peerSerial: 2, localNode: 5, wantedNode: 5);

        Assert.True(follower.NeedsNode(7, 3, true, localNode: 5, nowMs: 0));
    }
}

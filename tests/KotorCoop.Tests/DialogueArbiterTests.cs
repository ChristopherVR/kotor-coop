using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class DialogueArbiterTests
{
    [Fact]
    public void ClaimOnAFreeTokenGrantsItAndBumpsTheSerial()
    {
        DialogueState next = DialogueArbiter.Apply(DialogueState.Idle, requesterPeerId: 2, active: true, "Trask", "This way.");

        Assert.True(next.Active);
        Assert.Equal(2u, next.OwnerPeerId);
        Assert.Equal(1u, next.Serial);
        Assert.Equal("Trask", next.SpeakerName);
        Assert.Equal("This way.", next.Line);
    }

    [Fact]
    public void OwnerUpdatingTheLineBumpsTheSerial()
    {
        DialogueState granted = DialogueArbiter.Apply(DialogueState.Idle, 2, true, "Trask", "Line one.");
        DialogueState updated = DialogueArbiter.Apply(granted, 2, true, "Trask", "Line two.");

        Assert.Equal(2u, updated.OwnerPeerId);
        Assert.Equal("Line two.", updated.Line);
        Assert.Equal(granted.Serial + 1, updated.Serial);
    }

    [Fact]
    public void OwnerReSendingTheSameLineDoesNotChurnTheSerial()
    {
        DialogueState granted = DialogueArbiter.Apply(DialogueState.Idle, 2, true, "Trask", "Line one.");
        DialogueState again = DialogueArbiter.Apply(granted, 2, true, "Trask", "Line one.");

        Assert.Equal(granted, again);
    }

    [Fact]
    public void AContenderIsIgnoredWhileTheTokenIsHeld()
    {
        DialogueState granted = DialogueArbiter.Apply(DialogueState.Idle, 2, true, "Trask", "Mine.");
        DialogueState contended = DialogueArbiter.Apply(granted, requesterPeerId: 3, active: true, "Carth", "No, mine.");

        // First speaker keeps it, unchanged.
        Assert.Equal(granted, contended);
        Assert.Equal(2u, contended.OwnerPeerId);
    }

    [Fact]
    public void OnlyTheOwnerCanReleaseTheToken()
    {
        DialogueState granted = DialogueArbiter.Apply(DialogueState.Idle, 2, true, "Trask", "Mine.");

        DialogueState byStranger = DialogueArbiter.Apply(granted, requesterPeerId: 3, active: false, "", "");
        Assert.Equal(granted, byStranger); // a non-owner's release is a no-op

        DialogueState released = DialogueArbiter.Apply(granted, requesterPeerId: 2, active: false, "", "");
        Assert.False(released.Active);
        Assert.Equal(0u, released.OwnerPeerId);
        Assert.Equal(granted.Serial + 1, released.Serial);
    }

    [Fact]
    public void ReleasingAnAlreadyIdleTokenIsANoOp()
    {
        DialogueState next = DialogueArbiter.Apply(DialogueState.Idle, 2, active: false, "", "");
        Assert.Equal(DialogueState.Idle, next);
    }

    [Fact]
    public void AfterReleaseAFreshClaimByAnotherPeerSucceeds()
    {
        DialogueState granted = DialogueArbiter.Apply(DialogueState.Idle, 2, true, "Trask", "Mine.");
        DialogueState released = DialogueArbiter.Apply(granted, 2, false, "", "");
        DialogueState reclaimed = DialogueArbiter.Apply(released, requesterPeerId: 3, active: true, "Carth", "My turn.");

        Assert.True(reclaimed.Active);
        Assert.Equal(3u, reclaimed.OwnerPeerId);
    }

    [Fact]
    public void CodecRoundTripsBothDialoguePackets()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        DialogueSnapshotPacket snapshot = new(
            123456789, new DialogueState(true, 4, 9, "Bastila", "We should not be here."));
        int snapLength = PacketCodec.Write(buffer, snapshot);
        Assert.Equal(snapshot, PacketCodec.ReadDialogueSnapshot(buffer[..snapLength]));

        DialogueRequestPacket request = new(4, true, "Bastila", "We should not be here.");
        int reqLength = PacketCodec.Write(buffer, request);
        Assert.Equal(request, PacketCodec.ReadDialogueRequest(buffer[..reqLength]));

        ChatPacket chat = new(4, "Bastila", "regroup at the door");
        int chatLength = PacketCodec.Write(buffer, chat);
        Assert.Equal(chat, PacketCodec.ReadChat(buffer[..chatLength]));

        ProgressionSnapshotPacket progression = new(987654321, 4200u, 15750u);
        int progLength = PacketCodec.Write(buffer, progression);
        Assert.Equal(progression, PacketCodec.ReadProgressionSnapshot(buffer[..progLength]));
    }

    [Fact]
    public void TheOwnersNodeSurvivesBothDialogueRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        DialogueSnapshotPacket snapshot = new(
            42, new DialogueState(true, 4, 9, "Trask", "This way.", "trask", 3, 1, null, 17, 5));
        int snapLength = PacketCodec.Write(buffer, snapshot);
        DialogueSnapshotPacket? readSnapshot = PacketCodec.ReadDialogueSnapshot(buffer[..snapLength]);
        Assert.Equal(17, readSnapshot!.State.NodeIndex);
        Assert.Equal(5u, readSnapshot.State.NodeSerial);

        DialogueRequestPacket request = new(4, true, "Trask", "This way.", "trask", 3, 1, null, 17, 5);
        int reqLength = PacketCodec.Write(buffer, request);
        Assert.Equal(request, PacketCodec.ReadDialogueRequest(buffer[..reqLength]));
    }

    /// <summary>An unknown node is a first-class value, and it must stay -1 rather than
    /// arriving as 0 — node 0 is the top of the conversation, so a follower sent there would
    /// be yanked back to the beginning every time the owner's position was unavailable.</summary>
    [Fact]
    public void AnUnknownNodeStaysUnknownAcrossTheWire()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        DialogueSnapshotPacket snapshot = new(1, new DialogueState(true, 4, 9, "Trask", "Hi"));
        int length = PacketCodec.Write(buffer, snapshot);

        Assert.Equal(DialogueState.NoNode, PacketCodec.ReadDialogueSnapshot(buffer[..length])!.State.NodeIndex);
    }

    /// <summary>A node index that is neither real nor the explicit "unknown" can only come
    /// from a corrupt or mismatched sender, and the cost of trusting one is a follower driven
    /// to a line the owner is not on. Both directions reject it the same way.</summary>
    [Theory]
    [InlineData(-2)]
    [InlineData(int.MinValue)]
    [InlineData(4096)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeNodeIsReadAsUnknown(int nodeIndex)
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];

        DialogueSnapshotPacket snapshot = new(
            1, new DialogueState(true, 4, 9, "Trask", "Hi", "trask", 0, 0, null, nodeIndex, 2));
        int snapLength = PacketCodec.Write(buffer, snapshot);
        Assert.Equal(
            DialogueState.NoNode,
            PacketCodec.ReadDialogueSnapshot(buffer[..snapLength])!.State.NodeIndex);

        DialogueRequestPacket request = new(4, true, "Trask", "Hi", "trask", 0, 0, null, nodeIndex, 2);
        int reqLength = PacketCodec.Write(buffer, request);
        Assert.Equal(DialogueState.NoNode, PacketCodec.ReadDialogueRequest(buffer[..reqLength])!.NodeIndex);
    }

    /// <summary>The node serial is what moves a follower, so it has to count as a change on
    /// its own. Before node sync the arbiter coalesced any update whose speaker, line, tag and
    /// reply serial matched — and a conversation that loops back to a line it has already
    /// shown does exactly that while genuinely being somewhere else.</summary>
    [Fact]
    public void AChangedNodeBumpsTheSerialEvenWhenTheLineRepeats()
    {
        DialogueState granted = DialogueArbiter.Apply(
            DialogueState.Idle, 2, true, "Trask", "This way.", "trask", 0, 0, null, 4, 1);
        DialogueState moved = DialogueArbiter.Apply(
            granted, 2, true, "Trask", "This way.", "trask", 0, 0, null, 9, 2);

        Assert.Equal(granted.Serial + 1, moved.Serial);
        Assert.Equal(9, moved.NodeIndex);
    }

    /// <summary>The whole point of the veto: "first speaker wins" is only fair if both claims
    /// race on equal terms, and they never did — the host's claim reaches the arbiter
    /// in-process while a client's crosses the network. The lock records who actually got to
    /// the NPC first, and it must beat a FREE token, not merely a contested one.</summary>
    [Fact]
    public void AClaimIsRefusedWhenAnotherPeerHoldsTheObject()
    {
        DialogueState refused = DialogueArbiter.Apply(
            DialogueState.Idle, requesterPeerId: 1, active: true, "Trask", "Mine.", "trask",
            lockHolder: 2);

        Assert.Equal(DialogueState.Idle, refused);
    }

    [Fact]
    public void TheLockHolderItselfIsStillGrantedTheToken()
    {
        DialogueState granted = DialogueArbiter.Apply(
            DialogueState.Idle, requesterPeerId: 2, active: true, "Trask", "Mine.", "trask",
            lockHolder: 2);

        Assert.True(granted.Active);
        Assert.Equal(2u, granted.OwnerPeerId);
    }

    /// <summary>A claim whose partner tag has not resolved yet must NOT be refused. The tag is
    /// empty for a real window at the start of every conversation — the GUI's participant ids
    /// stay OBJECT_INVALID until the server resolves them — so refusing on an empty tag would
    /// refuse every conversation in the game for its first few hundred milliseconds.</summary>
    [Fact]
    public void AClaimWithNoPartnerTagYetIsNotRefused()
    {
        DialogueState granted = DialogueArbiter.Apply(
            DialogueState.Idle, requesterPeerId: 1, active: true, "", "", partnerTag: "",
            lockHolder: 2);

        Assert.True(granted.Active);
        Assert.Equal(1u, granted.OwnerPeerId);
    }

    [Fact]
    public void AnUnchangedNodeDoesNotChurnTheSerial()
    {
        DialogueState granted = DialogueArbiter.Apply(
            DialogueState.Idle, 2, true, "Trask", "This way.", "trask", 0, 0, null, 4, 1);
        DialogueState again = DialogueArbiter.Apply(
            granted, 2, true, "Trask", "This way.", "trask", 0, 0, null, 4, 1);

        Assert.Equal(granted, again);
    }
}

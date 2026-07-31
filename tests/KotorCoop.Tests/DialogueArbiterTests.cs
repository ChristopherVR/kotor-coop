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
}

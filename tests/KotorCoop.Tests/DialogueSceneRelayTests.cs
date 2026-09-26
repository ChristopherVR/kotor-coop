using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class DialogueSceneRelayTests
{
    [Fact]
    public void DialogueResourceBuffersStayOutsideTheEnemyTablesAndInsideTheBlock()
    {
        Assert.Equal(0x8600, KotorCoop.Plugin.SharedBlock.OffsetLocalDialogueResref);
        Assert.Equal(KotorCoop.Plugin.SharedBlock.OffsetLocalDialogueResref
            + KotorCoop.Plugin.SharedBlock.DialogueResrefLength,
            KotorCoop.Plugin.SharedBlock.OffsetRemoteDialogueResref);
        Assert.True(KotorCoop.Plugin.SharedBlock.OffsetRemoteDialogueResref
            + KotorCoop.Plugin.SharedBlock.DialogueResrefLength
            <= KotorCoop.Plugin.SharedBlock.TotalSize);
    }

    [Fact]
    public void ForcedSceneOverrideSurvivesClaimArbitrationAndSnapshot()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        var request = new DialogueRequestPacket(2, true, "", "", "dan14aa_bay",
            NodeIndex: 1, NodeSerial: 4, Forced: true, DialogResref: "dan14_cut01");
        int length = PacketCodec.Write(buffer, request);
        Assert.True(length > 0);
        var decoded = PacketCodec.ReadDialogueRequest(buffer.AsSpan(0, length))!;
        var token = DialogueArbiter.Apply(DialogueState.Idle, decoded.PeerId,
            decoded.Active, decoded.SpeakerName, decoded.Line, decoded.PartnerTag,
            nodeIndex: decoded.NodeIndex, nodeSerial: decoded.NodeSerial,
            forced: decoded.Forced, dialogResref: decoded.DialogResref);
        length = PacketCodec.Write(buffer, new DialogueSnapshotPacket(100, token));
        Assert.True(length > 0);
        var snapshot = PacketCodec.ReadDialogueSnapshot(buffer.AsSpan(0, length))!;
        Assert.Equal("dan14aa_bay", snapshot.State.PartnerTag);
        Assert.Equal("dan14_cut01", snapshot.State.DialogResref);
        Assert.True(snapshot.State.Forced);
        Assert.Equal(1, snapshot.State.NodeIndex);
    }

    [Fact]
    public void ResrefChangeUpdatesTheOwnerTokenEvenWhenTheNodeIsUnchanged()
    {
        var token = DialogueArbiter.Apply(DialogueState.Idle, 1, true, "", "", "actor");
        var changed = DialogueArbiter.Apply(token, 1, true, "", "", "actor",
            dialogResref: "scene");
        Assert.True(changed.Serial > token.Serial);
        Assert.Equal("scene", changed.DialogResref);
        Assert.Equal(changed, DialogueArbiter.Apply(changed, 1, true, "", "", "actor",
            dialogResref: "scene"));
    }

    [Fact]
    public void TruncatedSceneNamesAreRejected()
    {
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new DialogueRequestPacket(1, true, "", "",
            DialogResref: "scene"));
        Assert.Null(PacketCodec.ReadDialogueRequest(buffer.AsSpan(0, length - 1)));
    }

    [Fact]
    public void AQueuedLocalEntryCannotUndoAnAcknowledgedOwnerNode()
    {
        DialogueNodeFollower follower = new();
        Assert.True(follower.Arrived(20, 28, 28));
        Assert.False(follower.NeedsNode(28, 20, true, 28));
        // Observed on the listener: acknowledged node 28, then its engine sent node 0.
        Assert.True(follower.NeedsNode(28, 20, true, 0));
        Assert.False(follower.NeedsNode(28, 0, true, 0));
        Assert.False(follower.NeedsNode(28, 20, false, 0));
    }

    [Theory]
    [InlineData(true, false, 999, true)]
    [InlineData(true, false, 1001, false)]
    [InlineData(true, true, 0, false)]
    [InlineData(false, false, 0, false)]
    public void AutoFollowWaitsForALiveConnection(bool connected, bool lost, long age, bool expected)
        => Assert.Equal(expected, AutoFollowRule.MayFollowLink(connected, lost, age));
}

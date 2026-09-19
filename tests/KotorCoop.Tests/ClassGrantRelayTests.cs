using System.Text.RegularExpressions;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The class-grant relay (docs/research/class-and-power-grants.md, coop.ini
/// <c>class_grant_sync</c>): the wire codec and its numbering, the reliable sender's
/// (origin, sequence) discipline, the plugin's two pure rules (what to publish, what to
/// apply), the receiver's pure rule (when to apply, how to settle the plugin's answer), the
/// class-name table, the <c>addclass</c> verb's parse, and the block-layout agreement between
/// the plugin and the agent halves.
/// </summary>
public class ClassGrantRelayTests
{
    // ---- codec ------------------------------------------------------------------------

    [Fact]
    public void TheGrantRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        ClassGrantPacket original = new(OriginPeerId: 3, OpSequence: 9, ClassId: 4);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        ClassGrantPacket? read = PacketCodec.ReadClassGrant(buffer[..length]);
        Assert.NotNull(read);
        Assert.Equal(original, read);
    }

    [Fact]
    public void TheAckRoundTrips()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        ClassGrantAckPacket original = new(PeerId: 2, OriginPeerId: 3, OpSequence: 9);

        int length = PacketCodec.Write(buffer, original);
        Assert.True(length > 0);

        Assert.Equal(original, PacketCodec.ReadClassGrantAck(buffer[..length]));
    }

    [Fact]
    public void AClassOutOfRangeIsRefusedOnRead()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new ClassGrantPacket(3, 1, ClassId: 4));

        // Corrupt the class in place: the codec's reader is the last line of defence before
        // the plugin's own apply rule.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buffer[(length - 4)..], 99);

        Assert.Null(PacketCodec.ReadClassGrant(buffer[..length]));
    }

    [Fact]
    public void AWrongPacketTypeIsRefused()
    {
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int length = PacketCodec.Write(buffer, new ClassGrantAckPacket(1, 2, 3));

        Assert.Null(PacketCodec.ReadClassGrant(buffer[..length]));
        Assert.Null(PacketCodec.ReadClassGrantAck(buffer[..PacketCodec.Write(buffer, new ClassGrantPacket(1, 2, 3))]));
    }

    [Fact]
    public void ThePacketTypesAndVersionAreWhereTheChangelogSaysTheyAre()
    {
        Assert.Equal(78, (byte)PacketType.ClassGrant);
        Assert.Equal(79, (byte)PacketType.ClassGrantAck);
        Assert.Equal(53, ProtocolConstants.Version);
    }

    // ---- reliable sender ----------------------------------------------------------------

    [Fact]
    public void ALocalGrantIsNumberedAndRetransmittedUntilEveryRecipientAcks()
    {
        ClassGrantSender sender = new(retransmitIntervalMs: 100, maxIntervalMs: 100);

        ClassGrantPacket? packet = sender.EnqueueLocal(originPeerId: 1, classId: 3, nowMs: 0);
        Assert.NotNull(packet);
        Assert.Equal(1u, packet.OpSequence);
        Assert.Equal(3, packet.ClassId);

        uint[] recipients = [2, 3];
        Assert.Single(sender.CollectDue(recipients, 0));
        Assert.Empty(sender.CollectDue(recipients, 50)); // inside the backoff
        Assert.Single(sender.CollectDue(recipients, 200));

        sender.Acknowledge(1, 1, ackingPeerId: 2);
        Assert.Single(sender.CollectDue(recipients, 400)); // peer 3 still owes an ack

        sender.Acknowledge(1, 1, ackingPeerId: 3);
        Assert.Empty(sender.CollectDue(recipients, 600));
        Assert.Equal(0, sender.PendingCount);
    }

    [Fact]
    public void AnEmptyRecipientListIsNotEveryoneHasAcked()
    {
        ClassGrantSender sender = new();
        sender.EnqueueLocal(1, 3, 0);

        Assert.Empty(sender.CollectDue([], 0));
        Assert.Equal(1, sender.PendingCount);
        Assert.Single(sender.CollectDue([2], 0));
    }

    [Fact]
    public void ARelayedGrantKeepsItsOriginAndIsNeverQueuedTwice()
    {
        ClassGrantSender sender = new();
        ClassGrantPacket fromClient = new(OriginPeerId: 5, OpSequence: 7, ClassId: 5);

        Assert.True(sender.EnqueueRelay(fromClient, 0));
        Assert.False(sender.EnqueueRelay(fromClient, 10)); // the client's retransmit

        List<ClassGrantPacket> due = sender.CollectDue([2], 0);
        Assert.Single(due);
        Assert.Equal(5u, due[0].OriginPeerId);
        Assert.Equal(7u, due[0].OpSequence);

        // Even after it retires, the same op is remembered and refused.
        sender.Acknowledge(5, 7, 2);
        Assert.Empty(sender.CollectDue([2], 0));
        Assert.False(sender.EnqueueRelay(fromClient, 20));
    }

    [Fact]
    public void ABadClassIsRefusedAtEnqueue()
    {
        ClassGrantSender sender = new();
        Assert.Null(sender.EnqueueLocal(1, classId: 9, nowMs: 0));
        Assert.Null(sender.EnqueueLocal(1, classId: -1, nowMs: 0));
        Assert.Equal(0, sender.PendingCount);
    }

    // ---- plugin: what to publish --------------------------------------------------------

    [Fact]
    public void AScriptsGrantToTheLocalPlayerIsPublished()
    {
        Assert.Equal(
            ClassGrantCaptureVerdict.Publish,
            ClassGrantCaptureRule.Decide(targetObjectId: 0x7FFFFFFF, localPlayerObjectId: 0x7FFFFFFF, classId: 3, isReplay: false));
    }

    [Fact]
    public void AReplayIsNeverRepublished()
    {
        Assert.Equal(
            ClassGrantCaptureVerdict.IgnoreReplay,
            ClassGrantCaptureRule.Decide(0x7FFFFFFF, 0x7FFFFFFF, 3, isReplay: true));
    }

    [Fact]
    public void AGrantToAnotherCreatureIsNotOursToRelay()
    {
        Assert.Equal(ClassGrantCaptureVerdict.IgnoreOtherTarget, ClassGrantCaptureRule.Decide(0x131, 0x7FFFFFFF, 3, false));
        Assert.Equal(ClassGrantCaptureVerdict.IgnoreOtherTarget, ClassGrantCaptureRule.Decide(0x7FFFFFFF, 0, 3, false));
    }

    [Fact]
    public void AClassOutsideTheTableIsNotPublished()
    {
        Assert.Equal(ClassGrantCaptureVerdict.IgnoreBadClass, ClassGrantCaptureRule.Decide(0x7FFFFFFF, 0x7FFFFFFF, 9, false));
        Assert.True(ClassGrantCaptureRule.IsJediClass(3));
        Assert.True(ClassGrantCaptureRule.IsJediClass(5));
        Assert.False(ClassGrantCaptureRule.IsJediClass(0));
        Assert.False(ClassGrantCaptureRule.IsJediClass(6));
    }

    // ---- plugin: what to apply ----------------------------------------------------------

    [Fact]
    public void ASoldierGetsTheJediClassInTheSecondSlot()
    {
        Assert.Equal(
            ClassGrantApplyVerdict.Apply,
            ClassGrantApplyRule.Decide(requestedClass: 3, listReadable: true, classCount: 1, class0: 0, class1: -1));
    }

    [Fact]
    public void AClassAlreadyHeldIsAckedNotAddedAgain()
    {
        Assert.Equal(ClassGrantApplyVerdict.AlreadyHas, ClassGrantApplyRule.Decide(3, true, 2, 0, 3));
        Assert.Equal(ClassGrantApplyVerdict.AlreadyHas, ClassGrantApplyRule.Decide(0, true, 1, 0, -1));
    }

    [Fact]
    public void TwoOtherClassesLeaveNoSlot()
    {
        Assert.Equal(ClassGrantApplyVerdict.SlotFull, ClassGrantApplyRule.Decide(4, true, 2, 0, 3));
    }

    [Fact]
    public void ABadClassOrAnUnreadableListIsNotApplied()
    {
        Assert.Equal(ClassGrantApplyVerdict.BadClass, ClassGrantApplyRule.Decide(9, true, 1, 0, -1));
        Assert.Equal(ClassGrantApplyVerdict.NotReady, ClassGrantApplyRule.Decide(3, false, 0, -1, -1));
    }

    // ---- receiver: when to apply, how to settle ---------------------------------------

    [Fact]
    public void TheReceiverWaitsOutItsOwnLoadAndConversation()
    {
        Assert.Equal(ClassGrantReplayPath.Wait, ClassGrantReplayRule.Decide("", false, false));
        Assert.Equal(ClassGrantReplayPath.Wait, ClassGrantReplayRule.Decide("danm13", false, localMidTransition: true));
        Assert.Equal(ClassGrantReplayPath.Wait, ClassGrantReplayRule.Decide("danm13", inDialogue: true, false));
        Assert.Equal(ClassGrantReplayPath.Apply, ClassGrantReplayRule.Decide("danm13", false, false));
    }

    [Fact]
    public void AGrantIsNotBoundToAModuleOrAMoment()
    {
        // The leader clicked on Dantooine; the receiver is on Tatooine, minutes later: still
        // applied — a class is durable world truth, not a moment.
        Assert.Equal(ClassGrantReplayPath.Apply, ClassGrantReplayRule.Decide("tat_m17aa", false, false));
    }

    [Fact]
    public void ThePluginsAnswerSettlesTheGrant()
    {
        Assert.Equal(ClassGrantReplayOutcome.Done, ClassGrantReplayRule.Settle(ClassGrantReplayRule.StatusApplied, 1));
        Assert.Equal(ClassGrantReplayOutcome.Done, ClassGrantReplayRule.Settle(ClassGrantReplayRule.StatusAlreadyHas, 1));
        Assert.Equal(ClassGrantReplayOutcome.GiveUp, ClassGrantReplayRule.Settle(ClassGrantReplayRule.StatusBadClass, 1));
        Assert.Equal(ClassGrantReplayOutcome.GiveUp, ClassGrantReplayRule.Settle(ClassGrantReplayRule.StatusSlotFull, 1));
        Assert.Equal(ClassGrantReplayOutcome.Retry, ClassGrantReplayRule.Settle(ClassGrantReplayRule.StatusEngineRefused, 1));
        Assert.Equal(ClassGrantReplayOutcome.Retry, ClassGrantReplayRule.Settle(0, ClassGrantReplayRule.MaxAttempts - 1)); // silence
        Assert.Equal(ClassGrantReplayOutcome.GiveUp, ClassGrantReplayRule.Settle(ClassGrantReplayRule.StatusEngineRefused, ClassGrantReplayRule.MaxAttempts));
    }

    [Fact]
    public void TheClassTableNamesTheThreeJediRows()
    {
        Assert.Equal("Jedi Guardian", CreatureClassNames.Display(3));
        Assert.Equal("Jedi Consular", CreatureClassNames.Display(4));
        Assert.Equal("Jedi Sentinel", CreatureClassNames.Display(5));
        Assert.Equal("Soldier", CreatureClassNames.Display(0));
        Assert.Equal("class 42", CreatureClassNames.Display(42));
    }

    // ---- the addclass verb --------------------------------------------------------------

    [Fact]
    public void AddClassParsesOneClassRow()
    {
        CoopCommand command = ConsoleCommand.Parse("addclass 3");
        Assert.Equal(CoopCommandKind.AddClass, command.Kind);
        Assert.Equal(3, command.Amount);

        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("addclass").Kind);
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("addclass 9").Kind);
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("addclass jedi").Kind);
    }

    // ---- block layout: both halves agree ------------------------------------------------

    /// <summary>The plugin's SharedBlock.ClassGrant.cs and the agent's
    /// KotorGameBridge.PluginBlock.cs each declare the six offsets by hand; a half-move reads
    /// as a request the plugin never answers. Scraped from source, the way
    /// <c>TransitionMovieTests.TheBlockVersionMovedInBothHalves</c> checks the version.</summary>
    [Fact]
    public void TheClassGrantRegionIsDeclaredIdenticallyInBothHalves()
    {
        string root = RepositoryRoot();
        string plugin = File.ReadAllText(Path.Combine(root, "src", "KotorCoop.Plugin", "SharedBlock.ClassGrant.cs"));
        string bridge = File.ReadAllText(Path.Combine(root, "src", "KotorCoop.Memory", "KotorGameBridge.PluginBlock.cs"));

        string[] names = ["Serial", "Class", "RequestSerial", "RequestClass", "Applied", "Status"];
        foreach (string name in names)
        {
            Match p = Regex.Match(plugin, $@"OffsetClassGrant{name}\s*=\s*0x([0-9A-Fa-f]+);");
            Match b = Regex.Match(bridge, $@"BlockClassGrant{name}\s*=\s*0x([0-9A-Fa-f]+);");
            Assert.True(p.Success, $"plugin half lacks OffsetClassGrant{name}");
            Assert.True(b.Success, $"agent half lacks BlockClassGrant{name}");

            int offset = Convert.ToInt32(p.Groups[1].Value, 16);
            Assert.Equal(offset, Convert.ToInt32(b.Groups[1].Value, 16));
            Assert.True(offset >= 0x4C18 && offset + 4 <= SharedBlock.TotalSize, $"{name} at 0x{offset:X} is outside the region's tail");
        }
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KotorCoop.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory.FullName;
    }
}

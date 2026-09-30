using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ReviveEffectEventRuleTests
{
    private readonly struct Nodes(System.Collections.Generic.Dictionary<nint, (nint Next, nint Record)> nodes) : IReviveQueueNodeReader
    {
        public bool TryReadNode(nint node, out nint next, out nint record)
        {
            bool valid = nodes.TryGetValue(node, out var value);
            next = value.Next; record = value.Record; return valid;
        }
    }

    [Fact]
    public void ForeignHeadDoesNotHideSecondMatchingEvent()
    {
        var nodes = new Nodes(new() { [10] = (20, 100), [20] = (0, 200) });
        var walk = new ReviveQueueWalker<Nodes>(10, 4, nodes);
        Assert.True(walk.TryMoveNext(out nint foreign)); Assert.Equal(100, foreign);
        Assert.True(walk.TryMoveNext(out nint matching)); Assert.Equal(200, matching);
        Assert.False(walk.TryMoveNext(out _)); Assert.False(walk.Failed);
    }

    [Fact]
    public void CyclicOrUnreadableQueueCannotProduceCompleteSnapshot()
    {
        var cycle = new ReviveQueueWalker<Nodes>(10, 2, new(new() { [10] = (20, 100), [20] = (10, 200) }));
        Assert.True(cycle.TryMoveNext(out _)); Assert.True(cycle.TryMoveNext(out _));
        Assert.False(cycle.TryMoveNext(out _)); Assert.True(cycle.Failed);
        var unreadable = new ReviveQueueWalker<Nodes>(10, 4, new(new() { [10] = (20, 100) }));
        Assert.True(unreadable.TryMoveNext(out _));
        Assert.False(unreadable.TryMoveNext(out _)); Assert.True(unreadable.Failed);
    }
    private static readonly ReviveRemovalEvent Event = new(100, 1, 2, 0x77, 0x7FFFFFFD, 14, 200, 300);

    [Fact]
    public void HeadRemovalProvesForwardLinkInsteadOfPreviousLink()
    {
        var code = ReviveEffectEventRule.QueueHeadRemoval;
        Assert.Equal(new byte[] { 0x8B, 0x41, 0x04 }, code[..3].ToArray());
        Assert.Equal(4, ReviveEffectEventRule.NextNodeOffset);
        Assert.Equal(new byte[] { 0x8B, 0x79, 0x08 }, code.Slice(6, 3).ToArray());
    }

    [Fact]
    public void NativeNullPayloadAndNoDestructorContractMustMatchExactly()
    {
        byte[] code = ReviveEffectEventRule.NullPayloadBranch.ToArray();
        Assert.True(ReviveEffectEventRule.NativeContractMatches(code, 14, 1, 0x4B0B2D));
        Assert.False(ReviveEffectEventRule.NativeContractMatches(code, 15, 1, 0x4B0B2D));
        Assert.False(ReviveEffectEventRule.NativeContractMatches(code, 14, 2, 0x4B0B2D));
        Assert.False(ReviveEffectEventRule.NativeContractMatches(code, 14, 1, 0x4B0AD1));
        for (int i = 0; i < code.Length; i++)
        {
            code[i] ^= 1;
            Assert.False(ReviveEffectEventRule.NativeContractMatches(code, 14, 1, 0x4B0B2D));
            code[i] ^= 1;
        }
    }

    [Fact]
    public void NativeEffectOffsetsAndEventRecordStoresMustMatchExactly()
    {
        byte[] removal = ReviveEffectEventRule.ResurrectionEffectRemoval.ToArray();
        byte[] queue = ReviveEffectEventRule.QueueRecordWrites.ToArray();
        Assert.True(ReviveEffectEventRule.LayoutMatches(removal, queue, 57));
        Assert.False(ReviveEffectEventRule.LayoutMatches(removal, queue, 56));
        for (int i = 0; i < removal.Length; i++)
        {
            removal[i] ^= 1;
            Assert.False(ReviveEffectEventRule.LayoutMatches(removal, queue, 57));
            removal[i] ^= 1;
        }
        for (int i = 0; i < queue.Length; i++)
        {
            queue[i] ^= 1;
            Assert.False(ReviveEffectEventRule.LayoutMatches(removal, queue, 57));
            queue[i] ^= 1;
        }
    }

    [Theory]
    [InlineData(57, 1, true)]
    [InlineData(57, 9, true)]
    [InlineData(57, 0, false)]
    [InlineData(57, 2, false)]
    [InlineData(4, 1, false)]
    public void OnlyNativeResurrectionRemovalSubsetIsEligible(ushort type, byte subtype, bool expected) =>
        Assert.Equal(expected, ReviveEffectEventRule.RemovedByResurrection(type, subtype));

    [Fact]
    public void ExactRedundantExpiryMayRetireOnlyAfterSuccessfulRemoval()
    {
        Assert.True(Allow(Event));
        Assert.False(ReviveEffectEventRule.MayRetire(Event, Event, Event.Target, false, true, true));
        Assert.False(ReviveEffectEventRule.MayRetire(Event, Event, Event.Target, true, false, true));
        Assert.False(ReviveEffectEventRule.MayRetire(Event, Event, Event.Target, true, true, false));
        Assert.False(ReviveEffectEventRule.MayRetire(Event, Event, 999, true, true, true));
    }

    [Fact]
    public void ReusedRecordOrReorderedReplacedEventCannotRetire()
    {
        Assert.False(Allow(Event with { Record = 101 }));
        Assert.False(Allow(Event with { Day = 2 }));
        Assert.False(Allow(Event with { Time = 3 }));
        Assert.False(Allow(Event with { Sender = 0x78 }));
        Assert.False(Allow(Event with { Target = 999 }));
        Assert.False(Allow(Event with { Type = 15 }));
        Assert.False(Allow(Event with { Payload = 201 }));
        Assert.False(Allow(Event with { EffectId = 301 }));
    }

    private static bool Allow(ReviveRemovalEvent current) =>
        ReviveEffectEventRule.MayRetire(Event, current, Event.Target, true, true, true);
}

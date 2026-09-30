using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ReviveEffectEventRuleTests
{
    private static readonly ReviveRemovalEvent Event = new(100, 1, 2, 0x77, 0x7FFFFFFD, 14, 200, 300);

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

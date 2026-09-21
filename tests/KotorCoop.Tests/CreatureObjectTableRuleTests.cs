using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The bounded, pure contract used by the plugin's in-process server object-table walk.
/// The native walk itself cannot run in the x64 test host, so these tests pin the table's
/// bucket/id arithmetic and the finite guards that make a torn chain fail closed.
/// </summary>
public sealed class CreatureObjectTableRuleTests
{
    [Theory]
    [InlineData(0x035, 0x035u, 0x00000035u)]
    [InlineData(0x1035, 0x035u, 0x80000035u)]
    [InlineData(0x1FFF, 0x7FFF_FFFFu, 0xFFFF_FFFFu)]
    public void BucketHighBitReconstructsTheEngineObjectId(int bucket, uint key, uint expected)
    {
        Assert.Equal(expected, CreatureObjectTableRule.FullObjectId(bucket, key));
    }

    [Fact]
    public void BucketSelectsHighBitWhilePreservingTheRemainingKey()
    {
        // The table stores the engine's 31-bit key. Only the bucket contributes the
        // object-id high bit; the remaining key bits are legitimate and must survive.
        Assert.Equal(0xF0000035u, CreatureObjectTableRule.FullObjectId(0x1035, 0x70000035u));
        Assert.Equal(0x70000035u, CreatureObjectTableRule.FullObjectId(0x035, 0xF0000035u));
    }

    [Fact]
    public void ChainWalkStopsAtTheVerifiedBoundAndAtNull()
    {
        Assert.True(CreatureObjectTableRule.MayWalk(0, (nint)0x1000));
        Assert.True(CreatureObjectTableRule.MayWalk(CreatureObjectTableRule.MaxChainWalk - 1, (nint)0x1000));
        Assert.False(CreatureObjectTableRule.MayWalk(CreatureObjectTableRule.MaxChainWalk, (nint)0x1000));
        Assert.False(CreatureObjectTableRule.MayWalk(0, 0));
    }

    [Fact]
    public void CollectionCapacityIsAHardUpperBound()
    {
        Assert.True(CreatureObjectTableRule.HasRoom(0, 1));
        Assert.False(CreatureObjectTableRule.HasRoom(1, 1));
        Assert.False(CreatureObjectTableRule.HasRoom(0, 0));
        Assert.False(CreatureObjectTableRule.HasRoom(-1, 1));
    }
}

using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class NativeProbeContextRuleTests
{
    [Fact]
    public void SavedFlagsAreNotReadAsCallerAndShortOverflowContextsRefuse()
    {
        byte[] context = new byte[36];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(context.AsSpan(12), 0x1000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(context.AsSpan(32), 0x246);
        Assert.True(NativeProbeContextRule.TryReadOriginalStack(context, out uint original));
        Assert.Equal(0x1004u, original);
        Assert.False(NativeProbeContextRule.TryReadOriginalStack(context.AsSpan(0, 35), out _));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(context.AsSpan(12), uint.MaxValue - 3);
        Assert.False(NativeProbeContextRule.TryReadOriginalStack(context, out _));
    }
    [Fact]
    public void ContextSavesFlagsBeforeAllRegistersAndRestoresBothAfterCdeclCleanup()
    {
        Assert.Equal(new byte[] { 0x9C, 0x60, 0x8D, 0x04, 0x24, 0x50 }, NativeProbeContextRule.SaveAndPushContext.ToArray());
        Assert.Equal(new byte[] { 0x83, 0xC4, 0x04, 0x61, 0x9D }, NativeProbeContextRule.RestoreContext.ToArray());
        Assert.Equal(12, NativeProbeContextRule.SavedEsp);
        Assert.Equal(24, NativeProbeContextRule.SavedEcx);
        Assert.Equal(4, NativeProbeContextRule.OriginalEspAdjustment);
    }
}

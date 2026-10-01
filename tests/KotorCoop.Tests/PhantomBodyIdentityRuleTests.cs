using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PhantomBodyIdentityRuleTests
{
    [Fact]
    public void FormerBodyAfterTransitionCannotBeDrivenOrDestroyedByReusedId()
    {
        Assert.True(PhantomBodyIdentityRule.IsCurrent(0x1000, 0x1000, true, true, 0x7470C8, 0xD4, 0xD4, 0x3000));
        // Same type and ID do not confer ownership of the replacement body.
        Assert.False(PhantomBodyIdentityRule.IsCurrent(0x1000, 0x2000, true, true, 0x7470C8, 0xD4, 0xD4, 0x3000));
        Assert.False(PhantomBodyIdentityRule.IsCurrent(0x1000, 0, true, true, 0x7470C8, 0xD4, 0xD4, 0x3000));
        // A fresh explicit spawn can own the replacement only by recording its pointer.
        Assert.True(PhantomBodyIdentityRule.IsCurrent(0x2000, 0x2000, true, true, 0x7470C8, 0xE0, 0xE0, 0x3000));
    }

    [Theory]
    [InlineData(false, true, 0x7470C8u, 0xD4u, 0x3000)]
    [InlineData(true, false, 0x7470C8u, 0xD4u, 0x3000)] // Header lives, faulting +A60 does not.
    [InlineData(true, true, 0x7494D0u, 0xD4u, 0x3000)]
    [InlineData(true, true, 0x7470C8u, 0xD5u, 0x3000)]
    [InlineData(true, true, 0x7470C8u, 0xD4u, 0x1000)]
    public void UnreadableOrDifferentOwnerNeverPasses(bool header, bool appearance, uint vtable, uint id, int player) =>
        Assert.False(PhantomBodyIdentityRule.IsCurrent(0x1000, 0x1000, header, appearance, vtable, id, 0xD4, player));
}

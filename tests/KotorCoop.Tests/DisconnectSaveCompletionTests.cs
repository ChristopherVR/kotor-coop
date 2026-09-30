using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class DisconnectSaveCompletionTests
{
    [Theory]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    public void ReadablePartialOrWrongWorldNeverCompletes(bool ack, bool completed, bool current, bool ready)
    {
        var rule = new DisconnectSaveCompletionRule();
        Assert.False(rule.Observe(ack, completed, current, ready, "hash", 100));
        Assert.False(rule.Observe(ack, completed, current, ready, "hash", 10_000));
    }

    [Fact]
    public void NativeCompletionMustPrecedeAFullStableWindow()
    {
        var rule = new DisconnectSaveCompletionRule();
        Assert.False(rule.Observe(true, false, true, true, "hash", 100));
        Assert.False(rule.Observe(true, true, true, true, "hash", 1000));
        Assert.False(rule.Observe(true, true, true, true, "hash", 1749));
        Assert.True(rule.Observe(true, true, true, true, "hash", 1750));
    }

    [Fact]
    public void AnyOfFourOutputsChangingRestartsWindow()
    {
        var rule = new DisconnectSaveCompletionRule();
        Assert.False(rule.Observe(true, true, true, true, "first-four-files", 100));
        Assert.False(rule.Observe(true, true, true, true, "changed-globalvars", 800));
        Assert.False(rule.Observe(true, true, true, true, "changed-globalvars", 1549));
        Assert.True(rule.Observe(true, true, true, true, "changed-globalvars", 1550));
    }

    [Fact]
    public void ReadLockOrWorldChangeClearsPreviouslyStableProof()
    {
        var rule = new DisconnectSaveCompletionRule();
        Assert.False(rule.Observe(true, true, true, true, "hash", 100));
        Assert.False(rule.Observe(true, true, true, false, "hash", 800));
        Assert.False(rule.Observe(true, true, true, true, "hash", 900));
        Assert.False(rule.Observe(true, true, false, true, "hash", 1700));
        Assert.False(rule.Observe(true, true, true, true, "hash", 1800));
    }
}

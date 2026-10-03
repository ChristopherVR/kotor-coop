using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class DialogueMenuStartTests
{
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, false)]
    public void DefersNewStartsOnlyDuringCoopMenus(bool coop, bool menu, bool conversation, bool expected)
        => Assert.Equal(expected, DialogueMenuStartRule.Defer(coop, menu, conversation));

    [Fact]
    public void ClosingMenuLetsTheSameQueuedStartResume()
    {
        Assert.True(DialogueMenuStartRule.Defer(true, true, false));
        Assert.False(DialogueMenuStartRule.Defer(true, false, false));
    }
}

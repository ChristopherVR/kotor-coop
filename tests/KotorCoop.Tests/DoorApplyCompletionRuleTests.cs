using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class DoorApplyCompletionRuleTests
{
    [Theory]
    [InlineData(1, 0, 0, false)]
    [InlineData(2, 0, 0, false)]
    [InlineData(1, 0, 1, false)]
    [InlineData(1, 1, 1, true)]
    [InlineData(2, 2, 2, true)]
    [InlineData(0, 1, 0, false)]
    [InlineData(0, 0, 0, true)]
    [InlineData(1, 3, 3, false)]
    public void CompletionRequiresTheRequestedStateNotMerelyEqualOldBytes(byte requested, byte current, byte pending, bool complete) =>
        Assert.Equal(complete, DoorApplyCompletionRule.IsComplete(requested, current, pending));
}

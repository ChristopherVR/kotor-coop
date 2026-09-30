using KotorCoop.Plugin;
using Xunit;
namespace KotorCoop.Tests;

public sealed class ContainerUseAttemptTests
{
    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(4u, 5u)]
    [InlineData(0u, uint.MaxValue)]
    public void CloseBeforeFirstOpenPollCannotBeCompletedByStaleCanonicalOpen(uint seen, uint attempt)
    {
        seen = ContainerUseAttemptRule.ConsumeClosedAttempt(seen, 0xC6, attempt, 0xC6, attempt);
        Assert.False(ContainerUseAttemptRule.IsUnseen(attempt, seen));
        uint reopened = unchecked(attempt + 1);
        if (reopened == 0) reopened = 1;
        Assert.True(ContainerUseAttemptRule.IsUnseen(reopened, seen));
    }

    [Fact]
    public void ClosingOlderContainerDoesNotConsumeNewAttemptAtAnotherContainer()
    {
        uint seen = ContainerUseAttemptRule.ConsumeClosedAttempt(4, 0xC7, 5, 0xC6, 5);
        Assert.Equal(4u, seen);
        Assert.True(ContainerUseAttemptRule.IsUnseen(5, seen));
    }

    [Fact]
    public void NewAttemptAfterSameContainerCloseStillOpens()
    {
        uint seen = ContainerUseAttemptRule.ConsumeClosedAttempt(4, 0xC6, 6, 0xC6, 5);
        Assert.Equal(4u, seen);
        Assert.True(ContainerUseAttemptRule.IsUnseen(6, seen));
        Assert.False(ContainerUseAttemptRule.IsUnseen(0, seen));
    }
}

using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The thresholds behind the <c>autofollow</c> console verb.</summary>
public class AutoFollowRuleTests
{
    private static readonly TimeSpan Long = TimeSpan.FromMinutes(1);

    private static PlayerState At(float x, float y, string module = "end_m01aa", uint flags = 0) =>
        new(new Vector3f(x, y, 0f), 0f, module, 10, 0) { Flags = flags };

    [Fact]
    public void CloseEnoughStandsStill()
    {
        AutoFollowDecision d = AutoFollowRule.Decide(At(0, 0), At(2, 0), null, Long, Long);
        Assert.Equal(AutoFollowAction.None, d.Action);
    }

    [Fact]
    public void WalksToATrailPointShortOfTheLeader()
    {
        AutoFollowDecision d = AutoFollowRule.Decide(At(0, 0), At(10, 0), null, Long, Long);
        Assert.Equal(AutoFollowAction.Walk, d.Action);
        Assert.Equal(10f - AutoFollowRule.TrailGap, d.Destination.X, 3);
        Assert.Equal(0f, d.Destination.Y, 3);
    }

    [Fact]
    public void AFreshOrderToTheSameSpotIsNotRepeated()
    {
        Vector3f last = new(10f - AutoFollowRule.TrailGap, 0f, 0f);
        AutoFollowDecision d = AutoFollowRule.Decide(At(0, 0), At(10, 0), last, TimeSpan.FromMilliseconds(500), Long);
        Assert.Equal(AutoFollowAction.None, d.Action);
    }

    [Fact]
    public void AStaleOrderIsReissued()
    {
        Vector3f last = new(10f - AutoFollowRule.TrailGap, 0f, 0f);
        AutoFollowDecision d = AutoFollowRule.Decide(At(0, 0), At(10, 0), last, AutoFollowRule.StaleOrder, Long);
        Assert.Equal(AutoFollowAction.Walk, d.Action);
    }

    [Fact]
    public void ALeaderWhoMovedIsRetargeted()
    {
        Vector3f last = new(5f, 0f, 0f);
        AutoFollowDecision d = AutoFollowRule.Decide(At(0, 0), At(10, 0), last, TimeSpan.Zero, Long);
        Assert.Equal(AutoFollowAction.Walk, d.Action);
    }

    [Fact]
    public void FarBehindSnapsButNotTwiceInARow()
    {
        Assert.Equal(AutoFollowAction.Snap, AutoFollowRule.Decide(At(0, 0), At(50, 0), null, Long, Long).Action);
        Assert.Equal(AutoFollowAction.Walk, AutoFollowRule.Decide(At(0, 0), At(50, 0), null, Long, TimeSpan.FromSeconds(1)).Action);
    }

    [Fact]
    public void ADifferentModuleHoldsInsteadOfWarping()
    {
        AutoFollowDecision d = AutoFollowRule.Decide(At(0, 0), At(10, 0, "end_m01ab"), null, Long, Long);
        Assert.Equal(AutoFollowAction.Hold, d.Action);
        Assert.Contains("end_m01ab", d.Reason);
    }

    [Fact]
    public void AConversationHolds()
    {
        AutoFollowDecision d = AutoFollowRule.Decide(At(0, 0), At(10, 0, flags: PlayerState.FlagInDialogue), null, Long, Long);
        Assert.Equal(AutoFollowAction.Hold, d.Action);
    }

    [Fact]
    public void ParsesEveryForm()
    {
        Assert.Equal(CoopCommandKind.AutoFollow, ConsoleCommand.Parse("autofollow").Kind);
        Assert.Null(ConsoleCommand.Parse("autofollow").Address);
        Assert.Equal("off", ConsoleCommand.Parse("autofollow off").Address);
        Assert.Equal("#1", ConsoleCommand.Parse("autofollow #1").Address);
    }
}

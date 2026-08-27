using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure threshold behind the away flag (see PlayerState.FlagAway and
/// docs/research/disconnect-reconnect.md): a sustained run of failed local reads — not a
/// single-tick blip — is what CoopAgent.PumpOnce treats as "the player quit to the main menu",
/// scaled to keep a roughly one-second wall-clock window whatever the poll rate.
/// </summary>
public class AwayRuleTests
{
    [Fact]
    public void OneFailedReadIsNotAwayAtTheDefaultPollRate()
    {
        Assert.False(AwayRule.IsAway(consecutiveReadFailures: 1, pollRate: 30));
    }

    [Fact]
    public void ReachingTheDefaultThresholdIsAway()
    {
        Assert.True(AwayRule.IsAway(consecutiveReadFailures: 30, pollRate: 30));
    }

    [Fact]
    public void OneShortOfTheThresholdIsNotYetAway()
    {
        Assert.False(AwayRule.IsAway(consecutiveReadFailures: 29, pollRate: 30));
    }

    [Fact]
    public void StaysAwayWellPastTheThreshold()
    {
        // A player who has been at the main menu for minutes must not somehow un-latch.
        Assert.True(AwayRule.IsAway(consecutiveReadFailures: 9000, pollRate: 30));
    }

    [Fact]
    public void TheThresholdScalesWithPollRateToKeepAOneSecondWindow()
    {
        // Double the poll rate, double the tick count needed for the same ~1 s of real time —
        // tying the threshold to a bare tick count would flip "away" twice as fast at 60 Hz as
        // at 30 Hz, which is the exact tick-vs-wall-clock bug CoopAgent's own heavy-pass
        // cadence remarks warn against elsewhere.
        Assert.Equal(60, AwayRule.Threshold(pollRate: 60));
        Assert.Equal(15, AwayRule.Threshold(pollRate: 15));
        Assert.Equal(30, AwayRule.Threshold(pollRate: 30));
    }

    [Fact]
    public void AZeroOrNegativePollRateFallsBackToTheDefaultThresholdRatherThanDividingByZero()
    {
        Assert.Equal(AwayRule.DefaultThreshold, AwayRule.Threshold(pollRate: 0));
        Assert.Equal(AwayRule.DefaultThreshold, AwayRule.Threshold(pollRate: -5));
    }

    [Fact]
    public void APathologicallyLowPollRateStillRequiresAtLeastOneFailedRead()
    {
        Assert.True(AwayRule.Threshold(pollRate: 1) >= 1);
        Assert.False(AwayRule.IsAway(consecutiveReadFailures: 0, pollRate: 1));
    }
}

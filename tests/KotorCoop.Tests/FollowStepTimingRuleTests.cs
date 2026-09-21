using Xunit;
namespace KotorCoop.Tests;

public sealed class FollowStepTimingRuleTests
{
    [Fact]
    public void CarriesSubmillisecondIntervalsInsteadOfRoundingEachFrame()
    {
        double remainder = 0;
        int emitted = 0;
        for (int i = 0; i < 1000; i++)
        {
            emitted += KotorCoop.Plugin.FollowStepTimingRule.Consume(ref remainder, 0.0002);
        }

        // 1000 x 0.2 ms is a 200 ms budget. The integer calls may differ by one
        // because of floating point carry, but no time may disappear.
        Assert.InRange(emitted, 199, 200);
        Assert.InRange(emitted + remainder, 199.99, 200.01);

        remainder = 0;
        Assert.Equal(0, KotorCoop.Plugin.FollowStepTimingRule.Consume(ref remainder, 0.0002));
        Assert.Equal(0, KotorCoop.Plugin.FollowStepTimingRule.Consume(ref remainder, 0.0002));
        Assert.InRange(remainder, 0.39, 0.41);
    }

    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    [InlineData(330)]
    [InlineData(1000)]
    public void OneSecondBudgetTracksWallTimeAcrossRenderRates(int frequency)
    {
        double remainder = 0;
        int emitted = 0;
        double interval = 1.0 / frequency;
        for (int i = 0; i < frequency; i++)
        {
            emitted += KotorCoop.Plugin.FollowStepTimingRule.Consume(ref remainder, interval);
        }

        Assert.InRange(emitted + remainder, 999.0, 1001.0);
    }

    [Fact]
    public void InvalidAndLongGapsResetCarry()
    {
        double remainder = 0.75;
        Assert.Equal(0, KotorCoop.Plugin.FollowStepTimingRule.Consume(ref remainder, 0));
        Assert.Equal(0, remainder);

        remainder = 0.75;
        Assert.Equal(KotorCoop.Plugin.FollowStepTimingRule.MaxStepMilliseconds,
            KotorCoop.Plugin.FollowStepTimingRule.Consume(ref remainder, 0.5));
        Assert.Equal(0, remainder);
    }
}

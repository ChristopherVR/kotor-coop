using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The 2026-09-19 live flap: the host read 2 players, then 1 for ~5 s, then 2 again.</summary>
public sealed class PlayerCountSettleRuleTests
{
    [Fact]
    public void ARiseIsAdoptedImmediately()
    {
        PlayerCountSettleRule rule = new();
        Assert.Equal(1, rule.Observe(1, 0));
        Assert.Equal(2, rule.Observe(2, 100));
        Assert.Equal(3, rule.Observe(3, 200));
    }

    [Fact]
    public void ATransientDipIsIgnored()
    {
        PlayerCountSettleRule rule = new();
        rule.Observe(2, 0);

        // The flap measured live: 1 for about five seconds, then back to 2.
        Assert.Equal(2, rule.Observe(1, 5_000));
        Assert.Equal(2, rule.Observe(1, 9_000));
        Assert.Equal(2, rule.Observe(2, 10_000));

        // ...and the dip does not count toward a later one: a fresh dip starts its own clock.
        Assert.Equal(2, rule.Observe(1, 11_000));
        Assert.Equal(2, rule.Observe(1, 20_999));
    }

    [Fact]
    public void ASustainedFallIsAdoptedAfterTheSettleTime()
    {
        PlayerCountSettleRule rule = new();
        rule.Observe(3, 0);

        Assert.Equal(3, rule.Observe(2, 1_000));
        Assert.Equal(3, rule.Observe(2, 1_000 + PlayerCountSettleRule.FallSettleMs - 1));
        Assert.Equal(2, rule.Observe(2, 1_000 + PlayerCountSettleRule.FallSettleMs));
    }

    [Fact]
    public void ADifferentLowerValueRestartsTheClock()
    {
        PlayerCountSettleRule rule = new();
        rule.Observe(4, 0);

        Assert.Equal(4, rule.Observe(3, 0));
        Assert.Equal(4, rule.Observe(2, 9_000)); // a new candidate, so 3's 9 s do not carry over
        Assert.Equal(4, rule.Observe(2, 18_999));
        Assert.Equal(2, rule.Observe(2, 19_000));
    }

    [Fact]
    public void NonsenseCountsClampToOneAndResetStartsAlone()
    {
        PlayerCountSettleRule rule = new();
        Assert.Equal(1, rule.Observe(-5, 0));
        rule.Observe(3, 10);
        rule.Reset();
        Assert.Equal(1, rule.Settled);
    }
}

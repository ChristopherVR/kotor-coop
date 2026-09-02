using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The wrap guard in front of the gold ledger (<see cref="GoldReadingRule"/>). The reading it
/// pins is real: live 2026-09-03 01:06 the host's party table read 4294967286 (-10 wrapped)
/// after a store click subtracted 110 from a stale cached 100, and the ledger carried it.
/// </summary>
public class GoldReadingRuleTests
{
    [Fact]
    public void TheLiveWrapIsRecognisedAndReadsAsBroke()
    {
        Assert.True(GoldReadingRule.IsWrapped(4294967286u));
        Assert.Equal(0u, GoldReadingRule.Sanitise(4294967286u));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(100u)]
    [InlineData(GoldReadingRule.Max)]
    public void AnyBalanceTheEngineCanHoldPassesThrough(uint raw)
    {
        Assert.False(GoldReadingRule.IsWrapped(raw));
        Assert.Equal(raw, GoldReadingRule.Sanitise(raw));
    }

    [Fact]
    public void OneAboveTheEngineCeilingIsAWrapNotAFortune()
    {
        Assert.True(GoldReadingRule.IsWrapped(GoldReadingRule.Max + 1));
        Assert.Equal(0u, GoldReadingRule.Sanitise(uint.MaxValue));
    }
}

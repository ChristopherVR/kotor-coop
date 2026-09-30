using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LocalFightBodyRuleTests
{
    [Fact]
    public void ReadableSameTypeSameIdCacheMustStillBelongToCurrentObjectArray()
    {
        Assert.False(LocalFightBodyRule.IsCurrentCachedBody(0x1000, 0x2000, true, 0x7470C8, 0x156, 0x156));
        Assert.False(LocalFightBodyRule.IsCurrentCachedBody(0x1000, 0, true, 0x7470C8, 0x156, 0x156));
        Assert.False(LocalFightBodyRule.IsCurrentCachedBody(0x1000, 0x1000, false, 0x7470C8, 0x156, 0x156));
        Assert.True(LocalFightBodyRule.IsCurrentCachedBody(0x2000, 0x2000, true, 0x7470C8, 0x156, 0x156));
    }
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(false, true, 1)]
    [InlineData(true, false, 2)]
    [InlineData(true, true, 2)]
    public void UnresolvedNewbornWaitsButValidCreatureRuns(bool resolved, bool grace, int expected) =>
        Assert.Equal((LocalFightBodyVerdict)expected, LocalFightBodyRule.Decide(resolved, 0x7470C8, 0x156, 0x156, false, false, grace));

    [Theory]
    [InlineData(0x747180u, 0x156u, 0x156u, false, false)] // waypoint, reused same id
    [InlineData(0x7470C8u, 0x157u, 0x156u, false, false)] // mismatched current id
    [InlineData(0x7470C8u, 0x156u, 0x156u, true, false)]
    [InlineData(0x7470C8u, 0x156u, 0x156u, false, true)]
    [InlineData(0u, 0u, 0x156u, false, false)] // resolved but unreadable
    [InlineData(0x7470C8u, 0x7F000000u, 0x7F000000u, false, false)] // rejected map
    public void PositiveMismatchReleasesExclusionEvenWithinBirthGrace(uint vtable, uint actual, uint expected,
        bool pc, bool phantom) => Assert.Equal(LocalFightBodyVerdict.Release,
        LocalFightBodyRule.Decide(true, vtable, actual, expected, pc, phantom, true));

    [Fact]
    public void ReleasedStaleBodyDoesNotBlockFreshMappedCreature()
    {
        Assert.Equal(LocalFightBodyVerdict.Release,
            LocalFightBodyRule.Decide(true, 0x747180, 0x156, 0x156, false, false, false));
        Assert.Equal(LocalFightBodyVerdict.UseCreature,
            LocalFightBodyRule.Decide(true, 0x7470C8, 0x80000170, 0x170, false, false, false));
    }
}

using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ContainerLootExemptionRuleTests
{
    [Theory]
    [InlineData("lev_m40aa", "lev40_ptylocker", true)]
    [InlineData("LEV_M40AA", "LEV40_PTYLOCKER", true)]
    [InlineData("lev_m40aa", "lev40_otherchest", false)]
    [InlineData("tar_m02aa", "lev40_ptylocker", false)]
    [InlineData("lev_m40aa", "", false)]
    [InlineData("", "lev40_ptylocker", false)]
    public void OnlyTheLeviathanPartyLockerIsExempt(string module, string tag, bool exempt) =>
        Assert.Equal(exempt, ContainerLootExemptionRule.IsExempt(module, tag));
}

using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public sealed class DialogueNodeActionGateRuleTests
{
    [Theory]
    [InlineData("lev_m40aa", "k_plev_transform")]
    [InlineData("lev_m40aa", "k_plev_transfrm1")]
    [InlineData("LEV_M40AA", "K_PLEV_TRANSFORM")]
    [InlineData("LEV_M40AA", "K_PLEV_TRANSFRM1")]
    public void ClientInLeviathanSuppressesOnlyTheTwoExactActionResrefs(string module, string script)
    {
        Assert.True(DialogueNodeActionGateRule.ShouldSuppress(
            isClient: true,
            System.Text.Encoding.ASCII.GetBytes(module),
            System.Text.Encoding.ASCII.GetBytes(script)));
    }

    [Theory]
    [InlineData(false, "lev_m40aa", "k_plev_transform")]
    [InlineData(true, "unk_m44ac", "k_plev_transform")]
    [InlineData(true, "lev_m40ab", "k_plev_transform")]
    [InlineData(true, "lev_m40aa", "k_plev_transfrm")]
    [InlineData(true, "lev_m40aa", "k_plev_transformx")]
    [InlineData(true, "lev_m40aa", "k_plev_freeparty")]
    [InlineData(true, "", "")]
    public void UnknownRoleModuleOrResrefFailsOpen(bool isClient, string module, string script)
    {
        Assert.False(DialogueNodeActionGateRule.ShouldSuppress(
            isClient,
            System.Text.Encoding.ASCII.GetBytes(module),
            System.Text.Encoding.ASCII.GetBytes(script)));
    }
}

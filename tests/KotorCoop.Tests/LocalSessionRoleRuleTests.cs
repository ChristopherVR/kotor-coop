using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class LocalSessionRoleRuleTests
{
    [Fact]
    public void KnownClientIsValidWithoutInferringHostFromNoHostRoster()
    {
        uint word = LocalSessionRoleRule.Encode(sessionActive: true, isHost: false);

        Assert.True(LocalSessionRoleRule.TryDecode(word, out bool isHost));
        Assert.False(isHost);
        Assert.False(LocalSessionRoleRule.IsPositiveHost(word));
    }

    [Fact]
    public void KnownHostHasExplicitPositiveProofAndTeardownClearsIt()
    {
        uint host = LocalSessionRoleRule.Encode(sessionActive: true, isHost: true);
        Assert.True(LocalSessionRoleRule.IsPositiveHost(host));

        uint cleared = LocalSessionRoleRule.Encode(sessionActive: false, isHost: true);
        Assert.False(LocalSessionRoleRule.TryDecode(cleared, out bool isHost));
        Assert.True(isHost is false);
    }
}

using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class WipeTeardownRuleTests
{
    [Fact]
    public void WitnessedGameOverWithUnchangedRequestCanRecover()
        => Assert.True(WipeTeardownRule.MaySuppressOwnLoad(true, null, 2, 2, false, false));

    [Theory]
    [InlineData(false, 2u, 2u, false, false)]
    [InlineData(true, 2u, 3u, false, false)]
    [InlineData(true, null, 2u, false, false)]
    [InlineData(true, 2u, null, false, false)]
    [InlineData(true, 2u, 2u, true, false)]
    [InlineData(true, 2u, 2u, false, true)]
    public void MissingEvidenceAndActualOwnLoadsKeepTheirProtection(bool wiped,
        uint? previous, uint? current, bool command, bool protectedLoad)
        => Assert.False(WipeTeardownRule.MaySuppressOwnLoad(wiped, null, previous, current, command, protectedLoad));

    [Fact]
    public void ANewLocalWorldCannotBeSwallowedAsGameOver()
    {
        PlayerState standing = new(Vector3f.Zero, 0f, "end_m01aa", 12, 0, MaxHealth: 12);
        Assert.False(WipeTeardownRule.MaySuppressOwnLoad(true, standing, 2, 2, false, false));
    }
}

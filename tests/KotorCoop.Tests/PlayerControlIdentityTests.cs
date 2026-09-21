using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public sealed class PlayerControlIdentityTests
{
    [Theory]
    [InlineData(0x80000134u, 0x7FFFFFFEu, 0xFFFFFFFEu)]
    [InlineData(0xFFFFFFFFu, 0x7FFFFFFEu, 0xFFFFFFFEu)]
    [InlineData(0x80000134u, 0x7FFFFFFFu, 0xFFFFFFFFu)]
    public void RestoresTheVerifiedPlayerRatherThanAssumingAnId(uint controlled, uint player, uint expected)
    {
        Assert.True(PlayerControlIdentity.TryGetCorrection(controlled, player, out uint corrected));
        Assert.Equal(expected, corrected);
    }

    [Theory]
    [InlineData(0xFFFFFFFEu, 0x7FFFFFFEu)]
    [InlineData(0xFFFFFFFFu, 0x7FFFFFFFu)]
    [InlineData(0x134u, 0x7FFFFFFEu)]
    [InlineData(0xFFFFFFFFu, 0u)]
    [InlineData(0xFFFFFFFFu, 0x7F000000u)]
    [InlineData(0xFFFFFFFFu, 0xFFFFFFFFu)]
    public void LeavesMatchingUnflaggedOrUnverifiedControlAlone(uint controlled, uint player) =>
        Assert.False(PlayerControlIdentity.TryGetCorrection(controlled, player, out _));
}

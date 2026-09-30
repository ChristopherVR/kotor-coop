using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public sealed class SavedCharacterHealthRuleTests
{
    [Theory]
    [InlineData(0, 0, 12)] // Actual full-health PC Co-op archive, native HP 12.
    [InlineData(-10, 0, 12)] // Actual post-release archive, native HP 2.
    [InlineData(30, 100, 100)] // Ordinary creature without a derived-maximum bonus.
    [InlineData(5, 10, 20)]
    public void SavedOffsetCanRepresentLivingCharacter(int current, int stored, int derived) =>
        Assert.True(SavedCharacterHealthRule.IsLiving(current, stored, derived));

    [Theory]
    [InlineData(-12, 0, 12)]
    [InlineData(-13, 0, 12)]
    [InlineData(1, 0, 12)] // Above derived maximum is unproven.
    [InlineData(12, 0, 0)]
    [InlineData(12, -1, 12)]
    [InlineData(int.MaxValue, 0, 12)]
    [InlineData(int.MinValue, 0, 12)]
    [InlineData(0, int.MaxValue, 12)]
    [InlineData(0, 0, int.MaxValue)]
    public void DeadOrMalformedOffsetCannotAuthorizeRecovery(int current, int stored, int derived) =>
        Assert.False(SavedCharacterHealthRule.IsLiving(current, stored, derived));
}

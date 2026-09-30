using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

public class StalledRequestRuleTests
{
    [Theory]
    [InlineData(0, false)]
    [InlineData(45_000, false)]
    [InlineData(45_001, true)]
    [InlineData(120_000, true)]
    public void AutomaticRequestsFromBeforeABlockedNewGameAreRefused(long gapMs, bool refused)
        => Assert.Equal(refused, StalledRequestRule.ShouldRefuse(gapMs, manualIntent: false));

    [Theory]
    [InlineData(45_001)]
    [InlineData(120_000)]
    public void ExplicitHumanCommandsSurviveABlockedThread(long gapMs)
        => Assert.False(StalledRequestRule.ShouldRefuse(gapMs, manualIntent: true));

    [Fact]
    public void ManualIntentFieldsFitInTheTailWithoutOverlappingDialogueNames()
    {
        Assert.True(SharedBlock.OffsetTravelManualIntent >= SharedBlock.OffsetRemoteDialogueResref + SharedBlock.DialogueResrefLength);
        Assert.Equal(SharedBlock.OffsetTravelManualIntent + sizeof(uint), SharedBlock.OffsetMenuLoadManualIntent);
        Assert.True(SharedBlock.OffsetMenuLoadManualIntent + sizeof(uint) <= SharedBlock.TotalSize);
    }

    [Fact]
    public void ALoadAlreadyInItsNativeCompletionPathIsNotInterrupted()
        => Assert.False(StalledRequestRule.ShouldRefuse(120_000, manualIntent: false, alreadyStarted: true));
}

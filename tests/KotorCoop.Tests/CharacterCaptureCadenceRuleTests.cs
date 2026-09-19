using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class CharacterCaptureCadenceRuleTests
{
    private const long Minute = 60_000;

    private static PlayerState State(uint flags = 0) => new(new Vector3f(0f, 0f, 0f), 0f, "end_m01aa", 100, 0, Flags: flags);

    [Fact]
    public void DueOnlyOnceTheIntervalHasElapsed()
    {
        Assert.False(CharacterCaptureCadenceRule.IsDue(10, 9 * Minute, 0, false, State()));
        Assert.True(CharacterCaptureCadenceRule.IsDue(10, 10 * Minute, 0, false, State()));
    }

    [Fact]
    public void ZeroIntervalIsOff()
    {
        Assert.False(CharacterCaptureCadenceRule.IsDue(0, 999 * Minute, 0, false, State()));
    }

    [Fact]
    public void NotWhileARequestIsInFlightOrWithNoLocalState()
    {
        Assert.False(CharacterCaptureCadenceRule.IsDue(10, 20 * Minute, 0, true, State()));
        Assert.False(CharacterCaptureCadenceRule.IsDue(10, 20 * Minute, 0, false, null));
    }

    [Theory]
    [InlineData(PlayerState.FlagInCombat)]
    [InlineData(PlayerState.FlagInDialogue)]
    [InlineData(PlayerState.FlagInMinigame)]
    public void NotWhileFightingTalkingOrInAMinigame(uint flag)
    {
        Assert.False(CharacterCaptureCadenceRule.IsDue(10, 20 * Minute, 0, false, State(flag)));
    }

    [Fact]
    public void ASaveLandedWhenTheNewestDirectoryChangesOrIsRewritten()
    {
        DateTime t = new(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

        Assert.False(CharacterCaptureCadenceRule.SaveLanded("a", t, "a", t));
        Assert.True(CharacterCaptureCadenceRule.SaveLanded("a", t, "a", t.AddSeconds(2)));
        Assert.True(CharacterCaptureCadenceRule.SaveLanded("a", t, "b", t));
        Assert.False(CharacterCaptureCadenceRule.SaveLanded("a", t, null, t));
    }

    [Fact]
    public void TheWaitIsBoundedAtFiveSeconds()
    {
        Assert.False(CharacterCaptureCadenceRule.WaitExpired(1000, 5999));
        Assert.True(CharacterCaptureCadenceRule.WaitExpired(1000, 6000));
    }
}

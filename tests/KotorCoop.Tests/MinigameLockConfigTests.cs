using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The coop.ini side of the minigame peer lock (docs/research/minigames.md §24). Separate from
/// <see cref="MinigameLockRuleTests"/> on purpose: the rule is right and the key still silently
/// does nothing if the ON-by-default idiom is written the wrong way round — the exact drift
/// <c>docs/CONFIG.md</c>'s own footnote ³ records happening twice already.
/// </summary>
public class MinigameLockConfigTests
{
    private static CoopConfig Parse(params string[] lines)
    {
        string path = Path.Combine(Path.GetTempPath(), $"coop-{Guid.NewGuid():N}.ini");
        File.WriteAllLines(path, ["[coop]", .. lines]);

        try
        {
            return CoopConfig.TryLoad(path)!;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheLockIsOnWhenTheKeyIsAbsentEntirely()
    {
        // The ON-by-default idiom: an EXISTING coop.ini written before this feature landed
        // must get the new behaviour, not silently opt out of it.
        CoopConfig config = Parse("mode = host");

        Assert.True(config.LockPeersDuringMinigame);
        Assert.Equal(600, config.MinigameLockTimeoutSeconds);
        Assert.Empty(config.ExtraMinigameModules);
    }

    [Fact]
    public void TheLockCanBeTurnedOff()
    {
        Assert.False(Parse("minigame_lock_peers = 0").LockPeersDuringMinigame);
    }

    [Fact]
    public void AnExplicitOneIsStillOn()
    {
        Assert.True(Parse("minigame_lock_peers = 1").LockPeersDuringMinigame);
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("120", 120)]
    [InlineData("999999", 7200)]   // clamped
    [InlineData("-5", 0)]          // clamped
    [InlineData("not a number", 600)]
    public void TheTimeoutIsParsedAndClamped(string value, int expected)
    {
        Assert.Equal(expected, Parse($"minigame_lock_timeout_s = {value}").MinigameLockTimeoutSeconds);
    }

    [Fact]
    public void ExtraModulesAreSplitAndTrimmed()
    {
        CoopConfig config = Parse("minigame_modules = mod_track01, mod_track02 ,");

        Assert.Equal(["mod_track01", "mod_track02"], config.ExtraMinigameModules);
    }
}

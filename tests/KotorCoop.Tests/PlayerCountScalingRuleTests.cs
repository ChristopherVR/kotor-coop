using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Player-count scaling, agent side (docs/research/player-count-scaling.md): the parse /
/// clamp / curve rule, the per-session resolved state, the XP delta rule and the store
/// top-up rule with its once-per-playthrough marker.
/// </summary>
public class PlayerCountScalingRuleTests
{
    // ---- PlayerCountScalingRule --------------------------------------------------------

    [Theory]
    [InlineData(null, 100, 100)]
    [InlineData("", 100, 100)]
    [InlineData("   ", 0, 0)]
    [InlineData("auto", 100, 0)]
    [InlineData("AUTO", 100, 0)]
    [InlineData(" Auto ", 100, 0)]
    [InlineData("150", 100, 150)]
    [InlineData("150%", 100, 150)]
    [InlineData(" 150 % ", 100, 150)]
    [InlineData("50", 100, 50)]
    [InlineData("400", 100, 400)]
    [InlineData("10", 100, 50)]      // clamped up
    [InlineData("999", 100, 400)]    // clamped down
    [InlineData("-20", 100, 50)]
    [InlineData("lots", 100, 100)]   // junk -> fallback
    [InlineData("1.5", 0, 0)]        // no floats, ever -> fallback
    public void ParseSettingIsLenientAndClamps(string? text, int fallback, int expected)
    {
        Assert.Equal(expected, PlayerCountScalingRule.ParseSetting(text, fallback));
    }

    [Theory]
    [InlineData(1, 100, 100, 100, 100)]
    [InlineData(0, 100, 100, 100, 100)]
    [InlineData(-3, 100, 100, 100, 100)]
    [InlineData(2, 150, 115, 100, 150)]
    [InlineData(3, 200, 125, 100, 200)]
    [InlineData(4, 250, 135, 100, 250)]
    [InlineData(5, 250, 135, 100, 250)]
    [InlineData(12, 250, 135, 100, 250)]
    public void TheAutoCurveIsVanillaAloneAndSaturatesAtFour(int players, int health, int damage, int xp, int stock)
    {
        Assert.Equal(health, PlayerCountScalingRule.Curve(ScalingKind.EnemyHealth, players));
        Assert.Equal(damage, PlayerCountScalingRule.Curve(ScalingKind.EnemyDamage, players));
        Assert.Equal(xp, PlayerCountScalingRule.Curve(ScalingKind.XpRate, players));
        Assert.Equal(stock, PlayerCountScalingRule.Curve(ScalingKind.StoreStock, players));
    }

    [Fact]
    public void ResolveUsesTheCurveForAutoAndTheSettingOtherwise()
    {
        Assert.Equal(150, PlayerCountScalingRule.Resolve(PlayerCountScalingRule.Auto, ScalingKind.EnemyHealth, 2));
        Assert.Equal(300, PlayerCountScalingRule.Resolve(300, ScalingKind.EnemyHealth, 2));
        Assert.Equal(300, PlayerCountScalingRule.Resolve(300, ScalingKind.EnemyHealth, 1));

        // A fixed setting that somehow escaped the parse clamp is still clamped on resolve.
        Assert.Equal(400, PlayerCountScalingRule.Resolve(9000, ScalingKind.XpRate, 1));
        Assert.Equal(50, PlayerCountScalingRule.Resolve(1, ScalingKind.XpRate, 1));
    }

    [Theory]
    [InlineData(20, 150, 30)]
    [InlineData(1, 150, 2)]      // 1.5 rounds half up
    [InlineData(1, 115, 1)]      // 1.15 rounds down
    [InlineData(3, 115, 3)]      // 3.45 -> 3
    [InlineData(4, 115, 5)]      // 4.6 -> 5
    [InlineData(1, 50, 1)]       // never below 1 for a positive input
    [InlineData(0, 150, 0)]      // 0 stays 0
    [InlineData(-11, 150, -11)]  // a corpse stays a corpse
    [InlineData(7, 100, 7)]
    public void ScaleRoundsHalfUpAndNeverDropsAPositiveValueBelowOne(int value, int percent, int expected)
    {
        Assert.Equal(expected, PlayerCountScalingRule.Scale(value, percent));
    }

    [Fact]
    public void DescribeAndNormaliseRoundTripASetting()
    {
        Assert.Equal("auto", PlayerCountScalingRule.Describe(PlayerCountScalingRule.Auto));
        Assert.Equal("150", PlayerCountScalingRule.Describe(150));
        Assert.Equal("400", PlayerCountScalingRule.Describe(9999));
        Assert.Equal(PlayerCountScalingRule.Auto, PlayerCountScalingRule.NormaliseSetting(0));
        Assert.Equal(50, PlayerCountScalingRule.NormaliseSetting(-5));
        Assert.Equal(0, PlayerCountScalingRule.ParseSetting(PlayerCountScalingRule.Describe(0), 100));
        Assert.Equal(275, PlayerCountScalingRule.ParseSetting(PlayerCountScalingRule.Describe(275), 100));
    }

    // ---- PlayerCountScaling (resolved state) --------------------------------------------

    [Fact]
    public void AloneEverythingIsVanillaWhateverTheAutoSettingsSay()
    {
        PlayerCountScaling scaling = new(new PlayerCountScalingSettings(0, 0, 0, 0));

        Assert.Equal(100, scaling.EnemyHealthPercent);
        Assert.Equal(100, scaling.EnemyDamagePercent);
        Assert.Equal(100, scaling.XpRatePercent);
        Assert.Equal(100, scaling.StoreStockPercent);
        Assert.Equal(1, scaling.PlayerCount);
    }

    [Fact]
    public void ASecondPlayerMovesTheAutoAxesAndBumpsTheGeneration()
    {
        PlayerCountScaling scaling = new(new PlayerCountScalingSettings(0, 0, 0, 0));
        uint before = scaling.Generation;

        Assert.True(scaling.Update(SessionOverrides.None, 2));
        Assert.Equal(150, scaling.EnemyHealthPercent);
        Assert.Equal(115, scaling.EnemyDamagePercent);
        Assert.Equal(100, scaling.XpRatePercent);
        Assert.Equal(150, scaling.StoreStockPercent);
        Assert.NotEqual(before, scaling.Generation);

        // Same inputs again: nothing changed, nothing bumped.
        uint after = scaling.Generation;
        Assert.False(scaling.Update(SessionOverrides.None, 2));
        Assert.Equal(after, scaling.Generation);

        // The partner leaves: back to vanilla.
        Assert.True(scaling.Update(SessionOverrides.None, 1));
        Assert.Equal(100, scaling.EnemyHealthPercent);
    }

    [Fact]
    public void AnAdoptedHostSettingWinsOverTheLocalOne()
    {
        // Local coop.ini: fixed 300% health. The host said auto -> at two players, 150%.
        PlayerCountScaling scaling = new(new PlayerCountScalingSettings(300, 100, 100, 100));
        scaling.Update(SessionOverrides.None, 2);
        Assert.Equal(300, scaling.EnemyHealthPercent);

        scaling.Update(new SessionOverrides(EnemyHealth: PlayerCountScalingRule.Auto, XpRate: 200), 2);
        Assert.Equal(150, scaling.EnemyHealthPercent);
        Assert.Equal(200, scaling.XpRatePercent);
        Assert.Equal(100, scaling.EnemyDamagePercent); // no override, local 100
        Assert.Equal(PlayerCountScalingRule.Auto, scaling.Effective.EnemyHealth);
    }

    [Fact]
    public void TheShippedDefaultsAreAutoEverywhere()
    {
        PlayerCountScalingSettings d = PlayerCountScalingSettings.Default;
        Assert.Equal(PlayerCountScalingRule.Auto, d.EnemyHealth); // live-verified 2026-09-19
        Assert.Equal(PlayerCountScalingRule.Auto, d.EnemyDamage); // live-verified 2026-09-19: a hit 4 -> 5 at two players
        Assert.Equal(PlayerCountScalingRule.Auto, d.XpRate);
        Assert.Equal(PlayerCountScalingRule.Auto, d.StoreStock);

        // BehaviourDigest.Default carries the same four numbers (the three-reader rule).
        Assert.Equal(d.EnemyHealth, BehaviourDigest.Default.EnemyHealth);
        Assert.Equal(d.EnemyDamage, BehaviourDigest.Default.EnemyDamage);
        Assert.Equal(d.XpRate, BehaviourDigest.Default.XpRate);
        Assert.Equal(d.StoreStock, BehaviourDigest.Default.StoreStock);

        // ...and so does a CoopConfig with no scaling keys at all.
        CoopConfig config = new();
        Assert.Equal(d.EnemyHealth, config.EnemyHealth);
        Assert.Equal(d.EnemyDamage, config.EnemyDamage);
        Assert.Equal(d.XpRate, config.XpRate);
        Assert.Equal(d.StoreStock, config.StoreStock);
    }

    [Fact]
    public void ThePackedCombatWordCarriesHealthLowAndDamageHigh()
    {
        PlayerCountScaling scaling = new(new PlayerCountScalingSettings(0, 0, 0, 0));
        scaling.Update(SessionOverrides.None, 3);

        uint word = scaling.PackCombatWord();
        Assert.Equal(200u, word & 0xFFFF);
        Assert.Equal(125u, word >> 16);

        uint economy = scaling.PackEconomyWord();
        Assert.Equal(100u, economy & 0xFFFF);
        Assert.Equal(200u, economy >> 16);
    }

    [Fact]
    public void DescribeNamesTheCountAndTagsTheAutoAxes()
    {
        PlayerCountScaling scaling = new(new PlayerCountScalingSettings(0, 120, 0, 0));
        scaling.Update(SessionOverrides.None, 2);

        string text = scaling.Describe();
        Assert.StartsWith("2 player(s):", text);
        Assert.Contains("enemy health 150% (auto)", text);
        Assert.Contains("enemy damage 120%,", text);
        Assert.Contains("store stock 150% (auto)", text);
    }

    // ---- XpRateRule -------------------------------------------------------------------

    [Fact]
    public void TheFirstReadingOnlyBaselines()
    {
        XpRateRule rule = new();
        Assert.Equal(0, rule.Observe(5000, 200));
        Assert.Equal(5000, rule.LastObserved);
    }

    [Fact]
    public void ARiseIsScaledByTheRateAboveVanilla()
    {
        XpRateRule rule = new();
        rule.Observe(1000, 150);
        Assert.Equal(50, rule.Observe(1100, 150)); // +100 at 150% -> +50
        Assert.Equal(100, rule.LastRise);
    }

    [Fact]
    public void RoundingRemainderCarriesAcrossSmallGrants()
    {
        // Five 3-XP grants at 110%: 0.3 each, 1.5 total -> 1 bonus, not 0.
        XpRateRule rule = new();
        rule.Observe(0, 110);
        int total = 0;
        for (int i = 1; i <= 5; i++)
        {
            total += rule.Observe((uint)(3 * i), 110);
        }

        Assert.Equal(1, total);
    }

    [Fact]
    public void RebaselineKeepsOurOwnWriteFromBeingScaledAgain()
    {
        XpRateRule rule = new();
        rule.Observe(1000, 200);
        Assert.Equal(100, rule.Observe(1100, 200)); // engine granted 100, bonus 100 -> write 1200
        rule.Rebaseline(1200);

        // The next read is the value we wrote: no rise, no bonus.
        Assert.Equal(0, rule.Observe(1200, 200));

        // Without the rebaseline the 1100 -> 1200 step would have read as a fresh grant.
        XpRateRule naive = new();
        naive.Observe(1000, 200);
        naive.Observe(1100, 200);
        Assert.Equal(100, naive.Observe(1200, 200));
    }

    [Fact]
    public void AFallOrAVanillaRateProducesNothingAndABelowVanillaRateNeverTakesXpAway()
    {
        XpRateRule rule = new();
        rule.Observe(1000, 100);
        Assert.Equal(0, rule.Observe(1100, 100));
        Assert.Equal(0, rule.Observe(500, 150));   // loaded an older save: baseline again
        Assert.Equal(500, rule.LastObserved);
        Assert.Equal(0, rule.Observe(600, 50));    // below 100: never negative
        Assert.Equal(75, rule.Observe(650, 250));  // +50 at 250% -> +75
    }

    // ---- StoreStockScaleRule / StoreStockScaleLatch --------------------------------------

    [Theory]
    [InlineData(1, 150, 1)]    // 1 -> 2 (1.5 rounds up): +1
    [InlineData(1, 115, 0)]    // 1 -> 1: nothing
    [InlineData(4, 150, 2)]
    [InlineData(10, 250, 15)]
    [InlineData(5, 100, 0)]
    [InlineData(5, 50, 0)]     // never removes stock
    [InlineData(0, 300, 0)]
    [InlineData(-1, 300, 0)]
    public void ExtraStockIsTheRiseOnlyAndNeverNegative(int count, int percent, int expected)
    {
        Assert.Equal((ushort)expected, StoreStockScaleRule.ExtraStock(count, percent));
    }

    [Fact]
    public void ExtraStockCapsAtTheApplyChannelsCount()
    {
        Assert.Equal(ushort.MaxValue, StoreStockScaleRule.ExtraStock(1_000_000, 400));
    }

    [Fact]
    public void TheMarkerKeyIsModuleAndIdCaseInsensitiveAndTheNewGameModuleIsRecognised()
    {
        Assert.Equal("tar_m02aa/1C", StoreStockScaleRule.Key("TAR_M02AA", 0x1C));
        Assert.True(StoreStockScaleRule.IsNewGameModule("END_M01AA"));
        Assert.False(StoreStockScaleRule.IsNewGameModule("end_m01ab"));
        Assert.False(StoreStockScaleRule.IsNewGameModule(null));
    }

    [Fact]
    public void TheLatchSurvivesAProcessThroughItsFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kotorcoop-storescale-" + Guid.NewGuid().ToString("N"));
        try
        {
            StoreStockScaleLatch first = new(dir);
            Assert.True(first.Persistent || first.Count == 0);
            Assert.False(first.Contains("tar_m02aa/1C"));
            Assert.True(first.Mark("tar_m02aa/1C"));
            Assert.True(first.Persistent);
            Assert.True(File.Exists(Path.Combine(dir, StoreStockScaleLatch.FileName)));

            // A second "process" reads the same mark back.
            StoreStockScaleLatch second = new(dir);
            Assert.True(second.Contains("TAR_M02AA/1c"));
            Assert.True(second.Persistent);
            Assert.Equal(1, second.Count);

            // A new game forgets everything, on disk too.
            Assert.True(second.Reset());
            StoreStockScaleLatch third = new(dir);
            Assert.Equal(0, third.Count);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    [Fact]
    public void NoDirectoryMeansNotPersistentAndMarksStayInMemoryOnly()
    {
        StoreStockScaleLatch latch = new(null);
        Assert.False(latch.Contains("a/1"));
        Assert.False(latch.Persistent);
        Assert.NotNull(latch.LastFailure);
        Assert.False(latch.Mark("a/1"));
        Assert.True(latch.Contains("a/1"));
        Assert.False(latch.Persistent);
    }
}

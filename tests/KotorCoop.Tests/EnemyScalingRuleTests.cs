using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Player-count scaling, plugin side (docs/research/player-count-scaling.md): the shared-block
/// word the agent packs, the once-per-enemy hit-point arithmetic, the per-hit damage rewrite
/// and the per-epoch object latch. The engine writes themselves are NEEDS-LIVE; this is the
/// arithmetic they carry.
/// </summary>
public class EnemyScalingRuleTests
{
    [Fact]
    public void TheCombatWordUnpacksHealthLowAndDamageHigh()
    {
        uint word = 150u | (115u << 16);
        Assert.Equal(150, EnemyScalingRule.HealthPercent(word));
        Assert.Equal(115, EnemyScalingRule.DamagePercent(word));
    }

    [Theory]
    [InlineData(0u)]                 // fresh block / after disconnect
    [InlineData(0xFFFFu)]            // torn or foreign
    [InlineData(10u | (401u << 16))] // both halves out of range
    public void AZeroOrOutOfRangeHalfReadsAsVanilla(uint word)
    {
        Assert.Equal(100, EnemyScalingRule.HealthPercent(word));
        Assert.Equal(100, EnemyScalingRule.DamagePercent(word));
    }

    [Fact]
    public void ScaledHitPointsRaisesMaxAndCurrentTogether()
    {
        Assert.True(EnemyScalingRule.ScaledHitPoints(20, 20, 150, out int current, out int max));
        Assert.Equal(30, current);
        Assert.Equal(30, max);

        // A creature the module placed wounded stays proportionally wounded.
        Assert.True(EnemyScalingRule.ScaledHitPoints(10, 20, 200, out current, out max));
        Assert.Equal(20, current);
        Assert.Equal(40, max);
    }

    [Fact]
    public void ScaledHitPointsRefusesVanillaCorpsesAndPlayerCharacters()
    {
        Assert.False(EnemyScalingRule.ScaledHitPoints(20, 20, 100, out _, out _));
        Assert.False(EnemyScalingRule.ScaledHitPoints(0, 20, 150, out _, out _));
        Assert.False(EnemyScalingRule.ScaledHitPoints(-11, 20, 150, out _, out _));

        // A PC's STORED max is always 0 (MaxHitPointsRule): never touched.
        Assert.False(EnemyScalingRule.ScaledHitPoints(12, 0, 150, out _, out _));
    }

    [Fact]
    public void ScaledHitPointsStaysInsideTheEngineUshortAndNeverExceedsMax()
    {
        Assert.True(EnemyScalingRule.ScaledHitPoints(60000, 60000, 400, out int current, out int max));
        Assert.Equal(ushort.MaxValue, max);
        Assert.Equal(ushort.MaxValue, current);

        // A current above max (an engine oddity) is clamped to the new max, not scaled past it.
        Assert.True(EnemyScalingRule.ScaledHitPoints(25, 20, 150, out current, out max));
        Assert.Equal(30, max);
        Assert.Equal(30, current);
    }

    [Theory]
    [InlineData(10, 115, 12)]   // 11.5 rounds half up
    [InlineData(10, 135, 14)]   // 13.5 -> 14
    [InlineData(1, 50, 1)]      // a hit that landed still lands
    [InlineData(-1, 150, -1)]   // an unset slot reads -1 and stays -1
    [InlineData(0, 150, 0)]
    [InlineData(7, 100, 7)]
    public void ScaleDamageRoundsHalfUpAndLeavesUnsetSlotsAlone(int amount, int percent, int expected)
    {
        Assert.Equal(expected, EnemyScalingRule.ScaleDamage(amount, percent));
    }

    [Fact]
    public void TheLatchRetainsFirstSightingBaselineAndFailsFullTowardsVanilla()
    {
        ScaledObjectLatch latch = new(2);
        Assert.False(latch.Contains(0x14));
        Assert.True(latch.Add(0x14, baseMaximum: 15, appliedPercent: 150));
        Assert.False(latch.Add(0x14, baseMaximum: 23, appliedPercent: 200)); // keep first baseline
        Assert.True(latch.Contains(0x14));
        Assert.True(latch.TryGetBaseline(0x14, out int baseMaximum, out int appliedPercent));
        Assert.Equal(15, baseMaximum);
        Assert.Equal(150, appliedPercent);
        Assert.False(latch.Add(0, baseMaximum: 15, appliedPercent: 150)); // 0 is never an object
        Assert.False(latch.Add(0x15, baseMaximum: 0, appliedPercent: 150));
        Assert.False(latch.Add(0x15, baseMaximum: 15, appliedPercent: 401));
        Assert.True(latch.Add(0x15, baseMaximum: 15, appliedPercent: 200));
        Assert.True(latch.IsFull);
        Assert.False(latch.Add(0x16, baseMaximum: 15, appliedPercent: 150)); // newcomer stays vanilla
        Assert.False(latch.Contains(0x16));
        Assert.False(latch.TryGetBaseline(0x16, out _, out _));

        latch.Clear();                        // module changed: every id is stale
        Assert.Equal(0, latch.Count);
        Assert.False(latch.Contains(0x14));
        Assert.False(latch.TryGetBaseline(0x14, out _, out _));
        Assert.True(latch.Add(0x16, baseMaximum: 20, appliedPercent: 250));
        Assert.True(latch.TryGetBaseline(0x16, out baseMaximum, out appliedPercent));
        Assert.Equal(20, baseMaximum);
        Assert.Equal(250, appliedPercent);
    }
}

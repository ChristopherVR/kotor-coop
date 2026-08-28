using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure decision behind the `topup` fix: `Odyssey.GetMaxHitPoints`'s plain stored-field
/// read is always zero for a player character (the engine never writes CSWSObject+0xE0 for
/// one), which is why `topup` refused with "0 max hp" for every player, not just after
/// combat — see docs/research/playthrough-taris.md's dated follow-up. The real branch,
/// reproduced here: a non-PC creature keeps reading the stored field; a PC sums
/// (level.hitPoints + conModifier) over every resolved level instead.
/// </summary>
public class MaxHitPointsRuleTests
{
    [Fact]
    public void NonPcJustReturnsTheStoredField()
    {
        Assert.Equal(0, MaxHitPointsRule.ForNonPc(0));
        Assert.Equal(42, MaxHitPointsRule.ForNonPc(42));
    }

    [Fact]
    public void PcSumsRolledHitPointsPlusConModifierPerLevel()
    {
        // The live-verified case from KotorGameBridge.Attach.cs's own TryReadMaxHitPoints doc
        // comment: a level-1 PC with a rolled 10 and a +2 CON modifier reads back 12.
        Assert.Equal(12, MaxHitPointsRule.ForPc(conModifier: 2, levelHitPoints: [10]));
    }

    [Fact]
    public void PcSumsAcrossMultipleLevels()
    {
        // Three levels, con modifier +1 added once PER level (matching 0x004ED372, not once
        // total) - (8+1) + (6+1) + (9+1) = 26.
        Assert.Equal(26, MaxHitPointsRule.ForPc(conModifier: 1, levelHitPoints: [8, 6, 9]));
    }

    [Fact]
    public void PcWithNoConModifierJustSumsTheRolledHitPoints()
    {
        Assert.Equal(15, MaxHitPointsRule.ForPc(conModifier: 0, levelHitPoints: [5, 5, 5]));
    }

    [Fact]
    public void AnEmptyLevelListReportsUnknownNotZeroHitPoints()
    {
        // 0 is the "unresolved, cannot heal past this" signal every Odyssey read already uses
        // - not a claim the character genuinely has 0 max hp.
        Assert.Equal(0, MaxHitPointsRule.ForPc(conModifier: 2, levelHitPoints: []));
    }

    [Fact]
    public void ALevelCountPastTheDefensiveBoundReportsUnknown()
    {
        int[] tooMany = new int[MaxHitPointsRule.MaxCharacterLevels + 1];
        Array.Fill(tooMany, 10);

        Assert.Equal(0, MaxHitPointsRule.ForPc(conModifier: 0, levelHitPoints: tooMany));
    }

    [Fact]
    public void ExactlyAtTheBoundIsStillAccepted()
    {
        int[] atBound = new int[MaxHitPointsRule.MaxCharacterLevels];
        Array.Fill(atBound, 1);

        Assert.Equal(MaxHitPointsRule.MaxCharacterLevels, MaxHitPointsRule.ForPc(conModifier: 0, levelHitPoints: atBound));
    }
}

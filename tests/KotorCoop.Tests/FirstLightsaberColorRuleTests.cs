using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/class-and-power-grants.md §11: the pure lookups
/// <see cref="KotorCoop.Session.Pumps.FirstLightsaberBuildPump"/> drives — which of the three
/// canonical starter sabers a resref is, and which colour a player's own class list implies.
/// </summary>
public class FirstLightsaberColorRuleTests
{
    private const string Blue = "g_w_lghtsbr01";   // Guardian (3)
    private const string Green = "g_w_lghtsbr03";  // Consular (4)
    private const string Yellow = "g_w_lghtsbr04"; // Sentinel (5)

    private const int Guardian = 3;
    private const int Consular = 4;
    private const int Sentinel = 5;

    [Theory]
    [InlineData(Blue)]
    [InlineData(Green)]
    [InlineData(Yellow)]
    public void IsCanonicalFirstSaberRecognisesExactlyTheThreeRetailBlueprints(string resref)
    {
        Assert.True(FirstLightsaberColorRule.IsCanonicalFirstSaber(resref));
    }

    [Fact]
    public void IsCanonicalFirstSaberIsCaseInsensitiveAndRejectsEverythingElse()
    {
        Assert.True(FirstLightsaberColorRule.IsCanonicalFirstSaber("G_W_LGHTSBR01"));
        Assert.False(FirstLightsaberColorRule.IsCanonicalFirstSaber("g_w_lghtsbr02"));
        Assert.False(FirstLightsaberColorRule.IsCanonicalFirstSaber(string.Empty));
    }

    [Theory]
    [InlineData(Guardian, Blue)]
    [InlineData(Sentinel, Yellow)]
    [InlineData(Consular, Green)]
    public void ResrefForClassesReturnsTheRetailColourForThatClass(int classId, string expectedResref)
    {
        Assert.Equal(expectedResref, FirstLightsaberColorRule.ResrefForClasses([classId]));
    }

    [Fact]
    public void ABaseClassAloneWithNoJediSlotYetReturnsNull()
    {
        // Soldier (0) is not one of the three Jedi classes.
        Assert.Null(FirstLightsaberColorRule.ResrefForClasses([0]));
    }

    [Fact]
    public void NoClassIdsAtAllReturnsNull()
    {
        Assert.Null(FirstLightsaberColorRule.ResrefForClasses([]));
    }

    [Fact]
    public void AMulticlassedCharacterIsMatchedByWhicheverSlotHoldsTheJediClass()
    {
        // classes.2da slot order is base-class-first (AddMultiClass always appends), but the
        // lookup must not assume that - it should find the Jedi class wherever it sits.
        Assert.Equal(Green, FirstLightsaberColorRule.ResrefForClasses([Consular, 1 /* Scout base */]));
        Assert.Equal(Green, FirstLightsaberColorRule.ResrefForClasses([1 /* Scout base */, Consular]));
    }

    [Fact]
    public void TheFlagIdIsAboveUintMaxValueSoItCanNeverBeMistakenForARealGlobalVariable()
    {
        // ObjectKeyedFlags.IsObjectKeyed is "flagId > uint.MaxValue" - a real global variable's
        // flag id lives entirely in the low 32 bits (GlobalVariableTable.MakeFlagId).
        Assert.True(FirstLightsaberColorRule.FirstSaberBuiltFlagId > uint.MaxValue);
    }
}

using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>docs/research/personal-inventory.md's classification rule: a quest item is the
/// engine's own Plot flag OR a census tag (docs/research/key-items-and-solo.md §7).</summary>
public class QuestItemCatalogTests
{
    [Theory]
    [InlineData("rakghoulserum")]
    [InlineData("ptar_permacrete")]
    [InlineData("tat20_banthafod")]
    [InlineData("g_i_belt006")]
    [InlineData("g_i_belt010")]
    [InlineData("man28_envirosuit")]
    [InlineData("kor_starpad")]
    [InlineData("dan16_key")]
    public void ACensusTagIsAQuestItemTag(string resref) =>
        Assert.True(QuestItemCatalog.IsQuestItemTag(resref));

    [Theory]
    [InlineData("RAKGHOULSERUM")]
    [InlineData("Rakghoulserum")]
    [InlineData("Man28_EnviroSuit")]
    public void TheCensusCheckIsCaseInsensitive(string resref) =>
        Assert.True(QuestItemCatalog.IsQuestItemTag(resref));

    [Theory]
    [InlineData("g_i_medeqpmnt01")] // an ordinary medpac
    [InlineData("g_w_thermldet01")] // an ordinary grenade
    [InlineData("g_a_clothes01")] // ordinary starting gear
    [InlineData("")]
    [InlineData(null)]
    public void AnOrdinaryItemIsNotAQuestItemTag(string? resref) =>
        Assert.False(QuestItemCatalog.IsQuestItemTag(resref));

    [Fact]
    public void ThePlotFlagAloneMakesAnItemAQuestItemEvenOffTheCensus()
    {
        Assert.True(QuestItemCatalog.IsQuestItem(plotFlag: true, resref: "some_modded_tag_not_in_the_census"));
    }

    [Fact]
    public void ACensusTagAloneMakesAnItemAQuestItemEvenWithoutThePlotFlag()
    {
        Assert.True(QuestItemCatalog.IsQuestItem(plotFlag: false, resref: "rakghoulserum"));
    }

    [Fact]
    public void NeitherSignalMakesAnOrdinaryItem()
    {
        Assert.False(QuestItemCatalog.IsQuestItem(plotFlag: false, resref: "g_i_medeqpmnt01"));
    }

    [Fact]
    public void TheCatalogueIsNonEmptyAndHasNoBlankEntries()
    {
        Assert.NotEmpty(QuestItemCatalog.Tags);
        Assert.All(QuestItemCatalog.Tags, tag => Assert.False(string.IsNullOrWhiteSpace(tag)));
    }
}

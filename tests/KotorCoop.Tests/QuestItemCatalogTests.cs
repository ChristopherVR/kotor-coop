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

    [Theory]
    [InlineData("dan13_bluecryst")]   // tag dan13_plotcrys, resref differs: live 2026-09-25 it never mirrored
    [InlineData("ptar_rakghoulser")]  // the buyable Taris serum
    [InlineData("man28_envirosuit")]
    public void APlotItemsResrefIsAQuestPoolKey(string resref) =>
        Assert.True(QuestItemCatalog.IsQuestItemTag(resref));

    [Theory]
    [InlineData("g_i_medeqpmnt01")]
    [InlineData("g_w_lghtsbr01")]
    public void AnOrdinaryItemIsNot(string resref) =>
        Assert.False(QuestItemCatalog.IsQuestItemTag(resref));

    /// <summary>2026-09-25 census cross-check: found-only items a quest script tests with
    /// GetItemPossessedBy that carry no Plot flag, so only the tag list can share them.</summary>
    [Theory]
    [InlineData("tar10_elevcard")]
    [InlineData("tar10_accelerator")]
    [InlineData("k37_itm_freedont")]
    [InlineData("w_diary")]
    [InlineData("tat20_gaffistick")]
    [InlineData("punk_conch")]
    public void AScriptCheckedNonPlotItemIsStillAQuestItem(string tag) =>
        Assert.True(QuestItemCatalog.IsQuestItemTag(tag));
}

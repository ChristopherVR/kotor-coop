using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>docs/research/personal-inventory.md's per-channel mode rules — pure, no
/// session/bridge involved.</summary>
public class InventorySyncRuleTests
{
    [Fact]
    public void SharedModeSyncsEveryPartyPoolKeyRegardlessOfClassification()
    {
        Assert.True(InventorySyncRule.ShouldSyncPartyPoolKey(inventoryPersonal: false, "g_i_medeqpmnt01"));
        Assert.True(InventorySyncRule.ShouldSyncPartyPoolKey(inventoryPersonal: false, "rakghoulserum"));
        Assert.True(InventorySyncRule.ShouldSyncPartyPoolKey(inventoryPersonal: false, null));
    }

    [Fact]
    public void PersonalModeSyncsOnlyAQuestItem()
    {
        Assert.True(InventorySyncRule.ShouldSyncPartyPoolKey(inventoryPersonal: true, "rakghoulserum"));
        Assert.False(InventorySyncRule.ShouldSyncPartyPoolKey(inventoryPersonal: true, "g_i_medeqpmnt01"));
        Assert.False(InventorySyncRule.ShouldSyncPartyPoolKey(inventoryPersonal: true, null));
    }

    [Fact]
    public void SharedCreditsModeAlwaysMerges() =>
        Assert.True(InventorySyncRule.ShouldMergeCredits(creditsPersonal: false));

    [Fact]
    public void PersonalCreditsModeNeverMerges() =>
        Assert.False(InventorySyncRule.ShouldMergeCredits(creditsPersonal: true));
}

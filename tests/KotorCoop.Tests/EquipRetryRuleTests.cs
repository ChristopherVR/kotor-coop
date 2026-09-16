using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Whether a phantom's equip attempt should be latched as done, or left to retry.
///
/// THE REGRESSION THIS GUARDS: an equip failure used to be cached exactly like a success, so
/// a phantom whose first equip attempt failed never got dressed for the rest of the module.
/// Live-measured 2026-09-16 as a completely empty client-twin animbase model resref (an
/// invisible host on the joined player's screen) despite the appearance.2da row and name
/// both being correct. See EquipRetryRule's own doc comment.
///
/// The phantom path equips through Odyssey.EquipItemFromTemplateDirect, whose create step
/// (Odyssey.GiveItemToInventory) reports a blueprint this install does not have as failure 8.
/// </summary>
public class EquipRetryRuleTests
{
    [Fact]
    public void ASuccessAlwaysLatches()
    {
        Assert.True(EquipRetryRule.ShouldLatch(ok: true, failure: 0));
    }

    [Fact]
    public void ABlueprintThisInstallDoesNotHaveLatchesEvenThoughItFailed()
    {
        // The peer's mod is not installed here. Retrying changes nothing, so this is the one
        // failure that is correctly remembered as done.
        Assert.True(EquipRetryRule.ShouldLatch(ok: false, failure: EquipRetryRule.BlueprintMissing));
    }

    [Theory]
    [InlineData(1)]  // GiveItemToInventory: no implementer yet
    [InlineData(2)]  // GiveItemToInventory: no routine table yet
    [InlineData(3)]  // GiveItemToInventory: CreateItemOnObject not found in the table
    [InlineData(4)]  // GiveItemToInventory: the script VM is mid-call
    [InlineData(5)]  // GiveItemToInventory: a stack push failed
    [InlineData(6)]  // GiveItemToInventory: CreateItemOnObject returned non-zero
    [InlineData(7)]  // GiveItemToInventory: stack pointer imbalance
    [InlineData(20)] // EquipItemFromTemplateDirect: slot could not be mapped
    [InlineData(21)] // EquipItemFromTemplateDirect: create returned no item and no reason
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)] // EquipItemFromTemplateDirect: the engine's equip refused
    [InlineData(25)]
    [InlineData(26)] // EquipItemFromTemplateDirect: the slot read-back did not hold the item
    public void EveryOtherFailureMustRetry(int failure)
    {
        Assert.False(EquipRetryRule.ShouldLatch(ok: false, failure));
    }

    [Fact]
    public void TheMissingBlueprintCodeIsGiveItemToInventorysEight()
    {
        Assert.Equal(8, EquipRetryRule.BlueprintMissing);
    }

    /// <summary>
    /// THE REGRESSION THIS GUARDS: every failure code ShouldLatch tells the caller to retry
    /// fires AFTER GiveItemToInventory has already created a fresh item on the target creature
    /// (EquipItemFromTemplateDirect's own 20-26 range). Retrying forever on a PERSISTENT (not
    /// transient) failure creates and orphans one new item every attempt for the life of the
    /// module. ShouldGiveUp is the bound that stops it — the same shape as
    /// PhantomController.TrySpawnOne's own MaxSpawnFailures.
    /// </summary>
    [Fact]
    public void BelowTheBoundKeepsRetrying()
    {
        Assert.False(EquipRetryRule.ShouldGiveUp(EquipRetryRule.MaxConsecutiveFailures - 1));
    }

    [Fact]
    public void AtTheBoundGivesUp()
    {
        Assert.True(EquipRetryRule.ShouldGiveUp(EquipRetryRule.MaxConsecutiveFailures));
    }

    [Fact]
    public void PastTheBoundStillGivesUp()
    {
        Assert.True(EquipRetryRule.ShouldGiveUp(EquipRetryRule.MaxConsecutiveFailures + 100));
    }

    [Fact]
    public void ZeroConsecutiveFailuresNeverGivesUp()
    {
        Assert.False(EquipRetryRule.ShouldGiveUp(0));
    }
}

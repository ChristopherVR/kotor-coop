using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>docs/research/personal-inventory.md §6's pure carry/merge core.</summary>
public class PersonalInventoryCarryRuleTests
{
    private static readonly PartyPoolStackKey Medpac = new("g_i_medeqpmnt01", 0, 0, 0, 0);
    private static readonly PartyPoolStackKey Grenade = new("g_w_thermldet01", 0, 0, 0, 0);
    private static readonly PartyPoolStackKey QuestSerum = new("rakghoulserum", 0, 0, 0, 0);

    [Fact]
    public void FilterCarriableDropsQuestItemsAndKeepsEverythingElse()
    {
        PersonalInventoryCarryEntry[] scanned =
        [
            new(Medpac, 3),
            new(QuestSerum, 1),
            new(Grenade, 2),
        ];

        IReadOnlyList<PersonalInventoryCarryEntry> kept = PersonalInventoryCarryRule.FilterCarriable(scanned);

        Assert.Equal(2, kept.Count);
        Assert.Contains(kept, e => e.Key == Medpac && e.Count == 3);
        Assert.Contains(kept, e => e.Key == Grenade && e.Count == 2);
        Assert.DoesNotContain(kept, e => e.Key == QuestSerum);
    }

    [Fact]
    public void ComputeRestoreOpsReturnsNothingWhenTheAdoptedWorldAlreadyHasEverything()
    {
        PersonalInventoryCarryEntry[] carried = [new(Medpac, 3)];
        Dictionary<PartyPoolStackKey, int> present = new() { [Medpac] = 3 };

        Assert.Empty(PersonalInventoryCarryRule.ComputeRestoreOps(carried, present));
    }

    [Fact]
    public void ComputeRestoreOpsReturnsTheFullCarriedAmountWhenNothingIsPresentYet()
    {
        PersonalInventoryCarryEntry[] carried = [new(Medpac, 3)];
        Dictionary<PartyPoolStackKey, int> present = [];

        IReadOnlyList<PersonalInventoryCarryEntry> ops = PersonalInventoryCarryRule.ComputeRestoreOps(carried, present);

        Assert.Single(ops);
        Assert.Equal(Medpac, ops[0].Key);
        Assert.Equal(3, ops[0].Count);
    }

    [Fact]
    public void ComputeRestoreOpsReturnsOnlyTheExcessWhenPartiallyPresent()
    {
        PersonalInventoryCarryEntry[] carried = [new(Medpac, 5)];
        Dictionary<PartyPoolStackKey, int> present = new() { [Medpac] = 2 };

        IReadOnlyList<PersonalInventoryCarryEntry> ops = PersonalInventoryCarryRule.ComputeRestoreOps(carried, present);

        Assert.Single(ops);
        Assert.Equal(3, ops[0].Count);
    }

    [Fact]
    public void ComputeRestoreOpsNeverGoesNegativeWhenMorePresentThanCarried()
    {
        PersonalInventoryCarryEntry[] carried = [new(Medpac, 1)];
        Dictionary<PartyPoolStackKey, int> present = new() { [Medpac] = 4 };

        Assert.Empty(PersonalInventoryCarryRule.ComputeRestoreOps(carried, present));
    }

    [Fact]
    public void ComputeRestoreOpsHandlesMultipleDistinctKeysIndependently()
    {
        PersonalInventoryCarryEntry[] carried = [new(Medpac, 3), new(Grenade, 2)];
        Dictionary<PartyPoolStackKey, int> present = new() { [Medpac] = 3 };

        IReadOnlyList<PersonalInventoryCarryEntry> ops = PersonalInventoryCarryRule.ComputeRestoreOps(carried, present);

        Assert.Single(ops);
        Assert.Equal(Grenade, ops[0].Key);
        Assert.Equal(2, ops[0].Count);
    }

    [Fact]
    public void ComputeRestoreOpsIsIdempotentOnceApplied()
    {
        PersonalInventoryCarryEntry[] carried = [new(Medpac, 3)];
        Dictionary<PartyPoolStackKey, int> present = [];

        IReadOnlyList<PersonalInventoryCarryEntry> first = PersonalInventoryCarryRule.ComputeRestoreOps(carried, present);
        Assert.Single(first);

        // The apply "landed" - the world now holds what was requested.
        present[Medpac] = first[0].Count;

        Assert.Empty(PersonalInventoryCarryRule.ComputeRestoreOps(carried, present));
    }

    [Fact]
    public void ShouldSkipRestoreIsFalseTheFirstTimeAWorldIsSeen()
    {
        Assert.False(PersonalInventoryCarryRule.ShouldSkipRestore(lastRestoredWorldEpoch: 0, currentWorldEpoch: 5));
    }

    [Fact]
    public void ShouldSkipRestoreIsTrueForTheSameGenerationAgain()
    {
        Assert.True(PersonalInventoryCarryRule.ShouldSkipRestore(lastRestoredWorldEpoch: 5, currentWorldEpoch: 5));
    }

    [Fact]
    public void ShouldSkipRestoreIsFalseForADifferentGeneration()
    {
        Assert.False(PersonalInventoryCarryRule.ShouldSkipRestore(lastRestoredWorldEpoch: 5, currentWorldEpoch: 6));
    }

    [Fact]
    public void UnknownCurrentEpochNeverSkips()
    {
        // 0 = "unknown" on the CURRENT side never matches anything, per the house convention.
        Assert.False(PersonalInventoryCarryRule.ShouldSkipRestore(lastRestoredWorldEpoch: 5, currentWorldEpoch: 0));
    }
}

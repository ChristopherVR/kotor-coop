using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// docs/research/shared-inventory-v2.md §14's live finding, pinned as a rule.
///
/// Both shared-inventory scans walked a <c>CItemRepository</c>'s id array, skipped every id they
/// could not turn into a resref + identity with a bare <c>continue</c>, and returned the partial
/// aggregate as <c>true</c> — an authoritative statement of what that repository holds. On the
/// rig this was watched publish an EMPTY party pool while the pool was full: <c>TryGetItemResref</c>
/// resolves an unknown id by rebuilding the item-tag cache, <c>EnsureItemKnown</c> refuses to
/// rebuild for 15 seconds after the last rebuild, and so every id in the pool missed at once. The
/// host published "the stash is empty" and the receiving instance correctly emptied its own pool
/// to match.
/// </summary>
public class ItemAggregateScanRuleTests
{
    /// <summary>The ordinary case: everything walked was resolved.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4096)]
    public void AFullyResolvedWalkIsAnObservation(int walked) =>
        Assert.True(ItemAggregateScanRule.IsObservation(walked, unresolved: 0));

    /// <summary>An EMPTY repository is a real fact, not a failure. A party stash nobody has used
    /// holds nothing, and refusing to say so would make a genuine emptying unreplicable
    /// forever.</summary>
    [Fact]
    public void AnEmptyRepositoryIsStillAnObservation() =>
        Assert.True(ItemAggregateScanRule.IsObservation(walked: 0, unresolved: 0));

    /// <summary>The live failure: every entry missed, so the aggregate came out empty. Publishing
    /// that is a removal order for the whole pool.</summary>
    [Fact]
    public void AWalkThatResolvedNothingIsNotAnObservation() =>
        Assert.False(ItemAggregateScanRule.IsObservation(walked: 5, unresolved: 5));

    /// <summary>The strict half, and the one worth arguing about: a PARTIAL miss is the same
    /// defect with a smaller blast radius. Under-reporting a total in a host-authoritative
    /// absolute-state design is indistinguishable from someone having taken those units.</summary>
    [Fact]
    public void APartialMissIsNotAnObservationEither() =>
        Assert.False(ItemAggregateScanRule.IsObservation(walked: 5, unresolved: 1));

    /// <summary>A refusal has to say what it saw — five live rounds inferred this class of
    /// failure from log timestamps alone before it was watched happen.</summary>
    [Fact]
    public void ARefusalExplainsItself()
    {
        string why = ItemAggregateScanRule.Explain("party pool", walked: 5, unresolved: 5);

        Assert.Contains("5 of 5", why, StringComparison.Ordinal);
        Assert.Contains("party pool", why, StringComparison.Ordinal);
        Assert.Contains("partial", why, StringComparison.Ordinal);
    }
}

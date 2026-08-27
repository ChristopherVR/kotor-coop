using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure guard <c>Odyssey.BuyFromStore</c> applies before every unit it hands to the real
/// <c>CSWGuiStore::BuyItem</c> click handler — see docs/research/store-sync.md's 2026-08-28
/// live pass, which found `BuyItem` itself has NO affordability check (VERIFIED, fresh disasm:
/// price lookup -> subtract -> `SetGold`, no branch in between) and can silently underflow the
/// party's plain <c>uint</c> gold to just under <c>UINT32_MAX</c>.
/// </summary>
public class StoreAffordabilityRuleTests
{
    [Fact]
    public void ExactPriceIsAffordable()
    {
        Assert.True(StoreAffordabilityRule.CanAfford(price: 500, gold: 500));
    }

    [Fact]
    public void MoreGoldThanPriceIsAffordable()
    {
        Assert.True(StoreAffordabilityRule.CanAfford(price: 500, gold: 501));
    }

    [Fact]
    public void OneShortIsNotAffordable()
    {
        Assert.False(StoreAffordabilityRule.CanAfford(price: 500, gold: 499));
    }

    [Fact]
    public void ZeroGoldAgainstAPositivePriceIsNotAffordable()
    {
        Assert.False(StoreAffordabilityRule.CanAfford(price: 1, gold: 0));
    }

    [Fact]
    public void ThePoisonedNearWrapReadingCannotAffordAnything()
    {
        // The exact shape docs/research/store-sync.md's live pass observed: gold pinned at the
        // engine's own 999,999,999 SetGold clamp is still comfortably affordable for a normal
        // price, but nothing changes about the rule itself here - it is a plain comparison,
        // included to document the value this rule is meant to protect.
        Assert.True(StoreAffordabilityRule.CanAfford(price: 3_000, gold: 999_999_999));
    }

    [Fact]
    public void ZeroPriceIsAlwaysAffordableEvenWithNoGold()
    {
        Assert.True(StoreAffordabilityRule.CanAfford(price: 0, gold: 0));
    }

    [Fact]
    public void ANegativePriceReadingIsTreatedAsFreeRatherThanRefused()
    {
        // A failed/garbage engine price read should never be able to BLOCK a purchase - only
        // ever fail to protect against one. Refusing here would be a new failure mode this
        // rule does not exist to add.
        Assert.True(StoreAffordabilityRule.CanAfford(price: -1, gold: 0));
    }
}

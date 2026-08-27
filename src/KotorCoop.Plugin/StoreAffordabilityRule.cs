// StoreAffordabilityRule.cs - pure affordability check for the `buy` dev verb
// (docs/research/store-sync.md's 2026-08-28 live pass). Dependency-free by design (no
// Odyssey/SharedBlockView/unsafe pointer plumbing) so it qualifies for KotorCoop.Tests.csproj's
// "plugin compile-include trick" (docs/ARCHITECTURE.md) — tested directly rather than
// re-implemented in the test project.

namespace KotorCoop.Plugin;

/// <summary>
/// `CSWGuiStore::BuyItem` (0x006C0BE0) subtracts an item's price from the player's cached gold
/// int and hands the result straight to `CSWSCreature::SetGold` — UNCONDITIONALLY. VERIFIED,
/// fresh disasm of the whole function (docs/research/store-sync.md's 2026-08-28 live pass):
/// price lookup, subtract, `SetGold` — no `cmp`/conditional branch on the sign or magnitude of
/// the result anywhere in between. The real in-game Buy button is guarded only by the GUI
/// graying itself out when unaffordable; `Odyssey.BuyFromStore` calls the click handler
/// directly, bypassing that precondition entirely, so it applies this rule itself before every
/// single unit — matching the live-observed failure exactly (an unaffordable `buy` drove the
/// party's plain <c>uint</c> gold negative, which silently wraps to just under
/// <c>UINT32_MAX</c>).
/// </summary>
public static class StoreAffordabilityRule
{
    /// <summary>
    /// True when <paramref name="gold"/> covers <paramref name="price"/>. A non-positive price
    /// (a free item, or an engine price lookup that failed and read back 0) is always
    /// affordable — this rule exists to stop a negative RESULT, not to second-guess the
    /// engine's own price.
    /// </summary>
    public static bool CanAfford(int price, int gold) => price <= 0 || gold >= price;
}

// StoreSellGuard.cs - pure decision logic for the `sell` dev verb's own fixed-up path
// (docs/research/store-sync.md's 2026-08-29 root-cause pass on CSWGuiStore::SellItem).
// Dependency-free by design (no Odyssey/SharedBlockView/unsafe pointer plumbing) so it
// qualifies for KotorCoop.Tests.csproj's "plugin compile-include trick"
// (docs/ARCHITECTURE.md) — tested directly rather than re-implemented in the test project.

namespace KotorCoop.Plugin;

/// <summary>
/// <c>CSWSStore::AcquireItem</c> (0x005C7610) — the store's own "take an item from the
/// seller" primitive, called by both the real Sell-button handler and this codebase's own
/// fixed <c>SellToStore</c> — refuses UNCONDITIONALLY and immediately (before touching gold,
/// the item's repository membership, or the store's stock at all) when the incoming item's
/// <c>CSWSObject::plot</c> field (<c>CSWSItem + 0x108</c>, VERIFIED via
/// <c>tools/kotor1_0_3.db</c>: <c>CSWSItem.server_object</c> embeds a <c>CSWSObject</c> at
/// <c>+0x010</c>, and that class's own <c>plot</c> member sits at <c>+0x0F8</c>, so
/// <c>0x010 + 0x0F8 = 0x108</c>) is nonzero — VERIFIED, fresh disasm this pass:
/// <c>mov eax, [ecx+0x108]; test eax, eax; jne &lt;refuse&gt;</c> is the SECOND thing the
/// function does (right after its own item-null check; `AcquireItem` takes only the item as
/// an argument — its `this` is the already-resolved store, so unlike `SellItem` there is no
/// separate player/store null check to run first). This is the retail "you cannot sell plot
/// items" rule, read directly out of the store's own acquire path rather than the (separate,
/// GUI-only) plot-item greyed-out Sell button.
///
/// <see cref="Odyssey.SellToStore"/> checks this itself, BEFORE calling
/// <c>AcquireItem</c>, so a plot-flagged item gets a specific refusal reason instead of
/// silently doing nothing the way calling the native function on one used to (the exact
/// engine behaviour this rule reproduces for the purpose of failing loud rather than quiet).
/// </summary>
public static class StoreSellGuard
{
    /// <summary>True when the item's <c>CSWSObject::plot</c> field (read by the caller,
    /// passed in raw) is nonzero — matching the store's own <c>AcquireItem</c> refusal
    /// exactly. A non-plot item (the overwhelming majority) is never refused by this rule.</summary>
    public static bool IsPlotItem(uint plotFieldValue) => plotFieldValue != 0;
}

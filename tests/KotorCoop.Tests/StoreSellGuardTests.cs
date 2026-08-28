using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The pure guard <c>Odyssey.SellToStore</c> now applies before handing an item to
/// <c>CSWSStore::AcquireItem</c> — see docs/research/store-sync.md's 2026-08-29 root-cause
/// pass, which found `AcquireItem` itself refuses unconditionally (before touching gold or
/// either repository) whenever the item's <c>CSWSObject::plot</c> field is nonzero.
/// </summary>
public class StoreSellGuardTests
{
    [Fact]
    public void AZeroPlotFieldIsNotAPlotItem()
    {
        Assert.False(StoreSellGuard.IsPlotItem(plotFieldValue: 0));
    }

    [Fact]
    public void AOnePlotFieldIsAPlotItem()
    {
        Assert.True(StoreSellGuard.IsPlotItem(plotFieldValue: 1));
    }

    [Fact]
    public void AnyNonzeroPlotFieldIsAPlotItem()
    {
        // CSWSObject::plot is read as a plain nonzero test by the engine's own AcquireItem
        // (`test eax, eax; jne <refuse>`) - not masked against a specific bit pattern, so any
        // nonzero value refuses, not merely the literal 1 a .uti's Plot=1 field produces.
        Assert.True(StoreSellGuard.IsPlotItem(plotFieldValue: 0xFFFFFFFF));
    }
}

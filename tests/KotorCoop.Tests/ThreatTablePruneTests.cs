using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ThreatTable"/>'s self-bounding (docs/review-seamless-2026-09-18.md §2: <c>Forget</c>
/// had no callers, so a long module kept a row per creature ever hit). Pins: a creature whose
/// rows have all decayed is dropped, one still carrying threat or a live retarget cooldown is
/// kept, pruning never changes an answer the table gives, and <see cref="ThreatTable.AddThreat"/>
/// runs the prune itself at most once per window.
/// </summary>
public sealed class ThreatTablePruneTests
{
    [Fact]
    public void FullyDecayedCreaturesAreDroppedAndLiveOnesKept()
    {
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(0x10, 0x100, 5, nowSeconds: 0);
        table.AddThreat(0x20, 0x100, 5, nowSeconds: 8);

        Assert.Equal(1, table.PruneDecayed(nowSeconds: 12));

        Assert.Equal(1, table.CreatureCount);
        Assert.Equal(0, table.GetThreat(0x10, 0x100, nowSeconds: 12));
        Assert.True(table.GetThreat(0x20, 0x100, nowSeconds: 12) > 0);
    }

    [Fact]
    public void ARetargetCooldownStillRunningKeepsTheRow()
    {
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(0x10, 0x100, 5, nowSeconds: 0);
        table.MarkRetargeted(0x10, nowSeconds: 9);

        // Threat decayed at t=12, but the retarget was 3 s ago: the cooldown answer must survive.
        Assert.Equal(0, table.PruneDecayed(nowSeconds: 12));
        Assert.Equal(3, table.SecondsSinceLastRetarget(0x10, nowSeconds: 12));

        Assert.Equal(1, table.PruneDecayed(nowSeconds: 19));
        Assert.Equal(double.PositiveInfinity, table.SecondsSinceLastRetarget(0x10, nowSeconds: 19));
    }

    [Fact]
    public void AddThreatPrunesOnItsOwnAtMostOncePerWindow()
    {
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(0x10, 0x100, 5, nowSeconds: 0);
        table.AddThreat(0x20, 0x100, 5, nowSeconds: 1);

        // Inside the window: nothing dropped even though 0x10 would qualify later.
        table.AddThreat(0x30, 0x100, 5, nowSeconds: 9);
        Assert.Equal(3, table.CreatureCount);

        // A window past the last sweep, the next AddThreat drops the two that have fully
        // decayed (0x10 at t=10, 0x20 at t=11) and keeps 0x30, which decays at t=19.
        table.AddThreat(0x40, 0x100, 5, nowSeconds: 12);
        Assert.Equal(2, table.CreatureCount);
        Assert.True(table.GetThreat(0x30, 0x100, nowSeconds: 12) > 0);
        Assert.True(table.GetThreat(0x40, 0x100, nowSeconds: 12) > 0);
    }

    [Fact]
    public void ClearResetsThePruneClockToo()
    {
        ThreatTable table = new(windowSeconds: 10);
        table.AddThreat(0x10, 0x100, 5, nowSeconds: 100);
        table.Clear();

        // A fresh table after a module change prunes on its first add again (nothing to drop,
        // but the clock must not still think a sweep happened at t=100 of the OLD module).
        table.AddThreat(0x10, 0x100, 5, nowSeconds: 0);
        table.AddThreat(0x20, 0x100, 5, nowSeconds: 11);
        Assert.Equal(1, table.CreatureCount);
    }
}

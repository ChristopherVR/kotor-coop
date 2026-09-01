using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PhantomAttackerTable"/> is the capture-side half of per-attacker attribution —
/// what <c>PhantomHitHook</c> accumulates per phantom slot as hits land, before the controller
/// publishes it. Covers accumulation and the eviction rule; PhantomHitHook's own wiring (which
/// hits go to which slot's table) is exercised live, not here — this is the pure arithmetic.
/// </summary>
public class PhantomAttackerTableTests
{
    [Fact]
    public void AccumulatesPerAttacker()
    {
        PhantomAttackerTable table = new();

        table.Add(0x10, 4);
        table.Add(0x10, 6);
        table.Add(0x20, 3);

        Assert.Equal(10u, TotalFor(table, 0x10));
        Assert.Equal(3u, TotalFor(table, 0x20));
    }

    [Fact]
    public void ZeroIdOrZeroAmountIsIgnored()
    {
        PhantomAttackerTable table = new();

        table.Add(0, 40);
        table.Add(0x10, 0);

        Assert.Equal(0u, TotalFor(table, 0));
        Assert.Equal(0u, TotalFor(table, 0x10));
    }

    [Fact]
    public void EvictsTheSmallestTotalWhenAFifthAttackerLandsAHit()
    {
        PhantomAttackerTable table = new();

        table.Add(0x10, 100);
        table.Add(0x20, 50);
        table.Add(0x30, 10); // smallest — expected to be evicted
        table.Add(0x40, 75);

        table.Add(0x50, 5);

        Assert.Equal(100u, TotalFor(table, 0x10));
        Assert.Equal(50u, TotalFor(table, 0x20));
        Assert.Equal(0u, TotalFor(table, 0x30)); // evicted
        Assert.Equal(75u, TotalFor(table, 0x40));
        Assert.Equal(5u, TotalFor(table, 0x50));
    }

    [Fact]
    public void ClearDropsEveryEntry()
    {
        PhantomAttackerTable table = new();
        table.Add(0x10, 40);

        table.Clear();

        Assert.Equal(0u, TotalFor(table, 0x10));
    }

    private static uint TotalFor(PhantomAttackerTable table, uint attackerId)
    {
        for (int i = 0; i < PhantomAttackerTable.Capacity; i++)
        {
            if (table.IdAt(i) == attackerId && attackerId != 0)
            {
                return table.TotalAt(i);
            }
        }

        return 0;
    }
}

using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The receive-side half of the "no cap really" enemy census
/// (docs/research/creature-liveness.md): <see cref="EnemySnapshotAssembler"/> accumulates a
/// tick's page(s) with no dependency on a socket, so the out-of-order/partial-arrival arithmetic
/// — most importantly, "a lost page must not stall the others" — is asserted here directly. See
/// <see cref="WorldSnapshotAssemblerTests"/> for the sibling channel's identical shape.
/// </summary>
public class EnemySnapshotAssemblerTests
{
    private static EnemyState State(uint id) => new(id, 15, 15, false);

    private static EnemySnapshotPacket Page(
        long tickId, byte pageIndex, byte pageCount, params EnemyState[] enemies) =>
        new(tickId, "end_m01aa", enemies, pageIndex, pageCount);

    [Fact]
    public void ASinglePageTickAppliesImmediatelyAndCompletesAtOnce()
    {
        EnemySnapshotAssembler assembler = new();
        EnemySnapshotPacket page = Page(1000, 0, 1, State(0x14));

        IReadOnlyList<EnemyState> applied = assembler.Apply(page);

        Assert.Single(applied);
        Assert.Equal(0x14u, applied[0].ObjectId);
        Assert.True(assembler.TickComplete);
        Assert.Contains(0x14u, assembler.PresentIds);
    }

    /// <summary>
    /// The whole point: an enemy named only on page 2 of a tick must be usable the instant
    /// page 2 arrives, whether or not page 1 (or any other sibling page) ever shows up at all —
    /// a combat-critical health/position update cannot wait on a sibling datagram.
    /// </summary>
    [Fact]
    public void ALostPageDoesNotStallTheOthers()
    {
        EnemySnapshotAssembler assembler = new();

        // Page 0 of tick 1000 is simply never delivered — simulating a lost datagram — and
        // only page 1 arrives.
        EnemySnapshotPacket page1 = Page(1000, 1, 2, State(0x35));

        IReadOnlyList<EnemyState> applied = assembler.Apply(page1);

        Assert.Single(applied);
        Assert.Equal(0x35u, applied[0].ObjectId);
        Assert.Contains(0x35u, assembler.PresentIds);

        // But the tick's own cycle is honestly incomplete — page 0 never showed up — so a
        // roster prune must not run off this partial picture (CoopClient.HandleEnemySnapshot
        // gates its prune on exactly this).
        Assert.False(assembler.TickComplete);
    }

    [Fact]
    public void PagesOfTheSameTickAccumulateRegardlessOfArrivalOrder()
    {
        EnemySnapshotAssembler assembler = new();

        assembler.Apply(Page(1000, 1, 2, State(0x35)));
        Assert.False(assembler.TickComplete);

        assembler.Apply(Page(1000, 0, 2, State(0x14)));

        Assert.True(assembler.TickComplete);
        Assert.Equal([0x14u, 0x35u], assembler.PresentIds.OrderBy(id => id));
    }

    [Fact]
    public void ReDeliveringTheSamePageIsHarmless()
    {
        EnemySnapshotAssembler assembler = new();
        EnemySnapshotPacket page = Page(1000, 0, 1, State(0x14));

        assembler.Apply(page);
        assembler.Apply(page);

        Assert.True(assembler.TickComplete);
        Assert.Single(assembler.PresentIds);
    }

    [Fact]
    public void ANewTickIdStartsAFreshCycleDiscardingThePartialOldOne()
    {
        EnemySnapshotAssembler assembler = new();

        // Tick 1000's cycle is left incomplete — only page 0 of 2 ever arrives.
        assembler.Apply(Page(1000, 0, 2, State(0x14)));
        Assert.False(assembler.TickComplete);

        // Tick 1033 is a brand new, unrelated cycle (a single page this time) — resent every
        // tick regardless of page count, unlike the throttled liveness channel.
        IReadOnlyList<EnemyState> applied = assembler.Apply(Page(1033, 0, 1, State(0x35)));

        Assert.Single(applied);
        Assert.Equal(0x35u, applied[0].ObjectId);
        Assert.True(assembler.TickComplete);

        // The stale enemy from the abandoned tick 1000 cycle must not linger in the new one.
        Assert.DoesNotContain(0x14u, assembler.PresentIds);
        Assert.Contains(0x35u, assembler.PresentIds);
    }

    [Fact]
    public void ManyPagesOfOneTickAllAccumulateBeforeTickCompletes()
    {
        // The scenario this whole channel exists for: a census bigger than the old 32-entry
        // per-packet cap, arriving as several pages of one tick.
        EnemySnapshotAssembler assembler = new();

        List<EnemyState> firstPageEnemies = [];
        for (uint i = 0; i < EnemySnapshotPacket.MaxEntriesPerPacket; i++)
        {
            firstPageEnemies.Add(State(i + 1));
        }

        assembler.Apply(Page(5000, 0, 2, [.. firstPageEnemies]));
        Assert.False(assembler.TickComplete);

        IReadOnlyList<EnemyState> secondPageApplied = assembler.Apply(Page(5000, 1, 2, State(999)));

        Assert.Single(secondPageApplied);
        Assert.True(assembler.TickComplete);
        Assert.Equal(EnemySnapshotPacket.MaxEntriesPerPacket + 1, assembler.PresentIds.Count);
    }
}

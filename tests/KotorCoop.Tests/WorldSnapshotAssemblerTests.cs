using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The receive-side half of P0-2's graceful degradation: <see cref="WorldSnapshotAssembler"/>
/// accumulates a tick's page(s) with no dependency on a socket, so the out-of-order/partial-
/// arrival arithmetic — most importantly, "a lost page must not stall the others" — is asserted
/// here directly rather than only through the slower loopback battery. See
/// docs/research/lag-audit.md.
/// </summary>
public class WorldSnapshotAssemblerTests
{
    private static readonly PlayerState SampleState =
        new(new Vector3f(1f, 2f, 3f), 0.5f, "end_m01aa", 100, 0);

    private static WorldSnapshotPacket Page(
        long tickId, byte pageIndex, byte pageCount, params SnapshotEntry[] entries) =>
        new(tickId, pageIndex, pageCount, 1, 1, 1, 1, 0, 0, 0, 0, entries);

    [Fact]
    public void ASinglePageTickAppliesImmediatelyAndCompletesAtOnce()
    {
        WorldSnapshotAssembler assembler = new();
        WorldSnapshotPacket page = Page(1000, 0, 1, new SnapshotEntry(2, "Malak", SampleState));

        IReadOnlyList<SnapshotEntry> applied = assembler.Apply(page, localPeerId: 1);

        Assert.Single(applied);
        Assert.Equal(2u, applied[0].PeerId);
        Assert.True(assembler.TickComplete);
        Assert.Contains(2u, assembler.PresentPeerIds);
        Assert.False(assembler.SelfListed);
    }

    /// <summary>
    /// The whole point: a peer named only on page 2 of a tick must be usable the instant page 2
    /// arrives, whether or not page 1 (or any other sibling page) ever shows up at all.
    /// </summary>
    [Fact]
    public void ALostPageDoesNotStallTheOthers()
    {
        WorldSnapshotAssembler assembler = new();

        // Page 0 of tick 1000 is simply never delivered — simulating a lost datagram — and
        // only page 1 arrives.
        WorldSnapshotPacket page1 = Page(1000, 1, 2, new SnapshotEntry(3, "Bastila", SampleState));

        IReadOnlyList<SnapshotEntry> applied = assembler.Apply(page1, localPeerId: 1);

        // Bastila's entry is ready to use right now, regardless of page 0's fate.
        Assert.Single(applied);
        Assert.Equal(3u, applied[0].PeerId);
        Assert.Contains(3u, assembler.PresentPeerIds);

        // But the tick's own cycle is honestly incomplete — page 0 never showed up — so a
        // roster prune must not run off this partial picture (see CoopClient.HandleWorldSnapshot,
        // which gates SnapshotBuffer.RetainOnly on exactly this).
        Assert.False(assembler.TickComplete);
    }

    [Fact]
    public void PagesOfTheSameTickAccumulateRegardlessOfArrivalOrder()
    {
        WorldSnapshotAssembler assembler = new();

        assembler.Apply(Page(1000, 1, 2, new SnapshotEntry(3, "Bastila", SampleState)), localPeerId: 1);
        Assert.False(assembler.TickComplete);

        assembler.Apply(Page(1000, 0, 2, new SnapshotEntry(2, "Malak", SampleState)), localPeerId: 1);

        Assert.True(assembler.TickComplete);
        Assert.Equal([2u, 3u], assembler.PresentPeerIds.OrderBy(id => id));
    }

    [Fact]
    public void ReDeliveringTheSamePageIsHarmless()
    {
        WorldSnapshotAssembler assembler = new();
        WorldSnapshotPacket page = Page(1000, 0, 1, new SnapshotEntry(2, "Malak", SampleState));

        assembler.Apply(page, localPeerId: 1);
        assembler.Apply(page, localPeerId: 1);

        Assert.True(assembler.TickComplete);
        Assert.Single(assembler.PresentPeerIds);
    }

    [Fact]
    public void ANewTickIdStartsAFreshCycleDiscardingThePartialOldOne()
    {
        WorldSnapshotAssembler assembler = new();

        // Tick 1000's cycle is left incomplete — only page 0 of 2 ever arrives.
        assembler.Apply(Page(1000, 0, 2, new SnapshotEntry(2, "Malak", SampleState)), localPeerId: 1);
        Assert.False(assembler.TickComplete);

        // Tick 1033 is a brand new, unrelated cycle (a single page this time).
        IReadOnlyList<SnapshotEntry> applied =
            assembler.Apply(Page(1033, 0, 1, new SnapshotEntry(3, "Bastila", SampleState)), localPeerId: 1);

        Assert.Single(applied);
        Assert.Equal(3u, applied[0].PeerId);
        Assert.True(assembler.TickComplete);

        // The stale peer from the abandoned tick 1000 cycle must not linger in the new one.
        Assert.DoesNotContain(2u, assembler.PresentPeerIds);
        Assert.Contains(3u, assembler.PresentPeerIds);
    }

    [Fact]
    public void SelfListedTracksTheLocalPlayersOwnEntryAcrossPages()
    {
        WorldSnapshotAssembler assembler = new();

        // The local player's own entry lands on page 1, not page 0.
        assembler.Apply(Page(1000, 0, 2, new SnapshotEntry(2, "Malak", SampleState)), localPeerId: 1);
        Assert.False(assembler.SelfListed);

        assembler.Apply(Page(1000, 1, 2, new SnapshotEntry(1, "Revan", SampleState)), localPeerId: 1);
        Assert.True(assembler.SelfListed);

        // The local player's own id is never counted as a remote "present" peer.
        Assert.DoesNotContain(1u, assembler.PresentPeerIds);
    }
}

using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The wire-facing half of the "no cap really" enemy census (docs/research/creature-liveness.md,
/// docs/review-seamless-2026-09-18.md): splitting one tick's full enemy list into datagram-sized
/// pages, resent every tick (never throttled the way <see cref="CreatureLivenessPaging"/> is,
/// since combat needs low-latency health/position, not eventual consistency). See
/// <see cref="CreatureLivenessPagingTests"/> for the sibling channel's identical shape.
/// </summary>
public class EnemyCensusPagingTests
{
    private static EnemyState State(uint id) => new(id, 15, 15, false);

    [Fact]
    public void AnEmptyCycleStillProducesOnePacket()
    {
        List<EnemySnapshotPacket> pages = EnemyCensusPaging.Paginate(1000, "end_m01aa", []);

        EnemySnapshotPacket page = Assert.Single(pages);
        Assert.Equal(0, page.PageIndex);
        Assert.Equal(1, page.PageCount);
        Assert.Empty(page.Enemies);
    }

    [Fact]
    public void ASmallCensusIsOnePage()
    {
        List<EnemyState> enemies = [State(0x14), State(0x17)];

        List<EnemySnapshotPacket> pages = EnemyCensusPaging.Paginate(1000, "end_m01aa", enemies);

        EnemySnapshotPacket page = Assert.Single(pages);
        Assert.Equal(1, page.PageCount);
        Assert.Equal(enemies, page.Enemies);
    }

    [Fact]
    public void MoreThanThirtyTwoEnemiesSplitsAcrossSeveralPacketsInsteadOfTruncating()
    {
        // The old fixed cap this pass removes was exactly 32 (ProtocolConstants.MaxEnemies,
        // which is now the per-PAGE limit, not the total). A census of 51 — the largest
        // static Creature List entry count found across all 117 shipped modules, manm27aa —
        // must still deliver every single one.
        int total = 51;
        List<EnemyState> enemies = [];
        for (uint i = 0; i < total; i++)
        {
            enemies.Add(State(i + 1));
        }

        List<EnemySnapshotPacket> pages = EnemyCensusPaging.Paginate(2000, "manm27aa", enemies);

        Assert.Equal(2, pages.Count);

        int reassembled = 0;
        for (byte i = 0; i < pages.Count; i++)
        {
            EnemySnapshotPacket page = pages[i];
            Assert.Equal(i, page.PageIndex);
            Assert.Equal((byte)pages.Count, page.PageCount);
            Assert.Equal(2000, page.ServerTimeMs);
            reassembled += page.Enemies.Count;
        }

        Assert.Equal(total, reassembled);
        Assert.Equal(EnemySnapshotPacket.MaxEntriesPerPacket, pages[0].Enemies.Count);
        Assert.Equal(total - EnemySnapshotPacket.MaxEntriesPerPacket, pages[1].Enemies.Count);

        // Every id that went in comes back out — nothing silently dropped.
        Assert.Equal(
            enemies.Select(e => e.ObjectId).ToHashSet(),
            pages.SelectMany(p => p.Enemies).Select(e => e.ObjectId).ToHashSet());
    }

    [Fact]
    public void TwoHundredEnemiesStillFitEveryPageInsideOneDatagramWithNoneLost()
    {
        // Far beyond any real module (largest surveyed: 51) or even the shared-block capacity
        // (128) — a deliberately extreme case proving the wire layer itself has no ceiling of
        // its own: MaxPacketSize is respected on every page regardless of how many pages that
        // takes.
        const int total = 200;
        List<EnemyState> enemies = [];
        for (uint i = 0; i < total; i++)
        {
            enemies.Add(State(i + 1));
        }

        List<EnemySnapshotPacket> pages = EnemyCensusPaging.Paginate(3000, "end_m01aa", enemies);

        Assert.Equal(
            (int)Math.Ceiling(total / (double)EnemySnapshotPacket.MaxEntriesPerPacket), pages.Count);

        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int reassembled = 0;
        foreach (EnemySnapshotPacket page in pages)
        {
            int length = PacketCodec.Write(buffer, page);
            Assert.True(length > 0, "every page must fit inside one datagram");
            Assert.True(length <= ProtocolConstants.MaxPacketSize);

            EnemySnapshotPacket? decoded = PacketCodec.ReadEnemySnapshot(buffer[..length]);
            Assert.NotNull(decoded);
            reassembled += decoded.Enemies.Count;
        }

        Assert.Equal(total, reassembled);
    }
}

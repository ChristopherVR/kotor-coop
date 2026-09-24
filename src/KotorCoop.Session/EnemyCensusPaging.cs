using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Splits one tick's full enemy census into the packet(s) that actually go on the wire, so a
/// module with more hostiles than fit one datagram degrades to several instead of being
/// silently truncated. Pure and side-effect-free, exactly like <see cref="WorldSnapshotPaging"/>
/// and <see cref="CreatureLivenessPaging"/>, so the pagination arithmetic can be unit-tested
/// without a socket.
///
/// Resent every tick, like the channel it replaces — unlike
/// <see cref="CreatureLivenessPaging"/>'s multi-second throttle, combat needs low-latency
/// health and position, not eventual consistency, so a busy module simply costs more datagrams
/// per tick rather than a slower cycle. The list handed in is already sorted
/// fighting-first-then-nearest by <c>KotorGameBridge.ReadEnemies</c>, so page 0 is always the
/// most urgent page even if a later one is lost. See docs/research/creature-liveness.md's
/// cap-removal section.
/// </summary>
public static class EnemyCensusPaging
{
    /// <summary>
    /// Builds the packet(s) for one tick. Always returns at least one packet — an empty
    /// <paramref name="enemies"/> list still has to be SAID, the same "an empty write
    /// supersedes the last one" rule every other level channel in this codebase follows: a
    /// module gone quiet (every hostile died, or the owner left) must clear a stale accumulated
    /// set on the receiving side rather than leave it standing forever.
    /// </summary>
    public static List<EnemySnapshotPacket> Paginate(
        long serverTimeMs, string module, IReadOnlyList<EnemyState> enemies)
    {
        int pageCount = Math.Max(
            1, (enemies.Count + EnemySnapshotPacket.MaxEntriesPerPacket - 1) / EnemySnapshotPacket.MaxEntriesPerPacket);

        // A cycle never plans for more pages than a byte can index — see
        // CreatureLivenessPaging's identical guard. A module with enough concurrently-tracked
        // hostiles to need more than 255 * MaxEntriesPerPacket entries in one tick is not a
        // real case; later pages are dropped rather than overflowing PageIndex/PageCount.
        pageCount = Math.Min(pageCount, byte.MaxValue);

        List<EnemySnapshotPacket> pages = new(pageCount);
        for (int page = 0; page < pageCount; page++)
        {
            int start = page * EnemySnapshotPacket.MaxEntriesPerPacket;
            int count = Math.Max(
                0, Math.Min(EnemySnapshotPacket.MaxEntriesPerPacket, enemies.Count - start));

            List<EnemyState> slice = new(count);
            for (int i = 0; i < count; i++)
            {
                slice.Add(enemies[start + i]);
            }

            pages.Add(new EnemySnapshotPacket(serverTimeMs, module, slice, (byte)page, (byte)pageCount));
        }

        return pages;
    }
}

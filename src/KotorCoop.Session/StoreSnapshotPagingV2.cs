using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>The store's mirror of <see cref="PartyPoolSnapshotPagingV2"/> — identical reasoning,
/// widened by the store's own object id (already carried inside <see cref="StoreStackKey"/>, so
/// one cycle can page several stores together with no extra bookkeeping here).</summary>
public static class StoreSnapshotPagingV2
{
    public static List<StoreSnapshotPacketV2> Paginate(
        long serverTimeMs, uint generation, IReadOnlyDictionary<StoreStackKey, int> totals)
    {
        List<StoreSnapshotEntry> entries = new(totals.Count);
        foreach ((StoreStackKey key, int count) in totals)
        {
            entries.Add(new StoreSnapshotEntry(key, count));
        }

        int pageCount = Math.Max(
            1,
            (entries.Count + StoreSnapshotPacketV2.MaxEntriesPerPacket - 1)
                / StoreSnapshotPacketV2.MaxEntriesPerPacket);

        pageCount = Math.Min(pageCount, byte.MaxValue);

        List<StoreSnapshotPacketV2> pages = new(pageCount);
        for (int page = 0; page < pageCount; page++)
        {
            int start = page * StoreSnapshotPacketV2.MaxEntriesPerPacket;
            int count = Math.Max(
                0, Math.Min(StoreSnapshotPacketV2.MaxEntriesPerPacket, entries.Count - start));

            List<StoreSnapshotEntry> slice = new(count);
            for (int i = 0; i < count; i++)
            {
                slice.Add(entries[start + i]);
            }

            pages.Add(new StoreSnapshotPacketV2(serverTimeMs, generation, (byte)page, (byte)pageCount, slice));
        }

        return pages;
    }
}

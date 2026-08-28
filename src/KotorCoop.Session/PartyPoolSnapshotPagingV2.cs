using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Splits one publish cycle's full party-pool belief into the packet(s) that actually go on the
/// wire — the v2 (host-authoritative) sibling of <see cref="CreatureLivenessPaging"/>, minus its
/// cross-page digest (deliberately simpler; see
/// <see cref="Protocol.PartyPoolSnapshotPacketV2"/>'s own remarks for why that is safe here).
/// Pure and side-effect-free so the pagination arithmetic can be unit-tested without a socket.
/// </summary>
public static class PartyPoolSnapshotPagingV2
{
    /// <summary>
    /// Builds the packet(s) for one publish cycle. Always returns at least one packet — an
    /// empty <paramref name="totals"/> still has to be SAID, the same "an empty write supersedes
    /// the last one" rule <see cref="CreatureLivenessPaging.Paginate"/> already follows: a pool
    /// that has gone empty since the last cycle must actually clear a client's stale accumulated
    /// belief, not leave it standing forever.
    /// </summary>
    public static List<PartyPoolSnapshotPacketV2> Paginate(
        long serverTimeMs, uint generation, IReadOnlyDictionary<PartyPoolStackKey, int> totals)
    {
        List<PartyPoolSnapshotEntry> entries = new(totals.Count);
        foreach ((PartyPoolStackKey key, int count) in totals)
        {
            entries.Add(new PartyPoolSnapshotEntry(key, count));
        }

        int pageCount = Math.Max(
            1,
            (entries.Count + PartyPoolSnapshotPacketV2.MaxEntriesPerPacket - 1)
                / PartyPoolSnapshotPacketV2.MaxEntriesPerPacket);

        // A cycle never plans for more pages than a byte can index — see
        // CreatureLivenessPaging's identical reasoning; later pages are dropped rather than
        // overflowing PageIndex/PageCount, which a pool this size would never realistically hit
        // (ProtocolConstants.MaxPartyPoolStacks is a hard cap well under this).
        pageCount = Math.Min(pageCount, byte.MaxValue);

        List<PartyPoolSnapshotPacketV2> pages = new(pageCount);
        for (int page = 0; page < pageCount; page++)
        {
            int start = page * PartyPoolSnapshotPacketV2.MaxEntriesPerPacket;
            int count = Math.Max(
                0, Math.Min(PartyPoolSnapshotPacketV2.MaxEntriesPerPacket, entries.Count - start));

            List<PartyPoolSnapshotEntry> slice = new(count);
            for (int i = 0; i < count; i++)
            {
                slice.Add(entries[start + i]);
            }

            pages.Add(new PartyPoolSnapshotPacketV2(
                serverTimeMs, generation, (byte)page, (byte)pageCount, slice));
        }

        return pages;
    }
}

using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Splits one tick's full peer roster into the packet(s) that actually go on the wire, so a
/// lobby whose aggregate entry size would overrun <see cref="ProtocolConstants.MaxPacketSize"/>
/// degrades to several datagrams instead of being silently dropped whole. Pure and
/// side-effect-free, exactly like <see cref="CreatureLivenessPaging"/> on that channel, so the
/// pagination arithmetic can be unit-tested without a socket. See docs/research/lag-audit.md.
/// </summary>
public static class WorldSnapshotPaging
{
    /// <summary>
    /// Builds the packet(s) for one tick. Always returns at least one packet, even with zero
    /// entries (a host still at the main menu, or a lobby of one) — the same "an empty write
    /// still has to be SAID" rule <see cref="CreatureLivenessPaging.Paginate"/> already
    /// documents, so a client's roster prune still runs on an explicit empty page rather than
    /// silently receiving nothing.
    ///
    /// The flag/item/journal/loot version+digest arguments are passed on every returned
    /// packet's fields for simplicity, but only ever reach the wire for page 0 — see
    /// <see cref="KotorCoop.Protocol.PacketCodec"/>'s <c>Write(WorldSnapshotPacket)</c> and
    /// <see cref="WorldSnapshotPacket"/>'s remarks. <paramref name="droppedEntries"/> is 0
    /// unless the roster needed more than <see cref="WorldSnapshotPacket.MaxPages"/> pages —
    /// bounded rather than growing the send unboundedly, exactly like the old whole-snapshot
    /// guard bounded a single datagram; the caller (<c>CoopHost.Tick</c>) logs it once, the
    /// same latched "say it once, not every tick" shape that guard already used.
    /// </summary>
    public static List<WorldSnapshotPacket> Paginate(
        long serverTimeMs,
        uint flagVersion, uint itemVersion, uint journalVersion, uint lootVersion,
        ulong flagDigest, ulong itemDigest, ulong journalDigest, ulong lootDigest,
        IReadOnlyList<SnapshotEntry> entries,
        out int droppedEntries)
    {
        const int perPage = WorldSnapshotPacket.MaxEntriesPerPage;

        int neededPages = Math.Max(1, (entries.Count + perPage - 1) / perPage);
        int pageCount = Math.Min(neededPages, WorldSnapshotPacket.MaxPages);

        int carried = Math.Min(entries.Count, pageCount * perPage);
        droppedEntries = entries.Count - carried;

        List<WorldSnapshotPacket> pages = new(pageCount);
        for (int page = 0; page < pageCount; page++)
        {
            int start = page * perPage;
            int count = Math.Max(0, Math.Min(perPage, carried - start));

            List<SnapshotEntry> slice = new(count);
            for (int i = 0; i < count; i++)
            {
                slice.Add(entries[start + i]);
            }

            pages.Add(new WorldSnapshotPacket(
                serverTimeMs, (byte)page, (byte)pageCount,
                flagVersion, itemVersion, journalVersion, lootVersion,
                flagDigest, itemDigest, journalDigest, lootDigest, slice));
        }

        return pages;
    }
}

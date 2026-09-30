using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>
/// Splits one publish cycle's full creature-liveness list into the packet(s) that actually go
/// on the wire, and computes the digest every page of that cycle carries. Pure and
/// side-effect-free so the pagination arithmetic — how many pages, what each one holds,
/// whether the digest holds steady across all of them — can be unit-tested without a socket.
/// See docs/research/creature-liveness.md.
/// </summary>
public static class CreatureLivenessPaging
{
    /// <summary>
    /// Builds the packet(s) for one publish cycle. Always returns at least one packet — an
    /// empty <paramref name="entries"/> list still has to be SAID, the same "an empty write
    /// supersedes the last one" rule <see cref="ICoopSession.PublishEnemies"/>'s own module
    /// gate relies on elsewhere: a module gone quiet since the last cycle (every tracked
    /// creature aged out, or the host itself changed module) must clear a stale accumulated
    /// set on the receiving side rather than leave it standing forever.
    /// </summary>
    public static List<CreatureLivenessPacket> Paginate(
        long serverTimeMs, string module, uint generation, IReadOnlyList<CreatureLivenessEntry> entries,
        uint hostSessionId = 0, uint hostLoadSerial = 0, uint hostNativeEpoch = 0)
    {
        ulong digest = EntryDigest.Empty;
        foreach (CreatureLivenessEntry entry in entries)
        {
            digest ^= EntryDigest.CreatureLiveness(entry.ObjectId, entry.State);
        }

        int pageCount = Math.Max(
            1,
            (entries.Count + CreatureLivenessPacket.MaxEntriesPerPacket - 1)
                / CreatureLivenessPacket.MaxEntriesPerPacket);

        // A cycle never plans for more pages than a byte can index. A module holding enough
        // tracked creatures to need more than 255 * 96 entries in one cycle is not a real
        // case; later pages are dropped rather than overflowing PageIndex/PageCount.
        pageCount = Math.Min(pageCount, byte.MaxValue);

        List<CreatureLivenessPacket> pages = new(pageCount);
        for (int page = 0; page < pageCount; page++)
        {
            int start = page * CreatureLivenessPacket.MaxEntriesPerPacket;
            int count = Math.Max(0, Math.Min(CreatureLivenessPacket.MaxEntriesPerPacket, entries.Count - start));

            List<CreatureLivenessEntry> slice = new(count);
            for (int i = 0; i < count; i++)
            {
                slice.Add(entries[start + i]);
            }

            pages.Add(new CreatureLivenessPacket(
                serverTimeMs, module, digest, generation, (byte)page, (byte)pageCount, slice)
                { HostSessionId = hostSessionId, HostLoadSerial = hostLoadSerial, HostNativeEpoch = hostNativeEpoch });
        }

        return pages;
    }
}

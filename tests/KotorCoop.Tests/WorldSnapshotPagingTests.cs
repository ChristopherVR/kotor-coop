using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The wire-facing half of P0-2's graceful degradation: splitting one tick's full peer roster
/// into datagram-sized pages, and the client-side accumulator that reassembles them. See
/// docs/research/lag-audit.md.
/// </summary>
public class WorldSnapshotPagingTests
{
    private static readonly PlayerState SampleState =
        new(new Vector3f(1f, 2f, 3f), 0.5f, "end_m01aa", 100, 0);

    private static SnapshotEntry Entry(uint id) => new(id, $"Peer{id}", SampleState);

    [Fact]
    public void AnEmptyTickStillProducesOnePacket()
    {
        // A host still at the main menu (no local state published) still has to SAY an empty
        // roster, or a client's own prune (WorldSnapshotAssembler.TickComplete) never runs.
        List<WorldSnapshotPacket> pages = WorldSnapshotPaging.Paginate(
            1000, 1, 1, 1, 1, 0, 0, 0, 0, [], out int dropped);

        WorldSnapshotPacket page = Assert.Single(pages);
        Assert.Equal(0, page.PageIndex);
        Assert.Equal(1, page.PageCount);
        Assert.Empty(page.Entries);
        Assert.Equal(0, dropped);
    }

    [Fact]
    public void ASmallLobbyIsOnePage()
    {
        List<SnapshotEntry> entries = [Entry(1), Entry(2), Entry(3)];

        List<WorldSnapshotPacket> pages = WorldSnapshotPaging.Paginate(
            1000, 1, 1, 1, 1, 0, 0, 0, 0, entries, out int dropped);

        WorldSnapshotPacket page = Assert.Single(pages);
        Assert.Equal(1, page.PageCount);
        Assert.Equal(entries, page.Entries);
        Assert.Equal(0, dropped);
    }

    /// <summary>
    /// The scenario P0-2 exists for: a full <see cref="ProtocolConstants.MaxPeers"/> lobby,
    /// every entry at its worst-case wire size (max-length name, module and all three
    /// equipment resrefs), must all be carried across however many pages it takes — none
    /// silently dropped — and must fit comfortably inside <see cref="WorldSnapshotPacket.MaxPages"/>.
    /// </summary>
    [Fact]
    public void AFullEightPeerLobbyAtWorstCaseSizeSpansPagesWithNoneDropped()
    {
        PlayerState worstCase = SampleState with
        {
            Module = new string('m', ProtocolConstants.MaxModuleLength),
            EquippedBody = new string('b', ProtocolConstants.MaxResrefLength),
            EquippedRightWeapon = new string('r', ProtocolConstants.MaxResrefLength),
            EquippedLeftWeapon = new string('l', ProtocolConstants.MaxResrefLength),
        };

        List<SnapshotEntry> entries = [.. Enumerable.Range(1, ProtocolConstants.MaxPeers)
            .Select(i => new SnapshotEntry(
                (uint)i, new string('n', ProtocolConstants.MaxNameLength), worstCase))];

        List<WorldSnapshotPacket> pages = WorldSnapshotPaging.Paginate(
            1000, 9, 4, 7, 2,
            0x1122334455667788UL, 0x99AABBCCDDEEFF00UL, 0x0102030405060708UL, 0xFEDCBA9876543210UL,
            entries, out int dropped);

        Assert.Equal(0, dropped);
        Assert.True(pages.Count > 1, "a full worst-case lobby is expected to need more than one page");
        Assert.True(pages.Count <= WorldSnapshotPacket.MaxPages);

        // Every page actually fits in one datagram — the whole point.
        Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        int reassembled = 0;
        HashSet<uint> seenIds = [];
        for (byte i = 0; i < pages.Count; i++)
        {
            WorldSnapshotPacket page = pages[i];
            Assert.Equal(i, page.PageIndex);
            Assert.Equal((byte)pages.Count, page.PageCount);

            int length = PacketCodec.Write(buffer, page);
            Assert.True(length > 0, $"page {i} must fit inside one datagram");
            Assert.True(length <= ProtocolConstants.MaxPacketSize);

            WorldSnapshotPacket? decoded = PacketCodec.ReadWorldSnapshot(buffer[..length]);
            Assert.NotNull(decoded);
            reassembled += decoded.Entries.Count;
            foreach (SnapshotEntry entry in decoded.Entries)
            {
                Assert.True(seenIds.Add(entry.PeerId), $"peer {entry.PeerId} must appear exactly once");
            }
        }

        Assert.Equal(ProtocolConstants.MaxPeers, reassembled);

        // Paginate hands every page the real tripwire values (harmless — see
        // WorldSnapshotTripwireFieldsAreDroppedOnANonZeroPage for where the codec actually
        // enforces "page 0 only" by never putting them on the wire for a later page).
        Assert.Equal(9u, pages[0].FlagVersion);
        Assert.Equal(9u, pages[^1].FlagVersion);

        int lastPageLength = PacketCodec.Write(buffer, pages[^1]);
        WorldSnapshotPacket? decodedLastPage = PacketCodec.ReadWorldSnapshot(buffer[..lastPageLength]);
        Assert.NotNull(decodedLastPage);
        Assert.Equal(0u, decodedLastPage.FlagVersion);
    }

    [Fact]
    public void PagesBeyondMaxPagesAreDroppedAndReported()
    {
        int perPage = WorldSnapshotPacket.MaxEntriesPerPage;
        int total = (perPage * WorldSnapshotPacket.MaxPages) + 3;

        List<SnapshotEntry> entries = [.. Enumerable.Range(1, total).Select(i => Entry((uint)i))];

        List<WorldSnapshotPacket> pages = WorldSnapshotPaging.Paginate(
            1000, 1, 1, 1, 1, 0, 0, 0, 0, entries, out int dropped);

        Assert.Equal(WorldSnapshotPacket.MaxPages, pages.Count);
        Assert.Equal(3, dropped);

        int carried = pages.Sum(p => p.Entries.Count);
        Assert.Equal(perPage * WorldSnapshotPacket.MaxPages, carried);
    }

    [Fact]
    public void EveryPageButTheLastIsFullTheTailCarriesTheRemainder()
    {
        int perPage = WorldSnapshotPacket.MaxEntriesPerPage;
        int total = (perPage * 2) + 1;
        List<SnapshotEntry> entries = [.. Enumerable.Range(1, total).Select(i => Entry((uint)i))];

        List<WorldSnapshotPacket> pages = WorldSnapshotPaging.Paginate(
            1000, 1, 1, 1, 1, 0, 0, 0, 0, entries, out int dropped);

        Assert.Equal(0, dropped);
        Assert.Equal(3, pages.Count);
        Assert.Equal(perPage, pages[0].Entries.Count);
        Assert.Equal(perPage, pages[1].Entries.Count);
        Assert.Single(pages[2].Entries);
    }
}

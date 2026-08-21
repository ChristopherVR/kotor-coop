using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The wire-facing half of creature-liveness reconciliation: splitting one publish cycle's
/// full entry list into datagram-sized pages and the digest every page of that cycle carries.
/// See docs/research/creature-liveness.md.
/// </summary>
public class CreatureLivenessPagingTests
{
    [Fact]
    public void AnEmptyCycleStillProducesOnePacket()
    {
        // A module gone quiet since the last cycle must still be SAID, so a receiver's
        // accumulated set is superseded rather than left standing forever.
        List<CreatureLivenessPacket> pages = CreatureLivenessPaging.Paginate(1000, "end_m01aa", 1, []);

        CreatureLivenessPacket page = Assert.Single(pages);
        Assert.Equal(0, page.PageIndex);
        Assert.Equal(1, page.PageCount);
        Assert.Empty(page.Entries);
        Assert.Equal(EntryDigest.Empty, page.Digest);
    }

    [Fact]
    public void ASmallCycleIsOnePage()
    {
        CreatureLivenessEntry[] entries =
        [
            new CreatureLivenessEntry(0x14, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(0x35, CreatureLivenessState.Dead),
        ];

        List<CreatureLivenessPacket> pages = CreatureLivenessPaging.Paginate(1000, "end_m01aa", 1, entries);

        CreatureLivenessPacket page = Assert.Single(pages);
        Assert.Equal(1, page.PageCount);
        Assert.Equal(entries, page.Entries);
    }

    [Fact]
    public void ACycleLargerThanOnePageSplitsAcrossSeveralPacketsWithTheSameDigest()
    {
        int total = (CreatureLivenessPacket.MaxEntriesPerPacket * 2) + 5;
        List<CreatureLivenessEntry> entries = [];
        for (int i = 0; i < total; i++)
        {
            entries.Add(new CreatureLivenessEntry((uint)(i + 1), CreatureLivenessState.Alive));
        }

        List<CreatureLivenessPacket> pages = CreatureLivenessPaging.Paginate(1000, "end_m01aa", 3, entries);

        Assert.Equal(3, pages.Count);

        int reassembled = 0;
        ulong expectedDigest = pages[0].Digest;
        for (byte i = 0; i < pages.Count; i++)
        {
            CreatureLivenessPacket page = pages[i];
            Assert.Equal(i, page.PageIndex);
            Assert.Equal((byte)pages.Count, page.PageCount);
            Assert.Equal(3u, page.Generation);

            // Every page of one cycle carries the SAME digest, computed over the whole
            // cycle's entries — not just its own slice.
            Assert.Equal(expectedDigest, page.Digest);

            reassembled += page.Entries.Count;
        }

        Assert.Equal(total, reassembled);

        // Every page but the last is exactly full; the tail carries the remainder.
        Assert.Equal(CreatureLivenessPacket.MaxEntriesPerPacket, pages[0].Entries.Count);
        Assert.Equal(CreatureLivenessPacket.MaxEntriesPerPacket, pages[1].Entries.Count);
        Assert.Equal(5, pages[2].Entries.Count);
    }

    [Fact]
    public void TheDigestIsOrderIndependent()
    {
        CreatureLivenessEntry[] forward =
        [
            new CreatureLivenessEntry(1, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(2, CreatureLivenessState.Dead),
            new CreatureLivenessEntry(3, CreatureLivenessState.Gone),
        ];
        CreatureLivenessEntry[] shuffled =
        [
            new CreatureLivenessEntry(3, CreatureLivenessState.Gone),
            new CreatureLivenessEntry(1, CreatureLivenessState.Alive),
            new CreatureLivenessEntry(2, CreatureLivenessState.Dead),
        ];

        ulong a = Assert.Single(CreatureLivenessPaging.Paginate(0, "m", 1, forward)).Digest;
        ulong b = Assert.Single(CreatureLivenessPaging.Paginate(0, "m", 1, shuffled)).Digest;

        Assert.Equal(a, b);
    }

    [Fact]
    public void TheDigestChangesWhenAStateChanges()
    {
        CreatureLivenessEntry[] alive = [new CreatureLivenessEntry(1, CreatureLivenessState.Alive)];
        CreatureLivenessEntry[] dead = [new CreatureLivenessEntry(1, CreatureLivenessState.Dead)];

        ulong a = Assert.Single(CreatureLivenessPaging.Paginate(0, "m", 1, alive)).Digest;
        ulong b = Assert.Single(CreatureLivenessPaging.Paginate(0, "m", 1, dead)).Digest;

        Assert.NotEqual(a, b);
    }
}

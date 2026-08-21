using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class JournalStoreTests
{
    [Fact]
    public void MergeAdvancesAndBumpsTheVersion()
    {
        JournalStore store = new();

        Assert.True(store.Merge("k_swg_bastila", 10));
        Assert.Equal(1u, store.Version);
        Assert.True(store.Merge("k_swg_bastila", 20));
        Assert.Equal(2u, store.Version);
    }

    [Fact]
    public void MergeNeverLowersAState()
    {
        // KOTOR quest states are stage counters; walking one backwards would rewind the
        // quest for every player. A replayed or reordered packet must be a no-op.
        JournalStore store = new();
        store.Merge("quest", 30);

        Assert.False(store.Merge("quest", 10));
        Assert.False(store.Merge("quest", 30));

        List<KeyValuePair<string, int>> entries = store.Snapshot();
        Assert.Equal(30, Assert.Single(entries).Value);
        Assert.Equal(1u, store.Version);
    }

    [Fact]
    public void TagsAreCaseInsensitive()
    {
        // Odyssey resrefs compare case-insensitively; two casings must be one quest.
        JournalStore store = new();
        store.Merge("K_SWG_Bastila", 10);

        Assert.False(store.Merge("k_swg_bastila", 10));
        Assert.True(store.Merge("k_swg_bastila", 20));
        Assert.Single(store.Snapshot());
    }

    [Fact]
    public void SnapshotMergeIsMonotoneAndAdoptsTheNewestVersion()
    {
        JournalStore store = new();
        store.Merge("ahead_locally", 50);

        // A host snapshot with an older value for one entry and a new entry entirely.
        store.MergeSnapshot(
            [new("ahead_locally", 20), new("from_host", 10)], version: 7);

        List<KeyValuePair<string, int>> entries = store.Snapshot();
        Assert.Equal(50, entries.Single(e => e.Key == "ahead_locally").Value);
        Assert.Equal(10, entries.Single(e => e.Key == "from_host").Value);
        Assert.Equal(7u, store.Version);

        // A second part of the same multi-packet snapshot must not regress the version.
        store.MergeSnapshot([new("part_two", 5)], version: 7);
        Assert.Equal(7u, store.Version);
        Assert.Equal(3, store.Count);
    }

    [Fact]
    public void EmptyTagsAreRefused()
    {
        JournalStore store = new();
        Assert.False(store.Merge("", 10));
        Assert.Equal(0u, store.Version);
    }

    // ---- digest -----------------------------------------------------------------

    [Fact]
    public void DigestIsInvariantToInsertionOrder()
    {
        JournalStore forward = new();
        forward.Merge("k_swg_bastila", 10);
        forward.Merge("k_swg_carth", 20);
        forward.Merge("k_swg_juhani", 5);

        JournalStore backward = new();
        backward.Merge("k_swg_juhani", 5);
        backward.Merge("k_swg_carth", 20);
        backward.Merge("k_swg_bastila", 10);

        Assert.Equal(forward.Version, backward.Version);
        Assert.Equal(forward.Digest, backward.Digest);
    }

    [Fact]
    public void DigestIsCaseInsensitiveLikeTheStoreItself()
    {
        JournalStore store = new();
        store.Merge("K_SWG_Bastila", 10);

        JournalStore lower = new();
        lower.Merge("k_swg_bastila", 10);

        Assert.Equal(store.Digest, lower.Digest);
    }

    /// <summary>
    /// The reason the digest exists at all: two stores that reached the same VERSION by
    /// making the same number of changes, but different ones, must disagree on the digest —
    /// the tie a bare counter cannot see (docs/research/disconnect-reconnect.md §4.1).
    /// </summary>
    [Fact]
    public void EqualVersionWithDifferentContentProducesADifferentDigest()
    {
        JournalStore a = new();
        a.Merge("k_swg_bastila", 10);
        a.Merge("k_swg_carth", 20);

        JournalStore b = new();
        b.Merge("k_swg_bastila", 10);
        b.Merge("k_swg_carth", 30);

        Assert.Equal(a.Version, b.Version);
        Assert.NotEqual(a.Digest, b.Digest);
    }

    [Fact]
    public void SnapshotMergeUpdatesTheDigestNotJustTheVersion()
    {
        JournalStore store = new();
        store.Merge("ahead_locally", 50);
        ulong beforeSnapshot = store.Digest;

        store.MergeSnapshot([new("from_host", 10)], version: 7);

        JournalStore direct = new();
        direct.Merge("ahead_locally", 50);
        direct.Merge("from_host", 10);

        Assert.NotEqual(beforeSnapshot, store.Digest);
        Assert.Equal(direct.Digest, store.Digest);
    }
}

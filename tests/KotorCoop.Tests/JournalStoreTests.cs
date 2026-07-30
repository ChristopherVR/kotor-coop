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
}

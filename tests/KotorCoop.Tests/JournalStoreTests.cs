using KotorCoop.Protocol;
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
    public void MergeIsTheLocalPathAndAlwaysWinsEvenLowering()
    {
        // Merge is this instance's own deliberate local change (the engine's own
        // RemoveJournalQuestEntry/override-lower fired here, or a fresh advance) — it always
        // applies, mints a fresh outranking serial, and a redundant re-offer of the SAME
        // value is still a no-op (nothing to announce).
        JournalStore store = new();
        store.Merge("quest", 30);

        Assert.True(store.Merge("quest", 10));
        Assert.False(store.Merge("quest", 10));

        List<KeyValuePair<string, int>> entries = store.Snapshot();
        Assert.Equal(10, Assert.Single(entries).Value);
    }

    [Fact]
    public void RemoveTombstonesAnEntryAndSnapshotOmitsIt()
    {
        JournalStore store = new();
        store.Merge("quest", 30);

        Assert.True(store.Remove("quest"));
        Assert.Empty(store.Snapshot());
        Assert.Equal(0, store.Count);

        // Removing an already-removed (or never-known) tag is a no-op.
        Assert.False(store.Remove("quest"));
        Assert.False(store.Remove("never_known"));

        Assert.Contains("quest", store.RemovedTags());
    }

    [Fact]
    public void ARemovedTagCanBeReAddedByAFreshLocalMerge()
    {
        JournalStore store = new();
        store.Merge("quest", 30);
        store.Remove("quest");

        Assert.True(store.Merge("quest", 5));
        Assert.Equal(5, Assert.Single(store.Snapshot()).Value);
        Assert.DoesNotContain("quest", store.RemovedTags());
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
    public void EmptyTagsAreRefused()
    {
        JournalStore store = new();
        Assert.False(store.Merge("", 10));
        Assert.False(store.Remove(""));
        Assert.Equal(0u, store.Version);
    }

    // ---- convergence (protocol 31: Lamport clock, not a bare max) ----------------

    [Fact]
    public void ANonMonotonicRemoteDecreaseIsAcceptedWhenItIsGenuinelyNewer()
    {
        JournalStore store = new();
        Assert.True(store.ApplyRemote("quest", 30, serial: 1, originPeerId: 2, removed: false));
        Assert.True(store.ApplyRemote("quest", 10, serial: 2, originPeerId: 2, removed: false));

        Assert.Equal(10, Assert.Single(store.Snapshot()).Value);
    }

    [Fact]
    public void ARemoteRemovalIsAcceptedWhenItIsGenuinelyNewerAndPropagatesAsATombstone()
    {
        JournalStore store = new();
        Assert.True(store.ApplyRemote("quest", 30, serial: 1, originPeerId: 2, removed: false));
        Assert.True(store.ApplyRemote("quest", 0, serial: 2, originPeerId: 2, removed: true));

        Assert.Empty(store.Snapshot());
        Assert.Contains("quest", store.RemovedTags());
    }

    [Fact]
    public void AStaleReplayNeverRegressesANewerValueEvenAfterARemoval()
    {
        // The removal-specific version of the safety property: a tag removed at serial 2
        // must not be resurrected by a stale re-add carrying an OLDER serial arriving later
        // (a reconnecting peer replaying what it knew before the removal happened).
        JournalStore store = new();
        Assert.True(store.ApplyRemote("quest", 30, serial: 1, originPeerId: 2, removed: false));
        Assert.True(store.ApplyRemote("quest", 0, serial: 3, originPeerId: 2, removed: true));

        // Stale re-add at serial 2 (older than the removal's serial 3) — refused.
        Assert.False(store.ApplyRemote("quest", 30, serial: 2, originPeerId: 2, removed: false));

        Assert.Empty(store.Snapshot());
        Assert.Contains("quest", store.RemovedTags());
    }

    [Fact]
    public void ATieIsBrokenByTheLowerPeerIdIeTheHost()
    {
        JournalStore fromHostFirst = new();
        Assert.True(fromHostFirst.ApplyRemote("quest", 10, serial: 5, originPeerId: 1, removed: false));
        Assert.False(fromHostFirst.ApplyRemote("quest", 20, serial: 5, originPeerId: 2, removed: false));

        JournalStore fromClientFirst = new();
        Assert.True(fromClientFirst.ApplyRemote("quest", 20, serial: 5, originPeerId: 2, removed: false));
        Assert.True(fromClientFirst.ApplyRemote("quest", 10, serial: 5, originPeerId: 1, removed: false));

        Assert.Equal(10, Assert.Single(fromHostFirst.Snapshot()).Value);
        Assert.Equal(10, Assert.Single(fromClientFirst.Snapshot()).Value);
    }

    [Fact]
    public void SnapshotMergeAppliesPerEntryLastWriterWinsAndCarriesTombstones()
    {
        JournalStore store = new();

        // The client made its own local advance before the host's snapshot arrives — must
        // not be stomped by a snapshot entry the host stamped BEFORE that local change.
        store.Merge("ahead_locally", 50, originPeerId: 2);
        store.TryGetStamp("ahead_locally", out uint localSerial, out _);

        JournalSnapshotEntry stale = new("ahead_locally", 20, Serial: 0, OriginPeerId: 1, Removed: false);
        JournalSnapshotEntry fresh = new("from_host", 10, Serial: 1, OriginPeerId: 1, Removed: false);
        store.MergeSnapshot([stale, fresh], version: 7);

        List<KeyValuePair<string, int>> entries = store.Snapshot();
        Assert.Equal(50, entries.Single(e => e.Key == "ahead_locally").Value);
        Assert.Equal(10, entries.Single(e => e.Key == "from_host").Value);
        Assert.Equal(7u, store.Version);

        // A second part of the same multi-packet snapshot must not regress the version.
        store.MergeSnapshot([new JournalSnapshotEntry("part_two", 5, 2, 1, false)], version: 7);
        Assert.Equal(7u, store.Version);
        Assert.Equal(3, store.Count);

        // And a removal tombstone in a later snapshot part DOES override the client's own
        // stale local copy once it is genuinely newer.
        store.MergeSnapshot(
            [new JournalSnapshotEntry("ahead_locally", 0, localSerial + 1, 1, true)], version: 8);
        Assert.DoesNotContain(store.Snapshot(), e => e.Key == "ahead_locally");
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
    /// the tie a bare counter cannot see (docs/research/disconnect-reconnect.md §4.1). Still
    /// holds under the Lamport-clock scheme: the digest depends only on VALUE (and, for
    /// removed tags, on nothing at all), never on Serial/OriginPeerId.
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

    /// <summary>Divergence the digest specifically has to catch: one store removed a quest
    /// tag, the other still holds it — same change COUNT, same version, different content.
    /// This is exactly the resync tripwire docs/research/quest-sync.md's live-test script
    /// exercises for real.</summary>
    [Fact]
    public void ARemovedTagChangesTheDigestBackToWhatAnEmptyStoreWouldHave()
    {
        JournalStore removed = new();
        removed.Merge("quest", 30);
        removed.Remove("quest");

        JournalStore neverTouched = new();

        Assert.Equal(neverTouched.Digest, removed.Digest);

        JournalStore stillHoldsIt = new();
        stillHoldsIt.Merge("quest", 30);

        Assert.NotEqual(stillHoldsIt.Digest, removed.Digest);
    }

    [Fact]
    public void SnapshotMergeUpdatesTheDigestNotJustTheVersion()
    {
        JournalStore store = new();
        store.Merge("ahead_locally", 50);
        ulong beforeSnapshot = store.Digest;

        store.MergeSnapshot([new JournalSnapshotEntry("from_host", 10, 1, 1, false)], version: 7);

        JournalStore direct = new();
        direct.Merge("ahead_locally", 50);
        direct.Merge("from_host", 10);

        Assert.NotEqual(beforeSnapshot, store.Digest);
        Assert.Equal(direct.Digest, store.Digest);
    }
}

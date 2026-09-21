using KotorCoop.Protocol;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The permanent store item 11(b) (docs/research/enemy-materialisation.md §6) needed:
/// <see cref="MaterialisedLootSender"/> only tracks ops still in flight and forgets one the
/// moment every then-connected peer has acked it, so a peer that reconnects AFTER an op fully
/// retired never received it. <see cref="MaterialisedLootHistory"/> is the bounded, per-module
/// permanent record a rejoining peer's resync replays from — the same role
/// <see cref="ContainerLootStore"/> plays for container removals, keyed by owner enemy id
/// instead of a (container, item) pair.
/// </summary>
public class MaterialisedLootHistoryTests
{
    private static MaterialisedLootItem Item(string resref) =>
        new(resref, new ItemIdentity(1, Charges: 0, MaxCharges: 0, Upgrades: 0, Flags: 0));

    [Fact]
    public void EmptyHistoryHasNothingToReplay()
    {
        MaterialisedLootHistory history = new();

        Assert.Equal(0, history.Count);
        Assert.Empty(history.Snapshot());
    }

    [Fact]
    public void ARecordedDeathIsInTheSnapshot()
    {
        MaterialisedLootHistory history = new();

        history.Record(0x42, [Item("g_w_blstrpstl001")]);

        Assert.Equal(1, history.Count);
        var (ownerEnemyId, items) = Assert.Single(history.Snapshot());
        Assert.Equal(0x42u, ownerEnemyId);
        Assert.Equal("g_w_blstrpstl001", Assert.Single(items).Resref);
    }

    /// <summary>A creature dies exactly once, so a second capture for the same owner id
    /// (a death-detection loop firing twice, or a duplicate publish) must update the record in
    /// place rather than growing the history or disturbing eviction order.</summary>
    [Fact]
    public void ASecondRecordForTheSameOwnerOverwritesRatherThanDuplicates()
    {
        MaterialisedLootHistory history = new();

        history.Record(0x1, [Item("first_try")]);
        history.Record(0x1, [Item("corrected")]);

        Assert.Equal(1, history.Count);
        var (ownerEnemyId, items) = Assert.Single(history.Snapshot());
        Assert.Equal(0x1u, ownerEnemyId);
        Assert.Equal("corrected", Assert.Single(items).Resref);
    }

    [Fact]
    public void SnapshotOrderIsOldestFirst()
    {
        MaterialisedLootHistory history = new();

        history.Record(1, [Item("a")]);
        history.Record(2, [Item("b")]);
        history.Record(3, [Item("c")]);

        List<uint> order = [.. history.Snapshot().Select(entry => entry.OwnerEnemyId)];
        Assert.Equal([1u, 2u, 3u], order);
    }

    /// <summary>The bound exists so a long session's history does not grow without limit. The
    /// OLDEST death is the one evicted — a peer reconnecting soon after a death is the case this
    /// store exists for, not one from hundreds of kills ago.</summary>
    [Fact]
    public void HistoryIsCappedAndEvictsTheOldestEntry()
    {
        MaterialisedLootHistory history = new(maxEntries: 4);

        for (uint ownerEnemyId = 1; ownerEnemyId <= 5; ownerEnemyId++)
        {
            history.Record(ownerEnemyId, [Item($"item{ownerEnemyId}")]);
        }

        Assert.Equal(4, history.Count);
        List<uint> remaining = [.. history.Snapshot().Select(entry => entry.OwnerEnemyId)];
        Assert.Equal([2u, 3u, 4u, 5u], remaining);
    }

    /// <summary>Updating an already-known owner id must not itself trigger an eviction — the
    /// entry count did not actually grow.</summary>
    [Fact]
    public void OverwritingAnExistingEntryNeverEvictsAnything()
    {
        MaterialisedLootHistory history = new(maxEntries: 2);

        history.Record(1, [Item("a")]);
        history.Record(2, [Item("b")]);
        history.Record(1, [Item("a-updated")]); // still only 2 distinct owners

        Assert.Equal(2, history.Count);
        List<uint> remaining = [.. history.Snapshot().Select(entry => entry.OwnerEnemyId)];
        Assert.Equal([1u, 2u], remaining);
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        MaterialisedLootHistory history = new();

        history.Record(1, [Item("a")]);
        history.Record(2, [Item("b")]);
        history.Clear();

        Assert.Equal(0, history.Count);
        Assert.Empty(history.Snapshot());
    }

    [Fact]
    public void OwnerIdsAreIndependentAcrossModules()
    {
        MaterialisedLootHistory history = new();
        history.Record("end_m01aa", 7, [Item("end_item")]);
        history.Record("tar_m03aa", 7, [Item("tar_item")]);

        Assert.Equal(2, history.Count);
        Assert.Equal("end_item", Assert.Single(history.SnapshotForModule("end_m01aa")).Items.Single().Resref);
        Assert.Equal("tar_item", Assert.Single(history.SnapshotForModule("tar_m03aa")).Items.Single().Resref);
    }
}

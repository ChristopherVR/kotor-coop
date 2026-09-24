using KotorCoop.Memory;
using KotorCoop.Plugin;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

public class PlaceableFlagsTests
{
    [Fact]
    public void KeyRoundTripsAndCarriesModuleAndId()
    {
        uint hash = PlaceableFlags.HashModule("tar_m02aa");
        ulong key = PlaceableFlags.MakeKey(hash, 0x1234);

        Assert.True(PlaceableFlags.IsPlaceableKey(key));
        Assert.Equal(hash, PlaceableFlags.ModuleHashOf(key));
        Assert.Equal(0x1234u, PlaceableFlags.ObjectIdOf(key));
    }

    [Fact]
    public void PlaceableKeysAreDistinctFromDoorKeysAndGlobals()
    {
        uint hash = PlaceableFlags.HashModule("tar_m02aa");
        ulong placeable = PlaceableFlags.MakeKey(hash, 0x40);
        ulong door = DoorFlags.MakeKey(hash, 0x40);

        Assert.NotEqual(placeable, door);
        Assert.False(DoorFlags.IsDoorKey(placeable));
        Assert.False(PlaceableFlags.IsPlaceableKey(door));
        Assert.Equal(PlaceableFlags.TypeTag, ObjectKeyedFlags.TagOf(placeable));
        Assert.Equal(DoorFlags.TypeTag, ObjectKeyedFlags.TagOf(door));

        // The type tag in the top byte keeps every placeable key above uint.MaxValue, where
        // no global-variable flag id can live.
        Assert.True(ObjectKeyedFlags.IsObjectKeyed(placeable));
        Assert.False(PlaceableFlags.IsPlaceableKey(GlobalVariableTable.MakeFlagId(GlobalVariableType.Boolean, 0x40)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void ValueEncodesOpenAndUnlockedBits(bool open, bool unlocked)
    {
        sbyte value = PlaceableFlags.MakeValue(open, unlocked);
        Assert.Equal(open, PlaceableFlags.OpenOf(value));
        Assert.Equal(unlocked, PlaceableFlags.UnlockedOf(value));
    }

    [Fact]
    public void ValueEncodesTheDisarmedBit()
    {
        sbyte value = PlaceableFlags.MakeValue(open: false, unlocked: false, disarmed: true);
        Assert.True(PlaceableFlags.DisarmedOf(value));
        Assert.False(PlaceableFlags.OpenOf(value));
        Assert.False(PlaceableFlags.UnlockedOf(value));
    }

    [Fact]
    public void NewerStoryRelockWinsAndOldSnapshotCannotReopenIt()
    {
        // The placeable twin of DoorFlagsTests' own test of the same name: a story re-lock (or
        // a real close) reaches a peer honestly, and a stale snapshot replayed afterward cannot
        // undo it — EventFlagStore's per-key Lamport clock, the same store doors use.
        ulong key = PlaceableFlags.MakeKey(PlaceableFlags.HashModule("tar_m02ac"), 42);
        EventFlagStore owner = new();
        EventFlagStore peer = new();
        owner.Set(key, PlaceableFlags.MakeValue(open: true, unlocked: true), 1);
        var stale = owner.SnapshotEntries();
        uint staleVersion = owner.Version;
        peer.MergeSnapshot(stale, staleVersion);
        owner.Set(key, PlaceableFlags.MakeValue(open: false, unlocked: false), 1);
        peer.MergeSnapshot(owner.SnapshotEntries(), owner.Version);
        peer.MergeSnapshot(stale, staleVersion);
        Assert.True(peer.TryGet(key, out sbyte current));
        Assert.Equal(PlaceableFlags.MakeValue(open: false, unlocked: false), current);
    }

    [Fact]
    public void UnchangedDesiredStateDoesNotFightLocalStoryChanges()
    {
        // Pins PlaceableDesiredStateTracker's exact contract, mirroring
        // DoorFlagsTests.UnchangedDesiredStateDoesNotFightLocalStoryChanges: applying the same
        // (open, unlocked) tuple twice is a no-op, a genuinely new tuple re-arms it (in either
        // direction), and a module change (Clear) resets it for reused object ids — the
        // guarantee that replaces the old ContainerCloseHook.WasEverClosed permanent latch.
        PlaceableDesiredStateTracker tracker = new();
        Assert.True(tracker.IsNew(42, open: true, unlocked: true));
        tracker.Applied(42, open: true, unlocked: true);
        Assert.False(tracker.IsNew(42, open: true, unlocked: true));
        Assert.True(tracker.IsNew(42, open: false, unlocked: false));
        tracker.Applied(42, open: false, unlocked: false);
        Assert.False(tracker.IsNew(42, open: false, unlocked: false));
        // A LATER, genuinely different value — including a re-open — still goes through; this
        // is exactly what the old permanent latch could not do.
        Assert.True(tracker.IsNew(42, open: true, unlocked: true));
        tracker.Clear();
        Assert.True(tracker.IsNew(42, open: false, unlocked: false)); // New module reuses object IDs.
    }
}

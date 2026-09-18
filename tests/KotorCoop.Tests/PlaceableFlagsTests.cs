using KotorCoop.Memory;
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
    public void ValueIsMonotonicUnderTheStoreMaxMerge()
    {
        // Adding bits only ever raises the value, so a placeable that gains "unlocked" or
        // "disarmed" never regresses under a value-only merge or a stale replay.
        sbyte openOnly = PlaceableFlags.MakeValue(open: true, unlocked: false);
        sbyte openUnlocked = PlaceableFlags.MakeValue(open: true, unlocked: true);
        sbyte all = PlaceableFlags.MakeValue(open: true, unlocked: true, disarmed: true);
        Assert.True(openUnlocked > openOnly);
        Assert.True(all > openUnlocked);
    }
}

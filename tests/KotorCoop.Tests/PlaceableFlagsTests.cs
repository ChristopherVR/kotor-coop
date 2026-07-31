using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class PlaceableFlagsTests
{
    [Fact]
    public void KeyRoundTripsAndCarriesModuleAndId()
    {
        byte hash = PlaceableFlags.HashModule("tar_m02aa");
        uint key = PlaceableFlags.MakeKey(hash, 0x1234);

        Assert.True(PlaceableFlags.IsPlaceableKey(key));
        Assert.Equal(hash, PlaceableFlags.ModuleHashOf(key));
        Assert.Equal(0x1234u, PlaceableFlags.ObjectIdOf(key));
    }

    [Fact]
    public void PlaceableKeysAreDistinctFromDoorKeysAndGlobals()
    {
        uint placeable = PlaceableFlags.MakeKey(0x11, 0x40);
        uint door = DoorFlags.MakeKey(0x11, 0x40);

        Assert.NotEqual(placeable, door);
        Assert.False(DoorFlags.IsDoorKey(placeable));
        Assert.False(PlaceableFlags.IsPlaceableKey(door));

        // Top byte 0x50 keeps it out of the GlobalVariableType range the world-state apply
        // switches on (flagId >> 16 is >= 0x5000, never a small enum).
        Assert.True(placeable >> 16 >= 0x5000);
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
        // The reliable flag store keeps the max; adding bits only ever raises the value, so a
        // placeable that gains "unlocked" or "disarmed" never regresses.
        sbyte openOnly = PlaceableFlags.MakeValue(open: true, unlocked: false);
        sbyte openUnlocked = PlaceableFlags.MakeValue(open: true, unlocked: true);
        sbyte all = PlaceableFlags.MakeValue(open: true, unlocked: true, disarmed: true);
        Assert.True(openUnlocked > openOnly);
        Assert.True(all > openUnlocked);
    }
}

using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public class DoorFlagsTests
{
    [Fact]
    public void KeyRoundTripsItsParts()
    {
        uint key = DoorFlags.MakeKey(0xAB, 0x127);

        Assert.True(DoorFlags.IsDoorKey(key));
        Assert.Equal(0xAB, DoorFlags.ModuleHashOf(key));
        Assert.Equal(0x127u, DoorFlags.ObjectIdOf(key));
    }

    [Fact]
    public void KeysNeverCollideWithGlobalVariableFlagIds()
    {
        // The bridge's world-state apply switches on flagId >> 16 and expects a
        // GlobalVariableType (0..3). Door keys must land far outside that range.
        uint key = DoorFlags.MakeKey(0x00, 0x0001);
        Assert.True(key >> 16 >= 0x4400);

        key = DoorFlags.MakeKey(0xFF, 0xFFFF);
        Assert.True(DoorFlags.IsDoorKey(key));
        Assert.False(DoorFlags.IsDoorKey(GlobalVariableTable.MakeFlagId(GlobalVariableType.Boolean, 500)));
        Assert.False(DoorFlags.IsDoorKey(GlobalVariableTable.MakeFlagId(GlobalVariableType.Number, 42)));
    }

    [Theory]
    [InlineData(0, false, 0)]
    [InlineData(1, false, 1)]
    [InlineData(2, true, 6)]
    [InlineData(3, true, 7)]
    public void ValueEncodesOpenStateAndUnlock(byte open, bool unlocked, sbyte expected)
    {
        sbyte value = DoorFlags.MakeValue(open, unlocked);

        Assert.Equal(expected, value);
        Assert.Equal(open, DoorFlags.OpenStateOf(value));
        Assert.Equal(unlocked, DoorFlags.UnlockedOf(value));
    }

    [Fact]
    public void ValuesAreMonotonicUnderMaxMerge()
    {
        // The reliable flag channel merges by maximum. Every legal door progression must
        // be an increase, or it would be silently discarded.
        sbyte closedLocked = DoorFlags.MakeValue(0, false);
        sbyte closedUnlocked = DoorFlags.MakeValue(0, true);
        sbyte open = DoorFlags.MakeValue(1, true);
        sbyte destroyed = DoorFlags.MakeValue(3, true);

        Assert.True(closedUnlocked > closedLocked);
        Assert.True(open > closedUnlocked);
        Assert.True(destroyed > open);
    }

    [Fact]
    public void ModuleHashIsCaseInsensitiveAndDiscriminates()
    {
        Assert.Equal(DoorFlags.HashModule("end_m01aa"), DoorFlags.HashModule("END_M01AA"));
        Assert.NotEqual(DoorFlags.HashModule("end_m01aa"), DoorFlags.HashModule("end_m01ab"));
    }
}

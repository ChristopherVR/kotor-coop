using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

public class DoorFlagsTests
{
    [Fact]
    public void KeyRoundTripsItsParts()
    {
        uint module = DoorFlags.HashModule("tar_m03aa");
        ulong key = DoorFlags.MakeKey(module, 0x127);

        Assert.True(DoorFlags.IsDoorKey(key));
        Assert.Equal(module, DoorFlags.ModuleHashOf(key));
        Assert.Equal(0x127u, DoorFlags.ObjectIdOf(key));
    }

    [Fact]
    public void KeysNeverCollideWithGlobalVariableFlagIds()
    {
        // The bridge's world-state apply refuses anything above uint.MaxValue before it
        // casts to GlobalVariableType; a door key must always live up there, whatever the
        // module hash and object id happen to be — including a zero hash.
        ulong key = DoorFlags.MakeKey(0x0000_0000, 0x0001);
        Assert.True(ObjectKeyedFlags.IsObjectKeyed(key));
        Assert.True(key > uint.MaxValue);

        key = DoorFlags.MakeKey(0xFFFF_FFFF, 0xFFFF);
        Assert.True(DoorFlags.IsDoorKey(key));
        Assert.False(DoorFlags.IsDoorKey(GlobalVariableTable.MakeFlagId(GlobalVariableType.Boolean, 500)));
        Assert.False(DoorFlags.IsDoorKey(GlobalVariableTable.MakeFlagId(GlobalVariableType.Number, 42)));
        Assert.False(ObjectKeyedFlags.IsObjectKeyed(GlobalVariableTable.MakeFlagId(GlobalVariableType.Number, 42)));
    }

    [Fact]
    public void TheModuleHashIsCarriedInFullNotFolded()
    {
        // The whole point of protocol 53: two modules whose hashes agree in their low byte
        // (the old 8-bit fold) must still produce different keys for the same object id.
        uint a = 0x1234_5600;
        uint b = 0xABCD_EF00;
        Assert.Equal((byte)a, (byte)b);
        Assert.NotEqual(DoorFlags.MakeKey(a, 0x4A), DoorFlags.MakeKey(b, 0x4A));
        Assert.Equal(a, DoorFlags.ModuleHashOf(DoorFlags.MakeKey(a, 0x4A)));
        Assert.Equal(b, DoorFlags.ModuleHashOf(DoorFlags.MakeKey(b, 0x4A)));
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
        // Every legal door progression within a module must be an increase, so a value-only
        // merge (and a stale replay) can never move a door backwards.
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
        Assert.Equal(DoorFlags.HashModule("end_m01aa"), PlaceableFlags.HashModule("end_m01aa"));
    }
}

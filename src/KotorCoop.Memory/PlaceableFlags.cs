namespace KotorCoop.Memory;

/// <summary>
/// Encodes placeable (chest/corpse/switch/container) state onto the existing reliable
/// world-flag channel, exactly as <see cref="DoorFlags"/> does for doors, with no wire change.
///
/// Key layout matches doors so the world-state apply's <c>flagId &gt;&gt; 16</c> switch keeps
/// these out of the global-variable namespace: <c>0x50</c> ('P') in the top byte, an 8-bit
/// module-name hash next (placeable object ids reset per module), and the object id in the low
/// 16 bits (the same number in both installs, same save + deterministic GIT order).
///
/// Value is monotonic so the store's max-merge is correct: bit 0 the "opened/searched" state,
/// bit 1 set once the placeable has been unlocked. A placeable only ever advances — closed to
/// open, locked to unlocked — never the reverse.
/// </summary>
public static class PlaceableFlags
{
    private const uint TypeTag = 0x50u << 24;
    private const uint TypeMask = 0xFFu << 24;

    public const sbyte OpenBit = 1;
    public const sbyte UnlockedBit = 2;

    public static uint MakeKey(byte moduleHash, uint objectId) =>
        TypeTag | ((uint)moduleHash << 16) | (objectId & 0xFFFF);

    public static bool IsPlaceableKey(uint flagId) => (flagId & TypeMask) == TypeTag;

    public static byte ModuleHashOf(uint flagId) => (byte)(flagId >> 16);

    public static uint ObjectIdOf(uint flagId) => flagId & 0xFFFF;

    public static sbyte MakeValue(bool open, bool unlocked) =>
        (sbyte)((open ? OpenBit : 0) | (unlocked ? UnlockedBit : 0));

    public static bool OpenOf(sbyte value) => (value & OpenBit) != 0;

    public static bool UnlockedOf(sbyte value) => (value & UnlockedBit) != 0;

    /// <summary>FNV-1a over the module name, folded to 8 bits — identical to the door hash so
    /// the two channels agree on a module's byte.</summary>
    public static byte HashModule(string moduleName) => DoorFlags.HashModule(moduleName);
}

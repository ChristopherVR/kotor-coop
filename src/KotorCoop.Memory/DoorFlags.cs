namespace KotorCoop.Memory;

/// <summary>
/// Encodes door state onto the existing reliable world-flag channel, with no wire change.
///
/// Key layout (32 bits): 0x44 ('D') in the top byte keeps door keys out of the global
/// variable namespace entirely — the bridge's world-state apply switches on
/// <c>flagId &gt;&gt; 16</c>, which for a door key is at least 0x4400 and never matches a
/// <c>GlobalVariableType</c>. The next byte is an 8-bit hash of the module name, because
/// door object ids come from a counter that resets on every module transition, so two
/// modules routinely contain a door with the same id; without the module byte, opening a
/// door in one module could open an unrelated door in another. The low 16 bits are the
/// door's object id, which is the same number in both instances (same save, deterministic
/// id assignment in GIT order).
///
/// Value layout (monotonic by construction, so the store's max-merge is correct): bits
/// 0-1 the engine's open state (0 closed, 1/2 open, 3 destroyed), bit 2 set once the
/// door has been unlocked. A door only ever advances: closed to open, locked to
/// unlocked, anything to destroyed. Closing and re-locking are deliberately not
/// replicated — the same latch semantics the quest globals use.
/// </summary>
public static class DoorFlags
{
    private const uint TypeTag = 0x44u << 24;
    private const uint TypeMask = 0xFFu << 24;

    public const sbyte UnlockedBit = 4;

    public static uint MakeKey(byte moduleHash, uint objectId) =>
        TypeTag | ((uint)moduleHash << 16) | (objectId & 0xFFFF);

    public static bool IsDoorKey(uint flagId) => (flagId & TypeMask) == TypeTag;

    public static byte ModuleHashOf(uint flagId) => (byte)(flagId >> 16);

    public static uint ObjectIdOf(uint flagId) => flagId & 0xFFFF;

    public static sbyte MakeValue(byte openState, bool unlocked) =>
        (sbyte)((openState & 3) | (unlocked ? UnlockedBit : 0));

    public static byte OpenStateOf(sbyte value) => (byte)(value & 3);

    public static bool UnlockedOf(sbyte value) => (value & UnlockedBit) != 0;

    /// <summary>FNV-1a over the module name, folded to 8 bits.</summary>
    public static byte HashModule(string moduleName)
    {
        uint hash = 2166136261;
        foreach (char c in moduleName)
        {
            hash = (hash ^ (byte)char.ToLowerInvariant(c)) * 16777619;
        }

        return (byte)(hash ^ (hash >> 8) ^ (hash >> 16) ^ (hash >> 24));
    }
}

namespace KotorCoop.Memory;

/// <summary>
/// Encodes door state onto the existing reliable world-flag channel.
///
/// Key layout: the shared 64-bit object-keyed layout in <see cref="ObjectKeyedFlags"/> with
/// 0x44 ('D') as the type tag — tag in the top byte, the full 32-bit FNV-1a of the module name
/// next, the door's object id in the low 24 bits. The module part exists because door object
/// ids come from a counter that resets on every module transition, so two modules routinely
/// contain a door with the same id; the old 8-bit fold of the module hash made two modules
/// share a byte with near certainty over a long game, at which point opening a door in one
/// module opened an unrelated door in the other (see <see cref="ObjectKeyedFlags"/>'s remarks).
/// The object id is the same number in both instances (same save, deterministic id assignment
/// in GIT order).
///
/// Value layout (monotonic by construction): bits 0-1 the engine's open state (0 closed, 1/2
/// open, 3 destroyed), bit 2 set once the door has been unlocked. A door only ever advances:
/// closed to open, locked to unlocked, anything to destroyed. Closing and re-locking are
/// deliberately not replicated — the same latch semantics the quest globals use.
/// </summary>
public static class DoorFlags
{
    public const byte TypeTag = 0x44;

    public const sbyte UnlockedBit = 4;

    public static ulong MakeKey(uint moduleHash, uint objectId) =>
        ObjectKeyedFlags.MakeKey(TypeTag, moduleHash, objectId);

    public static bool IsDoorKey(ulong flagId) =>
        ObjectKeyedFlags.IsObjectKeyed(flagId) && ObjectKeyedFlags.TagOf(flagId) == TypeTag;

    public static uint ModuleHashOf(ulong flagId) => ObjectKeyedFlags.ModuleHashOf(flagId);

    public static uint ObjectIdOf(ulong flagId) => ObjectKeyedFlags.ObjectIdOf(flagId);

    public static sbyte MakeValue(byte openState, bool unlocked) =>
        (sbyte)((openState & 3) | (unlocked ? UnlockedBit : 0));

    public static byte OpenStateOf(sbyte value) => (byte)(value & 3);

    public static bool UnlockedOf(sbyte value) => (value & UnlockedBit) != 0;

    /// <summary>The full 32-bit FNV-1a of the module name, case-folded.</summary>
    public static uint HashModule(string moduleName) => ObjectKeyedFlags.HashModule(moduleName);
}

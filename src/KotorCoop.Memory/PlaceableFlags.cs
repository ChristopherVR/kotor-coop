namespace KotorCoop.Memory;

/// <summary>
/// Encodes placeable (chest/corpse/switch/container) state onto the existing reliable
/// world-flag channel, exactly as <see cref="DoorFlags"/> does for doors.
///
/// Key layout matches doors — the shared 64-bit layout in <see cref="ObjectKeyedFlags"/> —
/// with <c>0x50</c> ('P') as the type tag: tag in the top byte, the full 32-bit module-name
/// hash next (placeable object ids reset per module), the object id in the low 24 bits (the
/// same number in both installs, same save + deterministic GIT order).
///
/// Value layout, since the door-relock model was extended to placeables: bit 0 is the
/// CURRENT open/closed state, bit 1 the CURRENT unlocked state — both applied both directions,
/// ordered by the flag store's Lamport serial (host tie-break), exactly like
/// <see cref="DoorFlags"/>'s bits 0-2. A story <c>SetLocked(placeable, TRUE)</c> or a real
/// container close now reaches every peer the same way a door re-lock does; zero is a
/// meaningful closed-and-locked state, not "nothing happened yet". Bit 2 (the disarmed bit)
/// is UNCHANGED by that pass and stays forward-only: nothing in the shipped content ever
/// re-arms a trap, so <see cref="KotorCoop.Memory.KotorGameBridge.CollectChangedPlaceableState"/>
/// still reports it only once, relative to the module's own baseline.
/// </summary>
public static class PlaceableFlags
{
    public const byte TypeTag = 0x50;

    public const sbyte OpenBit = 1;
    public const sbyte UnlockedBit = 2;
    public const sbyte DisarmedBit = 4;

    public static ulong MakeKey(uint moduleHash, uint objectId) =>
        ObjectKeyedFlags.MakeKey(TypeTag, moduleHash, objectId);

    public static bool IsPlaceableKey(ulong flagId) =>
        ObjectKeyedFlags.IsObjectKeyed(flagId) && ObjectKeyedFlags.TagOf(flagId) == TypeTag;

    public static uint ModuleHashOf(ulong flagId) => ObjectKeyedFlags.ModuleHashOf(flagId);

    public static uint ObjectIdOf(ulong flagId) => ObjectKeyedFlags.ObjectIdOf(flagId);

    public static sbyte MakeValue(bool open, bool unlocked, bool disarmed = false) =>
        (sbyte)((open ? OpenBit : 0) | (unlocked ? UnlockedBit : 0) | (disarmed ? DisarmedBit : 0));

    public static bool OpenOf(sbyte value) => (value & OpenBit) != 0;

    public static bool UnlockedOf(sbyte value) => (value & UnlockedBit) != 0;

    public static bool DisarmedOf(sbyte value) => (value & DisarmedBit) != 0;

    /// <summary>Identical to the door hash so the two channels agree on a module's hash.</summary>
    public static uint HashModule(string moduleName) => ObjectKeyedFlags.HashModule(moduleName);
}

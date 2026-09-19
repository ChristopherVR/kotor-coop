namespace KotorCoop.Memory;

/// <summary>
/// Encodes floor-mine / trap-trigger state onto the existing reliable world-flag channel,
/// exactly as <see cref="DoorFlags"/> and <see cref="PlaceableFlags"/> do for doors and
/// placeables. Key layout is the shared 64-bit <see cref="ObjectKeyedFlags"/> layout with
/// <see cref="ObjectKeyedFlags.TriggerTag"/> (0x54, 'T') as the type tag: the full 32-bit
/// module-name hash, then the object id in the low 24 bits (a GIT-placed mine gets the same id
/// on both instances, same save and deterministic GIT order, like a door).
///
/// The value is a monotonic "removed" bit: a mine only ever goes from present to gone. The
/// engine has NO "disarmed" or "detonated" byte on a trap trigger — every way a floor mine
/// stops mattering (skill disarm, recover, key disarm, a one-shot detonation) ends the same
/// way, with the engine posting event 11 (DestroyObject) at the trigger, whose handler runs
/// <c>CSWSTrigger::RemoveFromArea</c> and deletes it. So "the mine is gone" is the one fact
/// worth carrying. See docs/research/mines.md.
/// </summary>
public static class MineFlags
{
    public const byte TypeTag = ObjectKeyedFlags.TriggerTag;

    /// <summary>The trigger no longer exists on the instance that published this flag.</summary>
    public const sbyte RemovedBit = 1;

    public static ulong MakeKey(uint moduleHash, uint objectId) =>
        ObjectKeyedFlags.MakeKey(TypeTag, moduleHash, objectId);

    public static bool IsMineKey(ulong flagId) =>
        ObjectKeyedFlags.IsObjectKeyed(flagId) && ObjectKeyedFlags.TagOf(flagId) == TypeTag;

    public static uint ModuleHashOf(ulong flagId) => ObjectKeyedFlags.ModuleHashOf(flagId);

    public static uint ObjectIdOf(ulong flagId) => ObjectKeyedFlags.ObjectIdOf(flagId);

    public static sbyte MakeValue(bool removed) => removed ? RemovedBit : (sbyte)0;

    public static bool RemovedOf(sbyte value) => (value & RemovedBit) != 0;

    /// <summary>Identical to the door and placeable hash so every channel agrees on a module's hash.</summary>
    public static uint HashModule(string moduleName) => ObjectKeyedFlags.HashModule(moduleName);
}

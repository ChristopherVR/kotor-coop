// Placeable sync: chests, corpses, switches and containers. The door channel's twin — same
// object-id keying, same reliable max-merge flag transport, same silent module-entry baseline.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- placeables ------------------------------------------------------------
    //
    // Chests, corpses, switches, containers. The door channel's twin: same object-id keying,
    // same reliable max-merge flag transport (tag 0x50), same silent baseline on module entry.
    // See docs/research/placeables-containers.md.

    /// <summary>CSWSPlaceable vtable; every 4-byte occurrence in private memory is a placeable.</summary>
    public const uint PlaceableVtable = 0x007494D0;

    /// <summary>CSWSPlaceable + this = locked int (nonzero = locked).</summary>
    private const int PlaceableLockedField = 0x260;

    /// <summary>CSWSPlaceable + this = open int (nonzero = opened/searched).</summary>
    private const int PlaceableOpenField = 0x338;

    /// <summary>CSWSPlaceable + this = trap int (nonzero = trapped; cleared = disarmed).</summary>
    private const int PlaceableTrapField = 0x278;

    // Placeable slots in the plugin's SharedBlock, version 11.
    private const int BlockPlaceableCount = 0xB58;
    private const int BlockPlaceableSlots = 0xB60;
    private const int BlockPlaceableStride = 0x08;
    private const int BlockPlaceableCapacity = 32;

    private readonly Dictionary<uint, (byte Open, byte Locked, byte Trapped)> _placeableShadow = [];
    private readonly Dictionary<uint, byte> _placeableBaselineLocked = [];
    private readonly Dictionary<uint, byte> _placeableBaselineTrapped = [];
    private string _placeableModule = string.Empty;
    private uint _placeableModuleHash;
    private bool _placeableBaselined;
    private long _nextPlaceableScanTicks;
    private int _placeableSlotCursor;

    public bool SupportsPlaceableSync => _pluginBlock != 0;

    /// <summary>
    /// Diffs the local module's placeables against the last scan and reports what advanced
    /// (opened, unlocked) as placeable-typed world flags. First scan per module is a silent
    /// baseline; scans throttled to one per second like doors.
    /// </summary>
    public List<KeyValuePair<ulong, sbyte>> CollectChangedPlaceableState()
    {
        List<KeyValuePair<ulong, sbyte>> changed = [];

        if (!EngineGlobals.TryReadModuleName(_memory, out string module))
        {
            return changed;
        }

        if (!string.Equals(module, _placeableModule, StringComparison.OrdinalIgnoreCase))
        {
            _placeableModule = module;
            _placeableModuleHash = PlaceableFlags.HashModule(module);
            _placeableShadow.Clear();
            _placeableBaselineLocked.Clear();
            _placeableBaselineTrapped.Clear();
            _placeableBaselined = false;
            _nextPlaceableScanTicks = 0;
            _placeableSlotCursor = 0;
        }

        long now = Environment.TickCount64;
        if (now < _nextPlaceableScanTicks)
        {
            return changed;
        }

        _nextPlaceableScanTicks = now + 1000;

        foreach (nint address in CachedScan(PlaceableVtable, 512, ObjectScanCacheMs))
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                || objectId is 0 or > 0xFFFF
                || !_memory.TryReadInt32(address + PlaceableOpenField, out int openInt)
                || !_memory.TryReadInt32(address + PlaceableLockedField, out int lockedInt)
                || !_memory.TryReadInt32(address + PlaceableTrapField, out int trapInt))
            {
                continue;
            }

            byte open = openInt != 0 ? (byte)1 : (byte)0;
            byte locked = lockedInt != 0 ? (byte)1 : (byte)0;
            byte trapped = trapInt != 0 ? (byte)1 : (byte)0;

            if (!_placeableBaselineLocked.ContainsKey(objectId))
            {
                _placeableBaselineLocked[objectId] = locked;
                _placeableBaselineTrapped[objectId] = trapped;
            }

            if (_placeableShadow.TryGetValue(objectId, out (byte Open, byte Locked, byte Trapped) last)
                && last.Open == open && last.Locked == locked && last.Trapped == trapped)
            {
                continue;
            }

            bool firstSighting = !_placeableShadow.ContainsKey(objectId);
            _placeableShadow[objectId] = (open, locked, trapped);

            if (!_placeableBaselined && firstSighting)
            {
                continue;
            }

            bool unlockedDuringPlay = locked == 0 && _placeableBaselineLocked[objectId] != 0;
            bool disarmedDuringPlay = trapped == 0 && _placeableBaselineTrapped.GetValueOrDefault(objectId) != 0;
            changed.Add(new KeyValuePair<ulong, sbyte>(
                PlaceableFlags.MakeKey(_placeableModuleHash, objectId),
                PlaceableFlags.MakeValue(open != 0, unlockedDuringPlay, disarmedDuringPlay)));
        }

        _placeableBaselined = true;
        return changed;
    }

    /// <summary>
    /// Publishes the session's placeable truth for the local module into the plugin, which
    /// applies each as a field write on the game's thread. Flags for other modules are held
    /// back until that module loads.
    /// </summary>
    public bool TryWritePlaceableStates(IReadOnlyList<KeyValuePair<ulong, sbyte>> flags)
    {
        if (_pluginBlock == 0 || _placeableModule.Length == 0)
        {
            return false;
        }

        bool ok = true;
        int slot = 0;

        // Mine removals get a small reserved slice first. MineSlotPlanner rotates that slice
        // across a larger backlog, so a module with 32+ placeables cannot starve a mine while
        // the ordinary placeable state still receives the remaining slots.
        ok &= AppendMineSlots(flags, ref slot);
        HashSet<uint> selectedPlaceables = [.. MineSlotPlanner.SelectPlaceables(
            flags, _placeableModuleHash, ref _placeableSlotCursor, BlockPlaceableCapacity - slot)];

        foreach (KeyValuePair<ulong, sbyte> flag in flags)
        {
            if (slot >= BlockPlaceableCapacity)
            {
                break;
            }

            if (!PlaceableFlags.IsPlaceableKey(flag.Key)
                || PlaceableFlags.ModuleHashOf(flag.Key) != _placeableModuleHash
                || flag.Value <= 0)
            {
                continue;
            }

            if (!selectedPlaceables.Contains(PlaceableFlags.ObjectIdOf(flag.Key)))
            {
                continue;
            }

            nint address = _pluginBlock + BlockPlaceableSlots + (slot * BlockPlaceableStride);
            ok &= _memory.TryWrite(address + 0x04, PlaceableFlags.OpenOf(flag.Value) ? (byte)1 : (byte)0);
            ok &= _memory.TryWrite(address + 0x05, PlaceableFlags.UnlockedOf(flag.Value) ? (byte)1 : (byte)0);
            ok &= _memory.TryWrite(address + 0x06, PlaceableFlags.DisarmedOf(flag.Value) ? (byte)1 : (byte)0);

            // Slot kind: 0 = placeable. Byte +7 was padding before floor-mine sync started
            // sharing this list (docs/research/mines.md §6); zeroed so a slot that once held a
            // mine entry is never read as one.
            ok &= _memory.TryWrite(address + 0x07, (byte)0);

            // Object id last, so the plugin never reads a slot whose id and payload disagree.
            ok &= _memory.TryWrite(address + 0x00, PlaceableFlags.ObjectIdOf(flag.Key));
            slot++;
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockPlaceableCount, (uint)slot);
        return ok;
    }

}

// Placeable sync: chests, corpses, switches and containers. The door channel's twin — same
// object-id keying, same Lamport-ordered flag transport, same silent module-entry baseline.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- placeables ------------------------------------------------------------
    //
    // Chests, corpses, switches, containers. The door channel's twin: same object-id keying,
    // same reliable Lamport-ordered flag transport (tag 0x50, EventFlagStore), same silent
    // baseline on module entry, and — since the door-relock model was extended here — the same
    // bidirectional open/locked state (docs/research/door-relock.md §6). See
    // docs/research/placeables-containers.md.

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
    private readonly Dictionary<uint, uint> _placeableApplyAcknowledgements = [];
    private readonly Dictionary<uint, uint> _placeableLocalChangeGenerations = [];
    private readonly Dictionary<uint, (byte Value, bool Published)> _placeablePendingLocalChanges = [];
    private readonly Dictionary<uint, byte> _placeableBaselineTrapped = [];
    private string _placeableModule = string.Empty;
    private uint _placeableModuleHash;
    private uint _placeableNativeEpoch;
    private bool _placeableBaselined;
    private bool _publishPlaceableBaseline;
    private long _nextPlaceableScanTicks;
    private int _placeableSlotCursor;

    public bool SupportsPlaceableSync => _pluginBlock != 0;

    /// <summary>
    /// Diffs the local module's placeables against the last scan and reports the CURRENT
    /// open/locked state (either direction — a story re-lock or a real close included) plus
    /// whatever advanced on the disarm bit, as placeable-typed world flags. First scan per
    /// module is a silent baseline; scans throttled to one per second like doors.
    /// </summary>
    public List<KeyValuePair<ulong, sbyte>> CollectChangedPlaceableState()
    {
        List<KeyValuePair<ulong, sbyte>> changed = [];

        if (!EngineGlobals.TryReadModuleName(_memory, out string module))
        {
            return changed;
        }

        if (!TryReadLivenessEpoch(out uint nativeEpoch)) return changed;
        EnsurePlaceableWorld(module, nativeEpoch);

        long now = Environment.TickCount64;
        if (now < _nextPlaceableScanTicks)
        {
            return changed;
        }

        _nextPlaceableScanTicks = now + 1000;
        ConsumePlaceableApplyAcknowledgements();
        ConsumeLocalPlaceableChanges();

        if (!EngineGlobals.TryGetServerObjectArray(_memory, out nint objectArray))
        {
            return changed;
        }

        foreach (nint address in CachedScan(PlaceableVtable, 512, ObjectScanCacheMs))
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                || objectId is 0 or > 0xFFFF
                // A scan also sees intact freed placeables from the previous world.
                // Publishing their old locks can undo a real close/unlock immediately.
                || !EngineGlobals.IsCurrentServerObject(_memory, objectArray, objectId, address)
                || !_memory.TryReadInt32(address + PlaceableOpenField, out int openInt)
                || !_memory.TryReadInt32(address + PlaceableLockedField, out int lockedInt)
                || !_memory.TryReadInt32(address + PlaceableTrapField, out int trapInt))
            {
                continue;
            }

            byte open = openInt != 0 ? (byte)1 : (byte)0;
            byte locked = lockedInt != 0 ? (byte)1 : (byte)0;
            byte trapped = trapInt != 0 ? (byte)1 : (byte)0;

            if (!_placeableBaselineTrapped.ContainsKey(objectId))
            {
                _placeableBaselineTrapped[objectId] = trapped;
            }

            if (_placeablePendingLocalChanges.TryGetValue(objectId, out var local))
            {
                byte localOpen = (byte)(local.Value & 1);
                // The retained event authors only the open/close edge. A later
                // story lock or disarm is independent and must use its current
                // native value, rather than the earlier interaction's tuple.
                (byte Open, byte Locked, byte Trapped) prior = _placeableShadow.GetValueOrDefault(objectId, (localOpen, locked, trapped));
                bool publish = PlaceableLocalChangeRule.ShouldPublish(local.Published,
                    prior.Locked, prior.Trapped, locked, trapped);
                if (publish)
                {
                    _placeableShadow[objectId] = (localOpen, locked, trapped);
                    byte authored = PlaceableLocalChangeRule.MergeOpenWithCurrentState(local.Value, locked, trapped);
                    bool disarmed = (authored & 4) == 0 && _placeableBaselineTrapped.GetValueOrDefault(objectId) != 0;
                    changed.Add(new(PlaceableFlags.MakeKey(_placeableModuleHash, objectId),
                        PlaceableFlags.MakeValue((authored & 1) != 0, (authored & 2) != 0, disarmed)));
                    _placeablePendingLocalChanges[objectId] = (local.Value, true);
                    continue;
                }
                // A delayed canonical open must not manufacture a new local open
                // after the genuine close. Release protection only once both the
                // native object and canonical desired state agree with the edge.
                if (!PlaceableLocalChangeRule.MayRelease(local.Published, open,
                    CanonicalPlaceableOpenMatches(objectId, localOpen), localOpen)) continue;
                _placeablePendingLocalChanges.Remove(objectId);
            }

            if (_placeableShadow.TryGetValue(objectId, out (byte Open, byte Locked, byte Trapped) last)
                && last.Open == open && last.Locked == locked && last.Trapped == trapped)
            {
                continue;
            }

            bool firstSighting = !_placeableShadow.ContainsKey(objectId);
            _placeableShadow[objectId] = (open, locked, trapped);

            if (!_placeableBaselined && firstSighting && !_publishPlaceableBaseline)
            {
                continue;
            }

            // Lamport ordering in EventFlagStore (the same store doors use) permits a story
            // re-lock or a real close to reach the peer honestly, the same fix 934c18e made for
            // doors: carry the CURRENT open/locked state, not "has this ever advanced". Disarm
            // stays relative to the module's own baseline — nothing shipped ever re-arms a trap,
            // so there is nothing to carry both ways there.
            bool disarmedDuringPlay = trapped == 0 && _placeableBaselineTrapped.GetValueOrDefault(objectId) != 0;
            changed.Add(new KeyValuePair<ulong, sbyte>(
                PlaceableFlags.MakeKey(_placeableModuleHash, objectId),
                PlaceableFlags.MakeValue(open != 0, locked == 0, disarmedDuringPlay)));
        }

        _placeableBaselined = true;
        _publishPlaceableBaseline = false;
        return changed;
    }

    private void EnsurePlaceableWorld(string module, uint nativeEpoch)
    {
        if (nativeEpoch == _placeableNativeEpoch
            && string.Equals(module, _placeableModule, StringComparison.OrdinalIgnoreCase)) return;
        _placeableModule = module;
        _placeableNativeEpoch = nativeEpoch;
        _placeableModuleHash = PlaceableFlags.HashModule(module);
        _placeableShadow.Clear();
        _placeableApplyAcknowledgements.Clear();
        _placeableLocalChangeGenerations.Clear();
        _placeablePendingLocalChanges.Clear();
        _placeableBaselineTrapped.Clear();
        _placeableBaselined = false;
        _nextPlaceableScanTicks = 0;
        _placeableSlotCursor = 0;
    }

    private void ConsumePlaceableApplyAcknowledgements()
    {
        if (_pluginBlock == 0) return;
        for (int slot = 0; slot < BlockPlaceableCapacity; slot++)
        {
            nint row = _pluginBlock + 0xB800 + slot * 12;
            if (!_memory.TryReadUInt32(row + 8, out uint generation) || generation == 0
                || !_memory.TryReadUInt32(row, out uint id) || id is 0 or > 0xFFFF
                || !_memory.TryReadUInt32(row + 4, out uint value) || value > 7
                || !_memory.TryReadUInt32(row + 8, out uint afterGeneration) || generation != afterGeneration
                || !_memory.TryReadUInt32(row, out uint afterId) || id != afterId
                || (_placeableApplyAcknowledgements.TryGetValue(id, out uint previous) && previous == generation)) continue;
            _placeableApplyAcknowledgements[id] = generation;
            _placeableShadow[id] = ((byte)(value & 1), (byte)((value & 2) == 0 ? 1 : 0), (byte)((value & 4) != 0 ? 1 : 0));
        }
    }

    private void ConsumeLocalPlaceableChanges()
    {
        if (_pluginBlock == 0) return;
        for (int slot = 0; slot < BlockPlaceableCapacity; slot++)
        {
            nint row = _pluginBlock + 0xBA00 + slot * 12;
            if (!_memory.TryReadUInt32(row + 8, out uint generation) || generation == 0
                || !_memory.TryReadUInt32(row, out uint id) || id is 0 or > 0xFFFF
                || !_memory.TryReadUInt32(row + 4, out uint value) || value > 7
                || !_memory.TryReadUInt32(row + 8, out uint afterGeneration) || generation != afterGeneration
                || !_memory.TryReadUInt32(row, out uint afterId) || id != afterId
                || (_placeableLocalChangeGenerations.TryGetValue(id, out uint previous) && previous == generation)) continue;
            _placeableLocalChangeGenerations[id] = generation;
            _placeablePendingLocalChanges[id] = ((byte)value, false);
        }
    }

    private bool CanonicalPlaceableOpenMatches(uint id, byte open)
    {
        if (!_memory.TryReadUInt32(_pluginBlock + BlockPlaceableCount, out uint count)) return false;
        for (int slot = 0; slot < Math.Min(count, BlockPlaceableCapacity); slot++)
        {
            nint row = _pluginBlock + BlockPlaceableSlots + slot * BlockPlaceableStride;
            if (_memory.TryReadUInt32(row, out uint desiredId) && desiredId == id
                && _memory.TryRead(row + 7, out byte kind) && kind == 0
                && _memory.TryRead(row + 4, out byte desiredOpen)) return desiredOpen == open;
        }
        return false;
    }

    /// <summary>
    /// Publishes the session's placeable truth for the local module into the plugin, which
    /// applies each through the engine's own open/close/lock calls on the game's thread — open
    /// and locked both apply in either direction now (a story re-lock or close reaches a peer
    /// the same way a door's does). Flags for other modules are held back until that module
    /// loads.
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
                || flag.Value is < 0 or > 7)
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

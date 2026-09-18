// Door sync: reading each door's open/locked state, tracking a per-module shadow of what has
// already been published, and writing changed door flags to the plugin.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- doors -----------------------------------------------------------------

    /// <summary>CSWSDoor vtable; every 4-byte occurrence in private memory is a door.</summary>
    public const uint DoorVtable = 0x007498B0;

    /// <summary>CSWSDoor + this = committed open state: 0 closed, 1/2 open, 3 destroyed.</summary>
    private const int DoorOpenState = 0x2CC;

    /// <summary>CSWSDoor + this = locked byte.</summary>
    private const int DoorLockedByte = 0x2C4;

    // Door slots in the plugin's SharedBlock, version 4.
    private const int BlockDoorCount = 0x690;
    private const int BlockDoorSlots = 0x698;
    private const int BlockDoorStride = 0x08;
    private const int BlockDoorCapacity = 32;

    /// <summary>Last observed (open, locked) per door id, in the current module.</summary>
    private readonly Dictionary<uint, (byte Open, byte Locked)> _doorShadow = [];

    /// <summary>Lock state each door had when first seen, so "was unlocked during play"
    /// can be told apart from "ships unlocked" — only the former is worth announcing.</summary>
    private readonly Dictionary<uint, byte> _doorBaselineLocked = [];

    private string _doorModule = string.Empty;
    private uint _doorModuleHash;
    private bool _doorBaselined;
    private long _nextDoorScanTicks;

    /// <summary>Door sync needs the plugin to apply swings on the game's thread.</summary>
    public bool SupportsDoorSync => _pluginBlock != 0;

    /// <summary>
    /// Diffs the local module's doors against the last scan and reports what advanced, as
    /// door-typed world flags. The first scan after entering a module is a silent
    /// baseline, exactly like the globals: pre-existing state is not worth announcing,
    /// only changes made while the session is live. Scans are throttled to one per
    /// second, since they walk the whole address space by vtable.
    /// </summary>
    public List<KeyValuePair<ulong, sbyte>> CollectChangedDoorState()
    {
        List<KeyValuePair<ulong, sbyte>> changed = [];

        if (!EngineGlobals.TryReadModuleName(_memory, out string module))
        {
            return changed;
        }

        if (!string.Equals(module, _doorModule, StringComparison.OrdinalIgnoreCase))
        {
            _doorModule = module;
            _doorModuleHash = DoorFlags.HashModule(module);
            _doorShadow.Clear();
            _doorBaselineLocked.Clear();
            _doorBaselined = false;
            _nextDoorScanTicks = 0;
        }

        long now = Environment.TickCount64;
        if (now < _nextDoorScanTicks)
        {
            return changed;
        }

        _nextDoorScanTicks = now + 1000;

        foreach (nint address in CachedScan(DoorVtable, 256, ObjectScanCacheMs))
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                || objectId is 0 or > 0xFFFF
                || !_memory.TryRead(address + DoorOpenState, out byte open)
                || !_memory.TryRead(address + DoorLockedByte, out byte locked))
            {
                continue;
            }

            if (!_doorBaselineLocked.ContainsKey(objectId))
            {
                _doorBaselineLocked[objectId] = locked;
            }

            if (_doorShadow.TryGetValue(objectId, out (byte Open, byte Locked) last)
                && last.Open == open && last.Locked == locked)
            {
                continue;
            }

            bool firstSighting = !_doorShadow.ContainsKey(objectId);
            _doorShadow[objectId] = (open, locked);

            if (!_doorBaselined && firstSighting)
            {
                continue;
            }

            // The value is absolute; the store's max-merge discards anything that is not
            // an advance, so a door closing again simply produces a no-op.
            bool unlockedDuringPlay = locked == 0 && _doorBaselineLocked[objectId] != 0;
            changed.Add(new KeyValuePair<ulong, sbyte>(
                DoorFlags.MakeKey(_doorModuleHash, objectId),
                DoorFlags.MakeValue(open, unlockedDuringPlay)));
        }

        _doorBaselined = true;
        return changed;
    }

    /// <summary>
    /// Publishes the session's door truth for the LOCAL module into the plugin, which
    /// applies it through the engine's own door paths. Door flags for other modules are
    /// held back; they apply when their module is entered and this runs again.
    /// </summary>
    public bool TryWriteDoorStates(IReadOnlyList<KeyValuePair<ulong, sbyte>> flags)
    {
        if (_pluginBlock == 0 || _doorModule.Length == 0)
        {
            return false;
        }

        bool ok = true;
        int slot = 0;

        foreach (KeyValuePair<ulong, sbyte> flag in flags)
        {
            if (slot >= BlockDoorCapacity)
            {
                break;
            }

            if (!DoorFlags.IsDoorKey(flag.Key)
                || DoorFlags.ModuleHashOf(flag.Key) != _doorModuleHash
                || flag.Value <= 0)
            {
                continue;
            }

            nint address = _pluginBlock + BlockDoorSlots + (slot * BlockDoorStride);
            ok &= _memory.TryWrite(address + 0x04, DoorFlags.OpenStateOf(flag.Value));
            ok &= _memory.TryWrite(address + 0x05, DoorFlags.UnlockedOf(flag.Value) ? (byte)1 : (byte)0);

            // Object id last, so the plugin never reads a slot whose id and payload
            // belong to different doors.
            ok &= _memory.TryWrite(address + 0x00, DoorFlags.ObjectIdOf(flag.Key));
            slot++;
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockDoorCount, (uint)slot);
        return ok;
    }

}

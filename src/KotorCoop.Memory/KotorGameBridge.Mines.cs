// Floor-mine sync (coop.ini mine_sync). The placeable channel's cousin for CSWSTrigger objects
// that are traps: same object-id keying, same reliable flag transport (tag 0x54), same silent
// baseline on module entry. See docs/research/mines.md.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- floor mines -----------------------------------------------------------
    //
    // A floor mine is a CSWSTrigger (UTT Type == 2) whose +0x2BC gate is non-zero. It has no
    // armed/disarmed byte: every way it stops mattering ends with the engine deleting the
    // trigger (event 11), so this side reads ABSENCE, guarded by MineVanishTracker, and the
    // apply side (plugin MineController) destroys the peer's copy through the engine's own
    // DestroyObject. The apply half rides the placeable slot list (slot kind byte +7 == 1), so
    // the shared block layout is unchanged.

    /// <summary>CSWSTrigger vtable (tools/q.py: CSWSTrigger vtable 0x00749A38, size 0x318).</summary>
    public const uint TriggerVtable = 0x00749A38;

    /// <summary>CSWSTrigger + this = trap/mine gate (nonzero = mine). Set by LoadTrigger for UTT
    /// Type == 2 (0x0058E2D0) and by CSWSCreature::AIActionSetMine for a placed mine
    /// (0x0051A696); read by InTrigger (0x0058CE40) and EventHandler (0x0058F4E4).</summary>
    private const int TriggerTrapField = 0x2BC;

    /// <summary>CSWTrigger + this = "placed by the player's party" (nonzero = a mine the party laid
    /// with AIActionSetMine). Written by CSWSTrigger::SetCreator 0x0058D700 (0x0058D741 / 0x0058D750)
    /// and counted by CSWSArea::GetNumMinesSetByPlayerParty 0x00508960 (0x005089A5), which is what
    /// enforces the 15-mine limit. A party-placed mine's object id is a per-instance dynamic id,
    /// so it must never be published: excluded from the scan outright.</summary>
    private const int TriggerPlacedByPartyField = 0x314;

    private const int MineScanLimit = 512;

    private readonly MineVanishTracker _mineTracker = new();
    private string _mineModule = string.Empty;
    private uint _mineModuleHash;
    private long _nextMineScanTicks;
    private int _mineSlotCursor;

    public bool SupportsMineSync => _pluginBlock != 0;

    /// <summary>
    /// Reports GIT-placed floor mines that disappeared locally since the last call. First few
    /// passes per module are a silent baseline; scans are throttled to one per second like
    /// doors and placeables. Read-only: nothing here writes to the game.
    /// </summary>
    public List<KeyValuePair<ulong, sbyte>> CollectChangedMineState()
    {
        List<KeyValuePair<ulong, sbyte>> changed = [];

        if (!EngineGlobals.TryReadModuleName(_memory, out string module))
        {
            return changed;
        }

        if (!string.Equals(module, _mineModule, StringComparison.OrdinalIgnoreCase))
        {
            _mineModule = module;
            _mineModuleHash = MineFlags.HashModule(module);
            _mineTracker.Reset();
            _nextMineScanTicks = 0;
            _mineSlotCursor = 0;
        }

        long now = Environment.TickCount64;
        if (now < _nextMineScanTicks)
        {
            return changed;
        }

        _nextMineScanTicks = now + 1000;

        // Re-verify every cached address on every pass: the address list is reused for 30 s
        // (CachedScan), but a deleted trigger's memory no longer carries its vtable / id / gate.
        HashSet<uint> alive = [];
        foreach (nint address in CachedScan(TriggerVtable, MineScanLimit, ObjectScanCacheMs))
        {
            if (_memory.TryReadUInt32(address, out uint vtable) && vtable == TriggerVtable
                && _memory.TryReadUInt32(address + OffObjectId, out uint objectId)
                && objectId is not 0 and <= 0xFFFF
                && _memory.TryReadInt32(address + TriggerTrapField, out int trap) && trap != 0
                && _memory.TryReadInt32(address + TriggerPlacedByPartyField, out int placedByParty)
                && placedByParty == 0)
            {
                alive.Add(objectId);
            }
        }

        foreach (uint gone in _mineTracker.Observe(MineWorldAlive(), alive))
        {
            changed.Add(new KeyValuePair<ulong, sbyte>(
                MineFlags.MakeKey(_mineModuleHash, gone), MineFlags.MakeValue(removed: true)));
        }

        return changed;
    }

    /// <summary>
    /// Independent "the area is still loaded" check for <see cref="MineVanishTracker"/>: at
    /// least half of the cached door and placeable addresses still carry their vtable. An area
    /// unload frees all of them at once, which would otherwise read as every mine vanishing.
    /// A module with no doors and no placeables never passes, so it never publishes (safe).
    /// </summary>
    private bool MineWorldAlive()
    {
        int total = 0;
        int live = 0;

        foreach (uint vtable in new[] { DoorVtable, PlaceableVtable })
        {
            foreach (nint address in CachedScan(vtable, 512, ObjectScanCacheMs))
            {
                total++;
                if (_memory.TryReadUInt32(address, out uint seen) && seen == vtable)
                {
                    live++;
                }
            }
        }

        return live > 0 && live * 2 >= total;
    }

    /// <summary>
    /// Appends the session's "this mine is gone" flags for the local module to the placeable
    /// slot list, starting at <paramref name="slot"/>, as kind-1 entries the plugin's
    /// MineController applies. Also tells the tracker each id is already known so this
    /// instance never echoes a peer's removal back as its own.
    /// </summary>
    private bool AppendMineSlots(IReadOnlyList<KeyValuePair<ulong, sbyte>> flags, ref int slot)
    {
        bool ok = true;
        List<uint> selected = MineSlotPlanner.Select(
            flags, _placeableModuleHash, ref _mineSlotCursor, BlockPlaceableCapacity - slot);

        foreach (uint objectId in selected)
        {
            if (slot >= BlockPlaceableCapacity)
            {
                break;
            }

            _mineTracker.MarkKnownGone(objectId);

            nint address = _pluginBlock + BlockPlaceableSlots + (slot * BlockPlaceableStride);
            ok &= _memory.TryWrite(address + 0x04, (byte)0);
            ok &= _memory.TryWrite(address + 0x05, (byte)0);
            ok &= _memory.TryWrite(address + 0x06, (byte)0);
            ok &= _memory.TryWrite(address + 0x07, BlockSlotKindMine);

            // Object id last, so the plugin never reads a slot whose id and payload disagree.
            ok &= _memory.TryWrite(address + 0x00, objectId);
            slot++;
        }

        return ok;
    }

    /// <summary>Placeable-slot byte +7 value marking a floor-mine removal entry (0 = placeable).
    /// Mirrors SharedBlock.PlaceableSlotKindMine in the plugin.</summary>
    private const byte BlockSlotKindMine = 1;
}

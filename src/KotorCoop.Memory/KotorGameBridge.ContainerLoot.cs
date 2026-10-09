// Container-loot sync: keeps a shared chest/corpse from duplicating its contents when the
// host removes an item from it. Host-authoritative, object-id keyed.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    // ---- container loot --------------------------------------------------------
    //
    // Keeps a shared chest/corpse from duplicating its contents: when the host loots an item
    // out of a container, that item is removed from the same container on the client too.
    // Host-authoritative and object-id keyed (same save => same ids), gated on the Doors
    // capability. See docs/research/placeables-containers.md §2 (all VERIFIED there).

    /// <summary>CSWSPlaceable + this = CItemRepository* (0 when the placeable has no inventory).</summary>
    private const int PlaceableRepositoryField = 0x36C;

    /// <summary>CSWSPlaceable + this = is_body_bag (docs/research/placeables-containers.md §1).
    /// VERIFIED live 2026-10-09: 1 on a dead Sith's "body bag", 0 on lockers and scenery.</summary>
    private const int PlaceableIsBodyBag = 0x440;

    private const int ObjectPositionX = 0x90;

    /// <summary>CItemRepository + this = flat array of item OBJECT IDs (DWORDs, not pointers).</summary>
    private const int RepositoryItemArray = 0x0C;

    /// <summary>CItemRepository + this = item count.</summary>
    private const int RepositoryItemCount = 0x10;

    // Legacy container-loot removal region, retained for old plugin readers.
    private const int BlockLootCount = 0xD00;
    private const int BlockLootSlots = 0xD08;
    private const int BlockLootStride = 0x08;
    private const int BlockLootCapacity = 24;

    // Protocol 59 acknowledged mailbox. Mirrors SharedBlock.Items.cs exactly.
    private const int BlockLootMailboxModuleHash = 0x4D00;
    private const int BlockLootMailboxAckSerial = 0x4D04;
    private const int BlockLootMailboxPageSerial = 0x4D08;
    private const int BlockLootMailboxCount = 0x4D0C;
    private const int BlockLootMailboxSlots = 0x4D10;
    private const int BlockLootMailboxStride = 0x0C;
    private const int BlockLootMailboxCapacity = 24;
    private const int BlockLootMailboxConsumedCount = 0x4E30;
    private const int BlockLootMailboxEpoch = 0x4E34;
    private const int BlockLootMailboxAckEpoch = 0x4E38;


    /// <summary>Last observed item-id set per container in the current module.</summary>
    private readonly Dictionary<uint, HashSet<uint>> _containerShadow = [];
    private string _lootModule = string.Empty;
    private bool _lootBaselined;
    private long _nextLootScanTicks;
    private string _lootPageModule = string.Empty;
    private int _lootPageCursor;
    private uint _lootPageSerial;
    private uint _lootPageEpoch;
    private bool _lootPageEpochInitialized;
    private bool _lootPagePending;
    private readonly List<ContainerLootRemoval> _lootQueuedEntries = [];

    /// <summary>Applying loot removal needs the plugin (RemoveItem is a main-thread engine call).</summary>
    public bool SupportsContainerLoot => _pluginBlock != 0;

    /// <summary>
    /// Diffs each container's item-id set against the last scan and reports items that left
    /// during play, as (container, item) removals. The first scan per module is a silent
    /// baseline — pre-existing contents are not "loot events" — exactly like the door and
    /// placeable scans, and the scan is throttled to one per second for the same reason (it
    /// walks the address space by the placeable vtable).
    /// </summary>
    public List<ContainerLootRemoval> CollectContainerLootRemovals()
    {
        List<ContainerLootRemoval> removals = [];

        if (!EngineGlobals.TryReadModuleName(_memory, out string module))
        {
            return removals;
        }

        if (!string.Equals(module, _lootModule, StringComparison.OrdinalIgnoreCase))
        {
            _lootModule = module;
            _containerShadow.Clear();
            _lootBaselined = false;
            _nextLootScanTicks = 0;
            // Keep an outstanding page owned by the plugin until it acknowledges or drops it
            // under its own settled-module guard. Resetting here would overwrite that page.
        }

        long now = Environment.TickCount64;
        if (now < _nextLootScanTicks)
        {
            return removals;
        }

        _nextLootScanTicks = now + 1000;

        HashSet<uint> seenContainers = [];

        foreach (nint address in CachedScan(PlaceableVtable, 512, ObjectScanCacheMs))
        {
            if (!_memory.TryReadUInt32(address + OffObjectId, out uint containerId)
                || containerId is 0 or > 0xFFFF)
            {
                continue;
            }

            // A container each engine fills with its own party's items (the Leviathan party
            // locker) is not a shared chest: equal ids on the two sides name different items.
            if (ContainerLootExemptionRule.IsExempt(module, ReadExoString(address + OffTag)))
            {
                continue;
            }

            // A body bag is made separately by each engine when a creature dies, so equal ids
            // on two sides name different bags (and different items): CorpseLootPump pairs them
            // by position instead. Through here a joiner's take could even remove an unrelated
            // host item that happens to share both ids.
            if (_memory.TryReadInt32(address + PlaceableIsBodyBag, out int isBodyBag) && isBodyBag != 0)
            {
                continue;
            }

            // Only a container has a repository; a plain placeable's field is 0. This also
            // rejects a mis-scanned hit, whose +0x36C is unlikely to be a valid pointer.
            if (!_memory.TryReadPointer(address + PlaceableRepositoryField, out nint repo) || repo == 0)
            {
                continue;
            }

            if (!_memory.TryReadInt32(repo + RepositoryItemCount, out int count)
                || count is < 0 or > 4096
                || !_memory.TryReadPointer(repo + RepositoryItemArray, out nint ids)
                || (count > 0 && ids == 0))
            {
                continue;
            }

            HashSet<uint> current = [];
            for (int i = 0; i < count; i++)
            {
                if (_memory.TryReadUInt32(ids + (nint)(i * 4), out uint itemId)
                    && itemId is > 0 and < 0x7F000000)
                {
                    current.Add(itemId);
                }
            }

            seenContainers.Add(containerId);

            // An item present last scan and gone now left the container during play. Only
            // announce once the baseline is established, so the first sighting is silent.
            if (_lootBaselined && _containerShadow.TryGetValue(containerId, out HashSet<uint>? previous))
            {
                foreach (uint itemId in previous)
                {
                    if (!current.Contains(itemId))
                    {
                        removals.Add(new ContainerLootRemoval(containerId, itemId));
                    }
                }
            }

            _containerShadow[containerId] = current;
        }

        // Forget containers no longer present, so a reused id never diffs against a stranger.
        if (_containerShadow.Count != seenContainers.Count)
        {
            List<uint> gone = [.. _containerShadow.Keys.Where(id => !seenContainers.Contains(id))];
            foreach (uint id in gone)
            {
                _containerShadow.Remove(id);
            }
        }

        _lootBaselined = true;
        return removals;
    }

    /// <summary>
    /// Publishes the session's container-loot removals into the plugin, which deletes each
    /// looted item from its own copy of the container on the game's thread. Idempotent — an
    /// item already gone resolves to a no-op — so re-declaring the set every tick is safe.
    /// Delivers the input in oldest-first pages through the acknowledged mailbox. A page is
    /// never overwritten while the plugin still owns it, so all 24-slot pages get a chance to
    /// be consumed; this fixes the former newest-only starvation.
    /// </summary>
    public bool TryWriteContainerLoot(IReadOnlyList<ContainerLootRemoval> removals)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        // Protocol 59 owns the acknowledged mailbox; keep the pre-59 level inert if a
        // plugin process survived an agent restart.
        _memory.TryWrite(_pluginBlock + BlockLootCount, 0u);

        string module = _lootModule;
        if (module.Length == 0)
            return false;

        bool sameModule = string.Equals(module, _lootPageModule, StringComparison.OrdinalIgnoreCase);
        if (!_lootPagePending && (!sameModule || !HasPrefix(removals)))
        {
            _lootPageModule = module;
            _lootQueuedEntries.Clear();
            _lootQueuedEntries.AddRange(removals);
            _lootPageCursor = 0;
        }
        else if (!_lootPagePending && sameModule)
        {
            _lootQueuedEntries.AddRange(removals.Skip(_lootQueuedEntries.Count));
        }

        if (_lootPagePending)
        {
            if (!_memory.TryReadUInt32(_pluginBlock + BlockLootMailboxAckSerial, out uint ack)
                || !_memory.TryReadUInt32(_pluginBlock + BlockLootMailboxAckEpoch, out uint ackEpoch)
                || ack != _lootPageSerial || ackEpoch != _lootPageEpoch)
                return false;
            _lootPagePending = false;
            if (!sameModule || !HasPrefix(removals))
            {
                _lootPageModule = module;
                _lootQueuedEntries.Clear();
                _lootQueuedEntries.AddRange(removals);
                _lootPageCursor = 0;
            }
            else
            {
                _lootQueuedEntries.AddRange(removals.Skip(_lootQueuedEntries.Count));
            }
        }

        if (_lootPageCursor >= _lootQueuedEntries.Count)
            return true;

        bool ok = true;
        int count = Math.Min(_lootQueuedEntries.Count - _lootPageCursor, BlockLootMailboxCapacity);

        for (int i = 0; i < count; i++)
        {
            ContainerLootRemoval removal = _lootQueuedEntries[_lootPageCursor + i];
            nint slot = _pluginBlock + BlockLootMailboxSlots + (i * BlockLootMailboxStride);
            ok &= _memory.TryWrite(slot + 0x04, removal.ItemId);
            ok &= _memory.TryWrite(slot + 0x00, removal.ContainerId);
        }

        if (!_lootPageEpochInitialized)
        {
            _memory.TryReadUInt32(_pluginBlock + BlockLootMailboxEpoch, out uint oldEpoch);
            _lootPageEpoch = oldEpoch + 1;
            if (_lootPageEpoch == 0)
                _lootPageEpoch = (uint)Environment.TickCount64 | 1u;
            _lootPageEpochInitialized = true;
        }

        if (_lootPageSerial == 0)
        {
            _memory.TryReadUInt32(_pluginBlock + BlockLootMailboxPageSerial, out uint oldSerial);
            _lootPageSerial = oldSerial + 1;
            if (_lootPageSerial == 0) _lootPageSerial = 1;
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockLootMailboxModuleHash, ModuleIdentity.Hash(module));
        ok &= _memory.TryWrite(_pluginBlock + BlockLootMailboxEpoch, _lootPageEpoch);
        ok &= _memory.TryWrite(_pluginBlock + BlockLootMailboxCount, (uint)count);
        _lootPageSerial++;
        if (_lootPageSerial == 0) _lootPageSerial = 1;
        if (!ok)
            return false;

        // Serial last is the mailbox publication barrier. Never expose a page whose slots or
        // metadata had a failed write: the previous serial remains owned by the plugin, and a
        // later retry rewrites the complete payload before publishing a new serial.
        if (!_memory.TryWrite(_pluginBlock + BlockLootMailboxPageSerial, _lootPageSerial))
            return false;

        _lootPageCursor += count;
        _lootPagePending = true;
        return true;
    }

    private bool HasPrefix(IReadOnlyList<ContainerLootRemoval> desired)
    {
        if (desired.Count < _lootQueuedEntries.Count)
            return false;
        for (int i = 0; i < _lootQueuedEntries.Count; i++)
        {
            if (_lootQueuedEntries[i] != desired[i])
                return false;
        }
        return true;
    }


    public List<BodyBagState> ReadBodyBags()
    {
        List<BodyBagState> bags = [];
        bool objectArrayResolved = EngineGlobals.TryGetServerObjectArray(_memory, out nint objectArray);
        foreach (nint address in EngineGlobals.EnumerateServerObjects(_memory))
        {
            if (!_memory.TryReadUInt32(address, out uint vtable) || vtable != PlaceableVtable
                || !_memory.TryReadInt32(address + PlaceableIsBodyBag, out int isBodyBag) || isBodyBag == 0
                || !_memory.TryReadUInt32(address + OffObjectId, out uint bagId)
                || !_memory.TryRead(address + ObjectPositionX, out float x)
                || !_memory.TryRead(address + ObjectPositionX + 4, out float y)
                || !_memory.TryReadPointer(address + PlaceableRepositoryField, out nint repo) || repo == 0
                || !_memory.TryReadInt32(repo + RepositoryItemCount, out int count) || count is < 0 or > 256
                || !_memory.TryReadPointer(repo + RepositoryItemArray, out nint ids) || (count > 0 && ids == 0))
            {
                continue;
            }

            List<BodyBagItem> items = [];
            for (int i = 0; i < count; i++)
            {
                if (_memory.TryReadUInt32(ids + (nint)(i * 4), out uint itemId) && itemId is > 0 and < 0x7F000000
                    && objectArrayResolved
                    && TryReadLiveItemMetadata(itemId, objectArray, out _, out string resref, out ItemIdentity identity, out bool deleting)
                    && !deleting && resref.Length > 0)
                {
                    items.Add(new BodyBagItem(itemId, resref, (ushort)Math.Max(1, (int)identity.StackSize)));
                }
            }

            bags.Add(new BodyBagState(bagId, x, y, items));
        }

        return bags;
    }
}

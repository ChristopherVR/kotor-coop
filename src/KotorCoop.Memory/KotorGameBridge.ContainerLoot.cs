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

    /// <summary>CItemRepository + this = flat array of item OBJECT IDs (DWORDs, not pointers).</summary>
    private const int RepositoryItemArray = 0x0C;

    /// <summary>CItemRepository + this = item count.</summary>
    private const int RepositoryItemCount = 0x10;

    // Container-loot removal region, pre-reserved at 0xD00..0xE00 in the shared block (the
    // block's TotalSize and version already cover it, so nothing there changes).
    private const int BlockLootCount = 0xD00;
    private const int BlockLootSlots = 0xD08;
    private const int BlockLootStride = 0x08;
    private const int BlockLootCapacity = 24;

    /// <summary>Last observed item-id set per container in the current module.</summary>
    private readonly Dictionary<uint, HashSet<uint>> _containerShadow = [];
    private string _lootModule = string.Empty;
    private bool _lootBaselined;
    private long _nextLootScanTicks;

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
    /// Writes at most <see cref="BlockLootCapacity"/>, the newest (the store keeps them
    /// oldest-first, and the newest are the ones least likely to be applied yet).
    /// </summary>
    public bool TryWriteContainerLoot(IReadOnlyList<ContainerLootRemoval> removals)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = true;
        int count = Math.Min(removals.Count, BlockLootCapacity);
        int start = removals.Count - count;

        for (int i = 0; i < count; i++)
        {
            ContainerLootRemoval removal = removals[start + i];
            nint slot = _pluginBlock + BlockLootSlots + (i * BlockLootStride);

            ok &= _memory.TryWrite(slot + 0x04, removal.ItemId);

            // Container id last, so the plugin never reads a slot whose id and payload disagree.
            ok &= _memory.TryWrite(slot + 0x00, removal.ContainerId);
        }

        ok &= _memory.TryWrite(_pluginBlock + BlockLootCount, (uint)count);
        return ok;
    }

}

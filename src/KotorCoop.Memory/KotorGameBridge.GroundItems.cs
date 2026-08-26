// Ground-item sync: declaring the desired item set to the plugin, tracking which coop item id
// occupies which shared-block slot, and reconciling vanished/picked-up items.
using KotorCoop.Protocol;

namespace KotorCoop.Memory;

public sealed partial class KotorGameBridge
{
    /// <summary>Agent's view of which coop item id occupies each plugin item slot.</summary>
    private readonly uint[] _itemSlotIds = new uint[BlockItemCapacity];
    // ---- ground items ----------------------------------------------------------

    /// <summary>Item sync needs the in-process plugin; there is no external fallback.</summary>
    public bool SupportsGroundItems => _pluginBlock != 0;

    /// <summary>
    /// Declares the desired ground-item set to the plugin. The plugin reconciles the
    /// world toward it on its own frames; this only assigns items to slots, publishes
    /// new wants, and withdraws stale ones.
    /// </summary>
    public bool TryWriteGroundItems(IReadOnlyList<GroundItem> items)
    {
        if (_pluginBlock == 0)
        {
            return false;
        }

        bool ok = true;

        // Withdraw slots whose item is no longer in the desired set.
        for (int i = 0; i < BlockItemCapacity; i++)
        {
            uint occupant = _itemSlotIds[i];
            if (occupant == 0)
            {
                continue;
            }

            bool stillWanted = false;
            for (int j = 0; j < items.Count; j++)
            {
                if (items[j].ItemId == occupant)
                {
                    stillWanted = true;
                    break;
                }
            }

            if (stillWanted)
            {
                continue;
            }

            nint slot = _pluginBlock + BlockItemSlots + (i * BlockItemSlotStride);
            ok &= _memory.TryWrite(slot + 0x00, 0u);

            // The slot stays reserved until the plugin confirms it has let the object
            // go; reusing it earlier could destroy the wrong item's body.
            if (ReadItemState(i) == ItemStateNone)
            {
                _itemSlotIds[i] = 0;
            }
        }

        // Publish items that have no slot yet.
        for (int j = 0; j < items.Count; j++)
        {
            GroundItem item = items[j];

            if (item.ItemId == 0)
            {
                continue;
            }

            int assigned = FindItemSlot(item.ItemId);
            if (assigned >= 0)
            {
                // Already published. Its identity can still have been LEARNED since — the
                // store merges one upward whenever a better-informed announcement arrives —
                // so keep the slot current. The plugin only reads identity when it spawns a
                // body, so this does not retro-fix an item already on the floor; it means a
                // respawn after a module transition gets the item right instead of stock.
                WriteItemIdentity(
                    _pluginBlock + BlockItemSlots + (assigned * BlockItemSlotStride),
                    item.Identity);
                continue;
            }

            int free = -1;
            for (int i = 0; i < BlockItemCapacity; i++)
            {
                if (_itemSlotIds[i] == 0 && ReadItemState(i) == ItemStateNone)
                {
                    free = i;
                    break;
                }
            }

            if (free < 0)
            {
                ok = false;
                continue;
            }

            nint slot = _pluginBlock + BlockItemSlots + (free * BlockItemSlotStride);
            ok &= _memory.TryWrite(slot + 0x04, item.ItemId);
            ok &= _memory.TryWrite(slot + 0x08, item.Position.X);
            ok &= _memory.TryWrite(slot + 0x0C, item.Position.Y);
            ok &= _memory.TryWrite(slot + 0x10, item.Position.Z);
            ok &= _memory.TryWriteAsciiString(slot + 0x14, item.Resref, BlockItemResrefLength);

            ok &= WriteItemIdentity(slot, item.Identity);

            // Wanted last, so the plugin never observes a half-written slot.
            ok &= _memory.TryWrite(slot + 0x00, 1u);

            _itemSlotIds[free] = item.ItemId;
        }

        return ok;
    }

    /// <summary>
    /// Item ids the plugin reports as vanished from the floor: the local player picked
    /// them up (or the module changed under them). Announced every pump until the store
    /// removes them, which is harmless because removal is idempotent.
    /// </summary>
    public List<uint> CollectVanishedItems()
    {
        List<uint> vanished = [];

        if (_pluginBlock == 0)
        {
            return vanished;
        }

        for (int i = 0; i < BlockItemCapacity; i++)
        {
            if (_itemSlotIds[i] == 0)
            {
                continue;
            }

            nint status = _pluginBlock + BlockItemStatus + (i * BlockItemStatusStride);

            if (_memory.TryReadUInt32(status + 0x08, out uint state)
                && state == ItemStateVanished
                && _memory.TryReadUInt32(status + 0x00, out uint itemId)
                && itemId == _itemSlotIds[i])
            {
                vanished.Add(itemId);
            }
        }

        return vanished;
    }

    /// <summary>
    /// Publishes an item's per-instance state into the slot's tail (block version 39).
    ///
    /// The charges/max-charges/flags trio goes out as ONE dword because the plugin reads the
    /// slot on its own frames: three separate WriteProcessMemory calls are three moments it
    /// could see a half-updated identity, and one is none. An unknown identity writes zeroes,
    /// which is exactly what the plugin reads as "leave the item you just made alone".
    /// </summary>
    private bool WriteItemIdentity(nint slot, ItemIdentity identity)
    {
        bool ok = _memory.TryWrite(slot + 0x24, identity.Upgrades);
        ok &= _memory.TryWrite(slot + 0x28, (uint)identity.StackSize);
        ok &= _memory.TryWrite(slot + 0x2C, identity.Charges
            | ((uint)identity.MaxCharges << 8)
            | ((uint)identity.Flags << 16));
        return ok;
    }

    private int FindItemSlot(uint itemId)
    {
        for (int i = 0; i < BlockItemCapacity; i++)
        {
            if (_itemSlotIds[i] == itemId)
            {
                return i;
            }
        }

        return -1;
    }

    private uint ReadItemState(int slot)
    {
        nint status = _pluginBlock + BlockItemStatus + (slot * BlockItemStatusStride);
        return _memory.TryReadUInt32(status + 0x08, out uint state) ? state : ItemStateNone;
    }
}

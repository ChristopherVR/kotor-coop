namespace KotorCoop.Plugin;

// Ground-item slot/status accessors and container-loot removal. See SharedBlockView.cs for
// the class declaration and shared private read/write helpers.
internal sealed unsafe partial class SharedBlockView
{
    // ---- ground items -----------------------------------------------------------


    // ---- container loot ---------------------------------------------------------

    /// <summary>Agent-written; the plugin zeroes it on a module change (ids name strangers).</summary>
    public uint LootCount
    {
        get => *U32(SharedBlock.OffsetLootCount);
        set => *U32(SharedBlock.OffsetLootCount) = value;
    }

    /// <summary>One container-loot removal: containerId, itemId.</summary>
    public LootView GetLoot(int index)
    {
        byte* slot = baseAddress + SharedBlock.OffsetLootSlots
            + (Clamp(index, SharedBlock.LootCapacity) * SharedBlock.LootSlotStride);
        return new LootView(*(uint*)(slot + 0x00), *(uint*)(slot + 0x04));
    }


    public ItemSlotView GetItemSlot(int index) =>
        new(baseAddress + SharedBlock.OffsetItemSlots
            + (Clamp(index, SharedBlock.ItemSlotCapacity) * SharedBlock.ItemSlotStride));

    public void SetItemStatus(int index, uint itemId, uint objectId, uint state, int error)
    {
        byte* status = baseAddress + SharedBlock.OffsetItemStatus
            + (Clamp(index, SharedBlock.ItemSlotCapacity) * SharedBlock.ItemStatusStride);
        *(uint*)(status + 0x00) = itemId;
        *(uint*)(status + 0x04) = objectId;
        *(int*)(status + 0x0C) = error;

        // State last, so the agent never observes a half-written status slot.
        *(uint*)(status + 0x08) = state;
    }
}

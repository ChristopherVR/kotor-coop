namespace KotorCoop.Plugin;

internal static class PlaceableLocalChanges
{
    private static readonly PlaceableLocalChangeTracker Pending = new();
    private static uint _generation;

    public static void Record(uint id, bool open) => Pending.Record(id, open);
    public static void Clear(SharedBlockView block) { Pending.Clear(); block.ClearLocalPlaceableChanges(); }
    public static void PublishConfirmed(SharedBlockView block)
    {
        for (int slot = 0; slot < SharedBlock.PlaceableCapacity; slot++)
        {
            uint id = Pending.PendingId(slot);
            if (id == 0) continue;
            nint placeable = Odyssey.ResolvePlaceable(id);
            if (placeable == 0) continue;
            bool open = Odyssey.IsPlaceableOpen(placeable);
            if (!Pending.Confirm(slot, open)) continue;
            if (++_generation == 0) ++_generation;
            byte tuple = (byte)((open ? 1 : 0) | (Odyssey.IsPlaceableLocked(placeable) ? 0 : 2)
                | (Odyssey.IsPlaceableTrapped(placeable) ? 4 : 0));
            block.PublishLocalPlaceableChange(slot, id, tuple, _generation);
        }
    }
}

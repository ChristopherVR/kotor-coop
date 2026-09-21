namespace KotorCoop.Memory;

/// <summary>
/// Chooses the mine removals that fit in the shared placeable slot list for one pass.
///
/// The list is also used by ordinary placeables and has only 32 entries. A module can have
/// more than 32 placeables, and a burst of mine removals can therefore starve either channel
/// if one is always written first. Four mine slots per pass leaves room for placeables while
/// the cursor makes a larger backlog fair: every mine is eventually offered to the plugin.
/// </summary>
internal static class MineSlotPlanner
{
    public const int MaxMineSlotsPerPass = 4;

    public static List<uint> Select(
        IReadOnlyList<KeyValuePair<ulong, sbyte>> flags,
        uint moduleHash,
        ref int cursor,
        int capacity)
    {
        List<uint> mineIds = [];
        HashSet<uint> seen = [];

        foreach (KeyValuePair<ulong, sbyte> flag in flags)
        {
            if (!MineFlags.IsMineKey(flag.Key)
                || MineFlags.ModuleHashOf(flag.Key) != moduleHash
                || !MineFlags.RemovedOf(flag.Value))
            {
                continue;
            }

            uint objectId = MineFlags.ObjectIdOf(flag.Key);
            if (objectId != 0 && seen.Add(objectId))
            {
                mineIds.Add(objectId);
            }
        }

        if (mineIds.Count == 0 || capacity <= 0)
        {
            cursor = 0;
            return [];
        }

        int start = Math.Clamp(cursor, 0, mineIds.Count - 1);
        int count = Math.Min(Math.Min(MaxMineSlotsPerPass, capacity), mineIds.Count);
        List<uint> selected = new(count);
        for (int i = 0; i < count; i++)
        {
            selected.Add(mineIds[(start + i) % mineIds.Count]);
        }

        cursor = (start + count) % mineIds.Count;
        return selected;
    }

    /// <summary>Selects ordinary placeable entries for the slots left after the mine slice.
    /// The same rotating policy prevents the reserved mine slice from permanently starving
    /// the tail of a module's placeable list.</summary>
    public static List<uint> SelectPlaceables(
        IReadOnlyList<KeyValuePair<ulong, sbyte>> flags,
        uint moduleHash,
        ref int cursor,
        int capacity)
    {
        List<uint> placeableIds = [];
        HashSet<uint> seen = [];

        foreach (KeyValuePair<ulong, sbyte> flag in flags)
        {
            if (!PlaceableFlags.IsPlaceableKey(flag.Key)
                || PlaceableFlags.ModuleHashOf(flag.Key) != moduleHash
                || flag.Value <= 0)
            {
                continue;
            }

            uint objectId = PlaceableFlags.ObjectIdOf(flag.Key);
            if (objectId != 0 && seen.Add(objectId))
            {
                placeableIds.Add(objectId);
            }
        }

        if (placeableIds.Count == 0 || capacity <= 0)
        {
            cursor = 0;
            return [];
        }

        int start = Math.Clamp(cursor, 0, placeableIds.Count - 1);
        int count = Math.Min(capacity, placeableIds.Count);
        List<uint> selected = new(count);
        for (int i = 0; i < count; i++)
        {
            selected.Add(placeableIds[(start + i) % placeableIds.Count]);
        }

        cursor = (start + count) % placeableIds.Count;
        return selected;
    }
}

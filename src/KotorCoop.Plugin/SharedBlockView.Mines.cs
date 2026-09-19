namespace KotorCoop.Plugin;

internal sealed unsafe partial class SharedBlockView
{
    /// <summary>
    /// The kind byte (+7) of placeable slot <paramref name="index"/>: 0 = a placeable's desired
    /// state, <see cref="SharedBlock.PlaceableSlotKindMine"/> = a floor-mine removal. See
    /// SharedBlock.Mines.cs.
    /// </summary>
    public byte GetPlaceableSlotKind(int index)
    {
        int slot = Math.Clamp(index, 0, SharedBlock.PlaceableCapacity - 1);
        return *(baseAddress + SharedBlock.OffsetPlaceableSlots
            + (slot * SharedBlock.PlaceableSlotStride) + SharedBlock.PlaceableSlotKindOffset);
    }
}

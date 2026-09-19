namespace KotorCoop.Plugin;

internal partial struct SharedBlock
{
    // ---- floor-mine removals ride the placeable slot list (NO layout change) ------------------
    //
    // docs/research/mines.md §6. Each placeable slot is 8 bytes: objectId u32 @0, open u8 @4,
    // unlocked u8 @5, disarmed u8 @6, and byte @7 — padding until floor-mine sync. That byte is
    // now the slot KIND: 0 = a placeable's desired state (the existing meaning), 1 = "this
    // trap trigger was removed on a peer; remove it here". A kind-1 slot carries only the
    // object id; bytes 4..6 are written 0. The agent zeroes byte 7 for every placeable slot it
    // writes, so a slot recycled from a mine entry to a placeable is never misread.
    //
    // The offsets, capacity (32) and stride (8) of the list are unchanged, so BlockVersion is
    // not bumped. An older plugin that predates this simply ignores byte 7: its own
    // ResolvePlaceable vtable-checks the id and a mine's id is never a placeable.

    /// <summary>Byte offset of the kind within one placeable slot.</summary>
    public const int PlaceableSlotKindOffset = 0x07;

    /// <summary>Slot kind: a floor mine removed on a peer (see the region comment above).</summary>
    public const byte PlaceableSlotKindMine = 1;
}

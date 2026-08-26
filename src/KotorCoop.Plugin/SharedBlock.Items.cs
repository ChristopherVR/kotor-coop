namespace KotorCoop.Plugin;

// Ground-item offsets (agent<->plugin item-slot reconciliation) and the container-loot
// removal region. See SharedBlock.cs for the struct's StructLayout/version header.
internal partial struct SharedBlock
{
    // ---- ground items (version 2) ----------------------------------------------
    //
    // Desired-state reconciliation rather than a command queue. The agent writes the set
    // of items that SHOULD be on the ground; the plugin spawns what is missing, destroys
    // what is no longer wanted, and reports per-slot status back. An item that vanishes
    // on its own (the local player picked it up) is reported as ItemStateVanished until
    // the agent stops wanting it.

    /// <summary>Agent -&gt; plugin. 8 slots of <see cref="ItemSlotStride"/> bytes each.</summary>
    public const int OffsetItemSlots = 0x290;

    /// <summary>
    /// wanted u32 @0, itemId u32 @4, position 3xf32 @8, resref char[16] @0x14, then the
    /// per-instance identity (block version 39) in the tail the stride always had spare:
    /// upgrades u32 @0x24, stackSize u32 @0x28, and charges/maxCharges/flags packed into
    /// one dword @0x2C (byte 0 charges, byte 1 max charges, byte 2 flags).
    ///
    /// Packed into a dword rather than laid out as bytes because the agent writes each field
    /// with a separate WriteProcessMemory and the plugin reads the slot on its own frames:
    /// one write is one thing the plugin can see half of, and three bytes written
    /// individually are three. The `wanted` flag is still published last, which is what
    /// actually makes the whole slot atomic enough.
    /// </summary>
    public const int ItemSlotStride = 0x30;

    /// <summary>Identity flags byte inside the slot's packed dword — mirrors
    /// <c>ItemIdentity.Flag*</c> on the wire. Bit 0 KNOWN gates every other bit and every
    /// other identity field: zero means the agent had nothing to say, and the plugin must
    /// leave the item the engine just created exactly as it is.</summary>
    public const uint ItemIdentityKnown = 1 << 0;

    public const uint ItemIdentityIdentified = 1 << 1;
    public const uint ItemIdentityStolen = 1 << 2;
    public const uint ItemIdentityNonEquippable = 1 << 3;

    /// <summary>Plugin -&gt; agent. 8 slots: itemId u32 @0, objectId u32 @4, state u32 @8, error i32 @0xC.</summary>
    public const int OffsetItemStatus = 0x410;

    public const int ItemStatusStride = 0x10;

    /// <summary>
    /// Agent -&gt; plugin. Non-zero means "do not let this instance pause just because its
    /// window is not focused". Opt-in rather than automatic, because suppressing that
    /// pause is wrong for ordinary single-player use of the plugin.
    /// </summary>
    public const int OffsetKeepAliveUnfocused = 0x268;

    /// <summary>Plugin -&gt; agent. How many focus-loss pauses have been cleared.</summary>
    public const int OffsetPausesCleared = 0x26C;

    /// <summary>
    /// Agent -&gt; plugin. Item slot index (1-based) whose item the local player should walk
    /// over and pick up, 0 for none. A test hook: it drives the engine's own
    /// ActionPickUpItem so the real pickup path can be exercised without needing a
    /// pixel-accurate mouse click on a small object on the floor.
    /// </summary>
    public const int OffsetPickUpRequest = 0x280;

    /// <summary>Plugin -&gt; agent. Increments each time a pickup action is issued.</summary>
    public const int OffsetPickUpIssued = 0x284;


    // ---- container-loot removal (region 0xD00..0xE00) -----------------------------
    //
    // Chests and corpses are shared, so an item the host loots out of a container must vanish
    // from the client's copy too or it could be taken twice. The agent declares the desired
    // (container, item) removals for the local module; the plugin resolves each container and
    // deletes the matching item through CSWSPlaceable::RemoveItem.

    /// <summary>Agent -&gt; plugin. How many container-loot removals are valid.</summary>
    public const int OffsetLootCount = 0xD00;

    /// <summary>Agent -&gt; plugin. containerId u32 @0, itemId u32 @4.</summary>
    public const int OffsetLootSlots = 0xD08;

    public const int LootSlotStride = 0x08;

    public const int LootCapacity = 24; // occupies 0xD08..0xDC8, inside the 0xD00..0xE00 region

}

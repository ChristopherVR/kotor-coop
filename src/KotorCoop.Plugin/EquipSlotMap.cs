namespace KotorCoop.Plugin;

/// <summary>
/// Maps the visible NWScript inventory-slot ids (<c>Odyssey.InventorySlotBody</c> = 1,
/// <c>InventorySlotRightWeapon</c> = 4, <c>InventorySlotLeftWeapon</c> = 5 — the ids
/// <c>ActionEquipItem</c>'s VM interface takes, and what every existing caller in this
/// codebase already passes) to the INTERNAL slot index <c>CSWSCreature::EquipItem</c>
/// (0x004FEB90) takes directly as its first argument.
///
/// The internal index is the <c>CSWInventory</c> slot's BYTE OFFSET divided by 4 — body
/// +0x08, right weapon +0x14, left weapon +0x18 (VERIFIED,
/// docs/research/item-identity.md/companion-state-sync.md) — which gives 2/5/6.
/// Cross-checked live-in-the-binary this round: <c>CSWSCreature::ForceEquipClothing</c>
/// (0x00501C40)'s own call into <c>EquipItem</c> at 0x00501D69 pushes slot=2 for a
/// body/clothing equip, matching the arithmetic exactly. Right (5) and left (6) weapon were
/// NOT independently found at a clean, single-value call site this round (the two weapon-equip
/// sites disassembled, 0x00501FC2 and 0x005022D8, push a slot value carried in a register from
/// their own caller rather than a literal) — INFERRED from the verified offset arithmetic, not
/// cross-checked against a second literal the way body was. NEEDS-LIVE.
///
/// Deliberately dependency-free (no reference to <c>Odyssey</c>) so it compiles into
/// <c>KotorCoop.Tests</c> without the engine-facing half of the plugin, the same trick this
/// project already uses for <see cref="EquipApplyDecision"/> and friends.
/// </summary>
public static class EquipSlotMap
{
    /// <summary>CSWInventory offset +0x08, divided by 4.</summary>
    public const int InternalSlotBody = 2;

    /// <summary>CSWInventory offset +0x14, divided by 4.</summary>
    public const int InternalSlotRightWeapon = 5;

    /// <summary>CSWInventory offset +0x18, divided by 4.</summary>
    public const int InternalSlotLeftWeapon = 6;

    /// <summary>-1 for any NWScript slot id this map does not know — the three visible slots
    /// this project's equipment channels ever ask for (1, 4, 5), and nothing else.</summary>
    public static int ToInternalSlot(int nwscriptSlot) => nwscriptSlot switch
    {
        1 => InternalSlotBody,
        4 => InternalSlotRightWeapon,
        5 => InternalSlotLeftWeapon,
        _ => -1,
    };
}

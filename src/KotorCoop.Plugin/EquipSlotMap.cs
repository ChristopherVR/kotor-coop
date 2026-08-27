namespace KotorCoop.Plugin;

/// <summary>
/// Maps the visible NWScript inventory-slot ids (<c>Odyssey.InventorySlotBody</c> = 1,
/// <c>InventorySlotRightWeapon</c> = 4, <c>InventorySlotLeftWeapon</c> = 5 — the ids
/// <c>ActionEquipItem</c>'s VM interface takes, and what every existing caller in this
/// codebase already passes) to the INTERNAL slot value <c>CSWSCreature::EquipItem</c>
/// (0x004FEB90) takes directly as its first argument, and to the raw <c>CSWInventory</c> byte
/// offset a caller needs to read back the same slot directly (verification, no engine call).
///
/// **The "byte offset / 4" formula this map used before (2/5/6) was WRONG for the two weapon
/// slots — VERIFIED wrong by full disassembly this round, three independent ways:**
///
/// 1. <c>CSWSCreature::EquipItem</c> resolves its slot argument through a private helper,
///    <c>CSWInventory</c>'s slot-to-pointer routine at 0x005A49C0 (called from both
///    <c>PutItemInSlot</c> 0x005A4BE0 and <c>UnequipItem</c>'s own helpers) — disassembled and
///    its jump tables read as raw data this round. It takes a **single-bit BIT-FLAG**, not an
///    array index: value 1→<c>inv+4</c> (HEAD), 2→<c>inv+8</c> (BODY), 4→<c>inv+0xC</c>,
///    8→<c>inv+0x10</c>, **0x10 (16)→<c>inv+0x14</c> (RIGHT WEAPON)**,
///    **0x20 (32)→<c>inv+0x18</c> (LEFT WEAPON)**, 0x40→<c>inv+0x1C</c>, 0x80→<c>inv+0x20</c>,
///    0x200→<c>inv+0x28</c>, 0x400→<c>inv+0x2C</c>, 0x4000→<c>inv+0x30</c>,
///    0x8000→<c>inv+0x34</c>, 0x10000→<c>inv+0x38</c>, 0x20000→<c>inv+0x3C</c>; every
///    non-power-of-two value (3, 5, 6, 7, 0x1000, ...) hits the routine's own invalid path and
///    returns a NULL pointer. Old value 5 (for right weapon) and 6 (for left weapon) both land
///    on that invalid path — <c>PutItemInSlot</c> silently no-ops on a null pointer
///    (`test eax,eax; je` past the write), which is exactly why the raw <c>CSWInventory</c>
///    weapon-slot dwords never changed in the 2026-08-28 rig round
///    (docs/research/companion-state-sync.md §10.2) despite <c>EquipItem</c> itself always
///    reporting success.
/// 2. <c>CSWVirtualMachineCommands::ExecuteCommandActionEquipItem</c> (0x005355F0, the VM
///    handler for NWScript's own <c>ActionEquipItem</c>) converts its 0-17 <c>nInventorySlot</c>
///    argument to this same internal value via an explicit `2^slot` computation
///    (0x00535667-0x0053567B: `fld [2.0]; fild [nInventorySlot]; call pow; call ftol`) before
///    ever touching <c>CSWSCreature::AddEquipItemActions</c> — i.e. the ENGINE ITSELF derives
///    the internal slot as <c>1 &lt;&lt; nwscriptSlot</c>, not "byte offset / 4".
/// 3. <c>CSWSCreature::AddEquipItemActions</c> (0x004F0420)'s own refusal gate
///    (0x004F043C-0x004F044D) compares its slot argument against the literals `2`, `1`, `0x10`,
///    `0x20` — the same four bit-flag values, not 5/6.
///
/// Body's old value (2) survived by coincidence: <c>1 &lt;&lt; 1 == 2</c>, so the "offset / 4"
/// formula and the real "1 &lt;&lt; nwscriptSlot" formula agree only for that one slot. VERIFIED,
/// docs/research/companion-state-sync.md §11.
///
/// Deliberately dependency-free (no reference to <c>Odyssey</c>) so it compiles into
/// <c>KotorCoop.Tests</c> without the engine-facing half of the plugin, the same trick this
/// project already uses for <see cref="EquipApplyDecision"/> and friends.
/// </summary>
public static class EquipSlotMap
{
    /// <summary>Bit flag 1 &lt;&lt; 1 — CSWInventory offset +0x08.</summary>
    public const int InternalSlotBody = 1 << 1;

    /// <summary>Bit flag 1 &lt;&lt; 4 (0x10) — CSWInventory offset +0x14.</summary>
    public const int InternalSlotRightWeapon = 1 << 4;

    /// <summary>Bit flag 1 &lt;&lt; 5 (0x20) — CSWInventory offset +0x18.</summary>
    public const int InternalSlotLeftWeapon = 1 << 5;

    /// <summary>-1 for any NWScript slot id this map does not know — the three visible slots
    /// this project's equipment channels ever ask for (1, 4, 5), and nothing else.</summary>
    public static int ToInternalSlot(int nwscriptSlot) => nwscriptSlot switch
    {
        1 => InternalSlotBody,
        4 => InternalSlotRightWeapon,
        5 => InternalSlotLeftWeapon,
        _ => -1,
    };

    /// <summary>The raw <c>CSWInventory</c> byte offset holding the equipped item's object id
    /// for the same three slots — used to read a slot back directly (no engine call) to VERIFY
    /// an equip actually landed, since <c>CSWSCreature::EquipItem</c> unconditionally returns
    /// success even when its own internal write silently no-ops on an unresolved slot. VERIFIED
    /// against the same 0x005A49C0 disassembly <see cref="ToInternalSlot"/>'s own doc comment
    /// cites, and independently against item-identity.md's already-shipped
    /// <c>KotorGameBridge.ReadEquippedResref</c> offsets.</summary>
    public static int ToSlotByteOffset(int nwscriptSlot) => nwscriptSlot switch
    {
        1 => 0x08,
        4 => 0x14,
        5 => 0x18,
        _ => -1,
    };
}

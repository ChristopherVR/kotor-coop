// Odyssey.Equipment.cs - creature appearance and equipment slot read/apply.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- appearance --------------------------------------------------------------

    // CSWSCreature::UpdateAppearanceDependantInfo(this, int bStore) — rebuilds modeltype, the
    // per-body-part model CExoStrings and the sibling appearance field from the appearance.2da
    // row at creature+0xA60. The engine runs this after EVERY appearance-row change (Disguise,
    // the replication message sites: `mov [esi+0xA60],cx; push 1; mov ecx,esi; call 0x4ED850`).
    // A direct field poke that skips it leaves the body-part strings allocated for the OLD
    // appearance, so a later equip's appearance rebuild reads a dangling CExoString and crashes
    // in CompareNoCase. See docs/research/phantom-equip-crash.md.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> UpdateAppearanceDependantInfoFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004ED850;

    /// <summary>Rebuilds a creature's appearance-derived state after its appearance.2da row was
    /// changed directly, so nothing downstream holds a stale model string. MAIN THREAD ONLY.</summary>
    public static void RebuildAppearance(nint creature)
    {
        if (creature != 0)
        {
            UpdateAppearanceDependantInfoFn(creature, 1);
        }
    }

    // ---- equipment ---------------------------------------------------------------

    /// <summary>CreateItemOnObject(string sTemplate, object oTarget, int nStackSize) -&gt; item.</summary>
    private const nint ExpectedCreateItemOnObject = 0x0052F570;

    /// <summary>ActionEquipItem(object oItem, int nInventorySlot, int bInstant).</summary>
    private const nint ExpectedActionEquipItem = 0x005355F0;

    private static int _routineCreateItemOnObject = -1;
    private static int _routineActionEquipItem = -1;

    /// <summary>nwscript inventory slots for the visible gear.</summary>
    public const int InventorySlotBody = 1;
    public const int InventorySlotRightWeapon = 4;
    public const int InventorySlotLeftWeapon = 5;

    /// <summary>
    /// Gives a creature an item by blueprint and equips it into a slot, through the
    /// engine's own NWScript pair: CreateItemOnObject then ActionEquipItem (instant).
    /// A resref this install does not have fails at the create step and reports failure
    /// 7 — the correct soft behaviour when peers have different mods.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool EquipItemFromTemplate(uint creatureObjectId, byte* resrefAscii, int resrefLength, int inventorySlot, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return false;
        }

        if (_routineCreateItemOnObject < 0)
        {
            _routineCreateItemOnObject = FindRoutine(table, ExpectedCreateItemOnObject);
            _routineActionEquipItem = FindRoutine(table, ExpectedActionEquipItem);
        }

        if (_routineCreateItemOnObject < 0 || _routineActionEquipItem < 0)
        {
            failure = 3;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);
        ExoString resref = new() { CString = (nint)resrefAscii, Length = (uint)resrefLength };

        // CreateItemOnObject(string sTemplate, object oTarget, int nStackSize),
        // pushed in reverse declaration order.
        if (StackPushInteger(vm, 1) == 0
            || StackPushObject(vm, creatureObjectId) == 0
            || StackPushString(vm, &resref) == 0)
        {
            failure = 5;
            return false;
        }

        var create = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedCreateItemOnObject;
        if (create(implementer, _routineCreateItemOnObject, 3) != 0)
        {
            failure = 6;
            return false;
        }

        uint itemId = ObjectInvalid;
        if (StackPopObject(vm, &itemId) == 0)
        {
            failure = 7;
            return false;
        }

        if (itemId == ObjectInvalid)
        {
            // The blueprint does not exist in this install (peer has a mod we lack).
            failure = 7;
            return false;
        }

        // ActionEquipItem applies to the VM's caller object; redirect OBJECT_SELF to
        // the phantom, exactly as the move and pickup dispatches do.
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = creatureObjectId;

        try
        {
            // ActionEquipItem(object oItem, int nInventorySlot, int bInstant), reversed.
            if (StackPushInteger(vm, 1) == 0
                || StackPushInteger(vm, inventorySlot) == 0
                || StackPushObject(vm, itemId) == 0)
            {
                failure = 8;
                return false;
            }

            var equip = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedActionEquipItem;
            if (equip(implementer, _routineActionEquipItem, 3) != 0)
            {
                failure = 9;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 10;
            }

            return true;
        }
        finally
        {
            *(int*)(implementer + ImplHasCallerObject) = savedHasCaller;
            *(uint*)(implementer + ImplCallerObjectId) = savedCallerId;
        }
    }

    /// <summary>
    /// Gives a creature an item by blueprint straight into its inventory, through
    /// CreateItemOnObject alone — no ActionEquipItem, so it lands in the backpack rather
    /// than a visible slot. The first half of <see cref="EquipItemFromTemplate"/>, split out
    /// because a corpse's loot has no slot to equip into; it only has to be there to loot.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static uint GiveItemToInventory(
        uint creatureObjectId, byte* resrefAscii, int resrefLength, int stackSize, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return ObjectInvalid;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return ObjectInvalid;
        }

        if (_routineCreateItemOnObject < 0)
        {
            _routineCreateItemOnObject = FindRoutine(table, ExpectedCreateItemOnObject);
        }

        if (_routineCreateItemOnObject < 0)
        {
            failure = 3;
            return ObjectInvalid;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return ObjectInvalid;
        }

        ExoString resref = new() { CString = (nint)resrefAscii, Length = (uint)resrefLength };

        // CreateItemOnObject(string sTemplate, object oTarget, int nStackSize),
        // pushed in reverse declaration order.
        if (StackPushInteger(vm, Math.Max(1, stackSize)) == 0
            || StackPushObject(vm, creatureObjectId) == 0
            || StackPushString(vm, &resref) == 0)
        {
            failure = 5;
            return ObjectInvalid;
        }

        var create = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedCreateItemOnObject;
        if (create(implementer, _routineCreateItemOnObject, 3) != 0)
        {
            failure = 6;
            return ObjectInvalid;
        }

        uint itemId = ObjectInvalid;
        if (StackPopObject(vm, &itemId) == 0)
        {
            failure = 7;
            return ObjectInvalid;
        }

        if (itemId == ObjectInvalid)
        {
            // The blueprint does not exist in this install (peer has a mod we lack).
            failure = 8;
        }

        return itemId;
    }

    // ---- direct (non-queued) equip, for a live AI-active creature ----------------------

    /// <summary>
    /// CSWSCreature::EquipItem(int nInternalSlot, object oItem, int p3, int p4, int p5) —
    /// the engine's own SYNCHRONOUS "put this on now" primitive (0x004FEB90; confirmed by name
    /// in tools/kotor1_0_3.db, __thiscall, param_size 20 bytes = 5 dwords). Runs to completion
    /// inside the call itself: <c>CanEquipWeapon</c> (0x5A4950), <c>UnequipItem</c> on whatever
    /// already occupies the slot (0x4FAA70), the stat/appearance apply (0x553BB0), the
    /// inventory-slot write (0x5A4BE0), a creature-stats recompute (0x5ADDC0) and the
    /// client-update broadcast — VERIFIED by full disassembly this round. This is the SAME
    /// primitive the workbench's install path and the AI's own
    /// <c>ForceEquipClothing</c>/<c>EquipMostDamagingMeleeWeapon</c>/etc. helpers call
    /// (15 xrefs, all inside those two families) — it is the engine's own answer to "equip
    /// this creature right now," not a script action.
    ///
    /// This is deliberately NOT what <see cref="EquipItemFromTemplate"/> uses.
    /// <c>ActionEquipItem</c>'s VM dispatch (0x005355F0) does not equip synchronously either:
    /// it calls <c>CSWSCreature::AddEquipItemActions</c> (0x004F0420), which appends a node
    /// (action-type byte 6) onto the creature's OWN <c>CSWSCombatRound</c> pending list
    /// (<c>creature+0x9C8</c> -&gt; round, node list at <c>round+0x9B0</c>,
    /// <c>CSWSCombatRound::AddEquipAction</c> 0x004D3C30) — VERIFIED by disassembly. A queued
    /// action needs the engine's own per-tick round processing
    /// (<c>UpdateCombatRoundTimer</c>/<c>CSWVirtualMachineCommands::ExecuteCommandActionAttack</c>'s
    /// own arming calls, see <c>Odyssey.Combat.Attack.cs</c>'s <c>QueueAttackAction</c>) to ever
    /// drain it — nothing in <c>EquipItemFromTemplate</c>'s call chain arms that, and a
    /// mirrored companion's round is repeatedly touched every
    /// <c>PartyPresenceController.DriveOne</c> pass by <see cref="ClaimCreature"/>'s call into
    /// <c>ClearAllActionsFn</c> -&gt; <c>CSWSCombatRound::ClearAllAttacks</c> (0x004D37E0,
    /// VERIFIED to loop the round's five maneuver sub-structures, NOT <c>round+0x9B0</c>
    /// itself — so the node is not directly wiped, but nothing else ever arms the round to
    /// drain it either). A phantom is never claimed at all (<c>PhantomController</c> never
    /// calls <see cref="ClaimCreature"/>), which is the likeliest reason the identical VM call
    /// has shipped and been observed working for a phantom but not for a live party member —
    /// NOT the caller-object redirect, which this round's disassembly of both
    /// <c>CreateItemOnObject</c> (0x0052F570) and <c>ExecuteCommandActionEquipItem</c>
    /// (0x005355F0) confirms correctly targets the explicit creature id all the way down to
    /// <c>CSWSCreature::AcquireItem</c>/<c>AddEquipAction</c>, with no dependency on VM
    /// caller-object state surviving past the call that reads it. See
    /// docs/research/companion-state-sync.md for the full writeup.
    ///
    /// This primitive sidesteps the whole action-queue/combat-round question: no queue, no
    /// arming, immune to <see cref="ClaimCreature"/>'s per-pass wipe, because nothing is left
    /// pending after the call returns.
    ///
    /// MAIN THREAD ONLY. NEEDS-LIVE — the call itself has never been exercised against a
    /// running game; see companion-state-sync.md's live test plan.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, int, int, int, int> EquipItemFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, int, int, int, int>)0x004FEB90;

    /// <summary>
    /// The companion-safe counterpart to <see cref="EquipItemFromTemplate"/>: creates the item
    /// on the target exactly the same way (<see cref="GiveItemToInventory"/>, itself unchanged
    /// and already VERIFIED target-based — no caller-object dependency at all, confirmed by
    /// disassembly of <c>CreateItemOnObject</c> this round), then equips it through the
    /// synchronous <see cref="EquipItemFn"/> instead of the queued VM action.
    ///
    /// <paramref name="nwscriptSlot"/> is the same 1/4/5 convention every other equip caller in
    /// this codebase already uses (<see cref="InventorySlotBody"/> etc.); mapped through
    /// <see cref="EquipSlotMap"/>, itself unit-tested.
    ///
    /// MAIN THREAD ONLY. NEEDS-LIVE.
    /// </summary>
    public static bool EquipItemFromTemplateDirect(
        uint creatureObjectId, byte* resrefAscii, int resrefLength, int nwscriptSlot, out int failure)
    {
        failure = 0;

        int internalSlot = EquipSlotMap.ToInternalSlot(nwscriptSlot);
        if (internalSlot < 0)
        {
            failure = 20;
            return false;
        }

        uint itemId = GiveItemToInventory(creatureObjectId, resrefAscii, resrefLength, 1, out int giveFailure);
        if (itemId == ObjectInvalid)
        {
            failure = giveFailure != 0 ? giveFailure : 21;
            return false;
        }

        nint creature = ResolveServerObject(creatureObjectId);
        if (creature == 0)
        {
            failure = 22;
            return false;
        }

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            failure = 23;
            return false;
        }

        // p3/p4/p5 read as (1, 0, 0) at every clean call site this round's disassembly found
        // (CSWSCreature::ForceEquipClothing's own 0x00501D69). NEEDS-LIVE: their exact meaning
        // is inferred from position, not confirmed against a name.
        EquipItemFn(creature, internalSlot, item, 1, 0, 0);
        return true;
    }
}

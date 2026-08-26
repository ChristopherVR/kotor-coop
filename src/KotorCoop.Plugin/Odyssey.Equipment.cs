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

}

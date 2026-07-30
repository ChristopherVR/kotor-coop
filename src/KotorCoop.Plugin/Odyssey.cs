using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

[StructLayout(LayoutKind.Sequential)]
internal struct Vec3
{
    public float X;
    public float Y;
    public float Z;

    public float Length => MathF.Sqrt((X * X) + (Y * Y) + (Z * Z));
}

/// <summary>CScriptLocation, 0x18 bytes: position then a NORMALISED direction vector.</summary>
[StructLayout(LayoutKind.Sequential, Size = 0x18)]
internal struct ScriptLocation
{
    public Vec3 Position;
    public Vec3 Orientation;
}

/// <summary>
/// What one call to the engine's walk step actually did. The step function's second and
/// third outputs and its return value are undocumented, so they are captured verbatim
/// rather than guessed at.
/// </summary>
internal readonly record struct StepDiagnostics(
    float RequestedDistance,
    int WalkResult,
    int SetPositionResult,
    Vec3 Before,
    Vec3 Stepped,
    Vec3 SpareVector,
    int SpareInt,
    Vec3 AfterCommit);

/// <summary>CExoString, 0x08 bytes: { char* c_string; uint length }.</summary>
[StructLayout(LayoutKind.Sequential, Size = 0x08)]
internal struct ExoString
{
    public nint CString;
    public uint Length;
}

/// <summary>
/// Bindings to swkotor.exe v1.0.3's own engine functions.
///
/// Every address is absolute and hardcoded, which is safe here for two specific reasons:
/// the image has no ASLR (base is always 0x400000), and the public Ghidra symbol database
/// these came from is sha256-matched to this exact executable. See docs/re/spawn.md,
/// docs/re/engine-movement.md and docs/re/object-model.md.
///
/// EVERYTHING HERE IS MAIN-THREAD ONLY. The engine is not reentrant and the plugin's worker
/// thread must never call into it; that is what the frame hook exists for.
///
/// On x86, `unmanaged[Thiscall]` puts the first argument in ECX, pushes the rest
/// right-to-left and lets the callee pop, which is exactly the retail convention.
/// </summary>
internal static unsafe class Odyssey
{
    // ---- globals --------------------------------------------------------------

    private const nint VirtualMachinePtr = 0x007A3A00;

    // CVirtualMachineInternal
    private const int CommandImplementerOffset = 0x3D4;
    private const int StackPointerOffset = 0x1AC;
    private const int CallStackSizeOffset = 0x1C4;

    // CSWVirtualMachineCommands
    private const int CommandsTableOffset = 0x0C;

    private const int RoutineCreateObject = 243;
    private const nint ExpectedCreateObject = 0x0052F820;

    /// <summary>ActionMoveToLocation. Verified live: table[21] == ExecuteCommandMoveToPoint.</summary>
    private const int RoutineActionMoveToLocation = 21;
    private const nint ExpectedMoveToPoint = 0x0053FE00;

    /// <summary>
    /// The routine numbers for the item calls are not documented anywhere trustworthy, so
    /// they are resolved at runtime by scanning the 772-entry routine table for the
    /// handler address the symbol database names. -1 = not resolved yet.
    /// </summary>
    private const int RoutineTableEntries = 772;
    private const nint ExpectedCreateItemOnFloor = 0x00547F60;
    private const nint ExpectedDestroyObject = 0x0052FF20;
    private const nint ExpectedActionPickUpItem = 0x005404F0;

    /// <summary>EffectDamage(int amount, int type, int power) -&gt; effect.</summary>
    private const nint ExpectedEffectDamage = 0x00531AE0;

    /// <summary>ApplyEffectToObject(int durationType, effect e, object target, float dur).</summary>
    private const nint ExpectedApplyEffectToObject = 0x0052E570;

    private static int _routineCreateItemOnFloor = -1;
    private static int _routineDestroyObject = -1;
    private static int _routineActionPickUpItem = -1;
    private static int _routineEffectDamage = -1;
    private static int _routineApplyEffect = -1;

    // CAppManager, reached through the global at 0x007A39FC.
    private const nint AppManagerPtr = 0x007A39FC;
    private const int AppServerOffset = 0x08;

    // CSWVirtualMachineCommands: the VM's notion of OBJECT_SELF.
    private const int ImplHasCallerObject = 0x04;
    private const int ImplCallerObjectId = 0x08;

    private const int ObjectTypeCreature = 1;
    private const int EngineStructureLocation = 2;

    public const uint ObjectInvalid = 0x7F000000;

    // ---- CSWSObject / CSWSCreature field offsets ------------------------------

    public const int ObjPosition = 0x090;
    public const int ObjOrientation = 0x09C;
    public const int ObjCommandable = 0x0E8;   // ClearAllActions no-ops unless this is 1
    public const int ObjDeadSelectable = 0x0F4;

    /// <summary>
    /// Current hit points. NOT +0xE0, which is the stored MAX - that mix-up is why the
    /// dashboard read a health of 0. Verified via CSWSObject::SetCurrentHitPoints, which
    /// writes here, and ExecuteCommandSetMaxHitPoints, which writes +0xE0 then calls it.
    /// </summary>
    public const int ObjCurrentHitPoints = 0x0DC;

    public const int ObjMaxHitPoints = 0x0E0;

    /// <summary>Damage lands but health floors at 1, and the death effect early-outs.</summary>
    public const int ObjMinOneHitPoint = 0x200;
    public const int ObjPlot = 0x0F8;          // plot creatures cannot be killed
    public const int CreStats = 0xA74;         // CSWSCreatureStats*
    public const int StatsFactionId = 0x78;
    public const int CreMoveState = 0xA8C;     // 1 idle, 2 requested, 3 follow, -1 walking
    public const int CreRunFlag = 0xA98;       // 0 walk, nonzero run
    public const int CreMovePermitted = 0x9F0; // bit 1 (& 2) gates the walk animation

    public const int AnimIdle = 0x2710;
    public const int AnimWalk = 0x2712;
    public const int AnimRun = 0x2714;

    private const int VtableSetAnimation = 0x7C;

    // ---- engine functions -----------------------------------------------------

    // CVirtualMachine (the wrapper at [VirtualMachinePtr]).
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int> StackPushInteger =
        (delegate* unmanaged[Thiscall]<nint, int, int>)0x005D1010;

    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, int> StackPushString =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, int>)0x005D1090;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void*, int> StackPushEngineStructure =
        (delegate* unmanaged[Thiscall]<nint, int, void*, int>)0x005D10B0;

    private static readonly delegate* unmanaged[Thiscall]<nint, uint*, int> StackPopObject =
        (delegate* unmanaged[Thiscall]<nint, uint*, int>)0x005D10C0;

    private static readonly delegate* unmanaged[Thiscall]<nint, float, int> StackPushFloat =
        (delegate* unmanaged[Thiscall]<nint, float, int>)0x005D1030;

    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int> StackPushObject =
        (delegate* unmanaged[Thiscall]<nint, uint, int>)0x005D10D0;

    // CServerExoApp::GetGameObject(objectId) -> CGameObject*, the id-to-pointer lookup.
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> GetGameObjectFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x004AE750;

    // CServerExoApp::SetPauseState(int nMask, int bValue). The mask is a BITMASK of pause
    // reasons, not an index; several reasons can hold the game paused at once.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetPauseStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x004AE9A0;

    private const int AppClientOffset = 0x04;

    // CServerExoApp::GetPlayerCreature() -> CSWSCreature*, null before a world is loaded.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetPlayerCreatureFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004B2820;

    // CSWVirtualMachineCommands::ExecuteCommandCreateObject(nCommandId, nParams)
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int> ExecuteCommandCreateObject =
        (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x0052F820;

    // int WalkUpdateLocationDistance(this, float dist, Vec3* newPos, Vec3* outB, int* outC)
    private static readonly delegate* unmanaged[Thiscall]<nint, float, Vec3*, Vec3*, int*, int> WalkStep =
        (delegate* unmanaged[Thiscall]<nint, float, Vec3*, Vec3*, int*, int>)0x00516630;

    // BOOL SetPosition(this, Vec3* pos, int bNotifyArea, int unused, int bSuppressDirty)
    private static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, int, int, int, int> SetPositionFn =
        (delegate* unmanaged[Thiscall]<nint, Vec3*, int, int, int, int>)0x004CD1A0;

    private static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, void> SetOrientationFn =
        (delegate* unmanaged[Thiscall]<nint, Vec3*, void>)0x004CD170;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> ClearAllActionsFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004CCD80;

    // ---- helpers --------------------------------------------------------------

    private static nint Deref(nint p) => *(nint*)p;

    public static Vec3 GetPosition(nint creature) => *(Vec3*)(creature + ObjPosition);

    /// <summary>Resolves the command implementer, or 0 if the VM is not ready.</summary>
    private static nint GetImplementer(out nint virtualMachine, out nint internalVm)
    {
        virtualMachine = 0;
        internalVm = 0;

        nint vm = Deref(VirtualMachinePtr);
        if (vm == 0)
        {
            return 0;
        }

        nint inner = Deref(vm);
        if (inner == 0)
        {
            return 0;
        }

        nint implementer = Deref(inner + CommandImplementerOffset);
        if (implementer == 0)
        {
            return 0;
        }

        virtualMachine = vm;
        internalVm = inner;
        return implementer;
    }

    /// <summary>
    /// Spawns a creature by calling the engine's own NWScript CreateObject implementation.
    /// Returns the new object id, or <see cref="ObjectInvalid"/> on refusal.
    ///
    /// MAIN THREAD ONLY. Three guards are deliberate and must not be removed: they turn
    /// "the layout moved under us" and "a script currently owns the VM stack" from a crash
    /// into a refusal.
    /// </summary>
    public static uint SpawnCreature(string template, Vec3 position, Vec3 facingUnitVector, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return ObjectInvalid;
        }

        // Guard 1: the routine table must still say what we think it says.
        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0)
        {
            failure = 2;
            return ObjectInvalid;
        }

        if (*(nint*)(table + (RoutineCreateObject * 4)) != ExpectedCreateObject)
        {
            failure = 3;
            return ObjectInvalid;
        }

        // Guard 2: never dispatch while a script is mid-execution.
        //
        // Only call_stack_size is checked. The stack pointer was originally checked too, on
        // the assumption that 0 meant idle, but it reads 158 on a live idle game - the VM
        // stack legitimately holds leftover data between scripts. That guard refused three
        // attempts out of four for no good reason. Neutrality is verified afterwards instead.
        int stackPointer = *(int*)(internalVm + StackPointerOffset);
        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return ObjectInvalid;
        }

        ScriptLocation location = new() { Position = position, Orientation = facingUnitVector };

        // The engine copies both the string and the engine structure, so stack locals are
        // correct here and there is nothing to free.
        byte* ascii = stackalloc byte[24];
        int length = 0;
        foreach (char c in template)
        {
            if (length == 23)
            {
                break;
            }

            ascii[length++] = (byte)c;
        }

        ascii[length] = 0;
        ExoString resref = new() { CString = (nint)ascii, Length = (uint)length };

        // Push in REVERSE declaration order:
        //   CreateObject(int nObjectType, string sTemplate, location lLoc, int bUseAppearAnimation)
        if (StackPushInteger(vm, 0) == 0
            || StackPushEngineStructure(vm, EngineStructureLocation, &location) == 0
            || StackPushString(vm, &resref) == 0
            || StackPushInteger(vm, ObjectTypeCreature) == 0)
        {
            failure = 5;
            return ObjectInvalid;
        }

        int status = ExecuteCommandCreateObject(implementer, RoutineCreateObject, 4);
        if (status != 0)
        {
            failure = 6;
            return ObjectInvalid;
        }

        uint objectId = ObjectInvalid;
        if (StackPopObject(vm, &objectId) == 0)
        {
            failure = 7;
            return ObjectInvalid;
        }

        // Guard 3: the whole sequence must have been stack-neutral.
        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
        }

        return objectId;
    }

    /// <summary>
    /// Takes a creature out of AI control so its own behaviour does not fight us.
    ///
    /// Order matters: ClearAllActions no-ops unless the commandable flag is 1, so the
    /// queue must be emptied BEFORE the creature is made non-commandable.
    /// </summary>
    public static void ClaimCreature(nint creature)
    {
        // Deliberately LEAVES the creature commandable. An earlier version set the
        // commandable flag to 0 to stop the AI interfering, which was self-defeating: the
        // action queue is executed by the AI master, so a non-commandable creature ignores
        // the move actions we queue and never walks anywhere.
        *(int*)(creature + ObjCommandable) = 1;
        ClearAllActionsFn(creature, 1);

        // Movement must be permitted or the walk animation is skipped entirely.
        *(int*)(creature + CreMovePermitted) |= 2;
        *(int*)(creature + CreRunFlag) = 0;
    }

    /// <summary>
    /// Queues ActionMoveToLocation (NWScript routine 21) on a creature, so the engine's own
    /// AI walks it there: pathfinding, walkmesh, ground snap and the walk animation all come
    /// for free.
    ///
    /// This replaces an attempt to call WalkUpdateLocationDistance directly. That function
    /// is an internal helper of AIActionMoveToPoint and depends on state the action sets up;
    /// called standalone it returned 1 and never advanced the creature (measured).
    ///
    /// Actions apply to the VM's "caller object", so caller_object_id is redirected to the
    /// phantom for the duration of the dispatch and restored afterwards.
    /// </summary>
    public static bool MoveToLocation(uint creatureObjectId, Vec3 destination, bool run, out int failure)
    {
        failure = 0;

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0 || *(nint*)(table + (RoutineActionMoveToLocation * 4)) != ExpectedMoveToPoint)
        {
            failure = 2;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        // Redirect OBJECT_SELF to the phantom, then put it back exactly as it was.
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = creatureObjectId;

        try
        {
            ScriptLocation location = new()
            {
                Position = destination,
                Orientation = new Vec3 { X = 1f, Y = 0f, Z = 0f },
            };

            // ActionMoveToLocation(location lDestination, int bRun), pushed last-declared first.
            if (StackPushInteger(vm, run ? 1 : 0) == 0
                || StackPushEngineStructure(vm, EngineStructureLocation, &location) == 0)
            {
                failure = 5;
                return false;
            }

            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedMoveToPoint;
            int status = dispatch(implementer, RoutineActionMoveToLocation, 2);

            if (status != 0)
            {
                failure = 6;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 9;
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
    /// Finds a routine's table index by the address of its handler. The nwscript routine
    /// numbering is only trustworthy where it was verified live, so unknown routines are
    /// located by value instead of by guessed index.
    /// </summary>
    private static int FindRoutine(nint table, nint expectedHandler)
    {
        for (int i = 0; i < RoutineTableEntries; i++)
        {
            if (*(nint*)(table + (i * 4)) == expectedHandler)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Resolves an object id to its server-side object through the engine's own lookup:
    /// [AppManagerPtr] -&gt; CAppManager -&gt; +0x08 CServerExoApp -&gt; GetGameObject.
    /// Returns a CSWSObject-shaped pointer (position +0x90, area id +0x8C), or 0.
    /// </summary>
    public static nint ResolveServerObject(uint objectId)
    {
        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return 0;
        }

        nint server = Deref(appManager + AppServerOffset);
        if (server == 0)
        {
            return 0;
        }

        nint gameObject = GetGameObjectFn(server, objectId);
        if (gameObject == 0)
        {
            return 0;
        }

        // Creatures embed CSWSObject at offset 0; items embed it at +0x10 behind a
        // CSWItem base. Rather than trust either layout, test where the object id lands.
        if (*(uint*)(gameObject + 0x04) == objectId)
        {
            return gameObject;
        }

        if (*(uint*)(gameObject + 0x14) == objectId)
        {
            return gameObject + 0x10;
        }

        return 0;
    }

    /// <summary>Reads the area id of a resolved server object (module-lifetime stable).</summary>
    public static uint GetAreaId(nint serverObject) => *(uint*)(serverObject + 0x8C);

    // ---- item floor state ------------------------------------------------------

    private const nint ItemVtable = 0x00748DA0;

    /// <summary>CSWSItem + this = possessor object id. OBJECT_INVALID means "on the floor".</summary>
    private const int ItemPossessor = 0x268;

    internal enum ItemFloorState
    {
        /// <summary>Destroyed, or merged into a stack the picker already carried.</summary>
        Gone,

        OnFloor,

        /// <summary>In somebody's inventory. <c>possessor</c> names whose.</summary>
        Held,
    }

    /// <summary>
    /// Asks the engine's own question about an item: is it lying on the ground?
    ///
    /// <c>CSWSCreature::AIActionPickUpItem</c> gates the whole pickup on
    /// <c>item+0x268 == OBJECT_INVALID</c>, and <c>AcquireItem</c> re-validates it, so this
    /// is the invariant rather than a proxy for it. Picking an item up does not destroy it -
    /// it is moved, keeping its object id - EXCEPT when it merges into a stack the picker
    /// already carries, in which case the object really is freed. Both are pickups.
    /// </summary>
    /// <summary>Diagnostics from the last <see cref="GetItemFloorState"/> call.</summary>
    public static nint LastItemPointer;

    public static uint LastItemIdAtOffset14;

    public static ItemFloorState GetItemFloorState(uint objectId, out uint possessor)
    {
        possessor = ObjectInvalid;
        LastItemPointer = 0;
        LastItemIdAtOffset14 = 0;

        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return ItemFloorState.Gone;
        }

        nint server = Deref(appManager + AppServerOffset);
        if (server == 0)
        {
            return ItemFloorState.Gone;
        }

        // The raw pointer, not the +0x10-adjusted one: the possessor field is indexed from
        // the CSWSItem base, which is not where CSWSObject starts for an item.
        //
        // Identify the layout by where the object id lands rather than by the vtable. The
        // id test is the one already proven against this build; a vtable equality check
        // looked stricter but simply rejected every item, which showed up as an item being
        // reported picked up 36ms after it was spawned - and that removal then propagated
        // back and deleted the item in every instance.
        nint pointer = GetGameObjectFn(server, objectId);
        LastItemPointer = pointer;

        if (pointer == 0)
        {
            return ItemFloorState.Gone;
        }

        LastItemIdAtOffset14 = *(uint*)(pointer + 0x14);

        // Accept either shape rather than assuming one. An item's CSWSObject is embedded at
        // CSWSItem+0x10, so depending on which of the two the lookup hands back, the object
        // id sits at +0x14 (CSWSItem base) or +0x04 (the CSWSObject inside it). Assuming
        // the base is what made this report every freshly spawned item as already gone —
        // and because a local vanish is broadcast, that false positive deleted the item in
        // every instance.
        nint itemBase;

        if (LastItemIdAtOffset14 == objectId)
        {
            itemBase = pointer;
        }
        else if (*(uint*)(pointer + 0x04) == objectId)
        {
            itemBase = pointer - 0x10;
        }
        else
        {
            return ItemFloorState.Gone;
        }

        // Sanity-check the layout before trusting a field 0x268 bytes in.
        if (*(nint*)itemBase != ItemVtable)
        {
            return ItemFloorState.Gone;
        }

        possessor = *(uint*)(itemBase + ItemPossessor);
        return possessor == ObjectInvalid ? ItemFloorState.OnFloor : ItemFloorState.Held;
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

    // ---- doors -------------------------------------------------------------------

    private const nint DoorVtable = 0x007498B0;

    /// <summary>CSWSDoor + this = committed open state: 0 closed, 1/2 open, 3 destroyed.</summary>
    private const int DoorOpenState = 0x2CC;

    /// <summary>CSWSDoor + this = pending open state the engine will commit after the swing.</summary>
    private const int DoorPendingOpenState = 0x2CD;

    /// <summary>CSWSDoor + this = locked byte. A plain write is the correct apply.</summary>
    private const int DoorLocked = 0x2C4;

    // CSWSDoor::SetOpenState(byte nState, int bDeferred). Verified by disassembly: writes
    // the pending state at +0x2CD (and +0x2CC too when bDeferred is 0) and dispatches the
    // matching door animation through vtable +0x7C. The engine's own OpenDoor calls this
    // with bDeferred = 1 and lets the swing commit the state.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetOpenStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x00589600;

    // CSWSDoor::MoveToNextOpenState(). Commits pending -> current and updates the door's
    // surface mesh, i.e. the walkmesh. This is why raw writes to +0x2CC are forbidden:
    // they move the collision without the model.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> MoveToNextOpenStateFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x00589680;

    /// <summary>Resolves a door by object id, vtable-checked. 0 if absent or not a door.</summary>
    public static nint ResolveDoor(uint objectId)
    {
        nint door = ResolveServerObject(objectId);
        return door != 0 && *(nint*)door == DoorVtable ? door : 0;
    }

    public static byte GetDoorOpenState(nint door) => *(byte*)(door + DoorOpenState);

    public static byte GetDoorPendingOpenState(nint door) => *(byte*)(door + DoorPendingOpenState);

    public static bool IsDoorLocked(nint door) => *(byte*)(door + DoorLocked) != 0;

    public static void UnlockDoor(nint door) => *(byte*)(door + DoorLocked) = 0;

    /// <summary>
    /// Asks the door to swing to a state through the engine's own deferred path: the
    /// animation plays and the engine commits state + walkmesh when the swing lands.
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void SetDoorOpenState(nint door, int state) => SetOpenStateFn(door, state, 1);

    /// <summary>
    /// Forces the pending open state to commit (state + walkmesh). Fallback for a swing
    /// whose commit never arrived. MAIN THREAD ONLY.
    /// </summary>
    public static void CommitDoorOpenState(nint door) => MoveToNextOpenStateFn(door);

    // ---- pause state -----------------------------------------------------------

    /// <summary>
    /// All six auto-pause options, as a mask over CClientOptions + 0x14: end of combat
    /// round, enemy sighted, mine sighted, party killed, action menu, new target selected.
    /// </summary>
    private const uint AutoPauseOptionMask = 0x0001F800;

    private const int ClientOptionsFlags = 0x14;

    /// <summary>
    /// Turns off every auto-pause. Two instances that pause themselves independently
    /// desynchronise on their own, and "party killed" in particular fires constantly once
    /// players start dying.
    ///
    /// This clears the same option bits the in-game options screen writes, rather than
    /// fighting the pause bitmask every frame — all six off is a configuration the game
    /// ships as valid, and manual, tutorial and GUI pauses keep working.
    /// </summary>
    public static bool SuppressAutoPause()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint options = Deref(Deref(client + 0x04) + 0x04);
        if (options == 0)
        {
            return false;
        }

        uint* flags = (uint*)(options + ClientOptionsFlags);
        if ((*flags & AutoPauseOptionMask) == 0)
        {
            return false;
        }

        *flags &= ~AutoPauseOptionMask;
        return true;
    }

    /// <summary>
    /// The pause reason the window-deactivation path sets. `Global::DeactivateRenderWindow`
    /// (`0x00401D90`) calls `SetPauseState(2, 1)` when the window loses focus, and that
    /// runs `CWorldTimer::PauseWorldTimer`, so an unfocused instance stops simulating
    /// entirely. Fatal for co-op, where at most one of the two windows can be focused.
    /// </summary>
    public const int PauseReasonUnfocused = 2;

    /// <summary>CServerExoAppInternal + this = the byte of pause-reason bits.</summary>
    private const int ServerPauseMaskOffset = 0x10078;

    private const int AppServerOffsetInternal = 0x04;

    /// <summary>Resolves CServerExoApp, or 0 before a module is loaded.</summary>
    private static nint GetServer()
    {
        nint appManager = Deref(AppManagerPtr);
        return appManager == 0 ? 0 : Deref(appManager + AppServerOffset);
    }

    /// <summary>Resolves CClientExoApp, which exists from startup, menu included.</summary>
    private static nint GetClient()
    {
        nint appManager = Deref(AppManagerPtr);
        return appManager == 0 ? 0 : Deref(appManager + AppClientOffset);
    }

    /// <summary>
    /// Asks the game to load a save, exactly as the quick-load path does: it is the same
    /// client-to-server request the save menu ends up sending, so the game goes straight
    /// into the world without the menu being involved.
    ///
    /// <paramref name="saveId"/> is the numeric prefix of the save directory, so
    /// `000002 - Game1` is 2.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>

    /// <summary>True once the server object exists (it does not at the main menu).</summary>
    public static bool HasServer => GetServer() != 0;

    // ---- module identity and transitions ----------------------------------------

    /// <summary>CServerExoAppInternal + this = CServerInfo* (symbol database: server_info).</summary>
    private const int ServerInfoOffset = 0x10004;

    /// <summary>CServerExoAppInternal + this = server mode; 2 while a module is running.</summary>
    private const int ServerModeOffset = 0x10008;

    /// <summary>CServerExoAppInternal + this = nonzero while a module load is in progress.</summary>
    private const int LoadingModuleOffset = 0x100B4;

    /// <summary>CServerInfo + this = module name, CExoString (symbol database: module_name).</summary>
    private const int ServerInfoModuleName = 0xFC;

    /// <summary>
    /// CSWSArea + this = trans_pending (symbol database), a byte that goes 1 roughly half a
    /// second before a door/trigger transition stores the area. That store serialises every
    /// live object — including anything this plugin spawned — into the saved module, so it
    /// is the window in which phantoms and replicated items must be destroyed.
    /// </summary>
    private const int AreaTransPending = 0x2C4;

    // CServerExoApp::GetObjectArray() -> CGameObjectArray*. UnloadModule destroys the array
    // and allocates a fresh one, which makes this pointer a natural module-lifetime token.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetObjectArrayFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AED70;

    // CServerExoApp::GetModule() -> CSWSModule*. A KOTOR 1 module holds exactly one area,
    // whose pointer sits at +0x220 (symbol database: area).
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AE6B0;

    private const int ModuleAreaOffset = 0x220;

    /// <summary>The player's CSWSCreature, resolved through the engine's own lookup, or 0.</summary>
    public static nint GetPlayerCreaturePointer()
    {
        nint server = GetServer();
        return server == 0 ? 0 : GetPlayerCreatureFn(server);
    }

    /// <summary>
    /// Identifies the currently loaded world. Returns false while there is no world or a
    /// module transition is mid-flight — every cached object pointer and object id is (or
    /// is about to be) invalid, and nothing should run.
    ///
    /// The epoch combines the server object array pointer, which UnloadModule replaces,
    /// with a hash of the module name string. Area object ids are deliberately not used:
    /// they come from a counter that resets on transition, so different modules routinely
    /// share one.
    /// </summary>
    public static bool TryGetWorldEpoch(out ulong epoch)
    {
        epoch = 0;

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0)
        {
            return false;
        }

        if (*(int*)(internalServer + LoadingModuleOffset) != 0
            || *(int*)(internalServer + ServerModeOffset) != 2)
        {
            return false;
        }

        nint objectArray = GetObjectArrayFn(server);
        if (objectArray == 0)
        {
            return false;
        }

        uint nameHash = 2166136261;
        nint serverInfo = Deref(internalServer + ServerInfoOffset);
        if (serverInfo != 0)
        {
            nint text = Deref(serverInfo + ServerInfoModuleName);
            uint length = *(uint*)(serverInfo + ServerInfoModuleName + 4);
            if (text != 0 && length is > 0 and <= 32)
            {
                for (uint i = 0; i < length; i++)
                {
                    nameHash = (nameHash ^ *(byte*)(text + (nint)i)) * 16777619;
                }
            }
        }

        epoch = ((ulong)(uint)objectArray << 32) | nameHash;
        return true;
    }

    /// <summary>
    /// True when the player's area has flagged an imminent transition (trans_pending).
    /// This is the pre-store flush window: StoreCurrentModule will serialise the area on
    /// the way out, and SaveCreatures only skips party members, so anything the plugin
    /// spawned would come back as a permanent NPC on a revisit — and shift the id counter
    /// for the rest of the playthrough.
    /// </summary>
    public static bool IsAreaTransitionPending()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint module = GetModuleFn(server);
        if (module == 0)
        {
            return false;
        }

        nint area = Deref(module + ModuleAreaOffset);
        return area != 0 && *(byte*)(area + AreaTransPending) != 0;
    }

    // ---- map markers -------------------------------------------------------------
    //
    // A peer's position shows on the M-screen map as a map-note pin. The map's draw code
    // (CSWGuiMapHider::Draw) re-reads each map-note waypoint's position field EVERY frame
    // and converts it to a pixel, so a waypoint whose +0x90 we move per tick moves its pin
    // live — no GUI patching. The waypoint is constructed DIRECTLY rather than through the
    // NWScript CreateObject routine, because that routine has an engine bug for waypoints:
    // it makes the object but forgets to return its id (returns OBJECT_INVALID).

    /// <summary>CSWSWaypoint total size, for the engine allocator.</summary>
    private const int WaypointSize = 0x240;

    /// <summary>CSWSWaypoint + this = HasMapNote (nonzero -&gt; the map includes it).</summary>
    private const int WaypointHasMapNote = 0x228;

    /// <summary>CSWSWaypoint + this = MapNoteEnabled (nonzero -&gt; the pin is drawn), re-read
    /// every frame by the map draw.</summary>
    private const int WaypointMapNoteEnabled = 0x22C;

    // Global::operator_new(size) — the engine allocator, so the object can be freed by the
    // engine's own teardown.
    private static readonly delegate* unmanaged[Cdecl]<int, nint> OperatorNewFn =
        (delegate* unmanaged[Cdecl]<int, nint>)0x006FA7E6;

    // CSWSWaypoint::Constructor(this, objectId). Passing 0x7F000000 makes the constructor
    // allocate a real object id through AddInternalObject, exactly as creature spawning does.
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> WaypointCtorFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x005C7E70;

    // CSWSWaypoint::AddToArea(this, area, x, y, z, bStore). Appends the object id to the
    // area's object array, which is what the map's InitializeMapNotes scans.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, void> WaypointAddToAreaFn =
        (delegate* unmanaged[Thiscall]<nint, nint, float, float, float, int, void>)0x005C8580;

    // CSWSWaypoint::RemoveFromArea(this, area). Half of teardown; the destructor is the
    // other half.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> WaypointRemoveFromAreaFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x005C8350;

    /// <summary>The server-side area of the loaded module, or 0.</summary>
    private static nint GetAreaPointer()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint module = GetModuleFn(server);
        return module == 0 ? 0 : Deref(module + ModuleAreaOffset);
    }

    /// <summary>
    /// Creates a map-note waypoint at a position and returns its object id (0 on failure).
    /// The pin appears on the map screen next time it is opened (the map snapshots note
    /// MEMBERSHIP on open, but reads each note's POSITION live), so spawn it at area load.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static uint SpawnMapMarker(Vec3 position, out nint waypoint)
    {
        waypoint = 0;

        nint area = GetAreaPointer();
        if (area == 0)
        {
            return ObjectInvalid;
        }

        nint wp = OperatorNewFn(WaypointSize);
        if (wp == 0)
        {
            return ObjectInvalid;
        }

        WaypointCtorFn(wp, 0x7F000000);
        SetPositionFn(wp, &position, 1, 1, 0);
        WaypointAddToAreaFn(wp, area, position.X, position.Y, position.Z, 0);

        *(int*)(wp + WaypointHasMapNote) = 1;
        *(int*)(wp + WaypointMapNoteEnabled) = 1;

        waypoint = wp;
        return *(uint*)(wp + 0x04);
    }

    /// <summary>Moves a map marker; its pin follows on the next map draw. MAIN THREAD ONLY.</summary>
    public static void MoveMapMarker(nint waypoint, Vec3 position)
    {
        if (waypoint != 0)
        {
            *(Vec3*)(waypoint + ObjPosition) = position;
        }
    }

    /// <summary>
    /// Destroys a map marker through the engine: remove from the area, then the object's
    /// own deleting destructor (vtable slot 0, flag 1). Required in the pre-store flush
    /// window — a waypoint left in the area is serialised into the saved module.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void DestroyMapMarker(nint waypoint)
    {
        if (waypoint == 0)
        {
            return;
        }

        nint area = GetAreaPointer();
        if (area != 0)
        {
            WaypointRemoveFromAreaFn(waypoint, area);
        }

        nint vtable = *(nint*)waypoint;
        var destructor = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)vtable);
        destructor(waypoint, 1);
    }

    // ---- death pose --------------------------------------------------------------

    /// <summary>
    /// Ambient animation states. CSWSCreature::SetAnimation remaps the idle (0x2710)
    /// through the state byte at creature+0x4C4 using the jump table at 0x004F0F50:
    /// states 3, 4 and 14 produce the three death poses (0x2718, 0x2716, 0x27AC); values
    /// outside the 2..14 table fall through to the plain idle, so 1 is "normal".
    /// </summary>
    public const int AmbientStateNormal = 1;
    public const int AmbientStateDead = 4;

    // CSWSCreature::SetAmbientAnimationState(byte). Plain store to +0x4C4, with a quirk:
    // for states 3/4 it substitutes 14 when the current animation is one of the
    // kneeling/prone set, which is exactly the behaviour we want anyway.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetAmbientAnimationStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004EFEF0;

    /// <summary>
    /// Lays a creature down in a death pose, or stands it back up. The animation field
    /// cannot do this: the client's animation sync explicitly rejects all three death
    /// constants (trap 21b), so a raw 0x2716 write does nothing at all. The engine's own
    /// route is the ambient state plus a re-issued idle, which SetAnimation then remaps
    /// into the pose — and pushes to the render side.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void SetDownState(nint creature, bool down)
    {
        if (creature == 0)
        {
            return;
        }

        SetAmbientAnimationStateFn(creature, down ? AmbientStateDead : AmbientStateNormal);
        SetAnimation(creature, AnimIdle);
    }

    /// <summary>Empties a creature's action queue (a queued move would stand a "dead"
    /// phantom back up mid-pose). MAIN THREAD ONLY.</summary>
    public static void ClearActions(nint creature)
    {
        if (creature != 0)
        {
            ClearAllActionsFn(creature, 1);
        }
    }

    // ---- journal -----------------------------------------------------------------

    /// <summary>CServerExoAppInternal + this = party table (embedded CSWPartyTable).</summary>
    private const int PartyTableOffset = 0x1B770;

    /// <summary>CSWPartyTable + this = quest journal, CExoArrayList&lt;SJournalEntry&gt;
    /// { SJournalEntry* data; int size; int capacity }.</summary>
    private const int PartyTableJournal = 0x11C;

    // CSWSJournal::SetState(CExoString* tag, int state, int bOverrideHigher). Walks the
    // entry list comparing tags case-insensitively, updates or appends, and sends the
    // journal-updated message so the GUI refreshes — which is exactly why applying a
    // peer's quest state must go through this call and not a raw entry write.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, int, int, void> JournalSetStateFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, int, int, void>)0x005C5A40;

    /// <summary>
    /// Advances a quest journal entry to the given state through the engine's own path.
    /// bOverrideHigher stays 0, so the engine's refusal to lower a quest state holds and
    /// the cross-instance merge stays monotonic even against a replayed packet.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetJournalState(byte* tagAscii, int tagLength, int state, out int failure)
    {
        failure = 0;

        if (tagLength <= 0)
        {
            failure = 1;
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            failure = 2;
            return false;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0)
        {
            failure = 3;
            return false;
        }

        nint journal = internalServer + PartyTableOffset + PartyTableJournal;

        // Length is capacity (strlen + 1), the engine's own CExoString convention.
        ExoString tag = new() { CString = (nint)tagAscii, Length = (uint)(tagLength + 1) };
        JournalSetStateFn(journal, &tag, state, 0);
        return true;
    }

    // ---- creature naming ---------------------------------------------------------

    /// <summary>CSWSCreatureStats + this = first_name, a CExoLocString (symbol database).
    /// CSWSCreature::GetFirstName (0x004F8310) returns exactly this address.</summary>
    private const int StatsFirstName = 0x34;

    // CExoLocString::ClearLocString: empties the localized string in place.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ClearLocStringFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005E9EA0;

    // CExoLocString::AddString(int nID, CExoString* text, int gender). Verified against
    // the engine's own rename flow at 0x00521B4C, which calls AddString(0, &str, 0) on a
    // fresh CExoLocString and assigns it onto stats+0x34; operating on the field in place
    // skips the temp. The text is copied by the engine, so a stack-local source is fine.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, ExoString*, int, void> LocStringAddStringFn =
        (delegate* unmanaged[Thiscall]<nint, int, ExoString*, int, void>)0x005EA340;

    /// <summary>
    /// Sets a creature's first name — what the selection band above its head shows. A
    /// spawned phantom otherwise has no name at all and the band stays blank (the
    /// commoner blueprint defines none), which reads as "nothing there" during play.
    ///
    /// MAIN THREAD ONLY. Call before the client twin exists (right after spawn) so every
    /// copy the engine makes carries the name from the start.
    /// </summary>
    public static bool SetCreatureFirstName(nint creature, byte* nameAscii, int length)
    {
        if (creature == 0 || length <= 0)
        {
            return false;
        }

        nint stats = Deref(creature + CreStats);
        if (stats == 0)
        {
            return false;
        }

        nint locString = stats + StatsFirstName;
        ClearLocStringFn(locString);

        // Length is capacity (strlen + 1), matching what the engine's own CExoString
        // constructor from char* stores. nameAscii must be null-terminated.
        ExoString text = new() { CString = (nint)nameAscii, Length = (uint)(length + 1) };
        LocStringAddStringFn(locString, 0, &text, 0);
        return true;
    }

    // ---- module travel -----------------------------------------------------------

    // CServerExoApp::SetMoveToModuleString(CExoString*): assigns the destination module
    // name into CServerExoAppInternal+0x10084 through CExoString::operator= (0x005E5C50),
    // so the engine copies the characters with its own allocator — a stack-local source
    // string is safe. Verified by disassembly against the GOG image (disk == memory).
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, void> SetMoveToModuleStringFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, void>)0x004AECD0;

    // CServerExoApp::SetMoveToModuleStartWaypoint(CExoString*): same shape, +0x1008C.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, void> SetMoveToModuleStartWaypointFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, void>)0x004AED30;

    // CServerExoApp::SetMoveToModulePending(int): plain dword store to +0x10080. The
    // engine's own MainLoop notices it and performs the entire transition.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetMoveToModulePendingFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004AECC0;

    /// <summary>
    /// True when the live module's name equals the given ascii name, ignoring case. Reads
    /// the CServerInfo module-name string (the same one the epoch hashes), so it is only
    /// meaningful while the world is stable.
    /// </summary>
    public static bool ModuleNameEquals(byte* ascii, int length)
    {
        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0)
        {
            return false;
        }

        nint serverInfo = Deref(internalServer + ServerInfoOffset);
        if (serverInfo == 0)
        {
            return false;
        }

        nint text = Deref(serverInfo + ServerInfoModuleName);
        if (text == 0)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            byte live = *(byte*)(text + i);
            byte want = ascii[i];

            // Fold A-Z onto a-z; module names are plain ascii resrefs.
            if ((uint)(live - 'A') <= 'Z' - 'A')
            {
                live += 0x20;
            }

            if ((uint)(want - 'A') <= 'Z' - 'A')
            {
                want += 0x20;
            }

            if (live != want || live == 0)
            {
                return false;
            }
        }

        return *(byte*)(text + length) == 0;
    }

    /// <summary>
    /// Sends this instance to another module through the engine's own warp path: set the
    /// destination string and optional start waypoint, then raise the pending flag. The
    /// engine's MainLoop performs the whole transition with every invariant intact —
    /// party collection, save-out of the old area, loading screen, entry placement —
    /// because this is exactly what the shipped `warp` cheat calls.
    ///
    /// The caller must flush plugin-spawned objects FIRST: the transition stores the
    /// outgoing area, and anything left standing becomes a permanent resident of it.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TravelToModule(
        byte* moduleAscii, int moduleLength, byte* waypointAscii, int waypointLength, out int failure)
    {
        failure = 0;

        nint server = GetServer();
        if (server == 0)
        {
            failure = 1;
            return false;
        }

        if (moduleLength <= 0)
        {
            failure = 2;
            return false;
        }

        ExoString module = new() { CString = (nint)moduleAscii, Length = (uint)moduleLength };
        SetMoveToModuleStringFn(server, &module);

        // Always written, even when empty: a stale waypoint from a previous request would
        // otherwise aim this transition at a tag that does not exist in the destination.
        ExoString waypoint = new()
        {
            CString = waypointLength > 0 ? (nint)waypointAscii : 0,
            Length = (uint)Math.Max(waypointLength, 0),
        };
        SetMoveToModuleStartWaypointFn(server, &waypoint);

        SetMoveToModulePendingFn(server, 1);
        return true;
    }

    /// <summary>
    /// True when the server can hand back a player creature, which is the cheapest
    /// "are we actually in a world" test available.
    /// </summary>
    public static bool HasPlayer
    {
        get
        {
            nint server = GetServer();
            return server != 0 && GetPlayerCreatureFn(server) != 0;
        }
    }

    /// <summary>Reads the raw pause-reason bitmask, or 0 when there is no server yet.</summary>
    public static int GetPauseMask()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        return internalServer == 0 ? 0 : *(byte*)(internalServer + ServerPauseMaskOffset);
    }

    /// <summary>
    /// Clears one pause reason through the engine's own setter, so the world timer is
    /// unpaused and the state change is announced the way the game expects. Returns true
    /// if the reason was set and has now been cleared.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ClearPauseReason(int mask)
    {
        if ((GetPauseMask() & mask) == 0)
        {
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        SetPauseStateFn(server, mask, 0);
        return true;
    }

    /// <summary>The player's own pause toggle. See EngineGlobals on the agent side; the
    /// shared-pause mirror uses this same bit so either player can pause both worlds.</summary>
    public const int PauseReasonPlayer = 0x01;

    // CClientExoApp::SetPauseState(int nMask, int bValue). The CLIENT keeps its own pause
    // mask and its own world timers, entirely separate from the server's — verified live:
    // pausing only the server left the bit cleared again within a frame, because the
    // client drives the pair back to agreement. Both halves must be set together.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> ClientSetPauseStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x005ED650;

    /// <summary>CClientExoAppInternal + this = the client's own pause-reason bitmask.</summary>
    private const int ClientPauseMaskOffset = 0x1CC;

    /// <summary>Reads the client's pause-reason bitmask, or 0 when there is no client.</summary>
    public static int GetClientPauseMask()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint internalClient = Deref(client + AppClientOffset);
        return internalClient == 0 ? 0 : *(byte*)(internalClient + ClientPauseMaskOffset);
    }

    /// <summary>
    /// Sets or clears a pause reason on BOTH the client and the server, which is what a
    /// real pause does. Pausing one alone does not hold: the two keep separate masks and
    /// separate world timers, and they reconcile against each other every frame.
    ///
    /// Returns true when this call changed something, so a caller can log an edge rather
    /// than every re-assertion.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetPlayerPause(bool paused)
    {
        int value = paused ? 1 : 0;
        bool changed = false;

        nint client = GetClient();
        if (client != 0 && ((GetClientPauseMask() & PauseReasonPlayer) != 0) != paused)
        {
            ClientSetPauseStateFn(client, PauseReasonPlayer, value);
            changed = true;
        }

        nint server = GetServer();
        if (server != 0 && ((GetPauseMask() & PauseReasonPlayer) != 0) != paused)
        {
            SetPauseStateFn(server, PauseReasonPlayer, value);
            changed = true;
        }

        return changed;
    }

    /// <summary>True when the player-pause reason is held on either half.</summary>
    public static bool IsPlayerPaused =>
        ((GetPauseMask() | GetClientPauseMask()) & PauseReasonPlayer) != 0;

    /// <summary>
    /// Sets one pause reason through the engine's own setter, so CWorldTimer actually
    /// stops (a raw bit write would flag the pause without pausing anything). Returns
    /// false when there is no server, or the reason was already set.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetPauseReason(int mask)
    {
        if ((GetPauseMask() & mask) != 0)
        {
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        SetPauseStateFn(server, mask, 1);
        return true;
    }

    /// <summary>
    /// Puts a real, clickable item on the ground by calling the engine's own NWScript
    /// CreateItemOnFloor(string sTemplate, location lLoc, int bUseAppearAnimation).
    /// Returns the new object id, or <see cref="ObjectInvalid"/> on refusal.
    ///
    /// MAIN THREAD ONLY. Same guard structure as <see cref="SpawnCreature"/>.
    /// </summary>
    public static uint SpawnItemOnFloor(byte* resrefAscii, int resrefLength, Vec3 position, out int failure)
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

        if (_routineCreateItemOnFloor < 0)
        {
            _routineCreateItemOnFloor = FindRoutine(table, ExpectedCreateItemOnFloor);
        }

        if (_routineCreateItemOnFloor < 0)
        {
            failure = 3;
            return ObjectInvalid;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return ObjectInvalid;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        ScriptLocation location = new()
        {
            Position = position,
            Orientation = new Vec3 { X = 1f, Y = 0f, Z = 0f },
        };

        ExoString resref = new() { CString = (nint)resrefAscii, Length = (uint)resrefLength };

        // Push in REVERSE declaration order:
        //   CreateItemOnFloor(string sTemplate, location lLoc, int bUseAppearAnimation)
        if (StackPushInteger(vm, 0) == 0
            || StackPushEngineStructure(vm, EngineStructureLocation, &location) == 0
            || StackPushString(vm, &resref) == 0)
        {
            failure = 5;
            return ObjectInvalid;
        }

        var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedCreateItemOnFloor;
        int status = dispatch(implementer, _routineCreateItemOnFloor, 3);
        if (status != 0)
        {
            failure = 6;
            return ObjectInvalid;
        }

        uint objectId = ObjectInvalid;
        if (StackPopObject(vm, &objectId) == 0)
        {
            failure = 7;
            return ObjectInvalid;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
        }

        return objectId;
    }

    /// <summary>
    /// Removes an object from the world via the engine's own NWScript
    /// DestroyObject(object oDestroy, float fDelay, int bNoFade, float fDelayUntilFade).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool DestroyObjectById(uint objectId, out int failure)
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

        if (_routineDestroyObject < 0)
        {
            _routineDestroyObject = FindRoutine(table, ExpectedDestroyObject);
        }

        if (_routineDestroyObject < 0)
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

        // Push in REVERSE declaration order; no fade and no delay, so the item is gone
        // the instant the remote pickup lands rather than lingering as a ghost.
        if (StackPushFloat(vm, 0f) == 0
            || StackPushInteger(vm, 1) == 0
            || StackPushFloat(vm, 0f) == 0
            || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedDestroyObject;
        int status = dispatch(implementer, _routineDestroyObject, 4);
        if (status != 0)
        {
            failure = 6;
            return false;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
        }

        return true;
    }

    /// <summary>Bit 0x04 of creature+0x9F0: attacking permitted. Cleared on phantoms so a
    /// stray hostile action can never make one swing at this instance's world.</summary>
    private const int CreAttackPermittedBit = 0x04;

    // CSWSCreature::PacifyCreature: walks the area object list and releases everything
    // whose combat round is targeting this creature. __thiscall, no arguments.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PacifyCreatureFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x004FD960;

    /// <summary>
    /// Marks a spawned creature as belonging to a remote player.
    ///
    /// The phantom keeps its blueprint faction. `n_commm` ships as faction 5 (Neutral),
    /// which reads as roughly 50 reputation in both directions: enemies ignore it and the
    /// local party ignores it.
    ///
    /// It deliberately does NOT take the local player's faction, which an earlier version
    /// did. Faction 0 is PLAYER, so that made enemies treat the phantom exactly as they
    /// treat a PC — a valid, hostile target. Combined with plot, which zeroes all incoming
    /// damage, the result was an immortal aggro sink: `CreateNewAttackActions` only
    /// releases a target that reads dead, a plot creature's health never moves, so the
    /// first enemy to pick a phantom attacked it forever.
    ///
    /// The current shape (7.4's design): NO plot — plot zeroes every incoming damage
    /// component, so splash damage would visibly bounce off a teammate — but min-1-hp,
    /// so this instance still cannot kill a body only the remote player has authority
    /// over. min1hp alone re-creates the aggro lock (the target never reads dead), which
    /// is why <see cref="PacifyPhantom"/> exists and is called whenever the owning peer
    /// leaves combat or goes down. The attack bit is cleared so the phantom can never
    /// swing back and start a fight of its own.
    /// </summary>
    public static void MarkAsRemotePlayer(nint phantom)
    {
        *(int*)(phantom + ObjPlot) = 0;
        *(int*)(phantom + ObjMinOneHitPoint) = 1;
        *(int*)(phantom + ObjDeadSelectable) = 0;
        *(int*)(phantom + CreMovePermitted) &= ~CreAttackPermittedBit;
    }

    /// <summary>
    /// Releases every local creature currently locked onto this phantom. Required
    /// whenever the owning peer leaves combat (or goes down): a min-1-hp body never
    /// reads dead, so nothing would ever let go on its own.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void PacifyPhantom(nint phantom)
    {
        if (phantom != 0)
        {
            PacifyCreatureFn(phantom);
        }
    }

    /// <summary>
    /// Makes a creature walk over and pick an item up, via the engine's own
    /// ActionPickUpItem. This is the same action a mouse click on a ground item produces,
    /// which is the point: it exercises the real pickup path rather than simulating one.
    ///
    /// Actions apply to the VM's caller object, so OBJECT_SELF is redirected to the
    /// creature for the dispatch and restored afterwards.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PickUpItem(uint creatureObjectId, uint itemObjectId, out int failure)
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

        if (_routineActionPickUpItem < 0)
        {
            _routineActionPickUpItem = FindRoutine(table, ExpectedActionPickUpItem);
        }

        if (_routineActionPickUpItem < 0)
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
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = creatureObjectId;

        try
        {
            if (StackPushObject(vm, itemObjectId) == 0)
            {
                failure = 5;
                return false;
            }

            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedActionPickUpItem;
            if (dispatch(implementer, _routineActionPickUpItem, 1) != 0)
            {
                failure = 6;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 9;
            }

            return true;
        }
        finally
        {
            *(int*)(implementer + ImplHasCallerObject) = savedHasCaller;
            *(uint*)(implementer + ImplCallerObjectId) = savedCallerId;
        }
    }

    // ---- health and death ------------------------------------------------------

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetCurrentHitPointsFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004CC280;

    public static ushort GetCurrentHitPoints(nint obj) => *(ushort*)(obj + ObjCurrentHitPoints);

    public static ushort GetMaxHitPoints(nint obj) => *(ushort*)(obj + ObjMaxHitPoints);

    /// <summary>
    /// Sets current hit points through the engine's own setter, which also honours the
    /// min-1-hp flag. No dirty flag is involved: the server-to-client update diffs live
    /// health against a snapshot every tick, so the health bar follows on its own.
    /// </summary>
    public static void SetCurrentHitPoints(nint obj, int hitPoints) => SetCurrentHitPointsFn(obj, hitPoints);

    /// <summary>
    /// Makes damage non-lethal (or lethal again). A mirrored enemy is pinned like this so
    /// the local player can fight it normally - damage lands, numbers float, the fight
    /// feels real - but only the owning peer can actually kill it.
    /// </summary>
    public static void SetMinOneHitPoint(nint obj, bool enabled) =>
        *(int*)(obj + ObjMinOneHitPoint) = enabled ? 1 : 0;

    /// <summary>
    /// Kills a creature the way the game does, by applying an instant lethal damage
    /// effect: real death animation, corpse, loot and OnDeath script rather than a health
    /// value poked to zero, which would leave an object the engine considers dead but
    /// never ran the death path for.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool KillCreature(uint objectId, out int failure)
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

        if (_routineEffectDamage < 0)
        {
            _routineEffectDamage = FindRoutine(table, ExpectedEffectDamage);
            _routineApplyEffect = FindRoutine(table, ExpectedApplyEffectToObject);
        }

        if (_routineEffectDamage < 0 || _routineApplyEffect < 0)
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

        // ApplyEffectToObject(int nDurationType, effect eEffect, object oTarget, float fDuration),
        // pushed in reverse declaration order. EffectDamage is dispatched in the middle so
        // that it leaves its effect exactly where the effect argument belongs.
        if (StackPushFloat(vm, 0f) == 0 || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        // EffectDamage(int nDamageAmount, int nDamageType, int nDamagePower).
        // The handler clamps the amount to 0..10000.
        if (StackPushInteger(vm, 0) == 0
            || StackPushInteger(vm, 8) == 0
            || StackPushInteger(vm, 9999) == 0)
        {
            failure = 6;
            return false;
        }

        var effectDamage = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedEffectDamage;
        if (effectDamage(implementer, _routineEffectDamage, 3) != 0)
        {
            failure = 7;
            return false;
        }

        if (StackPushInteger(vm, 0) == 0)
        {
            failure = 8;
            return false;
        }

        var applyEffect = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedApplyEffectToObject;
        if (applyEffect(implementer, _routineApplyEffect, 4) != 0)
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

    /// <summary>
    /// Snaps a creature to a position through SetPosition (raises the dirty bit so the
    /// render side follows). Correction for a phantom that has fallen hopelessly behind
    /// its player — a peer teleport, a load, or a pathfinder dead end.
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void SnapTo(nint creature, Vec3 position)
    {
        ClearAllActionsFn(creature, 1);
        SetPositionFn(creature, &position, 1, 0, 0);
    }

    /// <summary>Faces a creature along a world-space angle (0 = +X, counter-clockwise).</summary>
    public static void SetFacing(nint creature, float angle)
    {
        Vec3 direction = new() { X = MathF.Cos(angle), Y = MathF.Sin(angle), Z = 0f };
        SetOrientationFn(creature, &direction);
    }

    /// <summary>Object id of the local player creature, or 0 before a world is loaded.</summary>
    public static uint GetPlayerObjectId()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint player = GetPlayerCreatureFn(server);
        return player == 0 ? 0 : *(uint*)(player + 0x04);
    }

    public static void SetAnimation(nint creature, int animation)
    {
        // Always dispatch through the vtable. The only direct call to the implementation in
        // the whole image is from the constructor.
        nint vtable = *(nint*)creature;
        var fn = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(vtable + VtableSetAnimation));
        fn(creature, animation);
    }

    /// <summary>
    /// Advances a creature toward a target using the engine's own walk step, so the move is
    /// ground-snapped, walkmesh-aware and animated rather than a teleport.
    ///
    /// The step function holds no goal of its own: it steers by the creature's orientation.
    /// That is why a destination that changes 20 times a second costs nothing here.
    /// </summary>
    public static bool StepToward(
        nint creature, Vec3 target, float maxDistance, out Vec3 newPosition, out StepDiagnostics diagnostics)
    {
        Vec3 current = GetPosition(creature);
        newPosition = current;
        diagnostics = default;

        float dx = target.X - current.X;
        float dy = target.Y - current.Y;
        float planar = MathF.Sqrt((dx * dx) + (dy * dy));

        if (planar < 0.05f)
        {
            // Arrived: stand still in the idle pose.
            *(int*)(creature + CreMoveState) = 1;
            SetAnimation(creature, AnimIdle);
            return false;
        }

        // Face the target. Orientation is a direction vector, not an angle.
        Vec3 direction = new() { X = dx / planar, Y = dy / planar, Z = 0f };
        SetOrientationFn(creature, &direction);

        // Walking state must be set before the animation, and both before the step, which
        // is the order the engine's own callers use.
        *(int*)(creature + CreRunFlag) = 0;
        *(int*)(creature + CreMoveState) = -1;
        SetAnimation(creature, AnimWalk);

        float distance = MathF.Min(maxDistance, planar);

        Vec3 stepped = current;
        Vec3 spare = default;
        int spareInt = 0;
        int walkResult = WalkStep(creature, distance, &stepped, &spare, &spareInt);

        // Commit through SetPosition rather than poking the field, so the position dirty
        // bit is raised and the engine's own client sync runs (which is what reselects the
        // walk animation on the render side).
        int setResult = SetPositionFn(creature, &stepped, 1, 0, 0);

        diagnostics = new StepDiagnostics(
            distance, walkResult, setResult, current, stepped, spare, spareInt, GetPosition(creature));

        newPosition = stepped;
        return true;
    }
}

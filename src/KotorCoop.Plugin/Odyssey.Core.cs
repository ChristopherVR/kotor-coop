// Odyssey.Core.cs - shared engine plumbing: globals, CSWSObject/CSWSCreature field
// offsets, raw engine-function bindings, VM/stack primitives (GetImplementer,
// FindRoutine, StackPush/Pop*), and any binding used by 2+ of the other Odyssey.*.cs
// concern files. Everything here is MAIN-THREAD ONLY (see the class doc comment).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- globals --------------------------------------------------------------

    private const nint VirtualMachinePtr = 0x007A3A00;

    // CVirtualMachineInternal
    private const int CommandImplementerOffset = 0x3D4;
    private const int StackPointerOffset = 0x1AC;
    private const int CallStackSizeOffset = 0x1C4;

    /// <summary>
    /// The VM argument STACK's own struct, embedded at <c>internalVm+StackPointerOffset</c>:
    /// <c>+0x00</c> count (== <see cref="StackPointerOffset"/> itself) · <c>+0x08</c> capacity ·
    /// <c>+0x0C</c> TAG array pointer (one byte per entry) · <c>+0x10</c> VALUE array pointer
    /// (one dword per entry — an int/float/object bit pattern, or an engine-structure pointer)
    /// · <c>+0x14</c> a "structure array allocated" flag that gates tags 0x10..0x19 only.
    ///
    /// VERIFIED by fresh disassembly of the shared grow/tag-push helper every
    /// <c>StackPush*</c> wrapper calls (<c>0x005D15A0</c>) and of
    /// <c>StackPushEngineStructure</c> (thunk <c>0x005D10B0</c> -&gt; body <c>0x005D19C0</c>):
    /// the tag byte for an <c>effect</c> stack entry is <c>0x10</c>
    /// (<see cref="EffectStackTag"/>), and the VALUE array slot for that entry holds a pointer
    /// to a VM-owned <c>CGameEffect</c> copy — the SAME pointer <c>ExecuteCommandApplyEffectToObject</c>
    /// (<c>0x0052E570</c>) reads back out via the symmetric <c>StackPopEngineStructure</c>
    /// (thunk <c>0x005D10A0</c>) and hands to <c>CSWSObject::ApplyEffect</c> (<c>0x004D14F0</c>)
    /// with no further cloning in between. See docs/research/peer-damage-ledger.md §8.
    /// </summary>
    private const int StackTagArrayOffset = 0x1B8;    // internalVm + this
    private const int StackValueArrayOffset = 0x1BC;  // internalVm + this

    /// <summary>The VM stack tag byte for an <c>effect</c> engine-structure entry — VERIFIED
    /// (see <see cref="StackTagArrayOffset"/>'s remarks): <c>StackPushEngineStructure</c> is
    /// called with its own internal "structure type" argument 0 for Effect, and adds the
    /// stack's <c>0x10</c> tag-space base to it before pushing (<c>0x005D19EB</c>: <c>add ebx,
    /// 0x10</c>) — 0x10 + 0 = 0x10.</summary>
    private const byte EffectStackTag = 0x10;

    // CSWVirtualMachineCommands
    private const int CommandsTableOffset = 0x0C;

    private const int RoutineCreateObject = 243;
    private const nint ExpectedCreateObject = 0x0052F820;

    /// <summary>ActionMoveToLocation. Verified live: table[21] == ExecuteCommandMoveToPoint.</summary>
    private const int RoutineActionMoveToLocation = 21;
    private const nint ExpectedMoveToPoint = 0x0053FE00;

    /// <summary>ActionAttack. The table entry is checked against the handler before use, so
    /// a wrong index refuses instead of dispatching something else
    /// (CSWVirtualMachineCommands::ExecuteCommandAttack).</summary>
    private const int RoutineActionAttack = 37;
    private const nint ExpectedAttack = 0x0052E7F0;

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

    /// <summary>
    /// ActionFollowLeader() — zero NWScript parameters. <c>CSWVirtualMachineCommands::
    /// ExecuteCommandActionFollowLeader</c>, confirmed by the symbol database. Dispatched
    /// through the same verified VM path <see cref="AttackTarget"/> and <see cref="PickUpItem"/>
    /// use, deliberately not the raw <c>CSWSObject::AddAction</c> marshaling call. See
    /// docs/research/party-reconciliation.md for the full action-id verification story.
    /// </summary>
    private const nint ExpectedActionFollowLeader = 0x0052CBE0;

    private static int _routineCreateItemOnFloor = -1;
    private static int _routineDestroyObject = -1;
    private static int _routineActionPickUpItem = -1;
    private static int _routineEffectDamage = -1;
    private static int _routineApplyEffect = -1;
    private static int _routineActionFollowLeader = -1;

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

    // CVirtualMachine::StackPopInteger(int* out). Needed by the roster routines, which unlike
    // CreateObject return an int rather than an object — and whose return has to be popped
    // even when it is a refusal, or the VM stack is left one word deep for the next script.
    private static readonly delegate* unmanaged[Thiscall]<nint, int*, int> StackPopInteger =
        (delegate* unmanaged[Thiscall]<nint, int*, int>)0x005D1000;

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
    /// <summary>The address of the CreateObject slot in the live routine table, 0 while the VM
    /// is not up yet. For <see cref="ScriptCreateObjectHook"/>.</summary>
    public static nint CreateObjectRoutineSlot()
    {
        nint implementer = GetImplementer(out _, out _);
        nint table = implementer == 0 ? 0 : Deref(implementer + CommandsTableOffset);
        return table == 0 ? 0 : table + (RoutineCreateObject * 4);
    }

    /// <summary>The top VM stack entry's value word, read in place (see
    /// <see cref="StackValueArrayOffset"/>); 0 when the stack is empty or unreadable.</summary>
    public static nint PeekStackTopValue()
    {
        nint vm = *(nint*)VirtualMachinePtr;
        if (vm == 0 || !NativeMethods.IsReadable(vm, 4)) return 0;
        nint internalVm = *(nint*)vm;
        if (internalVm == 0 || !NativeMethods.IsReadable(internalVm + StackValueArrayOffset, 4)) return 0;
        int stackPointer = *(int*)(internalVm + StackPointerOffset);
        nint values = *(nint*)(internalVm + StackValueArrayOffset);
        if (stackPointer <= 0 || values == 0 || !NativeMethods.IsReadable(values + (stackPointer - 1) * 4, 4)) return 0;
        return *(nint*)(values + (stackPointer - 1) * 4);
    }

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
    public static uint SpawnCreature(string template, Vec3 position, Vec3 facingUnitVector, out int failure) =>
        SpawnCreature(template, position, facingUnitVector, viaScriptTable: false, out failure);

    /// <param name="viaScriptTable">Dispatch through the live routine-table slot, as a script's
    /// CreateObject does, instead of calling the handler directly: a dev test of
    /// <see cref="ScriptCreateObjectHook"/> (the `spawncreature @template` form).</param>
    public static uint SpawnCreature(string template, Vec3 position, Vec3 facingUnitVector, bool viaScriptTable, out int failure)
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

        nint createSlot = *(nint*)(table + (RoutineCreateObject * 4));
        if (createSlot != ExpectedCreateObject && createSlot != ScriptCreateObjectHook.StubAddress)
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

        int status = viaScriptTable
            ? ((delegate* unmanaged[Thiscall]<nint, int, int, int>)createSlot)(implementer, RoutineCreateObject, 4)
            : ExecuteCommandCreateObject(implementer, RoutineCreateObject, 4);
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

        // The engine returns OBJECT_INVALID with a success status when the template does
        // not resolve — a blueprint a mod removed or renamed. Give that its own code so the
        // caller can latch "phantoms unavailable" instead of retrying a missing resref
        // forever (and hammering the log on the game thread).
        if (objectId == ObjectInvalid && failure == 0)
        {
            failure = 9;
        }

        return objectId;
    }

    // ---- combat bisection mask -------------------------------------------------------
    //
    // A dev-only coop.ini bitmask (`local_combat_bisect_mask`, hex, default 0 = every op
    // below active — today's shipped behaviour) where each bit DISABLES exactly one distinct
    // engine write/call this plugin performs on a MIRRORED (not-yet-fighting) creature,
    // letting a live A/B pass isolate which write actually matters instead of reasoning from
    // disassembly alone. Strictly additive: every call site below runs its normal op when its
    // bit is 0, so mask-unset reproduces today's behaviour bit for bit. Full investigation
    // history and results: docs/research/shared-state-audit.md.
    //
    // Bit  Op                                                        Where
    // 0    ObjCommandable = 1                                        ClaimCreature
    // 1    ClearAllActionsFn (targeted move-class wipe)               ClaimCreature
    // 2    CreMovePermitted |= 2 ; CreRunFlag = 0                     ClaimCreature
    // 3    CreMovePermitted |= CreAttackPermittedBit (0x04)           PermitAttacking
    // 4    PlayOneShotAnimation replay                                EnemyPresenceController.DriveOne
    // 5    SetOrientationFn (facing write, server AND twin-snap half)  DriveBody
    // 6    CreRunFlag / CreMoveState destructive assignment           DriveBody
    // 7    SetAnimation vtable call (server animation field)          DriveBody
    // 8    SetPositionFn (engine's own SetPosition, suppress-dirty)   DriveBody
    // 9    Client twin desired-position + animation-sync writes       DriveBody
    // 10   ClearPositionDirty (ClearDirtyFlagFn)                      EnemyPresenceController.DriveOne
    //
    // Other callers of these same functions (MaterialisedEnemies, PartyPresenceController,
    // PhantomController) never pass a mask, so they default to 0 — unaffected, always full
    // behaviour, exactly as before this round.
    public const uint BisectBitClaimCommandable = 1u << 0;
    public const uint BisectBitClaimActionWipe = 1u << 1;
    public const uint BisectBitClaimMovePermit = 1u << 2;
    public const uint BisectBitPermitAttack = 1u << 3;
    public const uint BisectBitOneShotAnim = 1u << 4;
    public const uint BisectBitDriveFacing = 1u << 5;
    public const uint BisectBitDriveRunMoveState = 1u << 6;
    public const uint BisectBitDriveAnimation = 1u << 7;
    public const uint BisectBitDrivePosition = 1u << 8;
    public const uint BisectBitDriveTwin = 1u << 9;
    public const uint BisectBitClearPositionDirty = 1u << 10;

    /// <summary>Not a bisection bit: skips DriveBody's twin gait writes (looping-animation hold,
    /// sync calls, playback rate) while a dialogue stunt animation plays on the body, so the
    /// engine's idle reassertion cannot cancel it. Position and facing still drive.</summary>
    public const uint DriveSkipTwinGait = 1u << 16;

    /// <summary>All eleven DriveOne bisection bits, for "disable everything but census
    /// membership" (run 0 of the bisection protocol).</summary>
    public const uint BisectAllOps =
        BisectBitClaimCommandable | BisectBitClaimActionWipe | BisectBitClaimMovePermit
        | BisectBitPermitAttack | BisectBitOneShotAnim | BisectBitDriveFacing
        | BisectBitDriveRunMoveState | BisectBitDriveAnimation | BisectBitDrivePosition
        | BisectBitDriveTwin | BisectBitClearPositionDirty;

    // ---- health pin/mirror bisection bits ----------------------------------------------
    //
    // Extends the mask above to CombatController.Apply's own per-frame writes on a mirrored
    // enemy: the min-1-hp health pin and the health-mirror write itself. Gated the same way
    // as the eleven DriveOne bits (read once via EnemyPresenceController.LocalCombatBisectMask,
    // 0/unset reproduces shipped behaviour bit for bit); a blunt global switch, not
    // per-creature. Full investigation history: docs/research/shared-state-audit.md.
    //
    // Bit  Op                                                        Where
    // 11   SetMinOneHitPoint(creature, true) (the health pin)        CombatController.Apply
    // 12   SetCurrentHitPoints (the health-mirror write itself)      CombatController.Apply
    public const uint BisectBitHealthPin = 1u << 11;
    public const uint BisectBitHealthMirror = 1u << 12;

    /// <summary>
    /// Takes a creature out of AI control so its own behaviour does not fight us.
    ///
    /// Order matters: ClearAllActions no-ops unless the commandable flag is 1, so the
    /// queue must be emptied BEFORE the creature is made non-commandable.
    /// </summary>
    public static void ClaimCreature(nint creature) => ClaimCreature(creature, clearActions: true);

    /// <summary>
    /// As <see cref="ClaimCreature(nint)"/>, but <paramref name="clearActions"/> false leaves
    /// the creature's action queue alone.
    ///
    /// ⚠ THE WIPE IS WHY A CLIENT IS NEVER IN COMBAT. EnemyPresenceController re-claims every
    /// mirrored enemy every pass, and ClearAllActions does not distinguish a queued
    /// MoveToPoint (which fights our placement, and is the reason the wipe exists) from a
    /// queued attack (which is the entire fight). So the client's enemies have their swings
    /// deleted several times a second, never land one, and the client's combat state — which
    /// the engine derives from actually being fought — stays false through the whole battle.
    ///
    /// Leaving the queue alone hands the enemy back to its own AI on this instance. Its
    /// position is still driven from the owner every pass, so it stays where the owner says;
    /// what it gains is the ability to finish a combat round.
    /// </summary>
    /// <param name="bisectMask">0 (default) is today's unconditional behaviour, unaffected.
    /// Any other caller than <c>EnemyPresenceController.DriveOne</c> should never pass
    /// anything else. See the bit table above.</param>
    public static void ClaimCreature(nint creature, bool clearActions, uint bisectMask = 0)
    {
        // Deliberately LEAVES the creature commandable. An earlier version set the
        // commandable flag to 0 to stop the AI interfering, which was self-defeating: the
        // action queue is executed by the AI master, so a non-commandable creature ignores
        // the move actions we queue and never walks anywhere.
        if ((bisectMask & BisectBitClaimCommandable) == 0)
        {
            *(int*)(creature + ObjCommandable) = 1;
        }

        if (clearActions && (bisectMask & BisectBitClaimActionWipe) == 0)
        {
            ClearAllActionsFn(creature, 1);
        }

        // Movement must be permitted or the walk animation is skipped entirely.
        if ((bisectMask & BisectBitClaimMovePermit) == 0)
        {
            *(int*)(creature + CreMovePermitted) |= 2;
            *(int*)(creature + CreRunFlag) = 0;
        }
    }

    // ---- bindings shared by 2+ concern files, per the split's rule 3 ----

    /// <summary>
    /// CSWSObject::ClearDirtyFlag(int nFlag) — the flags are a bit array based at this+0x1FC:
    /// <c>[this + 0x1FC + (nFlag&gt;&gt;5)*4] &amp;= ~(1 &lt;&lt; (nFlag &amp; 31))</c>.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> ClearDirtyFlagFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004CC250;

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

    /// <summary>
    /// CClientExoApp::GetSWParty 0x005ED8B0 — <c>mov eax,[ecx+4]; mov eax,[eax+0x270]; ret</c>.
    /// A bare <c>ret</c>: no arguments, and two loads with no call.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetSWPartyFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED8B0;

    /// <summary>CClientExoApp::GetPlayerCreature — the client-side controlled creature.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetClientPlayerCreatureFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED540;

    // CServerExoApp::GetObjectArray() -> CGameObjectArray*. UnloadModule destroys the array
    // and allocates a fresh one, which makes this pointer a natural module-lifetime token.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetObjectArrayFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AED70;

    // CServerExoApp::GetModule() -> CSWSModule*. A KOTOR 1 module holds exactly one area,
    // whose pointer sits at +0x220 (symbol database: area).
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AE6B0;

    // CExoString::CExoString(const char*) / ~CExoString — build+free the temporary label string.
    private static readonly delegate* unmanaged[Thiscall]<byte*, byte*, void> CExoStringCtorFn =
        (delegate* unmanaged[Thiscall]<byte*, byte*, void>)0x005E5A90;

    private static readonly delegate* unmanaged[Thiscall]<byte*, void> CExoStringDtorFn =
        (delegate* unmanaged[Thiscall]<byte*, void>)0x005E5C20;

    /// <summary>CSWCObject::AnimationFireAndForget(u16) — the engine's own classifier, so we
    /// never need a hardcoded list of which ids are one-shots.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationFireAndForgetFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C380;

    /// <summary>CSWCObject::InsertFireForgetAnimation(u16 anim, int durationMs, float speed,
    /// int, int) — 0x0063C760, the one choke point every fire-and-forget passes through and
    /// the function <see cref="AnimationHook"/> detours (docs/research/animation-sync.md §2).
    /// Called here only for an id that is ALREADY a model animation index (below
    /// <see cref="AnimIdle"/>): the engine's own attack paths resolve those before inserting,
    /// so <c>AddFireForgetAnimation</c>'s 10000-based classifier has no row for them. The
    /// argument shape is the engine's own default-arm call (<c>0x5DC</c> ms, speed 1.0, 0, 0).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, float, int, int, void> InsertFireForgetAnimationFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, float, int, int, void>)0x0063C760;

    /// <summary>CSWCObject::AnimationOverlay(u16) — the same classifier for the third class,
    /// which has no object field at all and can only be applied through the animbase.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationOverlayFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C410;

    /// <summary>CSWCObject::AnimationLooping(u16) — the third of the engine's predicates.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationLoopingFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C300;

    /// <summary>DIAGNOSTIC: CSWCObject::AnimationWalking(u16) / AnimationRunning(u16) — the
    /// predicates <c>ClientServerSyncHandler_Position</c> (0x00611F50, vtable +0x12C)
    /// branches on to decide whether to resolve and play a walk/run animation by name. Probed
    /// to confirm the +0x12C call reaches the animbase-vtable+0x54 "play" step for a creature.
    /// See docs/research/phantom-locomotion.md.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationWalkingFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C240;

    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationRunningFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C2A0;

    /// <summary>
    /// <c>CSWCAnimBase::AnimationExists(u16)</c>, animbase vtable +0x50 (0x0069D910,
    /// confirmed by name in tools/kotor1_0_3.db) — NOT AnimationLooping/FireAndForget/Overlay
    /// — is the real gate on whether a gait id actually plays. <c>SetAnimation</c> (vtable
    /// +0x9C, 0x0063E930, the "plain setter" that "does not stick") calls this and returns
    /// immediately without touching twin+0x48 when it reports false; AnimationExists itself
    /// returns false when the animbase's <c>Gob*</c> (<see cref="AnimBaseGob"/>, the loaded
    /// render/scene node) is null, or when no node matching the resolved animation name
    /// exists on the loaded model. Full trace: docs/research/phantom-locomotion.md.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationExistsFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0069D910;

    /// <summary>CClientExoApp::GetSWCMessage — the client's message pipe to the server.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetSWCMessageFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED6F0;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> ClientGetWorldTimerFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED4F0;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> PauseWorldTimerFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x004ADFF0;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> UnpauseWorldTimerFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x004AE030;

    /// <summary>
    /// CGuiInGame::SetPauseVisuals(bPaused, reasonByte) — the one function that moves the
    /// GUI belief and the pause panel, normally reached only from message handlers.
    /// Internally guarded on the HUD existing, and its show path routes correctly around
    /// the map screen and conversations, so it is safe to call in any GUI state.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetPauseVisualsFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x0062DEF0;

    // CSWSCreature::PacifyCreature: walks the area object list and releases everything
    // whose combat round is targeting this creature. __thiscall, no arguments.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PacifyCreatureFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x004FD960;

    /// <summary>
    /// CSWSCombatRound + this = the round; a small, two-arg setter at <c>0x004D28F0</c>.
    /// VERIFIED by disassembly of the function itself AND cross-checked against three real,
    /// byte-identical call sites in the engine's own combat-decision code
    /// (<c>0x005BC61E</c>/<c>0x005BC67D</c>/<c>0x005BC6AD</c>, all <c>push &lt;objectId&gt;;
    /// push 1; call 0x4D28F0</c>): unconditionally writes <c>round+0x95c = arg2</c> then
    /// <c>round+0x958 = arg1</c>, in that order, with no branch that skips either store when
    /// arg1 is nonzero.
    ///
    /// `round+0x9B0` being non-null (VERIFIED live) only means an action is ON the list —
    /// `UpdateCombatRoundTimer` (<c>0x004FAF20</c>, called unconditionally every tick, no
    /// combat-state or mirrored gate) reads `round+0x958` and, if it is still the zero
    /// `StartCombatRound` (`0x004D5F70`) leaves it at, takes a bare `IncrementTimer`-only
    /// fallback (`0x004FB0D3`) that never touches the queued action — the actual
    /// silent-discard mechanism, not anything inside `AddAttackAction`. This call is what
    /// flips `+0x958` nonzero so the real processing branch runs instead. Full investigation:
    /// docs/research/shared-state-audit.md.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetRoundTimerActiveFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x004D28F0;

    /// <summary>
    /// CSWSCombatRound + this = the round; a small, three-branch, two-arg setter at
    /// <c>0x004D2920</c> — a SEPARATE function from <see cref="SetRoundTimerActiveFn"/>
    /// (which ends with `ret 8` at <c>0x004D2918</c>; this one starts five NOP-padding bytes
    /// later at <c>0x004D2920</c> and is its own complete function with three `ret 8` exits).
    /// VERIFIED by full disassembly of all three branches AND cross-checked against the exact
    /// same engine combat-decision routine that calls <c>StartCombatRound</c> and
    /// <see cref="SetRoundTimerActiveFn"/> (<c>0x005BC630</c>/<c>0x005BC6BF</c>, both
    /// <c>push 0; push &lt;delayMs&gt;; call 0x4D2920</c>, immediately after the matching
    /// <c>SetRoundTimerActiveFn</c> call in the same sequence):
    ///
    /// <c>round+0x964 = arg2</c> always, first. Then: if <c>arg2 != 0</c>, <c>round+0x960 =
    /// arg1</c> directly. If <c>arg2 == 0</c>: <c>arg1 == 0</c> sets <c>round+0x960 = 0</c>;
    /// otherwise, if the CURRENT <c>round+0x960</c> is already positive it ACCUMULATES
    /// (<c>round+0x960 += arg1</c>), else it sets <c>round+0x960 = arg1</c> directly.
    ///
    /// `UpdateCombatRoundTimer` (<c>0x004FAF20</c>) reaches the real action-processing call
    /// (<c>0x005B8050</c>, via <c>0x004FAFA7</c>-<c>0x004FAFD9</c>) only when a SECOND check
    /// — <c>ebx = round+0x960 - creature+0xCC</c> — is POSITIVE; this call is what
    /// establishes `round+0x960`, since `StartCombatRound` zeroes it. Without this call the
    /// function instead always takes a shortcut (<c>0x004FAFE3</c> onward) that resolves a
    /// leftover `round+0x9CC` target and calls the round-reset helper
    /// (<c>0x004D4620</c>), which zeroes `round+0x958` back to 0 along with `+0x934`,
    /// `+0x944`, `+0x94c`, `+0x960` and others, without ever reaching the action-processing
    /// call that would change the head action. Full investigation:
    /// docs/research/shared-state-audit.md.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetRoundDelayFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x004D2920;

    /// <summary>Bare call to 0x004B4CB0, the real body behind the
    /// 0x4AEA40/0x4B4CB0/0x4AE920/0x4B2AE0 thunk chain 0x5B8050's real processing branch uses
    /// to cross-check its target against "is party slot 0 resolvable." Cdecl, zero real
    /// arguments (confirmed by full disassembly — it never reads a stack argument or uses the
    /// incoming `this`), returns the resolved creature id or 0x7F000000 (not found).</summary>
    private static readonly delegate* unmanaged[Cdecl]<uint> ResolvePartySlot0Fn =
        (delegate* unmanaged[Cdecl]<uint>)0x004B4CB0;

}

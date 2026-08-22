using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

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
    /// ExecuteCommandActionFollowLeader</c>, confirmed by the symbol database and matching
    /// docs/research/party-reconciliation.md's own citation ("Only ONE site enqueues it").
    /// ROUND 71: the safe VM-dispatch route into the SAME action <see cref="IsMoveAction"/>'s
    /// own table comment lists as <c>0x3C PartyFollowLeader</c> (that table read directly off
    /// the RunActions jump table entry by entry, which is why it is trusted over
    /// party-reconciliation.md's earlier, unverified "action id 61" (0x3D) guess — see
    /// <see cref="QueuePartyFollowLeader"/>'s own remarks). Deliberately NOT the raw
    /// <c>CSWSObject::AddAction 0x004CEA20</c> marshaling call party-reconciliation.md's
    /// "Recommended design" speculated about: docs/research/dialogue-join-latency.md already
    /// flagged that call's full push-list typing as unverified and "not safe to ship blind."
    /// This goes through the identical, already-verified VM path <see cref="AttackTarget"/> and
    /// <see cref="PickUpItem"/> use instead.
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

    // ---- ROUND 47 BISECTION MASK ------------------------------------------------------
    //
    // docs/research/shared-state-audit.md §2 rounds 9-46 spent 46 rounds proving WHICH
    // fields a mirrored-then-excluded creature's round sub-structures stay disarmed despite,
    // one static-disassembly candidate at a time, and never found the actual cause. Round 47
    // is the empirical alternative: a dev-only coop.ini bitmask (`local_combat_bisect_mask`,
    // hex, default 0 = every op below active — today's shipped behaviour) where each bit
    // DISABLES exactly one distinct engine write/call this plugin performs on a MIRRORED
    // (not-yet-fighting) creature, so a live A/B pass can narrow which one actually matters
    // instead of reasoning about it from the disassembly alone. Strictly additive: every call
    // site below runs its normal op when the corresponding bit is 0, so `local_combat_bisect_mask`
    // unset reproduces exactly today's behaviour bit for bit. The exclusion/trigger machinery
    // itself (StartLocalFight's script fire, MaintainLocalFight's dead/disengage/timeout
    // checks, RestoreClaimState on the handoff frame) is deliberately NOT gated by any bit —
    // this mask only ever touches the ONGOING per-frame mirror-drive ops that run on a
    // creature BEFORE it is ever handed to local_combat, which is precisely the "went through
    // DriveOne even briefly" half of round 46's fact pattern.
    //
    // Bit  Op                                                        Where
    // 0    ObjCommandable = 1                                        ClaimCreature
    // 1    ClearAllActionsFn (targeted move-class wipe)               ClaimCreature
    // 2    CreMovePermitted |= 2 ; CreRunFlag = 0                     ClaimCreature
    // 3    CreMovePermitted |= CreAttackPermittedBit (0x04)           PermitAttacking
    // 4    PlayOneShotAnimation replay                                EnemyPresenceController.DriveOne
    // 5    SetOrientationFn (facing write)                            DriveBody
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

    /// <summary>All eleven round-47 bisection bits, for "disable everything but census
    /// membership" (run 0 of the bisection protocol).</summary>
    public const uint BisectAllOps =
        BisectBitClaimCommandable | BisectBitClaimActionWipe | BisectBitClaimMovePermit
        | BisectBitPermitAttack | BisectBitOneShotAnim | BisectBitDriveFacing
        | BisectBitDriveRunMoveState | BisectBitDriveAnimation | BisectBitDrivePosition
        | BisectBitDriveTwin | BisectBitClearPositionDirty;

    // ---- ROUND 48 EXTENSION ------------------------------------------------------------
    //
    // Round 47's own write-up flagged two ops the task brief expected inside the eleven
    // above and did not find there: the health pin and "any faction hold." Reading
    // CombatController.cs in full this round found there is in fact NO faction write
    // anywhere on a mirrored, GIT-identical enemy — round 47's phrasing was imprecise, not
    // a gap in that round's own table. (PhantomController.ApplyTargetable does hold a
    // faction, but only on THIS instance's own phantom bodies representing remote peers,
    // never on a mirrored enemy — irrelevant to a GIT creature like the one this bisection
    // targets.) What CombatController.Apply genuinely does, every 40 frames, completely
    // unmasked by round 47 or any round before it: <see cref="SetMinOneHitPoint"/> (the
    // health pin) and the health-mirror write itself
    // (<see cref="SetCurrentHitPoints"/>) — both of which DID run, unmasked, in every one
    // of round 47's trials, including mask=0x7FF ("every DriveOne op off"). That is the
    // one real difference between round 37's success (never enumerated in the census AT
    // ALL, so CombatController never touched it even once — <c>BlockEnemyPresenceCount</c>
    // and <c>BlockEnemyCount</c> are written from the identical truncated list, confirmed
    // in <c>KotorGameBridge.TryWriteEnemies</c>) and round 47's failure (enumerated, so
    // both ClaimCreature/DriveOne AND CombatController.Apply were touching it — the
    // bisection only ever silenced the first). These two bits close that gap.
    //
    // Bit  Op                                                        Where
    // 11   SetMinOneHitPoint(creature, true) (the health pin)        CombatController.Apply
    // 12   SetCurrentHitPoints (the health-mirror write itself)      CombatController.Apply
    //
    // Gated the same way as the eleven above: read once via
    // EnemyPresenceController.LocalCombatBisectMask (the only writer of the ini key), never
    // gates StartLocalFight/MaintainLocalFight, and 0 (unset) reproduces today's shipped
    // behaviour bit for bit. CombatController mirrors EVERY census enemy the same way
    // regardless of whether local_combat is about to engage any particular one of them, so
    // — like the eleven DriveOne bits — these two are a blunt, global "is the pin/mirror
    // active at all this session" switch, not a per-creature one; sound for a single-target
    // bisection rig, which is what this ini key has only ever been used for.
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
    /// <param name="bisectMask">ROUND 47: 0 (default) is today's unconditional behaviour,
    /// unaffected. Any other caller than <c>EnemyPresenceController.DriveOne</c> should never
    /// pass anything else. See the bit table above.</param>
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

    // ---- the action queue, read as a queue rather than wiped blind ------------------
    //
    // Established from the binary, because BOTH extremes are wrong and both have shipped:
    //
    //   CSWSObject + 0x0FC  -> CExoLinkedList<CSWSObjectActionNode>*
    //   [list]              -> the head position (+0x04 next, +0x08 data). ClearAllActions
    //                          0x004CCD80 walks exactly this at 0x004CCDBE, and
    //                          CExoLinkedList::GetAt 0x005E9B60 is `mov eax,[eax+8]; ret 4`.
    //   [pos + 0x08]        -> CSWSObjectActionNode* (0x74 bytes: CSWSObject::AddAction
    //                          0x004CEA20 opens with `push 0x74`).
    //   [node + 0x00]       -> the ACTION TYPE. RunActions 0x0057F4A0 dispatches on it:
    //                          `mov eax,[edi]` / `cmp eax,0x40` / `ja default` /
    //                          `jmp dword ptr [eax*4 + 0x5801E4]`.
    //
    // The HEAD is the action the engine is currently running: ClearAllActions compares each
    // element against [[obj+0xFC]]+8 to build the `bIsCurrent` argument for vtable +0x74.

    /// <summary>CSWSObject + this = the action-queue list pointer.</summary>
    private const int ObjActionList = 0x0FC;

    /// <summary>CExoLinkedList position + this = the element pointer.</summary>
    private const int ActionListNodeData = 0x08;

    /// <summary>
    /// The action type at the head of a creature's queue, or -1 for "empty, or nothing I
    /// recognise". Three reads and no engine call, so it is safe from the frame hook and
    /// cheap enough to run per mirrored enemy per pass.
    ///
    /// The range test is not decoration: RunActions itself refuses anything above 0x40, so a
    /// value outside it is a stale pointer read as a node, not an action.
    /// </summary>
    public static int GetHeadActionType(nint creature)
    {
        if (creature == 0)
        {
            return -1;
        }

        nint list = *(nint*)(creature + ObjActionList);
        if (list == 0)
        {
            return -1;
        }

        nint head = *(nint*)list;
        if (head == 0)
        {
            return -1;
        }

        nint node = *(nint*)(head + ActionListNodeData);
        if (node == 0)
        {
            return -1;
        }

        int type = *(int*)node;
        return type is >= 0 and <= 0x40 ? type : -1;
    }

    /// <summary>
    /// Every id in the RunActions jump table (0x5801E4) whose handler MOVES the body, read off
    /// the table entry by entry rather than guessed:
    ///
    ///   0x00 MoveToPoint  0x01 CheckMoveToObject  0x02 CheckMoveAwayFromObject
    ///   0x03 CheckInterAreaPathfinding  0x04 JumpToPoint  0x09 CheckMoveToPoint
    ///   0x10 CheckMoveToObjectRadius  0x11 CheckMoveToPointRadius
    ///   0x2B CheckMoveAwayFromLocation  0x2C RandomWalk  0x2F JumpToObject  0x32 Drive
    ///   0x36 ForceFollowObject  0x39 CheckForceFollowObject  0x3C PartyFollowLeader
    ///   0x3F CheckMoveToFollowRadius
    ///
    /// Everything else is left alone. The fight lives at 0x0B PhysicalAttacks (0x005BBBF0),
    /// 0x3E Combat (0x005B6210), 0x0E CastSpell, 0x31 CounterSpell, 0x0F WaitForEndOfRound.
    /// </summary>
    private static bool IsMoveAction(int type) =>
        type is 0x00 or 0x01 or 0x02 or 0x03 or 0x04 or 0x09 or 0x10 or 0x11
             or 0x2B or 0x2C or 0x2F or 0x32 or 0x36 or 0x39 or 0x3C or 0x3F;

    /// <summary>The head-of-queue types that mean "already fighting" — <see cref="QueueAttackAction"/>
    /// must not stack a second attack behind one of these, per the RunActions dispatch table
    /// comment above: 0x0B PhysicalAttacks, 0x3E Combat, 0x0E CastSpell, 0x31 CounterSpell,
    /// 0x0F WaitForEndOfRound.
    ///
    /// ⚠ ROUND 51/52: NOT ground truth for "is a real fight in progress." Rounds 9-50 used
    /// this as the sole signal for that question via the `firescript watch` diagnostic log
    /// (Odyssey.TickFireScriptWatch); round 51 measured it read False on 0 of 6000 sampled
    /// frames across three trials where the player's own HP independently, verifiably fell
    /// (docs/research/shared-state-audit.md §2, round 51). This tests the action-QUEUE head
    /// (RunActions' dispatch), which is a different state machine from the actual per-round
    /// combat processing (StartCombatRound 0x005BB590 and its maneuver-selection dispatch),
    /// so a creature can be genuinely landing hits with a head action outside this list the
    /// whole time. Still correct for its ORIGINAL, narrower purpose — "don't stack a second
    /// queued attack behind one already at the head" — which is why it stays here rather
    /// than being deleted outright. Round 52 replaced it as a ground-truth signal with
    /// PhantomHitHook's own damage-choke-point log ("combat ground-truth:").</summary>
    private static bool IsFightAction(int type) =>
        type is 0x0B or 0x3E or 0x0E or 0x31 or 0x0F;

    /// <summary>
    /// Stops a creature WALKING, and nothing else — the hold a peer's interaction lock buys.
    /// Returns true when something was actually cleared.
    ///
    /// ⚠ Only when the head action is a MOVE. A blanket ClearAllActions every pass is what
    /// made a mirrored enemy unable to ever land a swing (see
    /// <see cref="ClaimCreatureForFight"/>) — the queue does not distinguish a walk from the
    /// whole fight, so the test has to. Here the object being held is usually an NPC someone
    /// is talking to, and the only thing that must stop is the owner's AI strolling it out of
    /// the conversation.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool HoldCreatureStill(nint creature)
    {
        if (creature == 0 || !IsMoveAction(GetHeadActionType(creature)))
        {
            return false;
        }

        ClearAllActionsFn(creature, 1);
        return true;
    }

    /// <summary>
    /// The claim a mirrored enemy gets under local_combat: keep the fight, drop the walk.
    ///
    /// ⚠ NEITHER EXTREME IS CORRECT. The unconditional wipe deletes the swings, which is why a
    /// mirroring instance is never in combat. But leaving the queue alone is WORSE than the
    /// bug it replaced, because a queued move can then never COMPLETE: this instance
    /// overwrites the body's position from the owner's stream every pass, and MotionSmoother
    /// never reads the engine's position back, so AIActionMoveToPoint re-measures the same
    /// distance forever, stays at the queue head, and the attack actions behind it never run.
    /// The transient condition the wipe existed for becomes permanent.
    ///
    /// So the wipe is conditioned on what is actually AT the head — the node the engine is
    /// running. A move-class head is the wedge and goes; anything else is the fight and stays.
    ///
    /// ⚠ LOAD-BEARING: when it fires it clears the WHOLE queue, because that is the engine's
    /// own ClearAllActions — the one path that runs each node's virtual ClearAction (vtable
    /// +0x74) before unlinking. Removing a single node by hand needs CSWSCreature::ClearAction
    /// (0x004FAB00) plus CExoLinkedList::RemoveAt (0x005E9C10) driven from managed code, which
    /// is not worth an unverified first outing. The cost is that attacks queued BEHIND a move
    /// go with it; the AI re-queues at the next combat-round boundary, tens of milliseconds.
    /// The wedge is forever.
    /// </summary>
    public static void ClaimCreatureForFight(nint creature, uint bisectMask = 0) =>
        ClaimCreature(creature, clearActions: IsMoveAction(GetHeadActionType(creature)), bisectMask);

    /// <summary>CSWSCreature + this = a per-creature "path info" sub-object — VERIFIED
    /// elsewhere in this file (the range-diagnostic reading creature+0x90 vs. this+0x08 as
    /// "personal-space radius", <c>attackerPathInfo</c>/<c>playerPathInfo</c>) and, this
    /// round, disassembled as the SAME pointer <c>AIActionPhysicalAttacks</c> (0x005BBBF0)
    /// reads at its own entry (<c>mov eax,[esi+0x340]</c>) before touching anything about the
    /// attack itself.</summary>
    private const int CrePathInfo = 0x340;

    /// <summary>(creature+0x340) + this = the path-info sub-object's own cached "last enemy"
    /// id. ROUND 46, disassembled: <c>CSWSObject::ClearAllActions</c> (0x004CCD80) writes
    /// <see cref="ObjectInvalid"/> here (0x004CCE92) every time its FULL body runs (whenever
    /// the head action is a move-class action — the common case for a body this controller
    /// force-overwrites every frame), immediately followed by zeroing a 12-byte Vec3 at
    /// <c>+0x258</c> right after it (0x004CCE98-CCEC3, presumably the matching last-known
    /// position). Live-confirmed on a creature that had been normally mirrored for ~90s then
    /// excluded into local_combat: this field read <see cref="ObjectInvalid"/>, its action
    /// queue was genuinely EMPTY (no head node at all), and <c>creature+0x508</c> below also
    /// read <see cref="ObjectInvalid"/> — the <c>+0x260</c> script fired but the creature
    /// never queued an attack, matching "no memory of ever having a target" more precisely
    /// than any flag <see cref="ClaimSnapshot"/> already restored (which changed nothing when
    /// tested live: see docs/research/shared-state-audit.md §2, round 46).</summary>
    private const int PathInfoLastEnemyId = 0x254;

    /// <summary>CSWSCreature + this — an undocumented field until this round: ROUND 46's full
    /// disassembly of <c>ClearAllActions</c> found it written to <see cref="ObjectInvalid"/>
    /// (0x004CCE7F) in the same unconditional tail as <see cref="PathInfoLastEnemyId"/> above,
    /// on the creature itself rather than the path-info sub-object. Named for what the write
    /// site implies rather than a confirmed engine symbol — treat the name as a working
    /// hypothesis, not a verified fact the way the offset itself is.</summary>
    private const int CreLastAttackTarget = 0x508;

    /// <summary>
    /// ROUND 46: the state a creature's own AI needs back in the engine's own hands when it
    /// is handed a real fight — <see cref="ObjCommandable"/>, the full
    /// <see cref="CreMovePermitted"/> dword (not just the two bits <see cref="ClaimCreature"/>
    /// ORs on), <see cref="CreRunFlag"/>, <see cref="CreMoveState"/>, and the two target-cache
    /// fields <see cref="ClearAllActionsFn"/> silently invalidates every time it runs its full
    /// body on a driven creature.
    ///
    /// Captured once per creature, the first time it is resolved into a slot — BEFORE this
    /// plugin's own claim has ever touched it — so a creature that spent real minutes being
    /// mirrored can be handed back to its own AI in the state the ENGINE actually had it in,
    /// not "whatever the last claim/drive pass happened to leave." <see cref="CreMoveState"/>
    /// and the target-cache pair matter most: <c>DriveEnemy</c> assigns <c>CreMoveState</c>
    /// (not ORs) every driven frame from the smoother's own "moving" bit, and
    /// <c>ClearAllActionsFn</c> unconditionally invalidates both target fields every time its
    /// full body runs — neither is something a creature normally recovers from on its own; both
    /// are normally only ever written by the engine's own live, actively-updated combat/pathing
    /// code, not something meant to sit frozen at a stale value. See
    /// docs/research/shared-state-audit.md §2, round 46.
    /// </summary>
    public readonly struct ClaimSnapshot(
        int commandable, int movePermitted, int runFlag, int moveState,
        uint lastAttackTarget, uint pathInfoLastEnemyId)
    {
        public readonly int Commandable = commandable;
        public readonly int MovePermitted = movePermitted;
        public readonly int RunFlag = runFlag;
        public readonly int MoveState = moveState;
        public readonly uint LastAttackTarget = lastAttackTarget;
        public readonly uint PathInfoLastEnemyId = pathInfoLastEnemyId;
    }

    public static ClaimSnapshot CaptureClaimState(nint creature)
    {
        nint pathInfo = *(nint*)(creature + CrePathInfo);
        return new(
            *(int*)(creature + ObjCommandable),
            *(int*)(creature + CreMovePermitted),
            *(int*)(creature + CreRunFlag),
            *(int*)(creature + CreMoveState),
            *(uint*)(creature + CreLastAttackTarget),
            pathInfo != 0 ? *(uint*)(pathInfo + PathInfoLastEnemyId) : ObjectInvalid);
    }

    /// <summary>Writes a snapshot back. Deliberately does NOT re-run <see cref="PermitAttacking"/>
    /// — the caller does that afterward, on purpose, so the attack-permitted bit this plugin has
    /// always asserted for a fighting creature cannot be silently lost if the ORIGINAL, pre-claim
    /// snapshot happened not to have it set.</summary>
    public static void RestoreClaimState(nint creature, ClaimSnapshot snapshot)
    {
        *(int*)(creature + ObjCommandable) = snapshot.Commandable;
        *(int*)(creature + CreMovePermitted) = snapshot.MovePermitted;
        *(int*)(creature + CreRunFlag) = snapshot.RunFlag;
        *(int*)(creature + CreMoveState) = snapshot.MoveState;
        *(uint*)(creature + CreLastAttackTarget) = snapshot.LastAttackTarget;

        nint pathInfo = *(nint*)(creature + CrePathInfo);
        if (pathInfo != 0)
        {
            *(uint*)(pathInfo + PathInfoLastEnemyId) = snapshot.PathInfoLastEnemyId;
        }
    }

    /// <summary>
    /// CSWSObject::ClearDirtyFlag(int nFlag) — the flags are a bit array based at this+0x1FC:
    /// <c>[this + 0x1FC + (nFlag&gt;&gt;5)*4] &amp;= ~(1 &lt;&lt; (nFlag &amp; 31))</c>.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> ClearDirtyFlagFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x004CC250;

    /// <summary>
    /// Flag 1 = position. VERIFIED: SetPosition (0x004CD1A0) raises it with
    /// <c>or dword ptr [esi+0x1FC], 2</c> at 0x004CD1C8 and skips that instruction exactly
    /// when its fourth argument (bSuppressDirty) is non-zero — the argument DriveBody passes 1
    /// for.
    /// </summary>
    private const int DirtyFlagPosition = 1;

    /// <summary>
    /// Undoes a position dirty bit somebody else raised this frame.
    ///
    /// ⚠ The other half of DriveBody's suppress-dirty argument, and it exists only because of
    /// local_combat. DriveBody suppresses the bit on OUR write for a measured reason: raised,
    /// the AI master's sync pass runs the twin's position handler, which re-derives the
    /// playback rate through ComputeSpeedFactor and gets 0.0 for a creature with no waypoint
    /// list — legs frozen mid-pose, body sliding, the "flying" look. With the queue intact the
    /// local AI calls SetPosition itself and does NOT suppress, so the artifact returns
    /// through the back door.
    ///
    /// ⚠ NEEDS-LIVE: a belt, not a proof. Whether the SwapBuffers hook beats the AI master's
    /// sync pass within a frame is unmeasured. The targeted wipe is the real mitigation. If
    /// mirrored enemies show frozen legs under local_combat, the ordering is wrong.
    /// </summary>
    public static void ClearPositionDirty(nint creature)
    {
        if (creature != 0)
        {
            ClearDirtyFlagFn(creature, DirtyFlagPosition);
        }
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
    ///
    /// ⚠ THE GENERIC GETTER, DELIBERATELY. <c>CServerExoApp::GetGameObject</c> (0x004AE750) is
    /// a thunk onto 0x004B1700, which walks <c>CGameObjectArray</c> and returns whatever it
    /// finds. Its neighbours in the vtable-adjacent block —
    /// <c>GetItemByGameObjectID</c> (0x004AE760 -&gt; 0x004B1740) and
    /// <c>GetCreatureByGameObjectID</c> (0x004AE770 -&gt; 0x004B1790) — do the SAME walk and then
    /// additionally test the object type byte at <c>+0x08</c>, which is exactly the field the
    /// object-type trap says never to test (it carries flag bits; a live creature reads
    /// 0x40005, not 5). Nothing in this plugin may resolve through those, and nothing does:
    /// every call site funnels through this method. If a future one reaches for a typed
    /// getter "because it wants a creature", it will reject live creatures intermittently and
    /// silently — check the vtable after resolving instead, the way EnemyPresenceController
    /// does.
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

        // ⚠ THE ENGINE FOUND IT AND WE THREW IT AWAY. This branch used to be the last silent
        // failure on this path: the array holds the id, the lookup succeeds, and the caller is
        // told "nothing answers to that" because neither layout probe matched. It has to say
        // so, because from the caller's side it is indistinguishable from a missing object —
        // and that ambiguity is what three live rounds were spent on. Bounded, since this sits
        // inside a per-frame path.
        if (_layoutRefusals < 10)
        {
            _layoutRefusals++;
            PluginHost.Log(
                $"resolve: engine returned 0x{gameObject:X} for 0x{objectId:X} but neither layout "
                + $"matched (+0x04 = 0x{*(uint*)(gameObject + 0x04):X}, "
                + $"+0x14 = 0x{*(uint*)(gameObject + 0x14):X}, vtable 0x{*(nint*)gameObject:X}) "
                + "- refusing it");
        }

        return 0;
    }

    /// <summary>How many "the engine found it, we refused it" lines have been said. A handful
    /// is evidence; a flood inside a per-frame path is a second bug.</summary>
    private static int _layoutRefusals;

    /// <summary>Reads the area id of a resolved server object (module-lifetime stable).</summary>
    public static uint GetAreaId(nint serverObject) => *(uint*)(serverObject + 0x8C);

    /// <summary>
    /// CSWSObject::GetDead through the engine's own vtable — AsSWSObject (vtbl+0x10) then
    /// GetDead (vtbl+0x94) on the result, exactly the two-call chain the round 32 diagnostic
    /// already proved out live. True on a resolution failure too: a creature this cannot even
    /// find is not one anything should keep treating as a live fight.
    /// </summary>
    public static bool IsCreatureDead(nint creature)
    {
        if (creature == 0)
        {
            return true;
        }

        nint vtbl0 = *(nint*)creature;
        var asSwsObjectFn = (delegate* unmanaged[Thiscall]<nint, nint>)(*(nint*)(vtbl0 + 0x10));
        nint asSwsObject = asSwsObjectFn(creature);
        if (asSwsObject == 0)
        {
            return true;
        }

        nint vtbl1 = *(nint*)asSwsObject;
        var getDeadFn = (delegate* unmanaged[Thiscall]<nint, int>)(*(nint*)(vtbl1 + 0x94));
        return getDeadFn(asSwsObject) != 0;
    }

    // ---- the object-array disagreement, asked in one frame ----------------------

    /// <summary>CServerExoAppInternal + this = CGameObjectArray*. The same field
    /// <c>CServerExoApp::GetObjectArray</c> (0x004AED70) reads, and the same one the external
    /// agent walks — declared here so the two can be COMPARED rather than assumed equal.</summary>
    private const int InternalObjectArrayOffset = 0x1005C;

    /// <summary>
    /// A transcription of <c>CGameObjectArray::GetGameObject</c> (0x004D8230), run in-process.
    ///
    /// Not a replacement for the engine's own call — it exists so the plugin can ask the
    /// SAME QUESTION THE AGENT ASKS, in the same frame as the engine's answer, and print both.
    /// Two lookups that must be identical disagreed live on 2026-08-15; every previous attempt
    /// to settle it compared measurements taken seconds and processes apart.
    /// </summary>
    private static nint WalkObjectArray(nint objectArray, uint objectId)
    {
        if (objectArray == 0)
        {
            return 0;
        }

        nint tree = *(nint*)objectArray;
        if (tree == 0)
        {
            return 0;
        }

        uint bucket = (((objectId >> 31) & 1) << 12) | (objectId & 0xFFF);
        uint key = objectId & 0x7FFF_FFFF;

        nint entry = *(nint*)(tree + (nint)(bucket * 4));

        for (int walked = 0; entry != 0 && walked < 64; walked++)
        {
            if (*(uint*)entry == key)
            {
                return *(nint*)(entry + 4);
            }

            entry = *(nint*)(entry + 8);
        }

        return 0;
    }

    /// <summary>
    /// Everything this engine knows about one object id, gathered on the game thread in a
    /// single frame: the pointer chain, BOTH ways of reaching the object array, the engine's
    /// own lookup, an in-process transcription of that lookup, and what
    /// <see cref="ResolveServerObject"/> finally decides.
    ///
    /// ⚠ THIS IS A MEASUREMENT, NOT A FEATURE. It answers the one question that no amount of
    /// reading could: when the agent says an id is held at an address and the plugin says
    /// nothing answers to it, WHICH STEP differs. Every line is a fact read at the same
    /// instant, so the answer cannot be "you looked at different moments".
    /// </summary>
    public static string DescribeObjectResolution(uint objectId)
    {
        nint appManager = Deref(AppManagerPtr);
        if (appManager == 0)
        {
            return $"0x{objectId:X}: no app manager at 0x{AppManagerPtr:X}";
        }

        nint server = Deref(appManager + AppServerOffset);
        if (server == 0)
        {
            return $"0x{objectId:X}: app manager 0x{appManager:X} has no server (+0x08)";
        }

        nint serverInternal = Deref(server + AppServerOffsetInternal);
        nint arrayByOffset = serverInternal == 0 ? 0 : Deref(serverInternal + InternalObjectArrayOffset);
        nint arrayByEngine = GetObjectArrayFn(server);

        nint viaEngine = GetGameObjectFn(server, objectId);
        nint viaWalk = WalkObjectArray(arrayByEngine, objectId);
        nint resolved = ResolveServerObject(objectId);

        string at = string.Empty;
        nint probe = viaEngine != 0 ? viaEngine : viaWalk;
        if (probe != 0)
        {
            at = $" | at 0x{probe:X}: vtable 0x{*(nint*)probe:X} id+4 0x{*(uint*)(probe + 0x04):X}"
                + $" id+0x14 0x{*(uint*)(probe + 0x14):X} type 0x{*(ushort*)(probe + 0x08):X}";
        }

        return $"0x{objectId:X}: server 0x{server:X} internal 0x{serverInternal:X} "
            + $"array(+0x1005C) 0x{arrayByOffset:X} array(engine) 0x{arrayByEngine:X} | "
            + $"engine 0x{viaEngine:X} walk 0x{viaWalk:X} resolve 0x{resolved:X}{at}";
    }

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

    // ---- doors -------------------------------------------------------------------

    /// <summary>CSWSDoor vtable. Shared with the sweep rather than written twice — a constant
    /// with two copies is a constant that drifts.</summary>
    private const nint DoorVtable = CreatureFinder.DoorVtable;

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

    /// <summary>
    /// CSWSDoor::OpenDoor 0x00589C70 — <c>__thiscall</c>, ONE argument (the OPENER's object
    /// id); <c>ret 4</c> at 0x00589D3A.
    ///
    /// The engine's own door opening, and worth every byte of it rather than a state poke. It
    /// resolves the opener, takes the dot product of (opener.position − door.position) with the
    /// door's own orientation vector at +0x9C and picks swing state **1 or 2 from which side
    /// the opener is standing** (0x00589C97–0x00589CE1); records the opener at +0x31C; calls
    /// <c>SetOpenState(state, bDeferred=1)</c>; opens the door's LINKED twin the same way
    /// (<c>GetLinkedObject 0x00589580</c>, so double doors move together); and finally runs the
    /// door's OnOpen script at +0x228 through <c>CVirtualMachine::RunScript 0x005D0FC0</c>.
    /// Not one of those five things is reproducible by writing a byte.
    ///
    /// It leaves the state PENDING, exactly as the engine does — the swing animation commits
    /// it. <see cref="OpenDoorFully"/> commits it immediately instead, because a staging verb
    /// needs the walkmesh open now rather than in a second and a half.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> OpenDoorFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x00589C70;

    /// <summary>
    /// Opens a door through the engine's own path and commits the swing at once, so the
    /// walkmesh is open on this frame.
    ///
    /// The commit is <see cref="CommitDoorOpenState"/> (<c>MoveToNextOpenState 0x00589680</c>),
    /// which moves pending → current and then calls <c>CSWSArea::BudgeCreatures 0x00508180</c>
    /// with the door's position and its new open/closed bit — the update that makes the door
    /// stop being an obstacle. That is why the animation cannot simply be skipped by writing
    /// +0x2CC: the byte moves the collision without telling the area.
    ///
    /// Returns the open state the door actually ended up in, so the caller reports what the
    /// engine did rather than what it asked for. MAIN THREAD ONLY.
    /// </summary>
    public static byte OpenDoorFully(nint door, uint openerId)
    {
        if (door == 0)
        {
            return 0;
        }

        OpenDoorFn(door, openerId);
        MoveToNextOpenStateFn(door);

        return GetDoorOpenState(door);
    }

    /// <summary>The object's facing vector (CSWSObject::Orientation, +0x9C). For a door this
    /// is the axis <c>OpenDoor</c> itself uses to decide which side the opener is on.</summary>
    public static Vec3 GetOrientation(nint obj) => *(Vec3*)(obj + ObjOrientation);

    // ---- door module transitions: reading the link, and asking to walk through it ----
    //
    // See docs/research/cinematics.md §8 and DoorUse. A door's transition is TWO phases, and
    // the first one is the whole reason this exists: on use, CSWSDoor::EventHandler raises
    // CSWSArea::SetTransitionPending(1, id) 0x00506B30 and starts a 0.5 s fade
    // (0x0058C2A0-0x0058C315); when the fade completes it does the three move-to-module calls
    // (0x0058C185-0x0058C1BE) that Odyssey.TravelToModule also does, and clears the flag.
    // Phase 1 is what opens PluginHost's pre-store flush window, and a plugin-initiated warp
    // never opens it, which is why only a real door can test what a movie does to it.

    /// <summary>CSWSDoor + this = <c>linked_to_flags</c> (1 or 2 = a module transition).</summary>
    private const int DoorLinkedToFlags = 0x384;

    /// <summary>CSWSDoor + this = <c>linked_to</c>, the destination waypoint tag (CExoString).
    /// The engine passes exactly this to SetMoveToModuleStartWaypoint at 0x0058C1AB.</summary>
    private const int DoorLinkedTo = 0x388;

    /// <summary>CSWSDoor + this = <c>linked_to_module</c> (CExoString). The engine passes
    /// exactly this to SetMoveToModuleString at 0x0058C197.</summary>
    private const int DoorLinkedToModule = 0x390;

    /// <summary>
    /// CSWSDoor::GetIsLinked 0x005890D0 — <c>__thiscall</c>, NO arguments. The entire function
    /// is <c>mov al,[ecx+0x384]; cmp al,1; je ok; cmp al,2; je ok; xor eax,eax; ret</c> — a
    /// bare <c>ret</c>, so the argument size is 0, and it dereferences nothing beyond
    /// <c>this</c>. Called rather than reading the byte ourselves because the ENGINE owns the
    /// definition of "this door is a module transition" (transition-window rule 4: identity
    /// comes from the engine whenever an in-process call exists).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> DoorGetIsLinkedFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x005890D0;

    /// <summary>
    /// CSWCMessage::SendPlayerToServerInput_UseObject 0x00677D10 — <c>__thiscall</c>, ONE
    /// argument (the object id). <c>ret 4</c> at 0x00677D4E and 0x00677D6A.
    ///
    /// ⚠ **THIS IS THE PLACEABLE MESSAGE, NOT THE DOOR ONE, AND IT CANNOT START A TRANSITION.**
    /// Its ONE caller in the whole binary is `CSWCPlaceable::UsePlaceable 0x00682660`; the only
    /// thing that sends a door input is `CSWCDoor::ToggleDoorState 0x00683D90`, which sends
    /// `SendPlayerToServerInput_ChangeDoorState 0x00677D70` with 0x2725 to open. Neither one
    /// transitions: a module transition is event **0x1E** on the door, and 0x1E is built only
    /// by movement code (see `CheatController.UseDoor`). Sending this for a door queues
    /// `AddUseObjectAction`, walks the player to the door's action point and stops there —
    /// measured live: 4.1 m to 1.5 m, then nothing at all. Kept, declared honestly, because
    /// the address is right for a PLACEABLE and the comment is what stops the next person
    /// repeating the mistake.
    ///
    /// This is LITERALLY what clicking a placeable does. Its server handler is
    /// <c>CSWSMessage::HandlePlayerToServerInputMessage 0x005254C0</c>, whose jump table at
    /// 0x0052684C sends message type 0x0B to 0x00525B1A, which does
    /// <c>CSWSCreature::ActionManager(2)</c> and then
    /// <c>CSWSObject::AddUseObjectAction 0x0057C810</c> on the player. The queued action runs
    /// <c>CSWSObject::AIActionUseObject 0x0057E8C0</c>, which walks the PC to the door's
    /// action point and posts the signal event (0x0057ECC7 / 0x0057F3C9) that
    /// <c>CSWSDoor::EventHandler 0x0058B850</c> turns into the transition.
    ///
    /// ⚠ The DIRECT send, not the client-side wrapper — the same lesson
    /// <see cref="SendDialogRequest"/> paid for. <c>CSWCDoor</c>'s own menu helpers
    /// (<c>GetTargetActions 0x00684410</c>, <c>MenuAction* 0x00683E50</c>) have client-side
    /// gates that a mouse click satisfies and we do not, and driving them from here leaves the
    /// interaction half-started. The send is both the smaller and the safer half.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> SendUseObjectInputFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x00677D10;

    /// <summary>
    /// CClientExoApp::GetSWParty 0x005ED8B0 — <c>mov eax,[ecx+4]; mov eax,[eax+0x270]; ret</c>.
    /// A bare <c>ret</c>: no arguments, and two loads with no call.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetSWPartyFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED8B0;

    /// <summary>
    /// CSWParty::IsPartyTogether 0x00635350 — <c>__thiscall</c>, NO arguments (bare <c>ret</c>
    /// at 0x0063538D). Reads the player's position and compares every party member's against
    /// it.
    ///
    /// ⚠ THIS IS THE GATE THAT SILENTLY CANCELS A DOOR TRANSITION. <c>CSWSDoor::EventHandler</c>
    /// tests it at 0x0058C228 and, when it is false, jumps to 0x0058C31A — which runs the
    /// script <c>k_trg_transfail</c> (string at 0x007499C4) instead of starting the transition.
    /// Nothing about that failure is visible: the door is used, the fade never starts, and no
    /// module is stored. Reported rather than refused, deliberately — see
    /// <c>CheatController.UseDoor</c>.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> IsPartyTogetherFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x00635350;

    /// <summary>
    /// True when the engine currently considers the party gathered — the predicate a door
    /// transition is gated on. False here predicts <c>k_trg_transfail</c> rather than a move.
    /// Returns true when there is no party object to ask, because "unknown" must not read as
    /// "broken".
    /// </summary>
    public static bool IsPartyTogether()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return true;
        }

        nint party = GetSWPartyFn(client);
        return party == 0 || IsPartyTogetherFn(party) != 0;
    }

    /// <summary>
    /// CClientExoAppInternal::ServerToClientObjectId 0x005EDA50 — <c>__thiscall</c> on the same
    /// <c>client</c> pointer <see cref="GetClient"/> resolves, one stack arg (a SERVER object
    /// id), returns the CLIENT-side id space's own id for the same object.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, uint> ServerToClientObjectIdFn =
        (delegate* unmanaged[Thiscall]<nint, uint, uint>)0x005EDA50;

    /// <summary>
    /// CSWParty::GetIndex 0x006349C0 — <c>__thiscall</c> on a CSWParty*, one stack arg (a
    /// CLIENT-side object id, see <see cref="ServerToClientObjectIdFn"/>), returns that member's
    /// index in the 0x88-strided array <see cref="GetSWPartyFn"/> exposes, or -1.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int> GetPartyIndexFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int>)0x006349C0;

    /// <summary>
    /// CSWParty::RecaulateFollowPoint 0x006359C0 — <c>__thiscall</c>, NO stack arguments beyond
    /// <c>this</c> (a CSWParty*, see <see cref="GetSWPartyFn"/>).
    ///
    /// ROUND 72 (docs/research/phantom-locomotion.md, party-reconciliation.md): live-measured
    /// that a genuinely-owned, genuinely-following companion's own per-member follow-data record
    /// (<c>CSWParty+0x24+index*0x88</c>) holds a TARGET follow point (<c>record+0x28</c>) frozen
    /// at wherever the leader stood the moment the module loaded, unmoving no matter how far the
    /// leader afterward walks — with solo mode confirmed OFF (0) on the host the whole time,
    /// ruling out the obvious "the party-together freeze is suppressing this too" theory.
    ///
    /// ⚠ LIVE-DISPROVEN AS THE FIX, BY ITSELF. Calling this every stall pass (round 72's first
    /// live attempt) changed the frozen <c>record+0x28</c> value NOT AT ALL, several cycles,
    /// including immediately after <see cref="TryPokeFollowPriorPosition"/> had just confirmed
    /// (via the read-back) that <c>record+0x18</c> genuinely held the leader's fresh position at
    /// the moment this was called. Two theories that reading alone made attractive — "the
    /// recompute just isn't being called" and "it reads its input from <c>record+0x18</c>" — are
    /// therefore BOTH wrong, or at minimum incomplete: either this function reads its own
    /// "leader position" from somewhere else entirely (a candidate found this same round:
    /// <c>CSWParty::Update</c> 0x00637050 touches a party-level, not per-member, 3-float field at
    /// <c>this+0x2B0..0x2B8</c> against a GLOBAL cache at <c>0x7A19B0..0x7A19B8</c> that looks
    /// like the more likely "current leader/camera position" input — or it additionally requires
    /// some other precondition (a dirty flag, a moved-since-last-check distance) this call's own
    /// disassembly was not fully traced far enough to identify. NOT called from
    /// <see cref="PartyFollowController"/>'s shipped stall-handling path as a result — kept here,
    /// documented, for whoever picks this up next.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> RecalculateFollowPointFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006359C0;

    /// <summary>
    /// Calls the engine's own <c>CSWParty::RecaulateFollowPoint</c> — see
    /// <see cref="RecalculateFollowPointFn"/>'s own remarks for what this was tried for and why
    /// it is NOT part of the shipped follow-reissue path. No-ops (returns false) when there is no
    /// client/party object to call it on.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool RecalculateFollowPoint()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        RecalculateFollowPointFn(party);
        return true;
    }

    /// <summary>
    /// ROUND 72 DIAGNOSTIC (docs/research/phantom-locomotion.md, party-reconciliation.md):
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360 — the engine's own PER-FRAME
    /// continuation for a companion whose <c>CreMoveState</c> (+0xA8C) already reads 3
    /// ("follow") — opens by resolving <c>this</c> creature's own index within CSWParty via
    /// exactly this chain (<c>ServerToClientObjectId</c> then <c>CSWParty::GetIndex</c>) and
    /// bails out for the rest of the frame, without ever queuing a step, if that index comes
    /// back -1 (not found) or 0 (this creature IS the party's own leader slot — never follows
    /// itself). <see cref="PartyFollowController"/> calls this to tell "the index lookup itself
    /// is the blocker" apart from "the index resolves fine and something deeper in the same
    /// function's own path/offset computation is."
    /// </summary>
    public static bool TryGetClientPartyIndex(uint serverObjectId, out int index)
    {
        index = -2;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, serverObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        index = GetPartyIndexFn(party, clientObjectId);
        return true;
    }

    /// <summary>
    /// ROUND 72 DIAGNOSTIC: the raw per-member follow-data record CSWParty holds at
    /// <c>this+0x24+index*0x88</c> (party-reconciliation.md's own stride, re-confirmed against
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360's own disasm this round), plus
    /// the creature's own <c>+0xD0</c> (an angle/step accumulator the same function tests before
    /// ever touching the follow-data record) and <c>+0x1F8</c> (must be non-zero for
    /// <c>WalkUpdateLocation_QuickWalk</c> 0x0051D9C0 — the caller that dispatches into 0x0051C360
    /// on <c>CreMoveState==3</c> — to run AT ALL; see 0x0051D9E0's own gate). Returns false when
    /// the index itself does not resolve (<see cref="TryGetClientPartyIndex"/> already covers
    /// that case on its own).
    /// </summary>
    public static bool TryGetFollowDiagnostics(nint creature, uint serverObjectId, out FollowDiag diag)
    {
        diag = default;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, serverObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        diag.Index = index;

        if (index <= 0)
        {
            // <=0: no record to read (index 0 is the leader slot 0x0051C360 itself bails on;
            // -1 is "not found"). Whatever the caller already logs about the index covers this.
            return true;
        }

        nint record = party + 0x24 + (index * 0x88);
        diag.TargetPoint = *(Vec3*)(record + 0x28);
        diag.PriorPoint = *(Vec3*)(record + 0x18);
        diag.Snapshot = *(Vec3*)(record + 0x5c);
        diag.SnapshotPendingFlag = *(int*)(record + 0x68);

        diag.StepAccumulator = *(int*)(creature + 0xd0);
        diag.CachedPosPairLo = *(int*)(creature + 0xb0);
        diag.CachedPosPairHi = *(int*)(creature + 0xb4);
        diag.EnabledFlag = *(int*)(creature + 0x1f8);

        // ROUND 74: the step FSM this round is chasing, per WalkUpdateLocation_QuickWalk_
        // FollowLeader's own disasm (0x0051C7E5-0x0051C7F9) — record+4 is the phase index the
        // 11-entry jump table at 0x51CCEC dispatches on (incremented BEFORE the jump: the
        // dispatch uses phase+1, so a phase that has never left its module-load value reads
        // back here as whatever that raw value was, not necessarily 0), and record+0x44 is a
        // SEPARATE attempt/stall counter incremented every call regardless of phase — once it
        // exceeds 0x32 (50) the function force-resets phase to -1 and the counter to 0
        // (0x51C95E-0x51C96B). Sampled together, these say whether the phase dispatch is being
        // reached and cycling AT ALL (proves/disproves this round's leading theory that an
        // EARLIER gate — the edi+0xD0 step-accumulator throttle at 0x51C458-0x51C486 — returns
        // early every frame and never reaches the phase dispatch, vs. the phase dispatch being
        // reached but stalling once inside it, e.g. on the empty "other party slot" read at
        // 0x51C744-0x51C7AC).
        diag.PhaseIndex = *(int*)(record + 4);
        diag.PhaseAttempts = *(int*)(record + 0x44);

        // The "other party member" slot 0x0051C744 resolves against (this creature's own
        // index 1 <-> 2, or 2 <-> 1) — its raw stored id (record2+0x0) read directly the same
        // way, so a genuinely empty/never-populated slot 2 in a one-companion party shows up
        // here as whatever value it actually holds (0 if the array is zero-initialized,
        // garbage otherwise) rather than being inferred.
        int otherIndex = index == 1 ? 2 : index == 2 ? 1 : 0;
        nint otherRecord = party + 0x24 + (otherIndex * 0x88);
        diag.OtherSlotIndex = otherIndex;
        diag.OtherSlotStoredId = *(uint*)otherRecord;

        diag.Valid = true;
        return true;
    }

    public struct FollowDiag
    {
        public bool Valid;
        public int Index;
        public Vec3 TargetPoint;
        public Vec3 PriorPoint;
        public Vec3 Snapshot;
        public int SnapshotPendingFlag;
        public int StepAccumulator;
        public int CachedPosPairLo;
        public int CachedPosPairHi;
        public int EnabledFlag;
        public int PhaseIndex;
        public int PhaseAttempts;
        public int OtherSlotIndex;
        public uint OtherSlotStoredId;
    }

    /// <summary>
    /// ROUND 72 EXPERIMENT, LIVE-DISPROVEN: writes the per-member follow-data record's OWN
    /// cached leader position (<c>record+0x18</c>, <see cref="FollowDiag.PriorPoint"/>) directly
    /// — testing, combined with an immediate <see cref="RecalculateFollowPoint"/> call, whether
    /// <c>CSWParty::RecaulateFollowPoint</c> computes the follow TARGET point (<c>record+0x28</c>)
    /// FROM this cached field. The WRITE itself is confirmed live to work and to stick (read back
    /// correctly on the very next diagnostic pass, nothing else claws it back) — but the recompute
    /// that followed it still left <c>record+0x28</c> exactly at its module-load value, so this
    /// field is NOT the recompute's input, or is not sufficient on its own. NOT called from
    /// <see cref="PartyFollowController"/>'s shipped path — a field write with a measured zero
    /// benefit is not something to leave mutating engine state on every stall pass. Kept here,
    /// with its own live result recorded, as a ruled-out avenue for whoever picks this up next
    /// (see <see cref="RecalculateFollowPointFn"/>'s own remarks for the current prime suspect,
    /// <c>CSWParty::Update</c> 0x00637050).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryPokeFollowPriorPosition(uint companionServerObjectId, Vec3 leaderPos)
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            return false;
        }

        nint record = party + 0x24 + (index * 0x88);
        *(Vec3*)(record + 0x18) = leaderPos;
        return true;
    }

    /// <summary>
    /// ROUND 74 EXPERIMENT: <c>WalkUpdateLocation_QuickWalk_FollowLeader</c>'s own step FSM
    /// (0x0051C360, jump table at 0x51CCEC) reads <c>record+4</c> as its phase index (dispatch
    /// index = phase+1) and <c>record+0x44</c> as an attempt counter that, once it passes 0x32
    /// (50) WITHOUT the phase changing, force-resets phase to -1 (0x51C964-0x51C96B). Dispatch
    /// index 0 (phase == -1) is the inline block at 0x51C800: it re-snapshots the creature's
    /// current position into <c>record+0x5c</c> and sets phase DIRECTLY to 5 — WITHOUT ever
    /// calling <c>WalkUpdateLocation_QuickWalk_FollowLeader_FindPath</c> (phase 0, dispatch
    /// index 1, 0x51AC10), the ONLY handler that actually submits a fresh pathfinding request
    /// (via <c>CSWSCreature::AddMoveToPointActionToFront</c>) and unconditionally writes
    /// <c>CreMoveState</c> (creature+0xA8C) to -1 ("walking") on its way out
    /// (0x51AF5A, the shared epilogue both of FindPath's branches funnel into). Phase 5 is
    /// <c>WalkUpdateLocation_QuickWalk_FollowLeader_WaitingForPath</c> (0x5127D0), which does
    /// nothing but poll an async job-status field (creature+0x2DC, then +0xFA8 off THAT
    /// pointer) for &gt;= 5 and return "not ready" otherwise — a job that was never submitted,
    /// because FindPath was skipped. Once phase lands on -1 this way there is no natural way
    /// back into FindPath: -1 always redispatches to entry 0, which always re-enters phase 5
    /// without resubmitting anything. Live-observed round 74: a companion stalled long enough
    /// to have hit this cycle once (plausible after many minutes of round 71-73's repeated
    /// stall-reissues, each re-entering this same per-frame continuation) reads phase=5,
    /// attempts cycling through 0x32 and wrapping back down — the FSM genuinely running, every
    /// frame, permanently unable to leave the WaitingForPath/-1 ping-pong.
    ///
    /// This writes <c>record+4</c> to 0 (dispatch index 1 next call — FindPath, not the
    /// entry-0 shortcut) and <c>record+0x44</c> to 0 (clear the attempt counter so the
    /// watchdog does not immediately re-force -1 on the very next call), ONLY when the phase
    /// is presently stuck at -1 or 5 — the two states this round's disasm shows can never
    /// self-recover. A companion in any other phase is left alone: those are mid-flight in the
    /// FSM's own real progression and a phase stomp there would be actively destructive, not a
    /// nudge.
    ///
    /// Returns false when the index does not resolve. Returns true whether or not a write
    /// happened (the phase-guard is the caller-visible "did I actually intervene" signal via
    /// <paramref name="wrote"/>).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryResetStuckFollowPhase(uint companionServerObjectId, out bool wrote)
    {
        wrote = false;
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            return false;
        }

        nint record = party + 0x24 + (index * 0x88);
        int phase = *(int*)(record + 4);
        if (phase == -1 || phase == 5)
        {
            *(int*)(record + 4) = 0;
            *(int*)(record + 0x44) = 0;
            wrote = true;
        }

        return true;
    }

    /// <summary>
    /// ROUND 73 FALLBACK (per the round's own brief, step 3: "if CSWParty::Update doesn't move
    /// the frozen point either, directly write the follow-target point / cached leader position
    /// fields to the actual current leader position each step"). It does not — see
    /// <see cref="PartyUpdateFn"/>'s and <see cref="RecalculateFollowPointFn"/>'s own remarks:
    /// this round confirmed live that <c>CSWParty::Update</c> genuinely refreshes the
    /// PARTY-LEVEL reference position (<c>party+0x2B0..0x2B8</c>) from the leader's live
    /// position every frame, and that an unconditional <c>RecaulateFollowPoint</c> call fed by
    /// that fresh reference STILL leaves a stalled companion's PER-MEMBER
    /// <c>record+0x28</c>/<c>record+0x18</c> exactly where they were. Neither engine call
    /// reaches those two fields for a companion in this state, for a reason this round's
    /// disassembly of <c>WalkUpdateLocation_QuickWalk_FollowLeader</c> 0x0051C360 suggests but
    /// did not fully settle: the function treats a "target changed" transition as a DIRTY EVENT
    /// — <c>record+0x68</c> (<see cref="FollowDiag.SnapshotPendingFlag"/>) is read as a flag
    /// that, when set, makes the function copy the creature's own current position
    /// (<c>creature+0x90</c>) into <c>record+0x5c</c> (the "snapshot" <see cref="FollowDiag"/>
    /// already exposes) and then clear the flag — i.e. it looks like the SIGNAL that tells this
    /// per-frame continuation "the target moved, re-snapshot and compute a fresh step," not
    /// merely a byproduct of moving the target. A raw write to <c>record+0x28</c> alone (as
    /// round 72 tried for <c>record+0x18</c>) never sets it, so nothing downstream ever notices.
    ///
    /// This writes <c>record+0x28</c> (target) AND <c>record+0x18</c> (the cached leader
    /// position <see cref="TryPokeFollowPriorPosition"/> already covers) together, and ALSO
    /// sets <c>record+0x68</c> to 1 — but ONLY when the target has moved more than a small
    /// epsilon from its current value, so this does not stomp the flag every single frame once
    /// the companion is genuinely caught up and walking normally (which would fight the
    /// function's own snapshot/step bookkeeping instead of helping it). Returns false when the
    /// index does not resolve; returns true (having written nothing) when the target is already
    /// within the epsilon of <paramref name="leaderPos"/>.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryDriveFollowTarget(uint companionServerObjectId, Vec3 leaderPos)
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        uint clientObjectId = ServerToClientObjectIdFn(client, companionServerObjectId);

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = GetPartyIndexFn(party, clientObjectId);
        if (index <= 0)
        {
            return false;
        }

        nint record = party + 0x24 + (index * 0x88);
        Vec3 existingTarget = *(Vec3*)(record + 0x28);
        float dx = existingTarget.X - leaderPos.X;
        float dy = existingTarget.Y - leaderPos.Y;

        // ROUND 74 TRIED AND REVERTED: WalkUpdateLocation_QuickWalk_FollowLeader's own gate at
        // 0x51C4D5 computes target-minus-prior and branches on whether that is "close to zero"
        // (0x51C61F — live-confirmed round 74 to park the step FSM at whatever phase it last
        // reached and never re-trigger the phase dispatch again) or "a real delta" (0x51C4E6 —
        // the path this round's disasm confirms genuinely reaches and advances the phase
        // dispatch). This round tried writing prior <- the OLD target (instead of the same new
        // value target is about to become) EVERY call, unconditionally, on the theory that a
        // real, honest, small delta every frame would keep the "differs" path live for as long
        // as the leader keeps moving. Live-tested against a leader driven through 12 real
        // engine teleports ~1.5s apart (tools/blockpoke.ps1 console 'tp'): the party-follow
        // diagnostic log (which only samples once every reissue pass, ~2s) never once showed a
        // nonzero delta, and the phase/attempt counters this round's own diagnostics added
        // (Odyssey.FollowDiag.PhaseIndex/PhaseAttempts) never advanced past where
        // Odyssey.TryResetStuckFollowPhase alone already gets a stalled companion. The one-frame
        // window where target genuinely jumps ahead of prior (right after a teleport, before the
        // NEXT frame's write converges them again) is far too narrow for this plugin's ~2s
        // reissue-driven sampling to confirm any benefit, and rewriting record+0x18 on every
        // single frame (vs. round 73's original >10cm-gated write) is a real increase in engine
        // write frequency for a benefit that could not be measured. Reverted to round 73's
        // original, verified-safe, epsilon-gated write rather than ship an unproven behaviour
        // change — see Odyssey.TryResetStuckFollowPhase's own remarks for what round 74 DID
        // confirm live.
        if ((dx * dx) + (dy * dy) > 0.01f) // ~10cm — "meaningfully different," not float noise
        {
            *(Vec3*)(record + 0x28) = leaderPos;
            *(Vec3*)(record + 0x18) = leaderPos;
            *(int*)(record + 0x68) = 1;
        }

        return true;
    }

    /// <summary>
    /// <c>CSWParty::Update</c> 0x00637050 — <c>__thiscall</c>, ONE stack argument (4 bytes,
    /// matching <c>param_size_bytes=4</c> in <c>tools/kotor1_0_3.db</c>): a delta-time FLOAT,
    /// verified live round 73 via this function's single caller in the whole binary (
    /// <c>tools/xrefs.py 0x637050</c> finds exactly one, at 0x0060339A). That caller sits inside
    /// <c>CClientExoAppInternal</c>'s own per-frame update dispatch — the same routine that also
    /// calls <c>CClientExoAppInternal::ProcessInput</c> and <c>CExoResMan::Update</c> every
    /// frame — and computes <c>deltaTime * &lt;a global time-scale multiplier at 0x7439BC&gt;</c>
    /// into a scratch float (0x78E574) that it pushes as this function's only argument. This
    /// function's own body (round 73 disasm) opens with
    /// <c>fld dword ptr [ebp+8]; fadd dword ptr [this+0x2BC]; fstp dword ptr [this+0x2BC]</c> —
    /// an elapsed-time ACCUMULATOR at <c>this+0x2BC</c>, consistent with nothing but a delta
    /// time.
    ///
    /// ROUND 73: this is the missing per-frame driver round 72 named as the prime suspect but
    /// did not call. Past the accumulator, this function refreshes a PARTY-LEVEL (not
    /// per-member) reference position at <c>this+0x2B0..0x2B8</c> from the leader creature's own
    /// LIVE position (read fresh via <c>GetCreatureByGameObjectID</c> off the id at
    /// <c>this+0x24</c>, the leader/member-0 slot) whenever the leader has moved more than a
    /// threshold (the float at 0x741838) since the last call — then, if the party's own
    /// "furthest member" (<c>CSWPartyFollowDataArray::FindFurthestPartyMemeber_Current</c>,
    /// <c>this+0x2DC</c>) has drifted far enough
    /// (<c>PointOnLineDistanceFromLeader</c> vs the float at 0x747C3C) and
    /// <c>CSWParty::TestAndResetPath()</c> reports the path is not already being reset, it calls
    /// <c>CSWParty::RecaulateFollowPoint()</c> itself — the exact function round 72 already tried
    /// calling directly and found to have ZERO effect on a companion's frozen follow target.
    /// That null result is now explained: round 72's manual <c>RecaulateFollowPoint</c> call read
    /// <c>this+0x2B0..0x2B8</c> as its own "current leader position" input, and that field is
    /// ONLY ever refreshed by THIS function — which was never being called at all in the
    /// coop-driven session (its one real caller lives inside a per-frame dispatch this plugin's
    /// own drive loop does not reach for the reasons rounds 60-67 already found for animation),
    /// so the manual recompute was working from a reference position frozen at module-load, same
    /// as the per-member target it produced.
    ///
    /// The caller's own gate (<c>esi+0x18</c> non-null and <c>[esi+0x18+0x48]</c> non-zero, both
    /// read immediately before the real engine's push/call) is NOT reproduced here —
    /// <see cref="PumpPartyUpdate"/> calls this unconditionally, once per rendered frame, while a
    /// party object exists, guarded only by the same preconditions
    /// <see cref="PartyFollowController"/> already applies before touching anything (spawned and
    /// driving, not mid-conversation, roster not frozen, no peer party-presence channel active).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, void> PartyUpdateFn =
        (delegate* unmanaged[Thiscall]<nint, float, void>)0x00637050;

    /// <summary>
    /// Calls the engine's own per-frame <c>CSWParty::Update</c> — see
    /// <see cref="PartyUpdateFn"/>'s own remarks for what this drives and why round 72's fix
    /// attempts had no effect without it. Returns false when there is no client/party object to
    /// call it on.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PumpPartyUpdate(float deltaTimeSeconds)
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        PartyUpdateFn(party, deltaTimeSeconds);
        return true;
    }

    /// <summary>
    /// CClientExoApp::GetCreatureByGameObjectID 0x005ED5B0 — <c>__thiscall</c> on the CLIENT
    /// (<see cref="GetClient"/>), one stack arg (a CLIENT-side object id), returns the creature
    /// pointer or 0. This is the SAME call <c>CSWParty::Update</c> itself makes (round 73 disasm,
    /// see <see cref="PartyUpdateFn"/>) to resolve the leader record's own stored id
    /// (<c>party+0x24</c>) into a creature pointer before reading its position.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> GetCreatureByClientIdFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x005ED5B0;

    /// <summary>
    /// ROUND 73 DIAGNOSTIC: direct visibility into the PARTY-LEVEL fields
    /// <c>CSWParty::Update</c> itself reads and writes, to tell "the function isn't running"
    /// apart from "it runs but its own leader lookup or position offset isn't what this round's
    /// disasm assumed": <c>party+0x24</c> (the leader record's own stored CLIENT object id),
    /// <c>party+0x2B0..0x2B8</c> (the reference position the function refreshes from the
    /// leader), and <c>party+0x2BC</c> (the elapsed-time accumulator its first three
    /// instructions maintain — a monotonically increasing value while
    /// <see cref="PumpPartyUpdate"/> is genuinely being called each frame, regardless of
    /// whether anything else in the function's body does what this round's disasm expects).
    /// Also resolves the leader id through <see cref="GetCreatureByClientIdFn"/> — the SAME
    /// call the function itself makes — and reads that creature's <c>+0x24/+0x28/+0x2C</c>,
    /// the exact offsets <c>CSWParty::Update</c> subtracts from <c>party+0x2B0..0x2B8</c> (NOT
    /// necessarily the same as <c>CSWSObject</c>'s usual <c>+0x90</c> world-position field —
    /// this round's live data is what settles whether they agree).
    /// </summary>
    public static bool TryGetPartyReferenceState(out PartyRefState state)
    {
        state = default;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        state.LeaderRecordId = *(uint*)(party + 0x24);
        state.ReferencePos = *(Vec3*)(party + 0x2b0);
        state.TimeAccumulator = *(float*)(party + 0x2bc);

        nint leaderCreature = GetCreatureByClientIdFn(client, state.LeaderRecordId);
        state.LeaderCreaturePtr = leaderCreature;
        if (leaderCreature != 0)
        {
            state.LeaderFieldPos = *(Vec3*)(leaderCreature + 0x24);
        }

        state.Valid = true;
        return true;
    }

    public struct PartyRefState
    {
        public bool Valid;
        public uint LeaderRecordId;
        public Vec3 ReferencePos;
        public float TimeAccumulator;
        public nint LeaderCreaturePtr;
        public Vec3 LeaderFieldPos;
    }

    /// <summary>
    /// CServerExoApp::GetPartyTable 0x004AEE70 — <c>mov eax,[ecx+4]; add eax,0x1B770; ret</c>.
    /// A bare <c>ret</c>: no arguments, no dereference past <c>this</c>. The table is EMBEDDED
    /// in CServerExoAppInternal, so this can never be null while the server exists.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetPartyTableFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AEE70;

    /// <summary>
    /// CSWPartyTable::PositionParty 0x00565B00 — <c>__thiscall</c>, NO arguments (bare
    /// <c>ret</c> at 0x00565D1C). The engine's own "put the party where it belongs": for each
    /// member it computes a formation offset around the player
    /// (<c>CSWParty::GetWorldFormationPosition</c> data at party+0xD4), snaps it to something
    /// walkable with <c>CServerExoApp::ComputeSafeLocation 0x004AEB60</c> and a 10 m search
    /// radius, then writes it with <c>CSWSObject::SetPosition 0x004CD1A0</c> and
    /// <c>SetOrientation 0x004CD170</c>. On the way in it calls
    /// <c>CSWParty::ResetAllFollowData 0x00637890</c> with the player's position; on the way
    /// out, <c>UnstealthParty(0)</c> and it clears <c>pt_solomode</c> (+0x190).
    ///
    /// Its ONE caller in the whole binary is
    /// <c>CServerExoAppInternal::MovePlayerToArea 0x004B3D10</c> at 0x004B3E9E — i.e. this is
    /// exactly the party collection a module transition performs, which is why it is the right
    /// call for "gather the party to me" and not a reimplementation of one.
    ///
    /// ⚠ It reads the player creature unconditionally (<c>GetPlayerCreatureId</c> →
    /// <c>GetGameObject</c> → <c>[creature+0x9C]</c>), so it must never be called without a
    /// player, and it reads <c>CClientExoApp::GetSWParty</c>, so it must never be called
    /// without a party object either. <see cref="GatherParty"/> checks both.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> PositionPartyFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x00565B00;

    /// <summary>
    /// CSWPartyTable + this = a one-shot "skip the positioning" flag. PositionParty tests it
    /// at 0x00565B6E and, when it is non-zero, jumps STRAIGHT to the exit — which clears it
    /// and does nothing else. So a call made while it is set positions nobody and the NEXT
    /// call works. <see cref="GatherParty"/> reads it first rather than discovering that by
    /// having its first attempt silently do nothing.
    /// </summary>
    private const int PartyTableSkipPositioning = 0x10C;

    /// <summary>CSWPartyTable + 0 = how many members the party holds; +4 begins their ids.
    /// Read out of PositionParty's own loop bound (<c>cmp [ebx], ebp; jle</c> at 0x00565B7A
    /// and <c>lea ebp,[ebx+4]</c> at 0x00565BAC).</summary>
    private const int PartyTableMemberCount = 0x00;

    /// <summary>How many members the party table currently holds, or 0.</summary>
    public static int GetPartyMemberCount()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint table = GetPartyTableFn(server);
        if (table == 0)
        {
            return 0;
        }

        int count = *(int*)(table + PartyTableMemberCount);
        return count is > 0 and <= 16 ? count : 0;
    }

    /// <summary>
    /// Same read as <see cref="GetPartyMemberCount"/>, but distinguishes "read 0 because the
    /// table legitimately holds no companions" from "read 0 because there is no server/table to
    /// ask" (main menu, mid-transition). <see cref="TryClampClientPartyCount"/> needs that
    /// distinction — it must never clamp CSWParty toward a value that was really "unknown".
    /// </summary>
    public static bool TryGetPartyTableMemberCount(out int count)
    {
        count = 0;

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        nint table = GetPartyTableFn(server);
        if (table == 0)
        {
            return false;
        }

        int raw = *(int*)(table + PartyTableMemberCount);
        if (raw is < 0 or > 16)
        {
            return false;
        }

        count = raw;
        return true;
    }

    /// <summary>
    /// CSWParty (the CLIENT-SIDE struct <see cref="GetSWPartyFn"/> resolves — <c>[client+4]</c>
    /// then <c>+0x270</c> — a DIFFERENT structure from CSWPartyTable/<see cref="GetPartyTableFn"/>,
    /// which lives on the server side at <c>CServerExoAppInternal+0x1B770</c>) + this = its own
    /// member count. Read out of <c>CSWParty::IsPartyTogether</c>'s own loop bound
    /// (<c>mov eax,[edi]; test eax,eax; jle 0x635449</c> at 0x006353A8/0x006353B6, where
    /// <c>edi</c> is the <c>this</c> IsPartyTogether was thiscall'd with — i.e. exactly the
    /// pointer <see cref="GetSWPartyFn"/> returns). A non-zero read here means the loop runs and
    /// each surviving entry's position is diffed against the player's — see
    /// <see cref="IsPartyTogether"/>'s own doc comment for what a failing entry does.
    ///
    /// Expected to move in step with CSWPartyTable's own count — <c>RemovePartyMember</c>'s VM
    /// handler at 0x00541C00 calls back into THIS struct (0x00541C64/0x00541C6C/0x00541C85) on
    /// every table-side remove that resolves a live object — but measured live to diverge on a
    /// co-op client: CSWParty kept a stale, higher count after CSWPartyTable's own read 0
    /// (docs/research/party-reconciliation.md, transition-smoothness.md §8.5). That divergence
    /// is exactly what makes a real door refuse every client-side crossing, including one with
    /// no real companions at all.
    /// </summary>
    private const int SwPartyMemberCount = 0x00;

    /// <summary>CSWParty's own member count, or -1 when there is no client or no party object to
    /// ask (see <see cref="SwPartyMemberCount"/>).</summary>
    public static int GetClientPartyMemberCount()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return -1;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return -1;
        }

        return *(int*)(party + SwPartyMemberCount);
    }

    /// <summary>
    /// Clamps CSWParty's own member count DOWN to <paramref name="maxCount"/> when it currently
    /// reads higher. NEVER raises it — this only removes staleness the engine's own remove path
    /// was supposed to clear and measured live not to (<see cref="SwPartyMemberCount"/>); it can
    /// never invent a member <see cref="IsPartyTogether"/> would then check a garbage position
    /// for. Refuses to touch a count outside 0..16 — a struct that does not look like a member
    /// count is not one this was verified to understand.
    ///
    /// MAIN THREAD ONLY (a raw field write, same rule as every other engine poke in this file).
    /// </summary>
    public static bool TryClampClientPartyCount(int maxCount, out int before)
    {
        before = -1;

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int* countPtr = (int*)(party + SwPartyMemberCount);
        before = *countPtr;

        if (before <= maxCount || before is < 0 or > 16)
        {
            return false;
        }

        *countPtr = maxCount;
        return true;
    }

    /// <summary>
    /// Teleports every party companion into formation around the local player, through the
    /// engine's own <c>PositionParty</c> — the same call a module transition makes.
    ///
    /// This exists for one reason. A KOTOR door transition is gated on
    /// <see cref="IsPartyTogether"/> (0x0058C228), and a staging verb that moves only the PC
    /// leaves the companions where they were and the gate then cancels the transition into
    /// <c>k_trg_transfail</c> — silently. Moving the party with the engine's own routine gets
    /// the walkmesh snap, the formation and the follow-data reset for free, which is exactly
    /// what a hand-rolled position write would get wrong.
    ///
    /// Returns false, without calling anything, when the preconditions PositionParty
    /// dereferences are not met.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool GatherParty(out int members, out bool skipFlagWasSet)
    {
        members = 0;
        skipFlagWasSet = false;

        nint server = GetServer();
        if (server == 0 || GetPlayerCreatureFn(server) == 0)
        {
            return false;
        }

        nint client = GetClient();
        if (client == 0 || GetSWPartyFn(client) == 0)
        {
            return false;
        }

        nint table = GetPartyTableFn(server);
        if (table == 0)
        {
            return false;
        }

        members = *(int*)(table + PartyTableMemberCount);
        if (members is < 0 or > 16)
        {
            return false;
        }

        skipFlagWasSet = *(int*)(table + PartyTableSkipPositioning) != 0;

        PositionPartyFn(table);

        // The flag was set, so that call only cleared it and moved nobody (0x00565B6E). The
        // second call is the one that positions the party — deterministic, not a retry: the
        // branch is read out of the function, and the flag is provably clear now.
        if (skipFlagWasSet)
        {
            PositionPartyFn(table);
        }

        return true;
    }

    /// <summary>True when this door is a module transition, by the engine's own definition.</summary>
    public static bool DoorIsLinked(nint door) => door != 0 && DoorGetIsLinkedFn(door) != 0;

    /// <summary>The raw <c>linked_to_flags</c> byte, for an answer that says what it saw
    /// rather than just "no".</summary>
    public static byte GetDoorLinkFlags(nint door) => *(byte*)(door + DoorLinkedToFlags);

    /// <summary>Copies the door's destination module resref. Returns bytes written.</summary>
    public static int ReadDoorLinkedModule(nint door, byte* destination, int capacity) =>
        ReadExoString(door + DoorLinkedToModule, destination, capacity);

    /// <summary>Copies the door's destination waypoint tag. Returns bytes written.</summary>
    public static int ReadDoorLinkedWaypoint(nint door, byte* destination, int capacity) =>
        ReadExoString(door + DoorLinkedTo, destination, capacity);

    /// <summary>
    /// Copies a CExoString at <paramref name="field"/> as ASCII, cutting at the first null.
    ///
    /// Cutting rather than trusting the length is deliberate and load-bearing: a CExoString's
    /// length field COUNTS THE TERMINATOR, and believing it has produced three separate
    /// off-by-one bugs in this codebase. Every pointer is validated before it is followed,
    /// because a door read during a transition can hold a header that is already garbage.
    /// </summary>
    private static int ReadExoString(nint field, byte* destination, int capacity)
    {
        if (destination == null || capacity <= 0)
        {
            return 0;
        }

        nint chars = Deref(field);
        int length = *(int*)(field + 4);

        if (!LooksLikeHeapPointer(chars) || length is <= 0 or > 256)
        {
            return 0;
        }

        int written = 0;
        while (written < capacity && written < length)
        {
            byte c = *(byte*)(chars + written);
            if (c == 0)
            {
                break;
            }

            destination[written++] = c;
        }

        return written;
    }

    /// <summary>
    /// Sends the local player's "I clicked that" for <paramref name="objectId"/>, through the
    /// engine's own client-to-server input pipe. Returns false when there is no client or no
    /// message object — never silently.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool UseObject(uint objectId)
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint message = GetSWCMessageFn(client);
        if (message == 0)
        {
            return false;
        }

        SendUseObjectInputFn(message, objectId);
        return true;
    }

    /// <summary>
    /// Copies every door in the loaded module into <paramref name="into"/>, returning how many
    /// were written. One full address-space sweep; a debug verb only, never per frame.
    /// </summary>
    public static int CollectDoors(nint* into, int capacity) =>
        CreatureFinder.CollectByVtable(DoorVtable, into, capacity);

    /// <summary>Finds a door by tag. 0 when this module has no door with that tag.</summary>
    public static nint FindDoorByTag(byte* tag, int length) =>
        length <= 0 ? 0 : CreatureFinder.FindDoorByTag(new ReadOnlySpan<byte>(tag, length));

    // CSWSPlaceable. Unlike a door, a placeable has no walkmesh coupling, so mirroring its
    // locked/open state is a plain field write — no engine call, no crash risk (see
    // docs/research/placeables-containers.md). Both fields are ints, saved as bytes.
    private const nint PlaceableVtable = 0x007494D0;
    private const int PlaceableLocked = 0x260;
    private const int PlaceableOpen = 0x338;
    private const int PlaceableTrap = 0x278; // "is trapped"; clearing = disarmed

    /// <summary>Resolves a placeable by object id, vtable-checked. 0 if absent or not one.</summary>
    public static nint ResolvePlaceable(uint objectId)
    {
        nint placeable = ResolveServerObject(objectId);
        return placeable != 0 && *(nint*)placeable == PlaceableVtable ? placeable : 0;
    }

    public static bool IsPlaceableLocked(nint placeable) => *(int*)(placeable + PlaceableLocked) != 0;

    public static void UnlockPlaceable(nint placeable) => *(int*)(placeable + PlaceableLocked) = 0;

    public static bool IsPlaceableOpen(nint placeable) => *(int*)(placeable + PlaceableOpen) != 0;

    public static void SetPlaceableOpen(nint placeable) => *(int*)(placeable + PlaceableOpen) = 1;

    public static bool IsPlaceableTrapped(nint placeable) => *(int*)(placeable + PlaceableTrap) != 0;

    public static void DisarmPlaceable(nint placeable) => *(int*)(placeable + PlaceableTrap) = 0;

    // ---- container loot ----------------------------------------------------------
    //
    // A container's contents live in one CItemRepository* at CSWSPlaceable+0x36C (also
    // corpses/body-bags). Its item list at repo+0x0C is a flat array of OBJECT IDS (DWORDs,
    // not pointers); the count is at repo+0x10. RemoveItem deletes one. See
    // docs/research/placeables-containers.md §2 (all VERIFIED there).

    /// <summary>CSWSPlaceable + this = CItemRepository* (0 when it has no inventory).</summary>
    private const int PlaceableRepository = 0x36C;

    /// <summary>CItemRepository + this = flat array of item object ids (DWORDs).</summary>
    private const int RepositoryItemArray = 0x0C;

    /// <summary>CItemRepository + this = item count.</summary>
    private const int RepositoryItemCount = 0x10;

    // CSWSPlaceable::RemoveItem(CSWSItem*) 0x00584AC0 — the container "take" primitive
    // (VERIFIED, clean thiscall). It confirms the item is in this placeable's repository
    // (GetItemInRepository), unlinks the id, then detaches the item, so calling it for an
    // item that is not in this container is itself a safe no-op. MAIN THREAD ONLY.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, void> PlaceableRemoveItemFn =
        (delegate* unmanaged[Thiscall]<nint, nint, void>)0x00584AC0;

    /// <summary>
    /// Why the last <see cref="ResolveItemBase"/> refused, in words, or empty when it
    /// succeeded.
    ///
    /// This exists because the failure it reports is invisible otherwise. `ApplyItemIdentity`
    /// deliberately does nothing when the id is not a live item — a wrong write into a
    /// stranger's object is far worse than a stock item — but "did nothing" and "is broken"
    /// look identical from outside, and that ambiguity is exactly what left the whole apply
    /// path unverified through a live pass (docs/live-verification.md, 2026-08-15). A guard
    /// that fails safe still has to say what it saw.
    /// </summary>
    public static string LastItemResolveNote = string.Empty;

    /// <summary>
    /// Resolves an item object id to its CSWSItem base, vtable-checked. Mirrors the two-shape
    /// id logic the ground-item reader uses: GetGameObject may hand back the CSWSItem base
    /// (id at +0x14) or the embedded CSWSObject (id at +0x04). Returns 0 if not a live item,
    /// leaving <see cref="LastItemResolveNote"/> saying which step refused and what it read.
    ///
    /// The vtable constant is not a guess: <c>CSWSItem::Constructor</c> 0x005530A0 writes
    /// <c>0x748DA0</c> to <c>[this]</c> and <c>0x748CC0</c> to <c>[this+0x10]</c> (the
    /// embedded CGameObject's own vptr) at 0x005530EE/0x005530F4. So the base carries
    /// 0x748DA0 and the subobject carries something else, which is why a vtable test applied
    /// to the RAW pointer rejects every item and one applied to the resolved base does not.
    /// </summary>
    private static nint ResolveItemBase(uint itemId)
    {
        nint server = GetServer();
        if (server == 0)
        {
            LastItemResolveNote = "no server (not in a world)";
            return 0;
        }

        nint pointer = GetGameObjectFn(server, itemId);
        if (pointer == 0)
        {
            LastItemResolveNote = $"no object 0x{itemId:X} in this module";
            return 0;
        }

        nint itemBase;
        if (*(uint*)(pointer + 0x14) == itemId)
        {
            itemBase = pointer;
        }
        else if (*(uint*)(pointer + 0x04) == itemId)
        {
            itemBase = pointer - 0x10;
        }
        else
        {
            LastItemResolveNote =
                $"0x{itemId:X} resolves to 0x{pointer:X}, but its id is neither at +0x14 "
                + $"(0x{*(uint*)(pointer + 0x14):X}) nor at +0x04 (0x{*(uint*)(pointer + 0x04):X})";
            return 0;
        }

        nint vtable = *(nint*)itemBase;
        if (vtable != ItemVtable)
        {
            LastItemResolveNote =
                $"0x{itemId:X} is at 0x{itemBase:X} but its vtable is 0x{vtable:X}, "
                + $"not CSWSItem's 0x{ItemVtable:X} — it is some other kind of object";
            return 0;
        }

        LastItemResolveNote = string.Empty;
        return itemBase;
    }

    /// <summary>
    /// A live <c>CSWSItem</c>'s per-instance state, exactly as the five fields hold it. Raw
    /// engine values rather than the wire shape: this is what a read-back has to report, and
    /// re-encoding it would hide the very difference a read-back exists to reveal.
    /// </summary>
    internal struct ItemIdentityFields
    {
        public uint StackSize;
        public uint Charges;
        public uint MaxCharges;
        public uint Upgrades;

        /// <summary>The whole +0x288 dword, uncarried bits included — a read-back that
        /// silently dropped bits 3/4/7/8 could not show that the write left them alone.</summary>
        public uint BitFlags;

        /// <summary>The possessor id at +0x268; <see cref="ObjectInvalid"/> means the item is
        /// lying on the floor. Not part of identity, but it is the one fact that tells a
        /// listing whose pack an item is in.</summary>
        public uint Possessor;

        /// <summary>
        /// False when the item's Tag is empty — which, for a body we just asked the engine to
        /// create from a named blueprint, means the blueprint DOES NOT EXIST. See
        /// <see cref="LooksUnloaded"/>.
        /// </summary>
        public bool HasTag;

        /// <summary>
        /// This body was never filled in from a template.
        ///
        /// ⚠ `CreateItemOnFloor` 0x00547F60 calls `LoadFromTemplate` 0x005608B0 at
        /// 0x0054806B and **never tests its result** — it goes straight on to add the item to
        /// the area and returns the object id at 0x005480AF. So a resref that names no
        /// blueprint yields a live, addressable, perfectly valid `CSWSItem` carrying nothing
        /// but its constructor's defaults, and every "did it spawn?" guard reports success.
        ///
        /// The tell is the pair below, both from `CSWSItem::Constructor` 0x005530A0: it
        /// leaves the Tag empty (it never writes +0x28) and sets Charges to exactly 50
        /// (0x00553189). All 557 item blueprints in the base game declare a Tag, so an empty
        /// one is not something a real load produces.
        ///
        /// Measured live 2026-08-15: a drop of `g_w_blstrpstl01` — a typo, the real ones are
        /// `g_w_blstrpstl001`..`010` — produced object 0x131 with no tag, 50 charges, an
        /// uninitialised MaxCharges of 1768092, stack 1 and no upgrades. Exactly this.
        /// </summary>
        public bool LooksUnloaded => !HasTag;
    }

    /// <summary>CSWSItem + this = the object's Tag, a CExoString (char* then length). From
    /// `SaveItem` 0x0055CCEE, which writes <c>[esi+0x28]</c> under the GFF label "Tag".</summary>
    private const int ItemTag = 0x28;

    /// <summary>
    /// Reads one live item's identity fields through the engine's own object lookup.
    ///
    /// The counterpart to <see cref="ApplyItemIdentity"/> and the read-back that proves it:
    /// the plugin writes, then reads the same five addresses back and reports what is really
    /// there, so "applied" means the values are in the item rather than that a function
    /// returned true. False leaves <see cref="LastItemResolveNote"/> saying why.
    ///
    /// MAIN THREAD ONLY (it calls GetGameObject).
    /// </summary>
    internal static bool TryReadItemIdentityFields(uint itemId, out ItemIdentityFields fields)
    {
        fields = default;

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            return false;
        }

        fields = ReadItemFields(item);
        return true;
    }

    /// <summary>The five identity fields plus the possessor, straight off a resolved item.
    /// One reader shared by the plain read and by the apply's read-back, so the two can never
    /// disagree about which addresses they are talking about.</summary>
    private static ItemIdentityFields ReadItemFields(nint item) => new()
    {
        StackSize = *(uint*)(item + ItemStackSize),
        Charges = *(uint*)(item + ItemCharges),
        MaxCharges = *(uint*)(item + ItemMaxCharges),
        Upgrades = *(uint*)(item + ItemUpgrades),
        BitFlags = *(uint*)(item + ItemBitFlags),
        Possessor = *(uint*)(item + ItemPossessor),
        HasTag = HasNonEmptyTag(item),
    };

    /// <summary>
    /// True when the item carries a Tag. A CExoString is a char* followed by its length, and
    /// the length routinely INCLUDES the terminating null (three bugs in this project so
    /// far), so a one-byte "length" over an empty buffer still means empty — hence the first
    /// byte is checked too rather than the length alone.
    /// </summary>
    private static bool HasNonEmptyTag(nint item)
    {
        nint text = *(nint*)(item + ItemTag);
        uint length = *(uint*)(item + ItemTag + 4);

        return text != 0 && length != 0 && *(byte*)text != 0;
    }

    /// <summary>
    /// Deletes item <paramref name="itemId"/> from placeable <paramref name="placeable"/>'s
    /// container, if it is there. Confirms membership by scanning the repository's own id list
    /// first, so a miss is a clean no-op that never calls into the engine, and every deref is
    /// guarded. Returns true only when the item was found and RemoveItem was called.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool RemoveContainerItem(nint placeable, uint itemId)
    {
        if (placeable == 0)
        {
            return false;
        }

        nint repo = *(nint*)(placeable + PlaceableRepository);
        if (repo == 0)
        {
            return false;
        }

        int count = *(int*)(repo + RepositoryItemCount);
        nint ids = *(nint*)(repo + RepositoryItemArray);
        if (ids == 0 || count is <= 0 or > 4096)
        {
            return false;
        }

        bool present = false;
        for (int i = 0; i < count; i++)
        {
            if (*(uint*)(ids + (nint)(i * 4)) == itemId)
            {
                present = true;
                break;
            }
        }

        if (!present)
        {
            return false;
        }

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            return false;
        }

        PlaceableRemoveItemFn(placeable, item);
        return true;
    }

    // ---- item identity ---------------------------------------------------------------
    //
    // Everything that is true of ONE item rather than of its blueprint. CreateItemOnFloor
    // takes a template name and nothing else, so an item spawned for a peer arrives stock:
    // one of it, full charges, unidentified, no upgrades. These four fields are what the
    // engine itself carries across a rebuild-from-template.
    //
    // Provenance (docs/research/item-identity.md), all read out of this binary:
    //   CSWSItem::SaveItem       0x0055CCD0 — writes StackSize from the word at +0x28C,
    //                                        Charges/MaxCharges from +0x258/+0x25C,
    //                                        Upgrades from the dword at +0x294, and the
    //                                        Identified/Stolen/NonEquippable GFF fields
    //                                        from bits 0/5/6 of +0x288.
    //   CSWSItem::LoadDataFromGff 0x0055FCD0 — the matching reader, same offsets.
    //   CSWSItem::SetIdentified  0x00554170 — eight instructions that toggle bit 0 of +0x288
    //                                        and nothing else, which is why the bit poke
    //                                        below needs no engine call.
    //   CSWSItem::HasUpgrade     0x004EADA0 — `[this+0x294] & (1 << n)`, so +0x294 is a
    //                                        MASK of installed upgrade slots.
    //   CSWGuiUpgrade            0x006C5620 — the workbench itself: LoadFromTemplate, then
    //                                        SetIdentified(1), then a raw copy of the old
    //                                        item's +0x294 and Stolen bit onto the new body,
    //                                        with no rebuild call afterwards. This IS the
    //                                        sequence below, performed by the engine.
    //
    // Nothing derived caches any of it: ComputeArmorClass (0x00553CC0) reads the BASE ITEM
    // row and GetCost (0x00554000) recomputes from the base item every call, so there is no
    // stale sibling to refresh after the poke — unlike an appearance change, which there is.

    /// <summary>CSWSItem + this = charges (int, byte-ranged).</summary>
    private const int ItemCharges = 0x258;

    /// <summary>CSWSItem + this = max charges (int, byte-ranged).</summary>
    private const int ItemMaxCharges = 0x25C;

    /// <summary>CSWSItem + this = the flag bitfield. Bit 0 identified, 5 stolen, 6 non-equippable.</summary>
    private const int ItemBitFlags = 0x288;

    /// <summary>CSWSItem + this = stack size. A dword whose high half the engine keeps zero.</summary>
    private const int ItemStackSize = 0x28C;

    /// <summary>CSWSItem + this = the installed-upgrade bitmask.</summary>
    private const int ItemUpgrades = 0x294;

    private const uint ItemBitIdentified = 1u << 0;
    private const uint ItemBitStolen = 1u << 5;
    private const uint ItemBitNonEquippable = 1u << 6;

    /// <summary>
    /// Writes one item's per-instance state onto a freshly created body, so a peer's
    /// half-spent stack of twenty arrives as twenty and their upgraded lightsaber arrives
    /// upgraded.
    ///
    /// Field writes only — no engine call, so there is no argument size to get wrong and no
    /// stack to skew. An id that does not resolve to a live CSWSItem (wrong module, item
    /// already destroyed, the pointer reused) fails the vtable check inside
    /// <see cref="ResolveItemBase"/> and this returns false having touched nothing: the item
    /// stays exactly as the engine made it, which is the old behaviour rather than a crash.
    ///
    /// Only ever reached with an identity the agent actually READ, so the values are written
    /// as they came rather than second-guessed — a stock item reads as stock and writing its
    /// own numbers back changes nothing. The single exception is a stack size of 0, which the
    /// engine never produces and which would make the item unpickable; the decoder already
    /// clamps it, and this refuses it again because it is the one field that ends up inside
    /// somebody else's world.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool ApplyItemIdentity(
        uint itemId, uint upgrades, uint stackSize, uint charges, uint maxCharges, uint flags,
        out ItemIdentityFields after)
    {
        after = default;

        nint item = ResolveItemBase(itemId);
        if (item == 0)
        {
            return false;
        }

        *(uint*)(item + ItemUpgrades) = upgrades;

        if (stackSize > 0)
        {
            *(uint*)(item + ItemStackSize) = Math.Min(stackSize, ushort.MaxValue);
        }

        *(uint*)(item + ItemMaxCharges) = maxCharges;
        *(uint*)(item + ItemCharges) = charges;

        uint bits = *(uint*)(item + ItemBitFlags);
        bits = Set(bits, ItemBitIdentified, (flags & SharedBlock.ItemIdentityIdentified) != 0);
        bits = Set(bits, ItemBitStolen, (flags & SharedBlock.ItemIdentityStolen) != 0);
        bits = Set(bits, ItemBitNonEquippable, (flags & SharedBlock.ItemIdentityNonEquippable) != 0);
        *(uint*)(item + ItemBitFlags) = bits;

        // Read back from the SAME addresses that were just written, rather than reporting the
        // values we intended. The distinction is the whole point of the out parameter: an
        // apply that logs its inputs proves only that it was asked; an apply that logs what
        // is now in the item proves the write landed where the offsets say it does — which is
        // the one claim in docs/research/item-identity.md nothing had ever measured.
        after = ReadItemFields(item);

        return true;

        static uint Set(uint bits, uint bit, bool on) => on ? bits | bit : bits & ~bit;
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

    // ---- encounters ----------------------------------------------------------------
    //
    // Everything here is read out of the binary and written up in
    // docs/research/client-encounters.md; the addresses are repeated in each member's
    // comment because that is what makes a wrong one findable.
    //
    // The shape of an encounter, as the engine sees it: a polygon (the "activate area")
    // plus a spawn list. `CSWSArea::InSubAreas 0x004BF470` tests a point against every
    // subarea object in the area — triggers, doors, areas of effect and ENCOUNTERS, the
    // last identified by the vtable slot at +0x5C returning non-null (self_return on
    // 0x00749D98, return_zero on everyone else). `CSWSCreature::HandleSubAreaStateChanges
    // 0x00516020` turns a newly-entered subarea into event 0xC, and
    // `CSWSEncounter::EventHandler 0x00594220` answers that event with exactly two things:
    // store the entering object at +0x2E0, then call SpawnIfAppropriate. That is the whole
    // "an encounter fired" path, and it is what FireEncounter reproduces.
    //
    // ⚠ The subarea machinery only runs from the engine's own movement functions
    // (WalkUpdateLocation, DriveUpdateLocation, DoCombatStep, the jump actions). A phantom
    // is placed by direct SetPosition writes, so it never generates event 0xC on its own —
    // which is precisely why the host has to be asked.

    private const nint EncounterVtable = 0x00749D98;

    /// <summary>CSWSEncounter + this = active (symbol database). 0 = the encounter is off.</summary>
    private const int EncounterActive = 0x238;

    /// <summary>CSWSEncounter + this = the spawn list's count (list at +0x258). AIUpdate
    /// drains it through SpawnCreatureFromSpawnList 0x00591CA0.</summary>
    private const int EncounterSpawnListCount = 0x25C;

    /// <summary>CSWSEncounter + this = started (symbol database). 1 once it has fired, and
    /// the reason a repeated fire request costs nothing: SpawnIfAppropriate's second
    /// instruction pair tests it and returns.</summary>
    private const int EncounterStarted = 0x278;

    /// <summary>CSWSEncounter + this = the object that last entered. EventHandler stores it
    /// here (0x00594253) BEFORE calling SpawnIfAppropriate, and
    /// SpawnCreatureFromSpawnList reads it back (0x00591D0F) to anchor the spawn near
    /// whoever walked in. Undocumented in the symbol database; named from those two uses.</summary>
    private const int EncounterLastEntered = 0x2E0;

    /// <summary>CSWSEncounter + this = "did an eligible object turn up". UpdateActivateAreaList
    /// sets it (0x00590F4E) and SpawnIfAppropriate refuses unless it reads 1. Undocumented.</summary>
    private const int EncounterEligible = 0x28C;

    /// <summary>CSWSEncounter + this = the computed spawn pool. SpawnIfAppropriate requires
    /// it to be &gt; 0.0f after CalculateSpawnPool or nothing is built. Undocumented.</summary>
    private const int EncounterSpawnPool = 0x2D8;

    /// <summary>CSWSEncounter + this = player_only (symbol database). When set,
    /// UpdateActivateAreaList only accepts a creature whose stats say is_pc, or one the
    /// party table claims (0x00590F18..0x00590F4C). 34 of the base game's ~47 encounter
    /// placements set it, including every Taris, Korriban and Star Forge fight — so this
    /// is the gate, not a corner case.</summary>
    private const int EncounterPlayerOnly = 0x318;

    /// <summary>CSWSArea + this = the subarea object-id list (CExoArrayList: data @+0x1A0,
    /// count @+0x1A4). CSWSEncounter::AddToArea 0x00593520 appends every encounter here
    /// (the tail at 0x00593588..0x005935BB), and InSubAreas walks it — so this list is the
    /// complete, engine-maintained set of encounters in the module.</summary>
    private const int AreaSubareaList = 0x1A0;

    private const int AreaSubareaCount = 0x1A4;

    // CSWSEncounter::GetInActivateArea(Vector*) -> int. The engine's own point-in-polygon
    // test over the encounter's geometry (+0x290/+0x294 with the index list at
    // +0x298/+0x29C). Reads x and y only. Pure: it touches no global state, which is why
    // it is safe to call once per encounter per pass.
    private static readonly delegate* unmanaged[Thiscall]<nint, Vec3*, int> GetInActivateAreaFn =
        (delegate* unmanaged[Thiscall]<nint, Vec3*, int>)0x00590090;

    // CSWSEncounter::SpawnIfAppropriate(uint enteringObjectId, float difficultyOverride).
    // Reached from SetActive 0x00593C50, AIUpdate's reset path 0x00594150 and EventHandler
    // 0x00594261 — all three pass -1.0f for the override, so we do too. It does NOT spawn
    // anything itself: it decides, builds the spawn list and sets started, and the server's
    // next AIUpdate creates the creatures.
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, float, void> SpawnIfAppropriateFn =
        (delegate* unmanaged[Thiscall]<nint, uint, float, void>)0x00593BE0;

    /// <summary>The eligibility argument every engine call site passes.</summary>
    private const float EncounterDefaultDifficulty = -1.0f;

    /// <summary>
    /// First 14 bytes of GetInActivateArea: <c>sub esp,0x14</c>, <c>mov eax,[ecx+0x29C]</c>,
    /// <c>push ebx</c>, <c>xor ebx,ebx</c>, <c>cmp eax,ebx</c>. The <c>[ecx+0x29C]</c>
    /// displacement is the geometry index count, so a mismatch here means either the wrong
    /// address or a different build's class layout — exactly the two things worth refusing on.
    /// </summary>
    private static ReadOnlySpan<byte> GetInActivateAreaPrologue =>
        [0x83, 0xEC, 0x14, 0x8B, 0x81, 0x9C, 0x02, 0x00, 0x00, 0x53, 0x33, 0xDB, 0x3B, 0xC3];

    /// <summary>
    /// First 20 bytes of SpawnIfAppropriate: <c>push esi</c>, <c>mov esi,ecx</c>, then the
    /// two early-outs this whole feature leans on — <c>mov eax,[esi+0x238]</c> (active) and
    /// <c>cmp [esi+0x278],1</c> (started). Verifying these bytes verifies the two field
    /// offsets at the same time as the entry point.
    /// </summary>
    private static ReadOnlySpan<byte> SpawnIfAppropriatePrologue =>
    [
        0x56, 0x8B, 0xF1, 0x8B, 0x86, 0x38, 0x02, 0x00, 0x00, 0x85, 0xC0, 0x74, 0x56,
        0x83, 0xBE, 0x78, 0x02, 0x00, 0x00, 0x01,
    ];

    private static int _encounterCallsVerified;   // 0 unknown, 1 ok, -1 refused

    /// <summary>
    /// Verify-bytes-or-refuse, checked once and cached. Nothing in this section calls the
    /// engine until this has passed; a mismatch disables client-triggered encounters for the
    /// run rather than calling into whatever is actually there.
    /// </summary>
    public static bool VerifyEncounterCalls(out string diagnostic)
    {
        if (_encounterCallsVerified != 0)
        {
            diagnostic = _encounterCallsVerified > 0 ? "already verified" : "already refused";
            return _encounterCallsVerified > 0;
        }

        if (!Matches((byte*)0x00590090, GetInActivateAreaPrologue, out int index, out byte saw))
        {
            _encounterCallsVerified = -1;
            diagnostic = $"GetInActivateArea 0x590090 prologue mismatch at +{index}"
                + $" (saw {saw:X2}, expected {GetInActivateAreaPrologue[index]:X2})";
            return false;
        }

        if (!Matches((byte*)0x00593BE0, SpawnIfAppropriatePrologue, out index, out saw))
        {
            _encounterCallsVerified = -1;
            diagnostic = $"SpawnIfAppropriate 0x593BE0 prologue mismatch at +{index}"
                + $" (saw {saw:X2}, expected {SpawnIfAppropriatePrologue[index]:X2})";
            return false;
        }

        _encounterCallsVerified = 1;
        diagnostic = "encounter calls verified at 0x590090 / 0x593BE0";
        return true;
    }

    private static bool Matches(byte* site, ReadOnlySpan<byte> expected, out int index, out byte saw)
    {
        for (int i = 0; i < expected.Length; i++)
        {
            if (site[i] != expected[i])
            {
                index = i;
                saw = site[i];
                return false;
            }
        }

        index = 0;
        saw = 0;
        return true;
    }

    /// <summary>Resolves an encounter by object id, vtable-checked. 0 if absent or not one.</summary>
    public static nint ResolveEncounter(uint objectId)
    {
        nint encounter = ResolveServerObject(objectId);
        return encounter != 0 && *(nint*)encounter == EncounterVtable ? encounter : 0;
    }

    /// <summary>
    /// Copies the object ids of every encounter in the loaded area into
    /// <paramref name="ids"/> and returns how many were written. Reads the area's own
    /// subarea list, so it costs one dereference plus a vtable check per subarea — no
    /// memory sweep, and nothing is missed that the engine itself would test.
    /// </summary>
    public static int ListAreaEncounters(Span<uint> ids)
    {
        nint area = GetAreaPointer();
        if (area == 0)
        {
            return 0;
        }

        nint list = *(nint*)(area + AreaSubareaList);
        int count = *(int*)(area + AreaSubareaCount);
        if (list == 0 || count <= 0 || count > 4096)
        {
            return 0;
        }

        int written = 0;
        for (int i = 0; i < count && written < ids.Length; i++)
        {
            uint id = *(uint*)(list + (i * 4));
            if (id != 0 && id != ObjectInvalid && ResolveEncounter(id) != 0)
            {
                ids[written++] = id;
            }
        }

        return written;
    }

    /// <summary>True when <paramref name="position"/> is inside the encounter's activate
    /// polygon, by the engine's own test. Caller must have passed
    /// <see cref="VerifyEncounterCalls"/>.</summary>
    public static bool IsInsideEncounter(nint encounter, Vec3 position) =>
        GetInActivateAreaFn(encounter, &position) != 0;

    /// <summary>Why a fire request did nothing. Published for the log and the battery.</summary>
    public enum EncounterFireResult
    {
        Started = 0,
        NotAnEncounter = 1,
        NoPhantom = 2,
        NotArmed = 3,
        /// <summary>The engine ran its test and said no — out of the polygon, not hostile to
        /// the encounter's faction, dead, or an empty spawn pool. A legitimate answer.</summary>
        EngineDeclined = 4,
    }

    /// <summary>
    /// Runs the engine's own "should this encounter fire" decision with
    /// <paramref name="enteringObjectId"/> standing in for the player who walked in, and
    /// reports whether it started. MAIN THREAD ONLY.
    ///
    /// This is `CSWSEncounter::EventHandler`'s event-0xC body, reproduced: store the
    /// entering object at +0x2E0 (so the spawn is anchored on that body, exactly as it would
    /// be for a local player), then SpawnIfAppropriate. The engine still decides — it
    /// re-walks the area, checks the body is hostile to the encounter's faction
    /// (GetAIStateReputation == 2), alive, and computes a spawn pool. We supply the body,
    /// not the verdict.
    ///
    /// ⚠ The one thing we do override is <c>player_only</c>, and only for the duration of
    /// the call. The remote player IS a player; the engine cannot tell, because their body
    /// here is a spawned creature with is_pc clear and no party-table membership. Suspending
    /// the flag is far narrower than making a phantom claim to be a PC, which would change
    /// how it is treated by XP, death and saving. It is restored on every path.
    /// </summary>
    public static EncounterFireResult FireEncounter(uint encounterId, uint enteringObjectId)
    {
        nint encounter = ResolveEncounter(encounterId);
        if (encounter == 0)
        {
            return EncounterFireResult.NotAnEncounter;
        }

        // Cheap pre-check purely so the caller can tell "already fired / switched off" apart
        // from "the engine looked and declined". SpawnIfAppropriate tests both itself.
        if (*(int*)(encounter + EncounterActive) == 0 || *(int*)(encounter + EncounterStarted) == 1)
        {
            return EncounterFireResult.NotArmed;
        }

        int playerOnly = *(int*)(encounter + EncounterPlayerOnly);

        *(uint*)(encounter + EncounterLastEntered) = enteringObjectId;
        *(int*)(encounter + EncounterEligible) = 0;

        try
        {
            if (playerOnly != 0)
            {
                *(int*)(encounter + EncounterPlayerOnly) = 0;
            }

            SpawnIfAppropriateFn(encounter, enteringObjectId, EncounterDefaultDifficulty);
        }
        finally
        {
            if (playerOnly != 0)
            {
                *(int*)(encounter + EncounterPlayerOnly) = playerOnly;
            }
        }

        return *(int*)(encounter + EncounterStarted) == 1
            ? EncounterFireResult.Started
            : EncounterFireResult.EngineDeclined;
    }

    /// <summary>How many creatures the encounter still has queued to spawn. Diagnostics only
    /// — the server's own AIUpdate drains this.</summary>
    public static int EncounterPendingSpawns(nint encounter) =>
        *(int*)(encounter + EncounterSpawnListCount);

    /// <summary>The spawn pool the last eligibility pass computed, for the log.</summary>
    public static float EncounterPool(nint encounter) => *(float*)(encounter + EncounterSpawnPool);

    // ---- pause state -----------------------------------------------------------

    /// <summary>
    /// All six auto-pause options, as a mask over CClientOptions + 0x14: end of combat
    /// round, enemy sighted, mine sighted, party killed, action menu, new target selected.
    /// </summary>
    private const uint AutoPauseOptionMask = 0x0001F800;

    /// <summary>
    /// Tutorial windows, the same dword, bit 1.
    ///
    /// ⚠ These are a single-player convenience that stalls a co-op fight. Measured: a player
    /// who walked into a room of hostiles ALONE — their partner elsewhere in the module — got
    /// "The game pauses when you sight an enemy", and that box sat on screen for six minutes
    /// while enemies beat on them. Their character sheathed its weapon, every queued action
    /// was ignored, and the fight simply stopped. The auto-pause options beside it are already
    /// cleared for exactly this reason; the tutorial box is the same problem one layer up,
    /// and it is worse because it needs a click that a player being hit is not free to give.
    ///
    /// Two independent confirmations of the bit: <c>SetTutorialWindowsEnabled 0x005472F0</c>
    /// toggles it, and <c>CGuiInGame::TutorialReasonWillShowWindow 0x0062F420</c> opens with
    /// <c>test byte ptr [eax+0x14], 2</c> and returns "no window" when it is clear — so
    /// clearing it suppresses every tutorial reason, not just this one.
    /// </summary>
    private const uint TutorialWindowMask = 0x00000002;

    private const int ClientOptionsFlags = 0x14;

    /// <summary>
    /// Turns off every auto-pause. Two instances that pause themselves independently
    /// desynchronise on their own, and "party killed" in particular fires constantly once
    /// players start dying.
    ///
    /// This clears the same option bits the in-game options screen writes, rather than
    /// fighting the pause bitmask every frame — all six off is a configuration the game
    /// ships as valid, and manual and GUI pauses keep working.
    ///
    /// <see cref="TutorialWindowMask"/> goes with them: a tutorial box needs a CLICK to
    /// dismiss, which a player mid-fight cannot give, and it stalls their whole combat until
    /// they do.
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

        const uint unwanted = AutoPauseOptionMask | TutorialWindowMask;

        uint* flags = (uint*)(options + ClientOptionsFlags);
        if ((*flags & unwanted) == 0)
        {
            return false;
        }

        *flags &= ~unwanted;
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

    // ---- spectate camera: the engine's own follow handoff -------------------------
    //
    // CSWCModule::SetCameraFollow(clientObjectId, 1) is how the engine itself moves the
    // third-person camera between creatures: ChangeCharacter (0x5F6C27), the
    // ControlObject network handler (0x64CBB8/0x64CC76) and area attach (0x5F5D8B) all
    // funnel through it. It resolves the id via CClientExoApp::GetCreatureByGameObjectID,
    // reuses the existing CAurBehaviorCameraFollow when one is installed, queues into the
    // module's pending fields (+0x314/+0x318) when the module is still loading, and
    // early-outs while the client is in combat MODE (weapons-drawn UI state) — so a
    // caller must treat it as a LEVEL and re-assert, not fire it once and trust it.

    /// <summary>CClientExoApp::GetModule — the CSWCModule the camera belongs to; null
    /// until a client world is up.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetClientModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED530;

    /// <summary>CClientExoApp::GetPlayerCreature — the client-side controlled creature.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetClientPlayerCreatureFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED540;

    /// <summary>CSWCModule::SetCameraFollow — thiscall (uint clientObjectId, int bImmediate).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, void> SetCameraFollowFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, void>)0x0063F9D0;

    /// <summary>
    /// Points the camera at a SERVER object (the id space every co-op channel uses); the
    /// client id is the documented `id | 0x80000000` conversion (see PlayBark's notes on
    /// ServerToClientObjectId, 0x005F2AC0). MAIN THREAD ONLY. Returns false when no
    /// client world is up — and silently does nothing while the client is in combat mode,
    /// which is why callers re-assert.
    /// </summary>
    public static bool SetCameraFollowServerObject(uint serverObjectId)
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint module = GetClientModuleFn(client);
        if (module == 0)
        {
            return false;
        }

        SetCameraFollowFn(module, serverObjectId | 0x80000000, 1);
        return true;
    }

    /// <summary>Hands the camera back to the creature the player controls, through the
    /// same engine call. MAIN THREAD ONLY.</summary>
    public static bool RestoreCameraToControlledCreature()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint module = GetClientModuleFn(client);
        nint controlled = GetClientPlayerCreatureFn(client);
        if (module == 0 || controlled == 0)
        {
            return false;
        }

        // A client creature's +0x04 already holds its CLIENT-space id.
        SetCameraFollowFn(module, *(uint*)(controlled + 0x04), 1);
        return true;
    }

    // ---- control switch: engine-verified reorder, manually applied -----------------
    //
    // Round 70 (2026-08-22, docs/research/control-return.md "Round 70"). Rounds 69 and
    // earlier shipped a raw two-field poke here (member[0]'s id at `party+0x24` plus
    // `internal+0x20`) that moved INPUT AUTHORITY (live-confirmed) but never reordered
    // CSWParty's member array — so a client switched THIS way still had the array as
    // [companion, PC] afterward, exactly the shape `PartyTogetherClampRule` has to defer
    // around, and `ControlReturnController` still never saw a claim. Root cause, traced this
    // round: the controller's OWN detection read `Odyssey.GetPlayerCreaturePointer`
    // (`CServerExoApp::GetPlayerCreature`, 0x004B2820), which disassembles to a chain that
    // resolves a FIXED entry in the SERVER's own connected-players list ("player slot 0",
    // found by walking a list at `CServerExoAppInternal+0x10064` for the node whose value
    // equals 0) and reads THAT PLAYER's own registered PC id (`player+0x28`) — a value
    // `ChangeCharacter`/Tab/`leader` never touch. It always resolves the real PC (or nothing),
    // never a companion, by construction — see <see cref="GetControlledCreaturePointer"/> for
    // the field that actually moves. Fixed below: detection now reads that field instead.
    //
    // ⚠⚠ THE ENGINE'S OWN SWITCH FUNNEL WAS TRIED FIRST AND DID NOT WORK LIVE — NEEDS-LIVE,
    // UNRESOLVED, documented here so nobody re-derives this from scratch.
    // `CClientExoAppInternal::ChangeCharacter` (0x005F6B60) — the function a real Tab press
    // (`ChangeCharacterToNextLivingPartyMember` 0x005EDF80/0x005F7960) and a real portrait
    // click (`CGuiInGame::ChangeCharacter` 0x0062B0F0 → `CClientExoApp::ChangeCharacter`
    // 0x005EDC30) both fall through to — was fully disassembled this round, and its calling
    // convention was CONFIRMED against two real call sites via `xrefs.py 0x5F6B60`:
    // `ChangeCharacterToNextLivingPartyMember` pushes exactly `push 0; push -2` (Tab's own
    // "-2 = swap with member[1]" sentinel) and the portrait path pushes a value computed by
    // `CSWParty::FindMemberIndexById` (0x006349C0, disassembled and confirmed to be exactly
    // the same bounded array-id-search this file already implements as
    // <see cref="FindMemberIndex"/>). Calling it from this process — with the EXACT `-2`
    // sentinel a real Tab press uses, not just a computed index — left CSWParty's member array
    // completely unchanged in every live test this round ran: `internal+0x4B4` (the flag
    // `ChangeCharacter` only sets past its own "was this a no-op" short-circuit,
    // `cmp edi,eax; je` at 0x5F6B91) never moved off 0, meaning `CSWParty::GetControlledCreature`
    // read the identical pointer before and after `SetControlledCreature` ran, for every
    // argument tried. `SetControlledCreature`'s own body (0x00635480) was traced far enough to
    // rule out an index/bounds mistake (the -2 path skips the bounds check entirely and still
    // did nothing), which points at something failing further in — most likely an exception
    // inside `SetControlledCreature`'s own SEH frame, silently unwound before the array write,
    // for a reason this round's tooling (no debugger attach) could not isolate further. A
    // future round with debugger access should pick this up before trying anything else here;
    // until then, the switch below reproduces the SAME end state
    // (client-control-lock.md: "a real engine control switch reorders the member array so the
    // driven creature becomes member[0]") by direct, id-only writes instead.
    //
    // The REPLACEMENT still fixes the actual bug: it performs the array reorder
    // `SetControlledCreature` was supposed to (so `PartyTogetherClampRule`'s deferral has
    // something real to defer FOR, and a subsequent clamp pass sees the true state), then
    // moves the camera through the engine's own, ALREADY-VERIFIED-WORKING
    // <see cref="SetCameraFollowServerObject"/>/<see cref="RestoreCameraToControlledCreature"/>
    // (both wrap the real `CSWCModule::SetCameraFollow`, 0x0063F9D0 — the same call
    // `ChangeCharacter` itself would have made). Every write is id-only — Recipe A
    // (client-control-lock.md): "VERIFIED crash-proof... every consumer resolves it through
    // the null-safe GetCreatureByGameObjectID" — so this is exactly as safe as round 69's
    // shipped primitive, just applied to the WHOLE array instead of only member[0].

    /// <summary>CSWParty (client-side) + this = member[0]'s client-space object id — the SAME
    /// field <c>GetSWPartyFn</c>/<see cref="SwPartyMemberCount"/>'s struct exposes at +0x00,
    /// documented in docs/research/client-control-lock.md's "Recipe A" (id-only, VERIFIED
    /// crash-proof: every consumer resolves it through the null-safe
    /// <c>GetCreatureByGameObjectID</c>).</summary>
    private const int SwPartyMember0ClientId = 0x24;

    /// <summary>Byte distance between successive <c>CSWPartyMemberData</c> entries
    /// (client-control-lock.md, confirmed again this round in <c>SetControlledCreature</c>'s
    /// own <c>imul eax,eax,0x88</c> index arithmetic).</summary>
    private const int SwPartyMemberStride = 0x88;

    /// <summary>The "party npc" flag bit a member's client-space id carries while it names a
    /// companion (round-69 live finding: <c>party.member0.client_object_id</c> read
    /// <c>0x80000127</c> for Trask). Masked off before every id comparison in this file so the
    /// same code compares correctly whether the id in hand carries it or not.</summary>
    private const uint ControlPartyNpcFlag = 0x80000000;

    /// <summary>CSWParty's own member-array capacity: the PC plus the server table's own
    /// two-companion cap (<c>CSWPartyTable::AddMember</c> refuses at a count of 2,
    /// 0x00565633, cited in <c>PartyRosterController</c>) — three <c>0x88</c>-byte blocks,
    /// live-confirmed this round (member[2] reads the empty-slot sentinel <c>0x7F000000</c> on
    /// a one-companion party).</summary>
    private const int SwPartyMemberCapacity = 3;

    /// <summary>Finds <paramref name="wantedId"/> (either flagged or plain — the flag bit is
    /// masked from both sides) in CSWParty's member array and returns its index, or -1. The
    /// SAME bounded search <c>CSWParty::FindMemberIndexById</c> (0x006349C0) performs — the
    /// real portrait-click path's own way of turning an object id into a member index.
    ///
    /// <paramref name="withinLiveCount"/> true scans only the entries CSWParty's OWN count
    /// field currently reports as real. False scans the full <see cref="SwPartyMemberCapacity"/>
    /// instead, ignoring that count — see <see cref="SwitchControlToPartySlot"/>'s remarks for
    /// why a caller ever wants that.</summary>
    private static int FindMemberIndex(nint party, uint wantedId, bool withinLiveCount)
    {
        int count = withinLiveCount ? *(int*)(party + SwPartyMemberCount) : SwPartyMemberCapacity;
        if (count is <= 0 or > SwPartyMemberCapacity)
        {
            if (withinLiveCount)
            {
                return -1;
            }

            count = SwPartyMemberCapacity;
        }

        uint wantedMasked = wantedId & ~ControlPartyNpcFlag;
        if (wantedMasked == 0)
        {
            return -1;
        }

        for (int i = 0; i < count; i++)
        {
            uint id = *(uint*)(party + SwPartyMember0ClientId + (i * SwPartyMemberStride)) & ~ControlPartyNpcFlag;
            if (id == wantedMasked)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Moves <paramref name="targetIndex"/>'s member to the FRONT of CSWParty's array
    /// (member[0]) — the same end state <c>CSWParty::SetControlledCreature</c> was supposed to
    /// produce (client-control-lock.md) — by shifting members <c>0..targetIndex-1</c> up by one
    /// slot and writing the target's id into member[0]. Preserves the relative order of every
    /// other member, matching what <c>SetControlledCreature</c>'s own rotate loop does for the
    /// common case.
    ///
    /// ID-ONLY (Recipe A, client-control-lock.md: "VERIFIED crash-proof... every consumer
    /// resolves it through the null-safe GetCreatureByGameObjectID"). The per-member "extra"
    /// field at <c>+0x80</c> — which the real engine ALSO shifts alongside the id, per this
    /// round's own disassembly of <c>SetControlledCreature</c> — is deliberately left in place:
    /// round 69's own precedent already accepted "member[0]'s id only, cosmetic desync at
    /// worst, never a crash risk" for the SAME reason, and adding a second field write here
    /// would only be trading one round's worth of verification for a cosmetic gain nothing has
    /// asked for yet.
    ///
    /// A no-op when <paramref name="targetIndex"/> is already 0.
    /// </summary>
    private static void RotateControlToFront(nint party, int targetIndex)
    {
        if (targetIndex <= 0)
        {
            return;
        }

        uint targetId = *(uint*)(party + SwPartyMember0ClientId + (targetIndex * SwPartyMemberStride));
        for (int i = targetIndex; i > 0; i--)
        {
            uint* dst = (uint*)(party + SwPartyMember0ClientId + (i * SwPartyMemberStride));
            uint* src = (uint*)(party + SwPartyMember0ClientId + ((i - 1) * SwPartyMemberStride));
            *dst = *src;
        }

        *(uint*)(party + SwPartyMember0ClientId) = targetId;
    }

    /// <summary>Switches the LOCAL player's control to the party-roster companion in
    /// <paramref name="slot"/> (0..8, the same NPC slot index <c>partystate.ps1</c> reports —
    /// Trask is slot 0 in <c>end_m01aa</c>, LIVE-CONFIRMED, not 1): reorders CSWParty's member
    /// array (<see cref="RotateControlToFront"/>) and points the camera at the companion
    /// through the engine's own, already-working <see cref="SetCameraFollowServerObject"/>.
    /// Requires the companion to already be a live MEMBER of CSWParty's array (actually in the
    /// active party, not merely recruited) — there is no other way to name a target. Returns
    /// false, touching nothing, when the slot has no spawned body, is not currently a party
    /// member, or there is no client world.
    ///
    /// ⚠ ROUND 70 LIVE FINDING: on a client with <c>party_together_fix</c> (default ON) this
    /// companion's data is very often ALREADY sitting in CSWParty's array (member[1], say) with
    /// its full flagged id (live-confirmed: <c>0x80000127</c> for Trask) — but CSWParty's own
    /// COUNT field has been clamped down to <c>CSWPartyTable</c>'s companions-only count
    /// (<c>PartyRosterController.SyncClientPartyTogetherState</c>, docs/research/control-return.md
    /// "Round 70"). So the clamp that makes a door crossable also makes the companion
    /// unreachable BY INDEX for a deliberate switch, even though nothing about the array's DATA
    /// is missing. The fix is not to fight the clamp: it is temporarily raised to include
    /// exactly the one entry this call needs (never invented data — the id there is verified
    /// first), and the clamp's own deferral (<see cref="PartyRosterController.IsControllingRosterCompanion"/>
    /// via <see cref="GetControlledCreaturePointer"/>) takes over from the very next pass once
    /// the switch has landed, so nothing re-clamps it back down out from under the player.
    /// MAIN THREAD ONLY.</summary>
    public static bool SwitchControlToPartySlot(int slot, out uint rosterId)
    {
        rosterId = GetPartyRosterObjectId(slot);
        if (rosterId is 0 or ObjectInvalid)
        {
            return false;
        }

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        int index = FindMemberIndex(party, rosterId, withinLiveCount: true);
        if (index < 0)
        {
            // Not reachable within the CURRENT (possibly party_together_fix-clamped) count —
            // but the data may already be sitting past it (round-70 live finding above). Scan
            // the full array before giving up, and if it is there, raise the count to include
            // exactly that one entry. Never lowers it, never invents an id: the entry's own
            // content was already read and matched.
            index = FindMemberIndex(party, rosterId, withinLiveCount: false);
            if (index < 0)
            {
                return false;
            }

            int* countField = (int*)(party + SwPartyMemberCount);
            if (*countField <= index)
            {
                *countField = index + 1;
            }
        }

        RotateControlToFront(party, index);
        SetCameraFollowServerObject(rosterId);
        return true;
    }

    /// <summary>Switches the LOCAL player's control back to their own PC: reorders CSWParty's
    /// member array (<see cref="RotateControlToFront"/>) and points the camera at the PC
    /// through the engine's own, already-working <see cref="RestoreCameraToControlledCreature"/>.
    /// The PC's id is read fresh every call, through <see cref="GetClientPlayerCreatureFn"/>
    /// (<c>CClientExoAppInternal::GetPlayerCreature</c>, 0x5F1420 — <c>internal+0x20</c>,
    /// VERIFIED stable across any switch: a real <c>ChangeCharacter</c> never calls
    /// <c>SetPlayerCreature</c>, client-control-lock.md) — no cross-call cache needed, unlike
    /// round 69's raw poke. Returns false, touching nothing, when there is no client world, no
    /// live party object, or (should not happen — the PC is always a member by construction)
    /// the PC does not currently resolve to a CSWParty member. MAIN THREAD ONLY.</summary>
    public static bool SwitchControlToPc()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return false;
        }

        nint pcCreature = GetClientPlayerCreatureFn(client);
        if (pcCreature == 0)
        {
            return false;
        }

        int index = FindMemberIndex(party, *(uint*)(pcCreature + 0x04), withinLiveCount: true);
        if (index < 0)
        {
            return false;
        }

        RotateControlToFront(party, index);
        RestoreCameraToControlledCreature();
        return true;
    }

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
    /// The creature CLIENT INPUT currently drives — CSWParty member[0]
    /// (client-control-lock.md's control gate: <c>[[[0x7A39FC]+4]+0x270]+0x24</c>), which is
    /// NOT what <see cref="GetPlayerCreaturePointer"/> answers.
    ///
    /// Round 70 (2026-08-22, docs/research/control-return.md "Round 70"): both
    /// <c>ControlReturnController</c> and <c>PartyRosterController.IsControllingRosterCompanion</c>
    /// used to ask <see cref="GetPlayerCreaturePointer"/> "what is the player driving right
    /// now" and it structurally cannot answer that — disassembly this round shows it resolves
    /// a FIXED entry in the server's own connected-players list ("player slot 0") and reads
    /// that player's registered PC id (<c>player+0x28</c>), a value Tab/`leader`/
    /// <c>ChangeCharacter</c> never touch. It always resolves the real PC (or nothing), which
    /// is exactly why neither ever produced a "you are driving companion" claim. This function
    /// reads the field that DOES move — the same one <see cref="SwitchControlToPartySlot"/>/
    /// <see cref="SwitchControlToPc"/> locate a member index against — resolved to a server
    /// object pointer through the SAME safe generic resolver every other id read in this file
    /// uses (<see cref="ResolveServerObject"/>) rather than a typed/flag-sensitive getter.
    /// </summary>
    public static nint GetControlledCreaturePointer()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint party = GetSWPartyFn(client);
        if (party == 0)
        {
            return 0;
        }

        uint id = *(uint*)(party + SwPartyMember0ClientId) & ~ControlPartyNpcFlag;
        return id == 0 ? 0 : ResolveServerObject(id);
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
    ///
    /// ⚠ Roadmap item 14 phase 2 (docs/research/session-save.md) leans on one more fact about
    /// this value: a plain SAVE does not change it. Nothing about writing the current world to
    /// disk calls UnloadModule or replaces the object array — the epoch is a LIVE-WORLD
    /// identity, not a save-file one — so a save-only edge and a genuine module reload (either
    /// an ordinary transition or a Load Game action) are exactly the two cases this function
    /// already distinguishes by construction: unchanged epoch versus a transition through
    /// "false" and back. INFERRED from the disassembly this doc comment already cites
    /// (UnloadModule/GetObjectArray), not measured against a real in-game save — see
    /// WorldLoadRule.cs, which is where PluginHost turns this into the further distinction
    /// between an ordinary transition and an unannounced load.
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

        // ⚠ The mode is a WORD, not a dword: the engine itself uses
        // `cmp word ptr [esi+0x10008], 1` (0x4B6294) and `mov word ptr [...], 2` (0x4B62B0).
        // Reading 32 bits picks up the adjacent field, and whether that happens to be zero
        // depends on how the module was loaded — measured live, a click-loaded world read
        // 0x00000002 while a plugin-loaded one read 0x555FFC02, so the world gate silently
        // failed (and the agent's identical mistake made every module read fall back to
        // "area{id}", which in turn hid peers behind the module filter).
        if (*(int*)(internalServer + LoadingModuleOffset) != 0
            || *(ushort*)(internalServer + ServerModeOffset) != 2)
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

    // ---- main-menu Co-op button --------------------------------------------------
    //
    // The dormant BTN_WARP on the main menu is rebound to start co-op. See
    // docs/research/connect-flow.md. Guarded engine-call code, on by default now that it is
    // live-verified; coop.ini menu_button=0 opts out (read plugin-side, no agent needed).

    /// <summary>CClientExoAppInternal + this = CSWGuiMainMenu*, null outside the menu.</summary>
    private const int ClientInternalMainMenuOffset = 0x280;

    /// <summary>CSWGuiMainMenu + this = the BTN_WARP control pointer (dev-only, blank text).</summary>
    private const int MainMenuWarpButton = 0x1248;

    /// <summary>GUI input events: 0x27 = mouse click, 0x2D = A / Enter.</summary>
    public const int GuiEventClick = 0x27;
    public const int GuiEventAccept = 0x2D;

    // CSWGuiControl::AddEvent(this, int eventId, void* owner, void* fn). Same eventId
    // replaces; a null fn removes. thiscall, ret 0xC.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint, nint, void> AddEventFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint, nint, void>)0x0041AB20;

    /// <summary>The main-menu object, or 0 when not on the menu.</summary>
    public static nint GetMainMenu()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint internalClient = Deref(client + AppClientOffset);
        return internalClient == 0 ? 0 : *(nint*)(internalClient + ClientInternalMainMenuOffset);
    }

    /// <summary>
    /// Binds a callback to the main menu's dormant Co-op (BTN_WARP) control for both the
    /// click and accept events. The callback is a thiscall stub the engine invokes as
    /// <c>fn(owner /*ecx*/, firingControl /*stack*/)</c>. Returns false (harmlessly) if the
    /// menu or the control is not resolvable.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool BindCoopMenuButton(nint owner, nint callback)
    {
        nint menu = GetMainMenu();
        if (menu == 0)
        {
            return false;
        }

        nint button = *(nint*)(menu + MainMenuWarpButton);

        // Sanity-guard the control pointer before calling into it: a plausible heap
        // pointer, 4-aligned, whose vtable slot is also a plausible pointer. A wrong offset
        // would otherwise call AddEvent on garbage.
        if (!LooksLikeHeapPointer(button) || !LooksLikeHeapPointer(*(nint*)button))
        {
            return false;
        }

        AddEventFn(button, GuiEventClick, owner, callback);
        AddEventFn(button, GuiEventAccept, owner, callback);
        return true;
    }

    private static bool LooksLikeHeapPointer(nint p) =>
        p is > 0x10000 and < 0x7FFF0000 && (p & 3) == 0;

    // ---- forcing BTN_WARP visible at runtime -------------------------------------
    //
    // The retail menu constructor builds BTN_WARP, lays it out from the .gui (position + "Warp"
    // text + adds it to the draw list), then DELIBERATELY re-hides it: at 0x0067C669 it clears
    // bit1 (0x2) of control+0x44. That one bit gates BOTH drawing (CSWGuiPanel::Draw 0x0040B841
    // `test [ctl+0x44],2`) and click hit-testing (CSWGuiControl::HitCheckMouse 0x004187C5). So a
    // .gui Override can never surface it — but writing bit1 back each frame makes it draw AND
    // clickable in one store, with no code patch. See docs/research/menu-button-runtime.md.

    /// <summary>CSWGuiControl flags dword: bit1 0x2 = visible+hittable, bit5 0x20 = locked.</summary>
    private const int ControlFlagsOffset = 0x44;
    private const uint ControlFlagVisible = 0x2;
    private const uint ControlFlagLocked = 0x20;

    // The label lives in a CSWGuiText at button+0x154; its CSWGuiTextParams is at +0x18 -> +0x16C.
    private const int ButtonTextParamsOffset = 0x16C;

    // CSWGuiTextParams::SetText(this, CExoString* src) — sets the literal and str_ref = -1.
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, void> TextParamsSetTextFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, void>)0x00415E00;
    // CExoString::CExoString(const char*) / ~CExoString — build+free the temporary label string.
    private static readonly delegate* unmanaged[Thiscall]<byte*, byte*, void> CExoStringCtorFn =
        (delegate* unmanaged[Thiscall]<byte*, byte*, void>)0x005E5A90;
    private static readonly delegate* unmanaged[Thiscall]<byte*, void> CExoStringDtorFn =
        (delegate* unmanaged[Thiscall]<byte*, void>)0x005E5C20;

    /// <summary>CSWGuiButton vtable — identifies the embedded control and guards every write.</summary>
    private const nint ButtonVtable = 0x0073E658;

    /// <summary>
    /// The main-menu Co-op control (the dormant BTN_WARP), or 0 when not on the menu.
    ///
    /// ⚠ BTN_WARP is EMBEDDED in CSWGuiMainMenu, not pointed to: the control object *begins* at
    /// <c>mainMenu + 0x1248</c>, whose first dword is its vtable. Dereferencing that offset (as
    /// this code originally did) yields the vtable CONSTANT 0x0073E658, which passes a naive
    /// heap-pointer guard and sends every subsequent write into the executable's static data —
    /// that, not any "uninitialised control", is what crashed the menu. Verified live: at the
    /// correct address the object has the CSWGuiButton vtable and its real .gui extent, and the
    /// visible-bit write is stable.
    /// </summary>
    public static nint GetCoopMenuButton()
    {
        nint menu = GetMainMenu();
        if (menu == 0)
        {
            return 0;
        }

        // The control is AT this address. Confirm by its vtable rather than a range check, so a
        // layout shift can never let us scribble somewhere arbitrary.
        nint button = menu + MainMenuWarpButton;
        return LooksLikeHeapPointer(button) && *(nint*)button == ButtonVtable ? button : 0;
    }

    /// <summary>Un-hides a control: sets the visible/hittable bit and clears the lock bit. Must
    /// be re-applied each frame because a menu rebuild re-runs the ctor's hide. MAIN THREAD ONLY.</summary>
    public static void SetControlVisible(nint control)
    {
        uint* flags = (uint*)(control + ControlFlagsOffset);
        *flags = (*flags | ControlFlagVisible) & ~ControlFlagLocked;
    }

    /// <summary>Sets a button control's on-screen label via the engine's own SetText (which
    /// refreshes the linked text object and pins str_ref to -1 so the literal wins). Builds a
    /// temporary CExoString on the stack and frees it. Idempotent. MAIN THREAD ONLY.</summary>
    public static void SetControlText(nint control, string ascii)
    {
        byte* s = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            s[i] = 0;
        }

        int n = ascii.Length;
        byte* buf = stackalloc byte[64];
        int count = n < 63 ? n : 63;
        for (int i = 0; i < count; i++)
        {
            buf[i] = (byte)ascii[i];
        }

        buf[count] = 0;

        CExoStringCtorFn(s, buf);
        TextParamsSetTextFn(control + ButtonTextParamsOffset, s);
        CExoStringDtorFn(s);
    }

    /// <summary>Binds the plugin's click/accept sink onto a resolved control (same-id AddEvent
    /// replaces). Separated from <see cref="BindCoopMenuButton"/> so the caller can reuse a
    /// control it already resolved. MAIN THREAD ONLY.</summary>
    public static void BindControlClick(nint control, nint owner, nint callback)
    {
        AddEventFn(control, GuiEventClick, owner, callback);
        AddEventFn(control, GuiEventAccept, owner, callback);
    }

    /// <summary>Binds ONLY the accept (Enter/A) event — used on browser listbox rows, where a
    /// click must stay selection-only (the listbox forwards the click event to the selected
    /// row, so a click binding would connect on single click). MAIN THREAD ONLY.</summary>
    public static void BindControlAccept(nint control, nint owner, nint callback) =>
        AddEventFn(control, GuiEventAccept, owner, callback);

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

    // CSWSWaypoint::RemoveFromArea(this). Half of teardown; the destructor is the other half.
    //
    // ⚠ NO ARGUMENTS. This was declared as (this, area) and called with the area, and the
    // function does not take one: it ends in a plain `ret` (0x005C83A8) and derives the area
    // from `this` via 0x004CB120. A __thiscall callee pops its own arguments, so every call
    // pushed four bytes that nothing ever popped — the stack pointer walked away four bytes
    // at a time, and a skewed stack does not fault where it is caused, it corrupts a frame
    // and surfaces later as a stack-cookie failure (0xC0000409) in whatever returns next.
    // Found by tools/callaudit.py, which compares every one of these declarations against
    // the callee's own `ret` immediate.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> WaypointRemoveFromAreaFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005C8350;

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

    // ---- the minimap ---------------------------------------------------------------
    //
    // CSWGuiMainInterface::DrawMap 0x0068AB10, read end to end, draws the area image, the
    // fog of war and EXACTLY ONE arrow — the local player's. There is no map-note loop and
    // no party loop in it, so the M-screen pin the marker controller maintains was never
    // going to appear there, and no data-only trick can put it there.
    //
    // What CAN: the arrow is a control embedded at panel+0x5F40, drawn through its own
    // vtable inside a viewport the engine has already set up. Drawing it a SECOND time, at
    // an offset, costs no new GUI object and no viewport work. The offset falls out of the
    // engine's own scroll formula: it places the map image at (side/2 - pixel*scale), so a
    // map pixel lands on screen at side/2 + (pixel - localPixel)*scale — i.e. RELATIVE to
    // the arrow, which is what makes this independent of where the viewport actually is.

    /// <summary>CSWGuiMainInterface + this = the embedded player-arrow control.</summary>
    private const int MainInterfaceArrowControl = 0x5F40;

    /// <summary>CSWGuiObject + this = its extent {x, y, w, h}, as ints.</summary>
    private const int GuiObjectExtent = 0x04;

    // The arrow control embeds a CSWGuiImage whose CSWGuiImageParams start 0x68 in, so the
    // params' own layout (resref 0x00, angle 0x10, alpha 0x14, colour 0x18, flags 0x24)
    // lands at the offsets below. Confirmed by a live read of the panel: angle held the
    // rotation DrawMap had just written, and alpha/colour read exactly 1.0 / (1,1,1).

    /// <summary>Arrow control + this = CSWGuiImageParams.angle — the rotation DrawMap writes
    /// each draw (panel+0x5FB8 in its own terms).</summary>
    private const int ArrowRotation = 0x78;

    /// <summary>Arrow control + this = CSWGuiImageParams.alpha.</summary>
    private const int ArrowAlpha = 0x7C;

    /// <summary>Arrow control + this = CSWGuiImageParams.color, three floats multiplied into
    /// the icon. Above 1.0 they saturate a channel rather than clamping the result, which is
    /// what makes a red arrow possible at all out of blue art.</summary>
    private const int ArrowColour = 0x80;

    /// <summary>
    /// One tint per peer slot, so two partners are never the same arrow.
    ///
    /// These are MULTIPLIERS against the shipped blue arrow, not colours — which is why they
    /// look wrong written down and right on screen, and why they are over-bright. Each was
    /// eyeballed on the real icon: (1, 0.15, 0.15) came out maroon, (4, 0.4, 0.4) purple and
    /// (4, 0.3, 0) orange, because the art's blue channel dominates until the opposing
    /// channel is driven to zero and the wanted one past 1.0. All six below were captured and
    /// looked at; more peers than that cycle the list.
    /// </summary>
    private static ReadOnlySpan<float> PeerArrowColours =>
    [
        6.0f, 0.0f, 0.0f,   // red
        0.2f, 4.0f, 0.2f,   // green
        4.0f, 0.3f, 0.0f,   // orange
        5.0f, 0.4f, 3.0f,   // purple
        0.2f, 3.0f, 3.0f,   // cyan
        4.0f, 2.0f, 0.0f,   // yellow
    ];

    /// <summary>CSWGuiObject vtable +0x04 = SetExtent(Extent*) — a plain four-dword copy
    /// (0x00409E90), but called through the slot because the arrow's class may override it.</summary>
    private const int GuiSetExtentSlot = 0x04;

    /// <summary>CSWGuiObject vtable +0x38 = Draw(arg) — the same slot DrawMap itself calls
    /// for the map image and the arrow.</summary>
    private const int GuiDrawSlot = 0x38;

    /// <summary>CSWSModule + this = its CSWSAreaMap.</summary>
    private const int ModuleAreaMapOffset = 0x218;

    /// <summary>CSWSAreaMap + this = map data present; every accessor fails when it is 0.</summary>
    private const int AreaMapHasData = 0x04;

    /// <summary>CSWSAreaMap + this = the pixel-to-screen scale DrawMap multiplies by.</summary>
    private const int AreaMapScale = 0x14;

    /// <summary>CSWSAreaMap + this + slot*4 = a party member's map pixel X (-1 = unset).</summary>
    private const int AreaMapPartyX = 0x28;

    private const int AreaMapPartyY = 0x34;

    /// <summary>
    /// The party slot borrowed to convert one peer's world position into a map pixel.
    ///
    /// The engine's own world→pixel maths lives behind
    /// <see cref="SetPartyMemberWorldLocationFn"/>, applies the area's orientation mode, and
    /// is the only way to be certain a marker lands in the same space as the arrow. Slot 2
    /// is the last of three; its previous contents are saved and put back before this
    /// returns, so nothing observes the borrow — not even a party of three, whose slot 2
    /// <c>UpdateMapData</c> rewrites every frame anyway.
    /// </summary>
    private const int AreaMapScratchSlot = 2;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int*, int*, int> GetPartyMemberMapLocationFn =
        (delegate* unmanaged[Thiscall]<nint, int, int*, int*, int>)0x005791B0;

    /// <summary>CSWSAreaMap::SetPartyMemberWorldLocation(slot, Vector) — <c>ret 0x10</c>, so
    /// the vector is three separate stack dwords, not a pointer.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, float, float, float, void> SetPartyMemberWorldLocationFn =
        (delegate* unmanaged[Thiscall]<nint, int, float, float, float, void>)0x005790C0;

    /// <summary>CSWSAreaMap::GetMapRotateCCWFromWorldOrientation(Vector) — <c>ret 0xC</c>,
    /// returns the arrow angle for a facing.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, float, float, float> GetMapRotateFn =
        (delegate* unmanaged[Thiscall]<nint, float, float, float, float>)0x00578ED0;

    /// <summary>The server module's area map, or 0 when there is no world.</summary>
    private static nint GetAreaMap()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint module = GetModuleFn(server);
        if (module == 0)
        {
            return 0;
        }

        nint areaMap = Deref(module + ModuleAreaMapOffset);
        return LooksLikeHeapPointer(areaMap) && *(int*)(areaMap + AreaMapHasData) != 0 ? areaMap : 0;
    }

    /// <summary>
    /// Draws one extra arrow on the minimap per peer, from inside the engine's own open
    /// viewport (see <see cref="MinimapPeerHook"/>). Everything it touches is saved and put
    /// back before it returns: the arrow's extent, the arrow's rotation and the borrowed
    /// party slot. A peer outside the map is clipped by the viewport the engine already set
    /// up, which is the behaviour you want — an off-map partner simply is not on your map.
    ///
    /// MAIN THREAD, INSIDE THE RENDER PATH. No allocation, no logging, no engine object
    /// construction: this runs every frame the minimap is visible.
    /// </summary>
    public static void DrawMinimapPeers(nint mainInterface, nint drawArg, SharedBlockView block)
    {
        nint areaMap = GetAreaMap();
        if (mainInterface == 0 || areaMap == 0)
        {
            return;
        }

        int localX;
        int localY;
        if (GetPartyMemberMapLocationFn(areaMap, 0, &localX, &localY) == 0)
        {
            return; // the player's own pixel is unset; there is nothing to be relative to
        }

        nint arrow = mainInterface + MainInterfaceArrowControl;
        nint vtable = *(nint*)arrow;
        if (!LooksLikeCodePointer(vtable))
        {
            return;
        }

        var setExtent = (delegate* unmanaged[Thiscall]<nint, int*, void>)(*(nint*)(vtable + GuiSetExtentSlot));
        var draw = (delegate* unmanaged[Thiscall]<nint, nint, void>)(*(nint*)(vtable + GuiDrawSlot));

        // The vtable itself was checked; the two slots taken out of it were not. A garbage
        // slot here is a call to an arbitrary address from the render path.
        if (!LooksLikeCodePointer((nint)setExtent) || !LooksLikeCodePointer((nint)draw))
        {
            return;
        }

        float scale = *(float*)(areaMap + AreaMapScale);
        if (!float.IsFinite(scale))
        {
            return;
        }

        // A scratch copy, never the control's own field: SetExtent copies FROM the pointer
        // INTO this+0x04, so handing it that same address would be a self-copy — harmless
        // today and a trap the day the arrow's class overrides the slot.
        int* extentField = (int*)(arrow + GuiObjectExtent);
        int* extent = stackalloc int[4];
        for (int e = 0; e < 4; e++)
        {
            extent[e] = extentField[e];
        }

        int savedX = extent[0];
        int savedY = extent[1];
        float savedRotation = *(float*)(arrow + ArrowRotation);
        float savedAlpha = *(float*)(arrow + ArrowAlpha);
        float* colour = (float*)(arrow + ArrowColour);
        float savedR = colour[0];
        float savedG = colour[1];
        float savedB = colour[2];

        int* scratchX = (int*)(areaMap + AreaMapPartyX + (AreaMapScratchSlot * 4));
        int* scratchY = (int*)(areaMap + AreaMapPartyY + (AreaMapScratchSlot * 4));
        int scratchSavedX = *scratchX;
        int scratchSavedY = *scratchY;

        bool drewAny = false;
        int wanted = (int)Math.Min(block.RemoteCount, (uint)SharedBlock.SlotCapacity);

        for (int i = 0; i < wanted; i++)
        {
            RemoteView remote = block.GetRemote(i);
            if (!remote.Active)
            {
                continue;
            }

            // ⚠ Never hand the engine a position we have not looked at. This runs on the
            // RENDER PATH against a slot an entirely separate process writes, and a slot can
            // legitimately be read mid-write: a peer that has connected but not yet published
            // is all zeroes, and a torn write is arbitrary bits reinterpreted as floats. The
            // phantom spawner has always screened positions this way (IsPlausiblePosition);
            // this path was published without the same screen, and it is the one path where
            // the consequence lands inside the renderer.
            if (!float.IsFinite(remote.Position.X)
                || !float.IsFinite(remote.Position.Y)
                || !float.IsFinite(remote.Position.Z)
                || !float.IsFinite(remote.Facing))
            {
                continue;
            }

            // -1 first: SetPartyMemberWorldLocation only STORES on a successful conversion,
            // so without this an off-map peer would silently reuse the previous peer's pixel.
            *scratchX = -1;
            SetPartyMemberWorldLocationFn(
                areaMap, AreaMapScratchSlot, remote.Position.X, remote.Position.Y, remote.Position.Z);

            int peerX;
            int peerY;
            if (GetPartyMemberMapLocationFn(areaMap, AreaMapScratchSlot, &peerX, &peerY) == 0)
            {
                continue; // off the map image entirely
            }

            // Clamped before the cast. (int) on a float larger than int.MaxValue is not a
            // saturating conversion in IL — it is undefined, and on x86 it yields
            // 0x80000000, which is then handed to the engine as an extent.
            int wantX = savedX + (int)Math.Clamp(MathF.Round((peerX - localX) * scale), -8192f, 8192f);
            int wantY = savedY + (int)Math.Clamp(MathF.Round((peerY - localY) * scale), -8192f, 8192f);

            // ⚠ HELD AT THE EDGE, NOT CLIPPED AWAY.
            //
            // The minimap viewport is about 120 px and the map draws at roughly 2.8 px per
            // world unit, so it shows a radius of only ~21 units — the room you are standing
            // in. Letting the engine clip a peer outside that means your partner is missing
            // from the minimap for almost all of normal play, which is the one question the
            // feature exists to answer. Reported from play as "it doesn't show the other
            // person at all", and visible in the very first separated screenshot: at 18 units
            // the marker was already half off the edge.
            //
            // So an off-map peer is pinned to the border in their direction. You lose the
            // exact distance and keep the bearing, which is the useful half; the marker is
            // also faded, so "over there somewhere" never reads as "right there".
            bool offMap = ClampToViewport(mainInterface, extent[2], extent[3], ref wantX, ref wantY);

            extent[0] = wantX;
            extent[1] = wantY;
            *(float*)(arrow + ArrowRotation) =
                GetMapRotateFn(areaMap, MathF.Cos(remote.Facing), MathF.Sin(remote.Facing), 0f);

            int tint = (i % (PeerArrowColours.Length / 3)) * 3;
            colour[0] = PeerArrowColours[tint];
            colour[1] = PeerArrowColours[tint + 1];
            colour[2] = PeerArrowColours[tint + 2];
            *(float*)(arrow + ArrowAlpha) = offMap ? OffMapAlpha : savedAlpha;

            setExtent(arrow, extent);
            draw(arrow, drawArg);
            drewAny = true;
        }

        *scratchX = scratchSavedX;
        *scratchY = scratchSavedY;

        if (drewAny)
        {
            extent[0] = savedX;
            extent[1] = savedY;
            *(float*)(arrow + ArrowRotation) = savedRotation;
            *(float*)(arrow + ArrowAlpha) = savedAlpha;
            colour[0] = savedR;
            colour[1] = savedG;
            colour[2] = savedB;
            setExtent(arrow, extent);

            // And once more in its own colours, so the player is on TOP. These draws land
            // after the engine's, so a partner standing where you are stands on your arrow
            // and hides it — which is the one place you always want to be able to find
            // yourself. One extra textured quad, only when a peer was actually drawn.
            draw(arrow, drawArg);
        }
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

        // The area is still checked — it is how we know a module is loaded at all — but it is
        // NOT passed: the function reads it off the waypoint itself.
        if (GetAreaPointer() != 0)
        {
            WaypointRemoveFromAreaFn(waypoint);
        }

        nint vtable = *(nint*)waypoint;
        var destructor = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)vtable);
        destructor(waypoint, 1);
    }

    // ---- developer console -------------------------------------------------------

    // The retail build still links the whole Aurora developer console. These are its live
    // globals (see docs/research/console-overlay.md): the open flag, the command-line buffer
    // it edits as the player types, and the caret index into that buffer.
    private const nint ConsoleOpenFlagPtr = 0x007A3A40;
    private const nint ConsoleBufferPtr = 0x007A3A4C;
    private const nint ConsoleCaretPtr = 0x007A3A44;

    /// <summary>Capacity of the engine's console command buffer (null-terminated, cap 500).</summary>
    public const int ConsoleBufferCapacity = 0x1F4;

    // Global::AurPostString(str, x, line, life) — cdecl, caller-cleaned; ENQUEUES a string
    // node (new[0x434]) onto the render list and returns it. It never touches the texture font
    // or GL, so it is safe to call from the SwapBuffers hook when the font is unbound — unlike
    // the immediate PrintStringScreenPos, which faults there. UpdateScreen flushes the queue
    // every frame (AurRenderStrings, ungated by the console flag), so a posted line renders on
    // the next frame whether or not the console is open. See docs/research/text-render-queued.md.
    private static readonly delegate* unmanaged[Cdecl]<byte*, int, int, float, nint> AurPostStringFn =
        (delegate* unmanaged[Cdecl]<byte*, int, int, float, nint>)0x0044D490;

    /// <summary>True while the native developer console is open (grave/tilde toggled it).</summary>
    public static bool IsConsoleOpen => *(int*)ConsoleOpenFlagPtr != 0;

    /// <summary>Pointer to the engine's live console command-line buffer.</summary>
    public static byte* ConsoleBuffer => (byte*)ConsoleBufferPtr;

    /// <summary>
    /// Empties the engine's console command line. Used after a co-op command is captured, so
    /// the engine does not also try to run it as an (unknown) cheat verb on Enter.
    /// </summary>
    public static void ClearConsoleBuffer()
    {
        *(byte*)ConsoleBufferPtr = 0;
        *(int*)ConsoleCaretPtr = 0;
    }

    /// <summary>Length of the null-terminated console buffer, clamped to its capacity.</summary>
    public static int ConsoleBufferLength()
    {
        byte* p = (byte*)ConsoleBufferPtr;
        int i = 0;
        while (i < ConsoleBufferCapacity && p[i] != 0)
        {
            i++;
        }

        return i;
    }

    /// <summary>
    /// Reads the console command line as a managed string (ASCII, cut at the first null —
    /// this is a raw char buffer, not a CExoString, so there is no length-includes-null trap
    /// here; the null itself is the only terminator). Allocates, so call it on key edges and
    /// buffer changes rather than every frame. MAIN THREAD ONLY.
    /// </summary>
    public static string ReadConsoleLine()
    {
        byte* p = (byte*)ConsoleBufferPtr;
        int n = ConsoleBufferLength();
        if (n == 0)
        {
            return string.Empty;
        }

        Span<char> chars = n <= 256 ? stackalloc char[256] : new char[n];
        for (int i = 0; i < n; i++)
        {
            chars[i] = (char)p[i];
        }

        return new string(chars[..n]);
    }

    /// <summary>
    /// Replaces the console command line and parks the caret at its end — the same two
    /// stores <see cref="OpenChatInput"/> seeds, which is all the engine needs: it owns the
    /// buffer and keeps editing from the caret as the player types. Non-ASCII characters
    /// become '?' (the engine's WM_CHAR path only ever produces ASCII here anyway).
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void SetConsoleLine(string text)
    {
        byte* buffer = (byte*)ConsoleBufferPtr;
        int count = Math.Min(text.Length, ConsoleBufferCapacity - 1);
        for (int i = 0; i < count; i++)
        {
            char c = text[i];
            buffer[i] = c is >= (char)0x20 and <= (char)0x7E ? (byte)c : (byte)'?';
        }

        buffer[count] = 0;
        *(int*)ConsoleCaretPtr = count;
    }

    // The queued render's text color: the string node captures its RGBA at enqueue time from
    // these .data floats (NOT the 0x00798BB4 set the immediate path used). The A-channel pair
    // is 0x0078E384/0x0078E374; writing them before a post fades that line. Restore to 1.0 so
    // the game's own subtitles are not tinted. See docs/research/text-render-queued.md.
    private const nint QueuedTextAlphaA = 0x0078E384;
    private const nint QueuedTextAlphaB = 0x0078E374;

    /// <summary>Sets the alpha (0..1) for subsequently posted lines. MAIN THREAD ONLY.</summary>
    public static void SetQueuedTextAlpha(float a)
    {
        *(float*)QueuedTextAlphaA = a;
        *(float*)QueuedTextAlphaB = a;
    }

    /// <summary>Restores opaque, so the game's own queued text is not tinted.</summary>
    public static void ResetQueuedTextAlpha() => SetQueuedTextAlpha(1f);

    /// <summary>
    /// Posts one null-terminated ASCII line to the engine's render queue at a text grid cell
    /// (<paramref name="column"/>, <paramref name="row"/>), living <paramref name="life"/>
    /// seconds (~10 frames per unit). Re-post every frame to keep it up; the ctor de-dups a
    /// line at the same cell and the flush frees expired nodes, so re-posting does not leak.
    /// Enqueue only — never touches the font — so it is safe from the SwapBuffers hook even
    /// when the font is unbound. MAIN THREAD ONLY.
    /// </summary>
    public static void PostTextLine(byte* nulTerminated, int column, int row, float life)
    {
        if (nulTerminated != null && *nulTerminated != 0)
        {
            AurPostStringFn(nulTerminated, column, row, life);
        }
    }

    // Suppress the console's "function <x> not found" for our co-op verbs. Registering them in
    // the command table did not take (the lookup at 0x0044BB90 never matched our entries), so
    // instead blank the format string the not-found path prints: at 0x0044C4B5 the dispatcher
    // does sprintf(buf, 0x0074145C /*"function %s not found"*/, verb). Making that string empty
    // makes the message empty — nothing shown — while our buffer capture still routes the
    // command. It also silences a real typo's message, which is an acceptable trade for a
    // co-op console. One-byte patch to read-only .rdata, so unprotect around it.
    private const nint NotFoundFormatString = 0x0074145C;

    /// <summary>Blanks the "function %s not found" message. Idempotent. MAIN THREAD ONLY.</summary>
    public static void SuppressConsoleNotFound()
    {
        if (*(byte*)NotFoundFormatString == 0)
        {
            return; // already blanked
        }

        if (NativeMethods.VirtualProtect(NotFoundFormatString, (nuint)1, NativeMethods.PageReadWrite, out uint old))
        {
            *(byte*)NotFoundFormatString = 0;
            NativeMethods.VirtualProtect(NotFoundFormatString, (nuint)1, old, out _);
        }
    }

    // ---- force-open the console as a chat prompt ----------------------------------

    // Opening the console programmatically means replaying the engine's own open sequence so
    // its WM_CHAR handler will route typed characters into the command buffer. The dispatch at
    // HandleWMCharMessage 0x00402240 -> DoConsole 0x00401F00 gates on two flags — the
    // CExoInput keyboard-mode flag and the console-open flag — and (verified) has NO EnableCheats
    // gate, so a normal build can be driven into text-entry this way. The engine then translates
    // keystrokes via Windows, renders the "> " input line for free, and Enter/Esc auto-release
    // movement because the modal state at 0x007A3CB4 is set. See docs/research/chat-input-scroll.md.
    private const nint InputSingletonPtr = 0x007A39E4;   // -> CExoInput*
    private const nint KeyboardModalStatePtr = 0x007A3CB4;

    // CExoInput::KeyboardModeOn / ::ClearEvents — __thiscall, no args, on the input singleton.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> KeyboardModeOnFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005DF520;
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ClearEventsFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005DF4E0;

    // TurnOffKeyboardModal — __thiscall, no args, this = *(0x007A39FC) + 4 (a sub-object of the
    // CAppManager). Releasing the modal keyboard grab is what makes movement keys resume after
    // the line is submitted; guarded because the manager pointer can be null before the game loads.
    private static readonly delegate* unmanaged[Thiscall]<nint, void> TurnOffKeyboardModalFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005EDA20;

    /// <summary>
    /// Force-opens the developer console pre-filled with <c>say&#160;</c> so a player can type a
    /// chat line without knowing about the console. Replays the engine's own open sequence
    /// (verified against DoConsole 0x00401F00: latch → KeyboardModeOn → ClearEvents →
    /// TurnOffKeyboardModal → open flag), then seeds the buffer/caret. The FuncInterp hook routes
    /// the finished <c>say</c> line. Idempotent: bails if the console is already open so a real
    /// grave keypress isn't double-toggled. MAIN THREAD ONLY (same thread the engine runs this on).
    /// See docs/research/console-open-safe.md.
    /// </summary>
    public static void OpenChatInput() => OpenConsoleWith("say ");

    /// <summary>
    /// The general form of <see cref="OpenChatInput"/>: force-opens the developer console with
    /// <paramref name="prefill"/> already typed and the caret after it, so the player only has
    /// to supply the argument. <c>"say "</c> makes it a chat box; <c>"password "</c> makes it a
    /// password prompt for a locked server.
    ///
    /// Returns false when the console was already open — a real grave keypress must never be
    /// double-toggled, and the caller has to know its prompt did not take (so it can retry on a
    /// later frame rather than silently waiting for an answer nobody was asked for).
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool OpenConsoleWith(string prefill)
    {
        if (*(int*)ConsoleOpenFlagPtr != 0)
        {
            return false; // already open — do not re-toggle
        }

        // Latch first, matching the engine order.
        *(int*)KeyboardModalStatePtr = 1;

        nint input = Deref(InputSingletonPtr);
        if (input != 0)
        {
            KeyboardModeOnFn(input);
            ClearEventsFn(input);
        }

        // Release the modal keyboard grab on the CLIENT object. The crash before was passing
        // this = manager+4 (the address of the client field); the engine passes *(manager+4)
        // (the client pointer). The thunk derefs [this+4] internally, so a wrong `this` here
        // wrote through the server object and faulted. Deref it correctly.
        nint manager = Deref(AppManagerPtr);
        if (manager != 0)
        {
            nint client = Deref(manager + 4);
            if (client != 0)
            {
                TurnOffKeyboardModalFn(client);
            }
        }

        // Seed the verb and place the caret after it, so whatever the player types becomes its
        // argument and Enter routes the finished line through the FuncInterp detour.
        byte* buffer = (byte*)ConsoleBufferPtr;
        int written = Math.Min(prefill.Length, ConsoleBufferCapacity - 1);
        for (int i = 0; i < written; i++)
        {
            char c = prefill[i];
            buffer[i] = (byte)(c is < (char)0x20 or > (char)0x7E ? ' ' : c);
        }

        buffer[written] = 0;
        *(int*)ConsoleCaretPtr = written;
        *(int*)ConsoleOpenFlagPtr = 1;
        return true;
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

    /// <summary>
    /// CSWPartyTable + this = a POINTER to the CSWSJournal — not the journal itself.
    ///
    /// ⚠ This cost a live crash (2026-08-06). The plugin passed `partyTable + 0x11C`, the
    /// ADDRESS OF the pointer, so `CSWSJournal::SetState` walked whatever followed it in
    /// the party table as an entry array and faulted dereferencing a "tag" of 0xAE37C3BE.
    /// The engine's own accessor settles it: `CSWPartyTable::GetPartyJournal 0x005633C0`
    /// is `mov eax,[esi+0x11C]` (VERIFIED), reached from `CSWSCreature::GetJournal
    /// 0x004EDB00` -> `CServerExoApp::GetPartyTable 0x004AEE70` (`[server+4] + 0x1B770`,
    /// which confirms the party-table base too).
    ///
    /// Verified live after the fix: `*(partyTable + 0x11C)` -> journal whose array list at
    /// +0 reads { data, size 1, capacity 16 } with entry 0 tagged 'end_attack' state 20.
    /// </summary>
    private const int PartyTableJournal = 0x11C;

    // CSWSJournal::SetState(CExoString* tag, int state, int bOverrideHigher). Walks the
    // entry list comparing tags case-insensitively, updates or appends, and sends the
    // journal-updated message so the GUI refreshes — which is exactly why applying a
    // peer's quest state must go through this call and not a raw entry write.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, int, int, void> JournalSetStateFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, int, int, void>)0x005C5A40;

    /// <summary>
    /// Sets a quest journal entry to the given state through the engine's own path. The
    /// CALLER never has to say which direction: this walks the journal's own entry list (it
    /// already has to, to find the tag) and decides how to reach the target via <see
    /// cref="JournalApplyRule"/>.
    ///
    /// ⚠ PROTOCOL 31 / live pass 2026-08-22 (docs/research/quest-sync.md): this used to pass
    /// bOverrideHigher=1 to <c>CSWSJournal::SetState</c> itself for a genuine decrease. Live
    /// testing found that call reports success and logs the lower number, but a raw probe of
    /// the journal array's own <c>+0x20</c> state field showed it never actually moved —
    /// twice, on two independent tags. Disassembly shows the entry-array write inside
    /// <c>SetState</c> is not conditioned on bOverrideHigher at all (see <see
    /// cref="JournalApplyRule.RemoveThenAdd"/>'s remarks for the exact instructions read);
    /// the argument only suppresses a notification send. Exactly which branch inside
    /// <c>SetState</c> refuses the write for a decrease was not pinned down to one
    /// instruction — the write is gated behind a per-player loop whose own comparison was not
    /// fully decompiled. So a decrease is no longer attempted through SetState's override bit
    /// at all: it composes two ALREADY-LIVE-VERIFIED primitives instead — <see
    /// cref="RemoveJournalEntry"/>'s <c>CSWSJournal::Destroy</c> (confirmed live 2026-08-22 to
    /// genuinely shrink the array), followed by a plain add (override=0), which is the same
    /// path ordinary quest advancement has always used.
    ///
    /// <paramref name="allowDecreaseViaRemove"/> mirrors <c>JournalController</c>'s
    /// <c>journal_remove_sync</c> gate: a decrease fundamentally needs the removal primitive,
    /// so when that flag is off, a decrease is refused (failure 7) rather than silently
    /// reusing the disproven override path — the SESSION side still tracks the value either
    /// way (see <c>JournalStore</c>).
    ///
    /// ⚠ SetState is NEVER called when the journal already has the tag at EXACTLY the target
    /// (<paramref name="alreadyCurrent"/> comes back true instead) — this used to be "at or
    /// past" the target, which was correct only because a lower target used to be impossible
    /// to ask for. This is not an optimisation: SetState sends the server→player journal
    /// message — the "Journal Entry Added" feedback box — even when nothing changed.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetJournalState(
        byte* tagAscii, int tagLength, int state, bool allowDecreaseViaRemove,
        out int failure, out bool alreadyCurrent)
    {
        failure = 0;
        alreadyCurrent = false;

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

        // Dereference: +0x11C HOLDS the journal pointer (see PartyTableJournal).
        nint journal = Deref(internalServer + PartyTableOffset + PartyTableJournal);
        if (journal == 0)
        {
            failure = 6;
            return false;
        }

        // ⚠ VALIDATE THE LIST BEFORE HANDING IT TO THE ENGINE. This call crashed live
        // (2026-08-06): CSWSJournal::SetState walks every existing entry comparing tags,
        // and it faulted inside CExoString::CompareNoCase_2 at 0x005E6491 — the strlen
        // over an ENTRY's own tag pointer, reached from the compare at 0x005C5AFF. That is
        // the signature of a data pointer that is readable but not a journal array: the
        // walk then dereferences a garbage tag. SetState has no validation of its own, so
        // the guard has to live here.
        //
        // { SJournalEntry* data; int size; int capacity }, entries 0x3C bytes (the caller's
        // own stride at 0x005C5B11), tag CExoString at +0x18 (its `lea ecx,[eax+edx+0x18]`).
        if (!NativeMethods.IsReadable(journal))
        {
            failure = 4;
            return false;
        }

        nint data = *(nint*)journal;
        int size = *(int*)(journal + 4);
        int capacity = *(int*)(journal + 8);

        if (size < 0 || capacity < size || capacity > 4096
            || (size > 0 && (data == 0 || !NativeMethods.IsReadable(data))))
        {
            failure = 4;
            return false;
        }

        // Every entry the engine will walk must have a readable tag, or the walk faults
        // before it ever reaches our string. Cheap: journals hold tens of entries. The same
        // walk also answers "does the journal already have this tag, and at what state?" —
        // <see cref="JournalApplyRule.Decide"/>'s own inputs.
        ReadOnlySpan<byte> requestTag = new(tagAscii, tagLength);

        for (int i = 0; i < size; i++)
        {
            nint entry = data + (i * JournalEntryStride);
            nint entryTag = *(nint*)(entry + JournalEntryTag);
            if (entryTag != 0 && !NativeMethods.IsReadable(entryTag))
            {
                failure = 5;
                return false;
            }

            if (entryTag == 0)
            {
                continue;
            }

            // CExoString { char* @0, len @4 }; the length INCLUDES the null (the length
            // trap), and EqualsNoCase cuts at the first null, so a sane length bound is
            // all this needs. A garbage length is clamped: tags are resrefs, <= 32 chars.
            uint entryTagLength = *(uint*)(entry + JournalEntryTag + 4);
            int bounded = (int)Math.Min(entryTagLength, 64);

            if (bounded <= 0
                || !JournalTags.EqualsNoCase(new ReadOnlySpan<byte>((void*)entryTag, bounded), requestTag))
            {
                continue;
            }

            int currentState = *(int*)(entry + JournalEntryState);
            JournalApplyAction action = JournalApplyRule.Decide(hasExisting: true, currentState, state);

            if (action == JournalApplyAction.Skip)
            {
                alreadyCurrent = true;
                return true;
            }

            // Length is capacity (strlen + 1), the engine's own CExoString convention.
            ExoString foundTag = new() { CString = (nint)tagAscii, Length = (uint)(tagLength + 1) };

            if (action == JournalApplyAction.RemoveThenAdd)
            {
                if (!allowDecreaseViaRemove)
                {
                    failure = 7;
                    return false;
                }

                // The disproven direct path (see the method remarks): a genuine decrease is
                // realised as remove-then-add, composing two primitives that ARE each
                // live-verified, instead of SetState's own bOverrideHigher=1 argument, which
                // reports success without visibly moving the array's own state field.
                JournalDestroyEntryFn(journal, &foundTag);
                JournalSetStateFn(journal, &foundTag, state, 0);
                return true;
            }

            // A plain advance on an existing entry — the path ordinary quest progression has
            // always used; not in question.
            JournalSetStateFn(journal, &foundTag, state, 0);
            return true;
        }

        // Tag not found at all: a fresh entry, always a plain advance from nothing
        // (JournalApplyRule.Decide(hasExisting: false, ...) is always Add).
        ExoString newTag = new() { CString = (nint)tagAscii, Length = (uint)(tagLength + 1) };
        JournalSetStateFn(journal, &newTag, state, 0);
        return true;
    }

    /// <summary>SJournalEntry stride, from SetState's own walk (add ecx,0x3C).</summary>
    private const int JournalEntryStride = 0x3C;

    /// <summary>SJournalEntry + this = the tag CExoString (lea ecx,[eax+edx+0x18]).</summary>
    private const int JournalEntryTag = 0x18;

    /// <summary>SJournalEntry + this = the quest state. VERIFIED twice over: SetState
    /// itself compares and stores its state argument at [entry+0x20] (the cmp/mov pair at
    /// 0x5C5E23–0x5C5E30), and the agent's read path walked the same layout live —
    /// 'end_attack' state 20. Read-only here.</summary>
    private const int JournalEntryState = 0x20;

    // CSWSJournal + this, CExoString* tag (thiscall, one stack arg — VERIFIED against the
    // binary's own `ret 8` at 0x5C68CA, param_size_bytes=4 in the symbol DB). NOT verified
    // live — see RemoveJournalEntry's remarks for how this was found and why it ships
    // behind an opt-in flag instead of a live-tested offset.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, void> JournalDestroyEntryFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, void>)0x005C68B0;

    /// <summary>
    /// Takes a quest entry out of the journal entirely — KOTOR's own
    /// <c>RemoveJournalQuestEntry</c>, reached here the way <see cref="SetJournalState"/>
    /// reaches <c>AddJournalQuestEntry</c>.
    ///
    /// ⚠ NEEDS-LIVE, UNVERIFIED, OFF BY DEFAULT (see <c>JournalController</c>'s
    /// <c>journal_remove_sync</c> gate). The call site is <c>CSWSJournal::Destroy</c> at
    /// 0x005C68B0 — found by static disassembly of
    /// <c>ExecuteCommandRemoveJournalQuestEntry</c> (0x0054AF60, the VM wrapper the symbol DB
    /// names for NWScript routine 368; see docs/research/quest-sync.md's enumeration for how
    /// the routine number was computed), not by any live probe. The disassembly is
    /// self-consistent (it walks the server's player list exactly the way
    /// <see cref="SetJournalState"/>'s own caller chain does, fetches each player's journal
    /// via <c>CSWSCreature::GetJournal</c> — the same 0x004EDB00 already VERIFIED elsewhere
    /// in this file — and calls a function whose own `ret 8` matches a one-argument
    /// thiscall, immediately after pushing the address of a local CExoString tag buffer) but
    /// the engine's own live crash history around this exact journal pointer chain
    /// (2026-08-06, see <see cref="SetJournalState"/>'s remarks) is reason enough not to
    /// ship an unverified call unguarded. The same array-walk validation
    /// <see cref="SetJournalState"/> uses is applied here for the same reason: a readable
    /// but non-journal pointer must fail closed, not fault the process.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool RemoveJournalEntry(byte* tagAscii, int tagLength, out int failure)
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

        nint journal = Deref(internalServer + PartyTableOffset + PartyTableJournal);
        if (journal == 0)
        {
            failure = 6;
            return false;
        }

        if (!NativeMethods.IsReadable(journal))
        {
            failure = 4;
            return false;
        }

        nint data = *(nint*)journal;
        int size = *(int*)(journal + 4);
        int capacity = *(int*)(journal + 8);

        if (size < 0 || capacity < size || capacity > 4096
            || (size > 0 && (data == 0 || !NativeMethods.IsReadable(data))))
        {
            failure = 4;
            return false;
        }

        ReadOnlySpan<byte> requestTag = new(tagAscii, tagLength);
        bool found = false;

        for (int i = 0; i < size; i++)
        {
            nint entry = data + (i * JournalEntryStride);
            nint entryTag = *(nint*)(entry + JournalEntryTag);
            if (entryTag != 0 && !NativeMethods.IsReadable(entryTag))
            {
                failure = 5;
                return false;
            }

            if (entryTag == 0)
            {
                continue;
            }

            uint entryTagLength = *(uint*)(entry + JournalEntryTag + 4);
            int bounded = (int)Math.Min(entryTagLength, 64);

            if (bounded > 0
                && JournalTags.EqualsNoCase(new ReadOnlySpan<byte>((void*)entryTag, bounded), requestTag))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            // Nothing to remove — not a failure, just idempotent (the caller's own shadow
            // should already have refused this, but a defensive no-op costs nothing).
            return true;
        }

        ExoString tag = new() { CString = (nint)tagAscii, Length = (uint)(tagLength + 1) };
        JournalDestroyEntryFn(journal, &tag);
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

    /// <summary>CSWSCreature + this = client twin CSWCObject*, 0 until the twin is made.</summary>
    private const int CreClientTwin = 0x224;

    /// <summary>True once the engine has created this creature's render-side twin, which is
    /// what carries the name to the selection band.</summary>
    public static bool HasClientTwin(nint creature) =>
        creature != 0 && *(nint*)(creature + CreClientTwin) != 0;

    /// <summary>A creature's render-side twin, or 0.</summary>
    public static nint GetClientTwin(nint creature) =>
        creature == 0 ? 0 : *(nint*)(creature + CreClientTwin);

    /// <summary>
    /// The LOCAL player's client twin, or 0. Used by the animation hook to tell the player's
    /// own one-shots apart from every other creature's — the hook fires for every swing in
    /// the area, so this must be cheap and must never call into the engine.
    /// </summary>
    public static nint GetLocalPlayerTwin()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint player = GetPlayerCreatureFn(server);
        return player == 0 ? 0 : *(nint*)(player + CreClientTwin);
    }

    // ---- applying a peer's one-shot animation -------------------------------------

    /// <summary>CSWCCreature vtable +0xA0 = AddFireForgetAnimation(uint anim, void* options).
    /// The options pointer may be null; the engine then derives the duration from the
    /// animation's own length divided by the twin's animation speed.</summary>
    private const int TwinAddFireForgetSlot = 0xA0;

    /// <summary>CSWCObject::AnimationFireAndForget(u16) — the engine's own classifier, so we
    /// never need a hardcoded list of which ids are one-shots.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationFireAndForgetFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C380;

    /// <summary>CSWCObject::AnimationOverlay(u16) — the same classifier for the third class,
    /// which has no object field at all and can only be applied through the animbase.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationOverlayFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C410;

    /// <summary>CSWCObject::AnimationLooping(u16) — the third of the engine's predicates.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationLoopingFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C300;

    /// <summary>ROUND 61 DIAGNOSTIC: CSWCObject::AnimationWalking(u16) / AnimationRunning(u16)
    /// — the predicates <c>ClientServerSyncHandler_Position</c> (0x00611F50, vtable +0x12C)
    /// actually branches on to decide whether to resolve and play a walk/run animation by
    /// name. Probed to confirm the +0x12C call this pass added is even reaching the
    /// animbase-vtable+0x54 "play" step for this creature.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationWalkingFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C240;

    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationRunningFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0063C2A0;

    /// <summary>
    /// ROUND 64 X1: <c>CSWCAnimBase::GetAnimationName(u16 id)</c>, animbase vtable +0x40
    /// (0x0069E620, db-confirmed) — the function <c>SetAnimationInternal</c> (animbase
    /// vtable +0xF0, 0x0069E8D0) calls to turn a requested id into the model-row NAME it
    /// actually plays (docs/research/phantom-locomotion.md §10.2). Returns the resolved
    /// CExoString by value through a hidden first stack argument (static disassembly,
    /// this round: prologue takes <c>this</c> in ecx, <c>[ebp+8]</c> = output CExoString*,
    /// <c>[ebp+0xc]</c> = the id as a zero-extended dword; epilogue is <c>ret 8</c>, i.e.
    /// exactly those two stack args). <see cref="ReadAnimationName"/> wraps the ABI.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, ushort, nint> GetAnimationNameFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, ushort, nint>)0x0069E620;

    /// <summary>
    /// ROUND 64 X1: calls the real engine name-resolver and reads back what it constructed,
    /// then frees it (<see cref="CExoStringDtorFn"/>, the same destructor
    /// <see cref="SetControlText"/> already uses, and — independently confirmed this round —
    /// the exact address (0x005E5C20) <c>GetAnimationName</c>'s own tail calls to destruct its
    /// internal temporaries) so this can be called repeatedly from a live diagnostic without
    /// leaking.
    ///
    /// Cut at the first null rather than trusted-length, per the CExoString length trap (the
    /// stored length includes the terminating null — see BarkHook.cs/CreatureFinder.cs) — and
    /// belt-and-suspenders here anyway, since an unresolved id is expected to come back empty.
    ///
    /// MAIN THREAD ONLY (it can allocate/free through the engine's own string routines).
    /// </summary>
    private static string ReadAnimationName(nint animBase, ushort id, out int rawLength)
    {
        byte* buf = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            buf[i] = 0;
        }

        GetAnimationNameFn(animBase, buf, id);

        byte* chars = *(byte**)buf;
        int length = *(int*)(buf + 4);
        rawLength = length;

        string name = "";
        if (chars != null && length > 0)
        {
            int cap = Math.Min(length, 63);
            int n = 0;
            while (n < cap && chars[n] != 0)
            {
                n++;
            }

            name = n > 0 ? System.Text.Encoding.ASCII.GetString(chars, n) : "";
        }

        CExoStringDtorFn(buf);
        return name;
    }

    /// <summary>CSWCAnimBase + this = the model's own resref (CResRef, fixed char[16],
    /// zero-filled — NOT a CExoString). tools/kotor1_0_3.db: CSWCAnimBase.name, offset 44.
    /// ROUND 64 X1: read for context alongside <see cref="ReadAnimationName"/> — which model
    /// this animbase's classifiers/name table are actually keyed to.</summary>
    private const int AnimBaseModelName = 0x2C;

    /// <summary>Reads a fixed CResRef (char[16], zero-filled, not a CExoString) as ASCII,
    /// cutting at the first null or 16 bytes, whichever comes first.</summary>
    private static string ReadResRef(nint address)
    {
        byte* chars = (byte*)address;
        int n = 0;
        while (n < 16 && chars[n] != 0)
        {
            n++;
        }

        return n > 0 ? System.Text.Encoding.ASCII.GetString(chars, n) : "";
    }

    /// <summary>CSWSCreature + this = appearance.2da row (ushort), inside the embedded
    /// CSWSCreatureAppearanceInfo at +0xA50 (tools/kotor1_0_3.db:
    /// CSWSCreatureAppearanceInfo.appearance_type, offset 16 -> 0xA50+0x10 = 0xA60). The
    /// same field <see cref="PhantomController"/>'s appearance poke writes.</summary>
    private const int CreServerAppearanceRow = 0xA60;

    /// <summary>
    /// ROUND 64 X2: CSWCCreature + this = the CLIENT-side cached "desired appearance" row
    /// (ushort). <c>CSWCCreature::ForceAppearanceUpdate</c> (0x00614A30, thiscall, no stack
    /// args — db-confirmed) reads exactly this field, writes it into
    /// <c>this-&gt;appearance-&gt;appearance_info.appearance_id</c> (CSWCCreatureAppearance
    /// +0x18, matching tools/kotor1_0_3.db's CCAppearanceInfo.appearance_id at offset 24
    /// embedded at CSWCCreatureAppearance offset 0), and then calls
    /// <c>CSWCCreature::SetAppearance</c> (0x006134C0, one of ForceAppearanceUpdate's own 7
    /// live call sites, static disassembly this round) — which itself calls
    /// <c>CSWCObject::GetAnimBase</c> (0x0063C070, the same <c>[ecx+0x68]</c> accessor
    /// <see cref="TwinAnimBase"/> uses) and the AnimationLooping/AnimationStationary
    /// classifiers, i.e. it touches the animbase, not just server-side bookkeeping.
    ///
    /// NEEDS-LIVE: whether writing this field then calling ForceAppearanceUpdate actually
    /// reloads the twin's model/animbase name table (the X2 hypothesis) was not confirmed by
    /// static disassembly alone — SetAppearance's own body was not fully traced past its
    /// animbase-touching calls. See phantom-locomotion.md §11 for the live result.
    /// </summary>
    private const int TwinDesiredAppearanceRow = 0x38C;

    /// <summary>ROUND 64 X2: CSWCCreature::ForceAppearanceUpdate(this) — thiscall, no stack
    /// args (db-confirmed param_size_bytes=0). See <see cref="TwinDesiredAppearanceRow"/>'s
    /// doc for the call chain this reaches.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> ForceAppearanceUpdateFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x00614A30;

    /// <summary>
    /// ROUND 64 X2 FIX: rebuilds the client twin's render-side appearance/model — and, by
    /// construction (<see cref="TwinDesiredAppearanceRow"/>'s doc), its animbase's model name
    /// table — after a phantom's appearance row is poked directly. <see cref="RebuildAppearance"/>
    /// (server-side, <c>UpdateAppearanceDependantInfo</c>) already ran; this is the CLIENT-side
    /// counterpart the engine's own network appearance-change handler would have triggered, which
    /// nothing in this codebase called before this round. MAIN THREAD ONLY.
    /// </summary>
    public static void RebuildClientAppearance(nint creature)
    {
        nint twin = GetClientTwin(creature);
        if (twin == 0 || *(nint*)twin != ClientCreatureVtable)
        {
            return;
        }

        ushort row = *(ushort*)(creature + CreServerAppearanceRow);
        *(ushort*)(twin + TwinDesiredAppearanceRow) = row;
        ForceAppearanceUpdateFn(twin);
    }

    /// <summary>
    /// ROUND 61: <c>CSWCAnimBase::AnimationExists(u16)</c>, animbase vtable +0x50
    /// (0x0069D910, confirmed by name in tools/kotor1_0_3.db). This — NOT
    /// AnimationLooping/FireAndForget/Overlay, which round 60 blamed — is the real gate on
    /// whether a gait id actually plays. Static disassembly of the two functions between the
    /// sync call and the render field settles it:
    /// <list type="bullet">
    /// <item><c>CSWCCreature::ClientServerSyncHandler_Animation</c> (vtable +0x134,
    /// 0x0060C100 — the entry <see cref="TwinAnimationSyncSlot"/> calls) never calls
    /// AnimationLooping at all. It only calls AnimationFireAndForget to choose between
    /// vtable +0xA0 (fire-and-forget path) and vtable +0x9C (the plain setter) — our gait ids
    /// are not fire-and-forget, so it always takes +0x9C.</item>
    /// <item><c>CSWCObject::SetAnimation</c> (vtable +0x9C, 0x0063E930 — the "plain setter"
    /// the file's own comments already knew "does not stick") calls
    /// <c>animBase-&gt;vtbl[0x50](id)</c> = AnimationExists and returns immediately, WITHOUT
    /// touching twin+0x48 at all, when it reports false. AnimationExists itself returns false
    /// immediately if the animbase's own <c>Gob*</c> (tools/kotor1_0_3.db:
    /// <c>CSWCAnimBase.gob</c>, offset 184 = <see cref="AnimBaseGob"/>) is null — the loaded
    /// render/scene node — and otherwise resolves the id to a model animation name
    /// (<c>GetAnimationName</c>, animbase vtable +0x40, 0x0069E620) and asks the Gob whether a
    /// node with that name exists on the loaded model.</item>
    /// </list>
    /// So "the animbase does not classify these ids as playable" (round 60's read of
    /// AnimationLooping) was the wrong function; THIS is the one that actually decides.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, ushort, int> AnimationExistsFn =
        (delegate* unmanaged[Thiscall]<nint, ushort, int>)0x0069D910;

    /// <summary>CSWCAnimBase + this = Gob* (the render/scene node this animbase drives).
    /// Null until the model has finished loading. tools/kotor1_0_3.db: CSWCAnimBase.gob,
    /// offset 184 (0xB8). <see cref="AnimationExistsFn"/> refuses everything while this is
    /// null.</summary>
    private const int AnimBaseGob = 0xB8;

    private static int _animationClassLogged;

    /// <summary>Reports the first few unclassified ids. All three predicates are data-driven
    /// (they resolve the id through the model's own animbase and then look the row up in
    /// animations.2da), so which class an id falls into is a property of the CREATURE, not a
    /// constant — worth being able to see rather than assume.</summary>
    private static void LogAnimationClassOnce(string message)
    {
        if (_animationClassLogged < 8)
        {
            _animationClassLogged++;
            PluginHost.Log(message);
        }
    }

    /// <summary>CSWCObject + this = its CSWCAnimBase (the engine's own accessor at 0x0063C070
    /// is exactly <c>mov eax,[ecx+0x68]; ret</c>).</summary>
    private const int TwinAnimBase = 0x68;

    /// <summary>CSWCAnimBase vtable +0xE0 = ServerToClientAnimation(u16 anim, int) — maps an
    /// animation id onto the model's own row. <c>ret 4</c>: it consumes only the id, leaving
    /// the second pushed argument for the setter below, which is how the engine's own
    /// flourish arm at 0x005F4681 shares one <c>push 1</c> between the two calls.</summary>
    private const int AnimBaseServerToClientSlot = 0xE0;

    /// <summary>CSWCAnimBase vtable +0x44 = SetOverlayAnimation_2(row, int). <c>ret 8</c>.
    /// Overridden per animbase class; on a creature it is an adjustor thunk, so it is always
    /// called through the slot. See <see cref="PlayOverlayAnimation"/>.</summary>
    private const int AnimBaseSetOverlaySlot = 0x44;

    /// <summary>The +0xE0 implementation every creature-family animbase shares, and the one
    /// thing that tells a creature's animbase apart from a door's or a placeable's.</summary>
    private const nint AnimBaseServerToClientFn = 0x0069F650;

    /// <summary>
    /// Plays a peer's one-shot animation (a weapon draw, an attack swing, a flourish, an
    /// injected stim) on a phantom, through the engine's own client-side call.
    ///
    /// A raw field write cannot do this job: the engine's own object-update handler drops
    /// repeats and explicitly skips the entire combat animation set, and overlays have no
    /// field at all. See docs/research/animation-and-minimap.md.
    ///
    /// Three animation classes, three different calls, and using the wrong one is not a
    /// no-op: a looping or overlay id pushed down the fire-and-forget path parks in the
    /// twin's current-animation field with no expiry and wedges the body. So the engine's
    /// own predicates decide, and anything they both refuse is dropped.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PlayOneShotAnimation(nint creature, ushort animation)
    {
        nint twin = GetClientTwin(creature);
        if (twin == 0 || *(nint*)twin != ClientCreatureVtable)
        {
            return false;
        }

        if (AnimationFireAndForgetFn(twin, animation) == 0)
        {
            // The moving weapon flourish (0x27AD) lands here: an OVERLAY, which plays over
            // whatever the body is already doing and exists only inside the animbase.
            // Measured live on a phantom: 0x27AD reads overlay 1, looping 0.
            if (AnimationOverlayFn(twin, animation) != 0 && AnimationLoopingFn(twin, animation) == 0)
            {
                return PlayOverlayAnimation(twin, animation);
            }

            LogAnimationClassOnce($"animation: 0x{animation:X} is in no playable class; dropped");
            return false;
        }

        nint vtable = *(nint*)twin;
        var addFireForget =
            (delegate* unmanaged[Thiscall]<nint, uint, nint, void>)(*(nint*)(vtable + TwinAddFireForgetSlot));

        // The engine reaches InsertFireForgetAnimation from inside this call; without the
        // suppression the mirrored animation would be captured as if this creature had
        // played it itself and echo back to its owner.
        AnimationHook.Suppress = true;
        try
        {
            addFireForget(twin, animation, 0);
        }
        finally
        {
            AnimationHook.Suppress = false;
        }

        return true;
    }

    /// <summary>
    /// Applies an OVERLAY animation to a twin, exactly as the engine's own moving flourish
    /// does at 0x005F4681: resolve the animbase, map the id to a model row, set it as the
    /// overlay.
    ///
    /// ⚠ BOTH CALLS GO THROUGH THE VTABLE, and that is not a style choice. Measured live on
    /// a phantom, a creature's animbase carries vtable <c>0x00754AE8</c>, whose +0x44 is an
    /// ADJUSTOR THUNK (<c>sub ecx,[ecx-4]; jmp 0x00699420</c>, the CSWCAnimBaseHeadWield
    /// override) rather than <c>CSWCAnimBase::SetOverlayAnimation_2 0x006A03C0</c>. That
    /// pointer is a secondary base, so calling the base implementation directly would hand
    /// it an unadjusted <c>this</c>. The engine calls the slot; so do we. (The first version
    /// of this code required the slot to equal 0x006A03C0 and refused every real creature —
    /// which is how the thunk was found, and an argument for guards that log.)
    ///
    /// The guard that remains is the +0xE0 slot's identity. Every creature-family animbase
    /// shares <c>CSWCAnimBase::ServerToClientAnimation 0x0069F650</c>; doors, placeables,
    /// triggers, projectiles and visual effects each override it. So the slot matching is a
    /// positive statement that this really is a creature's animbase before we call the other
    /// slot, whatever that turns out to be.
    /// </summary>
    private static bool PlayOverlayAnimation(nint twin, ushort animation)
    {
        nint animBase = *(nint*)(twin + TwinAnimBase);
        if (!LooksLikeHeapPointer(animBase))
        {
            LogAnimationClassOnce($"animation: no animbase on twin 0x{twin:X} (0x{animBase:X})");
            return false;
        }

        nint vtable = *(nint*)animBase;
        if (!LooksLikeCodePointer(vtable))
        {
            LogAnimationClassOnce($"animation: animbase 0x{animBase:X} has no vtable (0x{vtable:X})");
            return false;
        }

        nint serverToClient = *(nint*)(vtable + AnimBaseServerToClientSlot);
        nint setOverlay = *(nint*)(vtable + AnimBaseSetOverlaySlot);

        if (serverToClient != AnimBaseServerToClientFn || !LooksLikeCodePointer(setOverlay))
        {
            LogAnimationClassOnce($"animation: animbase vtable 0x{vtable:X} is not a creature's"
                + $" (+0xE0 0x{serverToClient:X}, +0x44 0x{setOverlay:X})");
            return false;
        }

        int row = ((delegate* unmanaged[Thiscall]<nint, ushort, int>)serverToClient)(animBase, animation);
        ((delegate* unmanaged[Thiscall]<nint, int, int, void>)setOverlay)(animBase, row, 1);
        return true;
    }

    /// <summary>CSWGuiMainInterface + this = the minimap viewport's width and height. DrawMap
    /// passes the first as BOTH dimensions to AurGUISetupViewport (0x0068AC77), which is what
    /// actually clips, so the width is the honest bound on each axis.</summary>
    private const int MainInterfaceViewportW = 0x6088;

    private const int MainInterfaceViewportH = 0x608C;

    /// <summary>How faded an off-map peer's marker is, so "somewhere that way" cannot be
    /// mistaken for "right here".</summary>
    private const float OffMapAlpha = 0.55f;

    /// <summary>
    /// Holds a marker inside the minimap's viewport, returning true when it had to move —
    /// which is the same thing as "this peer is off the map".
    ///
    /// The icon's extent is in viewport space (measured live: a 32×32 icon at (44,44) inside a
    /// 120×120 viewport, i.e. dead centre), so the bound is just the viewport inset by the
    /// icon, and clamping each axis independently keeps the marker on the border nearest the
    /// peer's direction.
    /// </summary>
    private static bool ClampToViewport(nint mainInterface, int iconW, int iconH, ref int x, ref int y)
    {
        int width = *(int*)(mainInterface + MainInterfaceViewportW);
        int height = *(int*)(mainInterface + MainInterfaceViewportH);

        // A viewport smaller than the icon (or an unread field) leaves the marker alone
        // rather than pinning everything to 0,0.
        if (width <= iconW || height <= iconH || iconW <= 0 || iconH <= 0)
        {
            return false;
        }

        int clampedX = Math.Clamp(x, 0, width - iconW);
        int clampedY = Math.Clamp(y, 0, height - iconH);

        bool moved = clampedX != x || clampedY != y;
        x = clampedX;
        y = clampedY;
        return moved;
    }

    /// <summary>Range check for a vtable pointer: inside the image, where .rdata lives.</summary>
    internal static bool LooksLikeCodePointer(nint p) => p is > 0x400000 and < 0x8C3000;

    /// <summary>The CSWCCreature vtable — the guard that a twin is really a creature twin
    /// before its GetName-side name fields are written.</summary>
    private const nint ClientCreatureVtable = 0x0074F5F8;

    // CSWCCreature::SetName(CExoString* first, CExoString* last). This is what the
    // selection band actually renders (via GetName, vtable +0x70). Both strings are copied
    // with the engine allocator, so stack locals are fine; the last name must be a non-null
    // empty string, not a null pointer.
    private static readonly delegate* unmanaged[Thiscall]<nint, ExoString*, ExoString*, void> ClientSetNameFn =
        (delegate* unmanaged[Thiscall]<nint, ExoString*, ExoString*, void>)0x0060D600;

    /// <summary>
    /// Sets a creature's first name — the selection band above its head — on BOTH the
    /// server locstring and the client twin.
    ///
    /// The twin write is the load-bearing half, and it is why the earlier "AddString ran
    /// but nothing showed" was a mystery: the band renders from a plain CExoString cached
    /// on the client twin, and the engine's per-frame appearance diff has no "name changed"
    /// bit — a name reaches the twin exactly once, in the object's first update. Writing
    /// only the server locstring after that update never reaches the band. The server half
    /// still matters (saves, feedback text, GetFullName, the combat log read it), so both
    /// are written.
    ///
    /// Idempotent — re-assert it after a module transition (the twin is recreated) and
    /// whenever the twin first appears. MAIN THREAD ONLY.
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

        // Length is capacity (strlen + 1), matching what the engine's own CExoString
        // constructor from char* stores. nameAscii must be null-terminated.
        ExoString text = new() { CString = (nint)nameAscii, Length = (uint)(length + 1) };

        // Server half: the localized string GetFullName and the save writer read.
        nint locString = stats + StatsFirstName;
        ClearLocStringFn(locString);
        LocStringAddStringFn(locString, 0, &text, 0);

        // Client half: the plain string the band renders. Skipped safely until the twin
        // exists — the first GameObjUpdate then carries the name from the server locstring
        // just written.
        nint twin = *(nint*)(creature + CreClientTwin);
        if (twin != 0 && *(nint*)twin == ClientCreatureVtable)
        {
            byte emptyByte = 0;
            ExoString empty = new() { CString = (nint)(&emptyByte), Length = 1 };
            ClientSetNameFn(twin, &text, &empty);
        }

        return true;
    }

    // ---- dialogue spectate -------------------------------------------------------
    //
    // Read-only, main-thread. Per docs/research/dialogue-sync.md: the client
    // "conversation active" flag is *(CGuiInGame + 0xB4); the two participants are object
    // ids at gui+0x170 / gui+0x174; the live CSWSDialog is *(CSWSObject_owner + 0x34)
    // (vtable 0x0074A618); each entry node carries a speaker CExoString @ +0x00 and a text
    // CExoLocString @ +0x08. Every pointer is guarded — a null or garbage read bails to
    // empty and never faults, because a crashing conversation is unacceptable (spectate
    // only; the suppression lever is deliberately NOT implemented — it is NEEDS-LIVE).

    /// <summary>CGuiInGame + this = nonzero while a conversation is on screen (client).</summary>
    private const int GuiConversationActive = 0xB4;

    /// <summary>CGuiInGame + this = the two dialog participants, as object ids.</summary>
    private const int GuiParticipantA = 0x170;
    private const int GuiParticipantB = 0x174;

    /// <summary>CSWSObject + this = active CSWSDialog* while it runs a conversation, else null.</summary>
    private const int ObjActiveDialog = 0x34;

    /// <summary>CSWSDialog vtable — the guard that a +0x34 pointer really is a live dialog.</summary>
    private const nint DialogVtable = 0x0074A618;

    private const int DialogEntriesCount = 0x04; // int
    private const int DialogEntries = 0x08;       // CSWSDialogEntry*, stride 0xB0
    private const int DialogEntryStride = 0xB0;

    /// <summary>
    /// CSWSDialog + this = the index of the entry currently on screen.
    ///
    /// ⚠ NOT +0x60. That was a candidate picked out of the "+0x60..+0x74 band" without a
    /// proof, and it is the wrong dword. The right one is settled twice over, both times by
    /// the engine using it as an index rather than by inference:
    ///
    ///   * <c>CSWSDialog::SendDialogEntry</c> loads it at 0x005A4045 and indexes the entry
    ///     array with it — <c>imul eax, eax, 0xB0</c> against <c>[esi+0x08]</c>, and 0xB0 is
    ///     exactly <c>sizeof(CSWSDialogEntry)</c> in the symbol database, with +0x08 exactly
    ///     the <c>entries</c> pointer;
    ///   * <c>CSWSDialog::HandleReply</c> compares the caller's claimed entry index against
    ///     it at 0x005A0403 and refuses the reply when they differ. That is the engine's OWN
    ///     desync check, on this field, which makes it the field two instances have to agree
    ///     on by the engine's own definition of agreement.
    /// </summary>
    private const int DialogCurrentEntryIndex = 0x70;

    // CSWSDialogNode (at the start of each entry): speaker CExoString @0, text CExoLocString @8.
    private const int NodeSpeaker = 0x00;
    private const int NodeText = 0x08;

    /// <summary>Range-only pointer sanity (a char buffer need not be 4-aligned).</summary>
    private static bool LooksLikeDataPointer(nint p) => p is > 0x10000 and < 0x7FFF0000;

    /// <summary>The live in-game GUI (CGuiInGame), or 0 when not in a loaded module.</summary>
    private static nint GetInGameGui()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint clientInternal = Deref(client + AppClientOffset);
        if (!LooksLikeHeapPointer(clientInternal))
        {
            return 0;
        }

        nint gui = Deref(clientInternal + 0x40);
        return LooksLikeHeapPointer(gui) ? gui : 0;
    }

    /// <summary>True while the player is in a loaded module (not a menu/load screen), so a chat
    /// key should open the chat prompt rather than do nothing at the main menu.</summary>
    public static bool IsInGame => GetInGameGui() != 0;

    /// <summary>CGuiInGame + this = CSWGuiMainInterface* — the HUD panel: minimap, party
    /// portraits, action bar (docs/research/ingame-gui-transition.md §"main_interface").</summary>
    private const int GuiMainInterface = 0x90;

    /// <summary>CSWGuiMainInterface's vtable (symbol database). Checking it EXACTLY is what
    /// lets the widget offsets inside the object be trusted: a pointer with this vtable is
    /// this class, laid out the way the database says.</summary>
    private const nint MainInterfaceVtable = 0x00753F50;

    /// <summary>The live HUD object, vtable-verified, or 0 (no module loaded, HUD torn
    /// down for a transition, or a pointer that is not what the layout says it is).</summary>
    public static nint GetMainInterface()
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            return 0;
        }

        nint mainInterface = Deref(gui + GuiMainInterface);
        return mainInterface != 0
            && LooksLikeHeapPointer(mainInterface)
            && *(nint*)mainInterface == MainInterfaceVtable
            ? mainInterface
            : 0;
    }

    /// <summary>True while a conversation is on screen, so the chat key defers to dialogue.</summary>
    public static bool IsConversationActive
    {
        get
        {
            nint gui = GetInGameGui();
            return gui != 0 && ConversationLive(gui);
        }
    }

    /// <summary>
    /// Whether the client GUI has a conversation on screen.
    ///
    /// MEASURED, because <c>GetIsConversationActive 0x0053EF70</c> reads this field and then
    /// passes it to an object lookup (0x0053EF83), which reads like an object id. It is not:
    /// sampled live across a whole conversation it only ever holds 0 or 1, and the engine's
    /// own teardown clears it — a host that ended a conversation properly read
    /// <c>0xB0=0, 0xB4=0</c> on the next sample.
    ///
    /// ⚠ Do not "improve" this into a lookup of <c>+0xB4</c> as an id. That was tried: it
    /// resolves only because object id 1 happens to exist, and asking the wrong table
    /// (the 0x007A39FC client chain rather than 0x007A3A00) made every conversation in the
    /// game read as inactive. A flag test is what the field supports.
    ///
    /// A NON-ZERO VALUE HERE IS NOT PROOF OF A HEALTHY CONVERSATION. A client whose server
    /// half still holds an unfinished conversation re-sets this every frame, with the GUI's
    /// participants both reading OBJECT_INVALID — see <see cref="CloseDialogue"/>, which is
    /// where that case has to be ended.
    /// </summary>
    private static bool ConversationLive(nint gui) =>
        *(int*)(gui + GuiConversationActive) != 0;

    /// <summary>
    /// Reads the speaker and line of the conversation the local player is currently in, into
    /// two caller-owned null-terminated ASCII buffers. Returns true when a conversation is on
    /// screen (the buffers may still be empty if the current line could not be resolved),
    /// false when the player is not in a conversation.
    ///
    /// Base-game lines are TLK StrRefs with no inline substring; those resolve to empty here
    /// (resolving the TLK is out of scope). Custom / inline-text lines are read in full.
    /// MAIN THREAD ONLY.
    /// </summary>
    // The read surface is CGuiInGame's own dialogue HISTORY ring — the log every displayed
    // line is appended to. Located live (2026-08-01) by finding the on-screen sentence in
    // the heap and walking back to who held it:
    //
    //   gui + 0x1040  capacity (reads 0x40 = 64)
    //   gui + 0x1054  first record; stride 0x10
    //   record: { char* text; u32 len; char* speaker; u32 speakerLen }   (lens include the null)
    //
    // This beats reading the CSWSDialog tree on every axis: the text is already
    // TLK-RESOLVED (a base-game line's CExoLocString carries only a StrRef, which is why
    // the tree read produced empty lines), the speaker is the display name, and it works
    // for every dialog variant — the cinematic letterbox panel stores its message somewhere
    // else entirely, but every variant appends here.
    //
    // "Which line is current" falls out of change tracking: the newest line is the
    // highest-offset slot that changed most recently. An append changes one slot (the
    // newest); a shift-when-full changes many, of which the newest is the highest; a wrap
    // changes exactly the newest. All three cases agree with this rule.

    /// <summary>CGuiInGame + this = first record of the dialogue history ring.</summary>
    private const int GuiDialogueRing = 0x1054;

    private const int GuiDialogueRingSlots = 64;

    private const int GuiDialogueRecordStride = 0x10;

    private static readonly uint[] RingSignatures = new uint[GuiDialogueRingSlots];

    private static int _ringNewest = -1;

    public static bool TryReadActiveDialogue(byte* speaker, int speakerCap, byte* line, int lineCap)
    {
        if (speaker != null && speakerCap > 0)
        {
            speaker[0] = 0;
        }

        if (line != null && lineCap > 0)
        {
            line[0] = 0;
        }

        nint gui = GetInGameGui();
        if (gui == 0)
        {
            _ringNewest = -1;
            return false;
        }

        // Track ring changes EVERY frame, active or not, so the first frame of a
        // conversation already knows which slot is newest (all populated slots differ from
        // the zeroed signature table on the first pass; the highest wins, which is the
        // newest under the append order observed live).
        int changed = -1;
        for (int i = 0; i < GuiDialogueRingSlots; i++)
        {
            nint record = gui + GuiDialogueRing + i * GuiDialogueRecordStride;
            uint signature = (uint)*(nint*)record ^ (uint)(*(int*)(record + 4) * 2654435761);
            if (signature != RingSignatures[i])
            {
                RingSignatures[i] = signature;
                changed = i;
            }
        }

        if (changed >= 0)
        {
            _ringNewest = changed;
        }

        if (!ConversationLive(gui))
        {
            return false; // no conversation on screen
        }

        if (_ringNewest < 0)
        {
            return true; // active but no line seen yet -> empty
        }

        nint newest = gui + GuiDialogueRing + _ringNewest * GuiDialogueRecordStride;

        if (line != null)
        {
            nint text = *(nint*)newest;
            int textLen = *(int*)(newest + 4);
            if (LooksLikeDataPointer(text) && textLen is > 1 and <= 0x400)
            {
                CopyAsciiBounded(text, textLen - 1, line, lineCap);
            }
        }

        if (speaker != null)
        {
            nint name = *(nint*)(newest + 8);
            int nameLen = *(int*)(newest + 12);
            if (LooksLikeDataPointer(name) && nameLen is > 1 and <= 0x40)
            {
                CopyAsciiBounded(name, nameLen - 1, speaker, speakerCap);
            }
        }

        return true;
    }

    // ---- dialogue follow ---------------------------------------------------------
    //
    // The spectator runs the SAME conversation in the engine's own UI. Three levers:
    //
    //   open   CSWCCreature::ActionInitiateDialog (0x0060F620) on the NPC's client twin —
    //          the exact path a click on that NPC takes, ending in
    //          SendPlayerToServerInput_Dialog, so the server builds a real CSWSDialog.
    //   follow CSWGuiDialog::SelectReply (0x006A7110, thiscall(int)) — the click-reply-N
    //          entry. Replaying the owner's slot walks our copy of the same .dlg down the
    //          same branch.
    //   mute   DialogueReplyHook refuses CSWGuiDialog::SelectReply on the follower. That is
    //          the whole reply block.
    //          ⚠ NOT CClientExoApp::SetCanSendDialog (0x005EDC80), which reads like the
    //          right lever and is not one: its only consumer is IsDialogDelay 0x004CD2D0,
    //          so clearing it stalls the follower's own conversation ADVANCE — killing the
    //          voice-over and the lipsync with it. See DialogueSpectateController.

    /// <summary>CSWSObject + this = the object's tag (CExoString).</summary>
    private const int ObjTag = 0x18;

    /// <summary>
    /// CGuiInGame + this = the live CSWGuiDialog panel.
    ///
    /// ⚠ This was 0x78, which is the PARTY SELECTION panel, and the mistake was silent and
    /// expensive. Verified in the binary: <c>CGuiInGame::ShowDialogEntry 0x0062B730</c> and
    /// <c>ShowDialogReplies 0x006340E0</c> both load the panel from <c>[esi+0x3C]</c>
    /// (0x0062B758, 0x0062B795, 0x0062B7B6, 0x0063411B), while <c>ShowPartySelection
    /// 0x0062DD20</c> is the one that loads <c>[esi+0x78]</c> (0x0062DDC6). The symbol
    /// database agrees — it names +0x78 party_selection and leaves a gap at +0x3C.
    ///
    /// What the wrong offset cost: <see cref="SelectDialogueReply"/> read its reply count out
    /// of the party-selection panel, failed the 1..64 range test and returned false forever,
    /// so a FOLLOWER NEVER REPLAYED THE OWNER'S PICK. The follow then starved, hit the
    /// content timeout, dropped its reply suppression — and both players could answer the
    /// same conversation, independently. That is the reported bug.
    /// </summary>
    private const int GuiDialogPanel = 0x3C;

    /// <summary>CSWGuiDialog + this = how many reply slots are on screen.</summary>
    private const int DialogPanelReplyCount = 0x6C;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> ActionInitiateDialogFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x0060F620;

    /// <summary>CClientExoApp::GetSWCMessage — the client's message pipe to the server.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetSWCMessageFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED6F0;

    /// <summary>
    /// CSWCMessage::SendPlayerToServerInput_Dialog(objectId) — "I want to talk to that
    /// object". ret 4, one argument, and it self-guards on a conversation already being
    /// active (0x00677EB3), so it cannot start a second one.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> SendDialogInputFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x00677EA0;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> SetCanSendDialogFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)0x005EDC80;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> CloseDialogFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x006332B0;

    /// <summary>
    /// CSWSObject::StopDialog — the SERVER-side end of a conversation, and the only complete
    /// one. On the object that OWNS the dialog it runs the end-conversation script, clears
    /// the participants' dialog fields and destructs the CSWSDialog, whose destructor calls
    /// <c>CSWSDialog::Cleanup</c> — which is what calls <c>CGuiInGame::CloseDialog</c>
    /// (0x005A2841). On any other participant it forwards to the owner's
    /// <c>RemoveObjectFromDialog</c>, which ends the whole thing once the last player leaves.
    /// It is what the engine itself runs at a natural end (<c>CSWSDialog::HandleReply</c>
    /// 0x005A04AD) and when combat interrupts one (<c>CSWSCreature::ActivityManager</c>).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> StopDialogFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x004CB4F0;

    /// <summary>CSWSObject + this = the id of the object that owns the conversation this one
    /// is in, or OBJECT_INVALID. Verified against the disassembly of StopDialog, which
    /// compares it with the object's own id at +0x04 to decide owner from participant.</summary>
    private const int ObjDialogOwner = 0x54;

    /// <summary>Copies a creature's tag into an ASCII buffer. Empty when unavailable.</summary>
    public static void ReadObjectTag(nint obj, byte* dest, int cap)
    {
        if (cap <= 0)
        {
            return;
        }

        dest[0] = 0;
        if (obj == 0)
        {
            return;
        }

        ReadExoStringInto(obj + ObjTag, dest, cap);
    }

    /// <summary>
    /// Whether an object carries <paramref name="tag"/>, case-insensitively — the engine
    /// treats tags that way and so does the dialogue gate.
    ///
    /// Used to re-verify a cached pointer before acting on it: an object id and a raw pointer
    /// can both survive into memory that has been reused, and the tag is the thing the caller
    /// actually meant.
    /// </summary>
    public static bool HasTag(nint obj, ReadOnlySpan<byte> tag)
    {
        if (obj == 0 || tag.IsEmpty || tag.Length >= 64)
        {
            return false;
        }

        byte* actual = stackalloc byte[64];
        ReadObjectTag(obj, actual, 64);

        for (int i = 0; i < tag.Length; i++)
        {
            if (actual[i] == 0 || Lower(actual[i]) != Lower(tag[i]))
            {
                return false;
            }
        }

        return actual[tag.Length] == 0;

        static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
    }

    /// <summary>The tag of whoever the local player is currently in conversation with, or
    /// empty. Reads the client GUI's participant ids, skipping the player itself.</summary>
    public static void ReadDialoguePartnerTag(byte* dest, int cap)
    {
        if (cap > 0)
        {
            dest[0] = 0;
        }

        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui))
        {
            return;
        }

        uint player = GetPlayerObjectId();
        foreach (uint raw in stackalloc uint[] { *(uint*)(gui + GuiParticipantA), *(uint*)(gui + GuiParticipantB) })
        {
            // ⚠ The participant fields carry FLAG BITS above the object id. Measured live
            // during a Trask conversation: the field read 0x80000127 while the creature's
            // own id was 0x127, and 0xFFFFFFFF stood for the other (player) side. Resolving
            // the raw value found nothing, so the partner tag came out empty and no
            // spectator could ever open the conversation. Mask the top bit off.
            uint id = raw & 0x7FFFFFFF;

            if (id is 0 or ObjectInvalid or 0x7FFFFFFF || id == player)
            {
                continue;
            }

            nint obj = ResolveServerObject(id);
            if (obj != 0)
            {
                ReadObjectTag(obj, dest, cap);
                if (dest[0] != 0)
                {
                    return;
                }
            }
        }
    }

    /// <summary>
    /// Opens a conversation against the creature with <paramref name="tag"/>, exactly as a
    /// click on it would. Returns false when the creature is not in this module, a
    /// conversation is already running, or the twin does not exist yet.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool StartDialogueWith(ReadOnlySpan<byte> tag)
    {
        nint gui = GetInGameGui();
        if (gui == 0 || ConversationLive(gui))
        {
            return false; // no HUD, or we are already talking
        }

        nint creature = CreatureFinder.FindByTag(tag);
        if (creature == 0)
        {
            return false;
        }

        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        nint message = GetSWCMessageFn(client);
        if (message == 0)
        {
            return false;
        }

        // ⚠ Send the request DIRECTLY rather than through CSWCCreature::ActionInitiateDialog.
        //
        // The wrapper is what a mouse click runs, and it works for a click — but driven from
        // here it left the conversation half-started: measured live, the client marked a
        // conversation active while both participant ids stayed OBJECT_INVALID and no entry
        // ever arrived. Its client-side gates (an unset selection, the creature's own
        // interaction state) are satisfied by the act of clicking and not by us. The send
        // itself is the part that reaches the server, takes one argument, and self-guards on
        // a conversation already being active, so it is both the smaller and the safer half.
        uint objectId = *(uint*)(creature + ObjIdOffset);
        if (objectId is 0 or ObjectInvalid)
        {
            return false;
        }

        // dialogue_join_teleport (default ON since 2026-08-21 — see docs/research/dialogue-join-latency.md
        // "Far-join live pass"). This is what an automatic join (force_dialogue_party, or the
        // test hook) has that a real mouse click does not: nobody is driving the PC toward the
        // NPC. Measured live: a joiner left standing 14m away never reaches StartDialog at
        // all — CSWSObject::AIActionDialogObject's own range gate (VERIFIED live at 10.0
        // units, `[0x747450]`, disasm 0x0057A7B2-0x0057A7AC) re-queues itself forever because
        // this build's player character is drive-controlled, not action-queue-walked (see
        // TestDriveController.HandleWalk's own finding). So without help the join just sits,
        // silently, until this instance's own 15 s local-open grace gives up on it.
        if (DialogueJoinTeleportEnabled)
        {
            TryTeleportForJoin(creature);
        }

        SendDialogInputFn(message, objectId);
        return true;
    }

    /// <summary>CSWSObject + this = the object's id.</summary>
    private const int ObjIdOffset = 0x04;

    /// <summary><c>coop.ini dialogue_join_teleport</c> (default on). Set once from
    /// <c>PluginConfig</c> at startup, the same way <see cref="DialogueGateHook.Enabled"/> is.</summary>
    public static bool DialogueJoinTeleportEnabled;

    /// <summary>
    /// The conversation-start range check's own threshold, VERIFIED LIVE by reading
    /// <c>[0x747450]</c> out of a running instance (read 10.0f) — the same float
    /// <c>CSWSObject::AIActionDialogObject</c> squares and compares a candidate's distance
    /// against at 0x0057A799-0x0057A7A5 before it will queue the conversation for real. A
    /// player standing farther than this from the NPC never reaches
    /// <c>CSWSObject::StartDialog</c> without either walking there or being placed there.
    /// </summary>
    private const float DialogueStartRange = 10.0f;

    /// <summary>Where <see cref="TryTeleportForJoin"/> found the local player before it moved
    /// them, so the conversation's end can put them back. <see cref="_joinTeleportPending"/>
    /// is false whenever there is nothing to restore — including the ordinary case where the
    /// player was already close enough that no teleport happened.</summary>
    private static Vec3 _joinTeleportOrigin;
    private static bool _joinTeleportPending;

    /// <summary>
    /// Places the local player next to <paramref name="creature"/> when they are farther than
    /// the engine's own start-range check allows, so the join resolves immediately instead of
    /// waiting on a walk nothing in this build will actually perform for a player character.
    /// Remembers the original spot; <see cref="RestoreJoinTeleportIfPending"/> puts it back
    /// once the followed conversation concludes.
    ///
    /// A no-op (and nothing is remembered) when the player is already in range — the ordinary
    /// case for a real mouse click, which this flag must not visibly disturb.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    private static void TryTeleportForJoin(nint creature)
    {
        nint player = GetPlayerCreaturePointer();
        if (player == 0)
        {
            return;
        }

        Vec3 origin = GetPosition(player);
        Vec3 target = GetPosition(creature);

        float dx = target.X - origin.X;
        float dy = target.Y - origin.Y;
        float dz = target.Z - origin.Z;
        if (dx * dx + dy * dy + dz * dz <= DialogueStartRange * DialogueStartRange)
        {
            return; // already close enough; the engine's own check will pass unaided
        }

        // A couple of units short of the NPC, not on top of it — SnapTo's own action clear is
        // what actually matters here (see its doc: setting position alone does not stick while
        // a move action still owns it), the exact offset just keeps the player out of the
        // NPC's collision.
        Vec3 destination = new() { X = target.X + 1.5f, Y = target.Y, Z = target.Z };

        _joinTeleportOrigin = origin;
        _joinTeleportPending = true;
        SnapTo(player, destination);
    }

    /// <summary>
    /// Puts the local player back where <see cref="TryTeleportForJoin"/> found them, once the
    /// followed conversation is over. Idempotent: does nothing when nothing is pending, which
    /// covers every join that never needed to move the player at all.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void RestoreJoinTeleportIfPending()
    {
        if (!_joinTeleportPending)
        {
            return;
        }

        _joinTeleportPending = false;

        nint player = GetPlayerCreaturePointer();
        if (player != 0)
        {
            SnapTo(player, _joinTeleportOrigin);
        }
    }

    /// <summary>
    /// Breaks a conversation shell that <see cref="ForceCloseDialogueUi"/> alone cannot close.
    ///
    /// ⚠ LIVE-VERIFIED, and it is why this exists rather than another retry of the UI close.
    /// A joiner left too far from the NPC (see <see cref="TryTeleportForJoin"/>'s own note)
    /// gets stuck with the engine's own conversation-active flag (<c>gui+0xB4</c>) re-armed
    /// every tick by the still-queued <c>AIActionDialogObject</c>, and <c>ForceCloseDialogueUi</c>
    /// — which only closes the client GUI panel — cannot touch that: measured live, it repeated
    /// every <c>YieldCloseRetryMs</c> for over two minutes straight with no effect.
    /// <c>CheatController</c>'s own <c>tp</c> (via <see cref="SnapTo"/>, which clears the
    /// object's action queue before moving it) stopped the loop within one cycle, every time it
    /// was tried; plain position writes and plain UI closes did not. Snaps the player back to
    /// their OWN current position — nothing about where they stand should change, only that
    /// the stuck action is gone.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void ClearLocalPlayerStuckDialogueAction()
    {
        nint player = GetPlayerCreaturePointer();
        if (player != 0)
        {
            SnapTo(player, GetPosition(player));
        }
    }

    /// <summary>
    /// CGuiInGame holds FOUR dialogue panels, one per class, at their own members (symbol
    /// database): the ordinary one at +0x3C, then cinematic, computer and computer-camera.
    /// A conversation drives exactly one of them.
    /// </summary>
    private static ReadOnlySpan<int> GuiDialogPanels => [0x3C, 0x40, 0x44, 0x48];

    /// <summary>The four dialogue panel vtables, so a candidate pointer is identified rather
    /// than assumed. Order matches nothing — membership is the test. (uint, not nint: a span
    /// of a platform-sized type cannot be a compile-time blob.)</summary>
    private static ReadOnlySpan<uint> DialogPanelVtables =>
    [
        0x007559E0, // CSWGuiDialog
        0x00755800, // CSWGuiDialogCinematic
        0x00755888, // CSWGuiDialogComputer
        0x00755958, // CSWGuiDialogComputerCamera
    ];

    /// <summary>
    /// The live conversation panel, or 0.
    ///
    /// ⚠ This used to read ONE member and assume it. That is wrong twice over: the member it
    /// read was +0x78, the party-selection panel (see <see cref="GuiDialogPanel"/>), and even
    /// the right member only covers ordinary conversations — KOTOR's story dialogue runs on
    /// the CINEMATIC panel, a different object at a different offset. A follower therefore
    /// read its reply count out of a panel that was not in the conversation and never
    /// replayed the owner's pick.
    ///
    /// The panel is now IDENTIFIED: each candidate must carry one of the four dialogue panel
    /// vtables, and the one actually showing replies wins. A panel with a plausible reply
    /// count is the conversation in progress; failing that, the first identified panel is
    /// returned so callers that only test for existence still work.
    /// </summary>
    private static nint GetDialoguePanel()
    {
        nint gui = GetInGameGui();
        if (gui == 0)
        {
            return 0;
        }

        nint identified = 0;

        foreach (int offset in GuiDialogPanels)
        {
            nint panel = Deref(gui + offset);
            if (!LooksLikeHeapPointer(panel))
            {
                continue;
            }

            nint vtable = *(nint*)panel;
            bool known = false;
            foreach (uint candidate in DialogPanelVtables)
            {
                if (vtable == (nint)candidate)
                {
                    known = true;
                    break;
                }
            }

            if (!known)
            {
                continue;
            }

            identified = identified == 0 ? panel : identified;

            int count = *(int*)(panel + DialogPanelReplyCount);
            if (count is > 0 and <= 64)
            {
                return panel; // this one is showing a reply list: it is the live conversation
            }
        }

        return identified;
    }

    /// <summary>
    /// Replays a reply the conversation's owner picked. Bounds-checked against the reply
    /// count the panel is actually showing, so a slot from a diverged tree is dropped
    /// rather than dispatched — SelectReply itself would reject it, but a stale index is a
    /// signal worth not acting on.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SelectDialogueReply(int slot)
    {
        nint panel = GetDialoguePanel();
        if (panel == 0)
        {
            return false;
        }

        int count = *(int*)(panel + DialogPanelReplyCount);
        if (count is <= 0 or > 64 || slot < 0 || slot >= count)
        {
            return false;
        }

        // ⚠ THROUGH THE PANEL'S OWN VTABLE SLOT, never the hard-coded address. The four
        // dialogue panel classes do not share one SelectReply: the cinematic panel's slot is
        // 0x006A7E20, not 0x006A7110, and calling the base implementation with a cinematic
        // `this` is exactly the class of mistake the engine's own dispatch exists to avoid.
        // The engine itself commits through `call [eax+0x6C]` in HandleSelectReply.
        nint vtable = *(nint*)panel;
        var selectReply = (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(vtable + PanelSelectReplySlot));
        if (!LooksLikeCodePointer((nint)selectReply))
        {
            return false;
        }

        // Bracketed so the one pick we dispatch passes the follower's own-click block.
        DialogueReplyHook.AllowReplay = true;
        try
        {
            selectReply(panel, slot);
        }
        finally
        {
            DialogueReplyHook.AllowReplay = false;
        }

        return true;
    }

    /// <summary>CSWGuiDialog vtable + this = SelectReply(int) — the slot HandleSelectReply
    /// 0x006A7390 itself dispatches through (<c>call [eax+0x6C]</c>).</summary>
    private const int PanelSelectReplySlot = 0x6C;

    // ---- dialogue node sync ------------------------------------------------------
    //
    // ⚠ REPLAYING THE OWNER'S REPLY SLOT CANNOT CONVERGE, AND NO AMOUNT OF GATING FIXES IT.
    //
    // A reply slot is not a property of the conversation; it is a property of ONE INSTANCE'S
    // EVALUATION of the conversation. Three separate decisions inside a dialogue are taken
    // independently on each machine, by scripts reading that machine's own world state:
    //
    //   * WHERE IT STARTS. CSWSDialog::GetStartEntry 0x005A3640 takes the first starting
    //     entry whose Active script passes. end_trask01.dlg alone has 19 starting entries, 18
    //     of them conditional — so two players at different tutorial progress begin on
    //     different nodes with no pick involved at all.
    //   * WHICH REPLIES ARE OFFERED. The panel's slots are COMPACTED over the links that
    //     passed locally (the real link index lives at panel+0x1970[slot]). One
    //     differently-filtered link shifts every later slot, so "slot 1" is simply not the
    //     same branch on the two screens.
    //   * WHERE A REPLY GOES. Its outgoing link is first-that-passes as well.
    //
    // So the owner publishes WHERE IT IS rather than WHAT IT PRESSED: the index of the entry
    // on screen. That is a plain array index into the .dlg's entry list — identical on both
    // instances because both load the same file — and it is the quantity the engine itself
    // treats as the conversation's position (HandleReply refuses a reply whose claimed index
    // does not match it). Node sync also dissolves the start-node divergence for free: the
    // follower is driven to wherever the owner actually began.
    //
    // It replaces the pick log outright. The two must never run together: a pick gate waiting
    // on a line hash and a node gate driving the node would each undo the other's work, which
    // is the stranding this is here to end.

    /// <summary>
    /// CSWSDialog::SendDialogEntry(CSWSObject* speaker, int, int entryIndex, int* status) —
    /// __thiscall on the DIALOG, <c>ret 0x10</c> (four epilogues, 0x005A414A / 0x005A41B3 /
    /// 0x005A41F7 / 0x005A4259; and the engine's call site at 0x004CD4B5 does not clean the
    /// four arguments up itself).
    ///
    /// This is the engine's actual producer of a dialogue line: the single call site of
    /// CGuiInGame::HandleDialogSound (the voice-over) and of the lipsync arming that follows
    /// it, as well as of the subtitle. Driving a node through it therefore brings the line,
    /// its audio and its mouth together, which is the whole reason node sync is the right
    /// shape — "subtitles stop, lips not synced, audio not synced" is one defect, not three.
    ///
    /// ⚠ CALLED DIRECTLY, NOT THROUGH CSWSObject::SendDialogEntry 0x004CD460 — deliberately,
    /// and this is the difference between a follower that moves and one that does not.
    ///
    /// The outer wrapper consults CSWSObject::IsDialogDelay first and, when that says "wait",
    /// QUEUES the entry (+0x4C, flag +0x48) and returns SUCCESS without sending anything.
    /// Measured on a live follower: `pending 1:29 sendGate 0 isDialogDelay 1` — the request
    /// for node 29 sat queued, the call kept reporting success, and the node never moved,
    /// through twelve hundred retries.
    ///
    /// That gate is not wrong; it is simply not ours. It exists to pace an OWNER's
    /// conversation against its own audio, so the next line does not tread on the current
    /// one. A follower's pacing authority is the other player: it should show the line the
    /// owner is on, when the owner is on it. Deferring to a local audio clock as well means
    /// deferring to the wrong clock, and the queue it deferred into is only ever flushed by
    /// UpdateDialog — which is to say, by the pacing this instance is not supposed to be
    /// running.
    ///
    /// The producer bounds-checks the entry index itself (0x005A41FD, against the entry count
    /// at +0x04) and is the same function the deferral would eventually have called, so going
    /// straight to it is the narrower path rather than the riskier one.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, int, int, int*, int> SendDialogEntryFn =
        (delegate* unmanaged[Thiscall]<nint, nint, int, int, int*, int>)0x005A4010;

    /// <summary>The status the producer writes back when it declined to send after all. The
    /// engine's own caller tests for exactly this and re-queues (0x004CD4BA).</summary>
    private const int DialogEntryDeferred = 0xA;

    /// <summary>CSWSObject + this = a dialog scratch field the engine clears immediately
    /// before every call to the producer (0x004CD4AB). Replicated rather than reasoned about:
    /// it is part of the call's contract at the only site that reaches it.</summary>
    private const int ObjDlgSendScratch = 0x1F4;

    /// <summary>
    /// The object that OWNS the live conversation, and its CSWSDialog. (0, 0) when there is
    /// no conversation on screen or the owner cannot be identified.
    ///
    /// "Owner" is the engine's own test, not ours: <c>CSWSObject::UpdateDialog</c> 0x004CD583
    /// refuses to run unless <c>[obj+0x54] == [obj+0x04]</c> — the dialog-owner id equals the
    /// object's own id — so that equality IS the definition of the object a conversation
    /// hangs off. Everything else is a mere participant, and driving an entry on one would
    /// write +0x1F4 on an object the engine never treats this way.
    ///
    /// The walk starts at the GUI's two participant ids, masking the flag bits exactly as
    /// <see cref="ReadDialoguePartnerTag"/> does, then hops once to the owner each names.
    /// Every pointer is guarded and the dialog is vtable-verified.
    /// </summary>
    public static bool TryGetActiveDialogue(out nint owner, out nint dialog)
    {
        owner = 0;
        dialog = 0;

        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui))
        {
            return false;
        }

        foreach (uint raw in stackalloc uint[]
                 { *(uint*)(gui + GuiParticipantA), *(uint*)(gui + GuiParticipantB) })
        {
            uint id = raw & 0x7FFFFFFF;
            if (id is 0 or ObjectInvalid or 0x7FFFFFFF)
            {
                continue;
            }

            nint participant = ResolveServerObject(id);
            if (participant == 0)
            {
                continue;
            }

            if (IsDialogueOwner(participant, out dialog))
            {
                owner = participant;
                return true;
            }

            // A participant names its owner at +0x54. Follow it exactly ONCE — a chain would
            // mean the field is not what the engine says it is, and looping on a bad read is
            // how a frame hook becomes a hang.
            uint ownerId = *(uint*)(participant + ObjDialogOwner);
            if (ownerId is 0 or ObjectInvalid || ownerId == id)
            {
                continue;
            }

            nint candidate = ResolveServerObject(ownerId);
            if (candidate != 0 && IsDialogueOwner(candidate, out dialog))
            {
                owner = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether an object is the owner of a live conversation: it names itself as the
    /// dialog owner AND carries a pointer with the CSWSDialog vtable.</summary>
    private static bool IsDialogueOwner(nint obj, out nint dialog)
    {
        dialog = 0;

        if (*(uint*)(obj + ObjDialogOwner) != *(uint*)(obj + ObjIdOffset))
        {
            return false;
        }

        nint candidate = Deref(obj + ObjActiveDialog);
        if (!LooksLikeHeapPointer(candidate) || *(nint*)candidate != DialogVtable)
        {
            return false;
        }

        dialog = candidate;
        return true;
    }

    /// <summary>Sanity bound on a .dlg's entry count. Generous — the largest base-game file is
    /// nowhere near it — and its job is only to reject a pointer that is not a dialog.</summary>
    private const int MaxDialogueEntries = 4096;

    /// <summary>
    /// CSWSObject::IsDialogDelay — __thiscall, no stack arguments, plain <c>ret</c> (verified
    /// at 0x004CD41E). Returns 1 when the engine will DEFER an entry instead of sending it.
    /// It is the gate <see cref="DriveDialogueToNode"/> loses to, so being able to ask it
    /// directly is the difference between diagnosing a stuck follower and guessing at it.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> IsDialogDelayFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x004CD2D0;

    // The dialog bookkeeping on a CSWSObject, from the symbol database (+0x38..+0x44 are
    // named; +0x48..+0x50 are the unnamed neighbours SendDialogEntry uses for its deferral).
    private const int ObjDlgDelayDay = 0x38;
    private const int ObjDlgDelayMs = 0x3C;
    private const int ObjDlgLastDay = 0x40;
    private const int ObjDlgLastMs = 0x44;
    private const int ObjDlgPendingFlag = 0x48;
    private const int ObjDlgPendingEntry = 0x4C;
    private const int ObjDlgSendGate = 0x50;

    /// <summary>
    /// Why a node drive is or is not landing, as one line for the log. Read-only apart from
    /// the IsDialogDelay call, which is a pure predicate.
    ///
    /// This exists because "SendDialogEntry returned success" and "the line arrived" are
    /// different facts, and the first was mistaken for the second once already.
    /// </summary>
    public static string DescribeDialogueDrive()
    {
        if (!TryGetActiveDialogue(out nint owner, out nint dialog))
        {
            return "no conversation here";
        }

        return $"owner 0x{*(uint*)(owner + ObjIdOffset):X}"
            + $" node {*(int*)(dialog + DialogCurrentEntryIndex)}/{*(int*)(dialog + DialogEntriesCount)}"
            + $" pending {*(int*)(owner + ObjDlgPendingFlag)}:{*(int*)(owner + ObjDlgPendingEntry)}"
            + $" sendGate {*(int*)(owner + ObjDlgSendGate)}"
            + $" isDialogDelay {IsDialogDelayFn(owner)}"
            + $" delay {*(uint*)(owner + ObjDlgDelayDay)}:{*(uint*)(owner + ObjDlgDelayMs)}"
            + $" last {*(uint*)(owner + ObjDlgLastDay)}:{*(uint*)(owner + ObjDlgLastMs)}";
    }

    /// <summary>
    /// The index of the dialogue entry on screen, or -1 when there is no conversation here or
    /// the index does not sit inside this instance's entry array.
    ///
    /// Bounds-checked against the dialog's OWN entry count, so a misread is published as
    /// "unknown" rather than as a node the peer would then be driven to. -1 is a first-class
    /// value on the wire for exactly that reason.
    /// </summary>
    public static int GetDialogueNodeIndex()
    {
        if (!TryGetActiveDialogue(out _, out nint dialog))
        {
            return -1;
        }

        int count = *(int*)(dialog + DialogEntriesCount);
        int index = *(int*)(dialog + DialogCurrentEntryIndex);
        return count is > 0 and <= MaxDialogueEntries && index >= 0 && index < count ? index : -1;
    }

    /// <summary>
    /// Drives the local conversation onto <paramref name="entryIndex"/> — the follower's half
    /// of node sync. False when there is no conversation here, when the index is not inside
    /// this instance's copy of the .dlg, or when we are already on that node.
    ///
    /// ⚠ ALREADY-THERE IS A REFUSAL, NOT A NO-OP WORTH DOING ANYWAY. Re-sending the current
    /// entry restarts the line and its voice-over, so a controller that drove every frame
    /// would hold the conversation on one node forever, stuttering its audio from the top —
    /// the same shape as the old blind pick replay, in a new place.
    ///
    /// ⚠ Bounds-checked against the LOCAL entry count. Both instances load the same file so
    /// the counts agree; the check is what turns a mismatched install (a mod on one side
    /// only) into a conversation that stops following, rather than the engine indexing a
    /// 0xB0-stride array past its end.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool DriveDialogueToNode(int entryIndex)
    {
        if (entryIndex < 0 || !TryGetActiveDialogue(out nint owner, out nint dialog))
        {
            return false;
        }

        int count = *(int*)(dialog + DialogEntriesCount);
        if (count is <= 0 or > MaxDialogueEntries || entryIndex >= count)
        {
            return false;
        }

        if (*(int*)(dialog + DialogCurrentEntryIndex) == entryIndex)
        {
            return false;
        }

        *(int*)(owner + ObjDlgSendScratch) = 0;

        int status = 0;
        SendDialogEntryFn(dialog, owner, -1, entryIndex, &status);

        // The status, not the return value, is what the engine's own caller tests. Callers
        // here verify by RE-READING the node anyway — a producer that reports success is
        // still not proof a line arrived, which is the lesson that produced this comment.
        return status != DialogEntryDeferred;
    }

    /// <summary>
    /// CClientExoAppInternal::BarkString(objectId, a2, a3, a4) — <c>ret 0x10</c>. The real
    /// implementation, and the point every bark converges on.
    ///
    /// ⚠ Called DIRECTLY, with the client internal as `this` — not through the
    /// <c>CServerExoApp::BarkString 0x005EDBB0</c> thunk. The thunk is
    /// <c>mov ecx,[ecx+4]; jmp here</c>, i.e. it expects a `this` whose +0x04 is a
    /// CClientExoAppInternal. Handing it the SERVER app (whose +0x04 is a server internal)
    /// makes this function run against an object of the wrong class, and the first
    /// vtable dispatch inside it goes somewhere arbitrary. That is not theoretical: the
    /// first version did exactly that, and the instance it was replayed on died with no
    /// crash-probe line and no event-log entry — the replay never even reached its own log
    /// statement. The internal is the same pointer <see cref="GetInGameGui"/> already walks
    /// to (<c>[client + 0x04]</c>).
    /// </summary>
    /// <summary>
    /// CClientExoAppInternal::BarkString(uint clientObjectId, CExoString* text,
    /// CResRef* sound, int ignoreRange) — <c>ret 0x10</c>, verified at 0x005F2CCD.
    ///
    /// ⚠ ARGUMENTS 2 AND 3 ARE POINTERS. Forwarding them across processes is what killed the
    /// engine twice; both are dereferenced on BOTH branches of 0x005F2CA8, so the
    /// speaker-not-found path is no safer. They are rebuilt locally instead — see
    /// <see cref="PlayBark"/> and docs/research/bark-replay.md.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, byte*, byte*, int, void> BarkStringFn =
        (delegate* unmanaged[Thiscall]<nint, uint, byte*, byte*, int, void>)0x005F2C90;

    /// <summary>CClientExoAppInternal, or 0. Exists from startup, menu included.</summary>
    private static nint GetClientInternal()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return 0;
        }

        nint internalApp = Deref(client + AppClientOffset);
        return LooksLikeHeapPointer(internalApp) ? internalApp : 0;
    }

    /// <summary>
    /// Replays a bark a peer's instance produced, on this instance's copy of the same
    /// creature. The arguments are the captured ones, forwarded verbatim — see
    /// <see cref="BarkHook"/> for why they are not interpreted.
    ///
    /// Bracketed with <see cref="BarkHook.Replaying"/> so the capture hook ignores the bark
    /// we are playing back; without it a bark would bounce between instances forever.
    ///
    /// <paramref name="suppressCapture"/> is false ONLY for the test hook, which needs a bark
    /// this instance produces to be captured exactly as a game-produced one is — that round
    /// trip is the only way the capture stub's stack arithmetic can be checked without a
    /// human playing the game, and it shipped wrong twice for want of it.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool PlayBark(
        uint speakerObjectId, ReadOnlySpan<byte> text, ReadOnlySpan<byte> resRef, uint flags,
        bool suppressCapture = true)
    {
        if (speakerObjectId is 0 or ObjectInvalid)
        {
            return false;
        }

        nint clientInternal = GetClientInternal();
        if (clientInternal == 0)
        {
            return false;
        }

        // The speaker has to exist here, or the engine resolves nothing and the bubble is
        // attached to a creature this instance does not have.
        if (ResolveServerObject(speakerObjectId) == 0)
        {
            return false;
        }

        // The engine's argument is the CLIENT id. ServerToClientObjectId (0x005F2AC0) is
        // literally `id == OBJECT_INVALID ? id : id | 0x80000000` — the top bit selects the
        // client half of the 0x2000-slot object array.
        uint clientObjectId = speakerObjectId | 0x80000000;

        // arg3: a CResRef is a fixed char[16], zero-filled — NOT a C string. An empty one is
        // an ordinary case (a line with no voice-over), so there is nothing to fall back to.
        byte* sound = stackalloc byte[BarkHook.ResRefBytes];
        for (int i = 0; i < BarkHook.ResRefBytes; i++)
        {
            sound[i] = i < resRef.Length ? resRef[i] : (byte)0;
        }

        // arg2: build a REAL CExoString with the engine's own constructor and free it after
        // the call — exactly what ExecuteCommandBarkString does at 0x00548A4A. SetBark
        // copy-constructs from it (0x006A9986), so the callee never takes ownership.
        int length = Math.Min(text.Length, BarkHook.TextMax);
        byte* chars = stackalloc byte[BarkHook.TextMax + 1];
        for (int i = 0; i < length; i++)
        {
            chars[i] = text[i];
        }

        chars[length] = 0;

        byte* line = stackalloc byte[8];
        *(ulong*)line = 0;

        BarkHook.Replaying = suppressCapture;
        try
        {
            CExoStringCtorFn(line, chars);
            try
            {
                BarkStringFn(clientInternal, clientObjectId, line, sound, (int)flags);
            }
            finally
            {
                CExoStringDtorFn(line);
            }
        }
        finally
        {
            BarkHook.Replaying = false;
        }

        return true;
    }

    /// <summary>
    /// Allows or forbids this client committing a dialogue reply.
    ///
    /// ⚠ DO NOT CALL — NOT EVEN WITH `true`. Kept, unused, because the address and the trap
    /// are worth not re-discovering.
    ///
    /// CClientExoAppInternal::SetCanSendDialog (0x005F3030, ret 4) writes the flag at +0x228
    /// AND STAMPS +0x22C WITH THE CURRENT WORLD TIME (0x005F305F). GetCanSendDialog
    /// (0x005F3070, ret 0xC) refuses while `now - [+0x22C] <= 1000` (0x005F316C), and a
    /// successful check back-dates the stamp to now-2000 so the gate stays open by itself.
    /// So there is no such thing as "setting it back to true": every call is a fresh
    /// ~1 second during which CSWSObject::SendDialogEntry (0x004CD460) DEFERS entries rather
    /// than sending them — no CSWSDialog::SendDialogEntry, therefore no
    /// CGuiInGame::HandleDialogSound (0x0062E1D0), therefore no voice-over and no lipsync.
    ///
    /// The engine re-stamps it only during area load (MainLoop 0x006031DB). Nothing in this
    /// mod sets it false, so there is nothing to restore.
    /// </summary>
#pragma warning disable IDE0051
    public static void SetCanSendDialog(bool allowed)
    {
        nint client = GetClient();
        if (client != 0)
        {
            SetCanSendDialogFn(client, allowed ? 1 : 0);
        }
    }
#pragma warning restore IDE0051

    /// <summary>
    /// True when the live conversation has actually been populated — a resolved participant
    /// or something on the panel. A conversation the client has marked active but which no
    /// entry ever reached reads as false, which is how a half-started one is detected.
    /// </summary>
    public static bool DialogueHasContent()
    {
        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui))
        {
            return false;
        }

        foreach (uint raw in stackalloc uint[] { *(uint*)(gui + GuiParticipantA), *(uint*)(gui + GuiParticipantB) })
        {
            uint id = raw & 0x7FFFFFFF;
            if (id is not (0 or ObjectInvalid or 0x7FFFFFFF))
            {
                return true;
            }
        }

        nint panel = GetDialoguePanel();
        return panel != 0 && *(int*)(panel + DialogPanelReplyCount) > 0;
    }

    /// <summary>
    /// Ends the local conversation (used when the owner ends theirs, when a follow times out,
    /// and when we lose the token race).
    ///
    /// ⚠ This goes through the SERVER, not through <c>CGuiInGame::CloseDialog</c> directly.
    /// Closing only the client UI leaves the server's CSWSDialog running: it keeps walking
    /// the tree on its own timers and, one line later, sends the next reply list to a client
    /// whose participants it has just set to OBJECT_INVALID. <c>ShowDialogReplies</c> then
    /// calls <c>UpdateShotCamera</c>, which resolves both participants with NO null check
    /// (0x006307DD and 0x006307FB) and faults on the second. That crash was caught live
    /// ~2 s after a close — exactly one dialogue line's worth of delay.
    ///
    /// <see cref="StopDialogFn"/> tears the server dialog down first and reaches
    /// <c>CloseDialog</c> through the engine's own <c>Cleanup</c>, so nothing is left to send.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    /// <summary>
    /// Shuts the dialogue UI directly, without going through the server at all.
    ///
    /// ⚠ LAST RESORT, and only for a conversation <see cref="CloseDialogue"/> has already
    /// failed to end — a conversation whose participants resolve, so CloseDialogue calls
    /// StopDialog and returns, but whose UI is still up afterwards. It adds nothing on the
    /// path where no participant resolves, because CloseDialogue already falls back to this
    /// same call there.
    ///
    /// It is NOT a cure for a wedged client: where the server half still holds an unfinished
    /// conversation, this clears the flag and the next frame sets it straight back. That case
    /// belongs to <see cref="CloseDialogue"/>, which stops the dialog on the player itself.
    /// </summary>
    public static void ForceCloseDialogueUi()
    {
        nint gui = GetInGameGui();
        if (gui != 0 && ConversationLive(gui))
        {
            CloseDialogFn(gui);
        }
    }

    public static void CloseDialogue()
    {
        nint gui = GetInGameGui();
        if (gui == 0 || !ConversationLive(gui))
        {
            return;
        }

        // The GUI names both participants; either may be the owner. Prefer the owner (one
        // call does the whole teardown) and settle for a participant (which routes to the
        // owner through RemoveObjectFromDialog) when the owner is not one of the two.
        nint participant = 0;

        foreach (uint raw in stackalloc uint[] { *(uint*)(gui + GuiParticipantA), *(uint*)(gui + GuiParticipantB) })
        {
            uint id = raw & 0x7FFFFFFF; // the fields carry flag bits above the id
            if (id is 0 or ObjectInvalid or 0x7FFFFFFF)
            {
                continue;
            }

            nint obj = ResolveServerObject(id);
            if (obj == 0)
            {
                continue;
            }

            uint owner = *(uint*)(obj + ObjDialogOwner);
            if (owner == ObjectInvalid)
            {
                continue; // this one is not in a conversation as far as the server knows
            }

            if (owner == *(uint*)(obj + ObjIdOffset))
            {
                StopDialogFn(obj);
                return;
            }

            participant = obj;
        }

        if (participant != 0)
        {
            StopDialogFn(participant);
            return;
        }

        // ⚠ TRIED AND REVERTED: stopping the dialog on the PLAYER here as a last resort
        // (self+0x54 naming a dialog owner). The theory was that a conversation the server
        // started but the client never finished opening leaves the GUI naming 0x7F000000
        // twice while the player's own object still holds the dialog. It did NOT clear the
        // wedge — the flag came straight back on the next frame — and +0x54 is not reliably
        // reset to OBJECT_INVALID when a conversation ends, so the fallback fires on
        // conversations that are already over. Do not reintroduce it without first
        // establishing what re-sets gui+0xB4 every frame in that state.

        // No resolvable server side left — the conversation is a client-only shell (the
        // half-started case the follow timeout exists for). Closing the UI is then both
        // safe and the only thing left to do: there is no dialog to keep sending.
        CloseDialogFn(gui);
    }

    /// <summary>CClientExoApp::GetGameObject(objectId) — the CLIENT's object lookup, the one
    /// UpdateShotCamera itself uses. Returns 0 for an id the client does not hold.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> ClientGetGameObjectFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x005ED580;

    /// <summary>
    /// True when both of a CGuiInGame's dialogue participants still resolve to client
    /// objects — the precondition <c>CGuiInGame::UpdateShotCamera</c> assumes and never
    /// checks. Asked with the RAW field values, flag bits and all, because that is exactly
    /// what the engine passes (0x006307D8 / 0x006307F6).
    ///
    /// MAIN THREAD ONLY. Used by <see cref="ShotCameraGuardHook"/>.
    /// </summary>
    public static bool ShotCameraParticipantsResolvable(nint gui)
    {
        if (gui == 0)
        {
            return true; // nothing we can judge; let the engine run
        }

        nint client = GetClient();
        if (client == 0)
        {
            return true;
        }

        return ClientGetGameObjectFn(client, *(uint*)(gui + GuiParticipantA)) != 0
            && ClientGetGameObjectFn(client, *(uint*)(gui + GuiParticipantB)) != 0;
    }

    /// <summary>Resolves an object id to its live CSWSDialog*, or 0 (vtable-guarded).</summary>
    private static nint FindActiveDialog(uint objectId)
    {
        if (objectId is 0 or ObjectInvalid)
        {
            return 0;
        }

        nint obj = ResolveServerObject(objectId);
        if (obj == 0)
        {
            return 0;
        }

        nint dialog = Deref(obj + ObjActiveDialog);
        return LooksLikeHeapPointer(dialog) && *(nint*)dialog == DialogVtable ? dialog : 0;
    }

    /// <summary>Copies a CExoString {char* @0, len @4} at <paramref name="field"/> into an
    /// ASCII buffer. <paramref name="field"/> is a struct member address (already valid).</summary>
    private static void ReadExoStringInto(nint field, byte* dest, int cap)
    {
        if (cap <= 0)
        {
            return;
        }

        dest[0] = 0;
        CopyAsciiBounded(*(nint*)field, *(int*)(field + 4), dest, cap);
    }

    /// <summary>
    /// Copies the first inline substring of a CExoLocString at <paramref name="field"/> into
    /// an ASCII buffer. Chain (docs/research/name-plates.md): loc+0 -&gt; internal ; internal+4
    /// count ; internal+0 list* ; list+0 head ; head+8 entry ; entry+4 char* ; entry+8 cap.
    /// A StrRef-only line (no substring) leaves the buffer empty. Every hop is guarded.
    /// </summary>
    private static void ReadLocStringInto(nint field, byte* dest, int cap)
    {
        if (cap <= 0)
        {
            return;
        }

        dest[0] = 0;

        nint intern = *(nint*)field;
        if (!LooksLikeHeapPointer(intern))
        {
            return;
        }

        int subCount = *(int*)(intern + 4);
        if (subCount is <= 0 or > 64)
        {
            return; // StrRef-only (or implausible) -> nothing inline to read
        }

        nint list = *(nint*)intern;
        if (!LooksLikeHeapPointer(list))
        {
            return;
        }

        nint head = *(nint*)list;
        if (!LooksLikeHeapPointer(head))
        {
            return;
        }

        nint entry = *(nint*)(head + 8);
        if (!LooksLikeHeapPointer(entry))
        {
            return;
        }

        int capacity = *(int*)(entry + 8); // strlen + 1
        CopyAsciiBounded(*(nint*)(entry + 4), capacity > 0 ? capacity - 1 : 0, dest, cap);
    }

    /// <summary>Copies up to <paramref name="len"/> printable ASCII bytes from a guarded char
    /// pointer into <paramref name="dest"/>, null-terminated. Bounds every length.</summary>
    private static void CopyAsciiBounded(nint chars, int len, byte* dest, int cap)
    {
        dest[0] = 0;
        if (!LooksLikeDataPointer(chars) || len <= 0)
        {
            return;
        }

        if (len > cap - 1)
        {
            len = cap - 1;
        }

        if (len > 512)
        {
            len = 512; // hard ceiling regardless of a garbage capacity field
        }

        int n = 0;
        for (int i = 0; i < len; i++)
        {
            byte b = *(byte*)(chars + i);
            if (b == 0)
            {
                break;
            }

            dest[n++] = b is >= 0x20 and < 0x7F ? b : (byte)'?';
        }

        dest[n] = 0;
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

    // ---- render window activation -------------------------------------------------
    //
    // A launched instance comes up with its render window NEVER ACTIVATED, and draws
    // nothing until the player clicks into it — the reported "one of the two windows is
    // black until I focus it". Measured on the live pair: both instances sat at 0 straight
    // after launch, focusing one took it to 1, and focusing the OTHER left the first at 1.
    // So it is a latch, not a focus mirror: activating once per launch is enough, and
    // nothing turns it off again in normal play.

    /// <summary>Global: nonzero once the render window has been activated.</summary>
    private const nint RenderWindowActiveFlag = 0x007A3A38;

    /// <summary>
    /// Global::ActivateRenderWindow() — __stdcall, NO arguments (0x00401E83 is a plain
    /// `ret`). Sets the flag above, and, when the server exists and is not already running,
    /// clears the unfocused pause reason through SetPauseState(2, 0) — the same reason
    /// FocusKeeper clears. It early-outs when the flag is already set, so calling it
    /// repeatedly is free.
    /// </summary>
    private static readonly delegate* unmanaged[Stdcall]<void> ActivateRenderWindowFn =
        (delegate* unmanaged[Stdcall]<void>)0x00401E00;

    /// <summary>True once this instance's render window has been activated.</summary>
    public static bool IsRenderWindowActive => *(int*)RenderWindowActiveFlag != 0;

    /// <summary>
    /// Activates the render window if it has not been. Returns true when this call did it,
    /// so a caller can log the once-per-launch edge.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool EnsureRenderWindowActive()
    {
        // The function dereferences the app manager; before it exists there is nothing to
        // activate and the call would fault.
        if (Deref(AppManagerPtr) == 0 || IsRenderWindowActive)
        {
            return false;
        }

        ActivateRenderWindowFn();

        return IsRenderWindowActive;
    }

    // ---- cinematics: the Bink movie player ------------------------------------
    //
    // See docs/research/cinematics.md. Everything here is READ-ONLY on purpose: the whole
    // point of these accessors is to let the plugin SAY what the engine did during the
    // seconds it was not being called at all, not to steer the movie player.
    //
    // The load-bearing fact behind them: while a pre-rendered movie plays,
    // CClientExoAppInternal::WaitForMovie (0x005F6F20) spins the MAIN THREAD in a loop at
    // 0x005F7063-0x005F70B5 that runs nothing but Global::messagepump, so
    // CServerExoApp::MainLoop is never reached, GDI SwapBuffers is never called, and NEITHER
    // of the plugin's frame hooks fires. A movie is therefore observable only from its edges.

    /// <summary>
    /// CClientExoApp::IsMoviePlaying 0x005EDB40 — <c>__thiscall</c>, NO arguments.
    ///
    /// Argument size checked against the callee's own <c>ret</c> per docs/research/engine-api.md:
    /// the whole chain is 0x005EDB40 → 0x005F2BD0 → 0x0070C1F0 → 0x00404DD0, and 0x00404DD0 is
    /// literally <c>mov eax,[ecx+0x30]; ret</c> — a bare <c>ret</c>, so the argument size is 0.
    /// Every hop is a pointer load with a null guard at 0x005F2BD8, which is why this is safe
    /// to call on any frame, world or menu.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> IsMoviePlayingFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x005EDB40;

    /// <summary>
    /// <c>CClientExoAppInternal::move_to_module_movies</c> — the <c>CExoArrayList&lt;CExoString&gt;</c>
    /// of movies a module transition is going to play, named in the DB's own offsets table.
    ///
    /// NWScript <c>StartNewModule(module, waypoint, movie1..movie6)</c>
    /// (CSWVirtualMachineCommands::ExecuteCommandStartNewModule 0x00544390) pushes its six
    /// movie arguments into this list through AddMoveToModuleMovie 0x005EDB50, and
    /// CServerExoAppInternal::StartNewModule 0x004BA920 drains it with PlayLoadModuleMovies
    /// 0x005EDB20 while the new module loads. So a non-empty list is the ONE pre-warning the
    /// engine gives that a cinematic is about to take the main thread away.
    /// </summary>
    private const int ClientMoveToModuleMovies = 0x2D8;

    /// <summary>CExoArrayList layout, from the DB: <c>+0x00</c> data, <c>+0x04</c> size,
    /// <c>+0x08</c> capacity.</summary>
    private const int ArrayListData = 0x00;
    private const int ArrayListSize = 0x04;

    /// <summary>CExoString layout, from the DB: <c>+0x00 char*</c>, <c>+0x04</c> length —
    /// and that length INCLUDES the terminating null (see the CExoString length trap), which
    /// is why the reader below cuts at the first null rather than trusting it.</summary>
    private const int ExoStringSize = 0x08;

    /// <summary>
    /// True while the engine's Bink player has a movie up.
    ///
    /// ⚠ In a loaded module this practically never reads true from a frame callback, and that
    /// is the finding rather than a defect: an in-world movie blocks the main thread inside
    /// WaitForMovie, so no callback runs while it is true. It DOES read true at the main menu,
    /// whose looping movie is played asynchronously and is the reason the SwapBuffers frame
    /// hook is menu-dormant (docs/research/menu-frame-hook.md).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool IsMoviePlaying()
    {
        nint client = GetClient();
        return client != 0 && IsMoviePlayingFn(client) != 0;
    }

    /// <summary>
    /// How many movies the pending module transition has queued. Zero in the ordinary case.
    /// A pure read of two pointers deep; no engine call, so it is cheap enough for every frame.
    /// </summary>
    public static int QueuedTransitionMovieCount()
    {
        nint internalClient = GetClientInternal();
        if (internalClient == 0)
        {
            return 0;
        }

        int size = *(int*)(internalClient + ClientMoveToModuleMovies + ArrayListSize);

        // A list this long is not a list, it is a torn or wrong read. Six is the engine's own
        // ceiling (StartNewModule takes exactly six movie arguments); allow a little slack and
        // refuse anything past it rather than walk off into memory below.
        return size is > 0 and <= 16 ? size : 0;
    }

    /// <summary>
    /// Copies the name of queued transition movie <paramref name="index"/> into
    /// <paramref name="destination"/> as ASCII, and returns how many bytes were written.
    ///
    /// Cuts at the first null deliberately: a CExoString's length field counts the terminator,
    /// and trusting it has produced three separate off-by-one bugs in this codebase already.
    /// </summary>
    public static int ReadQueuedTransitionMovie(int index, byte* destination, int capacity)
    {
        if (destination == null || capacity <= 0 || index < 0 || index >= QueuedTransitionMovieCount())
        {
            return 0;
        }

        nint internalClient = GetClientInternal();
        if (internalClient == 0)
        {
            return 0;
        }

        nint data = Deref(internalClient + ClientMoveToModuleMovies + ArrayListData);
        if (data == 0)
        {
            return 0;
        }

        nint text = Deref(data + (index * ExoStringSize));
        if (text == 0)
        {
            return 0;
        }

        int written = 0;
        while (written < capacity)
        {
            byte c = *(byte*)(text + written);
            if (c == 0)
            {
                break;
            }

            destination[written++] = c;
        }

        return written;
    }

    // ---- cinematics: WRITING the transition queue (the staging instrument) -----
    //
    // Everything above this line is read-only. The two calls below are the exception, and
    // they exist for one purpose: to let a live test stage a movie-carrying module
    // transition with nobody at the controls. See docs/research/cinematics.md §7.
    //
    // Read the consequence before calling either of them. A queued transition movie is
    // played by CServerExoAppInternal::StartNewModule 0x004BA920 (via PlayLoadModuleMovies
    // 0x005EDB20), which parks the MAIN THREAD inside WaitForMovie 0x005F6F20 for the length
    // of the film. Neither frame hook fires in that window, so nothing this plugin does
    // happens during it — including the pre-store flush that keeps plugin-spawned bodies out
    // of the saved module. These are therefore guarded by MovieStaging, not called directly.

    /// <summary>
    /// CClientExoApp::AddMoveToModuleMovie 0x005EDB50 — <c>__thiscall</c>, ONE argument
    /// (<c>CExoString*</c>), returning the engine's own verdict.
    ///
    /// Argument size checked against the callee's own <c>ret</c> per docs/research/engine-api.md:
    /// 0x005EDB50 is the usual <c>mov ecx,[ecx+4]; jmp</c> adjustor thunk onto 0x006027A0,
    /// which is <c>mov edx,[esp+4]; lea eax,[ecx+0x2D8]; push eax; push edx;
    /// call 0x005FBBF0; <b>ret 4</b></c> — one 4-byte argument, and EAX falls straight out of
    /// the callee, which is why this is declared as returning <c>int</c> rather than void.
    ///
    /// That callee is `CClientExoAppInternal::AddMovieToExoArrayList 0x005FBBF0`, and its
    /// return value is worth having: it refuses an empty name (`CExoString::operator!=` against
    /// "" at 0x005FBC25, returning 0 at 0x005FBF39) and, for a name containing no <c>:</c>,
    /// builds <c>MOVIES:%s</c> (0x0073D7D8) and tries to open the file — returning 0 and adding
    /// NOTHING when it cannot. So a name this build has no <c>.bik</c> for is refused by the
    /// engine itself, and it tells us so. (That upgrades cinematics.md's INFERRED note about
    /// this guard to VERIFIED, and explains why the retail game gets away with referencing
    /// movies it does not ship.) The add itself is
    /// `CExoArrayList&lt;CExoString&gt;::AddUnique 0x005F7C10`, which copies the string — so
    /// the temporary below can be destroyed immediately.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, int> AddMoveToModuleMovieFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, int>)0x005EDB50;

    /// <summary>
    /// CClientExoApp::RemoveMoveToModuleMovies 0x005EE380 — <c>__thiscall</c>, NO arguments
    /// (the whole function is <c>mov eax,[ecx+4]; mov [eax+0x2DC],0; ret</c> — a bare
    /// <c>ret</c>, so the argument size is 0). It zeroes the array list's SIZE field, which is
    /// exactly what the engine itself does on the way out of WaitForMovie.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> RemoveMoveToModuleMoviesFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005EE380;

    /// <summary>True once the client application object exists. Cheaper and earlier than
    /// <see cref="HasServer"/>: the client is what owns the movie player and the transition
    /// movie queue.</summary>
    public static bool HasClient => GetClient() != 0;

    /// <summary>
    /// Appends one movie name to the engine's move-to-module queue, and returns whether the
    /// ENGINE accepted it — not whether we asked. A refusal means the name is empty or this
    /// install has no such file, and reporting our own optimism instead would produce a
    /// transition with no cinematic and a log claiming otherwise.
    ///
    /// The <c>CExoString</c> is built by the engine's own <c>CStrConstructor 0x005E5A90</c>
    /// and freed by <c>Destructor_2 0x005E5C20</c>, deliberately rather than being assembled
    /// by hand: a hand-built one has to get the length convention right, and a CExoString's
    /// length INCLUDES the terminating null — the trap that has produced three separate bugs
    /// in this codebase already.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool QueueTransitionMovie(byte* ascii, int length)
    {
        nint client = GetClient();
        if (client == 0 || ascii == null || length <= 0 || length > 16)
        {
            return false;
        }

        byte* name = stackalloc byte[24];
        for (int i = 0; i < length; i++)
        {
            name[i] = ascii[i];
        }

        name[length] = 0;

        byte* text = stackalloc byte[8];
        for (int i = 0; i < 8; i++)
        {
            text[i] = 0;
        }

        CExoStringCtorFn(text, name);
        int accepted = AddMoveToModuleMovieFn(client, text);
        CExoStringDtorFn(text);

        return accepted != 0;
    }

    /// <summary>
    /// Empties the transition movie queue. The way out of a staging run that is not going to
    /// happen — an armed queue left behind would attach a cinematic to whatever transition
    /// came next, hours later, with nothing in the log to connect the two.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ClearQueuedTransitionMovies()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        RemoveMoveToModuleMoviesFn(client);
        return true;
    }

    /// <summary>The player's own pause toggle. See EngineGlobals on the agent side; the
    /// shared-pause mirror uses this same bit so either player can pause both worlds.</summary>
    public const int PauseReasonPlayer = 0x01;

    // CClientExoApp::SetPauseState(int, int, int) — THREE arguments, not two. ⚠ DO NOT CALL.
    //
    // The recorded reason not to call it was that its tail walks a notify list only valid on
    // the engine's own call path, after it crashed both instances at 0x5E9BC9 (2026-08-01).
    // That explanation was incomplete: the declaration was also WRONG. The implementation
    // (0x005ED650 is a thunk to 0x005F7FA0) ends in `ret 0xc` at 0x005F8285 and reads a third
    // argument at 0x005F822A, so the two-argument call left the stack skewed by four bytes
    // every time — reason enough on its own for a crash somewhere else entirely.
    //
    // Left uncalled, but now declared honestly, because a signature kept "for documentation"
    // that documents the wrong thing is worse than none. SetPlayerPause assembles the client
    // half from the individually-verified pieces instead.
#pragma warning disable IDE0051, CS0414
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int, void> ClientSetPauseStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, int, void>)0x005ED650;
#pragma warning restore IDE0051, CS0414

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

        bool clientWas = (GetClientPauseMask() & PauseReasonPlayer) != 0;
        bool serverWas = (GetPauseMask() & PauseReasonPlayer) != 0;
        bool changed = clientWas != paused || serverWas != paused;

        // The SERVER half goes through the engine's own setter — it drives the server world
        // timer and the simulation stop, and that entry has run from this plugin for days
        // (FocusKeeper) without incident.
        nint server = GetServer();
        if (server != 0)
        {
            SetPauseStateFn(server, PauseReasonPlayer, value);
        }

        // The CLIENT half must NOT go through CClientExoApp::SetPauseState. Measured live
        // (2026-08-01): a direct call crashes in the notify walk at its tail
        // (0x5F8240..0x5F825C), which iterates a list that is only valid on the engine's
        // own call path — both instances died at 0x5E9BC9 within 65 ms of each other when
        // the space handler here and the mirror on the peer used it. Everything that call
        // maintains is individually safe with a verified body, so assemble it by hand:
        // the mask byte is a plain flag, the world-timer pause is a one-field function,
        // and the banner/GUI belief has its own setter.
        nint clientInternal = GetClient() == 0 ? 0 : Deref(GetClient() + AppClientOffset);
        if (clientInternal != 0)
        {
            byte* mask = (byte*)(clientInternal + ClientPauseMaskOffset);
            if (paused)
            {
                *mask |= PauseReasonPlayer;
            }
            else
            {
                *mask &= unchecked((byte)~PauseReasonPlayer);
            }
        }

        nint client = GetClient();
        if (client != 0)
        {
            nint timer = ClientGetWorldTimerFn(client);
            if (timer != 0)
            {
                if (paused)
                {
                    PauseWorldTimerFn(timer);
                }
                else
                {
                    UnpauseWorldTimerFn(timer);
                }
            }
        }

        nint gui = GetInGameGui();
        if (gui != 0 && *(int*)(gui + 0x108) != 0)
        {
            SetPauseVisualsFn(gui, value, PauseReasonManual);
        }

        return changed;
    }

    /// <summary>The reason byte whose banner is the plain "PAUSED" (reason 1 would read
    /// "ENEMY SIGHTED!" — the label jump table at 0x6C02BC maps 4 to the default strref).</summary>
    private const int PauseReasonManual = 4;

    /// <summary>True when the player-pause reason is held on either half.</summary>
    public static bool IsPlayerPaused =>
        ((GetPauseMask() | GetClientPauseMask()) & PauseReasonPlayer) != 0;

    // ---- pause-state reconciliation ---------------------------------------------
    //
    // The pause feature has THREE state holders that only ever move on edges: the two
    // pause-reason masks (client + server), the world timers' paused flags, and the GUI's
    // own belief at CGuiInGame+0xB3C — which is re-derived every frame from the CLIENT
    // world timer's flag, arms the pause panel, and routes the space bar to "unpause"
    // while set. SetPauseState early-outs when the mask bit already agrees, so a single
    // missed edge (a raw mask write, a swallowed loopback message, a timer paused outside
    // the setter) strands the holders in disagreement PERMANENTLY: the banner cannot
    // clear and the space bar's unpause no-ops forever. Measured live on both instances.
    // The cure is to be level-triggered somewhere: this reconciler drives every holder
    // back to what the masks say, through the engine's own idempotent setters.

    /// <summary>CGuiInGame + this = the GUI's own "the game is paused" belief.</summary>
    private const int GuiBelievesPaused = 0xB3C;

    /// <summary>CWorldTimer + this = 1 while paused. A flag, not a counter.</summary>
    private const int WorldTimerPausedFlag = 0x24;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> ClientGetWorldTimerFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED4F0;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> ServerGetWorldTimerFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AEDE0;

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

    /// <summary>
    /// Drives the world timers and the GUI's pause belief back into agreement with the
    /// pause masks. Returns true when something had to be repaired.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ReconcilePause(out string detail)
    {
        detail = "";

        nint client = GetClient();
        nint server = GetServer();
        nint gui = GetInGameGui();
        if (client == 0 || server == 0 || gui == 0 || *(int*)(gui + 0x108) == 0)
        {
            return false;
        }

        // Never touch anything mid-load: the login chain pauses these timers ON PURPOSE
        // for the duration of a module load, and resumes them itself at presentation.
        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0
            || *(int*)(internalServer + 0x100B4) != 0     // module load in progress
            || *(ushort*)(internalServer + 0x10008) != 2) // server mode: 2 = world running
        {
            return false;
        }

        int union = GetPauseMask() | GetClientPauseMask();
        bool guiBelieves = *(int*)(gui + GuiBelievesPaused) != 0;
        bool repaired = false;

        // ⚠ THE BANNER IS THE ENGINE'S OWN INTENT, AND IT WINS.
        //
        // Measured 2026-08-11, and it is the whole reason the pause button looked dead:
        // the engine's own pause paths (the space handler, the HUD button) move the GUI
        // belief and the world timers but DO NOT SET A MASK BIT. This reconciler used to
        // read that as "stranded holders" and tear the banner down within ~60 ms —
        //
        //     .600  reconciler UNPAUSED (banner) — no mask held a bit (server 0x00, client 0x00)
        //     .744  space fallback -> paused
        //
        // — so pressing space only ever worked because PauseKeyController re-paused it 200 ms
        // later, and clicking the HUD button, which has no such fallback, did nothing at all.
        // We had reinvented the very bug PauseSanityPatch exists to delete, on our own side.
        //
        // So when the masks and the banner disagree, ADOPT the banner instead of cancelling
        // it: write the mask the engine forgot. That turns an engine pause into a real,
        // mask-backed pause — which is also what makes it publishable, so a HUD-button pause
        // now reaches the other player like any other.
        //
        // Adoption is deliberately restricted to the case where NO reason bit is held (or
        // only the player's own). A script pause raises its own bit and shows the same
        // banner, and adding the player bit on top of it would leave the game stuck paused
        // after the script cleared its own.
        if (union == 0 && guiBelieves)
        {
            SetPlayerPause(true);
            detail = "ADOPTED an engine pause (banner up, no mask) — now mask-backed and shareable";
            return true;
        }

        if (union == PauseReasonPlayer && !guiBelieves)
        {
            // The mirror image: an engine path cleared the banner but left our mask set.
            // Adopting the unpause is what lets a mouse click release a pause at all — there
            // is no key fallback on that route to notice.
            SetPlayerPause(false);
            detail = "ADOPTED an engine unpause (banner down, mask still held)";
            return true;
        }

        if (union == 0)
        {
            // No banner and no mask, but a timer is still stopped: a genuine stray, from the
            // login chain or a swallowed edge. Resume it.
            nint clientTimer = ClientGetWorldTimerFn(client);
            if (clientTimer != 0 && *(int*)(clientTimer + WorldTimerPausedFlag) != 0)
            {
                UnpauseWorldTimerFn(clientTimer);
                detail += "client timer, ";
                repaired = true;
            }

            nint serverTimer = ServerGetWorldTimerFn(server);
            if (serverTimer != 0 && *(int*)(serverTimer + WorldTimerPausedFlag) != 0)
            {
                UnpauseWorldTimerFn(serverTimer);
                detail += "server timer, ";
                repaired = true;
            }

            if (repaired)
            {
                detail = $"resumed a stray stopped timer ({detail.TrimEnd(',', ' ')}), "
                    + "no banner and no mask held";
            }
        }
        else if ((union & PauseReasonPlayer) != 0 && !guiBelieves)
        {
            // A player pause held ALONGSIDE some other reason, with no banner: the show edge
            // was missed. Restoring the banner is right here — the pause is genuinely held by
            // something we must not clear.
            SetPauseVisualsFn(gui, 1, PauseReasonManual);
            detail = $"restored the banner for a held pause (mask 0x{union:X2})";
            repaired = true;
        }

        return repaired;
    }

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

    /// <summary>repute.2da row 5 — the Neutral faction, ~50 reputation both ways.</summary>
    private const ushort FactionNeutral = 5;

    /// <summary>The same, for callers choosing between it and <see cref="FactionPlayer"/>.</summary>
    public const int FactionNeutralId = FactionNeutral;

    /// <summary>
    /// Marks a spawned creature as belonging to a remote player.
    ///
    /// The faction is FORCED to Neutral rather than trusted from the blueprint. The stock
    /// `n_commm` ships Neutral, but a mod that edits its faction — or a co-op blueprint
    /// with a different one — would make enemies evaluate the phantom as a real target
    /// and, combined with min-1-hp below, recreate the immortal aggro sink 7.4 removed.
    /// The forced write costs a pointer read and makes the property independent of what is
    /// installed.
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

        nint stats = Deref(phantom + CreStats);
        if (stats != 0)
        {
            // DWORD, not ushort. The engine reads the whole dword (0x00513FF8,
            // 0x005BFADB); the old 16-bit write only worked because the upper half
            // happened to be zero, and a stale upper half would make the faction id read
            // as garbage — which resolves to reputation 50 and silently un-targets
            // everything (docs/research/combat-participation.md risk 4).
            *(uint*)(stats + StatsFactionId) = FactionNeutral;
        }
    }

    /// <summary>
    /// Marks a body materialised as a stand-in for an enemy that exists only in the owning
    /// peer's instance (see MaterialisedEnemies). Identical discipline to
    /// <see cref="MarkAsRemotePlayer"/> — no plot (plot zeroes incoming damage and the
    /// local player must be able to fight it), min-1-hp (only the owner may kill it), no
    /// selecting the corpse (its loot exists only in the owner's world), no swinging on
    /// its own (its swings are replayed from the owner's copy) — EXCEPT the faction, which
    /// the caller applies from the owner's published value rather than forcing Neutral: a
    /// materialised Sith trooper must read hostile or the fight it brought is invisible.
    /// </summary>
    // ---- combat state ------------------------------------------------------------

    /// <summary>
    /// CSWSCreature::SetCombatState(int inCombat, byte notify) — the engine's own writer of
    /// the in-combat dword at +0x4E0.
    ///
    /// TWO stack arguments, established from the body rather than the symbol row (which
    /// claims a nonsensical 5 bytes): after the SEH prologue and three pushes, 0x004F2648
    /// reads the requested state from `[esp+0x3C]` and 0x004F2655 reads a byte flag from
    /// `[esp+0x40]` — the incoming arg1 and arg2. It compares the request against the
    /// current field, returns early when they already agree, and 0x004F2687 is the only
    /// write to +0x4E0 in the function.
    ///
    /// It is a SETTER, not a re-derivation: it takes the state it is told. That is what
    /// makes replicating combat possible at all.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, byte, void> SetCombatStateFn =
        (delegate* unmanaged[Thiscall]<nint, int, byte, void>)0x004F2610;

    /// <summary>CSWSCreature + this = the in-combat dword.</summary>
    private const int CreInCombat = 0x4E0;

    /// <summary>True when the local PC's engine has it in combat.</summary>
    public static bool IsPlayerInCombat
    {
        get
        {
            nint player = GetPlayerCreaturePointer();
            return player != 0 && *(int*)(player + CreInCombat) != 0;
        }
    }

    /// <summary>
    /// Drives the local PC's combat state through the engine's own setter. Returns true
    /// when this call changed something, so a caller can log the edge rather than every
    /// re-assertion.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetPlayerCombatState(bool inCombat)
    {
        nint player = GetPlayerCreaturePointer();
        if (player == 0)
        {
            return false;
        }

        if ((*(int*)(player + CreInCombat) != 0) == inCombat)
        {
            return false;
        }

        // notify = 1: the same value the engine's own callers pass, so the party broadcast
        // and the HUD follow the change instead of the field moving under them.
        SetCombatStateFn(player, inCombat ? 1 : 0, 1);

        return (*(int*)(player + CreInCombat) != 0) == inCombat;
    }

    public static void MarkAsMirroredEnemy(nint creature)
    {
        *(int*)(creature + ObjPlot) = 0;
        *(int*)(creature + ObjMinOneHitPoint) = 1;
        *(int*)(creature + ObjDeadSelectable) = 0;

        // ⚠ THE ATTACK BIT IS PERMANENT ONCE CLEARED. ClaimCreature ORs bit 0x02 back on
        // every pass but never 0x04, so a body marked here can never swing again for as long
        // as it lives. That is right when this instance only wants a stand-in — and fatal
        // under local_combat, where these materialised bodies are precisely the enemies
        // (host encounter spawns, scripted waves) expected to do the fighting: they would
        // hold a target, queue attacks, deal nothing, and IncomingDamageController would
        // absorb the owner's report for them. The player would be invulnerable, silently.
        if (!EnemyPresenceController.LocalCombat)
        {
            *(int*)(creature + CreMovePermitted) &= ~CreAttackPermittedBit;
        }
    }

    /// <summary>
    /// Restores a mirrored enemy's ability to attack. Re-asserted every drive pass under
    /// local_combat, because a body can be marked once at spawn and live for the rest of the
    /// module — a spawn-time decision cannot be trusted to still hold.
    /// </summary>
    /// <param name="bisectMask">ROUND 47: 0 (default) is today's unconditional behaviour. See
    /// the bit table on <see cref="ClaimCreature(nint, bool, uint)"/>.</param>
    public static void PermitAttacking(nint creature, uint bisectMask = 0)
    {
        if ((bisectMask & BisectBitPermitAttack) == 0)
        {
            *(int*)(creature + CreMovePermitted) |= CreAttackPermittedBit;
        }
    }

    /// <summary>CSWSCreature + this = its CSWSCombatRound sub-object. VERIFIED: the field
    /// AddAttackAction below is called through (<c>mov ecx, dword ptr [esi + 0x9c8]</c> at
    /// <c>0x004FDE7C</c>, inside the engine's own <c>CSWSCreature::AddAttackActions</c>).</summary>
    private const int CreCombatRound = 0x9C8;

    /// <summary>
    /// CSWSCombatRound::AddAttackAction(uint targetId, int, int, int nDelayMs, int) —
    /// queues one swing at <c>targetId</c> on the round's pending-action list. VERIFIED by
    /// disassembly at <c>0x004D38B0</c>: resolves the target via <c>GetGameObject</c>
    /// (<c>0x004AE750</c>, matching arg1 = the id at <c>[esp+8]</c>), checks it is alive
    /// (<c>vtbl+0x94</c>, <c>CSWSCreature::GetDead</c>) and not a closed door/destroyed
    /// placeable (<c>vtbl+0x18</c> then <c>+0x2CC</c>), then unconditionally allocates a new
    /// 0x88-byte action node and appends it — there is no "already queued" check inside this
    /// function, which is why <see cref="QueueAttackAction"/> gates on the head action type
    /// before calling it rather than calling it every pass. Cross-confirmed against
    /// `docs/research/combat-participation.md` §1.2, which independently documents this same
    /// address and signature from the CALLER side (<c>CreateNewAttackActions</c>'s
    /// re-validation branch: "If the target survives validation, AddAttackAction(round,
    /// targetId, 0, 0, 0x5DC, 1) queues the next swing and the lock continues").
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, int, int, int, void> AddAttackActionFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, int, int, int, void>)0x004D38B0;

    /// <summary>
    /// CSWSCombatRound + this = the round; a small, two-arg setter at <c>0x004D28F0</c>.
    /// VERIFIED by disassembly of the function itself AND cross-checked against three real,
    /// byte-identical call sites in the engine's own combat-decision code
    /// (<c>0x005BC61E</c>/<c>0x005BC67D</c>/<c>0x005BC6AD</c>, all <c>push &lt;objectId&gt;;
    /// push 1; call 0x4D28F0</c>): unconditionally writes <c>round+0x95c = arg2</c> then
    /// <c>round+0x958 = arg1</c>, in that order, with no branch that skips either store when
    /// arg1 is nonzero.
    ///
    /// This is the actual missing initiation step, found one round after
    /// <see cref="AddAttackActionFn"/> was confirmed to append correctly: `round+0x9B0` being
    /// non-null (VERIFIED live) only means the action is ON the list — `UpdateCombatRoundTimer`
    /// (<c>0x004FAF20</c>, called unconditionally every tick for every creature, no
    /// combat-state or mirrored gate at its call site) reads `round+0x958` and, if it's still
    /// the zero `StartCombatRound` (`0x004D5F70`) itself leaves it at, takes a bare
    /// `IncrementTimer`-only fallback (`0x004FB0D3`) that never touches the queued action at
    /// all — that fallback is the actual silent-discard mechanism, not anything inside
    /// `AddAttackAction`. This call is what the engine's own code makes, in the same function
    /// that calls `StartCombatRound`, to flip `+0x958` nonzero afterward so the real
    /// processing branch runs instead.
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
    /// This is the actual answer to why the previous round's fix (this call was missing
    /// entirely) still did not work, live-confirmed by disassembling <c>UpdateCombatRoundTimer</c>
    /// (<c>0x004FAF20</c>) past its <c>round+0x958</c> gate: passing that gate is not enough.
    /// A SECOND check follows — <c>ebx = round+0x960 - creature+0xCC</c> (creature's own
    /// current-time-ish field) — and only when that is POSITIVE does the function reach the
    /// real action-processing call (<c>0x005B8050</c>, via <c>0x004FAFA7</c>-<c>0x004FAFD9</c>).
    /// When it is not positive (which it never is if <c>round+0x960</c> was left at its
    /// `StartCombatRound`-zeroed default — <c>StartCombatRound</c> zeroes THIS field too,
    /// confirmed by reading its full body, so it does not establish it either), the function
    /// instead takes a shortcut (<c>0x004FAFE3</c> onward) that resolves a leftover
    /// <c>round+0x9CC</c> target through a chain of vtable validity checks and then calls the
    /// round-reset helper (<c>0x004D4620</c>, confirmed by <c>fieldwrites.py 0x958</c> and
    /// cross-referenced via <c>xrefs.py</c> to a call site right where this shortcut ends,
    /// <c>0x004FB0BA</c>) — which zeroes <c>round+0x958</c> back to 0 along with <c>+0x934</c>,
    /// <c>+0x944</c>, <c>+0x94c</c>, <c>+0x960</c> and others. This is why the diagnostic
    /// always read <c>BEFORE=0</c>: not "normal consumption" and not "something else resetting
    /// it before the gate" as originally framed — the ONLY branch reachable without this call
    /// is the shortcut that ends in exactly that reset, every time, without ever reaching the
    /// action-processing call that would change the head action.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetRoundDelayFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x004D2920;

    /// <summary>
    /// ROUND 16 (`docs/research/shared-state-audit.md` §2). The real gate-closer neither
    /// <see cref="SetRoundTimerActiveFn"/> nor <see cref="SetRoundDelayFn"/> ever reaches:
    /// `UpdateCombatRoundTimer`'s SECOND check (past `round+0x958`/`round+0x960`) is
    /// `[round+4+(round+0x96C)*0x14C+0xB0] &gt; 0` — one of FIVE embedded per-participant
    /// sub-structures, selected by `round+0x96C`, a field only `CSWSCombatRound::StartCombatRound`
    /// (`0x004D5F70`) ever sets, via a loop that resets (not initializes ready-to-use — round 15
    /// disassembled `0x4D3010` and found it explicitly zeroes `+0xb0`) all five sub-structures
    /// before real setup. There is no shortcut around calling this function for real.
    ///
    /// Full disassembly of the body (`0x4D5F70`-`0x4D60EA`) plus the real caller's argument
    /// setup (`0x005BC480`-`0x5BC537`) pins the 5-argument thiscall:
    /// <list type="bullet">
    /// <item>`this` = the round (creature+0x9C8), same as every other call here.</item>
    /// <item>arg1 = the OPPONENT's object id. Resolved via `GetGameObject` (`0x004AE770`)
    /// inside the callee and stored into `round+0x9C0`/`+0x9C4`, THEN mirrored onto the
    /// resolved opponent's OWN round at the same offsets — this is why the engine's real
    /// caller makes a symmetric second call with the roles reversed (`this`=target's round,
    /// arg1=attacker's own id, at `0x005BC56D`) right after this one; that second call is not
    /// replicated here because our target is the local player, whose own round the plugin has
    /// no reason to independently arm.</item>
    /// <item>arg2/arg3 — NOT independently meaningful constants; they are the two return
    /// values of a fixed two-call sequence the real caller performs immediately before this
    /// call, on the SAME targetId, threading an in/out slot between them
    /// (`0x005BC4B3`-`0x005BC4D6`): `arg2 = ComputeRoundContextFn(round, targetId)`, written
    /// into a stack slot, then `arg3 = ResolveRoundContextFn(round, targetId, &thatSlot)` —
    /// which may itself modify the slot — after which arg2 is RE-READ from the slot
    /// post-call. Replicated here call-for-call rather than reasoned about semantically,
    /// since round 14/15 confirmed the destination fields (`round+0x9B8`=arg2,
    /// `round+0x9BC`=arg3) are load-bearing (0x4D2C30 itself reads a resolved target's
    /// `round+0x9BC` to decide whether that target "needs a new round").</item>
    /// <item>arg4 — read inside `0x4D2A10` as a 16-bit value compared against a small, fixed
    /// set of constants (`0xb`/`0x5b`/`0x35`/`0x1e`/`0x5c`/`0x1a`) to decide whether to
    /// increment a derived counter at `round+0x990`; never dereferenced as a pointer, so any
    /// integer is memory-safe regardless of its full semantic role (plausibly an attack-style
    /// code). 0 matches none of the listed constants, i.e. the ordinary/no-special-case path.</item>
    /// <item>arg5 — stored directly into `round+0x940` (`0x004D604B`), a plain DWORD, never
    /// read again inside the portion of `StartCombatRound` itself that was disassembled and
    /// not part of the confirmed critical gate chain (`+0x958`/`+0x960`/`+0x96C`→sub`+0xB0`).
    /// Also memory-safe for any integer value. 0.</item>
    /// </list>
    ///
    /// ⚠ NOT YET LIVE-VERIFIED. arg1's destination and arg2/arg3's call recipe are traced with
    /// real confidence (byte-exact, cross-checked against the engine's own field writes and a
    /// second, symmetric call site). arg4/arg5 are confirmed memory-safe (plain integer
    /// writes/comparisons, not pointers) but their full gameplay semantics were not traced
    /// past "does not corrupt anything" — see `docs/research/shared-state-audit.md` §2 round
    /// 16 for what a live pass should watch for if the attack cadence looks wrong even after
    /// the enemy starts fighting (arg4/arg5 are the first place to look).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int> ComputeRoundContextFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int>)0x004D2C30;

    /// <summary>See <see cref="ComputeRoundContextFn"/> — the second half of the same
    /// two-call recipe, called on the SAME targetId with a pointer to the first call's
    /// result. VERIFIED by disassembly of `0x004D2D60`.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint, int> ResolveRoundContextFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint, int>)0x004D2D60;

    /// <summary>See <see cref="ComputeRoundContextFn"/>. `ret 0x14` confirms exactly 5 stack
    /// DWORDs, matching all 5 arguments traced.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, int, int, int, void> StartCombatRoundFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, int, int, int, void>)0x004D5F70;

    /// <summary>
    /// ROUND 17: <c>CSWSCreature::CreateNewAttackActions</c> (<c>0x005B6980</c>, thiscall,
    /// `ret 0xC` — 3 stack args). Already fully decoded and VERIFIED in
    /// `docs/research/combat-participation.md` §1, independently of this investigation — this
    /// is the engine's OWN single entry point for "acquire/validate a target and queue the
    /// swing," and its own single caller is <c>EndCombatRound</c>, reached only from the round
    /// timers or a round already in progress (never from a driven puppet's own, non-running AI
    /// script). <see cref="AddAttackActionFn"/> alone (what this file called directly through
    /// round 16) only replicates ONE line of this function's body — "if the target survives
    /// validation, AddAttackAction(...) queues the next swing" — and that line is reached only
    /// when `round+0x9B0` (the pending list) is EMPTY, which our own prior `AddAttackActionFn`
    /// call had already made false. With `bMayAcquireNewTarget != 0` the function instead runs
    /// its ACQUISITION path (§1.3), which does everything the revalidation path skips: two
    /// `GetNearestEnemy` searches, then on success `SetLockOrientationToObject`, writes
    /// `this+0x84`/`this+0x50C`, **`UpdateAttackTargetForAllAction(round, newId)`
    /// (`0x004D3E50`, never previously called from this file)**, `this+0x4E8 = 1`, a client-GUI
    /// notification, and only THEN queues the attack action — this is the real candidate for
    /// whatever actually arms a round's ready-to-process state, since nothing this
    /// investigation has called before now ever touched `UpdateAttackTargetForAllAction` at
    /// all.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, uint, void> CreateNewAttackActionsFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, uint, void>)0x005B6980;

    /// <summary>
    /// ROUND 23 DIAGNOSTIC. Two clean, unambiguous thiscalls from <c>MaxCleaveRange</c>'s own
    /// sub-call (<c>0x004FB0F0</c>) — read directly instead of trying to replicate
    /// <c>0x004FFAD0</c>'s confusing caller-side stack-argument reuse, which is not safe to
    /// guess on a live process. <c>0x005A4C20(equipSlotPtr, 0x10)</c> resolves the equipped
    /// weapon at slot 0x10; <c>0x005B4790(weaponObj)</c> is an accessor whose result's
    /// <c>+0x1A</c> byte and <c>+0x34</c> float are what <c>0x004FB0F0</c> branches on and
    /// returns. If <c>weaponObj</c> is null, the "no weapon" fallback path runs instead
    /// (<c>0x004FB156</c>, a different function entirely, not read by this diagnostic).
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint> GetEquippedItemFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint>)0x005A4C20;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> WeaponAccessorFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005B4790;

    /// <summary>
    /// ROUND 25 DIAGNOSTIC. <c>0x004FFAD0</c>'s own body was fully re-traced this round and is
    /// a straight 3-argument passthrough to <c>0x004FB0F0</c> (its "extra" pushes at the real
    /// call site are the callee's OWN 12-byte <c>ret 0xc</c> cleanup consuming only the top 3
    /// of 5 pushed values — the caller pre-stages an unrelated 4th/5th push pair for the
    /// SUBSEQUENT <c>GetNearestEnemy</c> call, not for this one). Traced the real call site
    /// (<c>0x005B6B08</c>-<c>0x005B6B14</c>, attempt 2) to concrete values: arg1=the ATTACKER's
    /// own object id, arg2=0, arg3=1 — i.e. exactly <c>MaxCleaveRange(ownId,0,1)</c>, matching
    /// `combat-participation.md`'s own already-verified call notation (line 134/136)
    /// byte-for-byte. This is the missing cross-check that confirms the derivation rather than
    /// trusting it blind. Calls <c>0x004FB0F0</c> directly (thiscall, 3 stack args) instead of
    /// <c>0x004FFAD0</c>, bypassing its stack-reuse entirely now that the passthrough is proven
    /// exact.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, int, float> MaxCleaveRangeFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, int, float>)0x004FB0F0;

    /// <summary>
    /// ROUND 18 DIAGNOSTIC. <c>CSWSObject::GetAIStateReputation</c> (<c>0x0057CD90</c>, thiscall,
    /// one stack arg, <c>ret 4</c>) — VERIFIED in <c>docs/research/combat-participation.md</c>
    /// §1.4/§2 independently of this investigation: <c>GetNearestEnemy</c>'s hostility test is
    /// <c>GetAIStateReputation(candidate, vsId) == 2</c> (1 friendly / 2 hostile / 0 neutral).
    /// Measuring this live, for (mirrored creature, local player 0x7FFFFFFF), is round 18's
    /// entire job — see shared-state-audit.md §2.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int> GetAIStateReputationFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int>)0x0057CD90;

    /// <summary>
    /// ROUND 48 PUBLIC WRAPPER. The same engine call <see cref="GetAIStateReputationFn"/>
    /// makes, exposed so a caller outside this file (the round-48 probe in
    /// <c>EnemyPresenceController.StartLocalFight</c>) can ask the engine's own hostility
    /// question directly instead of hand-rolling a second copy of the pointer chain: 1
    /// friendly / 2 hostile / 0 neutral, or -1 if <paramref name="candidate"/> is null.
    /// </summary>
    public static int GetAIStateReputation(nint candidate, uint vsId) =>
        candidate == 0 ? -1 : GetAIStateReputationFn(candidate, vsId);

    /// <summary>
    /// ROUND 18 DIAGNOSTIC. <c>CSWSCreature::GetVisibleListElement</c> (<c>0x00517A20</c>,
    /// thiscall, one stack arg) — linear scan of the perceptions list at <c>+0x920</c>/<c>+0x924</c>,
    /// returns the node or null. Node byte at <c>+4</c> bit 0 is SEEN
    /// (`combat-participation.md` §3.1). `GetNearestEnemy` requires a node to exist AND the
    /// SEEN bit set — checked here as the other candidate gate, since it comes right after
    /// reputation in the real check order and is just as cheap to measure in the same pass.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint> GetVisibleListElementFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint>)0x00517A20;

    /// <summary>
    /// ROUND 19 DIAGNOSTIC. <c>CSWSCreature::GetNearestEnemy</c> (<c>0x004F2DE0</c>, thiscall,
    /// ret 0xC — 3 stack args): <c>(this, fMaxRange, excludeId, vsId)</c>, per
    /// `combat-participation.md` §1.3/§1.4. Called directly with a generously large fixed range
    /// (bypassing the engine's own `MaxCleaveRange` computation, irrelevant at ~2m) to answer
    /// one question directly: does the area scan this function performs actually FIND the local
    /// player as a candidate at all, independent of whether `CreateNewAttackActionsFn`'s own
    /// internal call to the same function succeeds. Every individual gate (resolve, hostility
    /// both directions, perception) is confirmed passing (round 18) and the post-acquisition
    /// `AsCreature` cast is confirmed succeeding (round 19) — this isolates whether the SCAN
    /// itself ever reaches the player object in the area's x-sorted array in the first place.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, float, uint, uint, uint> GetNearestEnemyFn =
        (delegate* unmanaged[Thiscall]<nint, float, uint, uint, uint>)0x004F2DE0;

    /// <summary>
    /// ROUND 20 DIAGNOSTIC. Zero-arg thiscall getter at <c>0x004CB120</c> — confirmed by
    /// disassembling <see cref="GetNearestEnemyFn"/>'s own prologue (<c>0x004F2DE0</c>-
    /// <c>0x004F2E13</c>): it is the VERY FIRST thing the function calls on <c>this</c>, and
    /// the returned pointer is immediately offset by <c>+0x190</c>/<c>+0x194</c> to walk the
    /// x-sorted object-id array (`docs/research/combat-participation.md` §1.4: "CSWSArea+0x190
    /// ids, +0x194 count"). Almost certainly <c>CSWSObject::GetArea()</c>. `GetNearestEnemy`
    /// bails to <see cref="ObjectInvalid"/> immediately if this returns 0.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetAreaFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004CB120;

    /// <summary>
    /// ROUND 20 DIAGNOSTIC. Full disassembly of <see cref="GetNearestEnemyFn"/>
    /// (<c>0x004F2DE0</c>-<c>0x004F2E4F</c>) found the VERY FIRST thing it does, before ever
    /// looking at a candidate, is a LINEAR SELF-LOOKUP: get <c>this</c>'s own area
    /// (<see cref="GetAreaFn"/>), read the area's x-sorted id array (<c>area+0x190</c> = data
    /// pointer, <c>area+0x194</c> = count), and scan every element for one equal to
    /// <c>this</c>'s own id (<c>this+0x04</c>, <see cref="ObjectIdOffset"/>). If not found
    /// anywhere in that array, the function returns <see cref="ObjectInvalid"/> IMMEDIATELY —
    /// before any hostility, perception, or distance check ever runs. This matches round 19's
    /// finding exactly: both search attempts failed identically regardless of which
    /// per-candidate gate was independently tested, because the scan never got past its own
    /// anchor step. This measures, for a real object, whether it is actually PRESENT in its
    /// own area's array at all — a plain linear scan is order-independent for a presence
    /// check, so this is not a sort-order/staleness question, it is a genuine presence
    /// question. Capped at 200000 iterations as a safety bound against a garbage count.
    /// </summary>
    private static (bool Found, int Index, int Count, nint AreaPtr, nint ArrayPtr) FindSelfInAreaArray(nint obj)
    {
        if (obj == 0)
        {
            return (false, -1, -1, 0, 0);
        }

        nint area = GetAreaFn(obj);
        if (area == 0)
        {
            return (false, -1, -1, 0, 0);
        }

        int count = *(int*)(area + 0x194);
        nint arrayPtr = *(nint*)(area + 0x190);
        uint ownId = *(uint*)(obj + ObjectIdOffset);

        if (count <= 0 || count > 200000 || arrayPtr == 0)
        {
            return (false, -1, count, area, arrayPtr);
        }

        for (int i = 0; i < count; i++)
        {
            if (*(uint*)(arrayPtr + i * 4) == ownId)
            {
                return (true, i, count, area, arrayPtr);
            }
        }

        return (false, -1, count, area, arrayPtr);
    }

    /// <summary>
    /// The missing half of `local_combat`: nothing else ever gives a mirrored enemy a reason
    /// to attack. `CSWSCreature::CreateNewAttackActions` (the ONLY place a NEW attack action
    /// gets queued, per `docs/research/combat-participation.md` §1.1) has exactly one caller,
    /// `EndCombatRound`, which is only ever reached from the round timers or a round already
    /// in progress — and starting that first round is normally the AI script's job
    /// (`DetermineCombatRound`/`ActionAttack`), which never runs for a body this instance only
    /// drives. `PermitAttacking`/`ClaimCreatureForFight` only ever gated an attack ALREADY
    /// queued (VERIFIED: `AIActionPhysicalAttacks` 0x005BBBF0 tests the `CreAttackPermittedBit`
    /// right before executing a queued swing, and nothing upstream of it ever queues the first
    /// one) — permission and initiation are two different things, and this call is the
    /// initiation `local_combat` was missing.
    ///
    /// Gated on the creature's own head action NOT already being a fight action (see
    /// <see cref="IsFightAction"/>) so this does not stack a duplicate swing behind one still
    /// running — <see cref="AddAttackActionFn"/> has no dedup of its own, it allocates and
    /// appends unconditionally every call.
    ///
    /// ⚠ NOT YET LIVE-VERIFIED. The calling convention (`this` = the round object at
    /// creature+0x9C8, stack args `targetId, 0, 0, 0x5DC, 1`) is read from two independent
    /// sources (direct disassembly of both this function and its only prior wrapper) but this
    /// exact call — a client's plugin queuing a native attack action on a creature it does not
    /// own control input for — has never been made on a running game. See
    /// `docs/research/shared-state-audit.md` §2 for what a live pass needs to check first.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    private static long _queueAttackDiagLoggedAtMs;

    public static void QueueAttackAction(nint creature, uint targetId)
    {
        // TEMPORARY DIAGNOSTIC (2026-08-20 live-verify pass) — this call has never fired
        // observably on a running game, and none of its three early returns had a witness.
        // One throttled line per ~3s says which branch is actually being taken, so a silent
        // no-op can be told from "it fired and the engine ignored it." Remove once the first
        // live pass either confirms or refutes this path.
        long diagNowMs = Environment.TickCount64;
        bool diagLog = diagNowMs - _queueAttackDiagLoggedAtMs >= 3000;
        if (diagLog)
        {
            _queueAttackDiagLoggedAtMs = diagNowMs;
        }

        if (creature == 0 || targetId is 0 or ObjectInvalid)
        {
            if (diagLog)
            {
                PluginHost.Log($"queueattack diag: bailed - creature 0x{creature:X} targetId 0x{targetId:X}");
            }

            return;
        }

        int headType = GetHeadActionType(creature);
        if (IsFightAction(headType))
        {
            if (diagLog)
            {
                PluginHost.Log($"queueattack diag: bailed - 0x{creature:X} head action 0x{headType:X} is already a fight action");
            }

            return;
        }

        nint round = *(nint*)(creature + CreCombatRound);
        if (round == 0)
        {
            if (diagLog)
            {
                PluginHost.Log($"queueattack diag: bailed - 0x{creature:X} round pointer at +0x{CreCombatRound:X} is null, head action 0x{headType:X}");
            }

            return;
        }

        // round+0x9B0 (the pending-action list) was CONFIRMED non-null live on two different
        // mirrored creatures over a full failed fight — AddAttackAction's own append path is
        // not the problem. round+0x958 is: it gates whether UpdateCombatRoundTimer (called
        // unconditionally every tick, no mirrored/combat-state gate) does anything with a
        // queued action at all, or just increments a timer and returns. StartCombatRound
        // leaves it at 0; only this setter (see SetRoundTimerActiveFn) flips it — and the
        // engine's own code always calls it in the same breath as StartCombatRound, which our
        // driven puppet never gets because its own AI script never runs. Read both offsets
        // before/after so a live pass can see the actual before/after state, not just infer it.
        nint pendingListBefore = *(nint*)(round + 0x9B0);
        int roundTimerBefore = *(int*)(round + 0x958);
        int roundDelayBefore = *(int*)(round + 0x960);

        // ROUND 12, LIVE FINDING: SetRoundDelayFn ACCUMULATES rather than sets — confirmed live,
        // round+0x960 climbed by exactly +1500 on EVERY call (BEFORE=2238005 AFTER=2239505, next
        // BEFORE=4483506 AFTER=4485006, ...), hundreds of times per second, because this whole
        // block used to run unconditionally on every drive pass whenever the head-action gate
        // above wasn't yet satisfied — which it never was, since nothing was making it true. That
        // is a self-defeating feedback loop: the round's own due time was pushed further into the
        // future faster than real time could ever catch up to it, so UpdateCombatRoundTimer could
        // never see it as due. This also explains why the one thing that DID produce a real fight
        // (round 11, Odyssey.AttackTarget) worked: it fires from a single console command, once —
        // never from a per-frame drive loop. Only initialize the round ONCE, on the genuine 0->1
        // transition, and let the head-action gate at the top of this function do its job for
        // every pass after that.
        // ROUND 13, STATIC FINDING: AddAttackActionFn was being called unconditionally on
        // EVERY pass, gated only by the head-action check at the top of this function — which
        // never trips, because head action never leaves 0x6 in the failing case. AddAttackAction
        // has NO dedup of its own (documented above: "unconditionally allocates and appends a
        // new action node every call"), so a driven puppet under LocalCombat was having a fresh,
        // undeduplicated action node appended to round+0x9B0's pending list on every single
        // drive pass — tens of times a second, for as long as the creature stayed engaged. The
        // engine's own normal flow (CreateNewAttackActions, reached only from EndCombatRound)
        // calls this exactly once per round, never in a tight per-frame loop. This is the same
        // shape of bug ROUND 12 already found and fixed one level up (SetRoundDelayFn
        // self-defeating via repeated accumulation) — appending here every pass is equally
        // self-defeating if whatever processes the pending list expects one entry, not an
        // ever-growing backlog. Queue the attack action in the SAME gate as the round-init
        // setters: once, on the genuine 0->1 transition — exactly matching round 11's only
        // working case (Odyssey.AttackTarget, a single staged call, never repeated).
        if (roundTimerBefore == 0)
        {
            // ROUND 16: StartCombatRound FIRST, matching the engine's own program order
            // (0x005BC480-0x5BC537 calls it before either setter below) — round+0x96C, the
            // five sub-structures, and the +0xB0 gate UpdateCombatRoundTimer's SECOND check
            // depends on are only ever established here. Without this, +0x958/+0x960 passing
            // their own gates was never sufficient (rounds 12-15 confirmed exactly that).
            int contextSlot = ComputeRoundContextFn(round, targetId);
            int context2 = ResolveRoundContextFn(round, targetId, (nint)(&contextSlot));
            StartCombatRoundFn(round, targetId, contextSlot, context2, 0, 0);

            SetRoundTimerActiveFn(round, 1, (int)(*(uint*)(creature + ObjectIdOffset)));

            // The second gate UpdateCombatRoundTimer checks past round+0x958 — see
            // SetRoundDelayFn. 0x5DC (1500ms) matches the delay AddAttackActionFn's own 4th
            // argument already uses for this same round elsewhere in this call; the engine's own
            // two call sites pass a computed value here, but this function's own logic (SET or
            // ACCUMULATE a duration) does not care about the source, only that it lands a
            // positive value in round+0x960 — ONCE, not every pass (see above).
            SetRoundDelayFn(round, 0x5DC, 0);

            // ROUND 17: CreateNewAttackActions, not a bare AddAttackAction. Six rounds of
            // manually replicating pieces of the engine's own combat-decision routine
            // (StartCombatRound, the two round-timer setters, AddAttackAction) left the queued
            // action a silent no-op — round+0x958/+0x960 genuinely stuck, but nothing ever
            // advanced the round's REAL per-participant processing state
            // (round+0x96C's selected sub-structure, +0xB0/+0xAC — an array-list of fully
            // computed, ready-to-execute swings that UpdateCombatRoundTimer's per-tick loop
            // actually iterates, entirely separate from the +0x9B0 "raw intent" list
            // AddAttackAction appends to). Full disassembly traced that array's only real
            // populator to CSWSCombatRound::AddAttackAction (0x004D57A0, `sub+0xAC`'s
            // push_back) <- called only by an "assemble a full attack descriptor from
            // already-computed weapon/roll fields" function (0x004D5A50) <- called only from
            // deep inside a per-target validation loop this investigation never traced to real
            // confidence. Rather than hand-populate those descriptor fields (a much larger and
            // more fragile undertaking), call the engine's own already-fully-documented single
            // entry point for exactly this job (`docs/research/combat-participation.md` §1,
            // VERIFIED independently of this investigation): with bMayAcquireNewTarget=1 it
            // runs the ACQUISITION path (§1.3), not the bare revalidation line — the
            // revalidation line only fires when round+0x9B0 is empty, which the queued state
            // above has already made false, so a bare AddAttackAction call here was always
            // hitting a path this function's own body would skip entirely. Acquisition does
            // real GetNearestEnemy searches (biased toward our target by passing its id as the
            // search origin — the search resolves to the origin itself when nothing else
            // closer is also hostile) and, on success, calls
            // UpdateAttackTargetForAllAction(round, newId) (0x004D3E50) — a call this file has
            // NEVER made before this round, and the strongest remaining candidate for whatever
            // actually arms the round's real per-tick state.
            // ROUND 18 DIAGNOSTIC — measure the two GetNearestEnemy gates BEFORE the call that
            // depends on them, for the exact (creature, targetId) pair about to be used.
            // Reputation per combat-participation.md §1.4: candidate.GetAIStateReputation(vsId)
            // where vsId is the "versus" object — here that's the target (targetId), asking
            // "how does the target see the candidate" per the vsId-valid branch. Perception per
            // §3.1: a visible-list node must exist AND its SEEN bit (byte+4, bit 0) must be set.
            int repVsTarget = GetAIStateReputationFn(creature, targetId);
            nint visNode = GetVisibleListElementFn(creature, targetId);
            byte visFlags = visNode != 0 ? *(byte*)(visNode + 4) : (byte)0xFF;
            bool visSeen = visNode != 0 && (visFlags & 1) != 0;

            // ROUND 18 EXTENSION: combat-participation.md §1.4's actual scan direction is
            // GetAIStateReputation(candidateCre, vsId) where the SCANNED candidate is `this` and
            // vsId is the search's "versus" argument — for GetNearestEnemy's attempt 1
            // (this=player position anchor, vsId=attacker), a candidate found near the player is
            // accepted only if GetAIStateReputation(candidate, attackerId)==2. If the PLAYER
            // themselves is the candidate being evaluated, that's player.GetAIStateReputation
            // (attackerId) — the OPPOSITE thiscall direction from repVsTarget above
            // (attacker.GetAIStateReputation(playerId)). §2.3 documents faction 0 (PLAYER) as
            // going through an argument-swapped matrix lookup, so these are not guaranteed equal
            // — measure both before assuming symmetry.
            nint playerObj = ResolveServerObject(targetId);
            int repPlayerVsAttacker = -1;
            uint attackerRealId = playerObj != 0 ? *(uint*)(creature + ObjectIdOffset) : 0;
            if (playerObj != 0)
            {
                repPlayerVsAttacker = GetAIStateReputationFn(playerObj, attackerRealId);
            }

            PluginHost.Log(
                $"queueattack ROUND18 diag: creature=0x{creature:X} target=0x{targetId:X} "
                + $"GetAIStateReputation(attacker,player)={repVsTarget} "
                + $"visibleListNode=0x{visNode:X} flags=0x{visFlags:X} seenBit={visSeen} "
                + $"| playerObj=0x{playerObj:X} attackerRealId=0x{attackerRealId:X} "
                + $"GetAIStateReputation(player,attacker)={repPlayerVsAttacker} (2=hostile,1=friendly,0=neutral)");

            // ROUND 19 DIAGNOSTIC: full disassembly of CreateNewAttackActions's
            // acquisition-success path (0x005B6B58-0x005B6B76) found a step never tested by any
            // prior round. After GetNearestEnemy finds a candidate id, the function RE-RESOLVES
            // it (GetGameObject) and calls the resolved object's own vtbl+0x30 ("AsCreature" —
            // the SAME cast this function's own top half uses to validate a pre-existing target,
            // combat-participation.md line 104). If that cast returns null, the function takes
            // the exact same silent "acquisition failed" bail (0x5b48b0: SetLockOrientationToObject
            // (OBJECT_INVALID), pause animation, SetCombatMode stand-down — confirmed by
            // disassembling 0x5b48b0 itself) as if GetNearestEnemy had found nothing at all — no
            // crash, no error, just the documented "creature stands down" path. Every INPUT gate
            // to acquisition (resolve, hostility both directions, perception) is confirmed
            // passing (round 18); this AsCreature cast on the RESOLVED target is the one
            // remaining untested step between "target found" and "attack queued". playerObj
            // above is already resolved via ResolveServerObject, confirmed to call the identical
            // GetGameObjectFn(server, id) the engine's own disassembly uses (same appManager ->
            // AppServerOffset -> server chain, same call) — so its own vtbl+0x30 tells us
            // directly whether the engine's internal cast on the SAME object would succeed.
            bool asCreatureCastOk = false;
            nint playerVtableForCast = 0;
            if (playerObj != 0)
            {
                playerVtableForCast = *(nint*)playerObj;
                nint asCreatureSlot = *(nint*)(playerVtableForCast + 0x30);
                var asCreatureFn = (delegate* unmanaged[Thiscall]<nint, nint>)asCreatureSlot;
                asCreatureCastOk = asCreatureFn(playerObj) != 0;
            }

            PluginHost.Log(
                $"queueattack ROUND19 diag: playerObj=0x{playerObj:X} vtable=0x{playerVtableForCast:X} "
                + $"AsCreature(vtbl+0x30) cast on the player object: {(asCreatureCastOk ? "SUCCEEDS" : "FAILS")}");

            // ROUND 19 DIAGNOSTIC, continued: does the area scan itself ever find the player?
            // Attempt 1 per §1.3: GetNearestEnemy(candidateCre=playerObj, range, excludeId=own,
            // vsId=own) — "nearest enemy of mine, searched from the candidate's position".
            // Attempt 2 fallback: GetNearestEnemy(this=attacker, range, excludeId=own,
            // vsId=OBJECT_INVALID). A large fixed range stands in for MaxCleaveRange, irrelevant
            // at ~2m separation.
            uint scanAttempt1 = playerObj != 0
                ? GetNearestEnemyFn(playerObj, 50.0f, attackerRealId, attackerRealId)
                : ObjectInvalid;
            uint scanAttempt2 = GetNearestEnemyFn(creature, 50.0f, attackerRealId, ObjectInvalid);
            PluginHost.Log(
                $"queueattack ROUND19 diag: GetNearestEnemy attempt1(from playerObj)=0x{scanAttempt1:X} "
                + $"attempt2(from attacker)=0x{scanAttempt2:X} (0x7F000000=nothing found, "
                + $"expected a real object id if the scan reaches the player)");

            // ROUND 19 DIAGNOSTIC, final: the scan never finding the player, despite every
            // individual gate passing, points at spatial-index registration rather than a
            // targeting rule. docs/research/realtime-creature-movement.md §1 (`SetPosition`)
            // documents the area resolve step verbatim: `this->area_id` at creature+0x8C is
            // looked up through the engine's own object array (0x004D8230) to find the area,
            // and ONLY THEN does UpdatePositionInObjectsArray register this object into that
            // area's x-sorted spatial index (CSWSArea+0x190/+0x194, the exact array
            // GetNearestEnemy walks). If the client's local player object carries a stale or
            // wrong area_id, its raw x/y/z can be correct while it is registered in the WRONG
            // area's index entirely — invisible to a scan anchored in the area the sithsoldiers
            // are actually in, no matter how close the coordinates read. Compare directly.
            if (playerObj != 0)
            {
                uint playerAreaId = *(uint*)(playerObj + 0x8C);
                uint attackerAreaId = *(uint*)(creature + 0x8C);
                PluginHost.Log(
                    $"queueattack ROUND19 diag: area_id (creature+0x8C) - player=0x{playerAreaId:X} "
                    + $"attacker(sithsoldier)=0x{attackerAreaId:X} MATCH={playerAreaId == attackerAreaId}");
            }

            // ROUND 20 DIAGNOSTIC: is the attacker itself, and separately the player, actually
            // PRESENT in their own area's x-sorted object array — the exact array
            // GetNearestEnemy's self-lookup step (never before disassembled) scans first,
            // before any candidate is ever evaluated. See FindSelfInAreaArray.
            (bool Found, int Index, int Count, nint AreaPtr, nint ArrayPtr) attackerSelf = FindSelfInAreaArray(creature);
            (bool Found, int Index, int Count, nint AreaPtr, nint ArrayPtr) playerSelf =
                playerObj != 0 ? FindSelfInAreaArray(playerObj) : (false, -1, -1, 0, 0);
            PluginHost.Log(
                $"queueattack ROUND20 diag: attacker self-lookup in its own area array: "
                + $"area=0x{attackerSelf.AreaPtr:X} arrayPtr=0x{attackerSelf.ArrayPtr:X} "
                + $"count={attackerSelf.Count} found={attackerSelf.Found} index={attackerSelf.Index} "
                + $"| player self-lookup: area=0x{playerSelf.AreaPtr:X} arrayPtr=0x{playerSelf.ArrayPtr:X} "
                + $"count={playerSelf.Count} found={playerSelf.Found} index={playerSelf.Index}");

        }

        // ROUND 20: moved OUT of the one-shot round-init gate above. Full disassembly of
        // GetNearestEnemy's own anchor step (never before dissected) confirmed both the
        // attacker AND the player ARE present with valid indices in the exact same area's
        // x-sorted object array (CSWSArea+0x190/+0x194) that GetNearestEnemy walks — the
        // "spatial-index registration failure" theory round 19 proposed is REFUTED. Instead,
        // the SAME live session's own GetNearestEnemy calls show the real mechanism: attempt2
        // (searched from the attacker) returns the player's own sentinel id (0x7FFFFFFF, a
        // genuine find) on SOME calls and the "nothing found" sentinel (0x7F000000) on
        // others, sub-second apart, across different mirrored creatures each hitting their
        // own first pass. That is a RACE, not a categorical failure: our own per-frame
        // SetPosition calls constantly re-sort this exact array (the attacker's own index in
        // it bounced between 85/86/110/121/118 across consecutive diagnostic samples), and
        // the outward walk's x-distance-bounded termination can miss the player depending on
        // exactly when, relative to the last re-sort, the scan happens to run. A single
        // one-shot attempt (the old placement, inside the round-init gate above) had only one
        // roll of that race and could permanently fail on an unlucky frame.
        //
        // CreateNewAttackActionsFn now retries every pass instead — this is safe to call
        // repeatedly (unlike a bare AddAttackActionFn, which has no dedup): its acquisition
        // path re-runs GetNearestEnemy fresh each time (giving the race another roll), and
        // its re-validation path (once a target is already set) is the engine's own normal
        // per-round bookkeeping, called every round in real play. The existing
        // IsFightAction(headType) gate at the very TOP of this function already stops all
        // future calls the instant one attempt actually lands a queued swing, so this cannot
        // become the same self-defeating per-frame spam round 13 found and fixed for the bare
        // append call — it can only keep trying until it succeeds, then the outer gate closes
        // the door.
        // ROUND 21 DIAGNOSTIC: MaxCleaveRange (0x004FFAD0) is a real, weapon-derived value —
        // its sub-call (0x4FB0F0) reads a pointer at creature+0xA2C (an equipment slot) and
        // branches on data reached through it. A driven puppet's equipment state has never
        // been directly checked against this specific field. A null/zero pointer here would
        // make the range gate degenerate for EVERY candidate, independent of any race — worth
        // ruling in or out before trusting the "it's a scan-timing race" framing further,
        // especially since point-blank positioning this round did not change the outcome.
        nint equipSlotPtr = *(nint*)(creature + 0xA2C);

        // ROUND 23 DIAGNOSTIC: read the real weapon-derived range inputs MaxCleaveRange
        // computes from, instead of trusting round 19's hardcoded 50.0f stand-in. Read-only,
        // two clean thiscalls — see GetEquippedItemFn/WeaponAccessorFn above for why this path
        // was chosen over replicating 0x004FFAD0's own confusing stack-argument reuse.
        nint weaponObj = equipSlotPtr != 0 ? GetEquippedItemFn(equipSlotPtr, 0x10) : 0;
        nint weaponAccessor = weaponObj != 0 ? WeaponAccessorFn(weaponObj) : 0;
        byte weaponFlagByte = weaponAccessor != 0 ? *(byte*)(weaponAccessor + 0x1A) : (byte)0xFF;
        float weaponRangeField = weaponAccessor != 0 ? *(float*)(weaponAccessor + 0x34) : float.NaN;

        // ROUND 25 DIAGNOSTIC: the two real numbers this investigation has never directly
        // compared. (1) MaxCleaveRange(ownId,0,1) — the actual search-radius seed, called
        // exactly as the real code does (see MaxCleaveRangeFn above). (2) the actual
        // distance-minus-both-radii value the walk itself computes per candidate
        // (0x004F3061-0x004F3096 in GetNearestEnemy), replicated here in managed code from the
        // same fields (position at +0x90, personal-space radius at [+0x340]+0x08) rather than
        // hooking mid-function, which is not something this investigation has ever needed to
        // do and is not worth the risk now.
        // ROUND 25 DIAGNOSTIC (2nd measurement): with bUseAttemptedTarget=0 (our call passes
        // arg2=0), CreateNewAttackActions reads its "current target" from round+0x9CC — if
        // that is NOT the invalid sentinel, the function takes the "validate/renew existing
        // target" branch (combat-participation.md §1.2) instead of the "acquire a NEW target"
        // branch (§1.3, the one this investigation has been assuming runs every call). If a
        // prior partial attempt left round+0x9CC non-invalid, every SUBSEQUENT retry this
        // round-init gate makes could be silently taking the validate path instead, which may
        // not include the same UpdateAttackTargetForAllAction arming step §1.3 documents.
        uint currentTargetBeforeCall = *(uint*)(round + 0x9CC);
        float maxCleaveRange = MaxCleaveRangeFn(creature, *(uint*)(creature + ObjectIdOffset), 0, 1);
        nint playerObjR25 = ResolveServerObject(targetId);
        float adjustedDistance = float.NaN;
        if (playerObjR25 != 0)
        {
            float dx = *(float*)(creature + 0x90) - *(float*)(playerObjR25 + 0x90);
            float dy = *(float*)(creature + 0x94) - *(float*)(playerObjR25 + 0x94);
            float dz = *(float*)(creature + 0x98) - *(float*)(playerObjR25 + 0x98);
            float rawDistance = MathF.Sqrt(dx * dx + dy * dy + dz * dz);
            nint attackerPathInfo = *(nint*)(creature + 0x340);
            nint playerPathInfo = *(nint*)(playerObjR25 + 0x340);
            float attackerRadius = attackerPathInfo != 0 ? *(float*)(attackerPathInfo + 8) : 0f;
            float playerRadius = playerPathInfo != 0 ? *(float*)(playerPathInfo + 8) : 0f;
            adjustedDistance = rawDistance - attackerRadius - playerRadius;
        }

        CreateNewAttackActionsFn(creature, 1, 0, targetId);

        byte subIndexNow = *(byte*)(round + 0x96C);
        int subB0Now = *(int*)(round + 4 + subIndexNow * 0x14C + 0xB0);
        int headRightAfterCreate = GetHeadActionType(creature);
        if (diagLog)
        {
            PluginHost.Log(
                $"queueattack ROUND20 diag: CreateNewAttackActionsFn(creature=0x{creature:X}, "
                + $"target=0x{targetId:X}) returned. round+0x96C sub-index={subIndexNow} "
                + $"sub+0xB0={subB0Now}, head action right after=0x{headRightAfterCreate:X}, "
                + $"is fight action: {IsFightAction(headRightAfterCreate)}, "
                + $"ROUND21: creature+0xA2C equip slot ptr=0x{equipSlotPtr:X}, "
                + $"ROUND23: weaponObj=0x{weaponObj:X} weaponAccessor=0x{weaponAccessor:X} "
                + $"flagByte@+0x1A=0x{weaponFlagByte:X2} rangeField@+0x34={weaponRangeField}, "
                + $"ROUND25: MaxCleaveRange(ownId,0,1)={maxCleaveRange} adjustedDistance={adjustedDistance} "
                + $"(range-distance={maxCleaveRange - adjustedDistance}, inRange={adjustedDistance <= maxCleaveRange}), "
                + $"ROUND25b: round+0x9CC BEFORE the call=0x{currentTargetBeforeCall:X} "
                + $"(taking {(currentTargetBeforeCall == 0x7F000000 ? "ACQUIRE-NEW" : "VALIDATE-EXISTING")} branch)");
        }

        int roundTimerAfter = *(int*)(round + 0x958);
        int roundDelayAfter = *(int*)(round + 0x960);
        int headAfter = GetHeadActionType(creature);

        // ROUND 16 DIAGNOSTIC: the second gate UpdateCombatRoundTimer checks past +0x958/+0x960
        // — round+0x96C selects one of five embedded 0x14C-byte sub-structures at round+4, and
        // [sub+0xB0] > 0 is what actually lets processing reach the call that would change the
        // head action. Read both directly to see whether StartCombatRoundFn established this.
        byte subIndex = *(byte*)(round + 0x96C);
        int subB0 = *(int*)(round + 4 + subIndex * 0x14C + 0xB0);

        if (diagLog)
        {
            PluginHost.Log(
                $"queueattack diag: round=0x{round:X}, target=0x{targetId:X}, creature=0x{creature:X}, "
                + $"queued-this-pass={roundTimerBefore == 0}, "
                + $"round+0x9B0 pending-list = 0x{pendingListBefore:X}, "
                + $"round+0x958 timer-active BEFORE={roundTimerBefore} AFTER={roundTimerAfter}, "
                + $"round+0x960 delay BEFORE={roundDelayBefore} AFTER={roundDelayAfter}, "
                + $"round+0x96C sub-index={subIndex} sub+0xB0={subB0}, "
                + $"head action before 0x{headType:X}, IMMEDIATELY after 0x{headAfter:X} "
                + $"(is fight action now: {IsFightAction(headAfter)})");
        }
    }

    // ---- STRATEGY 2 (2026-08-21): fire the creature's own event script instead of
    // replicating the engine's native combat calls piece by piece ------------------
    //
    // 27 rounds (above) got the round-arming write itself into view, then hit a wall: that
    // write lives inside the engine's own bounded loop over combat participants, called
    // from one site deep in stateful per-tick round-advancement machinery — not a small
    // utility a plugin can call in isolation. This is a different approach: instead of
    // driving the C++ mechanics ourselves, ask the engine's OWN event system to notify the
    // creature the way it notifies every other creature in the game, and let its assigned
    // NWScript AI (which normally decides to fight) take it from there.
    //
    // CVirtualMachine::RunScript (0x005D0FC0 is a thunk; the real body is 0x005D45D0,
    // confirmed via disassembly: a queue-based dispatcher with a pending-script list at
    // +0x1AC and a counter at +0x28, not a simple synchronous call). VERIFIED via two real,
    // already-shipped call sites:
    //   - CSWSDoor::OpenDoor (0x00589D32): RunScript(Deref(VirtualMachinePtr),
    //     &door+0x228 [the OnOpen script field, documented at Odyssey.cs:1208],
    //     *(door+4) [the door's own object id], 1).
    //   - A per-creature event dispatcher at 0x004DA8B6: RunScript(Deref(VirtualMachinePtr),
    //     &creature+0x260, *(creature+4), 1) — found by searching every xref to the RunScript
    //     thunk (83 call sites total) for one inside a known AI-tick function. This ONE
    //     function also calls UpdateVisibleList (0x0051B050, the perception scan an earlier
    //     round already confirmed runs unmodified for a mirrored creature) via a DIFFERENT
    //     branch of the same 13-case jump table (dispatched on the return of 0x00503690) —
    //     i.e. this is a generic "signal event N to this creature" dispatcher, and +0x260 is
    //     whichever named event reaches this specific branch. The exact event name (heartbeat?
    //     OnNotice/perception-noticed? something else?) is NOT decoded — this is a real,
    //     working script-resref field on a live creature object, confirmed by a real call
    //     site, nothing more asserted than that.
    //
    // `this` for RunScript is the SAME single dereference of VirtualMachinePtr (0x7A3A00)
    // both real call sites use — NOT the double-derefed "inner" object GetImplementer
    // resolves for the stack-push routines. Matching the proven-working OpenDoor call
    // exactly, not guessing at a different chain.
    private static readonly delegate* unmanaged[Thiscall]<nint, nint, uint, int, void> RunScriptFn =
        (delegate* unmanaged[Thiscall]<nint, nint, uint, int, void>)0x005D0FC0;

    /// <summary>Creature script slot at +0x260 — see the block comment above. NOT YET
    /// LIVE-VERIFIED: the field has never been read to confirm it holds a real, non-empty
    /// script resref, and firing it has never been tried on a running game.</summary>
    private const int CreScriptSlot0x260 = 0x260;

    /// <summary>
    /// Reads a creature's +0x260 script slot, for a diagnostic read before ever firing it.
    ///
    /// VERIFIED LIVE (2026-08-21): this is NOT an inline resref buffer — it is an indirect
    /// string handle, `{ char* data; int length; ... }` (CExoString-shaped: the first DWORD is
    /// a genuine heap pointer to the text, the second its length). Reading the raw bytes as
    /// ASCII (the original version of this method) produces garbage, because the first 4-5
    /// bytes are a little-endian pointer, not text — e.g. a pointer ending in 0x00 74 ('t',
    /// then a null) reads as a short, printable-looking but WRONG string. Dereferencing
    /// correctly, on a live untouched `sithsoldier`, reads `k_def_combend01` — a real,
    /// well-known KOTOR default AI script (the OnCombatRoundEnd slot, whose body normally
    /// calls NWScript's `DetermineCombatRound()` — the exact routine that decides to attack).
    /// </summary>
    public static string PeekCreatureScriptSlot0x260(nint creature)
    {
        if (creature == 0)
        {
            return string.Empty;
        }

        nint* p = (nint*)(creature + CreScriptSlot0x260);
        nint dataPtr = p[0];
        int length = (int)p[1];

        if (dataPtr == 0 || length <= 0 || length > 64)
        {
            return $"(no readable string here: ptr=0x{dataPtr:X} length={length})";
        }

        byte* text = (byte*)dataPtr;
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < length && text[i] != 0; i++)
        {
            char c = (char)text[i];
            sb.Append(c is >= (char)0x20 and < (char)0x7F ? c : '.');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Whether <paramref name="creature"/> actually carries a real script at +0x260 —
    /// VERIFIED LIVE (2026-08-21) to matter, not a hypothetical: `tar03_bekcs2`, a genuine
    /// GIT-placed hostile, has an EMPTY slot here (`ptr=0x0 length=0`). Firing an empty slot
    /// through <see cref="RunScriptFn"/> is a silent no-op — no script runs, so
    /// `DetermineCombatRound()` (what `k_def_combend01` normally calls, the routine that
    /// actually decides to attack) never fires. Measured live: the creature sat frozen and
    /// never landed a hit across a full engagement window at genuine melee range (down to
    /// 1.25 world units, well inside striking distance) — this was mistaken for a
    /// pathing/range problem before the empty slot was found. Worse than inert: without this
    /// gate, `local_combat` re-excludes a script-less creature from the census forever (each
    /// 25s timeout immediately re-triggers, since the player is still in range and the
    /// "fight" never resolves any other way), which keeps `IncomingDamageController`'s
    /// absorb-standdown licensed for it continuously — i.e. the player becomes permanently
    /// immune to that one enemy's forwarded damage. See <see cref="PeekCreatureScriptSlot0x260"/>
    /// for the exact field layout this reads.
    /// </summary>
    public static bool HasCreatureScriptSlot0x260(nint creature)
    {
        if (creature == 0)
        {
            return false;
        }

        nint* p = (nint*)(creature + CreScriptSlot0x260);
        nint dataPtr = p[0];
        int length = (int)p[1];
        return dataPtr != 0 && length is > 0 and <= 64;
    }

    /// <summary>
    /// Fires the creature's own +0x260 event script through the SAME dispatcher the engine
    /// uses for every other object's scripted events (proven live-working for a door's
    /// OnOpen). This does not touch any round/combat-round state directly — if it works, it
    /// works by letting the creature's OWN NWScript AI decide to fight, the way it already
    /// does for every non-mirrored creature in the game. Callers driving real `local_combat`
    /// engagement MUST check <see cref="HasCreatureScriptSlot0x260"/> first — this function
    /// itself does not, since the manual `firescript` diagnostic verb needs to be able to
    /// fire (and thereby prove) an empty slot on purpose. MAIN THREAD ONLY.
    /// </summary>
    public static void FireCreatureScriptSlot0x260(nint creature)
    {
        if (creature == 0)
        {
            return;
        }

        nint vm = Deref(VirtualMachinePtr);
        if (vm == 0)
        {
            return;
        }

        uint creatureId = *(uint*)(creature + ObjectIdOffset);
        RunScriptFn(vm, creature + CreScriptSlot0x260, creatureId, 1);
    }

    /// <summary>
    /// ROUND 39 CLEANUP: the per-frame diagnostic watch (rounds 28-33 built it, rounds 34-38
    /// depended on it) used to arm automatically inside <see cref="FireCreatureScriptSlot0x260"/>
    /// itself — harmless for a hand-fired diagnostic console command, but <c>local_combat</c>
    /// now calls that same function for every real fight, and 6000 lines of per-frame logging
    /// per fight is not something a shipped feature does. Diagnostic callers (the `firescript`
    /// console verb) call this explicitly right after firing; <see cref="EnemyPresenceController.StartLocalFight"/>
    /// does not call it at all.
    /// </summary>
    public static void ArmFireScriptWatch(nint creature)
    {
        uint creatureId = creature != 0 ? *(uint*)(creature + ObjectIdOffset) : 0;
        _fireScriptWatchCreature = creature;
        _fireScriptWatchFramesLeft = 6000;
        PluginHost.Log($"firescript watch: armed on 0x{creatureId:X} for 6000 frames");
    }

    // ROUND 34: +0x260's dispatcher (0x004DA846) was fully mapped — 9 of its 13 event codes
    // funnel into the SAME +0x260 slot; the other two branches never call RunScript at all.
    // There is no second script reachable through that one dispatcher. Found instead via a
    // DIFFERENT call site, 0x004EB79D (inside the perception/heartbeat area, near
    // UpdateVisibleList/DoPerceptionUpdateOnCreature): `lea eax, [esi+0x270]` then RunScript,
    // gated on `[esi+0xa24] == 0x7F000000` (no current target — the same "not found" sentinel
    // GetNearestEnemy's failure case uses) AND `[esi+0xa88] == 0`, then LATCHED via
    // `[esi+0x34c] = 1` so it never re-fires. This is exactly OnNotice's real-world shape:
    // "I don't have a target yet — evaluate once." +0x270 is a plausible, betterfitting
    // candidate for the script that's actually supposed to decide to fight from cold, as
    // opposed to +0x260 (confirmed to hold k_def_combend01, "OnCombatRoundEnd" by name,
    // called by 9/13 unrelated event codes through the SAME generic dispatcher — its true
    // semantic role was never nailed down beyond "a real script slot on a live creature").
    private const int CreScriptSlot0x270 = 0x270;

    /// <summary>Same indirect-string-handle read as <see cref="PeekCreatureScriptSlot0x260"/>,
    /// at +0x270 instead.</summary>
    public static string PeekCreatureScriptSlot0x270(nint creature)
    {
        if (creature == 0)
        {
            return string.Empty;
        }

        nint* p = (nint*)(creature + CreScriptSlot0x270);
        nint dataPtr = p[0];
        int length = (int)p[1];

        if (dataPtr == 0 || length <= 0 || length > 64)
        {
            return $"(no readable string here: ptr=0x{dataPtr:X} length={length})";
        }

        byte* text = (byte*)dataPtr;
        System.Text.StringBuilder sb = new();
        for (int i = 0; i < length && text[i] != 0; i++)
        {
            char c = (char)text[i];
            sb.Append(c is >= (char)0x20 and < (char)0x7F ? c : '.');
        }

        return sb.ToString();
    }

    /// <summary>Fires +0x270 through the same RunScript dispatch <see
    /// cref="FireCreatureScriptSlot0x260"/> uses. Diagnostic-only slot (no production caller
    /// exists), so the watch is armed the same explicit way — see <see
    /// cref="ArmFireScriptWatch"/> — by the `firescript ... fire 270` console verb, not by this
    /// function itself. MAIN THREAD ONLY.</summary>
    public static void FireCreatureScriptSlot0x270(nint creature)
    {
        if (creature == 0)
        {
            return;
        }

        nint vm = Deref(VirtualMachinePtr);
        if (vm == 0)
        {
            return;
        }

        uint creatureId = *(uint*)(creature + ObjectIdOffset);
        RunScriptFn(vm, creature + CreScriptSlot0x270, creatureId, 1);
    }

    private static nint _fireScriptWatchCreature;
    private static int _fireScriptWatchFramesLeft;

    /// <summary>
    /// ROUND 28 DIAGNOSTIC. Called every frame from PluginHost.OnFrame, unconditionally (no
    /// throttle) while a watch is armed — logs the creature's head action type and the full
    /// round-struct state (+0x9CC target, +0x958/+0x960 timer, +0x96C sub-index, sub+0xB0)
    /// every single frame for 180 frames after a firescript call, so a transient arm-then-wipe
    /// this fast cannot hide between two 3-second-throttled samples the way it could in rounds
    /// 9-27's own diagnostics. Independent of local_combat — runs regardless of that setting,
    /// which is the whole point of an isolated round 28 pass with local_combat=0.
    /// </summary>
    public static void TickFireScriptWatch(System.Action<string> log)
    {
        if (_fireScriptWatchFramesLeft <= 0 || _fireScriptWatchCreature == 0)
        {
            return;
        }

        nint creature = _fireScriptWatchCreature;
        int framesLeft = _fireScriptWatchFramesLeft--;

        int headType = GetHeadActionType(creature);
        nint round = *(nint*)(creature + CreCombatRound);

        if (round == 0)
        {
            log($"firescript watch: frame -{framesLeft}, creature=0x{creature:X}, head action=0x{headType:X}, round pointer is NULL");
            return;
        }

        uint currentTarget = *(uint*)(round + 0x9CC);
        int timerActive = *(int*)(round + 0x958);
        int delay = *(int*)(round + 0x960);
        byte subIndex = *(byte*)(round + 0x96C);
        nint sub = round + 4 + subIndex * 0x14C;
        int subB0 = *(int*)(sub + 0xB0);

        // ROUND 30: sub+0x5c is a STATE BYTE round 15/18 flagged and round 27 confirmed gates
        // entry to the arming loop (only -1 or 0..3 continue toward it). 0x5B8050's own real
        // processing branch cross-checks it against 2 via a call through 0x4AEA40 (a
        // this-adjustor thunk) -> 0x4B4CB0, which — fully disassembled this round — takes NO
        // real arguments: it reads the fixed global CServerExoApp-shaped singleton at
        // 0x7A39FC, derefs +8 (a party-table-shaped sub-object), and calls another thunk
        // chain (0x4AE920 -> 0x4B2AE0) with hardcoded args (0, 1) that walks one of two
        // lists at [that+0x10064]/[that+0x10068] looking for an entry whose id-field equals
        // the hardcoded key 0 — i.e. "is party slot 0 (THIS INSTANCE's own local player)
        // resolvable right now" — returning either the resolved id or the not-found sentinel
        // 0x7F000000. It is a pure read (two accessor calls, no writes observed), safe to
        // call directly as a bare zero-argument function — the adjusted `this` the thunk
        // chain sets up is never actually read by 0x4B4CB0's body.
        byte sub5c = *(byte*)(sub + 0x5C);
        uint partySlot0 = ResolvePartySlot0Fn();

        // ROUND 31: two fields inside 0x5B8050 (UpdateCombatRoundTimer's real processing
        // branch) never previously read. Full disassembly (this round) found, right after the
        // sub+0x5c/party-slot-0 cross-check already logged above:
        //   mov eax, [sub+0x40]
        //   test eax, eax
        //   je continue-normally
        //   cmp [sub+4], 0
        //   je BAIL (a far jump past the entire ~0x700-byte weapon/attack-descriptor build
        //            and, critically, past everything that leads to sub+0xB0's own Add call)
        // i.e.: "if sub+0x40 is set AND sub+4 is still zero, skip the whole thing." If sub+4
        // is meant to hold this participant's own resolved target pointer (a level BELOW the
        // round's own +0x9CC target field StartCombatRound already populates) and nothing this
        // investigation has ever called populates it, this is the actual missing write.
        int sub40 = *(int*)(sub + 0x40);
        int sub4 = *(int*)(sub + 4);

        // ROUND 32: full disassembly of UpdateCombatRoundTimer's EXPIRY path (0x004FAFE3
        // onward, reached when delay - creature+0xCC <= 0, i.e. the moment round+0x960 hits
        // zero) found the actual commit/reset fork. If sub+0xB0 is still 0 at that instant it
        // falls straight through to the reset call (0x004D4620) with no further checks — the
        // "never armed at all" case every round to date has measured. But the code ALSO
        // re-checks target validity on every loop iteration while sub+0xB0 IS positive
        // (0x4FAFF0's loop), and unconditionally, right before EITHER exit, re-resolves
        // round+0x9CC fresh via GetGameObject (0x004AE750 — same as ResolveServerObject),
        // casts it (vtbl+0x10, "AsSWSObject" per round 17/19's naming), checks GetDead
        // (vtbl+0x94 - already known live-false for our target), then on a live target casts
        // again (vtbl+0x30, "AsCreature") and calls CSWSCreature::GetDeadTemp (0x004EF890 -
        // named in tools/kotor1_0_3.db, not a guess) on the result. GetDeadTemp returning
        // TRUE routes straight into the reset call; returning FALSE just returns with no
        // reset AND no commit. GetDeadTemp's own body (disassembled this round): walks
        // CClientExoApp::GetSWParty()'s roster (CSWParty::GetCharacter per slot), converts
        // the passed-in target's id via CClientExoAppInternal::ServerToClientObjectId, and
        // only if the target IS found in the CLIENT's own local party does it go on to read
        // vtbl+0x9c(0) on the target and return (that value <= 0). Log the raw chain here so
        // a live pass can see exactly which branch fires for the real failing target, not just
        // GetDeadTemp's own final boolean.
        int deadTemp = -1;
        short vtbl9cRaw = short.MinValue;
        nint resolvedTarget = ResolveServerObject(currentTarget);
        nint asSwsObject = 0;
        int getDeadResult = -1;
        nint asCreature = 0;
        if (resolvedTarget != 0)
        {
            nint vtbl0 = *(nint*)resolvedTarget;
            var asSwsObjectFn = (delegate* unmanaged[Thiscall]<nint, nint>)(*(nint*)(vtbl0 + 0x10));
            asSwsObject = asSwsObjectFn(resolvedTarget);
            if (asSwsObject != 0)
            {
                nint vtbl1 = *(nint*)asSwsObject;
                var getDeadFn = (delegate* unmanaged[Thiscall]<nint, int>)(*(nint*)(vtbl1 + 0x94));
                getDeadResult = getDeadFn(asSwsObject);
                if (getDeadResult == 0)
                {
                    var asCreatureFn = (delegate* unmanaged[Thiscall]<nint, nint>)(*(nint*)(vtbl1 + 0x30));
                    asCreature = asCreatureFn(asSwsObject);
                    if (asCreature != 0)
                    {
                        nint vtbl2 = *(nint*)asCreature;
                        var vtbl9cFn = (delegate* unmanaged[Thiscall]<nint, int, short>)(*(nint*)(vtbl2 + 0x9C));
                        vtbl9cRaw = vtbl9cFn(asCreature, 0);
                        var getDeadTempFn = (delegate* unmanaged[Thiscall]<nint, int>)0x004EF890;
                        deadTemp = getDeadTempFn(asCreature);
                    }
                }
            }
        }

        // ROUND 51/52: the "(is fight action: {IsFightAction(headType)})" interpretation
        // this line used to print here is DELETED, not fixed — round 51 measured it read
        // False on 0 of 6000 sampled frames across three trials where the player's own HP
        // demonstrably fell 12->1 (docs/research/shared-state-audit.md §2, round 51). The
        // head action reflects the action-QUEUE dispatch (RunActions); the real combat-round
        // processing round 51's own disassembly found (StartCombatRound 0x005BB590, the
        // maneuver-selection dispatch at 0x005BAD8A) is a separate state machine this field
        // does not observe, so no fix to the test itself was evident from the disassembly on
        // hand. `headType` is still logged raw below for whoever continues the sub+0x5c lead;
        // just do not read it as "is a fight happening." Ground truth is now
        // PhantomHitHook.LogLocalHitGroundTruth's own "combat ground-truth:" log line
        // instead (round 52), which reads the engine's own damage choke point directly.
        log($"firescript watch: frame -{framesLeft}, creature=0x{creature:X}, head action=0x{headType:X}, "
            + $"round+0x9CC target=0x{currentTarget:X}, "
            + $"round+0x958 timer={timerActive}, round+0x960 delay={delay}, "
            + $"round+0x96C sub-index={subIndex} sub+0xB0={subB0}, sub+0x5c={sub5c}, "
            + $"party-slot-0 resolves to=0x{partySlot0:X}, sub+0x40=0x{sub40:X}, sub+4=0x{sub4:X}, "
            + $"resolvedTarget=0x{resolvedTarget:X}, asSwsObject=0x{asSwsObject:X}, GetDead={getDeadResult}, "
            + $"asCreature=0x{asCreature:X}, vtbl+0x9C(0)={vtbl9cRaw}, GetDeadTemp={deadTemp}");
    }

    /// <summary>ROUND 30: bare call to 0x004B4CB0, the real body behind the
    /// 0x4AEA40/0x4B4CB0/0x4AE920/0x4B2AE0 thunk chain 0x5B8050's real processing branch uses
    /// to cross-check its target against "is party slot 0 resolvable." Cdecl, zero real
    /// arguments (confirmed by full disassembly — it never reads a stack argument or uses the
    /// incoming `this`), returns the resolved creature id or 0x7F000000 (not found).</summary>
    private static readonly delegate* unmanaged[Cdecl]<uint> ResolvePartySlot0Fn =
        (delegate* unmanaged[Cdecl]<uint>)0x004B4CB0;

    // ---- faction, the targetability switch ---------------------------------------
    //
    // Hostility in KOTOR 1 is faction-vs-faction with no personal reputation list, and
    // every targeting path re-reads it live: GetReputation -> stats+0x78 -> the faction
    // matrix (docs/research/combat-participation.md §2). So a phantom becomes a real
    // combat target by joining faction 0 (PLAYER) and stops being one by returning to 5
    // (Neutral) — one dword per state change, no engine paths patched.

    /// <summary>CFactionManager::GetFaction(int id) — the faction object for an id.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, nint> GetFactionFn =
        (delegate* unmanaged[Thiscall]<nint, int, nint>)0x0052B3B0;

    public const int FactionPlayer = 0;

    /// <summary>
    /// Moves a creature into a faction through the engine's own membership call.
    ///
    /// This is what makes a peer's phantom a genuine combat target (faction 0) and what
    /// releases every attacker again (faction 5): retention test 6 in
    /// CreateNewAttackActions is a live `GetReputation > 10` check evaluated at every
    /// combat-round boundary, so the flip back is a real release lever — the one the
    /// original aggro-sink design (Handoff 7.4) never had.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool SetCreatureFaction(uint objectId, int factionId)
    {
        if (objectId is 0 or ObjectInvalid)
        {
            return false;
        }

        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        // The same accessor the world-up faction repair uses, rather than a second
        // hand-walked pointer chain to the same manager.
        nint factionManager = GetFactionManagerFn(server);
        if (factionManager == 0)
        {
            return false;
        }

        nint faction = GetFactionFn(factionManager, factionId);
        if (faction == 0)
        {
            return false;
        }

        FactionAddMemberFn(faction, objectId, 0);
        return true;
    }

    /// <summary>Reads a creature's faction id (dword), or -1.</summary>
    public static int GetCreatureFaction(nint creature)
    {
        if (creature == 0)
        {
            return -1;
        }

        nint stats = Deref(creature + CreStats);
        return stats == 0 ? -1 : (int)*(uint*)(stats + StatsFactionId);
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
    /// The same CSWSCreature::PacifyCreature (0x004FD960), named for the general case: release
    /// every local creature currently locked onto this body. Mirrored COMPANIONS need it for
    /// the same reason phantoms do — under local_combat they are pinned min-1-hp, so they
    /// never read dead and nothing lets go of them on its own.
    /// </summary>
    public static void ReleaseAttackersOn(nint creature) => PacifyPhantom(creature);

    /// <summary>
    /// Orders one creature to attack another through the engine's own ActionAttack — a real
    /// combat round, with swings, attack rolls, damage and the AI taking over from there.
    ///
    /// This exists because staging a fight on demand is otherwise unreliable: the Endar
    /// Spire's Sith are scripted placements that do not aggro (Handoff 7.0), so every
    /// combat-facing feature — the damage forward, swing animations, the phantom's
    /// targetability and its release levers — had no way to be exercised. Commanding the
    /// attack is the cheapest honest fight, and it is the route 7.0 itself recommends.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    /// <summary>
    /// CServerExoApp::ClearAllQueuedCombatActions(objectId) — drops a creature's queued
    /// combat actions, which is what actually ends a fight it was ordered into.
    ///
    /// Signature checked against the binary rather than taken from the symbol DB alone:
    /// one stack argument read at 0x004AEFA0 and `ret 4` at 0x004AEFBC, matching the DB's
    /// param size of 4. (See docs/research/engine-api.md for why that check is not optional.)
    ///
    /// Exists for the TEST BATTERY, which stages a real fight and then needs the world back:
    /// a creature left in combat cannot start a conversation, so the fight staged by one
    /// check was failing the dialogue check that ran after it.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ClearQueuedCombatActions(uint objectId)
    {
        nint server = GetServer();
        if (server == 0 || objectId == 0)
        {
            return false;
        }

        ClearQueuedCombatActionsFn(server, objectId);
        return true;
    }

    private static readonly delegate* unmanaged[Thiscall]<nint, uint, void> ClearQueuedCombatActionsFn =
        (delegate* unmanaged[Thiscall]<nint, uint, void>)0x004AEFA0;

    public static bool AttackTarget(uint attackerObjectId, uint targetObjectId, out int failure)
    {
        failure = 0;

        // CONFIRMED LIVE (round 11): this dispatches through the exact same ExecuteCommandAttack
        // -> AddAttackActions -> AddAttackAction chain QueueAttackAction's own investigation
        // mapped, and neither of AddAttackActions's two preamble helpers (0x004D3770/0x004D37E0)
        // calls SetRoundTimerActiveFn or SetRoundDelayFn (confirmed: neither address appears in
        // either function's xref list) — so a creature that has never had a round started the
        // way the engine's own AI decision code does it hits the identical silent-discard
        // UpdateCombatRoundTimer's round+0x958/+0x960 gates produce for QueueAttackAction before
        // that fix. This is not a separate blocker from local_combat's own failure; it is the
        // SAME missing precondition, reached through a different call path. Applying it here,
        // exactly as QueueAttackAction does, took a genuinely fresh sithsoldier (round+0x958 and
        // +0x960 both read 0 immediately before this call) from inert to a real fight: it closed
        // distance and landed actual damage (12 hp -> 8 hp -> 1 hp across two ordered attacks),
        // confirmed via `where` on the player and `creatures.ps1` on the attacker, not inferred.
        // This also restores AttackTarget as a working staging tool for the rest of this
        // investigation's test battery, which round 10 found broken by the same root cause.
        nint attackerServer = GetServer();
        if (attackerServer != 0 && attackerObjectId != 0)
        {
            nint attackerCreature = GetGameObjectFn(attackerServer, attackerObjectId);
            if (attackerCreature != 0)
            {
                nint attackerRound = *(nint*)(attackerCreature + CreCombatRound);
                if (attackerRound != 0)
                {
                    int roundTimerBefore = *(int*)(attackerRound + 0x958);
                    int roundDelayBefore = *(int*)(attackerRound + 0x960);
                    SetRoundTimerActiveFn(attackerRound, 1, (int)attackerObjectId);
                    SetRoundDelayFn(attackerRound, 0x5DC, 0);
                    PluginHost.Log(
                        $"attacktarget diag: 0x{attackerObjectId:X} round+0x958 {roundTimerBefore:X}->{(*(int*)(attackerRound + 0x958)):X}, "
                        + $"round+0x960 {roundDelayBefore:X}->{(*(int*)(attackerRound + 0x960)):X}");
                }
                else
                {
                    PluginHost.Log($"attacktarget diag: 0x{attackerObjectId:X} round pointer at +0x{CreCombatRound:X} is null");
                }
            }
        }

        nint implementer = GetImplementer(out nint vm, out nint internalVm);
        if (implementer == 0)
        {
            failure = 1;
            return false;
        }

        nint table = Deref(implementer + CommandsTableOffset);
        if (table == 0 || *(nint*)(table + (RoutineActionAttack * 4)) != ExpectedAttack)
        {
            failure = 2;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 3;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        // OBJECT_SELF is the ATTACKER: an action always applies to the caller.
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = attackerObjectId;

        try
        {
            // ActionAttack(object oAttackee, int bPassive = FALSE), last-declared first.
            if (StackPushInteger(vm, 0) == 0 || StackPushObject(vm, targetObjectId) == 0)
            {
                failure = 4;
                return false;
            }

            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedAttack;
            if (dispatch(implementer, RoutineActionAttack, 2) != 0)
            {
                failure = 5;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 6;
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
    /// ROUND 58 DIAGNOSTIC: everything the "attack armed but never swings" investigation
    /// (shared-state-audit.md round 56/57) needs to watch on ONE creature, read together so
    /// the caller does not need <see cref="CreCombatRound"/>'s own private offset. Read-only,
    /// MAIN THREAD ONLY, safe to call every frame — no engine calls, just field reads.
    /// </summary>
    public static void GetAttackDiagnostics(
        nint creature, out int commandable, out int movePermitted, out int headAction,
        out int roundActive, out int roundDelay)
    {
        commandable = 0;
        movePermitted = 0;
        headAction = -1;
        roundActive = 0;
        roundDelay = 0;

        if (creature == 0)
        {
            return;
        }

        commandable = *(int*)(creature + ObjCommandable);
        movePermitted = *(int*)(creature + CreMovePermitted);
        headAction = GetHeadActionType(creature);

        nint round = *(nint*)(creature + CreCombatRound);
        if (round != 0)
        {
            roundActive = *(int*)(round + 0x958);
            roundDelay = *(int*)(round + 0x960);
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

    /// <summary>
    /// Queues the engine's own <c>ActionFollowLeader()</c> on a party companion, so its real
    /// follow-AI (paths, formation slot, gait, animation) takes over from here — exactly what
    /// a recruiting conversation's own ending script fires in single-player.
    ///
    /// ROUND 71 (2026-08-23), docs/research/party-reconciliation.md, shared-state-audit.md
    /// round 68/71: built for <see cref="PartyFollowController"/>. Live-confirmed necessary —
    /// Trask recruited (via a real dialogue, or already recruited by a loaded save) sat with a
    /// genuinely EMPTY action queue (<c>+0xFC</c> head type <c>0xFFFFFFFF</c>) and
    /// <c>+0xA8C</c> (the follow state <c>AIActionPartyFollowLeader</c> itself sets to 3 while
    /// running) reading 1, not 3 — no follow order outstanding, nothing about to reissue one.
    /// The one-time in-party positioning a recruit script or a save's own load leaves behind is
    /// not a standing behaviour; nothing in retail re-arms it on an idle queue by itself
    /// (contrary to this project's earlier assumption that a companion's own heartbeat would).
    ///
    /// Same VM-caller-redirect shape as <see cref="AttackTarget"/>/<see cref="PickUpItem"/>:
    /// OBJECT_SELF becomes the companion for the one dispatch, restored after. No stack push —
    /// <c>ActionFollowLeader</c> takes no parameters.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool QueuePartyFollowLeader(uint creatureObjectId, out int failure)
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

        if (_routineActionFollowLeader < 0)
        {
            _routineActionFollowLeader = FindRoutine(table, ExpectedActionFollowLeader);
        }

        if (_routineActionFollowLeader < 0)
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
            var dispatch = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedActionFollowLeader;
            if (dispatch(implementer, _routineActionFollowLeader, 0) != 0)
            {
                failure = 5;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 6;
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

    /// <summary>
    /// Current hit points, SIGNED.
    ///
    /// ⚠ This used to read a ushort and that was wrong in exactly the state that matters
    /// most. The engine's own getter (vtable +0x9C) returns AX and every caller
    /// sign-extends it (`movsx eax, ax` in CSWSObject::DoDamage 0x004CCF97 and in the
    /// difficulty clamp at 0x004F39E3), and OnApplyDeath parks a dead body at **-11**
    /// (0x004E0D8F). Read unsigned, a downed creature reports 65525 hit points — a number
    /// that sails past every "is this thing hurt" test in the codebase.
    /// </summary>
    public static short GetCurrentHitPoints(nint obj) => *(short*)(obj + ObjCurrentHitPoints);

    public static ushort GetMaxHitPoints(nint obj) => *(ushort*)(obj + ObjMaxHitPoints);

    /// <summary>
    /// True while damage to this object floors at 1 hit point instead of downing it.
    ///
    /// This is not just ours to set: KOTOR's own modules pin the PC with it. The Endar
    /// Spire command deck does it from its area OnEnter (`k_pend_area01`:
    /// `SetMinOneHP(GetFirstPC(), TRUE)`) and only releases it when the player reaches the
    /// starboard section (`k_pend_1b_area`), and the Taris duel ring does the same around
    /// each duel. See docs/research/min-one-hitpoint.md.
    /// </summary>
    public static bool GetMinOneHitPoint(nint obj) => *(int*)(obj + ObjMinOneHitPoint) != 0;

    /// <summary>True while the object is plot-flagged: incoming damage is zeroed outright
    /// and an applied Death effect is dropped.</summary>
    public static bool IsPlot(nint obj) => *(int*)(obj + ObjPlot) != 0;

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

    /// <summary>CServerExoApp::GetDifficultyOption — indexes `diffsettings.2da` by
    /// (row, current difficulty). Returns the byte the table holds, 0 for a blank cell.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int> GetDifficultyOptionFn =
        (delegate* unmanaged[Thiscall]<nint, int, int>)0x004AE9F0;

    /// <summary>`diffsettings.2da` row 5, `MinHP1`.</summary>
    private const int DifficultyOptionMinHitPoint1 = 5;

    /// <summary>
    /// True when the engine's SECOND 1-hit-point floor is armed: the difficulty rule inside
    /// `CSWSCreature::DoDamage` (0x004F39B8) that, for a creature flagged is_pc, reduces any
    /// killing blow to "leave them on 1".
    ///
    /// Reading it costs one engine call and settles an otherwise unanswerable question when
    /// a player will not go down. Static analysis says it can never be on in retail — the
    /// table cell is only filled for the `dmeasy` column and `CServerInfo::difficulty` is
    /// hard-wired to 2 by GetServerInfoFromIniFile (0x004B1BA3), the one and only writer —
    /// but "the binary says it cannot happen" is exactly the claim worth measuring rather
    /// than asserting. See docs/research/min-one-hitpoint.md.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool IsDifficultyMinOneHitPointOn()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return false;
        }

        // ⚠ The engine's own routine dereferences CServerInfo with no null check
        // (0x004B1DF6), and CServerInfo only exists between StartServices and StopServices.
        // Walk the same two hops first and refuse rather than fault on a menu-side call.
        nint internalServer = Deref(server + AppServerOffsetInternal);
        if (internalServer == 0 || Deref(internalServer + ServerInfoOffset) == 0)
        {
            return false;
        }

        return GetDifficultyOptionFn(server, DifficultyOptionMinHitPoint1) == 1;
    }

    /// <summary>
    /// Kills a creature the way the game does, by applying an instant lethal damage
    /// effect: real death animation, corpse, loot and OnDeath script rather than a health
    /// value poked to zero, which would leave an object the engine considers dead but
    /// never ran the death path for.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool KillCreature(uint objectId, out int failure) =>
        ApplyDamageEffect(objectId, 9999, out failure);

    /// <summary>
    /// Applies a plain instant damage effect to an object through the engine's own VM
    /// routines — the same pipeline a real swing uses.
    ///
    /// This is how a hit that landed on a peer's phantom in THEIR game becomes real damage
    /// to this player's actual character: running it as a genuine EffectDamage means the
    /// receiving side applies its own resistances and difficulty scaling, floats the
    /// number, and — at zero — enters the engine's own party temp-death, with no derived
    /// state for us to rebuild by hand.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ApplyDamageEffect(uint objectId, int amount, out int failure)
    {
        // ⚠ Our own damage must not come back to us as a peer's contribution. The damage
        // hook credits any hit whose creator is the local player, and every synthetic hit
        // below is dispatched from a script context that names the player: the mirroring
        // side finishing a mirrored enemy with KillCreature would otherwise report 9999
        // fresh damage to the owner, and the owner applying a peer's damage would count it
        // again as its own. Set here rather than at the call sites so none can forget.
        //
        // The phantom half stays visible on purpose — the test hook stages a real hit on a
        // phantom through this call, and blinding it would blind the verification tool.
        PhantomHitHook.SuppressDealt = true;
        try
        {
            return ApplyDamageEffectCore(objectId, amount, out failure);
        }
        finally
        {
            PhantomHitHook.SuppressDealt = false;
        }
    }

    private static bool ApplyDamageEffectCore(uint objectId, int amount, out int failure)
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
            || StackPushInteger(vm, Math.Clamp(amount, 1, 10000)) == 0)
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

    // ---- module-transition safety -------------------------------------------------
    //
    // The engine's in-world GUI rebuilds the minimap name every frame from the SERVER's
    // module, and dereferences it with NO null check. A transition destroys the server
    // module and only builds the replacement on the next server MainLoop frame — but the
    // outer loop runs the CLIENT MainLoop first, so there is a window in which the map init
    // reads a destroyed module and faults (measured: ~15 ms after the unload, in
    // CResRef::CopyToString).
    //
    // Retail's protection is that the client tears its OWN world down first, which it does
    // when it processes the server's queued "dump player" message. A plugin-driven
    // transition can reach the window before that message is serviced, so the client is
    // still holding a world it thinks is live. Doing what the engine's own handler does —
    // calling CClientExoApp::UnloadModule — closes the window.
    // See docs/research/travel-map-init-crash.md.

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> ClientGetModuleFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x005ED530;

    private static readonly delegate* unmanaged[Thiscall]<nint, void> ClientUnloadModuleFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005ED430;

    /// <summary>
    /// Tears the CLIENT's world down when the SERVER's module is already gone — the exact
    /// call the engine's own dump-player handler makes, just made a frame earlier than the
    /// queued message would. Self-limiting: the engine's later call early-outs, and the
    /// client rebuilds itself from the next Module_Info. Returns true if it acted.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool CloseClientWorldIfServerModuleGone()
    {
        nint server = GetServer();
        nint client = GetClient();
        if (server == 0 || client == 0)
        {
            return false;
        }

        // Server module already destroyed, client still holding one: the unsafe window.
        if (GetModuleFn(server) != 0 || ClientGetModuleFn(client) == 0)
        {
            return false;
        }

        ClientUnloadModuleFn(client);
        return true;
    }

    // ---- faction membership repair -----------------------------------------------
    //
    // A plugin-driven load brings the world up with the player creature in NO faction: the
    // engine's own CSWSCreature::PostProcess adds a PC to the player faction, but silently
    // falls through when the faction manager slot is not yet populated at the moment it
    // runs. Nothing retries, so GetFaction keeps returning null — and seven unguarded
    // dereferences downstream (six GetSingletonParty callers plus GetLeader) fault the
    // instant the server updates client game objects, about half a second after the world
    // appears. See docs/research/world-up-faction-crash.md.
    //
    // The repair is the engine's own membership call, on the engine's own objects, so every
    // derived value (faction id on the stats block, the member array, the client broadcast)
    // is rebuilt by the engine rather than poked by us.

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetCreatureFactionFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x00513FC0;

    private static readonly delegate* unmanaged[Thiscall]<nint, nint> GetFactionManagerFn =
        (delegate* unmanaged[Thiscall]<nint, nint>)0x004AEFC0;

    /// <summary>CSWSFaction::AddMember(objectId, bSilent) — dedups, unlinks any stale
    /// membership, appends, broadcasts the update and writes the stats faction id.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int, void> FactionAddMemberFn =
        (delegate* unmanaged[Thiscall]<nint, uint, int, void>)0x005BFA70;

    /// <summary>CFactionManager + this = the player faction.</summary>
    private const int FactionManagerPlayerFaction = 0x18;

    /// <summary>CGameObject + this = the object id.</summary>
    private const int ObjectIdOffset = 0x04;

    /// <summary>
    /// Ensures the player creature belongs to a faction, repairing it through the engine's
    /// own AddMember if not. Returns a short status for the log: 0 = nothing to do (already
    /// in a faction), 1 = repaired, 2 = no player yet, 3 = no faction manager or no player
    /// faction (new information — the world loaded without factions).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static int EnsurePlayerFaction()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 2;
        }

        nint creature = GetPlayerCreatureFn(server);
        if (creature == 0)
        {
            return 2;
        }

        if (GetCreatureFactionFn(creature) != 0)
        {
            return 0;
        }

        nint manager = GetFactionManagerFn(server);
        if (manager == 0)
        {
            return 3;
        }

        nint playerFaction = *(nint*)(manager + FactionManagerPlayerFaction);
        if (playerFaction == 0)
        {
            return 3;
        }

        FactionAddMemberFn(playerFaction, *(uint*)(creature + ObjectIdOffset), 0);
        return 1;
    }

    // ---- downed state and revival ------------------------------------------------
    //
    // Party members (the PC included) never truly die: CSWSCreature::GetDead returns 0 for
    // anyone in the party, and at HP <= 0 they enter "temp dead" instead — GetDeadTemp
    // 0x004EF890. The engine's own TempDeathUpdate stands them back up once no hostile has
    // been visible for a few seconds. Co-op keeps that machinery and only suppresses the
    // control swap and the game-over screen. See docs/research/death-handling.md.

    /// <summary>CSWSCreature::GetDeadTemp — true while a party member is downed.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, int> GetDeadTempFn =
        (delegate* unmanaged[Thiscall]<nint, int>)0x004EF890;

    /// <summary>CSWSCreature + this = is_raiseable; Resurrection is gated on it.</summary>
    private const int CreatureIsRaiseable = 0xF0;

    /// <summary>EffectResurrection() — zero arguments, builds a type-4 effect.</summary>
    private const nint ExpectedEffectResurrection = 0x00533660;

    private static int _routineEffectResurrection = -1;

    /// <summary>
    /// True while the creature is downed (temp-dead). Party members only ever reach this
    /// state; a non-party creature simply dies.
    ///
    /// ⚠ ROUND 57 CRASH FIX: <c>GetDeadTempFn</c> alone is not reliable for this creature.
    /// GetDeadTemp (0x004EF890) loops the ENGINE's own idea of "the client party" (CSWParty,
    /// not CSWPartyTable) to decide whether the creature it was called on is a member at all,
    /// and party-together's own live diagnostic in this exact codebase ("party together:
    /// CSWParty's own member count read N against CSWPartyTable's M - clamping",
    /// PartyRosterController.SyncClientPartyTogetherState) is direct, repeated evidence that
    /// CSWParty can hold a stale count/membership view relative to the table
    /// <c>party_together_fix</c> corrects against. When the local player's own creature is not
    /// found as a party member by that stale view, GetDeadTemp silently returns false —
    /// including inside the ENGINE's OWN internal copy of this same check
    /// (CSWSEffectListHandler::OnApplyDamage's already-dead early-out calls the identical
    /// function), not just here.
    ///
    /// Live-reproduced 2026-08-22 (docs/research/shared-state-audit.md round 57): an
    /// un-godded client at HP -4/12 (already deeply downed by a first forwarded hit) took a
    /// SECOND forwarded hit while <c>IsDowned</c> — via GetDeadTemp alone — kept reading false;
    /// <see cref="IncomingDamageController"/>'s "no further damage once downed" guard let the
    /// second hit through, the engine's own damage pipeline ran it (confirmed live —
    /// <c>PhantomHitHook</c>'s ground-truth hook logged the hit landing), and the game process
    /// terminated silently moments later: no CrashProbe FIRST-CHANCE line, no managed
    /// AppDomain.UnhandledException, no Windows Application-log crash event — deep enough past
    /// the engine's own "already downed" gate that nothing in this process's crash-reporting
    /// chain ever ran (see CrashProbe.cs's own native-AOT-fail-fast note).
    ///
    /// The fix does not depend on knowing exactly why GetDeadTemp misses the lookup: by the
    /// engine's OWN definition a party member's temp-dead state IS "current hit points <= 0"
    /// (death-handling.md's "Who is 'dead'" — GetDeadTemp itself is <c>GetCurrentHitPoints(0)
    /// &lt;= 0</c> for a creature it recognises as a member), and <see cref="GetCurrentHitPoints"/>
    /// already reads that same raw field (+0xDC) directly, with no party lookup to go stale.
    /// ORing it in can never call a genuinely-still-up creature "downed" (HP is positive, so
    /// this test is simply false) — it only closes the exact window a party-membership miss
    /// left open, for every caller of <see cref="IsDowned"/> at once (spectate's camera
    /// handoff, the control-swap/game-over suppression's own state publish, and party-presence
    /// companion-down detection included).
    /// </summary>
    public static bool IsDowned(nint creature) =>
        creature != 0 && (GetDeadTempFn(creature) != 0 || GetCurrentHitPoints(creature) <= 0);

    /// <summary>
    /// Stands a downed creature back up through the engine's own Resurrection effect — the
    /// only path that restores the AI flags, clears the death animation and tears down a
    /// death GUI. A raw hit-point write does none of that and leaves a prone body with
    /// zeroed AI flags, so it is never used here.
    ///
    /// Hit points are NOT set in the same call: the effect handler may run deferred, and a
    /// top-up landing while the target still reads dead would be dropped by the engine's own
    /// "heal cannot touch the dead" rule. The caller applies them on a later tick.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool ReviveCreature(uint objectId, out int failure)
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

        if (_routineEffectResurrection < 0)
        {
            _routineEffectResurrection = FindRoutine(table, ExpectedEffectResurrection);
            _routineApplyEffect = FindRoutine(table, ExpectedApplyEffectToObject);
        }

        if (_routineEffectResurrection < 0 || _routineApplyEffect < 0)
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

        // ApplyEffectToObject(nDurationType, eEffect, oTarget, fDuration), pushed in reverse
        // declaration order with EffectResurrection dispatched in the middle so its effect
        // lands exactly where the effect argument belongs — the KillCreature shape.
        if (StackPushFloat(vm, 0f) == 0 || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        var effectResurrection = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedEffectResurrection;
        if (effectResurrection(implementer, _routineEffectResurrection, 0) != 0)
        {
            failure = 6;
            return false;
        }

        if (StackPushInteger(vm, 0) == 0)
        {
            failure = 7;
            return false;
        }

        var applyEffect = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedApplyEffectToObject;
        if (applyEffect(implementer, _routineApplyEffect, 4) != 0)
        {
            failure = 8;
            return false;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 9;
        }

        return true;
    }

    /// <summary>Makes a creature raiseable, which the Resurrection handler checks before it
    /// does anything. The engine's own auto-revive writes this same flag raw.</summary>
    public static void SetRaiseable(nint creature) => *(int*)(creature + CreatureIsRaiseable) = 1;

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

    /// <summary>
    /// Moves a phantom to its peer's position for per-step tracking.
    ///
    /// ⚠ The action clear is REQUIRED, not decoration. Measured: calling the position setter
    /// alone leaves the creature exactly where it was — the write is logged, the read is
    /// unchanged — because the engine re-asserts position from the action/movement state.
    /// Only clearing actions first makes it commit, which is why <see cref="SnapTo"/> always
    /// did both. The caller skips this entirely when the body is already in place, so the
    /// clear happens only on frames where the peer actually moved rather than 60 times a
    /// second.
    ///
    /// Safe to drive a phantom this way because it carries no AI — the co-op blueprint blanks
    /// its scripts — and nothing else queues movement for it. Do NOT use it on a real party
    /// member: their follow-AI rewrites position every frame and would fight the write (see
    /// docs/re trap 3). The mirrored animation is applied to the client twin, not the action
    /// queue, so it survives the clear.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static void SetPhantomPosition(nint creature, Vec3 position)
    {
        ClearAllActionsFn(creature, 1);
        SetPositionFn(creature, &position, 1, 0, 0);
    }

    // ---- driving a phantom in real time -------------------------------------------
    //
    // The renderer does NOT read the server creature. It reads the client twin, and the twin
    // does not take the server's position either — it EASES toward it over a fixed 512 ms
    // budget (CSWCCreature::ResetDesiredPositionTimer writes 0x200 into twin+0x1E8, and that
    // is the only value anything ever writes there). So a peer running at speed renders a
    // permanent ~2 units behind where they actually are, no matter how perfectly the server
    // object tracks. That is the larger half of the reported lag; the pathfinding walk was
    // the smaller half.
    //
    // Driving both halves ourselves — the server position, and the twin's desired position
    // with a zero budget so it snaps instead of easing — removes both. Writing the twin's
    // desired position directly also sidesteps the engine's "visible to player" gate, so an
    // off-screen phantom keeps tracking instead of freezing until someone looks at it.
    // See docs/research/realtime-creature-movement.md.

    /// <summary>CSWCCreature + this = the desired position the twin eases toward.</summary>
    private const int TwinDesiredPosition = 0x1DC;

    /// <summary>CSWCCreature + this = milliseconds it is allowed to take. 0 = snap.</summary>
    private const int TwinDesiredPositionBudget = 0x1E8;

    /// <summary>CSWSObject + this = the current animation the gait is read from.</summary>
    private const int ObjAnimationField = 0xD4;

    /// <summary>
    /// Places a phantom exactly where its peer is, this frame, with the gait that matches how
    /// fast they are actually moving.
    ///
    /// <paramref name="speed"/> is measured from the position stream rather than taken from
    /// the peer's animation field, because it is the thing that decides walk-vs-run and the
    /// animation field lags it.
    ///
    /// MAIN THREAD ONLY, and specifically from the SwapBuffers hook: that runs after both
    /// main loops, so these writes are the last word before the next frame consumes them.
    /// </summary>
    /// <param name="moving">Whether the peer is moving AT ALL, decided by the caller with
    /// hysteresis. It is passed in rather than derived from <paramref name="speed"/> because
    /// a threshold on an instantaneous estimate flickers, and a flickering gait is worse than
    /// a wrong one: the engine only pushes an animation to the render side ON CHANGE, so a
    /// value that alternates run/idle several times a second mostly arrives as idle — which
    /// is exactly the body gliding along with no run cycle.</param>
    public static void DrivePhantom(nint creature, Vec3 position, float facing, float speed, bool moving)
    {
        (bool wasRunning, float belowIdleSeconds) = AdvanceGaitHysteresis(creature, speed);
        int want = GaitRule.SelectAnimation(speed, moving, wasRunning, belowIdleSeconds);
        DriveBody(creature, position, facing, speed, moving, want, GaitRule.IsRun(speed, wasRunning));
    }

    /// <summary>
    /// Same drive as <see cref="DrivePhantom"/> for a creature the engine spawned itself —
    /// an enemy mirrored from the owning peer. The one difference is the standing
    /// animation: a phantom at rest is idle, but a mirrored enemy at rest should hold
    /// whatever its owner's engine has it holding (combat-ready stance, casting loop), so
    /// the owner's looping animation id is applied when it is plausible and idle when not.
    /// While moving, the gait still comes from the measured stream speed — the owner's
    /// field lags it, and a wrong gait reads worse than a wrong stance.
    /// </summary>
    /// <param name="bisectMask">ROUND 47: 0 (default) is today's unconditional behaviour,
    /// unaffected — <see cref="PartyPresenceController"/>'s call never passes anything else.
    /// See the bit table on <see cref="ClaimCreature(nint, bool, uint)"/>.</param>
    public static void DriveEnemy(nint creature, Vec3 position, float facing, float speed, bool moving, uint ownerAnimation, uint bisectMask = 0)
    {
        (bool wasRunning, float belowIdleSeconds) = AdvanceGaitHysteresis(creature, speed);

        // NEEDS-LIVE: engine looping ids sit at 0x2710 (10000) and up; anything outside
        // that band here is a read gone wrong or a one-shot that does not belong in a
        // looping field, and idle is the safe substitute.
        int want = moving
            ? GaitRule.SelectAnimation(speed, moving: true, wasRunning, belowIdleSeconds)
            : (ownerAnimation is >= 0x2710 and < 0x2800 ? (int)ownerAnimation : AnimIdle);

        DriveBody(creature, position, facing, speed, moving, want, GaitRule.IsRun(speed, wasRunning), bisectMask);
    }

    private static void DriveBody(nint creature, Vec3 position, float facing, float speed, bool moving, int want, bool run, uint bisectMask = 0)
    {
        if ((bisectMask & BisectBitDriveFacing) == 0)
        {
            Vec3 direction = new() { X = MathF.Cos(facing), Y = MathF.Sin(facing), Z = 0f };
            SetOrientationFn(creature, &direction);
        }

        if ((bisectMask & BisectBitDriveRunMoveState) == 0)
        {
            *(int*)(creature + CreRunFlag) = run ? 1 : 0;
            *(int*)(creature + CreMoveState) = moving ? -1 : 1;
        }

        if ((bisectMask & BisectBitDriveAnimation) == 0 && *(int*)(creature + ObjAnimationField) != want)
        {
            var setAnimation =
                (delegate* unmanaged[Thiscall]<nint, int, void>)(*(nint*)(*(nint*)creature + VtableSetAnimation));
            setAnimation(creature, want);
        }

        // arg3 = 1 SUPPRESSES the position dirty bit, deliberately. When the bit is raised,
        // the AI master's sync pass calls the twin's position handler, which (a) takes its
        // own interpolation step against ours and (b) re-derives the animation playback rate
        // via ComputeSpeedFactor — a function that returns 0.0 for any creature without a
        // waypoint list, which a phantom never has. Rate 0.0 is a run cycle that never
        // advances: the legs freeze mid-pose while the body slides — the "flying" look.
        // We drive the twin ourselves below, so the engine's push is pure interference.
        if ((bisectMask & BisectBitDrivePosition) == 0)
        {
            SetPositionFn(creature, &position, 1, 0, 1);
        }

        // The half that actually renders. Re-read the twin every frame — it is created and
        // destroyed by the engine and a cached pointer would eventually be stale.
        nint twin = (bisectMask & BisectBitDriveTwin) == 0 ? *(nint*)(creature + CreClientTwin) : 0;
        if (twin != 0)
        {
            *(Vec3*)(twin + TwinDesiredPosition) = position;
            *(int*)(twin + TwinDesiredPositionBudget) = 0;

            if (*(nint*)twin == ClientCreatureVtable)
            {
                // ⚠ The gait must be set on the TWIN as well, and specifically through the
                // server→client animation sync handler (twin vtable +0x134) — the entry the
                // engine's own dirty-push uses. It dispatches looping animations (run/walk/
                // idle are looping) to the looping setter that writes twin+0x48, which
                // AnimationUpdate re-asserts from every frame. The plain setter (vtable
                // +0x9C) plays the animation without owning +0x48 and the next frame
                // reverted it — measured live as the twin never leaving idle.
                //
                // ⚠⚠ Assert only when OUR DESIRED GAIT CHANGES — never on "the twin shows
                // something else". A one-shot (weapon flourish, stim, the X key) briefly
                // takes over twin+0x48 while it plays, and the mismatch form re-asserted the
                // run cycle within a frame, visibly cancelling every one-shot on the phantom
                // — reported from play as "the X animation no longer shows". The engine
                // restores the looping gait by itself when a one-shot ends, so leaving the
                // field alone between gait changes costs nothing.
                // ROUND 61 FIX, call #2 of 2: ClientServerSyncHandler_Position (+0x12C) is the
                // ONLY function that ever resolves 0x2712/0x2714 into a real, classifier-
                // approved (AnimationWalking/AnimationRunning) animation and plays it by name —
                // see TwinPositionSyncSlot's doc. The engine itself calls this on EVERY frame a
                // moving creature's position dirty bit is set (i.e. continuously while moving),
                // not once per gait change — DriveBody's bSuppressDirty=1 (the SetPositionFn
                // call above) has always kept it from firing at all. Calling it only on
                // GaitChanged (this fix's first live attempt) made walk display CORRECTLY SOME
                // of the time but not reliably, and run not at all — live-measured evidence
                // that, like the engine's own re-assertion loop in AnimationUpdate, this needs
                // to fire every step to stick, not just on the edge. So: every step, not gated
                // — SUPERSEDED by round 67, see that round's comment on the call site below:
                // once round 65's twin+0x58 write (also below) neutralises the reassertion by
                // itself, per-step firing of THIS call became actively harmful (it restarts the
                // cycle every step — the "stutter" defect) rather than merely redundant, so it
                // is edge-triggered again, this time correctly.
                // Its interpolation-step and rate side effects are overwritten by this same
                // function's unconditional position (above) and rate (below) writes before the
                // next frame ever reads them.
                // ROUND 62: the budget arg matters more than "some sane float" — static
                // disassembly of 0x00611F50 (docs/research/phantom-locomotion.md §9.1) shows
                // the RUNNING branch (AnimationRunning(want)==true) only reaches the real,
                // classifier-approved "play by name" step when budget < 0.5 (fcomp against the
                // constant at 0x0073E9AC, verified live-read as 0.5f). Any budget >= 0.5 —
                // which is exactly what creature+0xD8 (ObjAnimationBudget) reads live, ~1.0,
                // per round 60's own measurement — permanently diverts to the
                // "RunLooksAwful"-branched fallback, which has its own further gates (the
                // animBase+0xCC flag this same call computes from a SEPARATE, twin+0x210-keyed
                // comparison) that do not reliably resolve for a body this handler has never
                // driven before. WALK has no such gate at all (AnimationWalking's branch falls
                // straight into the shared play tail unconditionally) — this asymmetry is
                // exactly why round 61 measured walk partially working and run almost never.
                // We call this handler every step to look like a continuously-moving creature,
                // not a fresh onset — 0f is the semantically-correct "still moving, not a new
                // blend" budget for that, and it is what TwinDesiredPositionBudget already uses
                // for the same reason. Deliberately NOT reusing ObjAnimationBudget here — this
                // call's budget is unrelated to what TwinAnimationSyncSlot passes below, which
                // never reaches this gate (see TwinAnimationSyncSlot's doc, round 61 §8.1).
                // ROUND 65 FIX: neutralise CSWCCreature::AnimationUpdate's own per-frame
                // re-assertion instead of racing it. Static disassembly this round
                // (docs/research/phantom-locomotion.md §12) found the exact re-asserter: every
                // AIUpdate tick that does NOT dispatch to DriveAnimationUpdate/
                // FollowAnimationUpdate (neither applies to a directly-driven phantom/mirrored
                // enemy — no drive flag, no follow target) instead calls the DEFAULT
                // AnimationUpdate (0x0061A980), which — whenever twin+0x48 (TwinCurrentAnimation)
                // is NOT already AnimationStationary — calls CSWCObject::SetAnimation
                // (vtable+0x9C) with whatever id GetLoopingAnimation() (0x0063C100, verified a
                // one-line `return this->+0x58`) returns, UNCONDITIONALLY, every single frame.
                // twin+0x58 is the symbol db's own `looping_animation` field
                // (tools/kotor1_0_3.db: CSWCObject+0x058, ushort) and nothing in this codebase
                // (or, per round 61's own disassembly of TwinAnimationSyncSlot's target, anything
                // reachable for gait ids AT ALL) ever seeds it with 0x2712/0x2714 — it sits at
                // whatever idle/pause id it last legitimately held. So every frame our own
                // TwinPositionSyncSlot call below (or, rarely, some other path) manages to paint
                // a real walk/run id onto twin+0x48, THIS SAME OR THE VERY NEXT AnimationUpdate
                // tick reads the stale idle value out of twin+0x58 and reapplies it — and that
                // reassertion succeeds (unlike a direct walk/run write) because
                // CSWCObject::SetAnimation gates on AnimationLooping(id), and idle/pause rows
                // ARE Looping=1 (animations.2da row 6 "pause1": looping=1) while walkSS/runSS are
                // not (round 62's own finding) — the SAME structural rejection that permanently
                // blocks a direct walk/run write through this path is, in reverse, exactly what
                // lets a STALE idle value win the reassertion every time.
                //
                // The fix: keep twin+0x58 in sync with our own intended gait `want` every step,
                // the same "every step, not gated" discipline TwinPositionSyncSlot already needed
                // (see its own doc). Once twin+0x58 holds 0x2712/0x2714 instead of stale idle,
                // AnimationUpdate's reassertion call becomes `SetAnimation(twin, walkSS/runSS,
                // ...)` — which AnimationLooping structurally rejects, so the call bails WITHOUT
                // touching twin+0x48 (round 61 §8.1's own disassembly: "false -> bail, twin+0x48/
                // +0x58 untouched"). The re-asserter degrades from "guaranteed revert to idle"
                // to "guaranteed no-op" for exactly the two ids this bug is about — a plain,
                // verified-offset memory write, no new engine call, no risk to any other
                // consumer of this field (round 65 xref of GetLoopingAnimation's 8 call sites:
                // the other 7 are one-shot appearance/equip/network-update paths, not a per-frame
                // decider, and this is the SAME field DriveBody already relies on AnimationLooping
                // permanently rejecting for these two ids — see TwinAnimationSyncSlot's doc).
                *(ushort*)(twin + TwinLoopingAnimation) = (ushort)want;

                // ROUND 67 FIX (M1, docs/research/phantom-locomotion.md §13): only invoke
                // ClientServerSyncHandler_Position (TwinPositionSyncSlot, +0x12C) on a gait
                // CHANGE, not every drive step. Full disassembly of the call chain this round
                // (CSWCAnimBase::SetAnimationInternal, 0x0069E8D0, the function +0x12C's walk/
                // run branch always ends at — §13.2) found NO "already playing this id, skip"
                // guard anywhere in it: the generic path (0x0069E987, where every gait id
                // lands, per round 63 §10.2) unconditionally resolves the animation's name and
                // makes the "play" virtual call (`nameObj_vtable+0x18`) EVERY time it is
                // reached — regardless of whether the requested id matches what is already
                // playing. Round 61 §8.5's own comment already noted "its interpolation-step
                // and rate side effects are overwritten by this same function's unconditional
                // position and rate writes before the next frame ever reads them" — i.e.
                // nothing from +0x12C survives to matter except the play call itself. Calling
                // it every drive step (60 Hz, PhantomController.StepIntervalSeconds) therefore
                // restarted the walk/run cycle from frame 0 up to 60 times a second — a
                // structural stutter, not a rare glitch: the exact "you can see the run
                // animation initiated but it just never looks correct" the round 67 brief
                // reports, coexisting with round 65's 0% idle-while-moving measurement because
                // that metric only checks WHICH id is showing, never whether it is playing
                // smoothly.
                //
                // This call is safe to gate on the edge now, in a way it was NOT in round 61
                // (§8.5: an edge-triggered first attempt measured WORSE render reliability than
                // per-step, before round 65 existed): round 65's TwinLoopingAnimation write
                // (immediately above, still unconditional every step) is what keeps
                // AnimationUpdate's per-frame reassertion a no-op now — that no longer depends
                // on +0x12C re-firing. Position (TwinDesiredPosition, above) and the playback
                // rate (TwinAnimationSpeedFactor, below) are also both written unconditionally
                // every step already, independently of this call. So +0x12C's only remaining
                // job is the one-time "start playing this cycle" trigger a genuine gait
                // transition needs — exactly what GaitChanged already tracks for the +0x134
                // skip/diagnostics below, so the two now share one evaluation instead of the
                // stateful GaitChanged being called twice.
                bool gaitChanged = GaitChanged(creature, want);

                if (gaitChanged)
                {
                    var syncPosition =
                        (delegate* unmanaged[Thiscall]<nint, Vec3*, uint, float, void>)(*(nint*)(*(nint*)twin + TwinPositionSyncSlot));

                    // ROUND 62 DIAGNOSTIC (capped): does THIS call's own write to twin+0x48 stick
                    // for even one instant, or does it never take effect at all? Distinguishes
                    // "the play step never fires" from "it fires and something else reverts it
                    // before the next sample" — see phantom-locomotion.md §9.3. Only reachable on
                    // a gait edge now (round 67), so the cap fills far more slowly than before.
                    bool diag = moving && _syncPositionDiagLogged < 60;
                    ushort before = diag ? *(ushort*)(twin + TwinCurrentAnimation) : (ushort)0;

                    syncPosition(twin, &position, (uint)want, TwinPositionSyncBudget);
                    _syncPositionCallCount++;

                    if (diag)
                    {
                        _syncPositionDiagLogged++;
                        ushort after = *(ushort*)(twin + TwinCurrentAnimation);
                        PluginHost.Log(
                            $"sync12c: want 0x{want:X} run={run} before=0x{before:X} "
                            + $"after=0x{after:X} budget={TwinPositionSyncBudget:F2}");
                    }
                }

                if (gaitChanged)
                {
                    // ROUND 63 EXPERIMENT E1 (docs/research/phantom-locomotion.md §10): skip
                    // this call entirely for Walk/Run. Round 61 §8.1 already proved it never
                    // PLAYS a gait id (AnimationLooping structurally rejects 0x2712/0x2714 for
                    // every creature — walking/running are their own animations.2da category,
                    // so CSWCObject::SetAnimation's own AnimationLooping gate always bails
                    // before touching twin+0x48/+0x58 for these two ids specifically) — this
                    // is a provable dead call for Walk/Run, not a guess. The E1 hypothesis was
                    // that it was worse than dead — that re-touching twin+0x58's cache on the
                    // SAME edge frame ClientServerSyncHandler_Position (+0x12C, below) seeds it
                    // stomps that seed before the animbase's per-frame update ever applies it.
                    // Live-measured this round: skipping it changed nothing (96% idle-while-
                    // moving, statistically the same as the pre-E1 baseline) — it is not the
                    // cause, but the call is still proven dead weight for these two ids, so the
                    // skip stays as a no-behaviour-change cleanup. Idle and DriveEnemy's
                    // owner-animation branch (which can pass real combat/overlay ids, not just
                    // the three gait ids) still need this
                    // entry — see PlayOneShotAnimation/MirrorAnimation, which route through it
                    // for one-shots — so the skip is scoped to exactly Walk/Run.
                    if (want != GaitRule.AnimWalk && want != GaitRule.AnimRun)
                    {
                        var syncAnimation =
                            (delegate* unmanaged[Thiscall]<nint, uint, float, void>)(*(nint*)(*(nint*)twin + TwinAnimationSyncSlot));
                        syncAnimation(twin, (uint)want, *(float*)(creature + ObjAnimationBudget));
                    }

                    // ⚠ ROUND 60 DIAGNOSTIC (docs/research/phantom-locomotion.md, live pass
                    // 2026-08-22): +0x134 is confirmed address-correct (live vtable read
                    // matches docs exactly) and confirmed NOT to stick — 132/133 samples at
                    // 40ms cadence during a sustained real run showed server 0xD4 correctly
                    // cycling walk/run, rate nonzero, moveState -1, but twin+0x48 never left
                    // idle. A direct call to this function's own disassembled fallthrough
                    // apply slot (+0xA0) did not fix it either (tested live, reverted) — so
                    // whatever gates the apply is upstream of both. The engine's OWN
                    // classifiers (already proven correct elsewhere in this file for
                    // one-shots, see PlayOneShotAnimation) settle whether this creature's
                    // animbase recognises 0x2710/0x2712/0x2714 as looping/fire-forget/overlay
                    // AT ALL: live-measured answer is looping=0, fireForget=0, overlay=0 for
                    // every one of them — the animbase does not classify these ids as
                    // anything playable, which is consistent with them being SERVER-side
                    // abstract state markers (AnimationWalking/AnimationStationary) rather
                    // than per-model animations.2da rows, and explains why the client-side
                    // sync handler's internal "is this a real animation" gate never opens for
                    // a directly-driven phantom. The real fix needs the per-model row a
                    // normally AI/waypoint-driven creature would resolve through — not found
                    // this pass; kept as a live, capped diagnostic for whoever picks it up.
                    if (_gaitClassLogged < 30)
                    {
                        _gaitClassLogged++;
                        int loop = AnimationLoopingFn(twin, (ushort)want);
                        int fireForget = AnimationFireAndForgetFn(twin, (ushort)want);
                        int overlay = AnimationOverlayFn(twin, (ushort)want);

                        // ROUND 61: AnimationLooping/FireAndForget/Overlay (above) do NOT gate
                        // this call — disassembly of ClientServerSyncHandler_Animation
                        // (0x0060C100) and the plain setter it falls through to
                        // (CSWCObject::SetAnimation, 0x0063E930) shows the real gate is
                        // CSWCAnimBase::AnimationExists (animbase vtable +0x50, 0x0069D910),
                        // which refuses everything while the animbase's own Gob* (+0xB8) is
                        // null. Probe both directly. Also probe AnimationWalking/Running —
                        // the predicates ClientServerSyncHandler_Position (the round-61 +0x12C
                        // fix call, above) actually branches on — to see whether that call is
                        // even reaching its animbase-vtable+0x54 "play" step for this creature.
                        nint animBaseC = *(nint*)(twin + TwinAnimBase);
                        nint gob = animBaseC == 0 ? 0 : *(nint*)(animBaseC + AnimBaseGob);
                        int exists = animBaseC == 0 ? -1 : AnimationExistsFn(animBaseC, (ushort)want);
                        int walking = AnimationWalkingFn(twin, (ushort)want);
                        int running = AnimationRunningFn(twin, (ushort)want);
                        byte weaponClassC = animBaseC == 0 ? (byte)0xFF : *(byte*)(animBaseC + 0xC5);
                        int runLooksAwful = animBaseC == 0 ? -1 : *(int*)(animBaseC + 0xCC);
                        PluginHost.Log(
                            $"gait-class: id 0x{want:X} on twin 0x{twin:X} -> looping={loop} "
                            + $"fireForget={fireForget} overlay={overlay} animBase=0x{animBaseC:X} "
                            + $"gob=0x{gob:X} animationExists={exists} walking={walking} "
                            + $"running={running} weaponClass={weaponClassC} runLooksAwful={runLooksAwful}");
                    }

                    // ROUND 64 X1 (docs/research/phantom-locomotion.md §11): the actual
                    // resolved NAME §10.3 predicted GetAnimationName would substitute idle
                    // for, ground-truthed instead of inferred from the classifier booleans the
                    // block above already logs. Capped at 20 PER CREATURE (not global) —
                    // deliberately its OWN cap (GaitNameLogged), independent of
                    // _gaitClassLogged: that counter is process-lifetime and a long test
                    // session exhausts it long before this diagnostic was added, which would
                    // silently dead-code this one too if it shared the same gate.
                    nint animBase = *(nint*)(twin + TwinAnimBase);
                    int gaitSlot = GaitSlotOf(creature);
                    if (animBase != 0 && gaitSlot >= 0 && GaitNameLogged[gaitSlot] < 20)
                    {
                        GaitNameLogged[gaitSlot]++;
                        byte weaponClass = *(byte*)(animBase + 0xC5);
                        string phantomName = ReadAnimationName(animBase, (ushort)want, out int phantomLen);
                        string model = ReadResRef(animBase + AnimBaseModelName);
                        ushort appearanceRow = *(ushort*)(creature + CreServerAppearanceRow);

                        // The SAME probe on the LOCAL PLAYER's own twin/animbase — the
                        // control this round's brief asked for. GetAnimationName takes no
                        // moveState/velocity input (static disassembly, §10.2/§11: it is a
                        // pure function of (animBase, id)), so sampling it while the local
                        // player happens to be standing still is not a weaker sample than
                        // sampling it mid-stride — there is no "in transit" state for this
                        // specific call to observe.
                        nint myTwin = GetLocalPlayerTwin();
                        nint myAnimBase = myTwin == 0 ? 0 : *(nint*)(myTwin + TwinAnimBase);
                        string myName = "";
                        int myLen = -1;
                        string myModel = "";
                        byte myWeaponClass = 0xFF;
                        if (myAnimBase != 0)
                        {
                            myName = ReadAnimationName(myAnimBase, (ushort)want, out myLen);
                            myModel = ReadResRef(myAnimBase + AnimBaseModelName);
                            myWeaponClass = *(byte*)(myAnimBase + 0xC5);
                        }

                        PluginHost.Log(
                            $"gait-name: id 0x{want:X} phantom-> name='{phantomName}' len={phantomLen} "
                            + $"model='{model}' appearanceRow={appearanceRow} weaponClass={weaponClass} "
                            + $"| player-> name='{myName}' len={myLen} model='{myModel}' "
                            + $"weaponClass={myWeaponClass} animBase=0x{myAnimBase:X}");
                    }
                }

                // The playback rate the engine would have computed from the waypoint list we
                // do not have. 1.0 plays the cycle at its authored rate; scale it by how fast
                // the peer actually covers ground so the feet roughly match the floor.
                float rate = moving
                    ? Math.Clamp(speed / (run ? RunSpeedReference : WalkSpeedReference), 0.6f, 1.5f)
                    : 1.0f;
                *(float*)(twin + TwinAnimationSpeedFactor) = rate;
            }
        }

        // ⚠ WALL CLOCK, NOT A FRAME COUNT — and this is a performance fix, not tidiness.
        //
        // This was `_gaitLogTimer++` against a threshold of 240, on a SINGLE static counter
        // incremented once per DriveCreature call. That is once per DRIVEN BODY per frame, not
        // once per frame, so a client mirroring 32 enemies plus companions crossed the
        // threshold about 33x faster than the number suggests.
        //
        // Measured: ~18,000 gait lines on a client against 21 on the host over the same window
        // (a 2.1 MB log against 98 KB), averaging 22.8 lines/second and peaking at 74. And
        // PluginHost.Log writes each line TWICE — combined log plus channel log — each a full
        // open/write/close, so the client was doing ~46 synchronous file operations a second
        // on the game's frame thread, peaking near 148. On the path whose own contract says
        // allocation-free.
        //
        // A wall-clock gate makes it one line per interval no matter how many bodies are
        // driven, which is what "every so often, tell me what the gait looks like" always
        // meant.
        long nowMs = Environment.TickCount64;
        if (moving && nowMs - _gaitLoggedAtMs >= GaitLogIntervalMs)
        {
            long intervalMs = _gaitLoggedAtMs == 0 ? GaitLogIntervalMs : nowMs - _gaitLoggedAtMs;
            _gaitLoggedAtMs = nowMs;
            nint t = *(nint*)(creature + CreClientTwin);
            nint mine = GetLocalPlayerTwin();
            int syncCalls = _syncPositionCallCount;
            _syncPositionCallCount = 0;
            PluginHost.Log(
                $"gait: speed {speed:F2} -> {(run ? "run" : "walk")} "
                + $"(server 0x{*(int*)(creature + ObjAnimationField):X}, "
                + $"twin 0x{(t == 0 ? 0 : *(ushort*)(t + TwinCurrentAnimation)):X}, "
                + $"rate {(t == 0 ? 0f : *(float*)(t + TwinAnimationSpeedFactor)):F2}, "
                + $"my twin 0x{(mine == 0 ? 0 : *(ushort*)(mine + TwinCurrentAnimation)):X} "
                + $"rate {(mine == 0 ? 0f : *(float*)(mine + TwinAnimationSpeedFactor)):F2}, "
                // ROUND 67: TwinPositionSyncSlot (+0x12C) invocations across every driven body
                // in the last interval — the direct call-rate ground truth for the M1 fix (see
                // docs/research/phantom-locomotion.md §13). Pre-fix this tracked the drive-step
                // rate (up to ~60/s per body); post-fix it should sit near 0 during sustained
                // straight-line movement.
                + $"sync12c-calls {syncCalls} in {intervalMs}ms)");
        }
    }

    /// <summary>When the gait diagnostic last spoke, and how often it may. Seconds, because
    /// this is a "what does the gait look like" sample, not a trace.</summary>
    private static long _gaitLoggedAtMs;

    private const long GaitLogIntervalMs = 5000;

    /// <summary>Caps the round-60 "gait-class" one-time diagnostic (see DriveBody) so it
    /// prints a handful of samples across process lifetime, not once per gait change.</summary>
    private static int _gaitClassLogged;

    /// <summary>Caps the round-62 "sync12c" before/after diagnostic (see DriveBody).</summary>
    private static int _syncPositionDiagLogged;

    /// <summary>ROUND 67 (docs/research/phantom-locomotion.md §13): counts every actual
    /// invocation of TwinPositionSyncSlot (+0x12C), across every driven body, since the last
    /// time the periodic gait log line reset it. Direct, ground-truth evidence of the fix: a
    /// pre-fix trace shows this near the driven-body count times the ~60 Hz step rate over the
    /// log interval (every step re-triggers the play call); post-fix it should sit near zero
    /// during sustained straight-line movement and only spike on an actual walk/idle/run
    /// transition.</summary>
    private static int _syncPositionCallCount;

    /// <summary>Last gait asserted per driven creature, so the twin call fires on change
    /// only. Fixed slots rather than a dictionary: at most 8 phantoms plus 32 mirrored
    /// enemies exist, and this runs on the game's frame hook where allocation is unwelcome.</summary>
    private static readonly nint[] GaitCreature = new nint[48];
    private static readonly int[] GaitLast = new int[48];

    /// <summary>ROUND 64 X1: per-creature cap for the "gait-name" diagnostic (see DriveBody) —
    /// parallels <see cref="GaitCreature"/> by slot index, so each driven body gets its own
    /// first-20 budget instead of sharing one global counter.</summary>
    private static readonly int[] GaitNameLogged = new int[48];

    private static bool GaitChanged(nint creature, int want)
    {
        int free = -1;
        for (int i = 0; i < GaitCreature.Length; i++)
        {
            if (GaitCreature[i] == creature)
            {
                if (!GaitRule.ShouldReissue(GaitLast[i], want))
                {
                    return false;
                }

                GaitLast[i] = want;
                return true;
            }

            if (GaitCreature[i] == 0 && free < 0)
            {
                free = i;
            }
        }

        int slot = free >= 0 ? free : 0;
        GaitCreature[slot] = creature;
        GaitLast[slot] = want;
        GaitNameLogged[slot] = 0;
        return true;
    }

    /// <summary>ROUND 64 X1: the slot <see cref="GaitCreature"/> is holding this creature in,
    /// or -1. Only meaningful right after <see cref="GaitChanged"/> returned true for the same
    /// creature (which guarantees a slot exists).</summary>
    private static int GaitSlotOf(nint creature)
    {
        for (int i = 0; i < GaitCreature.Length; i++)
        {
            if (GaitCreature[i] == creature)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Forget a creature's gait memory (call when a phantom is destroyed, so a
    /// recycled pointer does not inherit stale state).</summary>
    public static void ForgetGait(nint creature)
    {
        for (int i = 0; i < GaitCreature.Length; i++)
        {
            if (GaitCreature[i] == creature)
            {
                GaitCreature[i] = 0;
                GaitLast[i] = 0;
                GaitNameLogged[i] = 0;
            }
        }

        for (int i = 0; i < GaitHysteresisCreature.Length; i++)
        {
            if (GaitHysteresisCreature[i] == creature)
            {
                GaitHysteresisCreature[i] = 0;
                GaitBelowIdleAccum[i] = 0f;
                GaitHysteresisLastStepMs[i] = 0;
            }
        }
    }

    /// <summary>ROUND 68 (docs/research/phantom-locomotion.md §14): per-driven-creature
    /// walk/run hysteresis memory, parallel to but separate from GaitCreature/GaitLast — those
    /// only populate once a client twin has been read inside DriveBody (see the ClientTwin
    /// gate above GaitChanged's call site), while this must exist from the very first drive
    /// step so the idle-dwell accumulator starts counting immediately rather than after the
    /// first twin-bearing frame.</summary>
    private static readonly nint[] GaitHysteresisCreature = new nint[48];

    /// <summary>Seconds this creature's speed has been continuously under
    /// GaitRule.IdleSpeedThreshold — see GaitRule.AdvanceBelowIdle.</summary>
    private static readonly float[] GaitBelowIdleAccum = new float[48];

    /// <summary>Wall-clock time (Environment.TickCount64) of this creature's previous
    /// AdvanceGaitHysteresis call, 0 meaning "no previous step" (first-ever sample gets dt=0,
    /// never a spurious huge dt from an uninitialised slot).</summary>
    private static readonly long[] GaitHysteresisLastStepMs = new long[48];

    private static int GaitHysteresisSlotOf(nint creature)
    {
        int free = -1;
        for (int i = 0; i < GaitHysteresisCreature.Length; i++)
        {
            if (GaitHysteresisCreature[i] == creature)
            {
                return i;
            }

            if (GaitHysteresisCreature[i] == 0 && free < 0)
            {
                free = i;
            }
        }

        int slot = free >= 0 ? free : 0;
        GaitHysteresisCreature[slot] = creature;
        GaitBelowIdleAccum[slot] = 0f;
        GaitHysteresisLastStepMs[slot] = 0;
        return slot;
    }

    /// <summary>
    /// Reads and advances a driven creature's round-68 hysteresis memory for this step:
    /// whether it was previously classified as running — from GaitLast, which only changes on
    /// an actual reissue (the last gait genuinely asserted to the engine), not a value
    /// computed and then discarded on an early return — and the updated "seconds continuously
    /// under GaitRule.IdleSpeedThreshold" accumulator (GaitRule.AdvanceBelowIdle). A creature
    /// with no GaitLast slot yet (no client twin read for it this session) reads as
    /// wasRunning=false, the same "no history" default GaitRule.IsRun already documents.
    /// MAIN THREAD ONLY, same as DriveBody/DrivePhantom/DriveEnemy.
    /// </summary>
    private static (bool WasRunning, float BelowIdleSeconds) AdvanceGaitHysteresis(nint creature, float speed)
    {
        int gaitSlot = GaitSlotOf(creature);
        bool wasRunning = gaitSlot >= 0 && GaitLast[gaitSlot] == GaitRule.AnimRun;

        int slot = GaitHysteresisSlotOf(creature);
        long nowMs = Environment.TickCount64;
        long lastMs = GaitHysteresisLastStepMs[slot];
        float dt = lastMs == 0 ? 0f : (nowMs - lastMs) / 1000f;
        GaitHysteresisLastStepMs[slot] = nowMs;

        float accum = GaitRule.AdvanceBelowIdle(GaitBelowIdleAccum[slot], speed, dt);
        GaitBelowIdleAccum[slot] = accum;

        return (wasRunning, accum);
    }

    /// <summary>CSWCObject + this = the animation actually being rendered (u16).</summary>
    private const int TwinCurrentAnimation = 0x48;

    /// <summary>
    /// ROUND 65: CSWCObject + this = <c>looping_animation</c> (u16), the symbol db's own field
    /// name for tools/kotor1_0_3.db's CSWCObject offset table. This is exactly what
    /// <c>CSWCObject::GetLoopingAnimation</c> (0x0063C100, verified live-disassembled this
    /// round: <c>mov ax,[ecx+0x58]; ret</c>, a one-line getter of this field and nothing else)
    /// returns, and it is what <c>CSWCCreature::AnimationUpdate</c>'s per-frame re-assertion
    /// reads and reapplies via <c>SetAnimation</c> (vtable +0x9C) whenever
    /// <see cref="TwinCurrentAnimation"/> is not already "stationary" — see the write site in
    /// <c>DriveBody</c> (just above <see cref="TwinPositionSyncSlot"/>'s call) for the full
    /// mechanism, and docs/research/phantom-locomotion.md §12.
    /// </summary>
    private const int TwinLoopingAnimation = 0x58;

    /// <summary>
    /// CSWCObject vtable +0x134 = ClientServerSyncHandler_Animation(anim, budget) — the entry
    /// the engine's server→client dirty-push calls. Routes looping animations to the setter
    /// that owns twin+0x48; calling the plain setter (+0x9C) instead does not stick.
    /// </summary>
    private const int TwinAnimationSyncSlot = 0x134;

    /// <summary>
    /// ROUND 61 FIX: CSWCObject vtable +0x12C = ClientServerSyncHandler_Position(Vector*
    /// desiredPos, uint serverAnimId, float budget), ret 0xC — 0x00611F50, the entry the
    /// engine's server→client dirty-push calls for the POSITION bit (dirty bit 1), which
    /// DriveBody has always deliberately suppressed (see the comment on
    /// <see cref="BisectBitDrivePosition"/>'s call site below) because its OWN interpolation
    /// step and its <c>ComputeSpeedFactor</c> re-derivation return 0.0 for a waypoint-less
    /// phantom — the historical "flying" bug.
    ///
    /// What that suppression ALSO threw out, undiscovered until this pass: static
    /// disassembly of 0x00611F50 shows it is the ONLY place in the engine that calls
    /// <c>CSWCObject::AnimationWalking</c> (0x0063C240) / <c>AnimationRunning</c> (0x0063C2A0)
    /// against the SERVER's animation id (the third arg) and, on a match, resolves and plays
    /// the walk/run animation BY NAME through the "Walking"/"Running" columns of
    /// animations.2da — a completely different mechanism from
    /// <see cref="TwinAnimationSyncSlot"/>'s id-based <c>AnimationLooping</c> gate, which
    /// (ROUND 61, see <see cref="AnimationExistsFn"/>'s doc) structurally rejects gait ids for
    /// every creature: <c>animations.2da</c> rows 338 "walkSS" and 345 "runSS" (what
    /// <c>ServerToClientAnimation</c> resolves 0x2712/0x2714 to for weapon class 2, live-
    /// confirmed) both have <c>Looping = 0</c> — walking/running are their own 2DA category,
    /// never "looping". <c>CSWCCreature::AnimationUpdate</c> (0x0061A980) never selects a new
    /// gait itself; it only re-asserts whatever is already cached at twin+0x58 each frame, so
    /// if nothing ever seeds that cache with the walk/run row, nothing ever will — exactly the
    /// "moves in sync but slides" symptom, on every driven body, always.
    ///
    /// Calling this handler ourselves — with our OWN position/id/budget, immediately followed
    /// by the SAME authoritative twin+0x1DC/+0x1E8/+0x1F8 overwrites <see cref="DriveBody"/>
    /// already performs every step — lets its internal AnimationWalking/Running dispatch run
    /// while the bad interpolation/rate side effects it also produces are overwritten before
    /// the next frame ever reads them. NEEDS-LIVE verification that this actually plays the
    /// gait (see docs/research/phantom-locomotion.md round 61).
    /// </summary>
    private const int TwinPositionSyncSlot = 0x12C;

    /// <summary>
    /// ROUND 62: the budget passed to <see cref="TwinPositionSyncSlot"/>'s call specifically —
    /// NOT <see cref="ObjAnimationBudget"/> (which reads ~1.0 live and permanently fails the
    /// run-branch's budget &lt; 0.5 gate at 0x0073E9AC, docs/research/phantom-locomotion.md
    /// §9.1/§9.2). 0f matches every other "this is a snap/continuous update, not a fresh
    /// blend" budget already used in this method (<see cref="TwinDesiredPositionBudget"/>).
    /// </summary>
    private const float TwinPositionSyncBudget = 0f;

    /// <summary>CSWSObject + this = float forwarded with the engine's animation sync.</summary>
    private const int ObjAnimationBudget = 0xD8;

    /// <summary>
    /// CSWCCreature + this = animation playback-rate scale. The engine computes it from the
    /// waypoint list (ComputeSpeedFactor) and gets 0.0 for a creature without one — frozen
    /// legs — so a phantom's is written by us every frame instead.
    /// </summary>
    private const int TwinAnimationSpeedFactor = 0x1F8;

    /// <summary>appearance.2da row 114 rundist / walkdist — world units per second at rate 1.0.</summary>
    private const float RunSpeedReference = 3.96f;
    private const float WalkSpeedReference = 1.6f;

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

    // Fire-and-forget animations (draw weapon, attack swings, flourishes — the "x" key) live
    // on the client twin's queue, not the server +0xD4 field, so SetAnimation cannot replay
    // them reliably. CSWCCreature::AddFireForgetAnimation is the engine's own replication call
    // for these (docs/research/animation-sync.md): thiscall(this=twin, uint anim, void* opts).
    private static readonly delegate* unmanaged[Thiscall]<nint, uint, nint, void> AddFireForgetAnimationFn =
        (delegate* unmanaged[Thiscall]<nint, uint, nint, void>)0x006183D0;

    // Removed on purpose, and worth not reintroducing:
    //
    //   GetTwinCurrentAnimation — read `*(uint*)(twin + 0x48)`. That field is a USHORT and
    //     +0x4A is a separate ushort (the queued animation), so the read returned
    //     queued<<16 | played. Use a 16-bit read if this is ever needed again.
    //   PlayFireForget — queued any id on the twin with no classification. A looping or
    //     overlay id sent down the fire-and-forget path parks with no expiry and wedges the
    //     body. PlayOneShotAnimation is the guarded replacement and the only caller's route.

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

    // ---- the companion roster -------------------------------------------------------
    //
    // KOTOR keeps two separate facts about a companion, and conflating them is the first
    // mistake available here:
    //
    //   AVAILABLE — the story has handed this companion over. Party table +0x30, int[9];
    //     CSWPartyTable::AddNPC writes the literal 1 at 0x00564350. Every roster operation
    //     refuses a slot whose entry is 0.
    //   IN THE PARTY — one of the at most TWO companions currently walking behind the player.
    //     Party table +0x00 is the count (AddMember refuses at 2: `cmp dword ptr [esi],2 /
    //     je refuse`, 0x00565633) and +0x04 is int[2] of NPC SLOT INDICES — slot indices, not
    //     object ids, which is the trap docs/research/party-reconciliation.md records.
    //
    // The layout below is that document's CORRECTED map, not the symbol DB's: the DB's
    // CSWPartyTable offsets are shifted (it calls +0x04 an int[11] of "member ids"), and the
    // shipped PartyPresenceController has been reading +0x0C as the object-id array against a
    // live game since it was written. Everything here is re-derived from the binary anyway,
    // with the address of the instruction that proves each one.
    //
    // ⚠ EVERY WRITE GOES THROUGH THE ENGINE'S OWN NWSCRIPT ROUTINES. Poking +0x04 and +0x0C
    // directly would produce a party table that looks right and a game that is not: AddMember
    // alone touches the object's in-party flag, the follow state, the GUI portraits and the
    // client-side party list, and RemovePartyMember additionally clears solo mode. This is the
    // repo's standing rule (rebuild derived state through engine calls) and here it is not a
    // preference — there is no reachable list of what else would need rebuilding.

    // The party table itself is reached with the file's existing PartyTableOffset (0x1B770 off
    // CServerExoAppInternal), verified in the journal section from CServerExoApp::GetPartyTable
    // 0x004AEE70 — the same accessor every roster routine in the engine opens with.
    // PartyPresenceController keeps its own copy of these constants; that file is not this
    // change's to edit, and the two agree.

    /// <summary>CSWPartyTable + this = the count of companions in the active party (0..2).</summary>
    private const int PartyMemberCount = 0x00;

    /// <summary>CSWPartyTable + this = int[2] of NPC SLOT INDICES, one per active companion.
    /// AddMember writes `[esi + count*4 + 4] = nNPC` at 0x005656B5.</summary>
    private const int PartyMemberSlots = 0x04;

    /// <summary>CSWPartyTable + this = int[9] spawned server object id per NPC slot.
    /// AddMember writes `[esi + nNPC*4 + 0xC] = oCreature` at 0x005656B9.</summary>
    private const int PartyObjectIds = 0x0C;

    /// <summary>CSWPartyTable + this = int[9] availability. AddMember refuses a zero entry at
    /// 0x00565651, RemoveMember at 0x00565581, AddNPC sets it to 1 at 0x00564350.</summary>
    private const int PartyAvailable = 0x30;

    /// <summary>
    /// CSWPartyTable + this = the "a script is driving one specific party member" field, -1
    /// when nothing is.
    ///
    /// ⚠ THE FIRST THING BOTH ROSTER ROUTINES TEST. AddMember (0x00565625) and RemoveMember
    /// (0x00565565) each open with `cmp dword ptr [esi+0xF0], -1 / jne refuse`, before they
    /// look at anything else. Reconciling during one of those scenes is not dangerous, it is
    /// simply refused — so the caller checks it here and waits, rather than spending a VM
    /// dispatch per pass on a call the engine has already decided to reject.
    /// </summary>
    private const int PartyControlledNpc = 0xF0;

    /// <summary>Roster slots. The engine's bound: AddMember refuses nNPC &lt; 0 (0x00565640)
    /// or &gt;= 9 (0x00565648).</summary>
    public const int PartyRosterSlots = 9;

    /// <summary>Companions in the active party at once. AddMember refuses at 2 (0x00565633).</summary>
    public const int PartyMaxCompanions = 2;

    // CSWVirtualMachineCommands::ExecuteCommandAddPartyMember(nCommandId, nParams).
    //
    // int AddPartyMember(int nNPC, object oCreature). Read off the handler rather than off a
    // header: it pops an INTEGER (CVirtualMachine::StackPopInteger 0x005D1000) at 0x0052DE7E
    // and then an OBJECT (StackPopObject 0x005D10C0) at 0x0052DE9D, and hands them to
    // CSWPartyTable::AddMember(nNPC, oCreature) at 0x0052DEC4 in that order. A VM pop yields
    // arguments in DECLARATION order, so the pushes below go in reverse.
    //
    // ⚠ Declared as a literal-address cast, not via a named constant, so tools/callaudit.py's
    // scanner actually sees it: it matches `)0xADDR` and would silently skip a declaration
    // written the other way. The callee is `ret 8` — two stack arguments past `this` — which is
    // what this declaration passes.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int> ExecuteCommandAddPartyMember =
        (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x0052DE70;

    // CSWVirtualMachineCommands::ExecuteCommandRemovePartyMember(nCommandId, nParams).
    //
    // int RemovePartyMember(int nNPC). One integer pop at 0x00541C0C, then
    // CSWPartyTable::GetNPCObject(nNPC, 0, 1) at 0x00541C3F and RemoveMember(nNPC) at
    // 0x00541C4D. Also `ret 8`.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int> ExecuteCommandRemovePartyMember =
        (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x00541C00;

    // CSWVirtualMachineCommands::ExecuteCommandSpawnAvailableNPC(nCommandId, nParams).
    //
    // object SpawnAvailableNPC(int nNPC, location lLoc). Integer pop at 0x00543EDE, then an
    // ENGINE STRUCTURE of type 2 (location) at 0x00543EF4 via StackPopEngineStructure
    // 0x005D10A0; calls CSWPartyTable::SpawnNPC at 0x00543F2A and pushes the resulting object
    // back with StackPushObject at 0x00543F36. Also `ret 8`.
    //
    // This is the engine's own "put a recruited companion into the world" entry point, and it
    // is here because AddPartyMember needs a live object: CSWPartyTable::AddMember validates
    // the id through CServerExoApp::GetGameObject (0x00565666) and refuses an unresolvable
    // one. The GUI does exactly this pair — CSWGuiPartySelection::AcceptParty calls SpawnNPC
    // at 0x006BE90C and AddMember at 0x006BEA25 — so the sequence below is the shipped one.
    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, int> ExecuteCommandSpawnAvailableNPC =
        (delegate* unmanaged[Thiscall]<nint, int, int, int>)0x00543ED0;

    private static int _routineAddPartyMember = -1;
    private static int _routineRemovePartyMember = -1;
    private static int _routineSpawnAvailableNpc = -1;

    /// <summary>The live CSWPartyTable, or 0 before a world exists (at the main menu, and
    /// through a module transition).</summary>
    private static nint GetPartyTable()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint serverInternal = Deref(server + AppServerOffsetInternal);
        return serverInternal == 0 ? 0 : serverInternal + PartyTableOffset;
    }

    /// <summary>
    /// Reads this instance's roster as two nine-bit masks. False when there is no party table
    /// to read, which is NOT an empty roster and must never be published as one.
    /// </summary>
    public static bool TryReadPartyRoster(out uint availableMask, out uint partyMask)
    {
        availableMask = 0;
        partyMask = 0;

        nint table = GetPartyTable();
        if (table == 0)
        {
            return false;
        }

        for (int slot = 0; slot < PartyRosterSlots; slot++)
        {
            if (*(int*)(table + PartyAvailable + (slot * 4)) != 0)
            {
                availableMask |= 1u << slot;
            }
        }

        // The membership list is walked rather than indexed: it is a DENSE list of slot
        // indices (members[0..count-1]), not a per-slot flag, so entry i says nothing about
        // slot i. Reading it as a flag array is the same category of mistake as reading +0x04
        // as object ids.
        int count = *(int*)(table + PartyMemberCount);
        if (count is < 0 or > PartyMaxCompanions)
        {
            // A count outside the engine's own bound means this is not a party table (a
            // transition frame, a pointer that has moved). Refuse the whole reading rather
            // than publish a partial one.
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            int slot = *(int*)(table + PartyMemberSlots + (i * 4));
            if (slot is >= 0 and < PartyRosterSlots)
            {
                partyMask |= 1u << slot;
            }
        }

        return true;
    }

    /// <summary>
    /// Raw-pokes this instance's own availability flag for one NPC slot — the write half of
    /// <see cref="TryReadPartyRoster"/>'s read, used only by the dialogue recruitment-sync path
    /// (docs/research/dialogue-consequences.md §4.2, coop.ini <c>dialogue_recruit_sync</c>).
    ///
    /// Mirrors the literal store <c>AddAvailableNPCByTemplate</c> performs at the same offset
    /// (<c>*(int*)(table + PartyAvailable + slot*4) = 1</c> at <c>0x00564350</c> — VERIFIED, the
    /// same evidence <see cref="PartyAvailable"/>'s own doc comment already cites:
    /// <c>AddMember</c>/<c>RemoveMember</c> testing this exact array for a zero entry). What is
    /// **NEEDS-LIVE**, per the design doc's own biggest open risk, is whether the flag ALONE is
    /// enough for <see cref="SpawnAvailableNpc"/> to resolve a template afterward, or whether
    /// <c>AddAvailableNPCByTemplate</c> also records a second association elsewhere in the
    /// table that this raw poke does not touch.
    /// </summary>
    public static bool TrySetSlotAvailable(int slot)
    {
        nint table = GetPartyTable();
        if (table == 0 || slot is < 0 or >= PartyRosterSlots)
        {
            return false;
        }

        *(int*)(table + PartyAvailable + (slot * 4)) = 1;
        return true;
    }

    /// <summary>This instance's spawned object id for an NPC slot, or 0 / OBJECT_INVALID when
    /// that companion has no body in the world right now.</summary>
    public static uint GetPartyRosterObjectId(int slot)
    {
        nint table = GetPartyTable();
        return table == 0 || slot is < 0 or >= PartyRosterSlots
            ? 0
            : *(uint*)(table + PartyObjectIds + (slot * 4));
    }

    /// <summary>
    /// True while a script owns one specific party member and the engine will refuse every
    /// roster change (party table +0xF0 != -1; tested first by both AddMember and RemoveMember).
    /// Also true when there is no party table at all, because "cannot change the roster" is the
    /// answer in both cases.
    /// </summary>
    public static bool IsPartyRosterFrozen()
    {
        nint table = GetPartyTable();
        return table == 0 || *(int*)(table + PartyControlledNpc) != -1;
    }

    /// <summary>
    /// Dispatches a zero-return NWScript roster routine, with the same guard structure as
    /// <see cref="SpawnCreature"/>: the routine index is found by VALUE in the live routine
    /// table (never by a guessed nwscript number), the VM must not be mid-script, and the whole
    /// sequence must be stack-neutral.
    ///
    /// Resolves everything and hands the pieces back; the caller pushes its own arguments and
    /// dispatches, because the argument count is part of the call and passing a wrong nParams
    /// makes the handler pop a stack that is not there.
    /// </summary>
    private static bool DispatchRosterRoutine(
        delegate* unmanaged[Thiscall]<nint, int, int, int> handler,
        ref int cachedRoutine,
        out nint vm,
        out nint internalVm,
        out int stackPointer,
        out nint implementer,
        out int failure)
    {
        vm = 0;
        internalVm = 0;
        stackPointer = 0;
        failure = 0;

        implementer = GetImplementer(out vm, out internalVm);
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

        if (cachedRoutine < 0)
        {
            cachedRoutine = FindRoutine(table, (nint)handler);
        }

        if (cachedRoutine < 0)
        {
            failure = 3;
            return false;
        }

        // ⚠ Never dispatch while a script is executing — the same guard SpawnCreature carries,
        // and it matters more here: a recruitment conversation's own script is EXACTLY what is
        // running when the roster changes, and re-entering the VM underneath it would corrupt
        // the stack it is mid-way through using.
        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        stackPointer = *(int*)(internalVm + StackPointerOffset);
        return true;
    }

    /// <summary>
    /// Puts NPC slot <paramref name="slot"/> into the active party through the engine's own
    /// AddPartyMember(int nNPC, object oCreature).
    ///
    /// <paramref name="objectId"/> must be a LIVE creature: AddMember resolves it through
    /// CServerExoApp::GetGameObject at 0x00565666 and refuses an id that does not resolve. Use
    /// <see cref="SpawnAvailableNpc"/> first when the slot has no body.
    ///
    /// MAIN THREAD ONLY. Failure codes: 1 no VM, 2 no routine table, 3 routine not found,
    /// 4 a script is running, 5 a push was refused, 6 the handler refused, 7 the return could
    /// not be popped, 8 the stack was left skewed, 9 the engine returned "not added".
    /// </summary>
    public static bool AddPartyMemberBySlot(int slot, uint objectId, out int failure)
    {
        failure = 0;

        if (slot is < 0 or >= PartyRosterSlots || objectId is 0 or ObjectInvalid)
        {
            failure = 10;
            return false;
        }

        if (!DispatchRosterRoutine(
            ExecuteCommandAddPartyMember, ref _routineAddPartyMember,
            out nint vm, out nint internalVm, out int stackPointer, out nint implementer, out failure))
        {
            return false;
        }

        // Push in REVERSE declaration order: AddPartyMember(int nNPC, object oCreature), so
        // the object goes down first and the slot index on top. Confirmed against the handler,
        // which pops the integer BEFORE the object (0x0052DE7E then 0x0052DE9D).
        if (StackPushObject(vm, objectId) == 0 || StackPushInteger(vm, slot) == 0)
        {
            failure = 5;
            return false;
        }

        if (ExecuteCommandAddPartyMember(implementer, _routineAddPartyMember, 2) != 0)
        {
            failure = 6;
            return false;
        }

        // The routine returns int, and the handler pushes it back onto the VM stack. It MUST
        // be popped even when it says "refused", or the next dispatch inherits a stack with an
        // extra word on it — which is how a channel that merely does not work becomes a
        // channel that corrupts the next script to run.
        int added = 0;
        if (StackPopInteger(vm, &added) == 0)
        {
            failure = 7;
            return false;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
            return false;
        }

        if (added == 0)
        {
            failure = 9;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Takes NPC slot <paramref name="slot"/> out of the active party through the engine's own
    /// RemovePartyMember(int nNPC). The companion stays RECRUITED — this is the party seat, not
    /// the roster entry.
    ///
    /// MAIN THREAD ONLY. Same failure codes as <see cref="AddPartyMemberBySlot"/>.
    /// </summary>
    public static bool RemovePartyMemberBySlot(int slot, out int failure)
    {
        failure = 0;

        if (slot is < 0 or >= PartyRosterSlots)
        {
            failure = 10;
            return false;
        }

        if (!DispatchRosterRoutine(
            ExecuteCommandRemovePartyMember, ref _routineRemovePartyMember,
            out nint vm, out nint internalVm, out int stackPointer, out nint implementer, out failure))
        {
            return false;
        }

        if (StackPushInteger(vm, slot) == 0)
        {
            failure = 5;
            return false;
        }

        if (ExecuteCommandRemovePartyMember(implementer, _routineRemovePartyMember, 1) != 0)
        {
            failure = 6;
            return false;
        }

        int removed = 0;
        if (StackPopInteger(vm, &removed) == 0)
        {
            failure = 7;
            return false;
        }

        if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
        {
            failure = 8;
            return false;
        }

        if (removed == 0)
        {
            failure = 9;
            return false;
        }

        return true;
    }

    /// <summary>
    /// Puts a recruited companion's body into the world at <paramref name="position"/> through
    /// the engine's own SpawnAvailableNPC(int nNPC, location lLoc), and returns its object id
    /// (or <see cref="ObjectInvalid"/>).
    ///
    /// Only needed when a slot is available here but has no body — the case a client reaches
    /// when it learns about a recruitment it did not itself perform. The body is placed at the
    /// caller's chosen point and then driven to wherever the owner has it by the companion
    /// mirror on the next pass, so the position is a starting point, not a claim.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static uint SpawnAvailableNpc(int slot, Vec3 position, out int failure)
    {
        failure = 0;

        if (slot is < 0 or >= PartyRosterSlots)
        {
            failure = 10;
            return ObjectInvalid;
        }

        if (!DispatchRosterRoutine(
            ExecuteCommandSpawnAvailableNPC, ref _routineSpawnAvailableNpc,
            out nint vm, out nint internalVm, out int stackPointer, out nint implementer, out failure))
        {
            return ObjectInvalid;
        }

        ScriptLocation location = new()
        {
            Position = position,
            Orientation = new Vec3 { X = 1f, Y = 0f, Z = 0f },
        };

        // Reverse declaration order again: SpawnAvailableNPC(int nNPC, location lLoc).
        if (StackPushEngineStructure(vm, EngineStructureLocation, &location) == 0
            || StackPushInteger(vm, slot) == 0)
        {
            failure = 5;
            return ObjectInvalid;
        }

        if (ExecuteCommandSpawnAvailableNPC(implementer, _routineSpawnAvailableNpc, 2) != 0)
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
}

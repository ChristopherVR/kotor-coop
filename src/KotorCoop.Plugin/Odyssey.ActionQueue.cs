// Odyssey.ActionQueue.cs - reads a creature's CSWSObject action queue as a queue
// (head action type, claim/clear helpers) rather than wiping it blind, plus the
// object-array resolution helpers used to disambiguate object lookups.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
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

}

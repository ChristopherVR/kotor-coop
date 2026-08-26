// Odyssey.Combat.Attack.cs — part 2 of combat state: owns QueueAttackAction, the entry
// point that arms a driven creature's native combat round and hands off to the engine's
// own CreateNewAttackActions so a mirrored/AI-driven attacker actually swings. See
// docs/research/combat-participation.md for the underlying engine mechanics this method
// drives, and docs/research/shared-state-audit.md for the alternate script-firing approach
// defined at the bottom of this file.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    public static void QueueAttackAction(nint creature, uint targetId)
    {
        // TEMPORARY DIAGNOSTIC: throttled (~3s) per-branch logging, so a silent no-op can be
        // told from "it fired and the engine ignored it." See
        // docs/research/combat-participation.md.
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

        // The round-init writes below (StartCombatRound, the round-timer setters, and the
        // queued-action call) must run exactly ONCE, on the genuine 0->1 transition, not on
        // every drive pass. CONFIRMED LIVE: SetRoundDelayFn accumulates rather than sets —
        // round+0x960 climbed by exactly +1500 on every call — so calling it every pass pushes
        // the round's due time into the future faster than real time can catch up, and
        // AddAttackActionFn has no dedup of its own, so an unconditional per-pass call appends
        // an ever-growing backlog to round+0x9B0. The IsFightAction(headType) gate at the top
        // of this function is what makes a single arm-once call sufficient for every later
        // pass — matching the one case that worked before this (a single staged console
        // command, never repeated). See docs/research/combat-participation.md.
        if (roundTimerBefore == 0)
        {
            // StartCombatRoundFn must run FIRST, matching the engine's own program order
            // (0x005BC480-0x5BC537 calls it before either setter below) — round+0x96C, the
            // five sub-structures, and the +0xB0 gate UpdateCombatRoundTimer's SECOND check
            // depends on are only ever established here.
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

            // Calls the engine's own CreateNewAttackActions entry point rather than
            // hand-populating the per-participant attack-descriptor array manually —
            // replicating engine internals piece by piece (StartCombatRound, the round-timer
            // setters, a bare AddAttackAction) got the round "armed" but never advanced its
            // real per-tick processing state (round+0x96C's selected sub-structure,
            // +0xB0/+0xAC — the array of fully computed swings UpdateCombatRoundTimer's
            // per-tick loop actually iterates, separate from the +0x9B0 "raw intent" list
            // AddAttackAction appends to). See docs/research/combat-participation.md §1
            // (CSWSCombatRound::AddAttackAction at 0x004D57A0, populated only via
            // 0x004D5A50). With bMayAcquireNewTarget=1 this runs the acquisition path (§1.3),
            // not the bare revalidation line (which only fires when round+0x9B0 is empty —
            // already false here): a GetNearestEnemy search biased toward our target, and on
            // success UpdateAttackTargetForAllAction(round, newId) (0x004D3E50) — the
            // strongest candidate for whatever arms the round's real per-tick state.

            // Reputation and perception gates GetNearestEnemy checks, read directly here for
            // diagnostic visibility ahead of the real call below. Reputation per
            // combat-participation.md §1.4: candidate.GetAIStateReputation(vsId) where vsId is
            // the "versus" object — here that's the target, i.e. "how does the target see the
            // candidate". Perception per §3.1: a visible-list node must exist AND its SEEN bit
            // (byte+4, bit 0) must be set.
            int repVsTarget = GetAIStateReputationFn(creature, targetId);
            nint visNode = GetVisibleListElementFn(creature, targetId);
            byte visFlags = visNode != 0 ? *(byte*)(visNode + 4) : (byte)0xFF;
            bool visSeen = visNode != 0 && (visFlags & 1) != 0;

            // combat-participation.md §1.4's scan direction is
            // candidate.GetAIStateReputation(vsId) where the SCANNED candidate is `this` — for
            // GetNearestEnemy's attempt 1 (candidate=player position anchor, vsId=attacker),
            // that's player.GetAIStateReputation(attackerId), the OPPOSITE thiscall direction
            // from repVsTarget above. §2.3 documents faction 0 (PLAYER) as going through an
            // argument-swapped matrix lookup, so these are not guaranteed equal — measure both
            // rather than assuming symmetry.
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

            // The acquisition-success path (0x005B6B58-0x005B6B76) re-resolves the found
            // candidate id via GetGameObject and casts it through vtbl+0x30 ("AsCreature" —
            // the SAME cast this function's own top half uses to validate a pre-existing
            // target, combat-participation.md line 104). A failed cast takes the same silent
            // "acquisition failed" bail as GetNearestEnemy finding nothing (0x5b48b0:
            // SetLockOrientationToObject(OBJECT_INVALID), pause animation, SetCombatMode
            // stand-down). playerObj above is already resolved via ResolveServerObject, which
            // calls the identical GetGameObjectFn(server, id) the engine's own disassembly
            // uses, so its own vtbl+0x30 result tells us directly whether the engine's
            // internal cast on the SAME object would succeed.
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

            // Does the area scan itself find the player? Attempt 1 per §1.3:
            // GetNearestEnemy(candidateCre=playerObj, range, excludeId=own, vsId=own) —
            // "nearest enemy of mine, searched from the candidate's position". Attempt 2
            // fallback: GetNearestEnemy(this=attacker, range, excludeId=own,
            // vsId=OBJECT_INVALID). A large fixed range stands in for MaxCleaveRange,
            // irrelevant at ~2m separation.
            uint scanAttempt1 = playerObj != 0
                ? GetNearestEnemyFn(playerObj, 50.0f, attackerRealId, attackerRealId)
                : ObjectInvalid;
            uint scanAttempt2 = GetNearestEnemyFn(creature, 50.0f, attackerRealId, ObjectInvalid);
            PluginHost.Log(
                $"queueattack ROUND19 diag: GetNearestEnemy attempt1(from playerObj)=0x{scanAttempt1:X} "
                + $"attempt2(from attacker)=0x{scanAttempt2:X} (0x7F000000=nothing found, "
                + $"expected a real object id if the scan reaches the player)");

            // If the scan never finds the player despite every individual gate passing, the
            // likely cause is spatial-index registration, not a targeting rule:
            // docs/research/realtime-creature-movement.md §1 (`SetPosition`) documents that
            // `this->area_id` (creature+0x8C) is resolved through the engine's own object
            // array, and only THEN does UpdatePositionInObjectsArray register the object into
            // that area's x-sorted spatial index (CSWSArea+0x190/+0x194) — the exact array
            // GetNearestEnemy walks. A stale/wrong area_id can leave an object registered in
            // the wrong area's index even with correct raw coordinates. Compare directly.
            if (playerObj != 0)
            {
                uint playerAreaId = *(uint*)(playerObj + 0x8C);
                uint attackerAreaId = *(uint*)(creature + 0x8C);
                PluginHost.Log(
                    $"queueattack ROUND19 diag: area_id (creature+0x8C) - player=0x{playerAreaId:X} "
                    + $"attacker(sithsoldier)=0x{attackerAreaId:X} MATCH={playerAreaId == attackerAreaId}");
            }

            // Confirms whether the attacker, and separately the player, are actually present
            // in their own area's x-sorted object array — the array GetNearestEnemy's
            // self-lookup step scans first, before any candidate is ever evaluated. See
            // FindSelfInAreaArray.
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

        // Moved OUTSIDE the one-shot round-init gate above: disassembly REFUTED the
        // spatial-index theory (the attacker and player are both present with valid indices
        // in the same area's x-sorted object array, CSWSArea+0x190/+0x194, that
        // GetNearestEnemy walks). The real cause is a RACE: our own per-frame SetPosition
        // calls constantly re-sort that array, so GetNearestEnemy's outward walk can miss the
        // player depending on exactly when it runs relative to the last re-sort. Retrying
        // CreateNewAttackActionsFn every pass is safe here (unlike a bare AddAttackActionFn
        // call, which has no dedup): its acquisition path re-runs GetNearestEnemy fresh each
        // time, giving the race another roll, and the IsFightAction(headType) gate at the top
        // of this function stops all future calls the instant one attempt lands a queued
        // swing. See docs/research/realtime-creature-movement.md §1.
        //
        // MaxCleaveRange (0x004FFAD0) is a real, weapon-derived value — its sub-call
        // (0x4FB0F0) reads a pointer at creature+0xA2C (an equipment slot) and branches on
        // data reached through it. A null/zero pointer here would make the range gate
        // degenerate for EVERY candidate, independent of any race.
        nint equipSlotPtr = *(nint*)(creature + 0xA2C);

        // Reads the real weapon-derived range inputs MaxCleaveRange computes from (rather
        // than a hardcoded 50.0f stand-in) via two clean thiscalls — see
        // GetEquippedItemFn/WeaponAccessorFn above for why this path was chosen over
        // replicating 0x004FFAD0's own confusing stack-argument reuse.
        nint weaponObj = equipSlotPtr != 0 ? GetEquippedItemFn(equipSlotPtr, 0x10) : 0;
        nint weaponAccessor = weaponObj != 0 ? WeaponAccessorFn(weaponObj) : 0;
        byte weaponFlagByte = weaponAccessor != 0 ? *(byte*)(weaponAccessor + 0x1A) : (byte)0xFF;
        float weaponRangeField = weaponAccessor != 0 ? *(float*)(weaponAccessor + 0x34) : float.NaN;

        // Compares MaxCleaveRangeFn(ownId,0,1) — the actual search-radius seed, called
        // exactly as the real code does (see MaxCleaveRangeFn above) — against the real
        // distance-minus-both-radii value the walk itself computes per candidate
        // (0x004F3061-0x004F3096 in GetNearestEnemy), replicated here in managed code from
        // the same fields (position at +0x90, personal-space radius at [+0x340]+0x08) rather
        // than hooking mid-function. Also reads round+0x9CC before the call: with
        // bUseAttemptedTarget=0 (our call passes arg2=0), CreateNewAttackActions reads its
        // "current target" from round+0x9CC — if that is NOT the invalid sentinel, the
        // function takes the "validate/renew existing target" branch
        // (combat-participation.md §1.2) instead of the "acquire a NEW target" branch (§1.3).
        // If a prior partial attempt left round+0x9CC non-invalid, every SUBSEQUENT retry
        // this round-init gate makes could be silently taking the validate path instead,
        // which may not include the same UpdateAttackTargetForAllAction arming step §1.3
        // documents.
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

        // The second gate UpdateCombatRoundTimer checks past +0x958/+0x960 — round+0x96C
        // selects one of five embedded 0x14C-byte sub-structures at round+4, and
        // [sub+0xB0] > 0 is what actually lets processing reach the call that would change
        // the head action. Read both directly to see whether StartCombatRoundFn established
        // this.
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

    // ---- STRATEGY 2: fire the creature's own OnCombatRoundEnd script (k_def_combend01)
    // through the engine's own event dispatcher, instead of replicating the engine's native
    // combat calls piece by piece. Full investigation and live results are in
    // docs/research/shared-state-audit.md. ------------------------------------------------
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

    /// <summary>Creature script slot at +0x260 — see the block comment above. VERIFIED LIVE:
    /// this is not an inline resref buffer but an indirect string handle,
    /// `{ char* data; int length; ... }` (CExoString-shaped) — reading the raw bytes as ASCII
    /// produces garbage since the first bytes are a little-endian pointer, not text.
    /// Dereferencing correctly reads `k_def_combend01` (KOTOR's default OnCombatRoundEnd
    /// script, whose body normally calls NWScript's `DetermineCombatRound()`) on a live
    /// untouched `sithsoldier`. See docs/research/shared-state-audit.md and
    /// <see cref="Odyssey.PeekCreatureScriptSlot0x260"/> / <see cref="Odyssey.FireCreatureScriptSlot0x260"/>
    /// (Odyssey.Combat.ScriptFire.cs) for the actual read/fire implementation.</summary>
    private const int CreScriptSlot0x260 = 0x260;

}

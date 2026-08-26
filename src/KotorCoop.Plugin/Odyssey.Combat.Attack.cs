// Odyssey.Combat.Attack.cs - part 2 of combat state: QueueAttackAction.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
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

}

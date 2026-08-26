// Odyssey.Combat.cs - part 1 of combat state: player combat state, marking a
// mirrored enemy, and PermitAttacking.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
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

}

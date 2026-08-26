// Odyssey.Combat.ScriptFire.cs - part 3 of combat state: firing a creature's own
// event script instead of a manual attack action (see the STRATEGY 2 note).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
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

}

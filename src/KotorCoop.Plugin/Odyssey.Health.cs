// Odyssey.Health.cs - hit points and death.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
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

    /// <summary>
    /// The STORED max hit points field only (CSWSObject+0xE0) — correct for doors, placeables
    /// and ordinary creatures, but ALWAYS ZERO for a player character (the engine never writes
    /// it for one; see <see cref="GetEffectiveMaxHitPoints"/>'s own remarks). Left exactly as
    /// it was: every existing caller of this method already deals only in non-PC creatures
    /// (mirrored enemies), for which this stored-field read is what the real engine's own
    /// <c>CSWSObject::GetMaxHitPoints</c> does too.
    /// </summary>
    public static ushort GetMaxHitPoints(nint obj) => *(ushort*)(obj + ObjMaxHitPoints);

    // CSWSCreatureStats offsets used by GetEffectiveMaxHitPoints's PC branch — the same ones
    // KotorGameBridge.Attach.cs's TryReadMaxHitPoints already uses out-of-process, VERIFIED
    // live (a level-1 PC with a rolled 10 and a +2 CON modifier read back 12, matching a
    // full-health reading exactly). CreStats itself (CSWSCreature+0xA74) is declared once in
    // Odyssey.Core.cs and already reused by Odyssey.Progression.cs's XP read.
    private const int StatsIsPc = 0x06C;
    private const int StatsLevelListData = 0x028;   // CExoArrayList<CSWLevelStats*>: T* data
    private const int StatsLevelListCount = 0x02C;  // ...then the count, then the capacity
    private const int StatsConModifier = 0x0EE;     // byte, added once PER LEVEL (0x004ED372)
    private const int LevelStatsHitPoints = 0x02B;  // byte, the hit points rolled at that level

    /// <summary>
    /// True for a creature whose stats block says is_pc — the player character itself, never a
    /// companion. The same test KotorGameBridge.Player.cs's <c>StillLooksLikeThePlayer</c>
    /// applies out of process, so a plugin-side "who is the player" agrees with the agent's.
    /// </summary>
    public static bool IsPlayerCharacter(nint creature)
    {
        // ⚠ LIVE 2026-09-02 (death-handling.md §6.4): the first frames after a mid-session
        // adopted load handed this a resolved object that was NOT a creature, and reading a
        // "stats pointer" 0xA74 bytes into it faulted (client RVA 0xAF71D, three frames in a
        // row, right after "the world is up"). CSWSObject ids resolve to doors, placeables
        // and items too, and nothing upstream promises a creature — so the vtable decides
        // before anything is dereferenced through the object, and the refusal is logged
        // (bounded) so the next such frame names what it saw instead of faulting silently.
        if (creature == 0)
        {
            return false;
        }

        if (*(nint*)creature != ServerCreatureVtable)
        {
            if (_notACreatureRefusals < 10)
            {
                _notACreatureRefusals++;
                PluginHost.Log($"is-pc: 0x{creature:X} (id 0x{*(uint*)(creature + 0x04):X}) is not a creature (vtable 0x{*(nint*)creature:X}) - not the player");
            }

            return false;
        }

        nint stats = Deref(creature + CreStats);
        return stats != 0 && *(int*)(stats + StatsIsPc) == 1;
    }

    /// <summary>CSWSCreature's vtable — the same value CreatureFinder sweeps for.</summary>
    private const nint ServerCreatureVtable = 0x007470C8;

    private static int _notACreatureRefusals;

    /// <summary>CSWSObject + 0x04 = the object's id.</summary>
    public static uint GetObjectId(nint obj) => *(uint*)(obj + 0x04);

    /// <summary>
    /// Max hit points, following the SAME branch the engine's own
    /// <c>CSWSCreature::GetMaxHitPoints</c> (0x004ED310) follows, unlike the plain stored-field
    /// read <see cref="GetMaxHitPoints"/> still is. See <see cref="MaxHitPointsRule"/>'s own
    /// class doc comment for why the stored field is wrong for a player character and what the
    /// correct branch computes instead.
    ///
    /// For anything that is not a PC-flagged creature this is identical to
    /// <see cref="GetMaxHitPoints"/> — the real engine's branch for them IS the plain field
    /// read, so there is nothing to change and every existing non-PC caller is unaffected.
    ///
    /// Returns 0 when nothing can be read (no stats block, no level list, a level pointer that
    /// does not resolve, or a count outside <see cref="MaxHitPointsRule.MaxCharacterLevels"/>'s
    /// defensive bound) — every caller already treats 0 as "unknown, cannot heal past this".
    /// MAIN THREAD ONLY.
    /// </summary>
    public static int GetEffectiveMaxHitPoints(nint obj)
    {
        nint stats = Deref(obj + CreStats);
        bool isPc = stats != 0 && *(int*)(stats + StatsIsPc) == 1;

        if (!isPc)
        {
            return MaxHitPointsRule.ForNonPc(*(ushort*)(obj + ObjMaxHitPoints));
        }

        nint levels = Deref(stats + StatsLevelListData);
        int levelCount = *(int*)(stats + StatsLevelListCount);
        if (levels == 0 || levelCount <= 0 || levelCount > MaxHitPointsRule.MaxCharacterLevels)
        {
            return 0;
        }

        Span<int> rolled = stackalloc int[levelCount];
        for (int i = 0; i < levelCount; i++)
        {
            nint level = Deref(levels + (i * 4));
            if (level == 0)
            {
                return 0;
            }

            rolled[i] = *(byte*)(level + LevelStatsHitPoints);
        }

        int conModifier = *(byte*)(stats + StatsConModifier);
        return MaxHitPointsRule.ForPc(conModifier, rolled);
    }

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

}

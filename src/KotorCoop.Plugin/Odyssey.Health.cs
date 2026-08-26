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

}

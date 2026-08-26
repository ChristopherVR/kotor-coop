// Odyssey.Downed.cs - the downed (temp-dead) state and revival.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
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

}

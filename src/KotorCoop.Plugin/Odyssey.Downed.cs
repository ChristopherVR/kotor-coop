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
    /// ⚠ <c>GetDeadTempFn</c> alone is not reliable for this creature. GetDeadTemp
    /// (0x004EF890) loops the ENGINE's own idea of "the client party" (CSWParty, not
    /// CSWPartyTable) to decide whether the creature it was called on is a member at all,
    /// and CSWParty can hold a stale count/membership view relative to the table — including
    /// inside the ENGINE's OWN internal copy of this same check
    /// (CSWSEffectListHandler::OnApplyDamage's already-dead early-out calls the identical
    /// function), not just here. When the local player's own creature is not found as a party
    /// member by that stale view, GetDeadTemp silently returns false and further damage is
    /// not blocked — live-reproduced as a silent process termination with no crash-reporting
    /// chain ever running. Full reproduction: docs/research/shared-state-audit.md (round 57).
    /// See also docs/research/death-handling.md.
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

    /// <summary>CClientExoApp::RunDeathSequence (0x005EDC40, forwards to the internal
    /// 0x005F7200) — the game-over: game_over_state + fade pending, HideAllPanels, the
    /// "Your entire party has been killed" box, camera on the last downed member. Zero
    /// arguments; <c>this</c> is the CClientExoApp the engine's own single call site reaches
    /// through <c>mov edx,[0x7A39FC]; mov ecx,[edx+4]</c> (0x004B6E90).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, void> RunDeathSequenceFn =
        (delegate* unmanaged[Thiscall]<nint, void>)0x005EDC40;

    /// <summary>
    /// Runs the engine's own game-over flow on this instance — the exact call
    /// <c>TempDeathUpdate</c>'s all-dead scan makes, for a client whose scan is dark
    /// (<c>client_temp_death_suppress</c>) once the session has ruled the wipe real. Idempotent
    /// in the engine (it writes the same two state words <c>SetGameOver</c> does); the caller
    /// still latches it so the log carries one line per wipe. Returns false only when there is
    /// no client app to call it on. MAIN THREAD ONLY.
    /// </summary>
    public static bool RunDeathSequence()
    {
        nint client = GetClient();
        if (client == 0)
        {
            return false;
        }

        RunDeathSequenceFn(client);
        return true;
    }

    /// <summary>Makes a creature raiseable, which the Resurrection handler checks before it
    /// does anything. The engine's own auto-revive writes this same flag raw.</summary>
    public static void SetRaiseable(nint creature) => *(int*)(creature + CreatureIsRaiseable) = 1;

    /// <summary>
    /// Snaps a creature to a position through SetPosition (raises the dirty bit so the
    /// render side follows). Correction for a phantom that has fallen hopelessly behind
    /// its player — a peer teleport, a load, or a pathfinder dead end.
    ///
    /// Walkmesh-guarded (docs/research/walkmesh-guard.md): a raw SetPosition write does NOT
    /// itself call TestDirectLine (VERIFIED by disassembly — this is a bare thiscall to
    /// 0x004CD1A0, no pathing), so this cannot crash the way <c>walkto</c> did. But a body
    /// left standing off-mesh is exactly the shape of the OTHER live fault this write-up
    /// covers (a joiner's phantom crashed on danm14aa shortly after being snapped to a
    /// (0,0,0) read during the host's own module load) — some LATER engine tick's own
    /// pathing/room check is what actually calls TestDirectLine against the corrupted
    /// position. Refusing here holds the creature at its last good position instead ("hold
    /// the stand-in where it is") and returns false so a caller that wants to report a
    /// refusal (<c>tp</c>) can. MAIN THREAD ONLY.
    /// </summary>
    public static bool SnapTo(nint creature, Vec3 position)
    {
        if (!TryValidateMoveDestination(creature, position, out MoveRejectReason reason))
        {
            LogMoveRefusalOnce(creature, 0, position, reason);
            return false;
        }

        ClearAllActionsFn(creature, 1);
        SetPositionFn(creature, &position, 1, 0, 0);
        return true;
    }

    /// <summary>
    /// Clears a creature's action queue and reasserts EXACTLY the position the engine already
    /// has it at — the "nuclear option" <see cref="Odyssey.ClearLocalPlayerStuckDialogueAction"/>
    /// uses to break a stuck <c>AIActionDialogObject</c> loop no UI-level close could touch.
    ///
    /// Deliberately bypasses <see cref="SnapTo"/>'s walkmesh guard: this never asks the engine
    /// to go anywhere new, only to re-commit where it already is (by definition already "the
    /// truth", walkable or not, since the engine itself put the creature there), and the one
    /// caller exists specifically because the more careful approaches already failed — refusing
    /// this one too on a borderline PositionWalkable read would remove the only recovery this
    /// stuck-dialogue case has. See docs/research/walkmesh-guard.md §2 for why this needed to
    /// be its own function rather than SnapTo(creature, GetPosition(creature)).
    /// </summary>
    public static void ClearActionsAndHoldPosition(nint creature)
    {
        Vec3 position = GetPosition(creature);
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

namespace KotorCoop.Plugin;

/// <summary>
/// Applies the non-damage peer-effect channel's kinds to the LOCAL player, through the
/// engine's own VM effect constructors — the same pattern <c>Odyssey.ApplyDamageEffectCore</c>
/// already uses for damage (push the VM stack args, call the routine's own table entry, apply
/// through <c>ApplyEffectToObject</c>), just for a different set of effect constructors. See
/// docs/research/effect-sync.md for what each of these routines was verified to do and why.
///
/// A separate static class rather than a second partial of <c>Odyssey</c>, on purpose:
/// <c>Odyssey.cs</c> and <c>EnemyPresenceController.cs</c> belong to other in-flight work this
/// pass must not touch, and <c>Odyssey</c> is not declared <c>partial</c> — so the small set of
/// VM-call primitives (<see cref="GetImplementer"/>, <see cref="FindRoutine"/>, the stack-push
/// delegates) are duplicated here rather than exposed from there. Same addresses, same
/// verified behaviour, one more copy — consistent with how <c>PhantomEffectHook</c> duplicates
/// <c>PeerEffectRules</c>' allowlist rather than referencing it (NativeAOT plugin code cannot
/// reference either <c>Odyssey</c>'s private members or <c>KotorCoop.Protocol</c>).
///
/// ⚠ KNOCKDOWN, PARALYZE AND SLEEP HAVE NO CONSTRUCTOR HERE. Knockdown is only reachable
/// through <c>ExecuteCommandEffectComplex</c>, whose full declared-parameter list was not
/// recovered (effect-sync.md §4) — guessing it risks desyncing the VM's own argument stack for
/// whatever script runs next, which nothing can catch after the fact, unlike a wrong ENGINE
/// OFFSET (checked up front, refused cleanly). Paralyze and Sleep are two of the three
/// <c>OnApplySetState</c> sub-codes this pass could not match to an English name at all
/// (effect-sync.md §3.2/§3.3). All three are captured and forwarded by
/// <c>PhantomEffectHook</c>/<c>PeerEffectController</c> regardless — this file just has nothing
/// to do with them yet, and says so with a distinct failure code rather than silently no-op'ing
/// on an unmapped kind.
///
/// MAIN THREAD ONLY, same as every other VM-dispatching call in this codebase.
/// </summary>
internal static unsafe class OdysseyEffects
{
    // ---- VM-call primitives — duplicated from Odyssey.cs; same VERIFIED addresses ------

    private const nint VirtualMachinePtr = 0x007A3A00;

    // CVirtualMachineInternal
    private const int CommandImplementerOffset = 0x3D4;
    private const int StackPointerOffset = 0x1AC;
    private const int CallStackSizeOffset = 0x1C4;

    // CSWVirtualMachineCommands
    private const int CommandsTableOffset = 0x0C;
    private const int RoutineTableEntries = 772;

    /// <summary>ApplyEffectToObject(int durationType, effect e, object target, float dur).</summary>
    private const nint ExpectedApplyEffectToObject = 0x0052E570;

    // The zero/one-parameter VM effect constructors this pass ships — see effect-sync.md §3/§4
    // for why each one's parameter count is safe to assume.
    private const nint ExpectedEffectDroidStun = 0x00534810;             // 0 params — "Stun"
    private const nint ExpectedEffectChoke = 0x005351E0;                 // 0 params
    private const nint ExpectedEffectWhirlWind = 0x00534C20;             // 0 params
    private const nint ExpectedEffectHorrified = 0x00534E90;             // 0 params — "Frightened"
    private const nint ExpectedEffectForcePushed = 0x005349F0;           // 0 params
    private const nint ExpectedEffectMovementSpeedDecrease = 0x005332E0; // 1 param (int percent) — "Slow"

    private static int _routineApplyEffect = -1;
    private static int _routineDroidStun = -1;
    private static int _routineChoke = -1;
    private static int _routineWhirlWind = -1;
    private static int _routineHorrified = -1;
    private static int _routineForcePushed = -1;
    private static int _routineMovementSpeedDecrease = -1;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int> StackPushInteger =
        (delegate* unmanaged[Thiscall]<nint, int, int>)0x005D1010;

    private static readonly delegate* unmanaged[Thiscall]<nint, float, int> StackPushFloat =
        (delegate* unmanaged[Thiscall]<nint, float, int>)0x005D1030;

    private static readonly delegate* unmanaged[Thiscall]<nint, uint, int> StackPushObject =
        (delegate* unmanaged[Thiscall]<nint, uint, int>)0x005D10D0;

    private static nint Deref(nint p) => *(nint*)p;

    /// <summary>Resolves the command implementer, or 0 if the VM is not ready. Identical to
    /// <c>Odyssey.GetImplementer</c> — duplicated, not shared, per the class comment above.</summary>
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

    // DURATION_TYPE_* — the standard Aurora/Odyssey convention (INSTANT 0, TEMPORARY 1),
    // INFERRED rather than independently disassembled: it is a value the CALLER supplies, not
    // one this engine's own code decides, so it is far less likely to have moved than the
    // effect-type table effect-sync.md documents moving. See effect-sync.md §2.4.
    private const int DurationTypeInstant = 0;
    private const int DurationTypeTemporary = 1;

    /// <summary>Slow's percent clamp. 0 would be a no-op the engine might reject outright;
    /// 100+ has no clear meaning for a "decrease". Neither end is a measured figure — a safe
    /// bound around what a percent decrease should be, pending a live check.</summary>
    private const int MinSlowPercent = 1;
    private const int MaxSlowPercent = 90;
    private const int DefaultSlowPercent = 50;

    /// <summary>
    /// Applies one peer-effect kind to <paramref name="objectId"/> (expected: the local
    /// player) through the engine's own VM pipeline. Returns false with a nonzero
    /// <paramref name="failure"/> on any refusal — including <c>-1</c>, "no apply-side
    /// constructor for this kind" (Knockdown, Paralyze, Sleep — see the class comment).
    /// </summary>
    public static bool ApplyEffect(uint objectId, uint kind, uint durationMs, uint magnitude, out int failure)
    {
        switch (kind)
        {
            case PeerEffectKindValues.Stun:
                return ApplyZeroParamEffect(
                    objectId, ExpectedEffectDroidStun, ref _routineDroidStun, durationMs, instant: false, out failure);

            case PeerEffectKindValues.Choke:
                return ApplyZeroParamEffect(
                    objectId, ExpectedEffectChoke, ref _routineChoke, durationMs, instant: false, out failure);

            case PeerEffectKindValues.Whirlwind:
                return ApplyZeroParamEffect(
                    objectId, ExpectedEffectWhirlWind, ref _routineWhirlWind, durationMs, instant: false, out failure);

            case PeerEffectKindValues.Frightened:
                return ApplyZeroParamEffect(
                    objectId, ExpectedEffectHorrified, ref _routineHorrified, durationMs, instant: false, out failure);

            case PeerEffectKindValues.ForcePush:
                // A push is a one-shot displacement, not an ongoing state — instant, like
                // Odyssey's own EffectDamage application.
                return ApplyZeroParamEffect(
                    objectId, ExpectedEffectForcePushed, ref _routineForcePushed, 0, instant: true, out failure);

            case PeerEffectKindValues.Slow:
                return ApplySlowEffect(objectId, durationMs, magnitude, out failure);

            default:
                // Knockdown, Paralyze, Sleep — captured and forwarded, no constructor yet.
                failure = -1;
                return false;
        }
    }

    /// <summary>
    /// Shared shape for every zero-declared-parameter effect constructor this pass ships
    /// (Choke, WhirlWind, Horrified, DroidStun, ForcePushed — all VERIFIED to read no VM
    /// script argument at all, effect-sync.md §3). Mirrors
    /// <c>Odyssey.ApplyDamageEffectCore</c>'s guard order exactly: resolve the implementer,
    /// verify the routine table still says what this expects, refuse while a script owns the
    /// VM stack, push <c>ApplyEffectToObject</c>'s trailing args, construct the effect with
    /// zero pushed arguments (<c>nParams = 0</c>), then push the leading arg and apply.
    /// </summary>
    private static bool ApplyZeroParamEffect(
        uint objectId, nint expectedHandler, ref int cachedRoutine, uint durationMs, bool instant, out int failure)
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

        if (cachedRoutine < 0)
        {
            cachedRoutine = FindRoutine(table, expectedHandler);
        }

        if (_routineApplyEffect < 0)
        {
            _routineApplyEffect = FindRoutine(table, ExpectedApplyEffectToObject);
        }

        if (cachedRoutine < 0 || _routineApplyEffect < 0)
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
        // pushed in reverse declaration order; the effect constructor is dispatched in the
        // middle so it leaves its result exactly where the effect argument belongs — the same
        // choreography Odyssey.ApplyDamageEffectCore uses for EffectDamage.
        float durationSeconds = instant ? 0f : durationMs / 1000f;
        if (StackPushFloat(vm, durationSeconds) == 0 || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        var effectCtor = (delegate* unmanaged[Thiscall]<nint, int, int, int>)expectedHandler;
        if (effectCtor(implementer, cachedRoutine, 0) != 0)
        {
            failure = 6;
            return false;
        }

        if (StackPushInteger(vm, instant ? DurationTypeInstant : DurationTypeTemporary) == 0)
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

    /// <summary>
    /// Slow — the one allowlist member with a single declared VM parameter
    /// (<c>int nPercentChange</c>, VERIFIED effect-sync.md §3). Same shape as
    /// <see cref="ApplyZeroParamEffect"/>, with one extra push and <c>nParams = 1</c>.
    /// </summary>
    private static bool ApplySlowEffect(uint objectId, uint durationMs, uint magnitude, out int failure)
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

        if (_routineMovementSpeedDecrease < 0)
        {
            _routineMovementSpeedDecrease = FindRoutine(table, ExpectedEffectMovementSpeedDecrease);
        }

        if (_routineApplyEffect < 0)
        {
            _routineApplyEffect = FindRoutine(table, ExpectedApplyEffectToObject);
        }

        if (_routineMovementSpeedDecrease < 0 || _routineApplyEffect < 0)
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

        float durationSeconds = durationMs / 1000f;
        if (StackPushFloat(vm, durationSeconds) == 0 || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        int percent = (int)magnitude;
        if (percent < MinSlowPercent || percent > MaxSlowPercent)
        {
            percent = DefaultSlowPercent;
        }

        if (StackPushInteger(vm, percent) == 0)
        {
            failure = 6;
            return false;
        }

        var effectCtor = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedEffectMovementSpeedDecrease;
        if (effectCtor(implementer, _routineMovementSpeedDecrease, 1) != 0)
        {
            failure = 7;
            return false;
        }

        if (StackPushInteger(vm, DurationTypeTemporary) == 0)
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

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
/// Knockdown, Paralyze, Sleep and (effect-sync.md §10, 2026-09-02) Confused are named and
/// constructible through the SAME native handler, <c>ExecuteCommandEffectComplex</c>
/// (<c>0x005313F0</c>), which is registered at over twenty routine numbers in the VM's command
/// table (a compiler CSE reusing one preloaded register across many <c>mov [table+off], eax</c>
/// stores — invisible to a raw address-literal scan). Their routine numbers —
/// <c>EffectKnockdown</c>=134, <c>EffectParalyze</c>=148, <c>EffectSleep</c>=154,
/// <c>EffectConfused</c>=157 — are VERIFIED against K1 <c>nwscript.nss</c>'s declaration order;
/// stack arithmetic on the handler's own prologue confirms it reads the CALLING routine number
/// (not a popped script argument) as its dispatch key, so each is safe to invoke with zero
/// pushed script arguments, exactly like the zero-param constructors below. See
/// <see cref="ApplyComplexEffect"/> for why <c>FindRoutine</c>'s first-match linear scan cannot
/// be reused here — it would resolve to routine 51 (<c>EffectAssuredHit</c>) for all four,
/// which is wrong. Full verification: docs/research/effect-sync.md §7, §10.
///
/// Heal and TemporaryHitpoints (also §10) are a DIFFERENT shape — each has its own dedicated,
/// unaliased one-int-parameter constructor (<see cref="ApplyOneIntParamEffect"/>), the same
/// family <see cref="ApplySlowEffect"/> already proves live.
///
/// effect-sync.md §12 (2026-09-18) adds four more: Fear (<c>EffectFrightened</c>=158) and the
/// general Stunned (<c>EffectStunned</c>=161) through <see cref="ApplyComplexEffect"/>
/// unchanged; Poison (<c>EffectPoison</c>=250, a dedicated one-int constructor) through
/// <see cref="ApplyOneIntParamEffect"/>; and Disguise (<c>EffectDisguise</c>=463 — a complex-
/// family case that, unlike the others, pops one int) through the new
/// <see cref="ApplyComplexEffectOneInt"/>. The first two reuse a live-verified path outright;
/// Poison and Disguise are shape-matched to live-verified paths but their apply halves have
/// never run on a game, so <c>PeerEffectController</c> gates them behind <c>poison_sync</c> /
/// <c>disguise_sync</c> (default OFF) until a live pass does.
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

    /// <summary>The shared "complex effect" handler — one C++ function registered at over
    /// twenty routine numbers in the VM's command table (docs/research/effect-sync.md §7).
    /// Dispatches on the CALLING routine number (read from its own arg1, VERIFIED via stack
    /// arithmetic on its prologue), not a popped script argument.</summary>
    private const nint ExpectedEffectComplex = 0x005313F0;

    /// <summary>Routine numbers for the three complex-family effects this pass adds, VERIFIED
    /// three independent ways: (1) a direct disassembly of
    /// <c>InitializeCommands</c>' register-reuse stores at these table offsets, (2) the
    /// existing, already-verified <c>ExecuteCommandEffectComplex</c> compaction/jump-table
    /// decode (effect-sync.md §2.2) landing on the exact same (type, sub-code) pairs, and
    /// (3) K1 <c>nwscript.nss</c> declaration-order counting, calibrated against 5 independent
    /// anchors. See effect-sync.md §7 for the full three-way cross-check.</summary>
    private const int RoutineEffectKnockdown = 134; // -> effect type 20, no sub-code
    private const int RoutineEffectParalyze = 148;  // -> effect type 8, sub-code 5
    private const int RoutineEffectSleep = 154;     // -> effect type 8, sub-code 6

    /// <summary>Routine number for the fourth complex-family effect this pass adds
    /// (effect-sync.md §10, 2026-09-02), named alongside Knockdown/Paralyze/Sleep in the same
    /// three-way cross-check (§7.3) but not shipped that pass.</summary>
    private const int RoutineEffectConfused = 157; // -> effect type 8, sub-code 1

    /// <summary>The two remaining zero-argument complex-family cases effect-sync.md §7.3 named
    /// but did not ship, shipped §12 (2026-09-18): the REAL <c>EffectFrightened</c> (not the
    /// <c>EffectHorrified</c> this codebase calls Frightened) and the general, non-droid
    /// <c>EffectStunned</c>. Case bodies at <c>0x531546</c>/<c>0x531559</c> pop nothing — same
    /// shape as Paralyze/Sleep/Confused, same <see cref="ApplyComplexEffect"/> path.</summary>
    private const int RoutineEffectFear = 158;    // -> effect type 8, sub-code 2
    private const int RoutineEffectStunned = 161; // -> effect type 8, sub-code 4

    /// <summary>
    /// <c>EffectDisguise(int nDisguiseAppearance)</c> — ALSO an <c>ExecuteCommandEffectComplex</c>
    /// case (reached through the handler's second compaction table for routines 270..463,
    /// effect-sync.md §12.2), but unlike every other complex-family member this codebase calls
    /// it DOES pop one script argument: its case body at <c>0x531689</c> calls the guarded VM
    /// stack-pop helper (<c>0x5D1000</c>), refuses with the VM's own error code if nothing is
    /// there, writes effect type 62 (global <c>0x746774</c>, read: 62) and
    /// <c>SetInteger(effect, 0, value)</c>. So it needs <see cref="ApplyComplexEffectOneInt"/>:
    /// the specific-index table check of <see cref="ApplyComplexEffect"/> plus the one push and
    /// <c>nParams = 1</c> of <see cref="ApplySlowEffect"/>.
    /// </summary>
    private const int RoutineEffectDisguise = 463; // -> effect type 62, integer_list[0] = appearance row

    /// <summary>
    /// <c>ExecuteCommandEffectPoison</c> — a dedicated, unaliased constructor (one address-literal
    /// registration, at table offset <c>0x3E8</c> = routine 250, effect-sync.md §12.3) with the
    /// exact <c>ExecuteCommandEffectHeal</c> shape: an up-front pop through the guarded helper
    /// (refuses with <c>0xFFFFF82F</c> on an empty/mistyped stack), then effect type 35 (global
    /// <c>0x746708</c>, read: 35) and <c>SetInteger(effect, 0, nPoisonType)</c>. Same
    /// <see cref="ApplyOneIntParamEffect"/> path as Heal/TemporaryHitpoints.
    /// </summary>
    private const nint ExpectedEffectPoison = 0x00533560; // 1 param (int nPoisonType) — type 35

    /// <summary>The two heal-family one-int-parameter VM effect constructors added in
    /// effect-sync.md §10 (2026-09-02) — VERIFIED via fresh disassembly: each pops exactly one
    /// int off the VM stack through the same shared stack-pop helper Slow's own constructor
    /// uses (<c>ExecuteCommandEffectMovementSpeedDecrease</c>'s <c>[esp+0x24]</c> read), feeds
    /// it straight to <c>SetInteger(effect, 0, value)</c>, and writes the SHARED null/no-op
    /// effect type (global <c>0x74667C</c> = 0) instead of its own real type if the popped
    /// value is not positive — so a non-positive magnitude self-neuters into a harmless no-op,
    /// not a corrupted call. Only ONE registration each was found in the VM's command table
    /// (<c>tools/xrefs.py</c>), unlike <see cref="ExpectedEffectComplex"/>'s 20+ aliases, so
    /// <see cref="FindRoutine"/>'s ordinary linear scan is safe for both.</summary>
    private const nint ExpectedEffectHeal = 0x00532A70;                 // 1 param (int nDamageToHeal) — type 39
    private const nint ExpectedEffectTemporaryHitpoints = 0x00534490;   // 1 param (int nHitPoints) — type 15

    private static int _routineApplyEffect = -1;
    private static int _routineDroidStun = -1;
    private static int _routineChoke = -1;
    private static int _routineWhirlWind = -1;
    private static int _routineHorrified = -1;
    private static int _routineForcePushed = -1;
    private static int _routineMovementSpeedDecrease = -1;
    private static int _routineKnockdown = -1;
    private static int _routineParalyze = -1;
    private static int _routineSleep = -1;
    private static int _routineConfused = -1;
    private static int _routineHeal = -1;
    private static int _routineTemporaryHitpoints = -1;
    private static int _routineFear = -1;
    private static int _routineStunned = -1;
    private static int _routinePoison = -1;
    private static int _routineDisguise = -1;

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

    /// <summary>Heal/TemporaryHitpoints magnitude clamp. Unlike Slow's percent, a heal/temp-HP
    /// AMOUNT has no natural engine-side upper bound (nothing analogous to "can't slow past
    /// 100%") — this is a sanity bound against a corrupted or malicious wire value, not a
    /// measured game figure, and the default is a plausible medpac-ish amount, not a measured
    /// one either.</summary>
    private const int MinHealMagnitude = 1;
    private const int MaxHealMagnitude = 999;
    private const int DefaultHealMagnitude = 20;

    private const int MinTemporaryHitpointsMagnitude = 1;
    private const int MaxTemporaryHitpointsMagnitude = 999;
    private const int DefaultTemporaryHitpointsMagnitude = 20;

    /// <summary>Poison's magnitude is a poison.2da ROW, not an amount, so there is no sensible
    /// substitute for a bad one — a value above this bound is REFUSED (failure 11) before
    /// anything touches the VM stack, never defaulted. The bound is a sanity cap on a wire
    /// value (K1's poison.2da is a few dozen rows), not a measured table size; row 0 is a real
    /// row and stays accepted.</summary>
    private const int MaxPoisonType = 255;

    /// <summary>What a magnitude has to be for the kind to be applied at all — checked in
    /// <see cref="ApplyEffect"/> BEFORE the shared helpers push anything, because every helper
    /// pushes the duration and target first and a refusal after that would leave two values
    /// on the VM stack.</summary>
    private static bool IsUsablePoisonType(uint magnitude) => magnitude <= MaxPoisonType;

    /// <summary>Mirrors <c>PeerEffectRules.IsUsableDisguiseRow</c> / <c>PeerIdentityRule
    /// .IsUsableAppearance</c>: a non-zero row that fits the u16 the engine stores it in.</summary>
    private static bool IsUsableDisguiseRow(uint magnitude) => magnitude is not 0 && magnitude <= 0xFFFF;

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

            case PeerEffectKindValues.Knockdown:
                return ApplyComplexEffect(
                    objectId, RoutineEffectKnockdown, ref _routineKnockdown, durationMs, out failure);

            case PeerEffectKindValues.Paralyze:
                return ApplyComplexEffect(
                    objectId, RoutineEffectParalyze, ref _routineParalyze, durationMs, out failure);

            case PeerEffectKindValues.Sleep:
                return ApplyComplexEffect(
                    objectId, RoutineEffectSleep, ref _routineSleep, durationMs, out failure);

            case PeerEffectKindValues.Confused:
                return ApplyComplexEffect(
                    objectId, RoutineEffectConfused, ref _routineConfused, durationMs, out failure);

            case PeerEffectKindValues.Heal:
                // Instant, like ForcePush: healing lands once, it does not persist as a state
                // — the literal 0 discards whatever durationMs the packet carried, same as
                // ForcePush's own case above.
                return ApplyOneIntParamEffect(
                    objectId, ExpectedEffectHeal, ref _routineHeal, 0, magnitude,
                    MinHealMagnitude, MaxHealMagnitude, DefaultHealMagnitude, instant: true, out failure);

            case PeerEffectKindValues.TemporaryHitpoints:
                return ApplyOneIntParamEffect(
                    objectId, ExpectedEffectTemporaryHitpoints, ref _routineTemporaryHitpoints, durationMs,
                    magnitude, MinTemporaryHitpointsMagnitude, MaxTemporaryHitpointsMagnitude,
                    DefaultTemporaryHitpointsMagnitude, instant: false, out failure);

            case PeerEffectKindValues.Fear:
                return ApplyComplexEffect(
                    objectId, RoutineEffectFear, ref _routineFear, durationMs, out failure);

            case PeerEffectKindValues.Stunned:
                return ApplyComplexEffect(
                    objectId, RoutineEffectStunned, ref _routineStunned, durationMs, out failure);

            case PeerEffectKindValues.Poison:
                // A poison.2da row, refused rather than defaulted when out of range — and
                // refused HERE, before the helper pushes the duration and target.
                if (!IsUsablePoisonType(magnitude))
                {
                    failure = 11;
                    return false;
                }

                return ApplyOneIntParamEffect(
                    objectId, ExpectedEffectPoison, ref _routinePoison, durationMs, magnitude,
                    0, MaxPoisonType, 0, instant: false, out failure);

            case PeerEffectKindValues.Disguise:
                if (!IsUsableDisguiseRow(magnitude))
                {
                    failure = 11;
                    return false;
                }

                return ApplyComplexEffectOneInt(
                    objectId, RoutineEffectDisguise, ref _routineDisguise, durationMs, (int)magnitude, out failure);

            default:
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

    /// <summary>
    /// Heal, TemporaryHitpoints — the two one-int-parameter constructors added in
    /// effect-sync.md §10 (2026-09-02). Same shape as <see cref="ApplySlowEffect"/> (a single
    /// declared <c>int</c>, VERIFIED via fresh disassembly of
    /// <c>ExecuteCommandEffectHeal</c>/<c>ExecuteCommandEffectTemporaryHitpoints</c>), factored
    /// out separately rather than folded into <see cref="ApplySlowEffect"/> itself so that
    /// method — already live-tested — is not touched by this pass. <paramref name="instant"/>
    /// picks Heal's one-shot application (like <see cref="ApplyZeroParamEffect"/>'s ForcePush
    /// case) versus TemporaryHitpoints' durable one (like Slow).
    /// </summary>
    private static bool ApplyOneIntParamEffect(
        uint objectId, nint expectedHandler, ref int cachedRoutine, uint durationMs, uint magnitude,
        int minMagnitude, int maxMagnitude, int defaultMagnitude, bool instant, out int failure)
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

        float durationSeconds = instant ? 0f : durationMs / 1000f;
        if (StackPushFloat(vm, durationSeconds) == 0 || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        int value = (int)magnitude;
        if (value < minMagnitude || value > maxMagnitude)
        {
            value = defaultMagnitude;
        }

        if (StackPushInteger(vm, value) == 0)
        {
            failure = 6;
            return false;
        }

        var effectCtor = (delegate* unmanaged[Thiscall]<nint, int, int, int>)expectedHandler;
        if (effectCtor(implementer, cachedRoutine, 1) != 0)
        {
            failure = 7;
            return false;
        }

        if (StackPushInteger(vm, instant ? DurationTypeInstant : DurationTypeTemporary) == 0)
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

    /// <summary>
    /// Knockdown, Paralyze, Sleep — the three complex-family effects (effect-sync.md §7). All
    /// three share ONE native handler, <see cref="ExpectedEffectComplex"/>, registered at over
    /// twenty different routine numbers in the VM's command table (a compiler CSE — one
    /// preloaded register, many <c>mov [table+off], eax</c> stores). That means
    /// <see cref="FindRoutine"/>'s first-match linear scan is the WRONG tool here: it would
    /// resolve to routine 51 (<c>EffectAssuredHit</c>, the lowest-numbered alias) for all three,
    /// silently building the wrong effect. Instead this checks the table entry at the SPECIFIC,
    /// VERIFIED routine index directly — table[routineIndex] must equal
    /// <see cref="ExpectedEffectComplex"/>, or this refuses (logged no-op), same "wrong offset
    /// is a refusal, not a guess" rule <see cref="ApplyZeroParamEffect"/> follows. The handler's
    /// own dispatch reads the CALLING routine number (not a popped script argument, verified via
    /// stack arithmetic on its prologue) to pick the effect, so — like the true zero-param
    /// constructors above — this pushes no script arguments and calls with <c>nParams = 0</c>.
    /// </summary>
    private static bool ApplyComplexEffect(
        uint objectId, int routineIndex, ref int cachedRoutine, uint durationMs, out int failure)
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
            cachedRoutine = Deref(table + (routineIndex * 4)) == ExpectedEffectComplex
                ? routineIndex
                : -1;
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

        float durationSeconds = durationMs / 1000f;
        if (StackPushFloat(vm, durationSeconds) == 0 || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        var effectCtor = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedEffectComplex;
        if (effectCtor(implementer, cachedRoutine, 0) != 0)
        {
            failure = 6;
            return false;
        }

        if (StackPushInteger(vm, DurationTypeTemporary) == 0)
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
    /// Disguise — the one complex-family case this codebase calls that POPS a script argument
    /// (<see cref="RoutineEffectDisguise"/>). <see cref="ApplyComplexEffect"/>'s specific-index
    /// table check (the handler is aliased twenty-odd times; a linear scan would find
    /// <c>EffectAssuredHit</c>) joined to <see cref="ApplySlowEffect"/>'s one push and
    /// <c>nParams = 1</c>. A separate method rather than a parameter on
    /// <see cref="ApplyComplexEffect"/> so that already-live-tested method is byte-for-byte
    /// untouched. The caller validates <paramref name="value"/> before this pushes anything.
    /// </summary>
    private static bool ApplyComplexEffectOneInt(
        uint objectId, int routineIndex, ref int cachedRoutine, uint durationMs, int value, out int failure)
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
            cachedRoutine = Deref(table + (routineIndex * 4)) == ExpectedEffectComplex
                ? routineIndex
                : -1;
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

        float durationSeconds = durationMs / 1000f;
        if (StackPushFloat(vm, durationSeconds) == 0 || StackPushObject(vm, objectId) == 0)
        {
            failure = 5;
            return false;
        }

        if (StackPushInteger(vm, value) == 0)
        {
            failure = 6;
            return false;
        }

        var effectCtor = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedEffectComplex;
        if (effectCtor(implementer, cachedRoutine, 1) != 0)
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

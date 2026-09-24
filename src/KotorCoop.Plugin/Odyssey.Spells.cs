// Odyssey.Spells.cs - the `cast`/`grantpower` live-test primitives (docs/research/
// effect-sync.md §14): make the LOCAL PLAYER cast a spells.2da row through the engine's own
// action queue, and grant a power to a save that doesn't have it yet, so partner_buffs /
// enemy_effect_sync's capture hooks - which require the effect's creator to be the local
// player - can finally be exercised by a REAL cast instead of `applyeffect`'s synthetic VM
// apply (creator 0x7F000000, OBJECT_SELF invalid).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- ActionCastSpellAtObject, VERIFIED(static) -----------------------------------
    //
    // Found the same way AddMultiClassHook found ExecuteCommandAddMultiClass: the routine
    // table's own population code, read as data. tools/xrefs.py 0x0052EE50 finds exactly one
    // address-literal occurrence, at 0x0054C9C9; the six bytes immediately before it
    // (0x0054C9BF) are `mov edx, 0x52EE50` followed by `mov [eax+0x7D8], edx` and, a few
    // instructions later, `mov [eax+0x7D4], edx` again - i.e. the SAME handler is registered
    // at table offsets 0x7D8 and 0x7D4 (routines 502 and 501: 0x7D8/4=502, 0x7D4/4=501).
    //
    // Full disassembly of the handler (0x0052EE50, tools/disasm.py 0x52EE50 2200) traces
    // every StackPop* call in the function against nCommandId (ebx). For ebx==0x1F5 (501)
    // exactly three values are popped, in this order: nSpell (StackPopInteger, 0x0052EE85),
    // oTarget (StackPopObject, 0x0052EF0A - reached only when ebx is 0x30 or 0x1F5), then a
    // shared third pop (StackPopInteger, 0x0052EF99) common to every branch that sets ebp=1
    // (ebx==0x1F5 or 0x1F6). No other StackPop* call in the whole function can be reached
    // with ebx==0x1F5 - the two extra pops guarded by `cmp ebx,0x30`/`cmp ebx,0xea` and the
    // nParams>=7 branch are for OTHER routine numbers only. The call into
    // CSWSCreature::AddCastSpellActions (0x0052F0EA) that follows constructs its own 18-dword
    // argument list from these three popped values plus hardcoded constants (0, 0xFF, -1) -
    // NOT from four more popped values - so `bCheat`/`nDomainLevel`/`nProjectilePathType`/
    // `bInstantSpell` (nwscript.nss declares all seven) are pushed by a compiled script but
    // NEVER READ by this build's native handler. There is no working native "free/instant
    // cast" route through this routine - see docs/research/effect-sync.md §14.2 for why the
    // task's optional cheat flag is not implemented.
    //
    // ⚠ NEEDS-LIVE: the routine-table match and the handler's own prologue bytes are checked
    // at the call site (VerifyCastSpellBytes) before every dispatch, but nobody has watched
    // this call land a real cast in the running game yet.

    private const int RoutineActionCastSpellAtObject = 501; // 0x1F5

    private const nint ExpectedCastSpell = 0x0052EE50;

    /// <summary>
    /// The first 32 bytes of <c>CSWVirtualMachineCommands::ExecuteCommandCastSpell</c>
    /// (0x0052EE50), read directly off the sha256-matched GOG v1.0.3 <c>swkotor.exe</c> -
    /// <c>push -1; push 0x71D048; mov eax,fs:[0]; push eax; mov fs:[0],esp; sub esp,0x40;
    /// push ebx; push ebp; push esi; push edi; lea eax,[esp+0x14]</c>. Checked before every
    /// dispatch alongside the routine-table entry, the same belt-and-braces pattern
    /// <c>Odyssey.MineActions.VerifyMineActionBytes</c> uses for a brand-new call target.
    /// </summary>
    private static ReadOnlySpan<byte> CastSpellPrologueBytes =>
    [
        0x6A, 0xFF, 0x68, 0x48, 0xD0, 0x71, 0x00, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00, 0x50,
        0x64, 0x89, 0x25, 0x00, 0x00, 0x00, 0x00, 0x83, 0xEC, 0x40, 0x53, 0x55, 0x56, 0x57,
        0x8D, 0x44, 0x24, 0x14,
    ];

    private static bool VerifyCastSpellBytes()
    {
        if (!NativeMethods.IsReadable(ExpectedCastSpell, (nuint)CastSpellPrologueBytes.Length))
        {
            return false;
        }

        byte* actual = (byte*)ExpectedCastSpell;
        for (int i = 0; i < CastSpellPrologueBytes.Length; i++)
        {
            if (actual[i] != CastSpellPrologueBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Queues <c>ActionCastSpellAtObject(nSpell, oTarget, nMetaMagic)</c> for
    /// <paramref name="casterObjectId"/> through the engine's own VM dispatch - the identical
    /// route a compiled script's own call takes, and the same <c>AddCastSpellActions</c>
    /// queuing function the client's radial-menu cast reaches through
    /// <c>HandlePlayerToServerInputMessage</c>. The animation, Force-point cost, projectile
    /// and impact all run exactly as the engine does them for either path - this is not a
    /// synthetic effect apply the way <c>applyeffect</c> is.
    ///
    /// Failure codes: 1 no VM, 2 no routine table, 3 the table entry no longer matches the
    /// verified handler, 4 the handler's own prologue bytes no longer match (paired with 3 as
    /// a second, independent check), 5 a script currently owns the VM stack, 6 a stack push
    /// refused, 7 the handler returned a nonzero status (unknown power, out of Force points,
    /// out of range/line of sight, or any other in-engine refusal - the handler does not say
    /// which), 8 the VM stack was left non-neutral (non-fatal; still returns true, matching
    /// <see cref="MoveToLocation"/>'s own "verify afterward" pattern).
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool CastSpellAtObject(
        uint casterObjectId, int spellId, uint targetObjectId, int metaMagic, out int failure)
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

        if (*(nint*)(table + (RoutineActionCastSpellAtObject * 4)) != ExpectedCastSpell)
        {
            failure = 3;
            return false;
        }

        if (!VerifyCastSpellBytes())
        {
            failure = 4;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 5;
            return false;
        }

        int stackPointer = *(int*)(internalVm + StackPointerOffset);

        // Redirect OBJECT_SELF to the caster, then restore exactly as MoveToLocation does.
        int savedHasCaller = *(int*)(implementer + ImplHasCallerObject);
        uint savedCallerId = *(uint*)(implementer + ImplCallerObjectId);

        *(int*)(implementer + ImplHasCallerObject) = 1;
        *(uint*)(implementer + ImplCallerObjectId) = casterObjectId;

        try
        {
            // ActionCastSpellAtObject(int nSpell, object oTarget, int nMetaMagic, ...): this
            // build's handler pops exactly these three (see the file header). Pushed in
            // reverse - nMetaMagic first, oTarget, nSpell last (top of stack, popped first) -
            // the same "reverse declaration order" convention AddMultiClass documents.
            if (StackPushInteger(vm, metaMagic) == 0
                || StackPushObject(vm, targetObjectId) == 0
                || StackPushInteger(vm, spellId) == 0)
            {
                failure = 6;
                return false;
            }

            var handler = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedCastSpell;
            int status = handler(implementer, RoutineActionCastSpellAtObject, 3);

            if (status != 0)
            {
                failure = 7;
                return false;
            }

            if (*(int*)(internalVm + StackPointerOffset) != stackPointer)
            {
                failure = 8;
            }

            return true;
        }
        finally
        {
            *(int*)(implementer + ImplHasCallerObject) = savedHasCaller;
            *(uint*)(implementer + ImplCallerObjectId) = savedCallerId;
        }
    }

    // ---- AddKnownSpell, VERIFIED(static) ----------------------------------------------
    //
    // CSWSCreatureStats::AddKnownSpell(int nClassIndex, int nSpellId) - 0x005AA9B0, __thiscall,
    // ret 8 (two stack args). Named and addressed by docs/research/class-and-power-grants.md
    // §3's routine-table scan (callers ReadSpellsFromGff and the debug menu). Disassembly:
    // `mov bl, byte ptr [esp+8]` (classIndex, compared against CSWSCreatureStats::class_count
    // at +0x89 - the SAME field Odyssey.ClassGrant.cs's StatsClassCount reads - refusing
    // silently, `jae`, when the index is out of range) then `mov edi, [esp+0x14]` (spellId,
    // read after three more pushes: 0x14-0xC=0x8, the second stack arg). No return value is
    // set before `ret` - success/failure is inferred by the caller the same way AddClass's
    // own verb reads the class list before/after, except here the live-test recipe (a `cast`
    // that now succeeds where it previously refused) is the actual confirmation.

    private const nint ExpectedAddKnownSpell = 0x005AA9B0;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> AddKnownSpellFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)ExpectedAddKnownSpell;

    /// <summary>First 24 bytes of <c>CSWSCreatureStats::AddKnownSpell</c> (0x005AA9B0):
    /// <c>push ebx; mov bl,[esp+8]; push esi; mov esi,ecx; cmp bl,[esi+0x89]; jae 0x5AAA20;
    /// mov eax,[0x7A3A28]; mov ecx,[eax+0x8C]</c>.</summary>
    private static ReadOnlySpan<byte> AddKnownSpellPrologueBytes =>
    [
        0x53, 0x8A, 0x5C, 0x24, 0x08, 0x56, 0x8B, 0xF1, 0x3A, 0x9E, 0x89, 0x00, 0x00, 0x00,
        0x73, 0x60, 0xA1, 0x28, 0x3A, 0x7A, 0x00, 0x8B, 0x88, 0x8C,
    ];

    private static bool VerifyAddKnownSpellBytes()
    {
        if (!NativeMethods.IsReadable(ExpectedAddKnownSpell, (nuint)AddKnownSpellPrologueBytes.Length))
        {
            return false;
        }

        byte* actual = (byte*)ExpectedAddKnownSpell;
        for (int i = 0; i < AddKnownSpellPrologueBytes.Length; i++)
        {
            if (actual[i] != AddKnownSpellPrologueBytes[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Grants <paramref name="casterObjectId"/> (in practice: always the local player) a
    /// <c>spells.2da</c> row through the engine's own <c>CSWSCreatureStats::AddKnownSpell</c> -
    /// never a raw poke of the known-spell bit array. <paramref name="classIndex"/> is the
    /// creature's class slot (0 or 1; see <c>Odyssey.TryReadLocalClassList</c>/<c>addclass</c>
    /// for a second Jedi slot).
    ///
    /// Failure codes: 1 the id does not resolve, 2 no stats block, 3 the class slot does not
    /// exist (index &gt;= class_count), 4 the handler's own prologue bytes no longer match the
    /// verified build.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryGrantKnownSpell(uint casterObjectId, int classIndex, int spellId, out int failure)
    {
        failure = 0;

        nint creature = ResolveServerObject(casterObjectId);
        if (creature == 0)
        {
            failure = 1;
            return false;
        }

        nint stats = Deref(creature + CreStats);
        if (stats == 0)
        {
            failure = 2;
            return false;
        }

        int classCount = *(byte*)(stats + StatsClassCount);
        if (classIndex < 0 || classIndex >= classCount)
        {
            failure = 3;
            return false;
        }

        if (!VerifyAddKnownSpellBytes())
        {
            failure = 4;
            return false;
        }

        AddKnownSpellFn(stats, classIndex, spellId);
        return true;
    }
}

// Odyssey.LevelUp.cs - the `levelup`/`stats` dev verbs (docs/research/class-and-power-
// grants.md §9): make the local player a REAL Jedi through the engine's own level-up logic, so
// `cast`/`grantpower` (effect-sync.md §14) finally have Force points to spend. Closes the dead
// end effect-sync.md §14.7 hit: "every character in the rig's saves is the level-1 test
// character with no Force points... the engine's own cast action cannot pay for the power."
//
// Route found by tracing CSWSCreatureStats::AutoLevelup (0x005B27E0) - the SAME function
// CSWGuiInGameCharacter::DoAutoLevelup (0x006AEC80, a raw tail `jmp` straight into it - the
// character screen's "Auto Level-Up" button) and the "Auto Level Up Party Members" option
// (CSWPartyTable::AutoLevelUpNPCs 0x00563B90, CSWPartyTable::DistributeExperience 0x005653A0,
// both gate it with CanLevelUp first) all reach. It takes ONE stack argument (DB says 4 bytes,
// matches the binary's own `ret 4`) but never reads it to pick a class: the class it levels is
// always classes[class_count-1] - the LAST slot, computed purely from
// CSWSCreatureStats+0x089/+0x08C (Odyssey.ClassGrant.cs's own StatsClassCount/StatsClasses/
// ClassInfoStride/ClassInfoClassId, reused verbatim here). That is exactly what `addclass`
// leaves behind (it always appends), so `addclass 3` then `levelup 1` levels the new Jedi
// class, never the base class.
//
// Internally AutoLevelup builds a CSWLevelStats level-up record (a rolled hit-point gain, a
// rolled Force-point gain, an ability-score-increase pick, and a feat pick via
// CSWClass::GetRecommendedFeat/CanChooseFeat, committed through the REAL
// CSWSCreatureStats::AddFeat 0x005AA810), then calls CSWSCreatureStats::ValidateLevelUp
// (0x005AF950), which calls CSWSCreatureStats::LevelUp (0x005AABF0) to apply that record.
// Disassembly of LevelUp (tools/disasm.py 0x5AABF0 2352) shows the exact two writes that
// matter here, both reached through the SAME record pointer LevelUp takes as its own second
// argument ([esp+0x1c] there):
//
//   0x005AAD20  mov eax, [esi+0x24]              ; eax = the owning CSWSCreature (backpointer)
//   0x005AAD23  movsx edi, word ptr [eax+0xe0]    ; NOTE: +0xE0 here is CSWSObject's own layout
//   0x005AAD2A  movzx edx, byte ptr [ecx+0x2b]    ;   as LevelUp sees it (record->hpGained)
//   0x005AAD2E  add edx, edi
//   0x005AAD30  mov [eax+0xe0], edx               ; hit_points += record.hpGained  (VERIFIED;
//                                                  ;   Odyssey.Core.cs's OWN split of +0xDC
//                                                  ;   current / +0xE0 stored-max does not
//                                                  ;   apply to a PC, whose +0xE0 the engine
//                                                  ;   never writes outside this path - see
//                                                  ;   Odyssey.Health.cs's own remarks)
//   0x005AAD36  movzx ax, byte ptr [ecx+0x2d]     ; record->fpGained
//   0x005AAD3B  add word ptr [esi+0x122], ax      ; force_points += record.fpGained (VERIFIED;
//                                                  ;   esi = LevelUp's own `this`, the stats
//                                                  ;   block - CSWSCreatureStats+0x122)
//
// So a level-up in a Force-using class raises CURRENT Force points by however much that level
// grants, the same way KOTOR raises current hit points on a level-up - NOT a raw poke (this IS
// the engine's own write, reached only by calling AutoLevelup), and NOT a "top up to max"
// (matches real game behaviour: leveling adds, it does not refill). That is why this file adds
// no separate "fill Force points" step: it falls out of calling the verified route.
//
// AutoLevelup never calls AddKnownSpell (0x005AA9B0) - tools/xrefs.py 0x5AA9B0 finds exactly
// two callers, ReadSpellsFromGff (loading saved data) and the level-up GUI's own debug path,
// neither of them reachable from AutoLevelup's call graph - so a NEW power's knowledge is never
// auto-granted by a level-up. `grantpower` (effect-sync.md §14) stays the separate step the
// task's own recipe already keeps it as; this file does not change that.
//
// XP gate: both real callers check CSWSCreatureStats::CanLevelUp (0x005A6810) before calling
// AutoLevelup - AutoLevelup does nothing observable otherwise (ValidateLevelUp's own
// precondition). Rather than reverse a two-stack-argument engine SetExperience/AddExperience
// whose semantics this pass did not verify, XP is topped up through the SAME raw field write
// `grantxp` already uses (Odyssey.Progression.SetExperience, CSWSCreatureStats+0x068,
// "consistently" per the task), with the target computed by the engine's own
// CSWSCreatureStats::CalculateExpNeededForNextLevel (0x005A5490) - the SAME calculator
// AddMultiClass's own handler uses for its DistributeExperience gap (class-and-power-
// grants.md §3). No raw poke of Force points anywhere in this file.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- CSWSCreatureStats::AutoLevelup, VERIFIED(static) -----------------------------

    private const nint ExpectedAutoLevelup = 0x005B27E0;

    /// <summary>First 32 bytes of <c>CSWSCreatureStats::AutoLevelup</c> (0x005B27E0), read
    /// directly off the sha256-matched GOG v1.0.3 <c>swkotor.exe</c> - the SEH prologue
    /// (<c>push -1; push 0x7222eb; mov eax,fs:[0]; push eax; mov fs:[0],esp; sub esp,0x44;
    /// push ebx; push ebp; xor ebp,ebp; push esi; push edi; mov [esp+0x14],ecx</c>).</summary>
    private static ReadOnlySpan<byte> AutoLevelupPrologueBytes =>
    [
        0x6A, 0xFF, 0x68, 0xEB, 0x22, 0x72, 0x00, 0x64, 0xA1, 0x00, 0x00, 0x00, 0x00, 0x50,
        0x64, 0x89, 0x25, 0x00, 0x00, 0x00, 0x00, 0x83, 0xEC, 0x44, 0x53, 0x55, 0x33, 0xED,
        0x56, 0x57, 0x89, 0x4C,
    ];

    private static readonly delegate* unmanaged[Thiscall]<nint, int, void> AutoLevelupFn =
        (delegate* unmanaged[Thiscall]<nint, int, void>)ExpectedAutoLevelup;

    // ---- CSWSCreatureStats::CanLevelUp, VERIFIED(static) ------------------------------

    private const nint ExpectedCanLevelUp = 0x005A6810;

    /// <summary>First 24 bytes of <c>CSWSCreatureStats::CanLevelUp</c> (0x005A6810).</summary>
    private static ReadOnlySpan<byte> CanLevelUpPrologueBytes =>
    [
        0xA1, 0xFC, 0x39, 0x7A, 0x00, 0x53, 0x56, 0x57, 0x8B, 0x78, 0x08, 0x6A, 0x00, 0x8B,
        0xF1, 0xE8, 0xAC, 0xF7, 0xFF, 0xFF, 0x8B, 0xCF, 0x0F, 0xB6,
    ];

    private static readonly delegate* unmanaged[Thiscall]<nint, int> CanLevelUpFn =
        (delegate* unmanaged[Thiscall]<nint, int>)ExpectedCanLevelUp;

    // ---- CSWSCreatureStats::CalculateExpNeededForNextLevel, VERIFIED(static) ----------

    private const nint ExpectedCalcExpNeeded = 0x005A5490;

    /// <summary>First 16 bytes of <c>CSWSCreatureStats::CalculateExpNeededForNextLevel</c>
    /// (0x005A5490) - same address class-and-power-grants.md §3 already names as
    /// <c>AddMultiClass</c>'s own XP-gap calculator.</summary>
    private static ReadOnlySpan<byte> CalcExpNeededPrologueBytes =>
    [
        0x8B, 0x51, 0x68, 0x8B, 0x0D, 0x28, 0x3A, 0x7A, 0x00, 0x56, 0x33, 0xC0, 0x8D, 0x64,
        0x24, 0x00,
    ];

    private static readonly delegate* unmanaged[Thiscall]<nint, int> CalcExpNeededFn =
        (delegate* unmanaged[Thiscall]<nint, int>)ExpectedCalcExpNeeded;

    // ---- CSWSCreature::GetMaxForcePoints, VERIFIED(static) -----------------------------
    //
    // The SAME address AutoLevelup itself calls first (0x005B286C: `mov ecx,[esi+0x24]; call
    // 0x4fd490` where esi is AutoLevelup's own `this`, i.e. the stats block, and +0x24 is its
    // creature backpointer) - a real, already-exercised-by-the-engine call target, not a new
    // guess. Zero stack arguments (DB and the binary's own bare `ret` agree).

    private const nint ExpectedGetMaxForcePoints = 0x004FD490;

    /// <summary>First 24 bytes of <c>CSWSCreature::GetMaxForcePoints</c>
    /// (0x004FD490).</summary>
    private static ReadOnlySpan<byte> GetMaxForcePointsPrologueBytes =>
    [
        0x51, 0x56, 0x8B, 0xF1, 0x8B, 0x86, 0x74, 0x0A, 0x00, 0x00, 0x66, 0x8B, 0x88, 0xDC,
        0x00, 0x00, 0x00, 0x66, 0x3B, 0x0D, 0x10, 0xBF, 0x74, 0x00,
    ];

    private static readonly delegate* unmanaged[Thiscall]<nint, short> GetMaxForcePointsFn =
        (delegate* unmanaged[Thiscall]<nint, short>)ExpectedGetMaxForcePoints;

    private static bool VerifyPrologue(nint address, ReadOnlySpan<byte> expected)
    {
        if (!NativeMethods.IsReadable(address, (nuint)expected.Length))
        {
            return false;
        }

        byte* actual = (byte*)address;
        for (int i = 0; i < expected.Length; i++)
        {
            if (actual[i] != expected[i])
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>CSWSCreatureStats::force_points - CURRENT Force points, SIGNED short (the same
    /// sign-extension caution <see cref="GetCurrentHitPoints"/> already documents for hit
    /// points applies here too, though nothing in this codebase has yet observed a negative
    /// value). VERIFIED: this file's own header traces <c>LevelUp</c>'s
    /// <c>add word ptr [stats+0x122], ax</c> incrementing this SAME field.</summary>
    private const int StatsForcePoints = 0x122;

    public static short GetCurrentForcePoints(nint creature)
    {
        nint stats = Deref(creature + CreStats);
        return stats == 0 ? (short)0 : *(short*)(stats + StatsForcePoints);
    }

    /// <summary>
    /// Writes CURRENT Force points (the same short at stats+0x122 that <c>LevelUp</c> increments).
    /// A plain field write: nothing derived hangs off it that the engine does not recompute from
    /// the field itself on its next read, and the value is clamped to [0, max] by the caller. Used
    /// only to put a duellist's Force points back as they were (docs/research/pvp-plan.md,
    /// NEEDS-LIVE: that the Force bar follows). MAIN THREAD ONLY.
    /// </summary>
    public static bool SetCurrentForcePoints(nint creature, short forcePoints)
    {
        nint stats = Deref(creature + CreStats);
        if (stats == 0 || !NativeMethods.IsReadable(stats + StatsForcePoints, 2))
        {
            return false;
        }

        *(short*)(stats + StatsForcePoints) = forcePoints;
        return true;
    }

    /// <summary>
    /// The engine's own computed max Force points for <paramref name="creature"/>, through
    /// <c>CSWSCreature::GetMaxForcePoints</c> - never a formula this codebase reimplements.
    /// Returns 0 (rather than a possibly-stale guess) when the handler's own bytes no longer
    /// match the verified build. MAIN THREAD ONLY.
    /// </summary>
    public static short GetMaxForcePoints(nint creature)
    {
        if (creature == 0 || !VerifyPrologue(ExpectedGetMaxForcePoints, GetMaxForcePointsPrologueBytes))
        {
            return 0;
        }

        return GetMaxForcePointsFn(creature);
    }

    /// <summary>
    /// Reads <c>classes[slot].known_spells</c> - a <c>CExoArrayList&lt;uint32&gt;</c> living at
    /// the ClassInfo entry's OWN base (ClassInfo+0x000; traced from
    /// <c>CSWSCreatureStats::AddKnownSpell</c> 0x005AA9B0's call into
    /// <c>CSWSCreatureStats_ClassInfo::AddKnownSpell</c> 0x00649660, which appends at
    /// <c>[data + count*4] = spellId</c> after a dedupe scan - the SAME array <c>grantpower</c>
    /// (<c>Odyssey.TryGrantKnownSpell</c>) adds to). Copies up to <paramref name="buffer"/>'s
    /// length into it, in array order, and returns the array's own TOTAL count even when it
    /// exceeds the buffer, so a caller can say "+N more". <paramref name="creature"/> is a
    /// CSWSCreature* (e.g. <see cref="GetPlayerCreaturePointer"/>'s own return), matching every
    /// other public read in this file - resolved to its stats block internally. MAIN THREAD
    /// ONLY.
    /// </summary>
    public static int ReadKnownSpells(nint creature, int slot, Span<int> buffer, out int totalCount)
    {
        totalCount = 0;
        nint stats = creature == 0 ? 0 : Deref(creature + CreStats);
        if (stats == 0 || slot is not (0 or 1))
        {
            return 0;
        }

        nint classInfo = stats + StatsClasses + (slot * ClassInfoStride);
        nint data = Deref(classInfo);
        totalCount = *(int*)(classInfo + 4);
        if (data == 0 || totalCount <= 0)
        {
            totalCount = Math.Max(totalCount, 0);
            return 0;
        }

        int n = Math.Min(totalCount, buffer.Length);
        for (int i = 0; i < n; i++)
        {
            buffer[i] = *(int*)(data + (i * 4));
        }

        return n;
    }

    /// <summary>
    /// Runs the engine's own <c>CSWSCreatureStats::AutoLevelup</c> on
    /// <paramref name="casterObjectId"/>'s LAST class slot, <paramref name="maxLevels"/> times
    /// (clamped 1-40, the two-class 20-level cap), topping up XP through the SAME raw field
    /// write <c>grantxp</c> uses whenever <c>CanLevelUp</c> says there is not yet enough. See
    /// this file's own header for the full route and why there is no separate "fill Force
    /// points" step.
    ///
    /// <paramref name="classArg"/> is either the slot index (0 or 1) or a classes.2da row id -
    /// it must name the SAME slot AutoLevelup will actually level (<c>classes[class_count-1]</c>,
    /// the LAST one; AutoLevelup takes no class-selecting argument of its own, see this file's
    /// header), or the call is refused with <paramref name="failure"/> 4 before anything is
    /// written.
    ///
    /// Returns false only when NOTHING could be granted (bad target/class-slot/verify failure -
    /// <paramref name="failure"/> 1-5); once the loop starts this returns true even if it stops
    /// early (max level reached, or the engine did not advance a level) - <paramref
    /// name="stopReason"/> says why, empty when the full <paramref name="maxLevels"/> was
    /// granted. MAIN THREAD ONLY.
    /// </summary>
    public static bool TryAutoLevelUp(
        uint casterObjectId, int classArg, int maxLevels,
        out int levelsGranted, out int slot, out int classId, out int finalLevel,
        out string stopReason, out int failure)
    {
        levelsGranted = 0;
        slot = -1;
        classId = -1;
        finalLevel = 0;
        stopReason = "";
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
        if (classCount <= 0)
        {
            failure = 3;
            return false;
        }

        int lastSlot = classCount - 1;
        int lastClassId = *(byte*)(stats + StatsClasses + (lastSlot * ClassInfoStride) + ClassInfoClassId);

        bool namesSlot = classArg is 0 or 1 && classArg == lastSlot;
        bool namesClassId = classArg == lastClassId;
        if (!namesSlot && !namesClassId)
        {
            slot = lastSlot;
            classId = lastClassId;
            failure = 4;
            return false;
        }

        slot = lastSlot;
        classId = lastClassId;

        if (!VerifyPrologue(ExpectedAutoLevelup, AutoLevelupPrologueBytes)
            || !VerifyPrologue(ExpectedCanLevelUp, CanLevelUpPrologueBytes)
            || !VerifyPrologue(ExpectedCalcExpNeeded, CalcExpNeededPrologueBytes))
        {
            failure = 5;
            return false;
        }

        int cap = Math.Clamp(maxLevels, 1, 40);
        for (int i = 0; i < cap; i++)
        {
            int levelBefore = *(byte*)(stats + StatsClasses + (lastSlot * ClassInfoStride) + ClassInfoLevel);

            if (CanLevelUpFn(stats) == 0)
            {
                int needed = CalcExpNeededFn(stats);
                if (needed <= 0)
                {
                    stopReason = levelsGranted == 0
                        ? "already at the maximum level - nothing to grant"
                        : "reached the maximum level";
                    break;
                }

                uint currentXp = GetExperience(creature);
                if ((uint)needed > currentXp)
                {
                    SetExperience(creature, (uint)needed);
                }

                if (CanLevelUpFn(stats) == 0)
                {
                    stopReason = $"XP top-up to {needed} did not make the engine's own "
                        + "CanLevelUp true - stopping rather than looping";
                    break;
                }
            }

            AutoLevelupFn(stats, 1);

            int levelAfter = *(byte*)(stats + StatsClasses + (lastSlot * ClassInfoStride) + ClassInfoLevel);
            if (levelAfter <= levelBefore)
            {
                stopReason = "AutoLevelup did not advance the class level (engine-side refusal) - stopping";
                break;
            }

            levelsGranted++;
            finalLevel = levelAfter;
        }

        if (levelsGranted == 0 && stopReason.Length == 0)
        {
            stopReason = "no levels granted";
        }

        return true;
    }
}

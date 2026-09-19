// Odyssey.ClassGrant.cs - a creature's class list, read; and AddMultiClass, replayed through
// the engine's own VM handler. docs/research/class-and-power-grants.md, coop.ini
// class_grant_sync. The read is for the local PC only (the one creature a peer's grant is ever
// applied to); the write is the SAME CSWVirtualMachineCommands::ExecuteCommandAddMultiClass
// call a script's own AddMultiClass(nClass, oTarget) reaches - never a raw poke of the
// class-count byte, because the handler does three things a poke would skip (AddClass's own
// UpdatePureGoodEvilPowers rebuild, and DistributeExperience's XP gap + level-up flag).

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    /// <summary><c>CSWSCreatureStats::class_count</c> — byte, 0..2 (symbol database 0x089;
    /// <c>AddClass</c> 0x005A5D10 caps it at 2: <c>cmp al, 2; jae</c>).</summary>
    private const int StatsClassCount = 0x089;

    /// <summary><c>CSWSCreatureStats::classes</c> — <c>CSWSCreatureStats_ClassInfo[2]</c> at
    /// 0x08C, stride 0x28 (<c>AddClass</c> indexes it as <c>(count-1)*5*8</c>).</summary>
    private const int StatsClasses = 0x08C;

    private const int ClassInfoStride = 0x28;

    /// <summary><c>CSWSCreatureStats_ClassInfo::class_id</c> — byte at +0x1B (the
    /// <c>mov byte ptr [ecx+eax*8+0xa7], dl</c> write in <c>AddClass</c> is 0x8C + 0x1B).</summary>
    private const int ClassInfoClassId = 0x1B;

    /// <summary><c>CSWSCreatureStats_ClassInfo::level</c> — byte at +0x1C (<c>AddClass</c>'s
    /// second argument lands at 0x8C + 0x1C = 0xA8; the VM handler passes 0).</summary>
    private const int ClassInfoLevel = 0x1C;

    /// <summary><c>CSWVirtualMachineCommands::ExecuteCommandAddMultiClass</c>; see
    /// <see cref="AddMultiClassHook"/> for the evidence.</summary>
    private const nint ExpectedAddMultiClass = AddMultiClassHook.HandlerAddress;

    private static int _routineAddMultiClass = -1;

    /// <summary>
    /// The local player creature's class list: how many classes it has (0..2) and, for each
    /// slot, the <c>classes.2da</c> row and the level in that class. False before a world is
    /// loaded or when the creature has no stats block. MAIN THREAD ONLY.
    /// </summary>
    public static bool TryReadLocalClassList(
        out int classCount, out int class0, out int level0, out int class1, out int level1)
    {
        classCount = 0;
        class0 = -1;
        level0 = 0;
        class1 = -1;
        level1 = 0;

        nint player = GetPlayerCreaturePointer();
        if (player == 0)
        {
            return false;
        }

        nint stats = Deref(player + CreStats);
        if (stats == 0)
        {
            return false;
        }

        classCount = *(byte*)(stats + StatsClassCount);
        if (classCount >= 1)
        {
            class0 = *(byte*)(stats + StatsClasses + ClassInfoClassId);
            level0 = *(byte*)(stats + StatsClasses + ClassInfoLevel);
        }

        if (classCount >= 2)
        {
            class1 = *(byte*)(stats + StatsClasses + ClassInfoStride + ClassInfoClassId);
            level1 = *(byte*)(stats + StatsClasses + ClassInfoStride + ClassInfoLevel);
        }

        return true;
    }

    /// <summary>
    /// Runs the engine's own <c>AddMultiClass(nClass, oTarget)</c> handler against
    /// <paramref name="targetObjectId"/> — the identical VM command a script's call reaches,
    /// with the identical argument order (declaration order reversed on the VM stack: the
    /// object first, the class last, since the handler pops the integer first). Returns false
    /// with a numbered <paramref name="failure"/> on refusal:
    /// 1 no VM, 2 no routine table, 3 the table no longer holds this handler, 4 a script
    /// currently owns the VM stack, 5 a stack push refused, 6 the handler returned an error.
    ///
    /// <paramref name="isReplay"/> true (a peer's grant being applied here) arms
    /// <see cref="AddMultiClassHook.NextGrantIsReplay"/> around the one call so the capture the
    /// hook makes is not republished; false (the <c>addclass</c> dev verb simulating a script)
    /// leaves it unarmed so the capture IS published, exactly as Dorak's script would be.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool AddMultiClass(uint targetObjectId, int classId, bool isReplay, out int failure)
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

        if (_routineAddMultiClass < 0)
        {
            _routineAddMultiClass = FindRoutine(table, ExpectedAddMultiClass);
        }

        if (_routineAddMultiClass < 0)
        {
            failure = 3;
            return false;
        }

        if (*(int*)(internalVm + CallStackSizeOffset) != 0)
        {
            failure = 4;
            return false;
        }

        // AddMultiClass(int nClass, object oTarget): pushed in reverse declaration order, so
        // the handler's StackPopInteger (first) gets the class and StackPopObject (second)
        // gets the target.
        if (StackPushObject(vm, targetObjectId) == 0 || StackPushInteger(vm, classId) == 0)
        {
            failure = 5;
            return false;
        }

        var handler = (delegate* unmanaged[Thiscall]<nint, int, int, int>)ExpectedAddMultiClass;

        AddMultiClassHook.NextGrantIsReplay = isReplay;
        int result;
        try
        {
            result = handler(implementer, _routineAddMultiClass, 2);
        }
        finally
        {
            AddMultiClassHook.NextGrantIsReplay = false;
        }

        if (result != 0)
        {
            failure = 6;
            return false;
        }

        return true;
    }
}

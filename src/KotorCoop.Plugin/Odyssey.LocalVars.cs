// Odyssey.LocalVars.cs - per-object local variables (SetLocalBoolean/SetLocalNumber), for
// local_var_sync and the `localvar` live-test verb. docs/research/quest-sync.md §8.1/§8.3.

using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- CSWVarTable accessors ------------------------------------------------------
    //
    // VERIFIED by disassembly (tools/disasm.py 0x59b000 70) and named by tools/kotor1_0_3.db:
    //   GetLocalBoolean 0x0059B000  thiscall(table, index) -> int (the masked bit, 0 = clear),
    //                               index outside 0..95 -> 0, `ret 4`.
    //   SetLocalBoolean 0x0059B040  thiscall(table, index, value): value != 0 ORs the bit in,
    //                               0 clears it; index outside 0..95 -> no-op, `ret 8`.
    //   GetLocalNumber  0x0059B0B0  thiscall(table, index) -> al = byte [table + 0xC + index],
    //                               index outside 0..7 -> 0, `ret 4`.
    //   SetLocalNumber  0x0059B0D0  thiscall(table, index, value): stores the low byte,
    //                               index outside 0..7 -> no-op, `ret 8`.
    // The two setters' ONLY callers are ExecuteCommandSetLocalValue's two call sites
    // (tools/xrefs.py 0x59b040 / 0x59b0d0: 0x00543232 and 0x005431FE), so calling them is
    // exactly the engine's own write, minus the VM stack pops. They touch nothing but the
    // table's own words: no derived state exists to rebuild.

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int> GetLocalBooleanFn =
        (delegate* unmanaged[Thiscall]<nint, int, int>)0x0059B000;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetLocalBooleanFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x0059B040;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, byte> GetLocalNumberFn =
        (delegate* unmanaged[Thiscall]<nint, int, byte>)0x0059B0B0;

    private static readonly delegate* unmanaged[Thiscall]<nint, int, int, void> SetLocalNumberFn =
        (delegate* unmanaged[Thiscall]<nint, int, int, void>)0x0059B0D0;

    /// <summary>CSWSObject + this = the object's own CSWVarTable. VERIFIED: the default branch of
    /// ExecuteCommandSetLocalValue, 0x005431CD <c>lea eax, [esi + 0x110]</c>, for every object
    /// that is neither an area nor a module.</summary>
    public const int ObjLocalVarTable = 0x110;

    /// <summary>The engine's own object-type constants the wrapper compares
    /// <c>byte [object+8]</c> against (0x0054319B area, 0x005431B4 module). Read live, the same
    /// way the engine reads them, rather than hardcoding their values (4 and 3 in the GOG image).</summary>
    private const nint ObjectTypeAreaConstant = 0x00746633;
    private const nint ObjectTypeModuleConstant = 0x00746632;

    /// <summary>CGameObject + this = the type byte the wrapper reads (0x00543197).</summary>
    private const int ObjTypeByte = 0x08;

    /// <summary>
    /// Resolves a server object id to the object and its OWN local-variable table, refusing an
    /// area or module (their tables live elsewhere and no GIT key can name them) and any id
    /// whose object does not carry that id at +4. Pure reads plus the engine's id lookup.
    /// MAIN THREAD ONLY.
    /// </summary>
    public static bool TryResolveLocalVarTable(uint objectId, out nint obj, out nint table, out string why)
    {
        obj = 0;
        table = 0;

        nint server = GetServer();
        if (server == 0)
        {
            why = "no server";
            return false;
        }

        nint gameObject = GetGameObjectFn(server, objectId);
        if (gameObject == 0)
        {
            why = $"no object 0x{objectId:X} in this module";
            return false;
        }

        if (*(uint*)(gameObject + 0x04) != objectId)
        {
            why = $"object 0x{objectId:X} is not a CSWSObject at the id offset (an item?)";
            return false;
        }

        byte type = *(byte*)(gameObject + ObjTypeByte);
        if (type == *(byte*)ObjectTypeAreaConstant || type == *(byte*)ObjectTypeModuleConstant)
        {
            why = $"object 0x{objectId:X} is an area or module";
            return false;
        }

        obj = gameObject;
        table = gameObject + ObjLocalVarTable;
        why = string.Empty;
        return true;
    }

    /// <summary>Reads one slot through the engine's own getter. MAIN THREAD ONLY.</summary>
    public static sbyte ReadLocalVar(nint table, LocalVarKind kind, int slot) =>
        kind == LocalVarKind.Boolean
            ? (sbyte)(GetLocalBooleanFn(table, slot) != 0 ? 1 : 0)
            : unchecked((sbyte)GetLocalNumberFn(table, slot));

    /// <summary>Writes one slot through the engine's own setter. Never re-enters
    /// <see cref="LocalVarSyncHook"/>: the hook splices the VM wrapper's call sites, not the
    /// setters themselves. MAIN THREAD ONLY.</summary>
    public static void WriteLocalVar(nint table, LocalVarKind kind, int slot, sbyte value)
    {
        if (!LocalVarSyncRule.IsValidSlot(kind, slot))
        {
            return;
        }

        if (kind == LocalVarKind.Boolean)
        {
            SetLocalBooleanFn(table, slot, value != 0 ? 1 : 0);
        }
        else
        {
            SetLocalNumberFn(table, slot, value);
        }
    }
}

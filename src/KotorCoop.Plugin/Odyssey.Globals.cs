// Odyssey.Globals.cs - the `setglobal` live-test primitive: writing a script global by NAME
// through the engine's own type-checked setter, in-process, instead of the agent-side raw
// table poke KotorCoop.Memory/GlobalVariableTable.cs already has (TryWriteBoolean/
// TryWriteNumber, out-of-process — reads the same catalogue, but a raw byte/bit write, not a
// call through the engine's own code). See docs/research/quest-sync.md's 2026-08-27 addendum
// for the full disassembly this implements.

namespace KotorCoop.Plugin;

internal static unsafe partial class Odyssey
{
    // ---- script globals: setglobal -----------------------------------------------

    /// <summary>CServerExoAppInternal + this = the embedded CSWGlobalVariableTable. VERIFIED,
    /// fresh disasm this pass of CServerExoApp::GetGlobalVariableTable (0x004AEE60:
    /// `mov eax,[ecx+4]; add eax,0x100FC; ret` — a bare thiscall accessor, no stack args) —
    /// the exact call ExecuteCommandSetGlobalNumber (0x00542B60) makes before SetValueNumber.
    /// Matches KotorCoop.Memory/GlobalVariableTable.cs's independently-derived
    /// GlobalTableOffset exactly (that file's own agent-side, out-of-process reader), which is
    /// itself corroborating evidence this offset is right.</summary>
    private const int ServerGlobalVariableTableOffset = 0x100FC;

    /// <summary>
    /// CSWGlobalVariableTable::SetValueNumber(this, CExoString* name, int value) -> void.
    /// VERIFIED, fresh disasm this pass (0x00529680, `ret 8` — two stack args, matching a
    /// pointer + an int). Resolves the identifier by name through
    /// CSWGlobalVariableTable::FindIdentifierIndex (0x00529030, a hash-based catalogue lookup
    /// SetValueBoolean shares byte-for-byte), refuses silently (logs internally, returns) if
    /// the name is undeclared OR declared as a type other than Number — this plugin cannot read
    /// that internal log, so a refusal reads identically to writing the value it already held;
    /// <see cref="TryWriteGlobal"/> reads the value back before and after to give an honest
    /// answer despite that. This IS the C++ setter
    /// CSWVirtualMachineCommands::ExecuteCommandSetGlobalNumber (0x00542B60) itself calls once
    /// it has popped its own two VM arguments — called directly here instead of through that
    /// machinery, the same choice <see cref="OpenStore"/> makes for <c>ShowStoreGui</c> over
    /// <c>ExecuteCommandOpenStore</c>.
    /// </summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, int, void> SetGlobalValueNumberFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, int, void>)0x00529680;

    /// <summary>CSWGlobalVariableTable::SetValueBoolean(this, CExoString* name, int value) ->
    /// void. Same shape as <see cref="SetGlobalValueNumberFn"/>, VERIFIED at 0x00529570 (fresh
    /// disasm this pass, `ret 8`).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, int, void> SetGlobalValueBooleanFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, int, void>)0x00529570;

    /// <summary>CSWGlobalVariableTable::GetValueNumber(this, CExoString* name, sbyte* outValue)
    /// -> void. VERIFIED, fresh disasm this pass (0x00529240): same FindIdentifierIndex lookup,
    /// zeroes *outValue unconditionally before the type/existence check, so a genuine 0 and a
    /// refusal are NOT distinguishable from the out value alone (documented gap, see
    /// <see cref="TryWriteGlobal"/>'s own remarks).</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, sbyte*, void> GetGlobalValueNumberFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, sbyte*, void>)0x00529240;

    /// <summary>CSWGlobalVariableTable::GetValueBoolean(this, CExoString* name, int* outValue)
    /// -> void. Same shape as <see cref="GetGlobalValueNumberFn"/>, VERIFIED at 0x00529110.</summary>
    private static readonly delegate* unmanaged[Thiscall]<nint, byte*, int*, void> GetGlobalValueBooleanFn =
        (delegate* unmanaged[Thiscall]<nint, byte*, int*, void>)0x00529110;

    /// <summary>The embedded global variable table, or 0 while no server exists (main menu).</summary>
    private static nint GetGlobalVariableTable()
    {
        nint server = GetServer();
        if (server == 0)
        {
            return 0;
        }

        nint internalServer = Deref(server + AppServerOffsetInternal);
        return internalServer == 0 ? 0 : internalServer + ServerGlobalVariableTableOffset;
    }

    /// <summary>Builds a CExoString on <paramref name="destination"/> (an 8-byte buffer: char*
    /// @0, length @4 — the same shape <see cref="CreatureFinder"/>'s TagOffset comment and
    /// every other CExoString-building Odyssey.*.cs file already assume) from an ASCII name at
    /// most 22 characters long — the catalogue's own <c>IdentifierNameLength</c>
    /// (KotorCoop.Memory/GlobalVariableTable.cs).</summary>
    private static void BuildGlobalNameExoString(byte* destination, byte* nameAscii, int nameLength)
    {
        byte* buffer = stackalloc byte[24];
        for (int i = 0; i < nameLength; i++)
        {
            buffer[i] = nameAscii[i];
        }

        buffer[nameLength] = 0;

        for (int i = 0; i < 8; i++)
        {
            destination[i] = 0;
        }

        CExoStringCtorFn(destination, buffer);
    }

    /// <summary>
    /// Writes a boolean or number script global by NAME through the engine's own type-checked
    /// setter (never a raw table poke — see the file header). <paramref name="asBoolean"/>
    /// picks the setter; <paramref name="value"/> is 0/1 for a boolean (any nonzero is coerced
    /// to 1) or a signed byte's worth of range for a number (the engine's own number globals
    /// are single bytes, KotorCoop.Memory/GlobalVariableTable.cs's own <c>NumberCount</c>/
    /// <c>sbyte</c> comments — this does not clamp, since neither does the engine's own setter
    /// per the disassembly this pass read).
    ///
    /// Reads the value back before and after, through the matching getter, and reports both —
    /// the getter zeroes its out-param on ANY refusal (undeclared name, or declared as the
    /// other type), so a refusal that happens to already hold the requested value is
    /// indistinguishable from success purely by the read-back; <paramref name="before"/> and
    /// <paramref name="after"/> are handed back regardless so the caller can say exactly what
    /// it saw rather than claim more confidence than this call actually has.
    ///
    /// MAIN THREAD ONLY.
    /// </summary>
    internal static bool TryWriteGlobal(
        byte* nameAscii, int nameLength, bool asBoolean, int value,
        out int before, out int after, out string reason)
    {
        reason = string.Empty;
        before = 0;
        after = 0;

        if (nameAscii == null || nameLength <= 0 || nameLength > 22)
        {
            reason = "global name must be 1-22 characters (the catalogue's own identifier length)";
            return false;
        }

        nint table = GetGlobalVariableTable();
        if (table == 0)
        {
            reason = "no server (not in a world)";
            return false;
        }

        byte* exoString = stackalloc byte[8];
        BuildGlobalNameExoString(exoString, nameAscii, nameLength);

        if (asBoolean)
        {
            int beforeInt = 0;
            GetGlobalValueBooleanFn(table, exoString, &beforeInt);
            before = beforeInt;

            SetGlobalValueBooleanFn(table, exoString, value != 0 ? 1 : 0);

            int afterInt = 0;
            GetGlobalValueBooleanFn(table, exoString, &afterInt);
            after = afterInt;
        }
        else
        {
            sbyte beforeByte = 0;
            GetGlobalValueNumberFn(table, exoString, &beforeByte);
            before = beforeByte;

            SetGlobalValueNumberFn(table, exoString, value);

            sbyte afterByte = 0;
            GetGlobalValueNumberFn(table, exoString, &afterByte);
            after = afterByte;
        }

        CExoStringDtorFn(exoString);
        return true;
    }
}

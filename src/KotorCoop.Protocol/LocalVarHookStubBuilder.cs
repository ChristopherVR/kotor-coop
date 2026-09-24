using System.Buffers.Binary;

namespace KotorCoop.Protocol;

/// <summary>
/// Pure x86 byte builder for the two <c>local_var_sync</c> capture splices inside
/// <c>CSWVirtualMachineCommands::ExecuteCommandSetLocalValue 0x00543130</c>
/// (docs/research/quest-sync.md §8.3). Each splice replaces exactly one whole instruction — the
/// 5-byte <c>call</c> to the lower <c>CSWVarTable</c> setter — with a <c>jmp</c> to a stub that
/// reports the write to a cdecl callback and then performs the original call itself.
///
/// VERIFIED frame at both call sites (<c>tools/disasm.py 0x543130 80</c>): the wrapper does
/// <c>sub esp,0xC</c>, pops the object id into <c>[esp+8]</c>, the index into <c>[esp+4]</c> and
/// the value into <c>[esp]</c>, then <c>push esi</c> (0x00543186). Each branch then pushes the
/// setter's two arguments (value, then index) and sets <c>ecx=eax</c> (the table). So at the
/// splice, before the call runs: <c>[esp]</c> = index, <c>[esp+4]</c> = value,
/// <c>[esp+8]</c> = saved esi, <c>[esp+0x0C]</c>/<c>[esp+0x10]</c> = the popped value/index
/// locals, <c>[esp+0x14]</c> = the popped object id; <c>esi</c> = the resolved object and
/// <c>eax</c> = <c>ecx</c> = the table pointer.
/// </summary>
public static class LocalVarHookStubBuilder
{
    /// <summary><c>call CSWVarTable::SetLocalNumber</c> — the only caller of 0x0059B0D0
    /// (<c>tools/xrefs.py 0x59b0d0</c>).</summary>
    public const uint NumberCallSite = 0x005431FE;

    /// <summary><c>call CSWVarTable::SetLocalBoolean</c> — the only caller of 0x0059B040
    /// (<c>tools/xrefs.py 0x59b040</c>).</summary>
    public const uint BooleanCallSite = 0x00543232;

    public const uint SetLocalNumber = 0x0059B0D0;
    public const uint SetLocalBoolean = 0x0059B040;

    /// <summary>The 8 bytes before the number call site (never overwritten):
    /// <c>push ecx; mov ecx,[esp+0xC]; push ecx; mov ecx,eax</c>.</summary>
    public static ReadOnlySpan<byte> NumberAnchor => [0x51, 0x8B, 0x4C, 0x24, 0x0C, 0x51, 0x8B, 0xC8];

    /// <summary>The 8 bytes before the boolean call site (never overwritten):
    /// <c>mov ecx,[esp+8]; push edx; push ecx; mov ecx,eax</c>.</summary>
    public static ReadOnlySpan<byte> BooleanAnchor => [0x8B, 0x4C, 0x24, 0x08, 0x52, 0x51, 0x8B, 0xC8];

    /// <summary>The overwritten instruction at <see cref="NumberCallSite"/>:
    /// <c>call 0x59b0d0</c>.</summary>
    public static ReadOnlySpan<byte> NumberCall => [0xE8, 0xCD, 0x7E, 0x05, 0x00];

    /// <summary>The overwritten instruction at <see cref="BooleanCallSite"/>:
    /// <c>call 0x59b040</c>.</summary>
    public static ReadOnlySpan<byte> BooleanCall => [0xE8, 0x09, 0x7E, 0x05, 0x00];

    public const int SiteLength = 5;
    public const int StubLength = 36;

    /// <summary>Offsets of the original frame's slots after <c>pushad</c> (+0x20) and the N
    /// pushes that precede each read. Every push moves esp by 4, so the value (orig +4, read
    /// first) and the index (orig +0, read after one push) share the SAME displacement, and the
    /// object id (orig +0x14) is read after two pushes.</summary>
    public const byte ValueOffsetAfterPushad = 0x20 + 0x04;
    public const byte IndexOffsetAfterOnePush = 0x20 + 0x00 + 0x04;
    public const byte ObjectIdOffsetAfterTwoPushes = 0x20 + 0x14 + 0x08;

    /// <summary>
    /// Writes the stub: <c>pushad</c>; push value, index, object id, table (eax), object (esi)
    /// and the kind; <c>call callback</c> (cdecl, six dwords); <c>add esp,0x18</c>;
    /// <c>popad</c>; the original <c>call setter</c> re-encoded for the stub's address; and
    /// <c>jmp</c> back to the instruction after the call site. Registers, flags that the
    /// original call would see, and the setter's own arguments are all exactly as the engine
    /// left them. Returns the byte count, or 0 when <paramref name="destination"/> is short.
    /// </summary>
    public static int WriteStub(
        Span<byte> destination, LocalVarKind kind, uint callbackAddress, uint stubAddress,
        uint setterAddress, uint callSite)
    {
        if (destination.Length < StubLength)
        {
            return 0;
        }

        int n = 0;
        destination[n++] = 0x60; // pushad
        destination[n++] = 0xFF; destination[n++] = 0x74; destination[n++] = 0x24; destination[n++] = ValueOffsetAfterPushad;       // push [esp+0x24] value
        destination[n++] = 0xFF; destination[n++] = 0x74; destination[n++] = 0x24; destination[n++] = IndexOffsetAfterOnePush;      // push [esp+0x24] index
        destination[n++] = 0xFF; destination[n++] = 0x74; destination[n++] = 0x24; destination[n++] = ObjectIdOffsetAfterTwoPushes; // push [esp+0x3C] object id
        destination[n++] = 0x50; // push eax (table)
        destination[n++] = 0x56; // push esi (object)
        destination[n++] = 0x6A; destination[n++] = (byte)kind; // push kind
        destination[n++] = 0xE8; // call callback
        WriteRelative(destination.Slice(n, 4), callbackAddress, unchecked(stubAddress + (uint)n + 4));
        n += 4;
        destination[n++] = 0x83; destination[n++] = 0xC4; destination[n++] = 0x18; // add esp, 0x18
        destination[n++] = 0x61; // popad
        destination[n++] = 0xE8; // call setter (the relocated original instruction)
        WriteRelative(destination.Slice(n, 4), setterAddress, unchecked(stubAddress + (uint)n + 4));
        n += 4;
        destination[n++] = 0xE9; // jmp back past the call site
        WriteRelative(destination.Slice(n, 4), unchecked(callSite + SiteLength), unchecked(stubAddress + (uint)n + 4));
        n += 4;

        // Pad to the fixed length with int3 so a stray fall-through can never run on.
        while (n < StubLength)
        {
            destination[n++] = 0xCC;
        }

        return StubLength;
    }

    /// <summary>The 5-byte <c>jmp stub</c> that replaces the call at <paramref name="callSite"/>.</summary>
    public static bool WriteSiteJump(Span<byte> destination, uint stubAddress, uint callSite)
    {
        if (destination.Length < SiteLength)
        {
            return false;
        }

        destination[0] = 0xE9;
        WriteRelative(destination[1..5], stubAddress, unchecked(callSite + SiteLength));
        return true;
    }

    /// <summary>The absolute target of a 5-byte <c>E8 rel32</c> at <paramref name="site"/>.</summary>
    public static uint CallTarget(ReadOnlySpan<byte> call, uint site) =>
        unchecked(site + SiteLength + (uint)BinaryPrimitives.ReadInt32LittleEndian(call[1..5]));

    private static void WriteRelative(Span<byte> destination, uint target, uint instructionEnd) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, unchecked((int)(target - instructionEnd)));
}

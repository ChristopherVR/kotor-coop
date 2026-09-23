using System.Buffers.Binary;

namespace KotorCoop.Protocol;

/// <summary>Pure x86 byte builder for the two verified CSWGuiStore getter detours. The copied
/// getter entry is eight bytes and contains no PC-relative instruction. The callback is cdecl
/// with explicit (panel,item) arguments; the entry stub preserves every register except EAX,
/// restores EAX to the callback's price result, then performs the retail <c>ret 4</c>.</summary>
public static class StorePriceHookStubBuilder
{
    public static ReadOnlySpan<byte> GetterPrologue => [0x8B, 0xC1, 0x8B, 0x0D, 0xFC, 0x39, 0x7A, 0x00];

    public const int TrampolineLength = 13;
    public const int DetourLength = 25;
    public const byte ItemArgumentOffsetAfterPushad = 0x24;
    public const byte ThisPointerOffsetAfterItemPush = 0x1C;

    public static int WriteTrampoline(Span<byte> destination, ReadOnlySpan<byte> prologue,
        uint entryAddress, uint trampolineAddress)
    {
        if (destination.Length < TrampolineLength || !prologue.SequenceEqual(GetterPrologue))
        {
            return 0;
        }

        prologue.CopyTo(destination);
        destination[8] = 0xE9;
        WriteRelative(destination[9..13], unchecked(entryAddress + 8), unchecked(trampolineAddress + TrampolineLength));
        return TrampolineLength;
    }

    public static int WriteDetour(Span<byte> destination, uint callbackAddress, uint stubAddress)
    {
        if (destination.Length < DetourLength)
        {
            return 0;
        }

        destination[0] = 0x60; // pushad
        destination[1] = 0xFF; destination[2] = 0x74; destination[3] = 0x24; destination[4] = ItemArgumentOffsetAfterPushad; // push original [entry esp+4] item
        destination[5] = 0xFF; destination[6] = 0x74; destination[7] = 0x24; destination[8] = ThisPointerOffsetAfterItemPush; // push saved ECX after first push
        destination[9] = 0xE8; // call cdecl callback(panel, item)
        WriteRelative(destination[10..14], callbackAddress, unchecked(stubAddress + 14));
        destination[14] = 0x83; destination[15] = 0xC4; destination[16] = 0x08; // add esp, 8
        destination[17] = 0x89; destination[18] = 0x44; destination[19] = 0x24; destination[20] = 0x1C; // save result as saved EAX
        destination[21] = 0x61; // popad
        destination[22] = 0xC2; destination[23] = 0x04; destination[24] = 0x00; // ret 4
        return DetourLength;
    }

    public static bool WriteEntryJump(Span<byte> destination, uint stubAddress, uint entryAddress)
    {
        if (destination.Length < GetterPrologue.Length)
        {
            return false;
        }

        destination[0] = 0xE9;
        WriteRelative(destination[1..5], stubAddress, unchecked(entryAddress + 5));
        destination[5] = destination[6] = destination[7] = 0x90;
        return true;
    }

    private static void WriteRelative(Span<byte> destination, uint target, uint instructionEnd) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, unchecked((int)(target - instructionEnd)));
}

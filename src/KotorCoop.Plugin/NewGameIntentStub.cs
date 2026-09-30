using System.Buffers.Binary;

namespace KotorCoop.Plugin;

internal static class NewGameIntentStub
{
    // OnNewGamePicked, after all three refusal branches and AddPanel.
    internal static ReadOnlySpan<byte> Original => [0x8B,0x15,0xEC,0xC5,0x74,0x00];
    internal const int Length = 20;
    internal static void Write(Span<byte> bytes, uint address, uint callback, uint resume)
    {
        if (bytes.Length < Length) throw new ArgumentException("stub buffer too short", nameof(bytes));
        bytes[0]=0x9C; bytes[1]=0x60; // pushfd / pushad
        bytes[2]=0xE8; BinaryPrimitives.WriteUInt32LittleEndian(bytes[3..],unchecked(callback-address-7));
        bytes[7]=0x61; bytes[8]=0x9D; // popad / popfd
        Original.CopyTo(bytes[9..]);
        bytes[15]=0xE9; BinaryPrimitives.WriteUInt32LittleEndian(bytes[16..],unchecked(resume-address-Length));
    }
}

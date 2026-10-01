namespace KotorCoop.Plugin;

internal static class NativeProbeContextRule
{
    // pushfd; pushad; lea eax,[esp]; push eax. The saved ESP points AFTER
    // pushfd, so original caller ESP is saved ESP+4.
    public static System.ReadOnlySpan<byte> SaveAndPushContext => [0x9C, 0x60, 0x8D, 0x04, 0x24, 0x50];
    public static System.ReadOnlySpan<byte> RestoreContext => [0x83, 0xC4, 0x04, 0x61, 0x9D];
    public const int SavedEsp = 12;
    public const int SavedEcx = 24;
    public const int OriginalEspAdjustment = 4;
    public static bool TryReadOriginalStack(System.ReadOnlySpan<byte> context, out uint original)
    {
        original = 0;
        if (context.Length < 36) return false;
        uint saved = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(context.Slice(SavedEsp, 4));
        if (saved > uint.MaxValue - OriginalEspAdjustment) return false;
        original = saved + OriginalEspAdjustment;
        return true;
    }
}

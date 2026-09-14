namespace KotorCoop.Plugin;

internal static partial class NativeMethods
{
    /// <summary>
    /// Checks every region covering an x86 engine field, including page boundaries.
    /// This does not prove object identity or prevent another thread freeing memory after
    /// the check. Engine object access must still run on the game thread with liveness checks.
    /// </summary>
    public static bool IsReadable(nint address, nuint length = 4) =>
        HasAccess(address, length, writable: false);

    public static bool IsWritable(nint address, nuint length = 4) =>
        HasAccess(address, length, writable: true);

    private static unsafe bool HasAccess(nint address, nuint length, bool writable)
    {
        const nuint limit = 0x7FFF_0000;
        if (address < 0x10000 || (nuint)address >= limit || length == 0
            || length > limit - (nuint)address)
        {
            return false;
        }

        nuint cursor = (nuint)address;
        nuint remaining = length;
        while (remaining > 0)
        {
            if (VirtualQuery((nint)cursor, out MemoryBasicInformation info,
                    (nuint)sizeof(MemoryBasicInformation)) == 0
                || info.State != MemCommit || (info.Protect & (0x100 | 0x01)) != 0)
            {
                return false;
            }

            const uint readableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;
            const uint writableMask = 0x04 | 0x08 | 0x40 | 0x80;
            if ((info.Protect & (writable ? writableMask : readableMask)) == 0
                || (nuint)info.BaseAddress > cursor)
            {
                return false;
            }

            nuint offset = cursor - (nuint)info.BaseAddress;
            if (offset >= info.RegionSize)
            {
                return false;
            }

            nuint available = info.RegionSize - offset;
            if (remaining <= available)
            {
                return true;
            }

            cursor += available;
            remaining -= available;
        }

        return true;
    }

    /// <summary>Bounds an engine strlen before handing a string to native code.</summary>
    public static unsafe bool IsReadableTerminatedString(nint address, int length) =>
        length is > 0 and <= 64 && IsReadable(address, (nuint)length)
        && new ReadOnlySpan<byte>((void*)address, length).IndexOf((byte)0) >= 0;
}

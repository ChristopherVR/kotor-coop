using System.Runtime.InteropServices;

namespace KotorCoop.Plugin;

/// <summary>
/// Locates creature objects from inside the game process.
///
/// CreateObject hands back an object id, not a pointer, and resolving an id properly means
/// walking CGameObjectArray's bucket chains. Scanning for the creature vtable is simpler,
/// needs no knowledge of the hash table's bucket count, and is already the technique the
/// external agent uses. In-process these are ordinary pointer reads, not ReadProcessMemory.
/// </summary>
internal static unsafe partial class CreatureFinder
{
    /// <summary>CSWSCreature vtable. Every 4-byte occurrence of this value is a creature.</summary>
    private const nint CreatureVtable = 0x007470C8;

    private const int ObjectIdOffset = 0x04;

    private const uint MemCommit = 0x1000;
    private const uint MemPrivate = 0x20000;
    private const uint PageGuard = 0x100;
    private const uint PageNoAccess = 0x01;
    private const uint ReadableMask = 0x02 | 0x04 | 0x08 | 0x20 | 0x40 | 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nint BaseAddress;
        public nint AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nuint VirtualQuery(nint address, out MemoryBasicInformation buffer, nuint length);

    /// <summary>
    /// Finds the creature whose object id matches. Returns 0 if not found.
    ///
    /// Called only right after a spawn, so a full sweep is acceptable; it is not on the
    /// per-frame path.
    /// </summary>
    public static nint FindByObjectId(uint objectId) => Sweep(objectId, default);

    /// <summary>
    /// Finds a creature by TAG rather than object id — what dialogue following needs, since
    /// object ids are assigned per module load and the peer's copy of the same NPC can hold
    /// a different one, while the tag comes from the module's own data and matches.
    ///
    /// Same full sweep as <see cref="FindByObjectId"/>, and used the same way: once, on the
    /// edge where a conversation starts, never per frame.
    /// </summary>
    public static nint FindByTag(ReadOnlySpan<byte> tag) => tag.IsEmpty ? 0 : Sweep(0, tag);

    private static nint Sweep(uint objectId, ReadOnlySpan<byte> tag)
    {
        nuint structSize = (nuint)Marshal.SizeOf<MemoryBasicInformation>();

        // Start at the first mappable page: the null page is never committed, and starting
        // at 0 would fail the loop's lower bound immediately.
        nint address = FirstScanAddress;

        // A 32-bit process without /LARGEADDRESSAWARE only uses the low 2 GB.
        while (address is > 0 and < 0x7FFF_0000)
        {
            if (VirtualQuery(address, out MemoryBasicInformation info, structSize) == 0)
            {
                break;
            }

            long size = (long)info.RegionSize;
            if (size <= 0)
            {
                break;
            }

            bool usable = info.State == MemCommit
                && info.Type == MemPrivate
                && (info.Protect & (PageGuard | PageNoAccess)) == 0
                && (info.Protect & ReadableMask) != 0;

            if (usable && size <= 64L * 1024 * 1024)
            {
                nint found = ScanRegion(info.BaseAddress, size, objectId, tag);
                if (found != 0)
                {
                    return found;
                }
            }

            nint next = info.BaseAddress + (nint)size;
            if (next <= address)
            {
                break;
            }

            address = next;
        }

        return 0;
    }

    /// <summary>CSWSObject + this = the object's tag (CExoString: char* @0, length @4).</summary>
    private const int TagOffset = 0x18;

    private static nint ScanRegion(nint start, long size, uint objectId, ReadOnlySpan<byte> tag)
    {
        // Creature pointers are 4-byte aligned, and so is the vtable slot at offset 0.
        for (long offset = 0; offset + 4 <= size; offset += 4)
        {
            nint candidate = start + (nint)offset;

            if (*(nint*)candidate != CreatureVtable)
            {
                continue;
            }

            if (tag.IsEmpty)
            {
                if (*(uint*)(candidate + ObjectIdOffset) == objectId)
                {
                    return candidate;
                }

                continue;
            }

            if (TagMatches(candidate, tag))
            {
                return candidate;
            }
        }

        return 0;
    }

    /// <summary>Case-insensitive tag compare. Odyssey tags are authored in mixed case and
    /// compared case-insensitively everywhere else in the engine.</summary>
    private static bool TagMatches(nint creature, ReadOnlySpan<byte> tag)
    {
        nint chars = *(nint*)(creature + TagOffset);
        int length = *(int*)(creature + TagOffset + 4);

        // The stored length can include the terminating null (the CExoString trap).
        if (length > 0 && *(byte*)(chars + length - 1) == 0)
        {
            length--;
        }

        if (chars is <= 0x10000 or >= 0x7FFF_0000 || length != tag.Length)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            if (Lower(*(byte*)(chars + i)) != Lower(tag[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static byte Lower(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;

    /// <summary>Starting address for the sweep. Page 0 is never mapped.</summary>
    public static nint FirstScanAddress => 0x10000;
}

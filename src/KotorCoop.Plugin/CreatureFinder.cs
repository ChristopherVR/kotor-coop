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

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(
        nint process, nint address, byte* buffer, nuint size, out nuint read);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    /// <summary>
    /// Copies memory out of THIS process without ever faulting: an unreadable address makes
    /// the call return false where a direct dereference would raise an access violation.
    ///
    /// ⚠ This is not belt-and-braces, it is the fix for a real crash. VirtualQuery says a
    /// region was committed and readable a moment ago; the game's own loader and audio
    /// threads free and re-protect regions continuously, and during a module load — the
    /// busiest allocation window there is — one of them went away underneath the scan. The
    /// AV landed in managed code, where NativeAOT cannot throw and calls FailFast, so the
    /// whole game died with "Attempted to read or write protected memory". Copying first
    /// turns that into a skipped region.
    /// </summary>
    internal static bool TryReadSelf(nint address, byte* destination, int length)
    {
        if (address <= 0 || length <= 0)
        {
            return false;
        }

        return ReadProcessMemory(GetCurrentProcess(), address, destination, (nuint)length, out nuint read)
            && read == (nuint)length;
    }

    /// <summary>How much of a region is copied at a time. Big enough that the syscall cost
    /// disappears against the memcpy, small enough to stay off the large-object heap.</summary>
    private const int ChunkBytes = 256 * 1024;

    /// <summary>A candidate needs its vtable and the object id that follows it.</summary>
    private const int CandidateBytes = 8;

    /// <summary>Reused across calls: this only ever runs on the game thread.</summary>
    private static readonly byte[] Chunk = new byte[ChunkBytes];

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

    /// <summary>CSWSDoor vtable. Same technique, different class — see
    /// <see cref="CollectByVtable"/> for why doors need collecting rather than finding.</summary>
    public const nint DoorVtable = 0x007498B0;

    /// <summary>Finds a DOOR by tag, for the door-use staging verb.</summary>
    public static nint FindDoorByTag(ReadOnlySpan<byte> tag) =>
        tag.IsEmpty ? 0 : Sweep(0, tag, DoorVtable);

    /// <summary>CSWSPlaceable vtable (docs/research/placeables-containers.md) — the same value
    /// <c>Odyssey.ResolvePlaceable</c> vtable-checks against. Redeclared here rather than
    /// exposed from Odyssey.cs so this file's sweep helpers stay self-contained, the same way
    /// <see cref="DoorVtable"/> already is for doors.</summary>
    public const nint PlaceableVtable = 0x007494D0;

    /// <summary>Finds a PLACEABLE by tag, for the container-use staging verb. Like
    /// <see cref="FindDoorByTag"/>, this stops at the first match — the base game routinely
    /// places several placeables with the same tag, so "by tag" only ever means "one of them",
    /// exactly as fuzzy as the door verb's own tag lookup.</summary>
    public static nint FindPlaceableByTag(ReadOnlySpan<byte> tag) =>
        tag.IsEmpty ? 0 : Sweep(0, tag, PlaceableVtable);

    /// <summary>CSWSStore vtable (docs/research/placeables-containers.md §4: "CSWSStore vtable
    /// 0x0074BDB8 (size 0x258)"). Redeclared here rather than exposed from Odyssey.Store.cs, the
    /// same "self-contained sweep helpers" convention <see cref="DoorVtable"/>/
    /// <see cref="PlaceableVtable"/> already follow.</summary>
    public const nint StoreVtable = 0x0074BDB8;

    /// <summary>Finds a STORE by tag, for the <c>openstore</c> live-test verb — the
    /// human-typed form; the automatic apply half always has an object id already (§2.3/§2.6,
    /// docs/research/store-sync.md). Like <see cref="FindDoorByTag"/>/<see cref="FindPlaceableByTag"/>,
    /// stops at the first match.</summary>
    public static nint FindStoreByTag(ReadOnlySpan<byte> tag) =>
        tag.IsEmpty ? 0 : Sweep(0, tag, StoreVtable);

    /// <summary>
    /// Copies every object with <paramref name="vtable"/> into <paramref name="into"/> and
    /// returns how many were written (never more than <paramref name="capacity"/>).
    ///
    /// The find-style sweeps above stop at the first match, which is right when the caller
    /// already knows what it is looking for. "The nearest transition door" is the other shape:
    /// it cannot be answered until every door has been seen, because the nearest one is only
    /// nearest relative to all the others. One full pass, on a debug verb, once — never on the
    /// per-frame path.
    /// </summary>
    public static int CollectByVtable(nint vtable, nint* into, int capacity)
    {
        if (into == null || capacity <= 0)
        {
            return 0;
        }

        _collected = into;
        _collectCapacity = capacity;
        _collectCount = 0;

        Sweep(0, default, vtable, collect: true);

        int count = _collectCount;
        _collected = null;
        _collectCapacity = 0;
        _collectCount = 0;
        return count;
    }

    // Collection state. Statics rather than a closure because the scan runs inside a `fixed`
    // block in a method that cannot capture, and because — like everything else here — it only
    // ever runs on the game's own thread, one call at a time.
    private static nint* _collected;
    private static int _collectCapacity;
    private static int _collectCount;

    private static nint Sweep(uint objectId, ReadOnlySpan<byte> tag) =>
        Sweep(objectId, tag, CreatureVtable);

    private static nint Sweep(uint objectId, ReadOnlySpan<byte> tag, nint vtable, bool collect = false)
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
                nint found = ScanRegion(info.BaseAddress, size, objectId, tag, vtable, collect);
                if (found != 0 && !collect)
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

    private static nint ScanRegion(
        nint start, long size, uint objectId, ReadOnlySpan<byte> tag, nint vtable, bool collect)
    {
        fixed (byte* buffer = Chunk)
        {
            long position = 0;

            while (position + CandidateBytes <= size)
            {
                int want = (int)Math.Min(ChunkBytes, size - position);

                if (!TryReadSelf(start + (nint)position, buffer, want))
                {
                    // The region moved or went away between the query and now. Skipping it
                    // can at worst miss a creature, and the caller already treats "not
                    // found" as a normal answer; faulting would end the process.
                    return 0;
                }

                // Creature pointers are 4-byte aligned, and so is the vtable slot at 0.
                for (int offset = 0; offset + CandidateBytes <= want; offset += 4)
                {
                    if (*(nint*)(buffer + offset) != vtable)
                    {
                        continue;
                    }

                    nint candidate = start + (nint)position + offset;

                    if (collect)
                    {
                        if (_collectCount < _collectCapacity)
                        {
                            _collected[_collectCount++] = candidate;
                        }

                        continue;
                    }

                    if (tag.IsEmpty)
                    {
                        if (*(uint*)(buffer + offset + ObjectIdOffset) == objectId)
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

                if (want < ChunkBytes)
                {
                    break;
                }

                // Overlap by one candidate so a match straddling the chunk boundary — whose
                // object id lives in the next chunk — is still seen whole.
                position += want - (CandidateBytes - 4);
            }
        }

        return 0;
    }

    /// <summary>Case-insensitive tag compare. Odyssey tags are authored in mixed case and
    /// compared case-insensitively everywhere else in the engine.</summary>
    /// <summary>Longest tag worth comparing; Odyssey tags are resref-sized.</summary>
    private const int MaxTagBytes = 64;

    private static bool TagMatches(nint creature, ReadOnlySpan<byte> tag)
    {
        // ⚠ The pointer is validated BEFORE it is followed. The previous version read
        // *(chars + length - 1) to strip the CExoString trailing null and only sanity-checked
        // `chars` on the line AFTER — so a stale or garbage string header was dereferenced
        // first, which is the same class of fault the region copy above exists to stop.
        byte* header = stackalloc byte[8];
        if (!TryReadSelf(creature + TagOffset, header, 8))
        {
            return false;
        }

        nint chars = (nint)(*(uint*)header);
        int length = *(int*)(header + 4);

        if (chars is <= 0x10000 or >= 0x7FFF_0000 || length is <= 0 or > MaxTagBytes)
        {
            return false;
        }

        byte* text = stackalloc byte[MaxTagBytes];
        if (!TryReadSelf(chars, text, length))
        {
            return false;
        }

        // The stored length can include the terminating null (the CExoString trap).
        if (text[length - 1] == 0)
        {
            length--;
        }

        if (length != tag.Length)
        {
            return false;
        }

        for (int i = 0; i < length; i++)
        {
            if (Lower(text[i]) != Lower(tag[i]))
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

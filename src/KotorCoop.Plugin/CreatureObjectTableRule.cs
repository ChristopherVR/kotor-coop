namespace KotorCoop.Plugin;

/// <summary>
/// Pure bounds and id arithmetic for the server <c>CGameObjectArray</c> walk.
///
/// The table is the same 8192-bucket chained hash table used by
/// <see cref="Odyssey.ResolveServerObject"/>.  Keeping the arithmetic and the finite walk
/// bound here gives the scaling path a small, testable contract without putting engine
/// pointers into the ordinary test assembly.
/// </summary>
internal static class CreatureObjectTableRule
{
    /// <summary>The object array has two 4096-bucket id spaces.</summary>
    public const int BucketCount = 8192;

    /// <summary>Maximum entries followed in one bucket, matching the engine lookup.</summary>
    public const int MaxChainWalk = 64;

    /// <summary>Entry fields are the x86 layout used by GetGameObject.</summary>
    public const int EntryObjectId = 0x00;
    public const int EntryObject = 0x04;
    public const int EntryNext = 0x08;

    /// <summary>Reconstructs the full object id from a bucket and its stored masked key.</summary>
    public static uint FullObjectId(int bucket, uint storedKey)
    {
        uint highBit = ((uint)bucket >> 12 & 1u) << 31;
        return (storedKey & 0x7FFF_FFFFu) | highBit;
    }

    /// <summary>Stops a table walk before a torn or cyclic chain can spin.</summary>
    public static bool MayWalk(int walked, nint entry) => entry != 0 && walked < MaxChainWalk;

    /// <summary>Capacity guard shared by every table-backed collection.</summary>
    public static bool HasRoom(int count, int capacity) => count >= 0 && count < capacity;
}

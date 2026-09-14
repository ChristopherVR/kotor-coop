namespace KotorCoop.Plugin;

/// <summary>
/// Validates the complete x86 CSWSJournal layout before native updates can traverse it.
/// All addresses come from the game; pointer fields stay four bytes even in the test host.
/// </summary>
internal static unsafe class JournalMemoryGuard
{
    internal const int JournalEntryStride = 0x3C;
    internal const int JournalEntryTag = 0x18;

    public static bool TryValidate(nint journal, out nint data, out int size, out int failure)
    {
        data = 0;
        size = 0;
        failure = 4;
        if (!NativeMethods.IsWritable(journal, 12))
        {
            return false;
        }

        data = (nint)(*(uint*)journal);
        size = *(int*)(journal + 4);
        int capacity = *(int*)(journal + 8);
        if (size < 0 || capacity < size || capacity > 4096
            || (capacity > 0 && !NativeMethods.IsWritable(data, (nuint)(capacity * JournalEntryStride))))
        {
            return false;
        }

        for (int i = 0; i < size; i++)
        {
            nint entry = data + i * JournalEntryStride;
            nint tag = (nint)(*(uint*)(entry + JournalEntryTag));
            int length = (int)Math.Min(*(uint*)(entry + JournalEntryTag + 4), 64u);
            if (!NativeMethods.IsReadableTerminatedString(tag, length))
            {
                failure = 5;
                return false;
            }
        }

        failure = 0;
        return true;
    }
}

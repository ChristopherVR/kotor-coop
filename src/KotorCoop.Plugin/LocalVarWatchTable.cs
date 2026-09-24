using KotorCoop.Protocol;

namespace KotorCoop.Plugin;

/// <summary>
/// The plugin's compiled form of <see cref="LocalVarSyncRule.Allowlist"/> plus any session-only
/// watches the <c>localvar watch</c> live-test verb added. Laid out as flat arrays so the
/// capture hook can match a write without allocating: an (kind, slot) interest bitmap rejects
/// the overwhelming majority of engine writes (the generic AI's own flags, slot 20 and up)
/// before a module name or tag is ever read.
///
/// Main thread only: the hook runs inside the VM on the game thread, and the verb and the
/// controller run from the frame hook on the same thread.
/// </summary>
internal static unsafe class LocalVarWatchTable
{
    public const int MaxRuntimeWatches = 8;
    public const int TagCapacity = 32;

    private static readonly int Capacity = LocalVarSyncRule.Allowlist.Count + MaxRuntimeWatches;
    private static readonly uint[] ModuleHash = new uint[Capacity];
    private static readonly byte[] Tags = new byte[Capacity * TagCapacity];
    private static readonly int[] TagLength = new int[Capacity];
    private static readonly byte[] Kind = new byte[Capacity];
    private static readonly byte[] Slot = new byte[Capacity];
    private static readonly ushort[] ExpectedId = new ushort[Capacity];
    private static readonly uint[] LastCaptureCursor = new uint[Capacity];
    private static readonly bool[] Interest = new bool[2 * LocalVarSyncRule.BooleanSlots];

    private static int _count;
    private static int _allowlisted;
    private static bool _built;

    public static int AllowlistedCount => _allowlisted;

    public static int Count => _count;

    /// <summary>Compiles the static allowlist once. Entries whose tag does not fit are skipped
    /// (none today; a tag is a 32-byte resref-like field).</summary>
    public static void Build()
    {
        if (_built)
        {
            return;
        }

        _built = true;
        byte* tag = stackalloc byte[TagCapacity];
        foreach (LocalVarAllowEntry entry in LocalVarSyncRule.Allowlist)
        {
            if (entry.Tag.Length is 0 or >= TagCapacity || !LocalVarSyncRule.IsValidSlot(entry.Kind, entry.Slot))
            {
                continue;
            }

            for (int i = 0; i < entry.Tag.Length; i++)
            {
                tag[i] = (byte)entry.Tag[i];
            }

            Add(LocalVarSyncRule.HashModule(entry.Module), tag, entry.Tag.Length, entry.Kind, entry.Slot, entry.ObjectId);
        }

        _allowlisted = _count;
    }

    /// <summary>Adds a session-only watch (the <c>localvar watch</c> verb). Returns its index,
    /// the existing index when it is already watched, or -1 when the table is full.</summary>
    public static int AddRuntimeWatch(uint moduleHash, byte* tag, int tagLength, LocalVarKind kind, byte slot, ushort objectId)
    {
        int existing = Find(moduleHash, tag, tagLength, kind, slot);
        if (existing >= 0)
        {
            return existing;
        }

        return _count - _allowlisted >= MaxRuntimeWatches ? -1 : Add(moduleHash, tag, tagLength, kind, slot, objectId);
    }

    private static int Add(uint moduleHash, byte* tag, int tagLength, LocalVarKind kind, byte slot, ushort objectId)
    {
        if (_count >= Capacity || tagLength is <= 0 or >= TagCapacity)
        {
            return -1;
        }

        int index = _count;
        ModuleHash[index] = moduleHash;
        for (int i = 0; i < tagLength; i++)
        {
            Tags[(index * TagCapacity) + i] = Lower(tag[i]);
        }

        TagLength[index] = tagLength;
        Kind[index] = (byte)kind;
        Slot[index] = slot;
        ExpectedId[index] = objectId;
        LastCaptureCursor[index] = 0;
        Interest[InterestIndex(kind, slot)] = true;
        _count++;
        return index;
    }

    /// <summary>The cheap first gate: is ANY watch interested in this (kind, slot)?</summary>
    public static bool IsInteresting(LocalVarKind kind, int slot) =>
        LocalVarSyncRule.IsValidSlot(kind, slot) && Interest[InterestIndex(kind, slot)];

    /// <summary>The watch index matching (module, tag, kind, slot), or -1. Tag compare is
    /// case-insensitive, like the engine's own tag lookups.</summary>
    public static int Find(uint moduleHash, byte* tag, int tagLength, LocalVarKind kind, int slot)
    {
        for (int i = 0; i < _count; i++)
        {
            if (ModuleHash[i] != moduleHash || Kind[i] != (byte)kind || Slot[i] != slot || TagLength[i] != tagLength)
            {
                continue;
            }

            bool same = true;
            for (int c = 0; c < tagLength; c++)
            {
                if (Tags[(i * TagCapacity) + c] != Lower(tag[c]))
                {
                    same = false;
                    break;
                }
            }

            if (same)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The watch index for (module, kind, slot) regardless of tag — the apply path
    /// looks up by key first, then checks the live object's tag against <see cref="TagEquals"/>.</summary>
    public static int FindByKey(uint moduleHash, LocalVarKind kind, int slot, int start)
    {
        for (int i = Math.Max(start, 0); i < _count; i++)
        {
            if (ModuleHash[i] == moduleHash && Kind[i] == (byte)kind && Slot[i] == slot)
            {
                return i;
            }
        }

        return -1;
    }

    public static bool TagEquals(int index, byte* tag, int tagLength)
    {
        if (index < 0 || index >= _count || TagLength[index] != tagLength)
        {
            return false;
        }

        for (int c = 0; c < tagLength; c++)
        {
            if (Tags[(index * TagCapacity) + c] != Lower(tag[c]))
            {
                return false;
            }
        }

        return true;
    }

    public static string TagOf(int index) =>
        index < 0 || index >= _count
            ? string.Empty
            : System.Text.Encoding.ASCII.GetString(Tags, index * TagCapacity, TagLength[index]);

    public static ushort ExpectedIdOf(int index) => index < 0 || index >= _count ? (ushort)0 : ExpectedId[index];

    public static bool IsRuntime(int index) => index >= _allowlisted;

    public static void NoteCapture(int index, uint cursor)
    {
        if (index >= 0 && index < _count)
        {
            LastCaptureCursor[index] = cursor;
        }
    }

    /// <summary>The capture cursor of this watch's most recent local write (0 = none since
    /// the plugin started).</summary>
    public static uint LastCaptureOf(int index) => index < 0 || index >= _count ? 0 : LastCaptureCursor[index];

    private static int InterestIndex(LocalVarKind kind, int slot) =>
        ((int)kind * LocalVarSyncRule.BooleanSlots) + slot;

    private static byte Lower(byte b) => (uint)(b - 'A') <= 'Z' - 'A' ? (byte)(b + 0x20) : b;
}

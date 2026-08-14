namespace KotorCoop.Plugin;

/// <summary>
/// Reads the names out of the engine's OWN console command table, so Tab completion offers
/// the retail cheats (<c>giveitem</c>, <c>revealmap</c>, <c>addexp</c>, the <c>set*</c> stat
/// verbs...) beside the co-op ones.
///
/// This is the difference between "our console" and "the console": the mod does not replace
/// the developer console, it borrows it, and a completion list that knew only about co-op
/// verbs would quietly imply that the ~60 verbs the game itself registers do not exist. It
/// also picks up anything a MOD registers, for free, because the source is the live table
/// rather than a list we wrote down.
///
/// STRICTLY READ-ONLY, and paranoid about it. The table is engine data we do not own: every
/// pointer is range-checked, every name is copied through the non-faulting read
/// <see cref="CreatureFinder.TryReadSelf"/> (an unreadable address returns false where a
/// direct dereference would raise an access violation NativeAOT cannot catch), and anything
/// that does not look like a verb is skipped rather than trusted. The worst outcome of a
/// wrong guess here is a missing or odd-looking completion candidate.
///
/// Layout is from docs/research/console-register.md, which read it out of this binary: the
/// table is a flat <c>void*[]</c> of entry pointers with the count beside it, and an entry
/// carries its name INLINE at offset 0 (verified — the register helper at 0x0044C560 copies
/// the string to <c>this+0</c> and every lookup compares bytes at <c>[entry]</c>).
/// </summary>
internal static unsafe class ConsoleCommandTable
{
    /// <summary>The flat array of entry pointers, sorted by name by ConsoleFuncs_Add.</summary>
    private const nint TablePtr = 0x007A8AB8;

    /// <summary>How many slots of that array are populated.</summary>
    private const nint CountPtr = 0x007B8FB8;

    /// <summary>
    /// Sanity ceiling on the count. The base game registers well under a hundred; a table
    /// claiming thousands means we are reading the wrong dword, and walking it would turn a
    /// completion list into a scan of unrelated memory.
    /// </summary>
    private const int MaxEntries = 512;

    /// <summary>Longest console verb worth believing in. The retail names top out at 15
    /// (<c>restartminigame</c>, <c>dancedancemalak</c>); anything much longer is not a verb.</summary>
    private const int MaxNameBytes = 31;

    private static string[]? _cached;

    /// <summary>What the last read actually saw, for the log — "62 verbs", or the reason
    /// there are none. A guard that fails safe still has to say so.</summary>
    public static string Diagnostic { get; private set; } = "not read yet";

    /// <summary>
    /// The engine's verbs, read once and cached.
    ///
    /// Once is enough and once is also required: the table is built by static initialisers
    /// at load and never grows during play, while re-walking it on every keystroke would put
    /// a pointer chase through foreign memory on the typing path. A caller that wants a
    /// re-read (a mod registering late) calls <see cref="Forget"/>.
    /// </summary>
    public static string[] Verbs()
    {
        if (_cached is { } cached)
        {
            return cached;
        }

        _cached = Read();
        return _cached;
    }

    /// <summary>Drops the cache so the next <see cref="Verbs"/> re-reads the table.</summary>
    public static void Forget() => _cached = null;

    private static string[] Read()
    {
        int count;
        if (!TryReadInt(CountPtr, out count) || count is <= 0 or > MaxEntries)
        {
            Diagnostic = $"console table count reads {count} - not walking it";
            return [];
        }

        List<string> names = new(count);
        int skipped = 0;

        for (int i = 0; i < count; i++)
        {
            if (!TryReadInt(TablePtr + (i * 4), out int raw))
            {
                skipped++;
                continue;
            }

            nint entry = raw;
            if (entry is <= 0x10000 or >= 0x7FFF_0000)
            {
                skipped++;
                continue;
            }

            if (TryReadName(entry, out string name))
            {
                names.Add(name);
            }
            else
            {
                skipped++;
            }
        }

        Diagnostic = skipped == 0
            ? $"{names.Count} engine console verbs"
            : $"{names.Count} engine console verbs ({skipped} slots skipped as unreadable)";

        return [.. names];
    }

    private static bool TryReadInt(nint address, out int value)
    {
        int local = 0;
        bool ok = CreatureFinder.TryReadSelf(address, (byte*)&local, 4);
        value = ok ? local : 0;
        return ok;
    }

    /// <summary>
    /// Copies the inline name at <paramref name="entry"/>, accepting only a lower-case
    /// verb-shaped token. The filter is what keeps a misread slot out of the candidate list:
    /// a struct that is not a command entry will almost never begin with printable ASCII
    /// terminated inside 31 bytes, and if it somehow does, the worst case is one nonsense
    /// completion candidate rather than anything the engine acts on.
    /// </summary>
    private static bool TryReadName(nint entry, out string name)
    {
        name = string.Empty;

        byte* raw = stackalloc byte[MaxNameBytes + 1];
        if (!CreatureFinder.TryReadSelf(entry, raw, MaxNameBytes + 1))
        {
            return false;
        }

        int length = 0;
        while (length <= MaxNameBytes && raw[length] != 0)
        {
            length++;
        }

        if (length is 0 or > MaxNameBytes)
        {
            return false;
        }

        Span<char> chars = stackalloc char[MaxNameBytes];
        for (int i = 0; i < length; i++)
        {
            byte b = raw[i];
            if (b is < (byte)'a' or > (byte)'z')
            {
                // Retail verbs are all lower-case ASCII letters and digits; the completion
                // engine lower-cases the typed prefix, so a mixed-case name could never be
                // matched anyway.
                if (b is < (byte)'0' or > (byte)'9')
                {
                    return false;
                }
            }

            chars[i] = (char)b;
        }

        name = new string(chars[..length]);
        return true;
    }
}

using System.Buffers.Binary;

namespace KotorCoop.Plugin;

/// <summary>
/// Pure decode of the record <see cref="CrashProbe"/>'s machine code (<c>BuildHandler</c>)
/// fills in on the faulting thread and hands to <c>kernel32!WriteFile</c>, which persists it
/// to <c>kotorcoop-crash.bin</c> next to the plugin log — the copy that survives a process
/// TERMINATED before the worker thread's next poll ever turns the in-memory record into a
/// "FIRST-CHANCE ..." log line (<see cref="CrashProbe.DrainReports"/>). See CrashProbe's
/// top-of-file doc comment for why that gap exists and mattered (2026-09-02,
/// docs/research/transition-crashes.md).
///
/// Kept separate from CrashProbe — which is <c>unsafe</c>, interop-heavy, and only ever runs
/// inside a live swkotor.exe process — so this one pure byte-layout function can be exercised
/// by a plain unit test with no game, no process, nothing but a byte array. Split out the same
/// way <see cref="LogRotation"/> keeps rotation's pure arithmetic out of PluginHost.
///
/// <c>tools\crashbin.ps1</c> is the player-facing decoder and deliberately does NOT call this
/// class (a PowerShell script cannot); it re-implements the same field layout independently,
/// the same relationship <c>tools\checkstub.py</c> has to <see cref="CrashProbe.BuildHandler"/>
/// — two independent readings of one format are how a layout mistake in either gets caught.
/// </summary>
public static class CrashRecord
{
    /// <summary>Byte offset of the 64-dword raw stack slice — everything before it is the
    /// five-dword header. Mirrors CrashProbe's private RecStack.</summary>
    public const int HeaderSize = 0x18;

    /// <summary>Mirrors CrashProbe's private StackWords: the stack slice's capacity, not
    /// necessarily how many words a given record actually populated (see <see cref="Report"/>'s
    /// Stack, which is only ever as long as the record's own stackWords field, clamped to this).</summary>
    public const int StackWords = 64;

    /// <summary>Total record size in bytes — also the WriteFile call's fixed
    /// nNumberOfBytesToWrite, so this is exactly how far one report extends into the .bin
    /// before the next one (if the file ever holds more than one; see <see cref="RecordCount"/>).</summary>
    public const int Size = HeaderSize + (StackWords * 4);

    /// <summary>STATUS_ACCESS_VIOLATION — mirrors CrashProbe's private constant of the same
    /// name; duplicated rather than shared because CrashProbe's is private to keep that file's
    /// unsafe surface self-contained.</summary>
    public const uint AccessViolation = 0xC0000005;

    /// <summary>STATUS_FAIL_FAST_EXCEPTION — a .NET abort, if a handler ever sees one.</summary>
    public const uint FailFast = 0xC0000602;

    public const uint StackOverflow = 0xC00000FD;

    /// <summary>One decoded report, raw fields only — no interpretation. Naming the exception
    /// code, filtering the stack to image-range candidates and tagging "OURS" against a known
    /// plugin image range are the caller's job (<see cref="ExceptionName"/> covers the first;
    /// CrashProbe.DrainReports and tools\crashbin.ps1 each do the rest independently, since one
    /// needs the plugin's own resolved image range and the other reads it back out of the
    /// plugin log).</summary>
    public readonly record struct Report(uint Serial, uint Code, uint Address, uint Eip, uint Esp, uint[] Stack);

    /// <summary>
    /// Decodes one <see cref="Size"/>-byte record starting at the beginning of
    /// <paramref name="bytes"/>. Returns null rather than throwing when there is not enough
    /// data — a truncated read (the file caught mid-write, or a stale zero-length .bin from a
    /// session that never faulted) is "nothing to show", not an error the caller must catch.
    /// </summary>
    public static Report? Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size)
        {
            return null;
        }

        uint serial = ReadU32(bytes, 0x00);
        uint code = ReadU32(bytes, 0x04);
        uint address = ReadU32(bytes, 0x08);
        uint eip = ReadU32(bytes, 0x0C);
        uint esp = ReadU32(bytes, 0x10);
        uint words = ReadU32(bytes, 0x14);

        // Clamped the same way DrainReports clamps it: a corrupt or torn write must not
        // read past the header's own 64-word capacity.
        int count = (int)Math.Min(words, StackWords);

        uint[] stack = new uint[count];
        for (int i = 0; i < count; i++)
        {
            stack[i] = ReadU32(bytes, HeaderSize + (i * 4));
        }

        return new Report(serial, code, address, eip, esp, stack);
    }

    /// <summary>How many whole <see cref="Size"/>-byte records fit in a file this long.
    /// CrashProbe's stub always resets to offset 0 before writing (see its doc comment), so in
    /// practice this is 0 or 1 today — a fixed-size "last report" file — but the decoder does
    /// not assume that stays true, in case a future writer appends generations instead.</summary>
    public static int RecordCount(long fileLength) => (int)(fileLength / Size);

    /// <summary>The same three names CrashProbe.DrainReports gives these codes in the
    /// "FIRST-CHANCE ..." log line, so a report read from the .bin reads the same way whether
    /// it came from the log or the file.</summary>
    public static string ExceptionName(uint code) => code switch
    {
        AccessViolation => "access violation",
        FailFast => "FAIL-FAST (0xC0000602)",
        StackOverflow => "stack overflow",
        _ => "exception",
    };

    private static uint ReadU32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, 4));
}

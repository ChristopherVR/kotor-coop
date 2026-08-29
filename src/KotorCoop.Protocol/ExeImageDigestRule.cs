namespace KotorCoop.Protocol;

/// <summary>
/// Fingerprints the LOADED executable image — the code the mod is about to hook — rather
/// than the file it came from.
///
/// WHY THIS EXISTS. Steam ships KOTOR 1.0.3 wrapped in Valve's SteamStub v3, which encrypts
/// only <c>.text</c> on disk and decrypts it in place before the game's own entry point runs.
/// The wrapped and unwrapped files are the same link — identical section table, identical
/// <c>ImageBase</c> (0x00400000, no <c>.reloc</c>, so the image cannot be rebased), identical
/// PE timestamp, byte-identical <c>.rdata</c>/<c>.data</c>/<c>.rsrc</c> — and differ on disk
/// in exactly two places: the <c>.text</c> body, and the four-byte
/// <c>AddressOfEntryPoint</c>. So a FILE hash says "different build" about two installs whose
/// running code is byte-for-byte the same, and <c>ContentCompatibility</c> refuses the
/// session on <c>RefusalReason.Executable</c>. That refusal is the only thing that ever
/// blocked Steam↔DRM-free co-op. See docs/research/steam-support.md §§1-4 and §8.
///
/// The fix is not to weaken the check — it is to hash the right bytes. This rule hashes:
///
///   * the PE timestamp and <c>ImageBase</c>;
///   * every section header's name, virtual address and virtual size;
///   * the whole mapped <c>.text</c> body.
///
/// <c>AddressOfEntryPoint</c> is deliberately EXCLUDED, because it is the one header field
/// the wrapper changes (it points into <c>.bind</c> on the Steam copy) and it says nothing
/// about the code that actually runs. Everything else is included, so this is strictly
/// STRONGER than the file hash it replaces: it also catches a runtime patcher that never
/// touched the file.
///
/// ⚠ WHERE IT MUST BE COMPUTED. Inside the game process, BEFORE the mod installs a single
/// hook — <c>PluginHost.AllocateBlock</c>, which runs before <c>InstallHooks</c>. Our own
/// inline detours rewrite <c>.text</c>, and which ones get installed depends on coop.ini,
/// so a digest taken after hooking would differ between two peers whose CONFIG differs and
/// refuse a session that works perfectly. Taken at that one fixed point in startup, both
/// peers hash the same pristine, decrypted image. The plugin publishes the result at
/// <c>SharedBlock.OffsetExeImageDigest</c> and the agent carries it as
/// <c>ContentDigest.ExeDigest</c>.
///
/// Pure and total: every malformed shape returns false with a reason rather than throwing.
/// The input is a memory image, and this must never be the thing that takes the game down.
/// </summary>
public static class ExeImageDigestRule
{
    private const uint FnvOffset = 2166136261;
    private const uint FnvPrime = 16777619;

    /// <summary>Size of the PE header region every mapped image starts with. One page is
    /// always enough for a 5-section 32-bit PE and is what the loader maps first.</summary>
    public const int HeaderWindow = 0x1000;

    /// <summary>
    /// Locates <c>.text</c> in a MAPPED image (one addressed by RVA, i.e. what the loader
    /// produced, not the file) and hashes the header facts that identify the link.
    /// <paramref name="headers"/> need only cover the first <see cref="HeaderWindow"/> bytes.
    /// </summary>
    public static bool TryLocateText(
        ReadOnlySpan<byte> headers, out uint textRva, out uint textSize, out uint headerHash, out string diagnostic)
    {
        textRva = 0;
        textSize = 0;
        headerHash = 0;
        diagnostic = string.Empty;

        if (headers.Length < 0x40 || headers[0] != (byte)'M' || headers[1] != (byte)'Z')
        {
            diagnostic = "not a PE image (no MZ)";
            return false;
        }

        int peOffset = (int)ReadUInt32(headers, 0x3C);
        if (peOffset <= 0 || peOffset + 0x78 > headers.Length
            || headers[peOffset] != (byte)'P' || headers[peOffset + 1] != (byte)'E')
        {
            diagnostic = "not a PE image (no PE signature)";
            return false;
        }

        int sectionCount = ReadUInt16(headers, peOffset + 6);
        uint timeStamp = ReadUInt32(headers, peOffset + 8);
        int optionalHeaderSize = ReadUInt16(headers, peOffset + 20);
        int optional = peOffset + 24;
        uint imageBase = ReadUInt32(headers, optional + 28);

        int sectionTable = optional + optionalHeaderSize;
        if (sectionCount is < 1 or > 96 || sectionTable + (sectionCount * 40) > headers.Length)
        {
            diagnostic = "section table does not fit in the mapped header page";
            return false;
        }

        // The identity of the link, minus AddressOfEntryPoint (see the type comment).
        uint h = FnvUInt(FnvOffset, timeStamp);
        h = FnvUInt(h, imageBase);
        h = FnvUInt(h, (uint)sectionCount);

        for (int i = 0; i < sectionCount; i++)
        {
            int header = sectionTable + (i * 40);
            h = Fnv(h, headers.Slice(header, 8));                 // name, NUL-padded
            h = FnvUInt(h, ReadUInt32(headers, header + 8));      // VirtualSize
            h = FnvUInt(h, ReadUInt32(headers, header + 12));     // VirtualAddress

            if (IsTextSection(headers, header))
            {
                textRva = ReadUInt32(headers, header + 12);
                textSize = ReadUInt32(headers, header + 8);
            }
        }

        if (textRva == 0 || textSize == 0)
        {
            diagnostic = "no .text section with a non-zero virtual size";
            return false;
        }

        headerHash = h;
        return true;
    }

    /// <summary>Folds the mapped <c>.text</c> body into the header hash.</summary>
    public static uint HashText(uint headerHash, ReadOnlySpan<byte> text) => Fnv(headerHash, text);

    /// <summary>
    /// The whole computation for a caller that already holds the entire mapped image as one
    /// span (a test, or an in-process reader that mapped headers and body together).
    /// </summary>
    public static bool TryComputeFromMappedImage(ReadOnlySpan<byte> image, out uint digest, out string diagnostic)
    {
        digest = 0;

        int window = Math.Min(HeaderWindow, image.Length);
        if (!TryLocateText(image[..window], out uint textRva, out uint textSize, out uint headerHash, out diagnostic))
        {
            return false;
        }

        long end = (long)textRva + textSize;
        if (end > image.Length)
        {
            diagnostic = $".text [0x{textRva:X}, 0x{end:X}) runs past the {image.Length}-byte image";
            return false;
        }

        digest = HashText(headerHash, image.Slice((int)textRva, (int)textSize));
        return true;
    }

    private static bool IsTextSection(ReadOnlySpan<byte> headers, int header) =>
        headers[header] == (byte)'.' && headers[header + 1] == (byte)'t'
        && headers[header + 2] == (byte)'e' && headers[header + 3] == (byte)'x'
        && headers[header + 4] == (byte)'t' && headers[header + 5] == 0;

    private static uint Fnv(uint seed, ReadOnlySpan<byte> data)
    {
        uint h = seed;
        foreach (byte b in data)
        {
            h = (h ^ b) * FnvPrime;
        }

        return h;
    }

    private static uint FnvUInt(uint seed, uint value)
    {
        uint h = seed;
        for (int i = 0; i < 4; i++)
        {
            h = (h ^ (byte)(value >> (i * 8))) * FnvPrime;
        }

        return h;
    }

    private static uint ReadUInt32(ReadOnlySpan<byte> image, int offset) =>
        (uint)(image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24));

    private static int ReadUInt16(ReadOnlySpan<byte> image, int offset) =>
        image[offset] | (image[offset + 1] << 8);
}

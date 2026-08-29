namespace KotorCoop.Memory;

/// <summary>Which of the two forms a <c>swkotor.exe</c> is in. See <see cref="SteamBuild"/>.</summary>
public enum KotorExecutableForm
{
    /// <summary>Not recognisable as KOTOR 1.0.3 at all — unreadable, not a PE, or a different
    /// link. Deliberately distinct from both real answers: "we could not tell" must never be
    /// reported as "it is fine".</summary>
    Unknown,

    /// <summary>The DRM-free form: a plain PE whose entry point is in <c>.text</c>. This is the
    /// binary docs/research/engine-api.md was verified against and the one the rig runs.</summary>
    DrmFree,

    /// <summary>The Steam form: the same link wrapped in Valve's SteamStub v3 — <c>.text</c>
    /// encrypted on disk, entry point in the wrapper's <c>.bind</c> section. Decrypts in place
    /// at load, so every hardcoded address is the same VA at runtime.</summary>
    SteamStub,
}

/// <summary>
/// Tells the two shipped forms of <c>swkotor.exe</c> apart by reading its PE header, so the
/// mod can say which one an install is rather than guess. Read-only; never launches anything.
///
/// WHY THIS EXISTS, AND WHY IT IS NOT AN ADDRESS TABLE. The Steam and DRM-free binaries are
/// the same link: identical section table, identical <c>ImageBase</c> (0x00400000, and no
/// <c>.reloc</c>, so the image cannot be rebased), identical PE timestamp, identical import
/// table, and byte-identical <c>.rdata</c>/<c>.data</c>/<c>.rsrc</c>. The only bytes that
/// differ are the <c>.text</c> body — encrypted on disk in the Steam copy, decrypted in place
/// by the wrapper before the game's own entry point runs — and the four-byte
/// <c>AddressOfEntryPoint</c> field. So there is nothing to key an address table on: every
/// address in the symbol database is correct on both, at runtime. What is worth knowing is
/// which form you are looking at, because it explains three otherwise-baffling things:
///
///   * a signature scan of the file finds nothing on the Steam copy (there is nothing to
///     match — <c>.text</c> is ciphertext at rest);
///   * the co-op compatibility handshake refuses a Steam↔DRM-free session on
///     <c>ExeDigest</c>, because <c>InstallDigest</c> hashes the file rather than the loaded
///     image (docs/research/steam-support.md §8);
///   * a second local instance cannot be started from the Steam copy, because the wrapper
///     goes through the Steam client.
///
/// See docs/research/steam-support.md for the measurements this recognition is built on.
/// </summary>
public static class SteamBuild
{
    /// <summary>KOTOR 1.0.3's <c>TimeDateStamp</c>, the same in both forms: 2004-02-12
    /// 18:15:53 UTC. Both files carry it because they are the same link.</summary>
    public const uint Kotor103TimeStamp = 0x402BC2D9;

    /// <summary>The fixed load address. There is no <c>.reloc</c> section, so this is not a
    /// preference — the image cannot load anywhere else, which is what makes every absolute
    /// address in this project meaningful in the first place.</summary>
    public const uint ExpectedImageBase = 0x00400000;

    /// <summary>Valve's SteamStub marker. It sits in the four bytes immediately before
    /// <c>AddressOfEntryPoint</c>, preceded by a <c>jmp +4</c> that hops over it onto the
    /// entry point. It occurs exactly once in the whole 4.4 MB image.</summary>
    public const uint SteamStubMarker = 0xC0DEC0DE;

    /// <summary>The wrapper's section name.</summary>
    public const string StubSectionName = ".bind";

    /// <summary>
    /// Classifies an executable on disk. Never throws: an unreadable or absent file is
    /// <see cref="KotorExecutableForm.Unknown"/> with the reason in
    /// <paramref name="diagnostic"/>.
    /// </summary>
    public static KotorExecutableForm InspectFile(string path, out string diagnostic)
    {
        try
        {
            if (!File.Exists(path))
            {
                diagnostic = $"no file at {path}";
                return KotorExecutableForm.Unknown;
            }

            return Inspect(File.ReadAllBytes(path), out diagnostic);
        }
        catch (Exception ex)
        {
            diagnostic = $"could not read {path}: {ex.Message}";
            return KotorExecutableForm.Unknown;
        }
    }

    /// <summary>
    /// Classifies an image already in memory. Pure and total — every malformed shape returns
    /// <see cref="KotorExecutableForm.Unknown"/> with a reason rather than throwing, because
    /// the input is a file some player chose and this must never be the thing that takes the
    /// mod down.
    /// </summary>
    public static KotorExecutableForm Inspect(ReadOnlySpan<byte> image, out string diagnostic)
    {
        diagnostic = string.Empty;

        if (image.Length < 0x40 || image[0] != (byte)'M' || image[1] != (byte)'Z')
        {
            diagnostic = "not a PE image (no MZ)";
            return KotorExecutableForm.Unknown;
        }

        int peOffset = ReadInt32(image, 0x3C);
        if (peOffset <= 0 || peOffset + 0x78 > image.Length
            || image[peOffset] != (byte)'P' || image[peOffset + 1] != (byte)'E')
        {
            diagnostic = "not a PE image (no PE signature)";
            return KotorExecutableForm.Unknown;
        }

        int sectionCount = ReadUInt16(image, peOffset + 6);
        uint timeStamp = ReadUInt32(image, peOffset + 8);
        int optionalHeaderSize = ReadUInt16(image, peOffset + 20);
        int optional = peOffset + 24;

        uint entryRva = ReadUInt32(image, optional + 16);
        uint imageBase = ReadUInt32(image, optional + 28);

        if (imageBase != ExpectedImageBase)
        {
            diagnostic = $"unexpected image base 0x{imageBase:X8} (expected 0x{ExpectedImageBase:X8})";
            return KotorExecutableForm.Unknown;
        }

        if (timeStamp != Kotor103TimeStamp)
        {
            diagnostic = $"unexpected PE timestamp 0x{timeStamp:X8} (KOTOR 1.0.3 is 0x{Kotor103TimeStamp:X8})";
            return KotorExecutableForm.Unknown;
        }

        int sectionTable = optional + optionalHeaderSize;
        if (sectionCount is < 1 or > 96 || sectionTable + (sectionCount * 40) > image.Length)
        {
            diagnostic = "section table does not fit in the file";
            return KotorExecutableForm.Unknown;
        }

        // Find the section the entry point lands in, and remember where .text starts.
        string entrySection = string.Empty;
        long entryFileOffset = -1;

        for (int i = 0; i < sectionCount; i++)
        {
            int header = sectionTable + (i * 40);
            string name = ReadName(image, header);
            uint virtualSize = ReadUInt32(image, header + 8);
            uint virtualAddress = ReadUInt32(image, header + 12);
            uint rawSize = ReadUInt32(image, header + 16);
            uint rawPointer = ReadUInt32(image, header + 20);

            uint span = Math.Max(virtualSize, rawSize);
            if (entryRva >= virtualAddress && entryRva < virtualAddress + span)
            {
                entrySection = name;
                entryFileOffset = rawPointer + (entryRva - virtualAddress);
            }
        }

        if (entrySection.Length == 0)
        {
            diagnostic = $"entry point RVA 0x{entryRva:X8} is not inside any section";
            return KotorExecutableForm.Unknown;
        }

        if (entrySection == ".text")
        {
            diagnostic = $"DRM-free: entry point 0x{imageBase + entryRva:X8} in .text";
            return KotorExecutableForm.DrmFree;
        }

        if (entrySection == StubSectionName)
        {
            // The marker is what separates "the entry point happens to be in .bind" from
            // "this is the SteamStub wrapper". Checked rather than assumed, because the whole
            // value of this classification is that it is evidence and not a guess.
            bool marked = entryFileOffset >= 4 && entryFileOffset <= image.Length
                && ReadUInt32(image, (int)entryFileOffset - 4) == SteamStubMarker;

            if (marked)
            {
                diagnostic =
                    $"SteamStub: entry point 0x{imageBase + entryRva:X8} in {StubSectionName}, "
                    + $"marker 0x{SteamStubMarker:X8} present. Same link as the DRM-free build; "
                    + ".text is encrypted at rest and decrypts in place before the game runs.";
                return KotorExecutableForm.SteamStub;
            }

            diagnostic =
                $"entry point 0x{imageBase + entryRva:X8} is in {StubSectionName} but the "
                + $"0x{SteamStubMarker:X8} marker is absent - an unrecognised wrapper";
            return KotorExecutableForm.Unknown;
        }

        diagnostic = $"entry point 0x{imageBase + entryRva:X8} is in an unexpected section '{entrySection}'";
        return KotorExecutableForm.Unknown;
    }

    private static string ReadName(ReadOnlySpan<byte> image, int offset)
    {
        int length = 0;
        while (length < 8 && image[offset + length] != 0)
        {
            length++;
        }

        return System.Text.Encoding.ASCII.GetString(image.Slice(offset, length));
    }

    private static int ReadInt32(ReadOnlySpan<byte> image, int offset) =>
        image[offset] | (image[offset + 1] << 8) | (image[offset + 2] << 16) | (image[offset + 3] << 24);

    private static uint ReadUInt32(ReadOnlySpan<byte> image, int offset) =>
        (uint)ReadInt32(image, offset);

    private static int ReadUInt16(ReadOnlySpan<byte> image, int offset) =>
        image[offset] | (image[offset + 1] << 8);
}

using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The Steam↔DRM-free interop fix, stated as a test: two installs whose RUNNING code is
/// identical must pass the compatibility handshake even though their FILES differ, and two
/// installs whose running code differs must still be refused.
///
/// The fixture is a miniature of the real pair measured in docs/research/steam-support.md
/// §§1-4: one PE whose <c>.text</c> is plaintext with the entry point inside it, and one whose
/// <c>.text</c> is ciphertext with the entry point inside a <c>.bind</c> wrapper section behind
/// Valve's <c>0xC0DEC0DE</c> marker. Everything else — section table, timestamp, image base,
/// every other section's bytes — is identical, which is what the real measurement found. Both
/// decrypt to the SAME mapped image, because that is what SteamStub does at load.
/// </summary>
public class ExeImageDigestRuleTests
{
    private const int TextRva = 0x1000;
    private const int TextSize = 0x20000;
    private const int BindRva = TextRva + TextSize;
    private const int BindSize = 0x1000;
    private const int ImageSize = BindRva + BindSize;

    private const uint UnwrappedEntry = TextRva + 0x40;
    private const uint WrappedEntry = BindRva + 0x2ED;

    [Fact]
    public void TheSameRunningImageHashesTheSameThoughTheFilesDiffer()
    {
        byte[] mapped = MappedImage();
        byte[] drmFreeFile = DrmFreeFile(mapped);
        byte[] steamFile = SteamStubFile(mapped);

        // The fixture really is the shape the research measured: the two files differ, and a
        // build recogniser tells them apart.
        Assert.NotEqual(drmFreeFile, steamFile);
        Assert.Equal(KotorExecutableForm.DrmFree, SteamBuild.Inspect(drmFreeFile, out _));
        Assert.Equal(KotorExecutableForm.SteamStub, SteamBuild.Inspect(steamFile, out _));

        // THE BUG: the file hash — what ContentDigest.ExeDigest used to carry — says these are
        // different builds.
        Assert.True(TryHashFile(drmFreeFile, out uint drmFreeFileHash));
        Assert.True(TryHashFile(steamFile, out uint steamFileHash));
        Assert.NotEqual(drmFreeFileHash, steamFileHash);

        // THE FIX: both processes run the same decrypted image, so both publish the same
        // digest. (The Steam process gets there via the wrapper; the DRM-free one was born
        // that way. The mapped bytes are identical either way, which is the whole point.)
        Assert.True(ExeImageDigestRule.TryComputeFromMappedImage(mapped, out uint a, out _));
        Assert.True(ExeImageDigestRule.TryComputeFromMappedImage(mapped, out uint b, out _));
        Assert.Equal(a, b);

        // ...and the handshake admits them.
        CompatibilityVerdict verdict = ContentCompatibility.Compare(DigestWithExe(a), DigestWithExe(b));
        Assert.True(verdict.Admitted);
        Assert.Equal(RefusalReason.None, verdict.Refusal);
    }

    [Fact]
    public void AnEntryPointDifferenceAloneDoesNotChangeTheDigest()
    {
        // AddressOfEntryPoint is the ONE header field the wrapper changes, and it says nothing
        // about the code that runs. Excluding it is what makes the two forms agree; this pins
        // that it is actually excluded rather than accidentally equal.
        byte[] asDrmFree = MappedImage();
        byte[] asWrapped = MappedImage();
        WriteUInt32(asWrapped, EntryPointOffset, WrappedEntry);

        Assert.NotEqual(asDrmFree, asWrapped);
        Assert.True(ExeImageDigestRule.TryComputeFromMappedImage(asDrmFree, out uint a, out _));
        Assert.True(ExeImageDigestRule.TryComputeFromMappedImage(asWrapped, out uint b, out _));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ADifferentRunningImageIsStillRefused()
    {
        byte[] mine = MappedImage();
        byte[] theirs = MappedImage();

        // One byte of code, deep inside .text: a different build, a patched exe, or a runtime
        // patcher a file hash would never have noticed at all.
        theirs[TextRva + 0x1234] ^= 0xFF;

        Assert.True(ExeImageDigestRule.TryComputeFromMappedImage(mine, out uint a, out _));
        Assert.True(ExeImageDigestRule.TryComputeFromMappedImage(theirs, out uint b, out _));
        Assert.NotEqual(a, b);

        CompatibilityVerdict verdict = ContentCompatibility.Compare(DigestWithExe(a), DigestWithExe(b));
        Assert.False(verdict.Admitted);
        Assert.Equal(RefusalReason.Executable, verdict.Refusal);
    }

    [Fact]
    public void AChangedSectionLayoutIsRefusedEvenWithIdenticalText()
    {
        // The header facts are part of the digest, so a genuinely different link whose .text
        // happened to match is still a different image.
        byte[] mine = MappedImage();
        byte[] theirs = MappedImage();
        WriteUInt32(theirs, SectionTableOffset + 40 + 12, BindRva + 0x1000); // .bind moved

        Assert.True(ExeImageDigestRule.TryComputeFromMappedImage(mine, out uint a, out _));
        Assert.True(ExeImageDigestRule.TryComputeFromMappedImage(theirs, out uint b, out _));
        Assert.NotEqual(a, b);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(0x100)]
    public void MalformedInputIsRejectedWithAReasonRatherThanThrowing(int length)
    {
        byte[] truncated = MappedImage()[..length];

        Assert.False(ExeImageDigestRule.TryComputeFromMappedImage(truncated, out uint digest, out string why));
        Assert.Equal(0u, digest);
        Assert.NotEqual(string.Empty, why);
    }

    [Fact]
    public void ATextSectionRunningPastTheImageIsRejected()
    {
        // The mapped body is absent (headers only). Nothing to hash, and saying so beats
        // hashing whatever happens to follow the header page in memory.
        byte[] headersOnly = MappedImage()[..ExeImageDigestRule.HeaderWindow];

        Assert.False(ExeImageDigestRule.TryComputeFromMappedImage(headersOnly, out _, out string why));
        Assert.Contains(".text", why);
    }

    // ---- fixture ---------------------------------------------------------------------

    private static ContentDigest DigestWithExe(uint exe) =>
        new((ushort)ContentDigest.Field.Exe, exe, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>The file hash the handshake used to compare, reached through its public name.
    /// </summary>
    private static bool TryHashFile(byte[] content, out uint digest)
    {
        string path = Path.Combine(Path.GetTempPath(), $"kc-exe-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, content);
            return InstallDigest.TryHashExecutableFile(path, out digest);
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is not a test failure.
            }
        }
    }

    /// <summary>
    /// The decrypted image both forms produce once loaded: PE headers, a plaintext
    /// <c>.text</c>, and a <c>.bind</c> section. Deterministic, so two calls are byte-equal.
    /// </summary>
    private static byte[] MappedImage()
    {
        byte[] image = new byte[ImageSize];

        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        WriteUInt32(image, 0x3C, 0x80);

        int pe = 0x80;
        image[pe] = (byte)'P';
        image[pe + 1] = (byte)'E';
        WriteUInt16(image, pe + 4, 0x014C);                       // i386
        WriteUInt16(image, pe + 6, 2);                            // two sections
        WriteUInt32(image, pe + 8, SteamBuild.Kotor103TimeStamp);
        WriteUInt16(image, pe + 20, 0xE0);                        // PE32 optional header

        int optional = pe + 24;
        WriteUInt16(image, optional, 0x010B);
        WriteUInt32(image, optional + 16, UnwrappedEntry);
        WriteUInt32(image, optional + 28, SteamBuild.ExpectedImageBase);

        int sections = optional + 0xE0;
        WriteSection(image, sections, ".text", TextRva, TextSize);
        WriteSection(image, sections + 40, ".bind", BindRva, BindSize);

        // Plausible, deterministic code and wrapper bodies. The exact bytes do not matter;
        // that they are the SAME on both sides of the wrapper is the whole claim.
        for (int i = 0; i < TextSize; i++)
        {
            image[TextRva + i] = (byte)((i * 31) + (i >> 8));
        }

        for (int i = 0; i < BindSize; i++)
        {
            image[BindRva + i] = (byte)(0xA0 + (i & 0x0F));
        }

        // Valve's marker sits in the four bytes immediately before the wrapped entry point.
        WriteUInt32(image, (int)WrappedEntry - 4, SteamBuild.SteamStubMarker);
        return image;
    }

    /// <summary>The unwrapped file: raw offsets equal RVAs, so it is the mapped image.</summary>
    private static byte[] DrmFreeFile(byte[] mapped) => (byte[])mapped.Clone();

    /// <summary>
    /// The wrapped file: identical to the unwrapped one except <c>.text</c> is ciphertext and
    /// the entry point points into <c>.bind</c> — the exact two-place diff §2 measured.
    /// </summary>
    private static byte[] SteamStubFile(byte[] mapped)
    {
        byte[] file = (byte[])mapped.Clone();
        WriteUInt32(file, EntryPointOffset, WrappedEntry);

        for (int i = 0; i < TextSize; i++)
        {
            file[TextRva + i] ^= (byte)(0x5A + (i & 0x7F));
        }

        return file;
    }

    private const int EntryPointOffset = 0x80 + 24 + 16;

    private const int SectionTableOffset = 0x80 + 24 + 0xE0;

    private static void WriteSection(byte[] image, int offset, string name, int rva, int size)
    {
        for (int i = 0; i < name.Length; i++)
        {
            image[offset + i] = (byte)name[i];
        }

        WriteUInt32(image, offset + 8, (uint)size);   // VirtualSize
        WriteUInt32(image, offset + 12, (uint)rva);   // VirtualAddress
        WriteUInt32(image, offset + 16, (uint)size);  // SizeOfRawData
        WriteUInt32(image, offset + 20, (uint)rva);   // PointerToRawData
    }

    private static void WriteUInt32(byte[] image, int offset, uint value)
    {
        image[offset] = (byte)value;
        image[offset + 1] = (byte)(value >> 8);
        image[offset + 2] = (byte)(value >> 16);
        image[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteUInt16(byte[] image, int offset, int value)
    {
        image[offset] = (byte)value;
        image[offset + 1] = (byte)(value >> 8);
    }
}

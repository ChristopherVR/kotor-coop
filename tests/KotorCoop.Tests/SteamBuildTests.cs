using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// Recognition of the two shipped forms of <c>swkotor.exe</c>. Driven by synthetic PE headers
/// rather than by the real files, deliberately: the real ones are 4.4 MB and live at paths
/// that only exist on a machine with KOTOR installed, and a test that silently passes when the
/// game is absent is worse than no test. What the synthetic headers encode are the exact
/// values measured from both real binaries — see docs/research/steam-support.md §1, which
/// records the section table and entry points these cases are built from.
/// </summary>
public class SteamBuildTests
{
    private const int PeOffset = 0x80;
    private const int OptionalHeaderSize = 0xE0;
    private const int SectionTable = PeOffset + 24 + OptionalHeaderSize;

    /// <summary>The real section table, from both binaries (they agree exactly).</summary>
    private static readonly (string Name, uint VSize, uint VAddr, uint RawSize, uint RawPtr)[] KotorSections =
    [
        (".text", 0x0033B1D0, 0x00001000, 0x0033C000, 0x00001000),
        (".rdata", 0x0004FBFE, 0x0033D000, 0x00050000, 0x0033D000),
        (".data", 0x000A8498, 0x0038D000, 0x00017000, 0x0038D000),
        (".rsrc", 0x00036BD8, 0x00436000, 0x00037000, 0x003A4000),
        (".bind", 0x00056000, 0x0046D000, 0x00056000, 0x003DB000),
    ];

    /// <summary>The DRM-free build's entry point: inside <c>.text</c>.</summary>
    private const uint DrmFreeEntry = 0x002FB38D;

    /// <summary>The Steam build's entry point: inside the wrapper's <c>.bind</c>.</summary>
    private const uint SteamEntry = 0x0046D2ED;

    [Fact]
    public void AnEntryPointInTextIsTheDrmFreeBuild()
    {
        byte[] image = BuildImage(DrmFreeEntry, withStubMarker: false);

        Assert.Equal(KotorExecutableForm.DrmFree, SteamBuild.Inspect(image, out string why));
        Assert.Contains(".text", why);
    }

    [Fact]
    public void AnEntryPointInBindBehindTheMarkerIsTheSteamBuild()
    {
        byte[] image = BuildImage(SteamEntry, withStubMarker: true);

        Assert.Equal(KotorExecutableForm.SteamStub, SteamBuild.Inspect(image, out string why));
        Assert.Contains(".bind", why);
    }

    [Fact]
    public void AnEntryPointInBindWithoutTheMarkerIsNotClaimedAsSteam()
    {
        // The marker is the evidence. Without it this is some other wrapper, and reporting it
        // as SteamStub would be asserting something about its runtime behaviour that nothing
        // here established.
        byte[] image = BuildImage(SteamEntry, withStubMarker: false);

        Assert.Equal(KotorExecutableForm.Unknown, SteamBuild.Inspect(image, out string why));
        Assert.Contains("marker", why);
    }

    [Fact]
    public void ADifferentImageBaseIsUnknown()
    {
        byte[] image = BuildImage(DrmFreeEntry, withStubMarker: false);
        Write32(image, PeOffset + 24 + 28, 0x00010000);

        Assert.Equal(KotorExecutableForm.Unknown, SteamBuild.Inspect(image, out string why));
        Assert.Contains("image base", why);
    }

    [Fact]
    public void ADifferentLinkTimestampIsUnknown()
    {
        // The timestamp is what makes this "KOTOR 1.0.3" rather than "some fixed-base 32-bit
        // PE". Both real builds carry 0x402BC2D9 because they are the same link.
        byte[] image = BuildImage(DrmFreeEntry, withStubMarker: false);
        Write32(image, PeOffset + 8, 0x12345678);

        Assert.Equal(KotorExecutableForm.Unknown, SteamBuild.Inspect(image, out string why));
        Assert.Contains("timestamp", why);
    }

    [Fact]
    public void AnEntryPointOutsideEverySectionIsUnknown()
    {
        byte[] image = BuildImage(0x00F00000, withStubMarker: false);

        Assert.Equal(KotorExecutableForm.Unknown, SteamBuild.Inspect(image, out string why));
        Assert.Contains("not inside any section", why);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(0x40)]
    [InlineData(0x200)]
    public void ATruncatedOrEmptyFileIsUnknownRatherThanACrash(int length)
    {
        byte[] image = BuildImage(DrmFreeEntry, withStubMarker: false);
        byte[] truncated = image[..Math.Min(length, image.Length)];

        Exception? caught = Record.Exception(
            () => Assert.Equal(KotorExecutableForm.Unknown, SteamBuild.Inspect(truncated, out _)));
        Assert.Null(caught);
    }

    [Fact]
    public void RandomBytesAreUnknownRatherThanACrash()
    {
        // The input is a file some player pointed us at. Nothing about it is guaranteed.
        Random random = new(20260829);

        for (int i = 0; i < 200; i++)
        {
            byte[] noise = new byte[random.Next(0, 4096)];
            random.NextBytes(noise);

            Exception? caught = Record.Exception(() => SteamBuild.Inspect(noise, out _));
            Assert.Null(caught);
        }
    }

    [Fact]
    public void AMissingFileIsUnknownRatherThanAThrow()
    {
        Assert.Equal(
            KotorExecutableForm.Unknown,
            SteamBuild.InspectFile(Path.Combine(Path.GetTempPath(), "kotorcoop-no-such-file.exe"), out string why));
        Assert.Contains("no file", why);
    }

    /// <summary>
    /// Builds a PE32 header with KOTOR 1.0.3's real section table and the given entry point.
    /// Only the headers are real; there is no section content, which is all
    /// <see cref="SteamBuild"/> ever reads.
    /// </summary>
    private static byte[] BuildImage(uint entryRva, bool withStubMarker)
    {
        byte[] image = new byte[SectionTable + (KotorSections.Length * 40) + 0x1000];

        image[0] = (byte)'M';
        image[1] = (byte)'Z';
        Write32(image, 0x3C, PeOffset);

        image[PeOffset] = (byte)'P';
        image[PeOffset + 1] = (byte)'E';
        Write16(image, PeOffset + 4, 0x014C);                       // i386
        Write16(image, PeOffset + 6, KotorSections.Length);
        Write32(image, PeOffset + 8, SteamBuild.Kotor103TimeStamp);
        Write16(image, PeOffset + 20, OptionalHeaderSize);

        int optional = PeOffset + 24;
        Write16(image, optional, 0x010B);                           // PE32
        Write32(image, optional + 16, entryRva);
        Write32(image, optional + 28, SteamBuild.ExpectedImageBase);

        for (int i = 0; i < KotorSections.Length; i++)
        {
            (string name, uint vsize, uint vaddr, uint rawSize, uint rawPtr) = KotorSections[i];
            int header = SectionTable + (i * 40);

            for (int c = 0; c < name.Length; c++)
            {
                image[header + c] = (byte)name[c];
            }

            Write32(image, header + 8, vsize);
            Write32(image, header + 12, vaddr);
            Write32(image, header + 16, rawSize);
            Write32(image, header + 20, rawPtr);
        }

        if (withStubMarker)
        {
            // The marker sits in the four bytes immediately before the entry point's own file
            // offset. .bind maps rawPtr 0x3DB000 to vaddr 0x46D000, but the synthetic image has
            // no section content, so place it wherever the real mapping arithmetic lands and
            // grow the buffer to fit.
            long entryFileOffset = FileOffsetOf(entryRva);
            if (entryFileOffset >= 4)
            {
                if (entryFileOffset > image.Length)
                {
                    Array.Resize(ref image, (int)entryFileOffset + 16);
                }

                Write32(image, (int)entryFileOffset - 4, SteamBuild.SteamStubMarker);
            }
        }

        return image;
    }

    private static long FileOffsetOf(uint rva)
    {
        foreach ((_, uint vsize, uint vaddr, uint rawSize, uint rawPtr) in KotorSections)
        {
            if (rva >= vaddr && rva < vaddr + Math.Max(vsize, rawSize))
            {
                return rawPtr + (rva - vaddr);
            }
        }

        return -1;
    }

    private static void Write16(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
    }

    private static void Write32(byte[] buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    private static void Write32(byte[] buffer, int offset, int value) => Write32(buffer, offset, (uint)value);
}

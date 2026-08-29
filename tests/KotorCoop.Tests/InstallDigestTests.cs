using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class InstallDigestTests
{
    private static readonly string GameDir = GameFilesTests.GogDirectory;

    [GogInstallFact]
    public void RealInstallProducesAStableNonEmptyDigest()
    {
        string exe = Path.Combine(GameDir, "swkotor.exe");

        // The exe field is the PLUGIN's hash of the running image, not the file — the file
        // hash says "different build" about a Steam install and a DRM-free one whose code is
        // identical (docs/research/steam-support.md §8). There is no game process here, so it
        // is supplied directly; what this test is about is the FILE-derived fields.
        const uint LiveImage = 0xABCD1234;

        ContentDigest a = InstallDigest.Compute(GameDir, exe, LiveImage);
        ContentDigest b = InstallDigest.Compute(GameDir, exe, LiveImage);

        // The same install must fingerprint identically twice (nothing time- or
        // order-dependent leaked into the hash).
        Assert.Equal(a, b);

        // A vanilla GOG install computes the fields co-op actually gates on.
        Assert.True(a.Knows(ContentDigest.Field.Exe));
        Assert.Equal(LiveImage, a.ExeDigest);
        Assert.True(a.Knows(ContentDigest.Field.Appearance));
        Assert.True(a.Knows(ContentDigest.Field.Tlk));
        Assert.True(a.Knows(ContentDigest.Field.Journal));

        // Sanity against the known GOG numbers (509 appearance rows, 49265 tlk entries).
        Assert.True(a.AppearanceRows is > 400 and < 4000);
        Assert.Equal(49265u, a.TlkEntryCount);

        // Two identical installs are fully compatible.
        CompatibilityVerdict v = ContentCompatibility.Compare(a, b);
        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.All, v.Allowed);
    }

    [GogInstallFact]
    public void WithoutALiveImageDigestTheExecutableIsUncheckedRatherThanFileHashed()
    {
        string exe = Path.Combine(GameDir, "swkotor.exe");

        ContentDigest d = InstallDigest.Compute(GameDir, exe);

        // ⚠ NO FALLBACK TO THE FILE HASH, on purpose. A peer carrying a file hash and a peer
        // carrying an image hash would compare two different KINDS of number and refuse every
        // session. Unknown warns; it never lies. (The file hash still exists — as a log label.)
        Assert.False(d.Knows(ContentDigest.Field.Exe));
        Assert.Equal(0u, d.ExeDigest);
        Assert.True(InstallDigest.TryHashExecutableFile(exe, out uint fileHash));
        Assert.NotEqual(0u, fileHash);

        // And an unknown field on one side cannot refuse a session.
        CompatibilityVerdict verdict = ContentCompatibility.Compare(
            d, InstallDigest.Compute(GameDir, exe, 0xDEADBEEF));
        Assert.True(verdict.Admitted);
    }

    [Fact]
    public void AMissingDirectoryDegradesToUncheckedRatherThanThrowing()
    {
        ContentDigest d = InstallDigest.Compute(
            @"Z:\definitely\not\a\game", @"Z:\definitely\not\a\game\swkotor.exe");

        // No source readable: everything unknown, which compares as "unchecked" and never
        // blocks a session.
        Assert.True(d.IsEmpty);
    }
}

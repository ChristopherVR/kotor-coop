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

        ContentDigest a = InstallDigest.Compute(GameDir, exe);
        ContentDigest b = InstallDigest.Compute(GameDir, exe);

        // The same install must fingerprint identically twice (nothing time- or
        // order-dependent leaked into the hash).
        Assert.Equal(a, b);

        // A vanilla GOG install computes the fields co-op actually gates on.
        Assert.True(a.Knows(ContentDigest.Field.Exe));
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

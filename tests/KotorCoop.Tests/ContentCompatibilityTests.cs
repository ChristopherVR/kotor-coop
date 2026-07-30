using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public class ContentCompatibilityTests
{
    private const ushort AllKnown = (ushort)(
        ContentDigest.Field.Exe | ContentDigest.Field.Override | ContentDigest.Field.TwoDa
        | ContentDigest.Field.Appearance | ContentDigest.Field.Tlk | ContentDigest.Field.Journal
        | ContentDigest.Field.Modules);

    private static ContentDigest Digest(
        uint exe = 1, uint over = 1, ushort overCount = 10, uint twoDa = 1, uint appearance = 1,
        ushort appearanceRows = 500, uint tlk = 1, uint tlkCount = 49265, uint journal = 1, uint modules = 1) =>
        new(AllKnown, exe, over, overCount, twoDa, appearance, appearanceRows, tlk, tlkCount, journal, modules);

    [Fact]
    public void IdenticalInstallsGetTheFullCapabilitySet()
    {
        CompatibilityVerdict v = ContentCompatibility.Compare(Digest(), Digest());

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.All, v.Allowed);
        Assert.Empty(v.Warnings);
    }

    [Fact]
    public void DifferentExecutableIsRefused()
    {
        CompatibilityVerdict v = ContentCompatibility.Compare(Digest(exe: 1), Digest(exe: 2));

        Assert.False(v.Admitted);
        Assert.Equal(RefusalReason.Executable, v.Refusal);
        Assert.Equal(SessionCapabilities.None, v.Allowed);
    }

    [Fact]
    public void DifferentAppearanceTableDropsOnlyTheAppearanceChannel()
    {
        // A reordered appearance.2da would paint the wrong body or index off the end.
        CompatibilityVerdict v = ContentCompatibility.Compare(Digest(appearance: 1), Digest(appearance: 2));

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.None, v.Allowed & SessionCapabilities.Appearance);
        // Everything else still on.
        Assert.NotEqual(SessionCapabilities.None, v.Allowed & SessionCapabilities.Enemies);
        Assert.NotEqual(SessionCapabilities.None, v.Allowed & SessionCapabilities.Phantoms);
        Assert.NotEmpty(v.Warnings);
    }

    [Fact]
    public void DifferentOverrideFoldersWarnButDoNotBlock()
    {
        // The expected case for two modded installs. Play continues; the difference is only
        // surfaced so it explains any gear that fails to appear.
        CompatibilityVerdict v = ContentCompatibility.Compare(Digest(over: 1, overCount: 5), Digest(over: 2, overCount: 40));

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.All, v.Allowed);
        Assert.Contains(v.Warnings, w => w.Contains("override"));
    }

    [Fact]
    public void AnUnknownFieldOnOneSideOnlyWarns()
    {
        // One peer could not read its appearance.2da — that must not refuse or degrade;
        // it can only be reported as unchecked.
        ContentDigest known = Digest(appearance: 1);
        ContentDigest missingAppearance = known with
        {
            KnownMask = (ushort)(AllKnown & ~(ushort)ContentDigest.Field.Appearance),
            AppearanceDigest = 999,
        };

        CompatibilityVerdict v = ContentCompatibility.Compare(known, missingAppearance);

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.All, v.Allowed);   // appearance NOT dropped
    }

    [Fact]
    public void AnEmptyDigestIsAdmittedUncheckedWithAWarning()
    {
        CompatibilityVerdict v = ContentCompatibility.Compare(Digest(), ContentDigest.Unknown);

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.All, v.Allowed);
        Assert.Contains(v.Warnings, w => w.Contains("unchecked"));
    }

    [Fact]
    public void SaveMismatchRefusesByDefaultAndDegradesWhenAllowed()
    {
        CompatibilityVerdict install = ContentCompatibility.Compare(Digest(), Digest());

        ContentCompatibility.AllowDifferentSave = false;
        CompatibilityVerdict refused = ContentCompatibility.ApplyWorldMismatch(
            install, globalsCatalogueMismatch: false, saveIdentityMismatch: true, moduleGitMismatch: false);
        Assert.Equal(RefusalReason.SaveGame, refused.Refusal);

        try
        {
            ContentCompatibility.AllowDifferentSave = true;
            CompatibilityVerdict degraded = ContentCompatibility.ApplyWorldMismatch(
                install, globalsCatalogueMismatch: false, saveIdentityMismatch: true, moduleGitMismatch: false);

            Assert.True(degraded.Admitted);
            Assert.Equal(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.Enemies);
            Assert.Equal(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.Doors);
            Assert.Equal(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.GroundItems);
            Assert.NotEqual(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.Phantoms);
        }
        finally
        {
            ContentCompatibility.AllowDifferentSave = false;
        }
    }

    [Fact]
    public void GlobalsCatalogueMismatchDropsWorldStateAndDoors()
    {
        CompatibilityVerdict install = ContentCompatibility.Compare(Digest(), Digest());
        CompatibilityVerdict v = ContentCompatibility.ApplyWorldMismatch(
            install, globalsCatalogueMismatch: true, saveIdentityMismatch: false, moduleGitMismatch: false);

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.None, v.Allowed & SessionCapabilities.WorldState);
        Assert.Equal(SessionCapabilities.None, v.Allowed & SessionCapabilities.Doors);
        Assert.NotEqual(SessionCapabilities.None, v.Allowed & SessionCapabilities.Enemies);
    }

    [Fact]
    public void ModuleGitMismatchDropsIdKeyedChannelsButKeepsGlobals()
    {
        CompatibilityVerdict install = ContentCompatibility.Compare(Digest(), Digest());
        CompatibilityVerdict v = ContentCompatibility.ApplyWorldMismatch(
            install, globalsCatalogueMismatch: false, saveIdentityMismatch: false, moduleGitMismatch: true);

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.None, v.Allowed & SessionCapabilities.Enemies);
        Assert.NotEqual(SessionCapabilities.None, v.Allowed & SessionCapabilities.WorldState);
    }

    [Fact]
    public void DigestSurvivesTheWire()
    {
        System.Span<byte> buffer = stackalloc byte[ProtocolConstants.MaxPacketSize];
        ContentDigest digest = Digest(exe: 0xDEADBEEF, appearance: 0xCAFE, appearanceRows: 512);

        int length = PacketCodec.Write(buffer, new HelloPacket("Revan", "end_m01aa", 7, digest, SessionPassword.None));
        HelloPacket? decoded = PacketCodec.ReadHello(buffer[..length]);

        Assert.NotNull(decoded);
        Assert.Equal(digest, decoded.Digest);
    }
}

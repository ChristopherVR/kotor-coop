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
    public void AnAllowedExecutableMismatchIsAdmittedWithAWarning()
    {
        // coop.ini exe_mismatch = allow: the refusal becomes a W_EXE note that costs no channel,
        // and both digests are named so a bug report shows which build each side ran.
        CompatibilityVerdict v = ContentCompatibility.Compare(
            Digest(exe: 0xC6962619), Digest(exe: 0x12345678), allowExeMismatch: true);

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.All, v.Allowed);
        CompatibilityNote note = Assert.Single(v.Notes);
        Assert.Equal(CompatibilityCode.Executable, note.Code);
        Assert.Equal("W_EXE", note.Tag);
        Assert.Contains("C6962619", note.Text);
        Assert.Contains("12345678", note.Text);
    }

    [Fact]
    public void AllowingAnExecutableMismatchAddsNothingWhenTheExecutablesMatch()
    {
        CompatibilityVerdict v = ContentCompatibility.Compare(Digest(), Digest(), allowExeMismatch: true);

        Assert.True(v.Admitted);
        Assert.Empty(v.Notes);
    }

    [Fact]
    public void AllowingAnExecutableMismatchStillAppliesTheOtherRules()
    {
        CompatibilityVerdict v = ContentCompatibility.Compare(
            Digest(exe: 1), Digest(exe: 2, appearance: 9), allowExeMismatch: true);

        Assert.True(v.Admitted);
        Assert.Contains(v.Notes, n => n.Code == CompatibilityCode.Executable);
        Assert.Contains(v.Notes, n => n.Code == CompatibilityCode.Appearance);
        Assert.False(v.Allowed.HasFlag(SessionCapabilities.Appearance));
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
    public void SaveMismatchDegradesByDefaultAndRefusesOnlyWhenDemanded()
    {
        // The policy world adoption changed. A different save used to end the join; it now
        // costs exactly the channels that rest on shared object ids, because adoption is a
        // recovery the player can actually reach — and evicting someone already standing in
        // the world is not.
        CompatibilityVerdict install = ContentCompatibility.Compare(Digest(), Digest());

        ContentCompatibility.RequireIdenticalSave = false;
        CompatibilityVerdict degraded = ContentCompatibility.ApplyWorldMismatch(
            install, globalsCatalogueMismatch: false, saveIdentityMismatch: true, moduleGitMismatch: false);

        Assert.True(degraded.Admitted);
        Assert.Equal(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.Enemies);
        Assert.Equal(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.Doors);
        Assert.Equal(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.GroundItems);
        Assert.Equal(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.Party);
        Assert.NotEqual(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.Phantoms);
        Assert.NotEqual(SessionCapabilities.None, degraded.Allowed & SessionCapabilities.Journal);

        // Machine-readable, not just a sentence: the note names the cause AND the cost.
        CompatibilityNote note = Assert.Single(degraded.Notes, n => n.Code == CompatibilityCode.Save);
        Assert.Equal("W_SAVE", note.Tag);
        Assert.Equal(ContentCompatibility.ObjectIdKeyed, note.Dropped);
        Assert.True(note.IsDegradation);
        Assert.Contains("rejoin", note.Text, StringComparison.OrdinalIgnoreCase);

        try
        {
            ContentCompatibility.RequireIdenticalSave = true;
            CompatibilityVerdict refused = ContentCompatibility.ApplyWorldMismatch(
                install, globalsCatalogueMismatch: false, saveIdentityMismatch: true, moduleGitMismatch: false);

            Assert.Equal(RefusalReason.SaveGame, refused.Refusal);
            Assert.Equal(SessionCapabilities.None, refused.Allowed);
            Assert.StartsWith("E_SAVE", refused.Summary, StringComparison.Ordinal);
        }
        finally
        {
            ContentCompatibility.RequireIdenticalSave = false;
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
    public void AModuleGitDegradationNamesTheModuleItAppliesTo()
    {
        // "enemies are off here" is only actionable if the player is told where "here" is —
        // and the module travels with the comparison precisely so this line can say it.
        CompatibilityVerdict install = ContentCompatibility.Compare(Digest(), Digest());
        WorldDigestComparison mismatch = WorldDigest.Compare(
            new WorldDigest(0xAAAA, 0xBBBB, "tar_m03aa", 0x1111),
            new WorldDigest(0xAAAA, 0xBBBB, "TAR_M03AA", 0x2222));

        Assert.True(mismatch.ModuleGitMismatch);
        Assert.Equal("tar_m03aa", mismatch.Module);

        CompatibilityVerdict v = ContentCompatibility.ApplyWorldMismatch(install, mismatch);
        CompatibilityNote note = Assert.Single(v.Notes, n => n.Code == CompatibilityCode.ModuleGit);

        Assert.Equal("W_GIT", note.Tag);
        Assert.Equal(ContentCompatibility.ObjectIdKeyed, note.Dropped);
        Assert.Contains("tar_m03aa", note.Text, StringComparison.Ordinal);
        Assert.Equal(ContentCompatibility.ObjectIdKeyed, v.Dropped);
    }

    [Fact]
    public void DifferentRuntimeDifficultiesWarnWithoutDroppingAnyChannel()
    {
        CompatibilityVerdict install = ContentCompatibility.Compare(Digest(), Digest());
        WorldDigestComparison mismatch = WorldDigest.Compare(
            new WorldDigest(0xAAAA, 0xBBBB, "end_m01aa", 0xCCCC, DifficultyLevel: 0),
            new WorldDigest(0xAAAA, 0xBBBB, "end_m01aa", 0xCCCC, DifficultyLevel: 2));

        CompatibilityVerdict verdict = ContentCompatibility.ApplyWorldMismatch(install, mismatch);
        CompatibilityNote note = Assert.Single(verdict.Notes, n => n.Code == CompatibilityCode.Difficulty);

        Assert.Equal("W_DIFFICULTY", note.Tag);
        Assert.Equal(SessionCapabilities.None, note.Dropped);
        Assert.Equal(SessionCapabilities.All, verdict.Allowed);
        Assert.Contains("PC and party damage scaling may differ", note.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnresolvedWorldFieldOnlyEverWarnsAndCostsNoChannel()
    {
        // The house rule at the world level: a peer whose game files could not be read
        // reports 0, and 0 must never take a channel off anyone. A filesystem quirk on one
        // machine is not evidence that the two worlds disagree.
        CompatibilityVerdict install = ContentCompatibility.Compare(Digest(), Digest());

        WorldDigestComparison mismatch = WorldDigest.Compare(
            new WorldDigest(0xAAAA, 0, "tar_m03aa", 0),        // could not fingerprint save or GIT
            new WorldDigest(0xAAAA, 0xBBBB, "tar_m03aa", 0x2222));

        Assert.False(mismatch.Any);

        CompatibilityVerdict v = ContentCompatibility.ApplyWorldMismatch(install, mismatch);

        Assert.True(v.Admitted);
        Assert.Equal(SessionCapabilities.All, v.Allowed);
        Assert.DoesNotContain(v.Notes, n => n.IsDegradation);
    }

    [Fact]
    public void EveryDegradationSaysWhichChannelsItCost()
    {
        // The machine-readable half of the contract: whatever a verdict switched off, the
        // union of its notes accounts for it. Nothing goes dark unexplained.
        CompatibilityVerdict install = ContentCompatibility.Compare(
            Digest(), Digest(appearance: 2)); // appearance mismatch: one install-time degradation

        CompatibilityVerdict v = ContentCompatibility.ApplyWorldMismatch(
            install, globalsCatalogueMismatch: true, saveIdentityMismatch: true,
            moduleGitMismatch: false, module: "end_m01aa");

        SessionCapabilities accountedFor = SessionCapabilities.None;
        foreach (CompatibilityNote note in v.Notes)
        {
            accountedFor |= note.Dropped;
        }

        Assert.Equal(v.Dropped, accountedFor);
        Assert.Contains("W_SAVE", v.Tags, StringComparison.Ordinal);
        Assert.Contains("W_GLOBALS", v.Tags, StringComparison.Ordinal);
        Assert.Contains("W_APPEARANCE", v.Tags, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedInstallIsNeverSoftenedByTheWorldComparison()
    {
        // A refusal is final: no later world fact may talk it back into a session.
        CompatibilityVerdict refused = ContentCompatibility.Compare(Digest(), Digest(exe: 2));
        Assert.Equal(RefusalReason.Executable, refused.Refusal);

        CompatibilityVerdict v = ContentCompatibility.ApplyWorldMismatch(
            refused, globalsCatalogueMismatch: false, saveIdentityMismatch: false, moduleGitMismatch: false);

        Assert.Equal(RefusalReason.Executable, v.Refusal);
        Assert.Equal(SessionCapabilities.None, v.Allowed);
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

    [Fact]
    public void CombineKeepsEveryPeersDropsAndEachFindingOnce()
    {
        CompatibilityNote save = new(CompatibilityCode.Save, SessionCapabilities.Enemies | SessionCapabilities.Doors, "different save");
        CompatibilityVerdict clean = new(RefusalReason.None, SessionCapabilities.All, []);
        CompatibilityVerdict differentSave = new(RefusalReason.None, SessionCapabilities.All & ~(SessionCapabilities.Enemies | SessionCapabilities.Doors), [save]);

        CompatibilityVerdict combined = ContentCompatibility.Combine([clean, differentSave, differentSave]);

        Assert.Equal(differentSave.Allowed, combined.Allowed);
        Assert.Single(combined.Notes);
        Assert.Equal(RefusalReason.None, combined.Refusal);
    }

    [Fact]
    public void CombineIsOrderIndependentSoAHostWithTwoPeersNeverFlaps()
    {
        CompatibilityNote save = new(CompatibilityCode.Save, SessionCapabilities.Enemies, "different save");
        CompatibilityVerdict clean = new(RefusalReason.None, SessionCapabilities.All, []);
        CompatibilityVerdict differentSave = new(RefusalReason.None, SessionCapabilities.All & ~SessionCapabilities.Enemies, [save]);

        Assert.Equal(
            ContentCompatibility.Combine([clean, differentSave]).Dropped,
            ContentCompatibility.Combine([differentSave, clean]).Dropped);
    }

    [Fact]
    public void CombineOfNoPeersAllowsEverything()
    {
        CompatibilityVerdict combined = ContentCompatibility.Combine([]);
        Assert.Equal(SessionCapabilities.All, combined.Allowed);
        Assert.Empty(combined.Notes);
    }
}

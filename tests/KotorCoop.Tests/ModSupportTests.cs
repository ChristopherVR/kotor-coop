using KotorCoop.Memory;
using KotorCoop.Agent;
using KotorCoop.Plugin;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class ModSupportTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kotor-mod-tests-" + Guid.NewGuid());
    public ModSupportTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void ReplacementCompanionUsesItsDeclaredTemplateAndLeavesOtherStockSlotsAlone()
    {
        var profile = Load("""{"schema":1,"id":"custom-companion","companions":[{"slot":2,"template":"custom_carth"}]}""");
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(2, "tar_m02aa", profile, out var template));
        Assert.Equal("custom_carth", template);
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(0, "tar_m02aa", profile, out template));
        Assert.Equal("p_bastilla", template);
    }

    [Fact]
    public void CampaignSlotDoesNotSilentlyRecoverAVanillaCharacter()
    {
        var profile = Load("""{"schema":1,"id":"campaign","campaignModules":["bos_*"],"companions":[{"slot":0,"template":"custom_shadow","module":"bos_*"}]}""");
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(0, "bos_ship", profile, out var template));
        Assert.Equal("custom_shadow", template);
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(2, "bos_ship", profile, out _));
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(2, "tar_m02aa", profile, out template));
        Assert.Equal("p_carth", template);
    }

    [Fact]
    public void SpecificModuleOverridesPrefixAndGeneralRulesRegardlessOfDeclarationOrder()
    {
        var profile = Load("""{"schema":1,"id":"campaign","companions":[{"slot":0,"template":"exact","module":"bos_ship"},{"slot":0,"template":"general"},{"slot":0,"template":"prefix","module":"bos_*"}]}""");
        Assert.Equal(ModSupportProfile.Resolution.Template, profile.Resolve(0, "BOS_SHIP", out var template));
        Assert.Equal("exact", template);
        profile.Resolve(0, "bos_chamber", out template);
        Assert.Equal("prefix", template);
    }

    [Theory]
    [InlineData("{\"schema\":2,\"id\":\"x\"}")]
    [InlineData("{\"schema\":1,\"id\":\"x\",\"companions\":[{\"slot\":9,\"template\":\"custom\"}]}")]
    [InlineData("{\"schema\":1,\"id\":\"x\",\"companions\":[{\"slot\":0,\"template\":\"../bad\"}]}")]
    [InlineData("{\"schema\":1,\"id\":\"x\",\"companions\":[{\"slot\":0,\"template\":\"a\"},{\"slot\":0,\"template\":\"b\"}]}")]
    public void InvalidProfilesAreReportedInsteadOfApplied(string json)
    {
        File.WriteAllText(Path.Combine(_directory, ModSupportProfile.FileName), json);
        Assert.False(ModSupportProfile.TryLoad(_directory, out _, out string error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void SameSizeScriptAndModuleEditsChangeTheGameplayFingerprint()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Override"));
        Directory.CreateDirectory(Path.Combine(_directory, "modules"));
        string script = Path.Combine(_directory, "Override", "recruit.ncs");
        string module = Path.Combine(_directory, "modules", "bos_ship.mod");
        File.WriteAllText(script, "same size A");
        File.WriteAllText(module, "module A");
        var first = ModContentManifest.Compute(_directory);
        File.WriteAllText(script, "same size B");
        var second = ModContentManifest.Compute(_directory);
        Assert.NotEqual(first.Digest(file => file.Gameplay), second.Digest(file => file.Gameplay));
        Assert.Equal(new[] { "override/recruit.ncs" }, first.Differences(second));
        File.WriteAllText(module, "module B");
        var third = ModContentManifest.Compute(_directory);
        Assert.NotEqual(second.Digest(file => file.Gameplay), third.Digest(file => file.Gameplay));
        Assert.Equal(new[] { "modules/bos_ship.mod" }, second.Differences(third));
    }

    [Fact]
    public void CosmeticTextureChangesDoNotDisableGameplayChannels()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Override"));
        string texture = Path.Combine(_directory, "Override", "head.tga");
        File.WriteAllText(texture, "texture A");
        var a = ModContentManifest.Compute(_directory);
        File.WriteAllText(texture, "texture B");
        var b = ModContentManifest.Compute(_directory);
        Assert.Equal(a.Digest(file => file.Gameplay), b.Digest(file => file.Gameplay));
        Assert.NotEqual(a.Digest(_ => true), b.Digest(_ => true));
    }

    [Fact]
    public void ModAuditAcceptsGameDirectoriesAndComparisonPaths()
    {
        var options = AgentOptions.Parse(["mods", "--game-path", _directory, "--compare-with", _directory]);
        Assert.Equal(AgentCommand.Mods, options.Command);
        Assert.Equal(_directory, options.GamePath);
        Assert.Equal(_directory, options.CompareGamePath);
        Assert.Equal(1, ModsCommand.Run(options)); // Missing chitin.key must not look like a match.
        File.WriteAllBytes(Path.Combine(_directory, "chitin.key"), []);
        Assert.Equal(1, ModsCommand.Run(options)); // Neither may a corrupt index.
        byte[] key = new byte[32];
        System.Text.Encoding.ASCII.GetBytes("KEY V1  ").CopyTo(key, 0);
        File.WriteAllBytes(Path.Combine(_directory, "chitin.key"), key);
        Assert.Equal(0, ModsCommand.Run(options)); // No profile requires no creature resource lookup.
        File.WriteAllText(Path.Combine(_directory, ModSupportProfile.FileName),
            """{"schema":1,"id":"missing-creature","companions":[{"slot":2,"template":"not_installed"}]}""");
        Assert.Equal(1, ModsCommand.Run(options));
    }

    [Fact]
    public void RecruitmentProfileChangesAreGameplayDifferences()
    {
        string path = Path.Combine(_directory, ModSupportProfile.FileName);
        File.WriteAllText(path, """{"schema":1,"id":"a"}""");
        var first = ModContentManifest.Compute(_directory);
        File.WriteAllText(path, """{"schema":1,"id":"b"}""");
        var second = ModContentManifest.Compute(_directory);
        Assert.NotEqual(first.Digest(file => file.Gameplay), second.Digest(file => file.Gameplay));
        Assert.Equal(new[] { ModSupportProfile.FileName }, first.Differences(second));
    }

    [Fact]
    public void GameplayDigestRoundTripsAndMismatchesDisableUnsafeReplication()
    {
        var digest = new ContentDigest((ushort)ContentDigest.Field.Gameplay,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, GameplayDigest: 123);
        byte[] buffer = new byte[ProtocolConstants.MaxPacketSize];
        var hello = new HelloPacket("mod-player", "bos_ship", 1, digest, 0);
        int length = PacketCodec.Write(buffer, hello);
        Assert.Equal(digest, PacketCodec.ReadHello(buffer.AsSpan(0, length))!.Digest);
        var mismatch = ContentCompatibility.Compare(digest, digest with { GameplayDigest = 456 });
        Assert.True(mismatch.Admitted);
        Assert.Equal(SessionCapabilities.Phantoms | SessionCapabilities.Appearance, mismatch.Allowed);
        Assert.Contains(mismatch.Notes, note => note.Tag == "W_GAMEPLAY");
        Assert.Equal(SessionCapabilities.All, ContentCompatibility.Compare(digest, digest).Allowed);
    }

    [Fact]
    public void SeparateModFilesActivateOnlyWhenEveryMarkerExistsAndOnlyActiveRulesAreHashed()
    {
        string catalog = Path.Combine(_directory, ModSupportProfile.CatalogDirectory);
        Directory.CreateDirectory(catalog);
        File.WriteAllText(Path.Combine(catalog, "npc.json"),
            """{"schema":2,"id":"npc","detect":{"allFiles":["Override/custom.utc","modules/custom.mod"]},"companions":[{"slot":2,"template":"custom"}]}""");
        var before = ModContentManifest.Compute(_directory);
        Assert.True(ModSupportProfile.TryLoad(_directory, out var inactive, out _));
        Assert.False(Assert.Single(inactive.Mods).Active);
        Assert.Empty(before.Files);
        Directory.CreateDirectory(Path.Combine(_directory, "Override"));
        File.WriteAllText(Path.Combine(_directory, "Override", "custom.utc"), "creature");
        Assert.True(ModSupportProfile.TryLoad(_directory, out inactive, out _));
        Assert.False(Assert.Single(inactive.Mods).Active);
        Directory.CreateDirectory(Path.Combine(_directory, "modules"));
        File.WriteAllText(Path.Combine(_directory, "modules", "custom.mod"), "module");
        Assert.True(ModSupportProfile.TryLoad(_directory, out var active, out _));
        Assert.True(Assert.Single(active.Mods).Active);
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(2, "tar_m02aa", active, out var template));
        Assert.Equal("custom", template);
        Assert.Contains(ModContentManifest.Compute(_directory).Files, file => file.Path == "coop-mods/npc.json" && file.Gameplay);
    }

    [Fact]
    public void ConflictingActiveModsAreReportedInsteadOfSelectingByFileOrder()
    {
        string catalog = Path.Combine(_directory, ModSupportProfile.CatalogDirectory);
        Directory.CreateDirectory(catalog);
        Directory.CreateDirectory(Path.Combine(_directory, "Override"));
        File.WriteAllText(Path.Combine(_directory, "Override", "marker.utc"), "marker");
        foreach (string id in new[] { "a", "b" })
            File.WriteAllText(Path.Combine(catalog, id + ".json"),
                $$$"""{"schema":2,"id":"{{{id}}}","detect":{"allFiles":["Override/marker.utc"]},"companions":[{"slot":2,"template":"{{{id}}}"}]}""");
        Assert.False(ModSupportProfile.TryLoad(_directory, out _, out string error));
        Assert.Contains("conflicting", error);
        Assert.False(ModContentManifest.Compute(_directory).Complete);
    }

    [Fact]
    public void StoryConditionsSelectShadowAndHoldUnknownOrLaterIdentities()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "Override"));
        File.WriteAllText(Path.Combine(_directory, "Override", "p_channa.utc"), "marker");
        var profile = Load("""{"schema":2,"id":"shadow","detect":{"allFiles":["Override/p_channa.utc"]},"companions":[{"slot":0,"template":"p_channa","when":{"global":"Tar_ZelkaRm","min":30,"max":30}},{"slot":0,"recovery":"hold","when":{"global":"Tar_ZelkaRm","min":31}}]}""");
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(0, "ebo_m12aa", profile, out _));
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(0, "ebo_m12aa", profile, out var template, _ => 2));
        Assert.Equal("p_bastilla", template);
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(0, "ebo_m12aa", profile, out template, _ => 30));
        Assert.Equal("p_channa", template);
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(0, "ebo_m12aa", profile, out _, _ => 69));
    }

    [Theory]
    [InlineData("../external.utc")]
    [InlineData("Override/../external.utc")]
    [InlineData("C:/external.utc")]
    public void ModDetectionCannotReadOutsideTheGame(string marker)
    {
        File.WriteAllText(Path.Combine(_directory, ModSupportProfile.FileName),
            $$$"""{"schema":2,"id":"bad","detect":{"allFiles":["{{{marker}}}"]}}""");
        Assert.False(ModSupportProfile.TryLoad(_directory, out _, out _));
    }

    [Fact]
    public void CheckedInSolomonRulesHoldRosterBeforeTheStoryUpdateArrives()
    {
        string catalog = Path.Combine(_directory, ModSupportProfile.CatalogDirectory);
        Directory.CreateDirectory(catalog);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ModProfiles", "brotherhood-of-shadow-solomons-revenge.json"),
            Path.Combine(catalog, "solomon.json"));
        Assert.True(ModSupportProfile.TryLoad(_directory, out var profile, out _));
        Assert.False(Assert.Single(profile.Mods).Active);
        Directory.CreateDirectory(Path.Combine(_directory, "modules"));
        Directory.CreateDirectory(Path.Combine(_directory, "Override"));
        File.WriteAllText(Path.Combine(_directory, "modules", "bos_orion.mod"), "marker");
        File.WriteAllText(Path.Combine(_directory, "Override", "p_channa.utc"), "marker");
        Assert.True(ModSupportProfile.TryLoad(_directory, out profile, out string error), error);
        Assert.True(Assert.Single(profile.Mods).Active);
        // Roster received first: a stale vanilla story value must not recruit Bastila.
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(0, "korr_m33aa", profile, out _, _ => 2));
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(0, "korr_m33aa", profile, out var template, _ => 30));
        Assert.Equal("p_channa", template);
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(0, "korr_m33aa", profile, out _, _ => 69));
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(0, "bos_orion", profile, out _, _ => 30));
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(0, "ship6", profile, out _, _ => 30));
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(0, "vision3", profile, out _, _ => 30));
        Assert.False(PartyRosterTemplateTable.TryGetTemplate(2, "bos_msp1", profile, out _, _ => 69));
        Assert.True(PartyRosterTemplateTable.TryGetTemplate(8, "korr_m33aa", profile, out template, _ => 2));
        Assert.Equal("p_zaalbar", template);
    }

    [Theory]
    [InlineData("super-skip-taris", "skip_spire.ncs", "ls_skip_taris.ncs", "ds_skip_taris.ncs")]
    [InlineData("jedi-from-the-start", "jfts_a_cjedic.ncs", "jfts_a_cjedig.ncs", "jfts_a_cjedis.ncs")]
    public void CheckedInStorySkipProfilesAreNamedOnlyWhenAllTheirScriptsArePresent(
        string id, string first, string second, string third)
    {
        string catalog = Path.Combine(_directory, ModSupportProfile.CatalogDirectory);
        Directory.CreateDirectory(catalog);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "ModProfiles", id + ".json"),
            Path.Combine(catalog, id + ".json"));
        Directory.CreateDirectory(Path.Combine(_directory, "Override"));
        File.WriteAllText(Path.Combine(_directory, "Override", first), "marker");
        Assert.True(ModSupportProfile.TryLoad(_directory, out var profile, out string error), error);
        Assert.False(Assert.Single(profile.Mods).Active);
        File.WriteAllText(Path.Combine(_directory, "Override", second), "marker");
        File.WriteAllText(Path.Combine(_directory, "Override", third), "marker");
        Assert.True(ModSupportProfile.TryLoad(_directory, out profile, out error), error);
        Assert.Equal(id, Assert.Single(profile.Mods).Id);
        Assert.True(profile.Mods[0].Active);
        Assert.Equal(id, profile.Id);
        // No companion or campaign-module rule: a stock slot stays unclaimed.
        Assert.Equal(ModSupportProfile.Resolution.Unclaimed,
            profile.Resolve(0, "end_m01aa", out _));
    }

    [Fact]
    public void MisspelledCompatibilityFixFieldsAreReported()
    {
        File.WriteAllText(Path.Combine(_directory, ModSupportProfile.FileName),
            """{"schema":2,"id":"typo","detect":{"allFiles":["Override/marker.utc"]},"compannions":[]}""");
        Assert.False(ModSupportProfile.TryLoad(_directory, out _, out string error));
        Assert.Contains("compannions", error);
    }

    private ModSupportProfile Load(string json)
    {
        File.WriteAllText(Path.Combine(_directory, ModSupportProfile.FileName), json);
        Assert.True(ModSupportProfile.TryLoad(_directory, out var profile, out string error), error);
        return profile;
    }
}

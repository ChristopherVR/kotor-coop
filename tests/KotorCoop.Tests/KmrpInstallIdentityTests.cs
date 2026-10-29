using KotorCoop.Memory;
using KotorCoop.Protocol;
using Xunit;

namespace KotorCoop.Tests;

public sealed class KmrpInstallIdentityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "kc-kmrp-" + Guid.NewGuid().ToString("N"));

    internal static string CreateInstall(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "patches"));
        Directory.CreateDirectory(Path.Combine(root, "configs"));
        foreach (string file in new[] { "swkotor.exe", "KotorPatcher.dll", "binkw32.dll", "binkw32Hooked.dll",
                     "dinput8.dll", "patches/kmrp.dll", "patches/kmrp-controller.dll" })
            File.WriteAllText(Path.Combine(root, file), "fixture content for " + file);
        File.WriteAllText(Path.Combine(root, "patch_config.toml"), """
            target_version_sha = "9C10E0450A6EECA417E036E3CDE7474FED1F0A92AAB018446D156944DEA91435"

            [[patches]]
            id = "kmrp"
            dll = "patches/kmrp.dll"
            [[patches.hooks]]
            address = 0x0040CE76
            type = "detour"
            function = "TestHook"
            original_bytes = [0x55, 0x8B, 0xEC, 0x83, 0xEC, 0x10]

            [[patches]]
            id = "kmrp-controller"
            dll = "patches/kmrp-controller.dll"
            """);
        File.WriteAllText(Path.Combine(root, "configs/kmrp.ini"), "[Patch Options]\nmap-notes=1\ndebug-logs=0\n");
        File.WriteAllText(Path.Combine(root, "configs/kmrp-controller.ini"), "[Patch Options]\ndebug-logs=0\n");
        return root;
    }

    private string Install(string name) => CreateInstall(Path.Combine(_root, name));
    private static string Identity(string root) => KmrpInstallIdentity.Compute(root, Path.Combine(root, "swkotor.exe"));

    [Fact]
    public void MatchingInstallsAtDifferentPathsAdmitDifferentLiveImages()
    {
        string a = Install("host"), b = Install("client");
        File.WriteAllText(Path.Combine(a, "swkotor.ini"), "Width=1920\nHeight=1080");
        File.WriteAllText(Path.Combine(b, "swkotor.ini"), "Width=2560\nHeight=1440");
        File.WriteAllText(Path.Combine(b, "kmrp-kpm.log"), "different runtime addresses");
        File.SetLastWriteTimeUtc(Path.Combine(b, "patches/kmrp.dll"), DateTime.UtcNow.AddDays(-10));
        // The same E9 detour at the same game address points into two different runtime
        // allocations. Those operand bytes are included in the plugin's raw .text hash.
        uint hostImage = ExeImageDigestRule.HashText(123, [0xE9, 0x10, 0x20, 0x30, 0x00, 0x90]);
        uint clientImage = ExeImageDigestRule.HashText(123, [0xE9, 0x10, 0x20, 0x40, 0x00, 0x90]);
        ContentDigest host = InstallDigest.Compute(a, Path.Combine(a, "swkotor.exe"), hostImage);
        ContentDigest client = InstallDigest.Compute(b, Path.Combine(b, "swkotor.exe"), clientImage);
        Assert.Matches("^[A-F0-9]{64}$", host.KmrpInstallDigest!);
        Assert.Equal(host.KmrpInstallDigest, client.KmrpInstallDigest);
        Assert.NotEqual(host.ExeDigest, client.ExeDigest);
        CompatibilityVerdict verdict = ContentCompatibility.Compare(host, client);
        Assert.True(verdict.Admitted);
        Assert.Equal(SessionCapabilities.All, verdict.Allowed);
        Assert.Contains(verdict.Notes, n => n.Tag == "W_KMRP");
    }

    [Theory]
    [InlineData("swkotor.exe")]
    [InlineData("KotorPatcher.dll")]
    [InlineData("binkw32.dll")]
    [InlineData("dinput8.dll")]
    [InlineData("patches/kmrp.dll")]
    [InlineData("patches/kmrp-controller.dll")]
    [InlineData("configs/kmrp.ini")]
    [InlineData("configs/kmrp-controller.ini")]
    [InlineData("patch_config.toml")]
    public void SameSizeEditWithRestoredTimestampCannotMatch(string file)
    {
        string a = Install("host"), b = Install("client");
        string path = Path.Combine(b, file);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        byte[] bytes = File.ReadAllBytes(path);
        // Change an option/value, or payload, without changing file size.
        bytes[^1] ^= 1;
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, stamp);
        Assert.NotEqual(Identity(a), Identity(b));
        var host = InstallDigest.Compute(a, Path.Combine(a, "swkotor.exe"), 1);
        var client = InstallDigest.Compute(b, Path.Combine(b, "swkotor.exe"), 2);
        Assert.Equal(RefusalReason.Executable, ContentCompatibility.Compare(host, client).Refusal);
    }

    [Theory]
    [InlineData("swkotor.exe")]
    [InlineData("KotorPatcher.dll")]
    [InlineData("binkw32.dll")]
    [InlineData("binkw32Hooked.dll")]
    [InlineData("patches/kmrp.dll")]
    [InlineData("patches/kmrp-controller.dll")]
    [InlineData("configs/kmrp.ini")]
    [InlineData("configs/kmrp-controller.ini")]
    public void MissingRequiredInputDisablesFallback(string file)
    {
        string root = Install("game");
        File.Delete(Path.Combine(root, file));
        Assert.Empty(Identity(root));
    }

    [Fact]
    public void UnreadableInputDisablesFallback()
    {
        string root = Install("game");
        using var held = new FileStream(Path.Combine(root, "patches/kmrp.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Empty(Identity(root));
    }

    [Theory]
    [InlineData("../outside.dll")]
    [InlineData("C:/mods/kmrp.dll")]
    [InlineData("patches/missing.dll")]
    public void UnsupportedModulePathsDoNotGetAnIdentity(string module)
    {
        string root = Install("game");
        string path = Path.Combine(root, "patch_config.toml");
        File.WriteAllText(path, File.ReadAllText(path).Replace("patches/kmrp.dll", module, StringComparison.Ordinal));
        Assert.Empty(Identity(root));
    }

    [Fact]
    public void AdditionalRuntimeFilesAndChainPayloadsAreCovered()
    {
        string root = Install("game");
        string before = Identity(root);
        File.WriteAllText(Path.Combine(root, "other.asi"), "extra patch");
        string after = Identity(root);
        Assert.NotEqual(before, after);
        File.WriteAllText(Path.Combine(root, "dinput8.chain"), "# chain\ndinput8.prev.dll\n");
        Assert.Empty(Identity(root));
        File.WriteAllText(Path.Combine(root, "dinput8.prev.dll"), "driver fix");
        Assert.NotEmpty(Identity(root));
        Assert.NotEqual(after, Identity(root));
        File.WriteAllText(Path.Combine(root, "dinput8.chain"), "../external.dll");
        Assert.Empty(Identity(root));
    }

    [Fact]
    public void RootPatchSettingsAreCoveredButCoopSettingsAreNegotiatedSeparately()
    {
        string a = Install("host"), b = Install("client");
        File.WriteAllText(Path.Combine(a, "coop.ini"), "mode=host");
        File.WriteAllText(Path.Combine(b, "coop.ini"), "mode=client");
        Assert.Equal(Identity(a), Identity(b));
        File.WriteAllText(Path.Combine(b, "kmrp-controller.ini"), "[Hud]\nStyle=PC");
        Assert.NotEqual(Identity(a), Identity(b));
    }

    [Fact]
    public void EmptyExecutableOrDifferentProcessExecutableDisablesFallback()
    {
        string root = Install("game");
        string other = Path.Combine(root, "other.exe");
        File.WriteAllText(other, "different process");
        Assert.Empty(KmrpInstallIdentity.Compute(root, other));
        File.WriteAllText(Path.Combine(root, "swkotor.exe"), "");
        Assert.Empty(Identity(root));
    }

    [Fact]
    public void AbsentKmrpOrLiveImageCannotSupplyFallback()
    {
        Assert.Empty(Identity(Path.Combine(_root, "absent")));
        string root = Install("game");
        Assert.Null(InstallDigest.Compute(root, Path.Combine(root, "swkotor.exe")).KmrpInstallDigest);
        File.Delete(Path.Combine(root, "patch_config.toml"));
        Assert.Empty(Identity(root));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

using System.Text;
using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="PackageInstaller"/> and <see cref="ForeignDinputIdentity"/> against synthetic
/// package/game directories — no real build output is needed, only files carrying the same
/// byte markers the real ones do (the plugin's UTF-16 banner, the loader's ASCII export
/// symbol, and the strings a few well-known foreign dinput8.dll mods carry). Covers the
/// GOAL in the chain-mode task: a foreign dinput8.dll is auto-chained without any flag, a
/// plain copy of Windows' own dinput8.dll is just overwritten, re-installing is idempotent
/// in both modes, uninstall restores the foreign DLL, and <c>--check</c> reports PASS/FAIL.
/// </summary>
public sealed class PackageInstallerTests : IDisposable
{
    // Same literals PackageInstaller.Identify searches for (see its own doc comment).
    private static readonly byte[] PluginMarkerBytes = Encoding.Unicode.GetBytes("=== KotorCoop plugin loaded");
    private static readonly byte[] LoaderMarkerBytes = Encoding.ASCII.GetBytes("KotorCoop.Loader chain dinput8");

    private readonly List<string> _tempDirectories = [];
    private readonly List<string> _log = [];

    public void Dispose()
    {
        foreach (string directory in _tempDirectories)
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup; a leaked temp directory must not fail the suite.
            }
        }
    }

    private void Log(string line) => _log.Add(line);

    private string NewRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "kcoop-pkg-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _tempDirectories.Add(root);
        return root;
    }

    /// <summary>A fake extracted package: real byte markers for our two DLLs, junk everywhere
    /// else (nothing PackageInstaller.Install reads the content of besides the markers).</summary>
    private static string CreatePackage(string root)
    {
        string package = Path.Combine(root, "package");
        Directory.CreateDirectory(Path.Combine(package, "chain-loader"));
        Directory.CreateDirectory(Path.Combine(package, "Override"));

        File.WriteAllBytes(Path.Combine(package, PackageInstaller.PluginFileName), Pad(PluginMarkerBytes));
        File.WriteAllBytes(Path.Combine(package, PackageInstaller.LoaderRelativePath), Pad(LoaderMarkerBytes));
        File.WriteAllText(Path.Combine(package, "KotorCoop.Agent.exe"), "fake agent exe");
        File.WriteAllText(Path.Combine(package, "KotorCoop.Session.dll"), "fake session dll");
        File.WriteAllText(Path.Combine(package, "Override", "k_coop_test.2da"), "fake override asset");
        File.WriteAllText(Path.Combine(package, "coop.ini"), "; fake coop.ini\nname=test\n");
        return package;
    }

    private static string CreateGameDirectory(string root, string name = "game")
    {
        string game = Path.Combine(root, name);
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(game, PackageInstaller.GameExecutable), "fake exe");
        return game;
    }

    private static byte[] Pad(byte[] marker) => [.. Encoding.ASCII.GetBytes("junk-prefix-"), .. marker, .. Encoding.ASCII.GetBytes("-junk-suffix")];

    private static void WriteForeignDinput(string gameDir, byte[] content) =>
        File.WriteAllBytes(Path.Combine(gameDir, PackageInstaller.PluginFileName), content);

    // --- fake foreign dinput8.dll content, matching the strings ForeignDinputIdentity looks for ---

    private static byte[] ReShadeDll() => Encoding.ASCII.GetBytes("junk ReShadeVersion junk ReShade junk binary content");

    private static byte[] UltimateAsiLoaderDll() => Encoding.ASCII.GetBytes("junk Ultimate ASI Loader junk binary content");

    private static byte[] MicrosoftSystemDll() => Encoding.Unicode.GetBytes("junk CompanyName junk Microsoft Corporation junk FileDescription junk");

    private static byte[] UnrecognisedModDll() => Encoding.ASCII.GetBytes("some entirely unrecognised foreign dinput8 mod binary, no known markers here");

    // ---------------------------------------------------------------------------------------
    // ForeignDinputIdentity: pure, no disk involved.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void IdentifiesReShadeByItsMarker()
    {
        ForeignDinputIdentity.Identity identity = ForeignDinputIdentity.Identify(ReShadeDll(), fileDescription: null);

        Assert.Equal(ForeignDinputIdentity.Kind.Mod, identity.Kind);
        Assert.Equal("ReShade", identity.FriendlyName);
    }

    [Fact]
    public void IdentifiesUltimateAsiLoaderByItsMarker()
    {
        ForeignDinputIdentity.Identity identity = ForeignDinputIdentity.Identify(UltimateAsiLoaderDll(), fileDescription: null);

        Assert.Equal(ForeignDinputIdentity.Kind.Mod, identity.Kind);
        Assert.Equal("Ultimate ASI Loader", identity.FriendlyName);
    }

    [Fact]
    public void IdentifiesAMicrosoftCompanyStringAsTheSystemFile()
    {
        ForeignDinputIdentity.Identity identity = ForeignDinputIdentity.Identify(MicrosoftSystemDll(), fileDescription: null);

        Assert.Equal(ForeignDinputIdentity.Kind.SystemFile, identity.Kind);
    }

    [Fact]
    public void FallsBackToAGenericNameWithNoFileDescription()
    {
        ForeignDinputIdentity.Identity identity = ForeignDinputIdentity.Identify(UnrecognisedModDll(), fileDescription: null);

        Assert.Equal(ForeignDinputIdentity.Kind.Mod, identity.Kind);
        Assert.Equal("another dinput8 mod", identity.FriendlyName);
    }

    [Fact]
    public void FallsBackToTheFileDescriptionWhenOneWasRead()
    {
        ForeignDinputIdentity.Identity identity =
            ForeignDinputIdentity.Identify(UnrecognisedModDll(), fileDescription: "Widescreen Fixer");

        Assert.Equal("another dinput8 mod (Widescreen Fixer)", identity.FriendlyName);
    }

    // ---------------------------------------------------------------------------------------
    // Plain install: no dinput8.dll present yet.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void PlainInstallPlacesThePluginDirectlyAndPassesTheCheck()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);

        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Equal(PackageInstaller.DinputKind.Plugin, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.ChainFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.ChainPluginFileName)));
        Assert.True(File.Exists(Path.Combine(game, "coop.ini")));
        Assert.True(File.Exists(Path.Combine(game, "KotorCoop.Agent.exe")));

        // The install's own end-of-run check must have reported success too.
        Assert.Contains(_log, l => l.StartsWith("layout: plain"));
        Assert.DoesNotContain(_log, l => l.StartsWith("FAIL"));
    }

    [Fact]
    public void CoopIniIsNeverOverwrittenOnANoFlagReinstall()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);

        PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        string iniPath = Path.Combine(game, "coop.ini");
        File.WriteAllText(iniPath, "name=my-tuned-name\npassword=secret\n");

        _log.Clear();
        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Equal("name=my-tuned-name\npassword=secret\n", File.ReadAllText(iniPath));
    }

    // ---------------------------------------------------------------------------------------
    // Auto-chain: a foreign, non-Microsoft dinput8.dll with no --chain/--force given.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void InstallOverAForeignDllAutoChainsWithNoFlags()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, UltimateAsiLoaderDll());

        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));

        // The loader now owns dinput8.dll; the plugin is KotorCoop.Plugin.dll; the foreign
        // DLL was kept, not deleted.
        Assert.Equal(PackageInstaller.DinputKind.Loader, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.Equal(PackageInstaller.DinputKind.Plugin, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.ChainPluginFileName)));
        Assert.True(File.Exists(Path.Combine(game, PackageInstaller.PreviousFileName)));

        string chain = File.ReadAllText(Path.Combine(game, PackageInstaller.ChainFileName));
        Assert.Contains(PackageInstaller.ChainPluginFileName, chain);
        Assert.Contains(PackageInstaller.PreviousFileName, chain);

        // The message names the mod and explains the outcome in one line, with no --chain
        // needed from the caller.
        Assert.Contains(_log, l => l.Contains("Ultimate ASI Loader") && l.Contains("installing as a chain"));
        Assert.Contains(_log, l => l.StartsWith("layout: chain"));
        Assert.DoesNotContain(_log, l => l.StartsWith("FAIL"));
    }

    [Fact]
    public void InstallOverReShadeNamesItInTheSummary()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());

        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Contains(_log, l => l.Contains("ReShade") && l.Contains("installing as a chain"));
    }

    [Fact]
    public void ForceOverwritesAForeignDllInsteadOfChaining()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());

        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game, Force: true), Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Equal(PackageInstaller.DinputKind.Plugin, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.PreviousFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.ChainFileName)));
    }

    // ---------------------------------------------------------------------------------------
    // A plain copy of Windows' own dinput8.dll: not a mod, just overwritten, no chain.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void InstallOverAMicrosoftCopyOverwritesDirectlyWithNoChain()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, MicrosoftSystemDll());

        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Equal(PackageInstaller.DinputKind.Plugin, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.PreviousFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.ChainFileName)));
        Assert.Contains(_log, l => l.Contains("Microsoft Corporation") && l.Contains("not a mod"));
    }

    // ---------------------------------------------------------------------------------------
    // Idempotence: re-running install over each mode must not break or duplicate anything.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ReinstallingPlainModeStaysPlainAndKeepsWorking()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);

        PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);
        _log.Clear();
        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Equal(PackageInstaller.DinputKind.Plugin, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
    }

    [Fact]
    public void ReinstallingOverAnExistingChainInstallWithNoFlagsStaysChainedAndUpgradesInPlace()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());

        // First install: chains automatically.
        PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);
        Assert.True(File.Exists(Path.Combine(game, PackageInstaller.PreviousFileName)));

        // Second install, no flags at all - must NOT fall back to plain mode (which would
        // overwrite the loader with the bare plugin and orphan the chain).
        _log.Clear();
        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Equal(PackageInstaller.DinputKind.Loader, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.Equal(PackageInstaller.DinputKind.Plugin, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.ChainPluginFileName)));

        // dinput8.prev.dll was renamed exactly once - it still holds ReShade's own content,
        // not something re-renamed or clobbered on the second pass.
        byte[] previousContent = File.ReadAllBytes(Path.Combine(game, PackageInstaller.PreviousFileName));
        Assert.Equal(ReShadeDll(), previousContent);

        // dinput8.chain lists the plugin and the previous mod exactly once each.
        string[] entries = ChainEntries(game);
        Assert.Single(entries, e => e.Equals(PackageInstaller.ChainPluginFileName, StringComparison.OrdinalIgnoreCase));
        Assert.Single(entries, e => e.Equals(PackageInstaller.PreviousFileName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ReinstallingAChainInstallKeepsAPlayerAddedChainEntry()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());
        File.WriteAllText(Path.Combine(game, "extra-mod.asi"), "not ours, must survive");

        PackageInstaller.Install(new PackageInstaller.Options(package, game, Chain: true, Load: ["extra-mod.asi"]), Log);
        Assert.Contains("extra-mod.asi", ChainEntries(game));

        // Re-run with NO --load this time: the player's earlier extra entry must survive,
        // not be silently dropped because this call didn't repeat it.
        _log.Clear();
        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));
        string[] entries = ChainEntries(game);
        Assert.Contains("extra-mod.asi", entries);
        Assert.Single(entries, e => e.Equals("extra-mod.asi", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] ChainEntries(string game) =>
        File.ReadAllLines(Path.Combine(game, PackageInstaller.ChainFileName))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && l[0] != '#' && l[0] != ';')
            .ToArray();

    // ---------------------------------------------------------------------------------------
    // .asi mods beside a chained loader are mentioned, never touched.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void AsiModsAreMentionedAndLeftInPlaceWhenChaining()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, UltimateAsiLoaderDll());
        File.WriteAllText(Path.Combine(game, "SomeAsiMod.asi"), "fake asi content");
        Directory.CreateDirectory(Path.Combine(game, "scripts"));
        File.WriteAllText(Path.Combine(game, "scripts", "AnotherMod.asi"), "fake asi content");

        bool ok = PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.True(File.Exists(Path.Combine(game, "SomeAsiMod.asi")));
        Assert.True(File.Exists(Path.Combine(game, "scripts", "AnotherMod.asi")));
        Assert.Contains(_log, l => l.Contains(".asi mod") && l.Contains("Ultimate ASI Loader"));
    }

    // ---------------------------------------------------------------------------------------
    // Uninstall restores the foreign DLL and removes only our own files.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void UninstallRestoresTheForeignDllAndRemovesOnlyOurFiles()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());
        File.WriteAllText(Path.Combine(game, "SomeAsiMod.asi"), "not ours, must survive");

        PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        _log.Clear();
        PackageInstaller.Uninstall(game, Log);

        Assert.Equal(PackageInstaller.DinputKind.Foreign, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.Equal(ReShadeDll(), File.ReadAllBytes(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.PreviousFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.ChainFileName)));
        Assert.False(File.Exists(Path.Combine(game, PackageInstaller.ChainPluginFileName)));
        Assert.False(File.Exists(Path.Combine(game, "KotorCoop.Agent.exe")));
        Assert.True(File.Exists(Path.Combine(game, "coop.ini")));
        Assert.True(File.Exists(Path.Combine(game, "SomeAsiMod.asi")));
    }

    [Fact]
    public void UninstallRestoresTheForeignDllEvenWhenThePlayerAddedChainLines()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());

        PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        // The player hand-edits dinput8.chain afterwards to add a DLL of their own.
        string chainFile = Path.Combine(game, PackageInstaller.ChainFileName);
        File.AppendAllLines(chainFile, ["PlayerAdded.dll"]);
        File.WriteAllText(Path.Combine(game, "PlayerAdded.dll"), "not ours");

        _log.Clear();
        PackageInstaller.Uninstall(game, Log);

        Assert.Equal(PackageInstaller.DinputKind.Foreign, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.False(File.Exists(chainFile));
        // A DLL the player added is not KotorCoop.* and is not deleted, even though it was
        // only ever reachable through the (now-removed) chain file.
        Assert.True(File.Exists(Path.Combine(game, "PlayerAdded.dll")));
    }

    [Fact]
    public void UninstallOfAPlainInstallLeavesNoForeignFileToRestore()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);

        PackageInstaller.Install(new PackageInstaller.Options(package, game), Log);

        _log.Clear();
        PackageInstaller.Uninstall(game, Log);

        Assert.Equal(PackageInstaller.DinputKind.None, PackageInstaller.Identify(Path.Combine(game, PackageInstaller.PluginFileName)));
        Assert.True(File.Exists(Path.Combine(game, "coop.ini")));
    }

    // ---------------------------------------------------------------------------------------
    // --check: reports without changing anything.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void CheckOnAFreshGameDirectoryReportsNoneAndFails()
    {
        string root = NewRoot();
        string game = CreateGameDirectory(root);

        bool ok = PackageInstaller.Check(game, Log);

        Assert.False(ok);
        Assert.Contains(_log, l => l.StartsWith("layout: none"));
        Assert.Contains(_log, l => l.StartsWith("FAIL"));
        // Nothing was created by a check.
        Assert.False(File.Exists(Path.Combine(game, "coop.ini")));
    }

    [Fact]
    public void CheckOnAPlainInstallPassesEverything()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        PackageInstaller.Install(new PackageInstaller.Options(package, game), _ => { });

        _log.Clear();
        bool ok = PackageInstaller.Check(game, Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Contains(_log, l => l.StartsWith("layout: plain"));
        Assert.DoesNotContain(_log, l => l.StartsWith("FAIL"));
    }

    [Fact]
    public void CheckOnAChainInstallVerifiesEveryChainEntry()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());
        PackageInstaller.Install(new PackageInstaller.Options(package, game), _ => { });

        _log.Clear();
        bool ok = PackageInstaller.Check(game, Log);

        Assert.True(ok, string.Join('\n', _log));
        Assert.Contains(_log, l => l.StartsWith("layout: chain"));
        Assert.Contains(_log, l => l.Contains(PackageInstaller.PreviousFileName) && l.StartsWith("PASS"));
        Assert.DoesNotContain(_log, l => l.StartsWith("FAIL"));
    }

    [Fact]
    public void CheckFailsWhenAChainEntryIsMissingFromDisk()
    {
        string root = NewRoot();
        string package = CreatePackage(root);
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());
        PackageInstaller.Install(new PackageInstaller.Options(package, game), _ => { });

        // Simulate the kept mod having been deleted out from under the chain by hand.
        File.Delete(Path.Combine(game, PackageInstaller.PreviousFileName));

        _log.Clear();
        bool ok = PackageInstaller.Check(game, Log);

        Assert.False(ok);
        Assert.Contains(_log, l => l.StartsWith("FAIL") && l.Contains(PackageInstaller.PreviousFileName));
    }

    [Fact]
    public void CheckReportsForeignOnlyWhenAnotherModOwnsTheSlotAndKotorCoopIsNotInstalled()
    {
        string root = NewRoot();
        string game = CreateGameDirectory(root);
        WriteForeignDinput(game, ReShadeDll());

        bool ok = PackageInstaller.Check(game, Log);

        Assert.False(ok);
        Assert.Contains(_log, l => l.StartsWith("layout: foreign-only"));
    }
}

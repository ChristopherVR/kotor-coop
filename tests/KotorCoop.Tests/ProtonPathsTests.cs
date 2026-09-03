using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// <see cref="ProtonPaths"/> against fake environment/filesystem delegates — there is no
/// Linux/Wine box in this environment (see MEMORY.md's "Autonomous live testing" note; this
/// whole feature is NEEDS-LIVE), so every case here is exactly what makes the class worth
/// writing this way: pure functions over injected env/filesystem stand-ins instead of
/// <see cref="Environment"/>/<see cref="Directory"/> directly.
/// </summary>
public sealed class ProtonPathsTests
{
    // ---------------------------------------------------------------------------------------
    // ToWinePath: POSIX -> Z:\, Windows passthrough, null/blank.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void ToWinePathConvertsAPosixPathToWineForm()
    {
        string? wine = ProtonPaths.ToWinePath("/home/deck/.local/share/Steam/steamapps/common/swkotor");

        Assert.Equal(@"Z:\home\deck\.local\share\Steam\steamapps\common\swkotor", wine);
    }

    [Fact]
    public void ToWinePathConvertsAVdfStyleSdCardPath()
    {
        // Exactly the shape libraryfolders.vdf carries on a Linux Steam client: a plain POSIX
        // path with no escaping, unlike a Windows client's "C:\\Games" (backslash-escaped).
        string? wine = ProtonPaths.ToWinePath("/run/media/mmcblk0p1");

        Assert.Equal(@"Z:\run\media\mmcblk0p1", wine);
    }

    [Fact]
    public void ToWinePathPassesThroughAnAlreadyWindowsPath()
    {
        string? path = ProtonPaths.ToWinePath(@"D:\SteamLibrary");

        Assert.Equal(@"D:\SteamLibrary", path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ToWinePathReturnsNullForNullOrBlankInput(string? input)
    {
        Assert.Null(ProtonPaths.ToWinePath(input));
    }

    // ---------------------------------------------------------------------------------------
    // SteamCompatInstallPath: the one Proton env var that actually names a game directory.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void SteamCompatInstallPathConvertsTheProtonEnvVar()
    {
        string? path = ProtonPaths.SteamCompatInstallPath(name => name switch
        {
            ProtonPaths.EnvSteamCompatInstallPath => "/home/deck/.local/share/Steam/steamapps/common/swkotor",
            _ => null,
        });

        Assert.Equal(@"Z:\home\deck\.local\share\Steam\steamapps\common\swkotor", path);
    }

    [Fact]
    public void SteamCompatInstallPathIsNullWhenTheEnvVarIsUnset()
    {
        string? path = ProtonPaths.SteamCompatInstallPath(_ => null);

        Assert.Null(path);
    }

    [Fact]
    public void SteamCompatInstallPathIgnoresCompatDataPathAndWineprefix()
    {
        // STEAM_COMPAT_DATA_PATH (the compatdata prefix) and WINEPREFIX never name the game
        // itself; ProtonPaths never even looks at them, so setting only those still yields null.
        string? path = ProtonPaths.SteamCompatInstallPath(name => name switch
        {
            "STEAM_COMPAT_DATA_PATH" => "/home/deck/.local/share/Steam/steamapps/compatdata/32370",
            "WINEPREFIX" => "/home/deck/.wine",
            _ => null,
        });

        Assert.Null(path);
    }

    // ---------------------------------------------------------------------------------------
    // A fake Wine filesystem: a case-insensitive set of directories that "exist", each mapped
    // to the subdirectories Directory.GetDirectories would report for it.
    // ---------------------------------------------------------------------------------------

    private sealed class FakeFileSystem
    {
        private readonly Dictionary<string, string[]> _directories =
            new(StringComparer.OrdinalIgnoreCase);

        public FakeFileSystem With(string directory, params string[] subdirectories)
        {
            _directories[directory] = subdirectories;
            return this;
        }

        public bool Exists(string path) => _directories.ContainsKey(path);

        public IEnumerable<string> Enumerate(string path) =>
            _directories.TryGetValue(path, out string[]? subs) ? subs : [];
    }

    // ---------------------------------------------------------------------------------------
    // HomeDirectories: Z:\home\* plus Z:\root.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void HomeDirectoriesListsEveryHomeUnderZHomePlusRoot()
    {
        FakeFileSystem fs = new FakeFileSystem()
            .With(@"Z:\home", @"Z:\home\deck")
            .With(@"Z:\root");

        List<string> homes = [.. ProtonPaths.HomeDirectories(fs.Exists, fs.Enumerate)];

        Assert.Equal([@"Z:\home\deck", @"Z:\root"], homes);
    }

    [Fact]
    public void HomeDirectoriesOmitsRootWhenItDoesNotExist()
    {
        FakeFileSystem fs = new FakeFileSystem().With(@"Z:\home", @"Z:\home\deck", @"Z:\home\alice");

        List<string> homes = [.. ProtonPaths.HomeDirectories(fs.Exists, fs.Enumerate)];

        Assert.Equal([@"Z:\home\deck", @"Z:\home\alice"], homes);
    }

    [Fact]
    public void HomeDirectoriesIsEmptyAndNeverEnumeratesWhenNothingExists()
    {
        List<string> homes =
        [
            .. ProtonPaths.HomeDirectories(
                directoryExists: _ => false,
                enumerateDirectories: _ => throw new InvalidOperationException("must not enumerate a directory that was never confirmed to exist")),
        ];

        Assert.Empty(homes);
    }

    // ---------------------------------------------------------------------------------------
    // SteamLibraryRoots: native/current/Flatpak client roots under each home, plus SD cards.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void SteamLibraryRootsFindsTheCurrentClientUnderAHome()
    {
        FakeFileSystem fs = new FakeFileSystem().With(@"Z:\home\deck\.local\share\Steam");

        List<string> roots = [.. ProtonPaths.SteamLibraryRoots([@"Z:\home\deck"], fs.Exists, fs.Enumerate)];

        Assert.Equal([@"Z:\home\deck\.local\share\Steam"], roots);
    }

    [Fact]
    public void SteamLibraryRootsFindsTheNativeAndFlatpakInstallsSeparately()
    {
        FakeFileSystem fs = new FakeFileSystem()
            .With(@"Z:\home\deck\.steam\steam")
            .With(@"Z:\home\deck\.var\app\com.valvesoftware.Steam\.local\share\Steam");

        List<string> roots = [.. ProtonPaths.SteamLibraryRoots([@"Z:\home\deck"], fs.Exists, fs.Enumerate)];

        Assert.Equal(
        [
            @"Z:\home\deck\.steam\steam",
            @"Z:\home\deck\.var\app\com.valvesoftware.Steam\.local\share\Steam",
        ],
            roots);
    }

    [Fact]
    public void SteamLibraryRootsFindsADirectlyMountedSdCard()
    {
        FakeFileSystem fs = new FakeFileSystem()
            .With(@"Z:\run\media", @"Z:\run\media\mmcblk0p1")
            .With(@"Z:\run\media\mmcblk0p1\steamapps");

        List<string> roots = [.. ProtonPaths.SteamLibraryRoots([], fs.Exists, fs.Enumerate)];

        Assert.Equal([@"Z:\run\media\mmcblk0p1"], roots);
    }

    [Fact]
    public void SteamLibraryRootsFindsAPerUserSdCard()
    {
        // The usual Steam Deck layout: /run/media/<user>/<card>/steamapps. The <user> level
        // itself has no steamapps of its own.
        FakeFileSystem fs = new FakeFileSystem()
            .With(@"Z:\run\media", @"Z:\run\media\deck")
            .With(@"Z:\run\media\deck", @"Z:\run\media\deck\SDCARD")
            .With(@"Z:\run\media\deck\SDCARD\steamapps");

        List<string> roots = [.. ProtonPaths.SteamLibraryRoots([], fs.Exists, fs.Enumerate)];

        Assert.Equal([@"Z:\run\media\deck\SDCARD"], roots);
    }

    [Fact]
    public void SteamLibraryRootsSkipsRunMediaEntirelyWhenAbsent()
    {
        List<string> roots =
        [
            .. ProtonPaths.SteamLibraryRoots(
                homeDirectories: [],
                directoryExists: _ => false,
                enumerateDirectories: _ => throw new InvalidOperationException("must not enumerate /run/media when it was never confirmed to exist")),
        ];

        Assert.Empty(roots);
    }

    // ---------------------------------------------------------------------------------------
    // HeroicLutrisRoots: deliberately modest — Games\Heroic and Games under each home.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void HeroicLutrisRootsYieldsBothConventionsPerHome()
    {
        List<string> roots = [.. ProtonPaths.HeroicLutrisRoots([@"Z:\home\deck"])];

        Assert.Equal([@"Z:\home\deck\Games\Heroic", @"Z:\home\deck\Games"], roots);
    }

    [Fact]
    public void HeroicLutrisRootsIsEmptyWithNoHomeDirectories()
    {
        List<string> roots = [.. ProtonPaths.HeroicLutrisRoots([])];

        Assert.Empty(roots);
    }

    // ---------------------------------------------------------------------------------------
    // Nothing here ever touches a real filesystem or environment, so nothing throws even when
    // every predicate is as negative as possible.
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void EveryEnumerationIsEmptyAndThrowsNothingWhenNothingExistsAnywhere()
    {
        static bool NoDirectoryExists(string _) => false;
        static IEnumerable<string> NoSubdirectories(string _) => [];

        IReadOnlyList<string> homes = [.. ProtonPaths.HomeDirectories(NoDirectoryExists, NoSubdirectories)];
        IReadOnlyList<string> libraries = [.. ProtonPaths.SteamLibraryRoots(homes, NoDirectoryExists, NoSubdirectories)];
        IReadOnlyList<string> heroic = [.. ProtonPaths.HeroicLutrisRoots(homes)];

        Assert.Empty(homes);
        Assert.Empty(libraries);
        Assert.Empty(heroic);
        Assert.Null(ProtonPaths.SteamCompatInstallPath(_ => null));
    }
}

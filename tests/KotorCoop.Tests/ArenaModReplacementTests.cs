using KotorCoop.Memory.GameFiles;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// A mod build that replaces the Taris cantina with its own modules\tar_m02ae.mod (Dueling Arena
/// Adjustment in the K1 Full build) changes what the game loads for the duel ring; the arena must
/// be made from that copy, the way the engine resolves it (override, the module's .mod, then its
/// rims, then the base archives).
/// </summary>
public sealed class ArenaModReplacementTests : IDisposable
{
    private readonly string _game = Directory.CreateTempSubdirectory("kcoop-arena-mod-").FullName;

    public ArenaModReplacementTests() => ArenaTestGame.WriteCantina(_game);

    public void Dispose()
    {
        try { Directory.Delete(_game, recursive: true); } catch (IOException) { }
    }

    private void WriteReplacementMod(byte[] are)
    {
        ArenaSources s = ArenaTestGame.Sources();
        File.WriteAllBytes(
            Path.Combine(_game, "modules", "tar_m02ae.mod"),
            ErfWriter.Build("MOD ", [("m02ae", 2012, are), ("m02ae", 2023, s.Git), ("module", 2014, s.Ifo), ("m02ae", 3003, s.Pth!)]));
    }

    [Fact]
    public void TheModulesModWinsOverItsRims()
    {
        byte[] modded = [.. ArenaTestGame.Sources().Are, 0x00];
        WriteReplacementMod(modded);

        using GameFiles files = new(_game);
        Assert.True(files.TryReadAreaResource("tar_m02ae", "m02ae", "are", out byte[] are));
        Assert.Equal(modded, are);
    }

    [Fact]
    public void WithoutAModTheRimsAreRead()
    {
        using GameFiles files = new(_game);
        Assert.True(files.TryReadAreaResource("tar_m02ae", "m02ae", "are", out byte[] are));
        Assert.Equal(ArenaTestGame.Sources().Are, are);
    }

    [Fact]
    public void AnOverrideFileStillWinsOverTheModule()
    {
        WriteReplacementMod([.. ArenaTestGame.Sources().Are, 0x00]);

        using GameFiles files = new(_game);
        Assert.True(files.TryReadAreaResource("tar_m02ae", "m02ae", "lyt", out byte[] lyt));
        Assert.Equal(ArenaTestGame.Sources().Lyt, lyt);
    }

    [Fact]
    public void TheArenaIsMadeFromTheReplacement()
    {
        Assert.True(ArenaModuleInstaller.TryGenerate(_game, out byte[] stock, out string error), error);

        WriteReplacementMod(ArenaTestGame.SourcesWithRoomAudio(12).Are);
        Assert.True(ArenaModuleInstaller.TryGenerate(_game, out byte[] modded, out error), error);

        Assert.NotEqual(stock, modded);
    }
}

using KotorCoop.Agent;
using KotorCoop.Session;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The console side of named saves: <c>save [name]</c> and <c>loadgame &lt;slot|name&gt;</c>.</summary>
public sealed class NamedSaveCommandTests : IDisposable
{
    private readonly string _saves = Path.Combine(Path.GetTempPath(), "kotorcoop-saves-" + Guid.NewGuid().ToString("N"));

    public NamedSaveCommandTests() => Directory.CreateDirectory(_saves);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_saves, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void ABareSaveIsStillTheEngineQuicksave()
    {
        CoopCommand command = ConsoleCommand.Parse("save");

        Assert.Equal(CoopCommandKind.Save, command.Kind);
        Assert.Null(command.Address);
    }

    [Fact]
    public void ASaveWithANameCarriesTheSanitisedName()
    {
        CoopCommand command = ConsoleCommand.Parse("save Manaan  rift/2");

        Assert.Equal(CoopCommandKind.Save, command.Kind);
        Assert.Equal("Manaan rift2", command.Address);
    }

    [Fact]
    public void ANameWithNothingUsableIsRefused()
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse("save ///").Kind);
    }

    [Fact]
    public void LoadGameStillTakesASlotNumber()
    {
        CoopCommand command = ConsoleCommand.Parse("loadgame 2");

        Assert.Equal(CoopCommandKind.LoadGame, command.Kind);
        Assert.Equal(2, command.Amount);
        Assert.Null(command.Address);
    }

    [Fact]
    public void LoadGameTakesANameToo()
    {
        CoopCommand command = ConsoleCommand.Parse("loadgame manaan rift");

        Assert.Equal(CoopCommandKind.LoadGame, command.Kind);
        Assert.Equal("manaan rift", command.Address);
    }

    [Theory]
    [InlineData("loadgame")]
    [InlineData("loadgame -1")]
    public void LoadGameNeedsSomething(string line)
    {
        Assert.Equal(CoopCommandKind.Invalid, ConsoleCommand.Parse(line).Kind);
    }

    private string MakeQuickSave()
    {
        string quick = Path.Combine(_saves, NamedSaveRule.QuickSaveFolder);
        Directory.CreateDirectory(quick);
        File.WriteAllText(Path.Combine(quick, "SAVEGAME.sav"), "world v1");
        File.WriteAllText(Path.Combine(quick, "savenfo.res"), "info");
        return quick;
    }

    [Fact]
    public void TheCopyGetsTheNextNumberAndEveryFile()
    {
        string quick = MakeQuickSave();
        Directory.CreateDirectory(Path.Combine(_saves, "000003 - Game1"));

        string target = CoopCommandDispatcher.CopyQuickSaveAs(_saves, quick, "manaan");

        Assert.Equal("000004 - manaan", Path.GetFileName(target));
        Assert.Equal("world v1", File.ReadAllText(Path.Combine(target, "SAVEGAME.sav")));
        Assert.Equal("info", File.ReadAllText(Path.Combine(target, "savenfo.res")));
    }

    [Fact]
    public void SavingUnderTheSameNameReplacesItInPlace()
    {
        string quick = MakeQuickSave();
        string first = CoopCommandDispatcher.CopyQuickSaveAs(_saves, quick, "manaan");

        File.WriteAllText(Path.Combine(quick, "SAVEGAME.sav"), "world v2");
        string second = CoopCommandDispatcher.CopyQuickSaveAs(_saves, quick, "MANAAN");

        Assert.Equal(first, second);
        Assert.Equal("world v2", File.ReadAllText(Path.Combine(second, "SAVEGAME.sav")));
        Assert.Single(Directory.GetDirectories(_saves), d => Path.GetFileName(d).Contains("manaan", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ANewCopyIsTheNewestSoItIsSlotZero()
    {
        string quick = MakeQuickSave();
        Directory.SetLastWriteTimeUtc(quick, DateTime.UtcNow.AddMinutes(-5));

        CoopCommandDispatcher.CopyQuickSaveAs(_saves, quick, "manaan");

        (string, DateTime)[] list =
        [
            .. new DirectoryInfo(_saves).GetDirectories().Select(d => (d.Name, d.LastWriteTimeUtc)),
        ];
        Assert.Equal(NamedSaveRule.LookupResult.Found, NamedSaveRule.FindSlot(list, "manaan", out int slot, out _));
        Assert.Equal(0, slot);
    }
}

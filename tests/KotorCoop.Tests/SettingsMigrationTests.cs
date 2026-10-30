using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "coop-defaults-" + Guid.NewGuid().ToString("N"));
    public SettingsMigrationTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void UsesIncomingDefaultsAndPreservesCustomValuesCommentsAndBackup()
    {
        string player = Path.Combine(_directory, "coop.ini");
        string template = Path.Combine(_directory, "defaults.ini");
        const string original = "[coop]\r\nMENU_BUTTON = 0 ; keep comment\r\njoin_mode=save\r\ncompanion_local_combat=0\r\nstore_sync=9\r\npassword=private\r\n";
        File.WriteAllText(player, original);
        File.WriteAllText(template, "menu_button=1\njoin_mode=auto\ncompanion_local_combat=0\nstore_sync=2\n");
        IReadOnlyList<SettingsMigration.Change> changes = SettingsMigration.Find(player, template);
        Assert.Equal(2, changes.Count);
        Assert.Equal(original, File.ReadAllText(player));
        string backup = SettingsMigration.Apply(player, changes);
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Equal(original.Replace("MENU_BUTTON = 0", "MENU_BUTTON = 1").Replace("join_mode=save", "join_mode=auto"), File.ReadAllText(player));
    }

    [Fact]
    public void ChangedSincePreviewIsPreservedAndSecondMigrationIsEmpty()
    {
        string player = Path.Combine(_directory, "coop.ini");
        string template = Path.Combine(_directory, "defaults.ini");
        File.WriteAllText(player, "menu_button=0\njoin_mode=save\n");
        File.WriteAllText(template, "menu_button=1\njoin_mode=auto\n");
        IReadOnlyList<SettingsMigration.Change> changes = SettingsMigration.Find(player, template);
        File.WriteAllText(player, "menu_button=7\njoin_mode=save\n");
        SettingsMigration.Apply(player, changes);
        Assert.Equal("menu_button=7\njoin_mode=auto\n", File.ReadAllText(player));
        Assert.Empty(SettingsMigration.Find(player, template));
    }

    [Fact]
    public void SupportAndMigrationOptionsRouteCorrectly()
    {
        Assert.Equal(AgentCommand.Support, AgentOptions.Parse(["support"]).Command);
        Assert.True(AgentOptions.Parse(["install", "--update-defaults"]).UpdateDefaults);
        Assert.False(AgentOptions.Parse(["install"]).UpdateDefaults);
        AgentOptions preview = AgentOptions.Parse(["install", "--preview-defaults", "--set", "menu_button=0"]);
        Assert.True(preview.PreviewDefaults);
        Assert.False(preview.UpdateDefaults);
        Assert.Single(preview.IniSettings);
    }

    [Fact]
    public void ExplicitWizardChoicesAreExcludedFromBothPreviewAndMigration()
    {
        string player = Path.Combine(_directory, "coop.ini");
        string template = Path.Combine(_directory, "defaults.ini");
        const string original = "menu_button=0\nstore_sync=0\njoin_mode=custom\npassword=secret\n";
        File.WriteAllText(player, original);
        File.WriteAllText(template, "menu_button=1\nstore_sync=2\njoin_mode=auto\n");
        IReadOnlyList<SettingsMigration.Change> changes = SettingsMigration.Find(player, template, ["MENU_BUTTON"]);
        SettingsMigration.Change change = Assert.Single(changes);
        Assert.Equal("store_sync", change.Key);
        Assert.Contains("Shared merchant stock: off -> shared mode", SettingsMigration.Describe(change));
        Assert.Equal(original, File.ReadAllText(player));
        string backup = SettingsMigration.Apply(player, changes);
        Assert.Equal(original, File.ReadAllText(backup));
        Assert.Equal(original.Replace("store_sync=0", "store_sync=2"), File.ReadAllText(player));
    }

    [Fact]
    public void LockedGameRefusesMigrationBeforeChangingOrBackingUpSettings()
    {
        string player = Path.Combine(_directory, "coop.ini");
        string executable = Path.Combine(_directory, "swkotor.exe");
        File.WriteAllText(player, "menu_button=0\n");
        File.WriteAllText(executable, "locked game image fixture");
        using var held = new FileStream(executable, FileMode.Open, FileAccess.Read, FileShare.Read);
        Assert.Throws<IOException>(() => SettingsMigration.Apply(player,
            [new SettingsMigration.Change("menu_button", "0", "1")]));
        Assert.Equal("menu_button=0\n", File.ReadAllText(player));
        Assert.Empty(Directory.GetFiles(_directory, "*.bak"));
    }
}

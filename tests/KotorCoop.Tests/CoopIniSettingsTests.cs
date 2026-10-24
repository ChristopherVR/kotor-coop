using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

public sealed class CoopIniSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "coop-ini-set-" + Guid.NewGuid().ToString("N"));
    public CoopIniSettingsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void ReplacesActiveValuesKeepingCommentsAndUncommentsExamples()
    {
        string path = Path.Combine(_directory, "coop.ini");
        File.WriteAllText(path, "; header\r\n[coop]\r\nport = 27140 ; the port\r\nname =\r\nserver_name = keep\r\n# auto_start_agent = 1\r\n");

        IReadOnlyList<CoopIniSettings.Change> changes = CoopIniSettings.Apply(path,
        [
            new("port", "27150"), new("name", "Bastila Fan"), new("auto_start_agent", "1"), new("menu_button", "0"),
        ]);

        Assert.Equal(4, changes.Count);
        Assert.Equal(
            "; header\r\n[coop]\r\nport = 27150 ; the port\r\nname = Bastila Fan\r\nserver_name = keep\r\nauto_start_agent = 1\r\nmenu_button = 0\r\n",
            File.ReadAllText(path));
        Assert.Equal("27140", changes[0].OldValue);
        Assert.Null(changes[3].OldValue);
        Assert.Equal("Bastila Fan", CoopIniSettings.Read(path, "name"));
        Assert.Equal("keep", CoopIniSettings.Read(path, "server_name"));
    }

    [Fact]
    public void UnchangedValuesLeaveTheFileAlone()
    {
        string path = Path.Combine(_directory, "coop.ini");
        File.WriteAllText(path, "[coop]\nagent_window = minimized\n");
        DateTime before = File.GetLastWriteTimeUtc(path);

        Assert.Empty(CoopIniSettings.Apply(path, [new("agent_window", "minimized")]));
        Assert.Equal(before, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public void AppendsInsideTheCoopSectionBeforeAnyLaterSection()
    {
        string path = Path.Combine(_directory, "coop.ini");
        File.WriteAllText(path, "[coop]\nport = 1\n\n[other]\nx = 1\n");

        CoopIniSettings.Apply(path, [new("steam_invites", "0")]);

        Assert.Equal("[coop]\nport = 1\nsteam_invites = 0\n\n[other]\nx = 1\n", File.ReadAllText(path));
    }

    [Theory]
    [InlineData("name", "a;b")]
    [InlineData("name", "say \"hi\"")]
    [InlineData("na me", "x")]
    [InlineData("", "novalue")]
    public void RefusesValuesTheReadersWouldMangle(string key, string value)
    {
        Assert.NotNull(CoopIniSettings.Validate(key, value));
    }

    [Fact]
    public void WindowedCreatesAMissingSwkotorIniAndCompletesAPartialOne()
    {
        Assert.True(GameLauncher.TrySetWindowed(_directory));
        Assert.True(GameLauncher.IsWindowed(_directory));

        string ini = Path.Combine(_directory, "swkotor.ini");
        File.WriteAllLines(ini, ["[Sound Options]", "Volume=1", "[Graphics Options]", "Width=1024"]);
        Assert.False(GameLauncher.IsWindowed(_directory));
        Assert.True(GameLauncher.TrySetWindowed(_directory));

        Assert.Equal(
            ["[Sound Options]", "Volume=1", "[Graphics Options]", "AllowWindowedMode=1", "FullScreen=0", "Width=1024"],
            File.ReadAllLines(ini));
        Assert.True(File.Exists(ini + ".kotorcoop-backup"));
    }
}

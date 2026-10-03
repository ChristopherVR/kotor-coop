using System.IO.Compression;
using KotorCoop.Agent;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The <c>logs</c> verb: one zip a player can attach to a bug report, secrets blanked.</summary>
public sealed class LogsCommandTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kotorcoop-logs-" + Guid.NewGuid().ToString("N"));

    public LogsCommandTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void PasswordAndHostAreBlankedEverythingElseSurvives()
    {
        const string ini = "[coop]\nmode = join\nhost = 203.0.113.9\nport = 27140\nPassword = hunter2\nname = Lanik\n";

        string redacted = LogsCommand.RedactIni(ini);

        Assert.DoesNotContain("hunter2", redacted);
        Assert.DoesNotContain("203.0.113.9", redacted);
        Assert.Contains("Password = <redacted>", redacted);
        Assert.Contains("host = <redacted>", redacted);
        Assert.Contains("mode = join", redacted);
        Assert.Contains("port = 27140", redacted);
        Assert.Contains("name = Lanik", redacted);
    }

    [Fact]
    public void AnEmptyPasswordStaysEmptySoTheBundleShowsNoneWasSet()
    {
        string redacted = LogsCommand.RedactIni("password =\nhost = \n");

        Assert.Contains("password =", redacted);
        Assert.DoesNotContain("<redacted>", redacted);
    }

    [Fact]
    public void CommentsAndCrLfLineEndingsAreLeftAlone()
    {
        const string ini = "; password = keep this comment\r\n# host = and this one\r\npassword = s3cret\r\nport = 1\r\n";

        string redacted = LogsCommand.RedactIni(ini);

        Assert.Contains("; password = keep this comment\r\n", redacted);
        Assert.Contains("# host = and this one\r\n", redacted);
        Assert.Contains("password = <redacted>\r\n", redacted);
        Assert.DoesNotContain("s3cret", redacted);
        Assert.Contains("port = 1\r\n", redacted);
    }

    [Theory]
    [InlineData("kotorcoop-plugin.log", true)]
    [InlineData("kotorcoop-plugin.log.1", true)]
    [InlineData("KotorCoop-Plugin.LOG", true)]
    [InlineData("kotorcoop-agent.log", true)]
    [InlineData("kotorcoop-agent.log.2", true)]
    [InlineData("kotorcoop-crash.bin", true)]
    [InlineData("dinput8-chain.log", true)]
    [InlineData("swkotor.exe", false)]
    [InlineData("coop.ini", false)]
    [InlineData("kotorcoop-character.pifo", false)]
    [InlineData("kotorcoop-last-address.txt", false)]
    [InlineData("", false)]
    public void OnlyTheSupportFilesAreCollected(string name, bool expected)
    {
        Assert.Equal(expected, LogsCommand.IsSupportFile(name));
    }

    [Fact]
    public void TheZipHoldsTheLogsARedactedIniAndASystemNoteAndNothingElse()
    {
        File.WriteAllText(Path.Combine(_dir, "kotorcoop-plugin.log"), "plugin line\n");
        File.WriteAllText(Path.Combine(_dir, "kotorcoop-agent.log"), "agent line\n");
        File.WriteAllBytes(Path.Combine(_dir, "kotorcoop-crash.bin"), new byte[280]);
        File.WriteAllText(Path.Combine(_dir, "coop.ini"), "[coop]\npassword = hunter2\nhost = 1.2.3.4\n");
        File.WriteAllText(Path.Combine(_dir, "kotorcoop-character.pifo"), "must not be collected");
        File.WriteAllText(Path.Combine(_dir, "swkotor.exe"), "not really");
        string zipPath = Path.Combine(_dir, "out.zip");

        int files = LogsCommand.WriteBundle(_dir, zipPath, "test-1.0");

        Assert.Equal(5, files); // 3 support files + coop.ini + system.txt

        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        string[] names = zip.Entries.Select(e => e.FullName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        Assert.Equal(
            ["coop.ini", "kotorcoop-agent.log", "kotorcoop-crash.bin", "kotorcoop-plugin.log", "system.txt"],
            names);

        using StreamReader ini = new(zip.GetEntry("coop.ini")!.Open());
        string iniText = ini.ReadToEnd();
        Assert.DoesNotContain("hunter2", iniText);
        Assert.DoesNotContain("1.2.3.4", iniText);

        using StreamReader system = new(zip.GetEntry("system.txt")!.Open());
        string systemText = system.ReadToEnd();
        Assert.Contains("test-1.0", systemText);
        Assert.Contains("swkotor.exe", systemText);
        Assert.Contains("installed   unknown", systemText);
    }

    [Fact]
    public void ChainLoaderFailureIsIncludedInTheBundle()
    {
        File.WriteAllText(Path.Combine(_dir, "dinput8-chain.log"), "failed to load KotorCoop.Plugin.dll\n");
        string zipPath = Path.Combine(_dir, "out.zip");

        Assert.Equal(2, LogsCommand.WriteBundle(_dir, zipPath, "preview"));
        using ZipArchive zip = ZipFile.OpenRead(zipPath);
        using StreamReader log = new(zip.GetEntry("dinput8-chain.log")!.Open());
        Assert.Contains("failed to load KotorCoop.Plugin.dll", log.ReadToEnd());
    }

    [Fact]
    public void AFolderWithNothingToCollectWritesNothing()
    {
        string zipPath = Path.Combine(_dir, "out.zip");

        Assert.Equal(0, LogsCommand.WriteBundle(_dir, zipPath, "test"));
        Assert.False(File.Exists(zipPath));
    }

    [Fact]
    public void AHeldOpenLogStillGetsCollected()
    {
        // The game keeps its own log open for writing while it runs.
        string log = Path.Combine(_dir, "kotorcoop-plugin.log");
        using (FileStream held = new(log, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            held.Write("still being written\n"u8);
            held.Flush();

            Assert.True(LogsCommand.WriteBundle(_dir, Path.Combine(_dir, "out.zip"), "test") >= 1);
        }

        using ZipArchive zip = ZipFile.OpenRead(Path.Combine(_dir, "out.zip"));
        using StreamReader reader = new(zip.GetEntry("kotorcoop-plugin.log")!.Open());
        Assert.Contains("still being written", reader.ReadToEnd());
    }
}

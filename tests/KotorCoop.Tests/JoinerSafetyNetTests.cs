using System.Text;
using KotorCoop.Agent;
using KotorCoop.Memory;
using KotorCoop.Plugin;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>The joiner/host safety nets: the autosave backup before co-op overwrites it, the
/// swkotor.ini EnableCheats edit with its one-time backup, and the capped agent log.</summary>
public sealed class JoinerSafetyNetTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (string d in _dirs)
        {
            try { Directory.Delete(d, recursive: true); } catch (IOException) { }
        }
    }

    private string Temp()
    {
        string d = Path.Combine(Path.GetTempPath(), "kcoop-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    // A minimal ERF-shaped SAVEGAME.sav holding one ifo member named <member> (or none).
    private static byte[] Sav(string? member, int pad = 0)
    {
        int count = member is null ? 0 : 1;
        byte[] b = new byte[160 + 24 + pad];
        BitConverter.GetBytes((uint)count).CopyTo(b, 0x10);
        BitConverter.GetBytes(160u).CopyTo(b, 0x18);
        if (member is not null)
        {
            Encoding.ASCII.GetBytes(member).CopyTo(b, 160);
            BitConverter.GetBytes((ushort)2014).CopyTo(b, 160 + 20);
        }

        return b;
    }

    private static string WriteSave(string saves, string folder, byte[]? sav)
    {
        string dir = Path.Combine(saves, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "SAVEGAME.sav"), sav ?? Sav("pifo"));
        File.WriteAllText(Path.Combine(dir, "savenfo.res"), folder);
        return dir;
    }

    [Fact]
    public void PlayersOwnAutosaveIsMovedToANumberedBackup()
    {
        string saves = Temp();
        WriteSave(saves, "000000 - QUICKSAVE", null);
        WriteSave(saves, PluginMenuLoader.AutosaveSlot, Sav("pifo"));

        Assert.True(AutosaveBackup.TryBackup(saves, out string diagnostic), diagnostic);

        Assert.False(Directory.Exists(Path.Combine(saves, PluginMenuLoader.AutosaveSlot)));
        string backup = Path.Combine(saves, "000002 - Autosave before co-op");
        Assert.True(File.Exists(Path.Combine(backup, "SAVEGAME.sav")));
        Assert.True(File.Exists(Path.Combine(backup, "savenfo.res")));
    }

    [Fact]
    public void AStagedCopyIsNotBackedUp()
    {
        string saves = Temp();
        WriteSave(saves, PluginMenuLoader.AutosaveSlot, Sav("kcoopoldpifo"));

        Assert.False(AutosaveBackup.TryBackup(saves, out _));
        Assert.True(Directory.Exists(Path.Combine(saves, PluginMenuLoader.AutosaveSlot)));
        Assert.Single(Directory.GetDirectories(saves));
    }

    [Fact]
    public void AMarkedStagedCopyIsNotBackedUpEvenWithALivePifo()
    {
        string saves = Temp();
        string slot = WriteSave(saves, PluginMenuLoader.AutosaveSlot, Sav("pifo"));
        Assert.True(KotorCoop.Session.StagedSave.TryRecord(saves, slot, out string d), d);

        Assert.False(AutosaveBackup.TryBackup(saves, out _));
        Assert.Single(Directory.GetDirectories(saves));
    }

    [Fact]
    public void OnlyTheTwoNewestBackupsOfThatLabelSurvive()
    {
        string saves = Temp();
        WriteSave(saves, "000005 - My own save", null);
        WriteSave(saves, "000003 - Autosave before co-op", null);
        WriteSave(saves, "000004 - Autosave before co-op", null);
        WriteSave(saves, PluginMenuLoader.AutosaveSlot, Sav("pifo"));

        Assert.True(AutosaveBackup.TryBackup(saves, out _));

        string[] names = [.. Directory.GetDirectories(saves).Select(Path.GetFileName)!];
        Assert.Contains("000006 - Autosave before co-op", names);
        Assert.Contains("000004 - Autosave before co-op", names);
        Assert.DoesNotContain("000003 - Autosave before co-op", names);
        Assert.Contains("000005 - My own save", names);
    }

    [Fact]
    public void TheBackupHappensOncePerProcessAndStagingRunsIt()
    {
        AutosaveBackup.ResetProcessLatch();
        string saves = Temp();
        string game = Path.GetDirectoryName(saves)!;
        string gameDir = Temp();
        string gameSaves = Path.Combine(gameDir, "saves");
        Directory.CreateDirectory(gameSaves);
        WriteSave(gameSaves, "000007 - Source", null);
        WriteSave(gameSaves, PluginMenuLoader.AutosaveSlot, Sav("pifo"));
        _ = game;

        Assert.True(PluginMenuLoader.TryStageSaveAsAutosaveFromDirectory(
            gameDir, Path.Combine(gameSaves, "000007 - Source"), out string d), d);
        Assert.True(Directory.Exists(Path.Combine(gameSaves, "000008 - Autosave before co-op")));

        Assert.False(AutosaveBackup.TryBackupOnce(gameSaves, out _));
        AutosaveBackup.ResetProcessLatch();
    }

    [Fact]
    public void EnableCheatsKeepsCrlfAndTakesAOneTimeBackup()
    {
        string dir = Temp();
        string ini = Path.Combine(dir, "swkotor.ini");
        File.WriteAllBytes(ini, Encoding.Latin1.GetBytes("[Graphics]\r\nX=1\r\n[Game Options]\r\nEnableCheats=0\r\nY=é\r\n"));

        Assert.Equal(IniCheatsFile.Result.Enabled, IniCheatsFile.EnableCheats(ini));
        Assert.Equal("[Graphics]\r\nX=1\r\n[Game Options]\r\nEnableCheats=1\r\nY=é\r\n",
            Encoding.Latin1.GetString(File.ReadAllBytes(ini)));
        string backup = ini + ".kcoop-orig";
        Assert.Contains("EnableCheats=0", File.ReadAllText(backup));

        // A second edit (cheats turned off again) must not overwrite the first backup.
        File.WriteAllText(ini, "[Game Options]\r\nEnableCheats=0\r\n");
        IniCheatsFile.EnableCheats(ini);
        Assert.Contains("Graphics", File.ReadAllText(backup));
        Assert.False(File.Exists(ini + ".kcoop-tmp"));
    }

    [Fact]
    public void EnableCheatsDoesNothingAndMakesNoBackupWhenAlreadyOn()
    {
        string dir = Temp();
        string ini = Path.Combine(dir, "swkotor.ini");
        File.WriteAllText(ini, "[Game Options]\nEnableCheats=1\n");

        Assert.Equal(IniCheatsFile.Result.AlreadyEnabled, IniCheatsFile.EnableCheats(ini));
        Assert.False(File.Exists(ini + ".kcoop-orig"));
        Assert.Equal("[Game Options]\nEnableCheats=1\n", File.ReadAllText(ini));
    }

    [Fact]
    public void EnableCheatsPreservesLfAndUtf8Bom()
    {
        string dir = Temp();
        string ini = Path.Combine(dir, "swkotor.ini");
        byte[] bom = [0xEF, 0xBB, 0xBF];
        File.WriteAllBytes(ini, [.. bom, .. Encoding.UTF8.GetBytes("[Other]\nA=1\n")]);

        IniCheatsFile.EnableCheats(ini);

        byte[] result = File.ReadAllBytes(ini);
        Assert.Equal(bom, result[..3]);
        Assert.Equal("[Other]\nA=1\n[Game Options]\nEnableCheats=1\n", Encoding.UTF8.GetString(result, 3, result.Length - 3));
    }

    [Fact]
    public void AgentLogRotatesMidSessionAndKeepsGenerations()
    {
        string dir = Temp();
        string log = Path.Combine(dir, "kotorcoop-agent.log");
        // Hold the file open the way the launcher's inherited handle does (share delete).
        FileStream inherited = new(log, FileMode.Create, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        using (RotatingLogWriter w = new(log, capMb: 1, keepGenerations: 2))
        {
            // The agent releases that handle once it owns the log (ReleaseInheritedStdHandles);
            // otherwise it would follow the file into the oldest generation and block rotation.
            inherited.Dispose();
            string line = new('x', 1000);
            for (int i = 0; i < 3500; i++)
            {
                w.WriteLine(line);
            }
        }

        Assert.True(File.Exists(Path.Combine(dir, "kotorcoop-agent.1.log")));
        Assert.True(File.Exists(Path.Combine(dir, "kotorcoop-agent.2.log")));
        Assert.False(File.Exists(Path.Combine(dir, "kotorcoop-agent.3.log")));
        Assert.True(new FileInfo(log).Length <= 1024 * 1024 + 2048, $"live={new FileInfo(log).Length}");
    }
}

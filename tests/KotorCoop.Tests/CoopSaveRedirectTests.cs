using KotorCoop.Memory;
using Xunit;

namespace KotorCoop.Tests;

/// <summary>
/// The filesystem half of the dedicated co-op save slot (docs/research/session-save.md's
/// 2026-09-25 entry): a real temp <c>saves\</c> directory stands in for the game's own, and
/// every case is driven through <see cref="CoopSaveRedirect"/>'s actual directory moves — no
/// mocking, matching every other save primitive tested in this tree.
/// </summary>
public sealed class CoopSaveRedirectTests : IDisposable
{
    private readonly List<string> _tempDirectories = [];

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
            }
        }
    }

    private string NewSavesDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "kcoop-redirect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _tempDirectories.Add(dir);
        return dir;
    }

    private static void WriteFakeSave(string saveDirectory, string content)
    {
        Directory.CreateDirectory(saveDirectory);
        File.WriteAllText(Path.Combine(saveDirectory, "SAVEGAME.sav"), content);
    }

    [Fact]
    public void BeginHoldbackIsANoOpWhenThereIsNoExistingQuicksave()
    {
        string saves = NewSavesDirectory();

        bool ok = CoopSaveRedirect.BeginHoldback(saves, out string diagnostic);

        Assert.True(ok);
        Assert.Contains("no existing quicksave", diagnostic, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(saves, CoopSaveRedirect.HoldbackFolder)));
    }

    [Fact]
    public void BeginHoldbackMovesAnExistingQuicksaveAside()
    {
        string saves = NewSavesDirectory();
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);
        WriteFakeSave(quick, "the player's own game");

        bool ok = CoopSaveRedirect.BeginHoldback(saves, out _);

        Assert.True(ok);
        Assert.False(Directory.Exists(quick));
        string holdback = Path.Combine(saves, CoopSaveRedirect.HoldbackFolder);
        Assert.True(Directory.Exists(holdback));
        Assert.Equal("the player's own game", File.ReadAllText(Path.Combine(holdback, "SAVEGAME.sav")));
    }

    [Fact]
    public void AStaleHoldbackFromACrashIsRestoredAndThenHeldBackAgain()
    {
        string saves = NewSavesDirectory();
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);
        string holdback = Path.Combine(saves, CoopSaveRedirect.HoldbackFolder);
        WriteFakeSave(quick, "the player's own game");

        // An earlier sequence held the quicksave back, then the game crashed before completing,
        // and nothing landed.
        Assert.True(CoopSaveRedirect.BeginHoldback(saves, out _));

        Assert.True(CoopSaveRedirect.BeginHoldback(saves, out string diagnostic));
        Assert.Contains("restored", diagnostic, StringComparison.OrdinalIgnoreCase);

        Assert.False(CoopSaveRedirect.CompleteRedirect(saves, out _, out _));
        Assert.Equal("the player's own game", File.ReadAllText(Path.Combine(quick, "SAVEGAME.sav")));
        Assert.False(Directory.Exists(holdback));
    }

    [Fact]
    public void AStaleHoldbackIsKeptAsANumberedSaveWhenTheQuicksaveSlotIsTaken()
    {
        string saves = NewSavesDirectory();
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);
        WriteFakeSave(quick, "the player's older quicksave");

        Assert.True(CoopSaveRedirect.BeginHoldback(saves, out _));
        // Crash; later the player quicksaves again in single-player.
        WriteFakeSave(quick, "the player's newer quicksave");

        Assert.True(CoopSaveRedirect.BeginHoldback(saves, out string diagnostic));
        Assert.Contains("kept", diagnostic, StringComparison.OrdinalIgnoreCase);

        string recovered = Assert.Single(Directory.GetDirectories(saves),
            d => Path.GetFileName(d).EndsWith(CoopSaveRedirect.RecoveredQuickSaveLabel, StringComparison.Ordinal));
        Assert.Equal("the player's older quicksave", File.ReadAllText(Path.Combine(recovered, "SAVEGAME.sav")));

        CoopSaveRedirect.CompleteRedirect(saves, out _, out _);
        Assert.Equal("the player's newer quicksave", File.ReadAllText(Path.Combine(quick, "SAVEGAME.sav")));
    }

    [Fact]
    public void ACompleteWithNothingFreshIsANoOpAndRestoresAnyHoldback()
    {
        string saves = NewSavesDirectory();
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);
        WriteFakeSave(quick, "the player's own game");

        Assert.True(CoopSaveRedirect.BeginHoldback(saves, out _));
        Assert.False(Directory.Exists(quick));

        // Nothing new ever landed (the engine refused the save, say).
        bool redirected = CoopSaveRedirect.CompleteRedirect(saves, out string coopDir, out _);

        Assert.False(redirected);
        Assert.Equal(string.Empty, coopDir);
        Assert.True(Directory.Exists(quick));
        Assert.Equal("the player's own game", File.ReadAllText(Path.Combine(quick, "SAVEGAME.sav")));
    }

    [Fact]
    public void TheFullSequenceRelocatesTheFreshSaveAndRestoresThePlayersQuicksaveByteForByte()
    {
        string saves = NewSavesDirectory();
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);
        WriteFakeSave(quick, "the player's own game");
        DateTime playerQuicksaveMtimeBefore = File.GetLastWriteTimeUtc(Path.Combine(quick, "SAVEGAME.sav"));

        Assert.True(CoopSaveRedirect.BeginHoldback(saves, out _));

        // Stand in for "the engine wrote a fresh quicksave" - the redirect does not care who
        // wrote it, only that it is sitting at the one hard-coded directory name.
        WriteFakeSave(quick, "a fresh co-op capture");

        bool redirected = CoopSaveRedirect.CompleteRedirect(saves, out string coopDir, out string diagnostic);

        Assert.True(redirected);
        Assert.NotEqual(string.Empty, coopDir);
        Assert.True(CoopSaveSlot.IsCoopSlot(Path.GetFileName(coopDir)));
        Assert.Equal("a fresh co-op capture", File.ReadAllText(Path.Combine(coopDir, "SAVEGAME.sav")));
        Assert.Contains("redirected", diagnostic, StringComparison.OrdinalIgnoreCase);

        // The player's quicksave is back, unchanged, at the same path.
        Assert.True(Directory.Exists(quick));
        Assert.Equal("the player's own game", File.ReadAllText(Path.Combine(quick, "SAVEGAME.sav")));
        Assert.Equal(playerQuicksaveMtimeBefore, File.GetLastWriteTimeUtc(Path.Combine(quick, "SAVEGAME.sav")));

        // No holdback directory left behind.
        Assert.False(Directory.Exists(Path.Combine(saves, CoopSaveRedirect.HoldbackFolder)));
    }

    [Fact]
    public void WithNoPriorQuicksaveTheSlotIsLeftAbsentAfterRedirecting()
    {
        string saves = NewSavesDirectory();
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);

        Assert.True(CoopSaveRedirect.BeginHoldback(saves, out _)); // nothing to hold back
        WriteFakeSave(quick, "a fresh co-op capture, first ever on this install");

        bool redirected = CoopSaveRedirect.CompleteRedirect(saves, out string coopDir, out _);

        Assert.True(redirected);
        Assert.Equal("a fresh co-op capture, first ever on this install", File.ReadAllText(Path.Combine(coopDir, "SAVEGAME.sav")));
        // Exactly the pre-existing state: no quicksave at all.
        Assert.False(Directory.Exists(quick));
    }

    [Fact]
    public void RepeatedRedirectsReuseTheSameCoopSlotDirectory()
    {
        string saves = NewSavesDirectory();
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);

        WriteFakeSave(quick, "capture 1");
        Assert.True(CoopSaveRedirect.CompleteRedirect(saves, out string first, out _));

        WriteFakeSave(quick, "capture 2");
        Assert.True(CoopSaveRedirect.CompleteRedirect(saves, out string second, out _));

        Assert.Equal(first, second);
        Assert.Equal("capture 2", File.ReadAllText(Path.Combine(second, "SAVEGAME.sav")));
    }

    [Fact]
    public void ANumberedNamedSaveIsNeverOverwrittenByTheCoopSlotAllocation()
    {
        string saves = NewSavesDirectory();
        string quick = Path.Combine(saves, CoopSaveRedirect.QuickSaveFolder);
        WriteFakeSave(Path.Combine(saves, "000001 - AUTOSAVE"), "autosave");
        WriteFakeSave(Path.Combine(saves, "000002 - my manaan run"), "a player's own named save");
        WriteFakeSave(quick, "a fresh co-op capture");

        Assert.True(CoopSaveRedirect.CompleteRedirect(saves, out string coopDir, out _));

        Assert.NotEqual(Path.Combine(saves, "000002 - my manaan run"), coopDir);
        Assert.Equal("a player's own named save",
            File.ReadAllText(Path.Combine(saves, "000002 - my manaan run", "SAVEGAME.sav")));
    }

    [Fact]
    public void MirrorIntoCoopSlotCopiesWithoutDisturbingTheSource()
    {
        string saves = NewSavesDirectory();
        string staged = Path.Combine(saves, "000001 - AUTOSAVE");
        WriteFakeSave(staged, "the adopted world");

        bool ok = CoopSaveRedirect.MirrorIntoCoopSlot(saves, staged, out string coopDir, out string diagnostic);

        Assert.True(ok);
        Assert.True(CoopSaveSlot.IsCoopSlot(Path.GetFileName(coopDir)));
        Assert.Equal("the adopted world", File.ReadAllText(Path.Combine(coopDir, "SAVEGAME.sav")));
        // The source (the mechanical staging target the engine's own load replay needs) is untouched.
        Assert.True(Directory.Exists(staged));
        Assert.Equal("the adopted world", File.ReadAllText(Path.Combine(staged, "SAVEGAME.sav")));
        Assert.Contains("mirrored", diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MirrorIntoCoopSlotOverwritesAPreviousMirrorInPlace()
    {
        string saves = NewSavesDirectory();
        string staged = Path.Combine(saves, "000001 - AUTOSAVE");

        WriteFakeSave(staged, "world 1");
        Assert.True(CoopSaveRedirect.MirrorIntoCoopSlot(saves, staged, out string first, out _));

        WriteFakeSave(staged, "world 2");
        Assert.True(CoopSaveRedirect.MirrorIntoCoopSlot(saves, staged, out string second, out _));

        Assert.Equal(first, second);
        Assert.Equal("world 2", File.ReadAllText(Path.Combine(second, "SAVEGAME.sav")));
    }
}

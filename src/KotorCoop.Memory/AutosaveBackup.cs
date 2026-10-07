using System.Text.Json;

namespace KotorCoop.Memory;

/// <summary>
/// Keeps a joiner's own single-player autosave from being destroyed by co-op. Joining copies
/// the host's world over <c>saves\000001 - AUTOSAVE</c> (<c>WorldAdoption</c>,
/// <see cref="PluginMenuLoader.TryStageSaveAsAutosaveFromDirectory"/>); before the FIRST such
/// overwrite in a process, an autosave that is the player's own is moved to a numbered, visible
/// <c>NNNNNN - Autosave before co-op</c> folder, so it appears in the retail Load Game list.
///
/// "The player's own" means not itself a staged co-op copy: the same two signals
/// <c>StagedSave.IsStagedCopy</c> uses (the <c>kotorcoop-staged.json</c> marker still matching
/// the slot's <c>SAVEGAME.sav</c>, or a neutralised/absent pifo in the autosave slot). That
/// logic lives in KotorCoop.Session, which this project cannot reference, so the few lines are
/// mirrored here. Once per process: after the first overwrite the slot holds staged data or an
/// engine autosave written inside the host's world, neither of which is worth keeping.
///
/// Best-effort and non-throwing, like the other save primitives: a failure is a diagnostic and
/// never blocks the staging that follows.
/// </summary>
public static class AutosaveBackup
{
    public const string BackupLabel = "Autosave before co-op";

    /// <summary>How many labelled backups survive; older ones of that exact label are deleted.</summary>
    public const int KeepBackups = 2;

    private const string SaveFileName = "SAVEGAME.sav";
    private const string MarkerFileName = "kotorcoop-staged.json";

    private static int _done;

    /// <summary>Test hook: forget that this process already took (or skipped) its backup.</summary>
    public static void ResetProcessLatch() => Interlocked.Exchange(ref _done, 0);

    /// <summary>The once-per-process entry point the staging sites call.</summary>
    public static bool TryBackupOnce(string savesDirectory, out string diagnostic)
    {
        if (Interlocked.Exchange(ref _done, 1) != 0)
        {
            diagnostic = "autosave backup already handled this session";
            return false;
        }

        return TryBackup(savesDirectory, out diagnostic);
    }

    /// <summary>Moves the autosave to a numbered backup folder when it is the player's own.
    /// Not latched; <see cref="TryBackupOnce"/> is the production entry point.</summary>
    public static bool TryBackup(string savesDirectory, out string diagnostic)
    {
        try
        {
            string autosave = Path.Combine(savesDirectory, PluginMenuLoader.AutosaveSlot);
            if (!File.Exists(Path.Combine(autosave, SaveFileName)))
            {
                diagnostic = "no autosave to back up";
                return false;
            }

            if (IsStagedCopy(savesDirectory, autosave))
            {
                diagnostic = "the autosave is already a staged co-op copy; nothing to back up";
                return false;
            }

            string[] existing = [.. Directory.EnumerateDirectories(savesDirectory).Select(d => Path.GetFileName(d))];
            string folder = CoopSaveSlot.FolderName(CoopSaveSlot.NextIndex(existing), BackupLabel);
            Directory.Move(autosave, Path.Combine(savesDirectory, folder));

            int pruned = Prune(savesDirectory);
            diagnostic = $"kept your autosave as '{folder}'" + (pruned > 0 ? $" (removed {pruned} older backup(s))" : string.Empty);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"could not back up the autosave: {ex.Message}";
            return false;
        }
    }

    private static int Prune(string savesDirectory)
    {
        List<(int Index, string Path)> backups = [];
        foreach (string dir in Directory.EnumerateDirectories(savesDirectory))
        {
            string name = Path.GetFileName(dir);
            if (!string.Equals(CoopSaveSlot.NameOf(name), BackupLabel, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int digits = 0;
            while (digits < name.Length && char.IsAsciiDigit(name[digits]))
            {
                digits++;
            }

            if (digits > 0 && int.TryParse(name.AsSpan(0, digits), out int index))
            {
                backups.Add((index, dir));
            }
        }

        int removed = 0;
        foreach ((int _, string path) in backups.OrderByDescending(b => b.Index).Skip(KeepBackups))
        {
            try
            {
                Directory.Delete(path, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // leave it; a stale backup is harmless
            }
        }

        return removed;
    }

    private static bool IsStagedCopy(string savesDirectory, string autosave)
    {
        FileInfo save = new(Path.Combine(autosave, SaveFileName));
        try
        {
            string markerPath = Path.Combine(savesDirectory, MarkerFileName);
            if (File.Exists(markerPath))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(markerPath));
                JsonElement root = doc.RootElement;
                if (root.TryGetProperty("Slot", out JsonElement slot)
                    && root.TryGetProperty("Length", out JsonElement length)
                    && root.TryGetProperty("LastWriteUtc", out JsonElement written)
                    && string.Equals(slot.GetString(), Path.GetFileName(autosave), StringComparison.OrdinalIgnoreCase)
                    && length.GetInt64() == save.Length
                    && written.GetDateTime().ToUniversalTime() == save.LastWriteTimeUtc)
                {
                    return true;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or FormatException)
        {
            // an unreadable marker is "nothing staged"
        }

        PifoPresence pifo = PlayerIfo.InspectPifo(File.ReadAllBytes(save.FullName));
        return pifo is PifoPresence.Neutralised or PifoPresence.None;
    }
}

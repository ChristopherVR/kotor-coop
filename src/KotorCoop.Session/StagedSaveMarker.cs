using System.Text;
using KotorCoop.Memory;
using System.Text.Json;

namespace KotorCoop.Session;

/// <summary>
/// What <c>saves\kotorcoop-staged.json</c> records: the one save directory under
/// <c>saves\</c> that is NOT this install's own — the byte copy of the host's world that
/// <see cref="WorldAdoption"/> staged into the autosave slot, identified by the
/// <c>SAVEGAME.sav</c> it wrote (size and mtime). The engine reuses that same slot for its
/// own transition autosave, so the slot NAME alone cannot tell "the host's world we copied
/// in" from "the save the engine wrote after the joiner walked through a door" — the file
/// the staging produced can, and the moment the engine rewrites it the marker stops matching.
///
/// Persisted, not in-memory: the agent that staged the world and the agent that later
/// captures the character at disconnect are routinely different processes (the joiner
/// restarts between sessions; the staged copy stays on disk until the next adoption or the
/// engine's next autosave replaces it).
/// </summary>
public sealed record StagedSaveMarker(string Slot, long Length, DateTime LastWriteUtc);

/// <summary>
/// The staged-copy guard for <see cref="ClientCharacterPersistence"/>. Recorded once per
/// adoption; consulted by every character capture so that "carry the character out of the
/// current save" never lifts the HOST's player out of the copy of the host's world.
///
/// Why this exists (2026-09-03 join_mode = auto live pass): the disconnect capture read the
/// staged autosave — a byte copy of the host's save, <c>Mod_PlayerList</c> and all — and
/// overwrote the joiner's sidecar with the host's PC. The next join would have carried the
/// host's character in as the joiner's. A save identity comparison cannot catch this: the
/// joiner's own save in the same module digests identically by construction (that is what
/// staging is for), so only the file the staging wrote can be told apart.
/// </summary>
public static class StagedSave
{
    public const string MarkerFileName = "kotorcoop-staged.json";
    private const string SaveFileName = "SAVEGAME.sav";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>Records <paramref name="slotDirectory"/>'s just-written <c>SAVEGAME.sav</c>
    /// as the staged copy. Best-effort and non-fatal for the adoption: a marker that could
    /// not be written only means a later capture falls back to the pre-2026-09-03 behaviour
    /// — say so through <paramref name="diagnostic"/> so the log shows which case ran.</summary>
    public static bool TryRecord(string savesDirectory, string slotDirectory, out string diagnostic)
    {
        try
        {
            FileInfo save = new(Path.Combine(slotDirectory, SaveFileName));
            if (!save.Exists)
            {
                diagnostic = $"no {SaveFileName} in '{Path.GetFileName(slotDirectory)}' to mark as staged";
                return false;
            }

            StagedSaveMarker marker = new(Path.GetFileName(slotDirectory), save.Length, save.LastWriteTimeUtc);
            string json = JsonSerializer.Serialize(marker, JsonOptions);

            string path = Path.Combine(savesDirectory, MarkerFileName);
            string tmp = path + ".tmp";
            File.WriteAllBytes(tmp, Encoding.UTF8.GetBytes(json));
            File.Move(tmp, path, overwrite: true);

            diagnostic = $"marked '{marker.Slot}' as the staged copy ({marker.Length:N0} bytes)";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"could not mark the staged copy: {ex.Message}";
            return false;
        }
    }

    /// <summary>Reads the marker back; false (and <paramref name="marker"/> null) when it is
    /// absent, mid-write or corrupt — never thrown, an unreadable marker is "nothing staged".</summary>
    public static bool TryRead(string savesDirectory, out StagedSaveMarker? marker)
    {
        marker = null;
        try
        {
            string path = Path.Combine(savesDirectory, MarkerFileName);
            if (!File.Exists(path))
            {
                return false;
            }

            marker = JsonSerializer.Deserialize<StagedSaveMarker>(File.ReadAllText(path), JsonOptions);
            return marker is not null && marker.Slot.Length > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            marker = null;
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="saveDirectory"/> is the host's world rather than a save this
    /// install's engine wrote. Two independent signals, either suffices:
    /// <list type="bullet">
    /// <item>the marker: the slot still holds exactly the <c>SAVEGAME.sav</c> the staging
    /// recorded (size and mtime). A rewritten file — the engine's own autosave replaced it —
    /// no longer matches;</item>
    /// <item>the content, for the autosave slot only: the engine writes a live <c>pifo</c>
    /// into every autosave it makes, and a staged world carries a neutralised one or none
    /// (<see cref="PlayerIfo.InspectPifo"/>). This is what catches a copy staged before the
    /// marker existed (an install upgraded mid-life) and a marker write that failed — the
    /// 2026-09-03 re-run hit exactly the first case: the previous session's staged copy,
    /// unmarked, was captured once more before the fixed adoption could mark it.</item>
    /// </list>
    /// Manual saves in other slots never have a pifo and are never staged into, so the
    /// content rule deliberately does not look at them.
    /// </summary>
    public static bool IsStagedCopy(string savesDirectory, string saveDirectory)
    {
        try
        {
            FileInfo save = new(Path.Combine(saveDirectory, SaveFileName));
            if (!save.Exists)
            {
                return false;
            }

            string slot = Path.GetFileName(saveDirectory);

            if (TryRead(savesDirectory, out StagedSaveMarker? marker) && marker is not null
                && string.Equals(slot, marker.Slot, StringComparison.OrdinalIgnoreCase)
                && save.Length == marker.Length && save.LastWriteTimeUtc == marker.LastWriteUtc)
            {
                return true;
            }

            if (string.Equals(slot, PluginMenuLoader.AutosaveSlot, StringComparison.OrdinalIgnoreCase))
            {
                PifoPresence pifo = PlayerIfo.InspectPifo(File.ReadAllBytes(save.FullName));
                return pifo is PifoPresence.Neutralised or PifoPresence.None;
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

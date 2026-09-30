using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Session;

/// <summary>Binds a living own sidecar to the exact character in a stable local save.</summary>
public static class WorldlessRecoveryCheckpoint
{
    public static bool TryRead(string savesDirectory, out GffStruct player, out SidecarMetadata metadata)
    {
        player = null!;
        metadata = SidecarMetadata.None;
        if (!ClientCharacterPersistence.TryReadSidecar(savesDirectory, out var own, out var meta, out _)
            || !own.TryGetShort("CurrentHitPoints", out short current)
            || !own.TryGetShort("HitPoints", out short stored)
            || !own.TryGetShort("MaxHitPoints", out short maximum)
            || !SavedCharacterHealthRule.IsLiving(current, stored, maximum)) return false;
        byte[] character = PlayerIfo.BuildPifo(own);
        bool hasSourceProof = meta.SourceSaveDirectory is not null || meta.SourceSaveSha256 is not null || meta.SourceSaveLength != 0;
        if (hasSourceProof && (meta.SourceSaveDirectory is null || meta.SourceSaveSha256 is null || meta.SourceSaveLength <= 0)) return false;
        try
        {
            foreach (string source in Directory.EnumerateDirectories(savesDirectory))
            {
                if (Path.GetFileName(source).Equals("000001 - AUTOSAVE", StringComparison.OrdinalIgnoreCase)) continue;
                if (StagedSave.IsStagedCopy(savesDirectory, source)) continue;
                DateTime stamp = File.GetLastWriteTimeUtc(Path.Combine(source, "SAVEGAME.sav"));
                bool recordedSource = meta.SourceSaveDirectory is not null
                    && string.Equals(Path.GetFullPath(source), meta.SourceSaveDirectory, StringComparison.OrdinalIgnoreCase);
                if (hasSourceProof && !recordedSource) continue;
                // Legacy captures did not record source hashes. Their original archive must
                // contain the identical whole character and predate the capture by at most 5s.
                if (!recordedSource && (stamp > meta.CapturedAtUtc || meta.CapturedAtUtc - stamp > TimeSpan.FromSeconds(5))) continue;
                if (!ClientCharacterPersistence.TryHashSave(source, out string before, out long length)
                    || !PlayerIfo.TryExtractPlayer(source, out var saved, out _)
                    || !PlayerIfo.BuildPifo(saved).AsSpan().SequenceEqual(character)
                    || !ClientCharacterPersistence.TryHashSave(source, out string after, out long afterLength)
                    || before != after || length != afterLength) continue;
                if (recordedSource && (before != meta.SourceSaveSha256 || length != meta.SourceSaveLength)) continue;
                player = own;
                metadata = meta with { SourceSaveDirectory = Path.GetFullPath(source), SourceSaveSha256 = before, SourceSaveLength = length };
                return true;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or ArgumentException)
        { }
        return false;
    }
}

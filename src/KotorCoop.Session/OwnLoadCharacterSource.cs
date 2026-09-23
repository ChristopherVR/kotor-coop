using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>A request-time snapshot of the exact autosave directory a native load selected.
/// It can authorize character capture only for the corresponding completed world-load edge.</summary>
public sealed record OwnLoadCharacterSource(
    uint ProvenanceGeneration,
    uint WorldLoadSerialAtRequest,
    string Directory,
    string SaveSha256,
    long SaveLength,
    long ObservedAtMs,
    uint? CommandRequestId = null,
    uint? CommandStartWorldLoadSerial = null);

public static class OwnLoadCharacterSourceRule
{
    public const long MaxRequestAgeMs = 90_000;

    /// <summary>Accept only a new, successful direct-directory request for the staged autosave,
    /// observed before its world-load serial advances. Unsupported or stale requests fail closed.</summary>
    public static bool TryCreate(
        LoadProvenance provenance,
        uint? previouslyObservedGeneration,
        string savesDirectory,
        uint currentWorldLoadSerial,
        long nowMs,
        out OwnLoadCharacterSource? source)
    {
        source = null;
        if (!previouslyObservedGeneration.HasValue
            || previouslyObservedGeneration.Value == provenance.Generation
            || provenance.Source != LoadProvenanceSource.MainMenuDirectDirectory
            || provenance.Status != LoadProvenanceStatus.CapturedDirectory
            || !string.Equals(provenance.DirectoryName, "000001 - AUTOSAVE", StringComparison.OrdinalIgnoreCase)
            || provenance.WorldLoadSerialAtRequest != currentWorldLoadSerial)
        {
            return false;
        }

        string directory = Path.GetFullPath(Path.Combine(savesDirectory, provenance.DirectoryName!));
        if (!ClientCharacterPersistence.TryHashSave(directory, out string hash, out long length))
        {
            return false;
        }

        source = new OwnLoadCharacterSource(
            provenance.Generation,
            provenance.WorldLoadSerialAtRequest,
            directory,
            hash,
            length,
            nowMs);
        return true;
    }

    public static bool IsCompletedFor(
        OwnLoadCharacterSource? source,
        LoadProvenance? currentProvenance,
        bool ownLoadEdge,
        uint currentWorldLoadSerial,
        long nowMs)
    {
        if (source is null || !ownLoadEdge || currentProvenance is null
            || currentProvenance.Generation != source.ProvenanceGeneration
            || currentProvenance.WorldLoadSerialAtRequest != source.WorldLoadSerialAtRequest
            || nowMs < source.ObservedAtMs
            || nowMs - source.ObservedAtMs > MaxRequestAgeMs)
        {
            return false;
        }

        return currentWorldLoadSerial == unchecked(source.WorldLoadSerialAtRequest + 1);
    }

    public static bool IsCommandCompletedFor(
        OwnLoadCharacterSource? source,
        uint currentWorldLoadSerial,
        uint appliedRequestId,
        int requestStatus,
        long nowMs)
    {
        return source is not null
            && source.CommandRequestId.HasValue
            && source.CommandStartWorldLoadSerial.HasValue
            && requestStatus == 0
            && appliedRequestId == source.CommandRequestId.Value
            && currentWorldLoadSerial == unchecked(source.CommandStartWorldLoadSerial.Value + 1)
            && nowMs >= source.ObservedAtMs
            && nowMs - source.ObservedAtMs <= MaxRequestAgeMs;
    }
}

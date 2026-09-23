using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Atomic sidecar persistence for health-scaling baselines. Callers must invoke
/// <see cref="TryWriteForArchive"/> only after the game has finished writing the save archive;
/// the store independently hashes the archive before and after preparing the sidecar and
/// refuses to publish if those bytes changed during the operation.</summary>
public static class EnemyScalingBaselineStore
{
    public const int MaximumSidecarBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    public static bool TryWriteForArchive(
        string sidecarPath,
        string saveArchivePath,
        EnemyScalingSaveKey saveKey,
        IReadOnlyList<EnemyScalingBaselineRecord> records,
        out string diagnostic)
    {
        diagnostic = "invalid input";
        if (string.IsNullOrWhiteSpace(sidecarPath)
            || string.IsNullOrWhiteSpace(saveArchivePath)
            || !EnemyScalingBaselineRule.IsValidSaveKey(saveKey)
            || records is null)
        {
            return false;
        }

        var unhashed = new EnemyScalingBaselineDocument(
            EnemyScalingBaselineDocument.CurrentSchemaVersion,
            saveKey.Slot,
            saveKey.Name,
            saveKey.SaveIdentity,
            string.Empty,
            records.ToArray());
        if (!EnemyScalingBaselineRule.IsValidDocument(unhashed, requireHash: false))
        {
            diagnostic = "invalid baseline document";
            return false;
        }

        string? temporaryPath = null;
        try
        {
            if (AreSameFilePath(sidecarPath, saveArchivePath))
            {
                diagnostic = "sidecar path cannot be the save archive";
                return false;
            }

            if (!TryHashArchive(saveArchivePath, out string archiveHash))
            {
                diagnostic = "save archive unreadable";
                return false;
            }

            EnemyScalingBaselineDocument document = unhashed with { ArchiveSha256 = archiveHash };
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
            if (bytes.Length > MaximumSidecarBytes)
            {
                diagnostic = "sidecar exceeds size limit";
                return false;
            }

            string fullPath = Path.GetFullPath(sidecarPath);
            string? directory = Path.GetDirectoryName(fullPath);
            if (string.IsNullOrEmpty(directory))
            {
                diagnostic = "sidecar directory unavailable";
                return false;
            }

            Directory.CreateDirectory(directory);
            temporaryPath = fullPath + ".tmp." + Guid.NewGuid().ToString("N");
            using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (!TryHashArchive(saveArchivePath, out string settledHash)
                || !string.Equals(archiveHash, settledHash, StringComparison.Ordinal))
            {
                diagnostic = "save archive changed while sidecar was being prepared";
                return false;
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            temporaryPath = null;
            diagnostic = "written";
            return true;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            diagnostic = ex is JsonException ? "sidecar serialization failed" : "sidecar write failed";
            return false;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (IsExpectedIoFailure(ex)) { }
            }
        }
    }

    /// <summary>Reads and validates a sidecar only when both the exact selected-save key and
    /// the current archive's SHA-256 match. Any failure returns false and no document.</summary>
    public static bool TryReadForArchive(
        string sidecarPath,
        string saveArchivePath,
        EnemyScalingSaveKey selectedSave,
        out EnemyScalingBaselineDocument? document,
        out string diagnostic)
    {
        document = null;
        diagnostic = "invalid input";
        if (string.IsNullOrWhiteSpace(sidecarPath)
            || string.IsNullOrWhiteSpace(saveArchivePath)
            || !EnemyScalingBaselineRule.IsValidSaveKey(selectedSave))
        {
            return false;
        }

        try
        {
            if (AreSameFilePath(sidecarPath, saveArchivePath))
            {
                diagnostic = "sidecar path cannot be the save archive";
                return false;
            }

            using FileStream sidecar = new(sidecarPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (sidecar.Length is <= 0 or > MaximumSidecarBytes)
            {
                diagnostic = "sidecar missing or outside size limit";
                return false;
            }

            byte[] bytes = new byte[checked((int)sidecar.Length)];
            sidecar.ReadExactly(bytes);
            if (sidecar.Length != bytes.Length || sidecar.ReadByte() != -1)
            {
                diagnostic = "sidecar changed while being read";
                return false;
            }

            if (!HasUniqueJsonProperties(bytes))
            {
                diagnostic = "sidecar contains duplicate JSON properties";
                return false;
            }

            EnemyScalingBaselineDocument? parsed = JsonSerializer.Deserialize<EnemyScalingBaselineDocument>(bytes, JsonOptions);
            if (!EnemyScalingBaselineRule.IsValidDocument(parsed, requireHash: true))
            {
                diagnostic = "sidecar schema or contents invalid";
                return false;
            }

            if (parsed!.SaveSlot != selectedSave.Slot
                || parsed.SaveIdentity != selectedSave.SaveIdentity
                || !string.Equals(parsed.SaveName, selectedSave.Name, StringComparison.Ordinal))
            {
                diagnostic = "selected save identity mismatch";
                return false;
            }

            if (!TryHashArchive(saveArchivePath, out string actualHash))
            {
                diagnostic = "save archive unreadable";
                return false;
            }

            if (!CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(parsed.ArchiveSha256),
                    Convert.FromHexString(actualHash)))
            {
                diagnostic = "save archive content mismatch";
                return false;
            }

            document = parsed;
            diagnostic = "matched";
            return true;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            diagnostic = ex is JsonException ? "sidecar JSON invalid" : "sidecar read failed";
            return false;
        }
    }

    private static bool TryHashArchive(string path, out string hash)
    {
        hash = string.Empty;
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= 0)
            {
                return false;
            }

            hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            return true;
        }
        catch (Exception ex) when (IsExpectedIoFailure(ex))
        {
            return false;
        }
    }

    private static bool HasUniqueJsonProperties(ReadOnlyMemory<byte> bytes)
    {
        using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        return HasUniqueJsonProperties(json.RootElement);
    }

    private static bool AreSameFilePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool HasUniqueJsonProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name) || !HasUniqueJsonProperties(property.Value))
                {
                    return false;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (!HasUniqueJsonProperties(item))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool IsExpectedIoFailure(Exception ex) =>
        ex is IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or JsonException
            or NotSupportedException
            or ArgumentException
            or CryptographicException;
}

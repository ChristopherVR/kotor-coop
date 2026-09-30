using System.Security.Cryptography;
using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Memory;

/// <summary>Identity of the engine's serialized companion, never inferred from its tag.</summary>
public static class PartyCacheFingerprint
{
    public static bool IsValid(string hash) => hash.Length == 64
        && hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    public static string[] ReadSave(string saveDirectory)
    {
        string[] hashes = new string[9];
        Array.Fill(hashes, string.Empty);
        try
        {
            ErfArchive archive = ErfArchive.Open(Path.Combine(saveDirectory, "SAVEGAME.sav"));
            for (int slot = 0; slot < hashes.Length; slot++)
                if (archive.TryRead($"AVAILNPC{slot}", "utc", out byte[] payload)
                    && payload.Length is > 0 and <= 1_048_576 && Gff.Parse(payload).FileType == "UTC ")
                    hashes[slot] = Convert.ToHexString(SHA256.HashData(payload));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or OverflowException) { }
        return hashes;
    }
}

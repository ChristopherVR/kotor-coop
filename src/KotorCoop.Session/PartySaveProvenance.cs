using System.Text.Json;
using KotorCoop.Memory;
using KotorCoop.Protocol;

namespace KotorCoop.Session;

/// <summary>Exact saved payload lineage, recorded only after a verified native save or load.
/// A UTC's tag, appearance and name are never used to recover its recruitment template.</summary>
public static class PartySaveProvenance
{
    public const string FileName = "kotorcoop-party-provenance.json";
    public sealed record Snapshot(string SaveSha256, long SaveLength, PartyOccupantIdentity[] Occupants, string Module);
    private sealed record Manifest(int Version, string SaveSha256, long SaveLength, PartyOccupantIdentity[] Occupants);
    public static bool MatchesSnapshot(string directory, Snapshot snapshot) =>
        ClientCharacterPersistence.TryHashSave(directory, out string hash, out long length)
        && length == snapshot.SaveLength && snapshot.SaveSha256.Equals(hash, StringComparison.Ordinal);

    public static bool TryWriteVerified(string directory, PartyRoster verified,
        IReadOnlyList<string> verifiedPayloads, out string diagnostic)
    {
        diagnostic = "companion provenance refused";
        try
        {
            if (!ClientCharacterPersistence.TryHashSave(directory, out string before, out long length)) return false;
            string[] actual = PartyCacheFingerprint.ReadSave(directory);
            PartyOccupantIdentity[] rows = new PartyOccupantIdentity[9];
            bool any = false;
            for (int slot = 0; slot < 9; slot++)
            {
                PartyOccupantIdentity occupant = verified.OccupantFor(slot);
                if (!verified.IsAvailable(slot) || !ModSupportProfile.IsResref(occupant.Template)
                    || slot >= verifiedPayloads.Count || !PartyCacheFingerprint.IsValid(actual[slot])
                    || !actual[slot].Equals(verifiedPayloads[slot], StringComparison.Ordinal))
                { rows[slot] = new(0, ""); continue; }
                rows[slot] = occupant with { CacheFingerprint = actual[slot] }; any = true;
            }
            if (!any || !ClientCharacterPersistence.TryHashSave(directory, out string after, out long afterLength)
                || length != afterLength || !before.Equals(after, StringComparison.Ordinal)) return false;
            string path = Path.Combine(directory, FileName), temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Manifest(1, before, length, rows)));
                File.Move(temporary, path, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            diagnostic = "verified companion save provenance recorded";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException)
        { diagnostic = $"companion provenance unavailable: {ex.Message}"; return false; }
    }

    /// <summary>Call before the selected native load request. The full save hash is retained
    /// by the caller and checked again at the corresponding completed native load.</summary>
    public static bool TryRead(string directory, out Snapshot? snapshot)
    {
        snapshot = null;
        try
        {
            string path = Path.Combine(directory, FileName);
            if (!File.Exists(path)) return false;
            byte[] manifestBytes;
            using (FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length is <= 0 or > 16_384) return false;
                manifestBytes = new byte[(int)stream.Length];
                stream.ReadExactly(manifestBytes);
                if (stream.ReadByte() != -1) return false;
            }
            Manifest? data = JsonSerializer.Deserialize<Manifest>(manifestBytes, new JsonSerializerOptions { MaxDepth = 8 });
            if (data is null || data.Version != 1 || data.Occupants is null || data.Occupants.Length != 9
                || !PartyCacheFingerprint.IsValid(data.SaveSha256)
                || !ClientCharacterPersistence.TryHashSave(directory, out string hash, out long length)
                || length != data.SaveLength || !hash.Equals(data.SaveSha256, StringComparison.Ordinal)) return false;
            string[] actual = PartyCacheFingerprint.ReadSave(directory);
            bool any = false;
            for (int slot = 0; slot < 9; slot++)
            {
                PartyOccupantIdentity occupant = data.Occupants[slot];
                if (string.IsNullOrEmpty(occupant.Template)) continue;
                if (!ModSupportProfile.IsResref(occupant.Template) || !PartyCacheFingerprint.IsValid(occupant.CacheFingerprint)
                    || !actual[slot].Equals(occupant.CacheFingerprint, StringComparison.Ordinal)) return false;
                any = true;
            }
            if (!any || !ClientCharacterPersistence.TryHashSave(directory, out string after, out long afterLength)
                || length != afterLength || !hash.Equals(after, StringComparison.Ordinal)) return false;
            SaveDigest.TryReadActiveModuleName(directory, out string module);
            snapshot = new(hash, length, data.Occupants.ToArray(), module); return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { return false; }
    }
}

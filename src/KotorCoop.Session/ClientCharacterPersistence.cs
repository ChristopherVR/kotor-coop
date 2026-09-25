using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Buffers.Binary;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Session;

/// <summary>
/// A digest-change edge detector with no peer comparison at all: "did MY OWN save identity
/// just become a DIFFERENT known value from the one I first saw this session". See
/// docs/research/session-save.md §3-4 for why this, rather than a host/client comparison, is
/// the right signal for both roadmap item 14 phase 1 triggers (a client's own save changing,
/// a host's own save changing) — each side only ever needs to watch itself.
///
/// Deliberately NOT "any mismatch is a change": the very first real reading after a (re)connect
/// becomes the baseline rather than a change, and 0 (unresolved — <c>SaveDigestCache</c> reports
/// this for a tick or two while a background thread computes the real value) never counts as
/// either a baseline or a change, per the house "unknown is not a mismatch" rule every other
/// compatibility check already follows.
/// </summary>
public readonly record struct SaveIdentityWatchState(uint Baseline, bool Established)
{
    public static SaveIdentityWatchState Initial => new(0, false);
}

/// <summary>Pure, stateless observation function for <see cref="SaveIdentityWatchState"/> —
/// see <c>SaveIdentityWatchTests</c> for the cases this covers.</summary>
public static class SaveIdentityWatch
{
    /// <returns>The next state, and whether THIS call is the edge — the one tick a real,
    /// already-established identity changed. The caller reacts once, on <c>Changed</c>, not
    /// on every subsequent tick the new value persists (the returned state re-baselines to
    /// the new value immediately, so the next call already reports no change again).</returns>
    public static (SaveIdentityWatchState Next, bool Changed) Observe(SaveIdentityWatchState current, uint digestNow)
    {
        if (digestNow == 0)
        {
            // Unresolved, not "matches nothing" and not "changed to nothing" — leave the
            // baseline exactly where it was and try again next tick.
            return (current, false);
        }

        if (!current.Established)
        {
            // The first real reading this session/connection. It IS the baseline, not a
            // change from one — there was nothing to change from yet.
            return (new SaveIdentityWatchState(digestNow, true), false);
        }

        if (digestNow == current.Baseline)
        {
            return (current, false);
        }

        return (new SaveIdentityWatchState(digestNow, true), true);
    }
}

/// <summary>What <c>kotorcoop-character.json</c> records about the sidecar pifo sitting
/// beside it. A record, not a class, so <c>System.Text.Json</c> round-trips it with no
/// attributes — see <see cref="GameProfile"/> for the precedent in this tree.</summary>
public sealed record SidecarMetadata(uint HostSaveIdentity, DateTime CapturedAtUtc)
{
    /// <summary>The all-zero, "nothing captured yet" answer a corrupt or absent sidecar
    /// resolves to — never null, so a caller need not juggle a second kind of absence on
    /// top of the bool <see cref="ClientCharacterPersistence.TryReadSidecar"/> already returns.</summary>
    public static SidecarMetadata None => new(0, DateTime.MinValue);
}

/// <summary>What one <see cref="ClientCharacterPersistence.Capture"/> did to the sidecar on
/// disk. <see cref="Kept"/> is not a failure: the capture found nothing it should write —
/// the sidecar already holds the newer (or the only correct) character.</summary>
public enum CharacterCapture
{
    Written,
    Kept,
    Failed,
}

/// <summary>
/// Roadmap item 14 phase 1, "resume tomorrow": a joiner's progressed character (inventory,
/// equipment, alignment, feats) lives only in the ADOPTED world's save while a session is
/// running, and is lost the moment the client's own save on disk is what the next join reads
/// from — because that own save is exactly what it was before session 1 started (world
/// adoption never writes it; see <c>docs/research/world-adoption.md</c>'s "the trade").
///
/// THE MECHANISM, per <c>docs/research/session-save.md</c> §2: when the client's own save
/// identity changes while playing in the adopted world (a normal in-game save landing in
/// their own <c>saves\</c> directory — the SAME directory <c>SaveDigestCache</c> already
/// watches for the compat handshake) or the session ends cleanly, lift the character out
/// with <see cref="PlayerIfo.TryExtractPlayer"/> — exactly the extraction the join flow
/// already runs, just at a different moment — and stash it as a sidecar pair
/// (<c>kotorcoop-character.pifo</c> + <c>kotorcoop-character.json</c>) the NEXT join prefers
/// over whatever the client's own save directory happens to hold.
///
/// Every write here is temp-file-then-rename (a single filesystem rename is atomic on both
/// Windows and POSIX), and every read tolerates the sidecar being absent, mid-write or
/// corrupt — a missing or bad sidecar is just "nothing to prefer", never an exception.
/// </summary>
public static class ClientCharacterPersistence
{
    public const string PifoFileName = "kotorcoop-character.pifo";
    public const string MetaFileName = "kotorcoop-character.json";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// The save a capture should lift the character out of: the newest <c>SAVEGAME.sav</c>
    /// under <paramref name="savesDirectory"/> by file mtime, SKIPPING the staged copy of the
    /// host's world (<see cref="StagedSave.IsStagedCopy"/>). Null when nothing qualifies.
    ///
    /// Not <c>SaveDigest.FindSessionSave</c>, deliberately: that one prefers the autosave
    /// slot outright because it answers "which save is this session PLAYING" (the staged
    /// world, for a joiner) — the right question for the digest, the wrong one here, where
    /// the staged world is the one save that must never be captured from.
    /// </summary>
    public static string? FindCaptureSource(string savesDirectory)
    {
        return EnumerateCaptureSources(savesDirectory).FirstOrDefault();
    }

    /// <summary>
    /// Returns whether the save archive is complete enough for character extraction.  The
    /// engine exposes a directory as soon as it starts writing a quicksave, so existence and
    /// mtime alone are not a commit indication.  Keep this check separate from
    /// <see cref="FindCaptureSource"/>: ordinary save identity polling must remain cheap, while
    /// the bounded post-quicksave wait can afford to parse the candidate it is about to capture.
    /// </summary>
    public static bool IsCaptureSourceReady(string? saveDirectory)
    {
        if (string.IsNullOrEmpty(saveDirectory))
        {
            return false;
        }

        try
        {
            FileInfo save = new(Path.Combine(saveDirectory, "SAVEGAME.sav"));
            if (!save.Exists || save.Length == 0)
            {
                return false;
            }

            return PlayerIfo.TryExtractPlayer(saveDirectory, out _, out _);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    /// <summary>
    /// Captures from a load source whose exact directory was observed in a fresh native load
    /// request and whose SAVEGAME archive still matches the request-time fingerprint. This is
    /// the only path allowed to read the staged AUTOSAVE slot: ordinary captures must keep
    /// rejecting it because it normally contains the host's character.
    /// </summary>
    public static bool TryCaptureVerifiedLoadSource(
        string savesDirectory,
        string sourceDirectory,
        string expectedSha256,
        long expectedLength,
        uint hostSaveIdentity,
        bool allowStagedDirectory,
        out string diagnostic)
    {
        string root = Path.GetFullPath(savesDirectory);
        string source = Path.GetFullPath(sourceDirectory);
        bool staged = string.Equals(Path.GetFileName(source), "000001 - AUTOSAVE", StringComparison.OrdinalIgnoreCase);
        if (!source.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || staged && !allowStagedDirectory)
        {
            diagnostic = "the verified load source is outside this saves folder or is an unproven staged autosave";
            return false;
        }

        if (!TryHashSave(source, out string beforeHash, out long beforeLength)
            || beforeLength != expectedLength
            || !string.Equals(beforeHash, expectedSha256, StringComparison.Ordinal))
        {
            diagnostic = "the selected save changed after its load request - kept the existing character";
            return false;
        }

        if (!PlayerIfo.TryExtractPlayer(source, out GffStruct player, out diagnostic))
        {
            return false;
        }

        if (!TryHashSave(source, out string afterHash, out long afterLength)
            || afterLength != expectedLength
            || !string.Equals(afterHash, expectedSha256, StringComparison.Ordinal))
        {
            diagnostic = "the selected save changed while its character was being read - kept the existing character";
            return false;
        }

        return TryWriteSidecar(savesDirectory, player, hostSaveIdentity, DateTime.UtcNow, out diagnostic);
    }

    /// <summary>Stable content fingerprint for the archive a confirmed load request names.</summary>
    public static bool TryHashSave(string saveDirectory, out string sha256, out long length)
    {
        sha256 = string.Empty;
        length = 0;
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            string[] inputs = ["SAVEGAME.sav", "savenfo.res", "PARTYTABLE.res"];
            byte[] lengthBytes = new byte[sizeof(long)];
            bool saveArchivePresent = false;
            foreach (string name in inputs)
            {
                byte[] nameBytes = Encoding.ASCII.GetBytes(name);
                hash.AppendData(nameBytes);
                string path = Path.Combine(saveDirectory, name);
                if (!File.Exists(path))
                {
                    hash.AppendData([0]);
                    continue;
                }

                using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                long fileLength = stream.Length;
                if (name == "SAVEGAME.sav")
                {
                    if (fileLength <= 0) return false;
                    saveArchivePresent = true;
                }
                BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, fileLength);
                hash.AppendData([1]);
                hash.AppendData(lengthBytes);
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hash.AppendData(buffer.AsSpan(0, read));
                }
                length = checked(length + fileLength);
            }

            if (!saveArchivePresent) return false;
            sha256 = Convert.ToHexString(hash.GetHashAndReset());
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateCaptureSources(string savesDirectory)
    {
        try
        {
            if (!Directory.Exists(savesDirectory))
            {
                return [];
            }

            return new DirectoryInfo(savesDirectory)
                .GetDirectories()
                .Select(d => new FileInfo(Path.Combine(d.FullName, "SAVEGAME.sav")))
                .Where(f => f.Exists)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.DirectoryName!)
                .Where(d => !StagedSave.IsStagedCopy(savesDirectory, d))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    /// <summary>
    /// Rechecks the source at the capture boundary after an engine save request. The
    /// quicksave can make an intermediate file visible before its <c>SAVEGAME.sav</c>
    /// timestamp moves; callers that observed a source while waiting must therefore resolve
    /// again immediately before extraction. Keep the observed source as a teardown fallback
    /// when the save tree disappears between the two reads.
    /// </summary>
    public static string? RefreshCaptureSource(string savesDirectory, string? observedSource)
    {
        // Walk all candidates, not just the newest one: while the engine is replacing
        // QUICKSAVE, its new archive can be visible with a newer mtime before it is parseable.
        // In that interval the last complete candidate is the only safe teardown fallback.
        foreach (string candidate in EnumerateCaptureSources(savesDirectory))
        {
            if (IsCaptureSourceReady(candidate))
            {
                return candidate;
            }
        }

        return IsCaptureSourceReady(observedSource) ? observedSource : null;
    }

    /// <summary>
    /// Extracts the player out of <paramref name="saveDirectory"/> — this install's own,
    /// most recently written save (<see cref="FindCaptureSource"/>) — and writes it as the
    /// sidecar. Best-effort: every failure reports through <paramref name="diagnostic"/>
    /// and never throws.
    ///
    /// Two refusals keep a capture from making the sidecar WORSE, both reported as
    /// <see cref="CharacterCapture.Kept"/> (the sidecar on disk stays as it was):
    /// <list type="bullet">
    /// <item>the source is the staged copy of the host's world — its player is the HOST's
    /// PC, and carrying that into the next join is the 2026-09-03 live bug
    /// (<see cref="StagedSave"/>);</item>
    /// <item>the source was written before the sidecar was captured — a disconnect with no
    /// save since the last capture would otherwise roll the character back to whatever
    /// older save happens to be newest on disk.</item>
    /// </list>
    /// </summary>
    public static CharacterCapture Capture(
        string savesDirectory,
        string saveDirectory,
        uint hostSaveIdentity,
        out string diagnostic) =>
        Capture(savesDirectory, saveDirectory, hostSaveIdentity, DateTime.MinValue, out diagnostic);

    /// <summary>
    /// <see cref="Capture(string, string, uint, out string)"/> with a third refusal: a source
    /// written before <paramref name="connectedSinceUtc"/> (the start of the connection the
    /// capture belongs to) is a single-player save, not this session's, and is never lifted
    /// into the co-op character. Every in-session capture path runs while connected and every
    /// save the session itself makes is captured as it lands, so the only thing this refuses
    /// is the newest-save FALLBACK (a refused pre-disconnect/periodic quicksave) landing on a
    /// save the player made on their own between sessions — which is newer than the sidecar,
    /// so the rollback rule above does not catch it (docs/research/key-items-and-solo.md §5).
    /// <see cref="DateTime.MinValue"/> = no floor.
    /// </summary>
    public static CharacterCapture Capture(
        string savesDirectory,
        string saveDirectory,
        uint hostSaveIdentity,
        DateTime connectedSinceUtc,
        out string diagnostic)
    {
        string sourceName = Path.GetFileName(saveDirectory);

        if (StagedSave.IsStagedCopy(savesDirectory, saveDirectory))
        {
            diagnostic = $"'{sourceName}' is the staged copy of the host's world, not this install's own save - kept the sidecar";
            return CharacterCapture.Kept;
        }

        if (connectedSinceUtc != DateTime.MinValue
            && TryGetSaveLastWriteUtc(saveDirectory, out DateTime writtenUtc)
            && writtenUtc < connectedSinceUtc)
        {
            diagnostic = $"'{sourceName}' (written {writtenUtc:u}) predates this co-op connection ({connectedSinceUtc:u}) - "
                + "written before this connection began, so it is a single-player save; kept the sidecar";
            return CharacterCapture.Kept;
        }

        if (TryReadSidecar(savesDirectory, out _, out SidecarMetadata existing, out _)
            && TryGetSaveLastWriteUtc(saveDirectory, out DateTime sourceWrittenUtc)
            && sourceWrittenUtc <= existing.CapturedAtUtc)
        {
            diagnostic = $"'{sourceName}' (written {sourceWrittenUtc:u}) is no newer than the sidecar (captured {existing.CapturedAtUtc:u}) - kept the sidecar";
            return CharacterCapture.Kept;
        }

        if (!PlayerIfo.TryExtractPlayer(saveDirectory, out GffStruct player, out diagnostic))
        {
            return CharacterCapture.Failed;
        }

        string extracted = diagnostic;

        if (!TryWriteSidecar(savesDirectory, player, hostSaveIdentity, DateTime.UtcNow, out diagnostic))
        {
            return CharacterCapture.Failed;
        }

        diagnostic = $"{extracted}; {diagnostic}";
        return CharacterCapture.Written;
    }

    private static bool TryGetSaveLastWriteUtc(string saveDirectory, out DateTime lastWriteUtc)
    {
        lastWriteUtc = DateTime.MinValue;
        try
        {
            FileInfo save = new(Path.Combine(saveDirectory, "SAVEGAME.sav"));
            if (!save.Exists)
            {
                return false;
            }

            lastWriteUtc = save.LastWriteTimeUtc;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Writes the sidecar pair atomically. Public on its own (not folded into
    /// <see cref="TryCaptureAndWrite"/>) so a test can drive it directly from an in-memory
    /// <see cref="GffStruct"/> with no save directory involved.</summary>
    public static bool TryWriteSidecar(
        string savesDirectory,
        GffStruct player,
        uint hostSaveIdentity,
        DateTime capturedAtUtc,
        out string diagnostic)
    {
        ArgumentNullException.ThrowIfNull(player);

        try
        {
            Directory.CreateDirectory(savesDirectory);

            byte[] pifo = PlayerIfo.BuildPifo(player);
            string json = JsonSerializer.Serialize(new SidecarMetadata(hostSaveIdentity, capturedAtUtc), JsonOptions);

            string pifoPath = Path.Combine(savesDirectory, PifoFileName);
            string metaPath = Path.Combine(savesDirectory, MetaFileName);

            // The pifo FIRST, the json LAST. The json's mere presence (with a timestamp a
            // reader trusts) is what a reader treats as "the pifo beside it is complete" —
            // writing the other order would let a reader see a fresh timestamp pointing at a
            // pifo that has not landed yet.
            WriteAtomic(pifoPath, pifo);
            WriteAtomic(metaPath, Encoding.UTF8.GetBytes(json));

            diagnostic = $"wrote the character sidecar ({pifo.Length} bytes, captured {capturedAtUtc:u})";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"could not write the character sidecar: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Reads the sidecar back. Tolerant by construction: either file missing, unparsable
    /// json, or a pifo that will not parse are all reported as "no sidecar", never thrown —
    /// this is what "every read tolerant of a half-written sidecar" means in practice.
    /// </summary>
    public static bool TryReadSidecar(
        string savesDirectory, out GffStruct player, out SidecarMetadata metadata, out string diagnostic)
    {
        player = null!;
        metadata = SidecarMetadata.None;

        string pifoPath = Path.Combine(savesDirectory, PifoFileName);
        string metaPath = Path.Combine(savesDirectory, MetaFileName);

        try
        {
            if (!File.Exists(pifoPath) || !File.Exists(metaPath))
            {
                diagnostic = "no session-saved character sidecar";
                return false;
            }

            byte[] pifoBytes = File.ReadAllBytes(pifoPath);
            string json = File.ReadAllText(metaPath);

            SidecarMetadata? parsed = JsonSerializer.Deserialize<SidecarMetadata>(json, JsonOptions);
            if (parsed is null)
            {
                diagnostic = "the character sidecar's metadata is corrupt";
                return false;
            }

            if (!PlayerIfo.TryExtractPlayerFromPifoBytes(pifoBytes, out player, out diagnostic))
            {
                return false;
            }

            metadata = parsed;
            diagnostic = $"the session-saved character (captured {metadata.CapturedAtUtc:u})";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or JsonException or FormatException)
        {
            diagnostic = $"could not read the character sidecar: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// The pure preference rule (roadmap item 14 phase 1). A valid, strictly newer sidecar
    /// wins over the client's own save; a missing/invalid sidecar, one no newer than the own
    /// save, or one captured against a DIFFERENT host save (only when BOTH identities are
    /// actually known — 0 is "unknown" and, per the house convention every other
    /// compatibility check in this tree already follows, an unknown value never blocks) all
    /// fall back to the client's own save. Pure and deterministic so it is testable with no
    /// filesystem at all — see <c>ClientCharacterPersistenceTests</c>.
    /// </summary>
    public static bool ShouldPreferSidecar(
        bool sidecarValid,
        DateTime sidecarCapturedAtUtc,
        DateTime ownSaveLastWriteUtc,
        uint sidecarHostIdentity,
        uint currentHostIdentity,
        out string reason)
    {
        if (!sidecarValid)
        {
            reason = "no valid session-saved character";
            return false;
        }

        if (sidecarCapturedAtUtc <= ownSaveLastWriteUtc)
        {
            reason = "the client's own save is newer than the session-saved character";
            return false;
        }

        if (sidecarHostIdentity != 0 && currentHostIdentity != 0 && sidecarHostIdentity != currentHostIdentity)
        {
            reason = "the session-saved character was captured against a different host save";
            return false;
        }

        reason = $"the session-saved character (captured {sidecarCapturedAtUtc:u}) is newer than the own save";
        return true;
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.Move(tmp, path, overwrite: true);
    }
}

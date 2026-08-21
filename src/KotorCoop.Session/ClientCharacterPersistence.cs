using System.Text;
using System.Text.Json;
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
    /// Extracts the player out of <paramref name="saveDirectory"/> — the client's own,
    /// currently-active save (whatever <c>SaveDigest.FindSessionSave</c> is watching) — and
    /// writes it as the sidecar. Best-effort: every failure reports through
    /// <paramref name="diagnostic"/> and never throws.
    /// </summary>
    public static bool TryCaptureAndWrite(
        string savesDirectory,
        string saveDirectory,
        uint hostSaveIdentity,
        out string diagnostic)
    {
        if (!PlayerIfo.TryExtractPlayer(saveDirectory, out GffStruct player, out diagnostic))
        {
            return false;
        }

        string extracted = diagnostic;

        if (!TryWriteSidecar(savesDirectory, player, hostSaveIdentity, DateTime.UtcNow, out diagnostic))
        {
            return false;
        }

        diagnostic = $"{extracted}; {diagnostic}";
        return true;
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

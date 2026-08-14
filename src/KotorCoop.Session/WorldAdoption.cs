using System.Net;
using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;

namespace KotorCoop.Session;

/// <summary>What an adoption produced, for the log, the dashboard and the caller's own
/// sanity check.</summary>
/// <param name="Module">The module the adopted world stands in.</param>
/// <param name="SaveIdentity">The staged world's save-identity digest — the number the
/// whole object-id-keyed half of the session rests on. Compare it against the host's.</param>
/// <param name="Bytes">How much world crossed the wire.</param>
public readonly record struct AdoptedWorld(string Module, uint SaveIdentity, long Bytes);

/// <summary>
/// A joining player adopts the host's world and carries their own character into it.
///
/// THE PROBLEM THIS SOLVES. Every object-id-keyed channel — enemies, doors, placeables,
/// container loot, encounters — assumes both engines dealt the same object ids, which is
/// only true when both loaded the same save. <c>ContentCompatibility</c> could detect the
/// assumption failing and refuse the join (<c>RefusalReason.SaveGame</c>), but nothing could
/// make it hold. That left the co-op session unable to admit a player who had ever played
/// on their own: their <c>Mod_NextObjId</c> counters and stored GITs had moved, and no
/// amount of re-keying on the wire brings two different worlds back together.
///
/// THE SHAPE. The host's save directory is the world; the joiner's own save contributes
/// exactly one thing, their character, lifted out as <c>Temp\pifo.ifo</c>
/// (<see cref="PlayerIfo"/>) and logged in by the engine's own
/// <c>LoadCharacterFromIFO(-1)</c> path. Afterwards both installs' save identities are equal
/// BY CONSTRUCTION rather than by luck — the digest check that used to gate the join now
/// passes because the thing it measures was made true.
///
/// WHAT THE JOINER GIVES UP, stated plainly because it is a real cost: the world they were
/// playing. Party, inventory, companions, globals and journal all come from the host's save,
/// because in co-op those are shared state. Their own save on disk is never touched — only
/// the autosave slot is written — so leaving the session and loading their own game again
/// costs nothing. This is the trade Seamless Co-op makes too: you bring your character, the
/// host brings the world.
/// </summary>
public static class WorldAdoption
{
    /// <summary>
    /// Fetches the host's world, stages it over the autosave slot, and carries the character
    /// from <paramref name="ownSaveDirectory"/> into it.
    ///
    /// ⚠ ORDER IS LOAD-BEARING. The character is extracted FIRST, before a single byte is
    /// fetched or written, because that is the step most likely to fail (an unreadable or
    /// characterless save) and the only one whose failure must not cost the joiner their
    /// autosave slot. Nothing is destroyed until the whole operation is known to be able to
    /// finish.
    /// </summary>
    /// <param name="expectedSaveIdentity">The host's own save-identity digest, if known.
    /// Non-zero makes the adoption self-checking: a staged world that does not fingerprint
    /// identical to the host's is reported as a failure rather than silently played on. 0
    /// skips the check.</param>
    public static async Task<AdoptedWorld?> TryAdoptAsync(
        string gameDirectory,
        string ownSaveDirectory,
        IPEndPoint host,
        uint passwordHash,
        uint expectedSaveIdentity,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        // 1. Our character, before anything is touched.
        if (!PlayerIfo.TryExtractPlayer(ownSaveDirectory, out GffStruct player, out string diagnostic))
        {
            log?.Invoke($"world adoption: {diagnostic}");
            return null;
        }

        log?.Invoke($"world adoption: {diagnostic}");

        // 2. The host's world.
        FetchedWorld fetched;
        try
        {
            fetched = await WorldTransfer.FetchAsync(host, passwordHash, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
            or System.Net.Sockets.SocketException)
        {
            log?.Invoke($"world adoption: could not fetch the host's world — {ex.Message}");
            return null;
        }

        IReadOnlyList<WorldFile> world = fetched.Files;

        // The host states the identity of what it served (transfer v2). A caller that already
        // knows it from elsewhere wins — it is the more trustworthy of the two, having come
        // from the session rather than from the machine we are about to copy files from — but
        // otherwise this is what turns the staging into a self-check instead of a hope.
        uint expected = expectedSaveIdentity != 0 ? expectedSaveIdentity : fetched.SaveIdentity;

        long bytes = 0;
        foreach (WorldFile file in world)
        {
            bytes += file.Data.Length;
        }

        // 3. Stage it, and carry the character in. Both writes, then the verification.
        string slot = Path.Combine(gameDirectory, "saves", PluginMenuLoader.AutosaveSlot);

        try
        {
            WorldTransfer.WriteSaveDirectory(slot, world);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"world adoption: could not stage the world — {ex.Message}");
            return null;
        }

        if (!PlayerIfo.TryWritePifo(gameDirectory, player, out diagnostic))
        {
            log?.Invoke($"world adoption: {diagnostic}");
            return null;
        }

        // 4. Prove it. This is the check the whole design exists to be able to pass: the
        //    staged world must fingerprint identically to the host's, because that digest is
        //    what every object-id-keyed channel is really asserting.
        if (!SaveDigest.TryComputeSaveIdentity(slot, out uint identity))
        {
            log?.Invoke("world adoption: the staged world could not be fingerprinted");
            return null;
        }

        if (expected != 0 && identity != expected)
        {
            log?.Invoke(
                $"world adoption: the staged world fingerprints 0x{identity:X8}, "
                + $"the host reported 0x{expected:X8} — refusing to play a world we did not receive");
            return null;
        }

        if (expected == 0)
        {
            // Not fatal — an unknown never blocks, here as everywhere — but worth saying,
            // because it means the one check that could have caught a bad transfer did not run.
            log?.Invoke("world adoption: the host did not state its save identity; staging unverified");
        }

        PluginMenuLoader.TryReadSaveModuleName(slot, out string module);

        log?.Invoke(
            $"world adoption: adopted '{module}' ({bytes:N0} bytes, identity 0x{identity:X8})");

        return new AdoptedWorld(module, identity, bytes);
    }

    /// <summary>
    /// Re-asserts <c>Temp\pifo.ifo</c> from the already-staged autosave slot's neighbour —
    /// the joiner's own save — immediately before the load fires.
    ///
    /// ⚠ Why this exists as a separate call: <c>LoadPCAutoSave</c> exports the staged save
    /// and copies <c>&lt;export&gt;\pifo</c> over <c>TEMP:pifo</c>, and although staging
    /// neutralises the pifo inside the world we received, whether that function otherwise
    /// disturbs <c>Temp\</c> on its own path is NOT YET MEASURED on the live game. Writing
    /// the file again just before the login reads it is idempotent, costs a millisecond, and
    /// removes the question. The read happens frames later — <c>LoadCharacterFromIFO</c>
    /// registers <c>TEMP:</c> itself (0x561EAE) at login time, seconds after the load starts.
    /// </summary>
    public static bool TryReassertCharacter(
        string gameDirectory, string ownSaveDirectory, out string diagnostic) =>
        PlayerIfo.TryStagePlayer(gameDirectory, ownSaveDirectory, out diagnostic);
}

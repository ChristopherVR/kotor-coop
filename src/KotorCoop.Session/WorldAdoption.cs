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
/// <param name="CarriedPlayer">The character that was actually staged into
/// <c>Temp\pifo.ifo</c> — the client's own save, or a roadmap-item-14 session-saved sidecar,
/// whichever <see cref="WorldAdoption.TryAdoptAsync"/> picked. Callers that re-assert the
/// pifo later (a truncation the loader caused) write THIS back, never re-derive it from
/// disk — re-deriving would silently lose a sidecar preference on every re-assert.</param>
/// <param name="CharacterSource">A one-line description of where <see cref="CarriedPlayer"/>
/// came from, for the on-screen line and the log.</param>
public readonly record struct AdoptedWorld(
    string Module, uint SaveIdentity, long Bytes, GffStruct CarriedPlayer, string CharacterSource, string[]? CompanionCacheFingerprints = null);

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
    public static Task<AdoptedWorld?> TryAdoptAsync(
        string gameDirectory,
        string ownSaveDirectory,
        IPEndPoint host,
        uint passwordHash,
        uint expectedSaveIdentity,
        Action<string>? log,
        CancellationToken cancellationToken,
        bool udpFallback = false,
        Action? onNoWorldAvailable = null,
        bool inventoryPersonal = false,
        bool creditsPersonal = false) =>
        TryAdoptAsync(
            gameDirectory, ownSaveDirectory, sidecar: null, host, passwordHash, expectedSaveIdentity, log,
            cancellationToken, udpFallback, onNoWorldAvailable, inventoryPersonal, creditsPersonal);

    /// <summary>
    /// Roadmap item 14 phase 1 overload: also considers a "resume tomorrow" sidecar
    /// (<c>KotorCoop.Session.ClientCharacterPersistence</c>) captured at the end of a
    /// PREVIOUS session, and prefers it over <paramref name="ownSaveDirectory"/> when
    /// <see cref="ClientCharacterPersistence.ShouldPreferSidecar"/> says so — decided AFTER
    /// the host's world is fetched, because that is the earliest point the host's real save
    /// identity is known and the preference rule can compare against it. The own-save
    /// character is still extracted first and kept as the fallback: reading it is cheap and
    /// non-destructive, so the fail-fast behaviour ("do not touch the autosave slot for a
    /// joiner with no character to carry") is unchanged whichever source ends up used.
    /// </summary>
    /// <param name="sidecar">A previously captured character to consider preferring, or null
    /// to behave exactly as the simpler overload (no sidecar in play at all).</param>
    /// <param name="inventoryPersonal">coop.ini <c>inventory_mode = personal</c> (negotiated —
    /// the caller passes <c>session.Overrides.InventoryPersonal ?? settings.InventoryPersonal</c>
    /// or the best equivalent it has). Default false (today's/`shared` behaviour) so every
    /// pre-existing caller and test is unaffected. When true,
    /// <see cref="PersonalInventoryGraft"/> replaces the staged world's party gold/backpack
    /// with the joiner's own — see that class for why (docs/research/personal-inventory.md).</param>
    /// <param name="creditsPersonal">coop.ini <c>credits_mode = personal</c>, same idiom.</param>
    public static async Task<AdoptedWorld?> TryAdoptAsync(
        string gameDirectory,
        string ownSaveDirectory,
        (GffStruct Player, SidecarMetadata Metadata)? sidecar,
        IPEndPoint host,
        uint passwordHash,
        uint expectedSaveIdentity,
        Action<string>? log,
        CancellationToken cancellationToken,
        bool udpFallback = false,
        Action? onNoWorldAvailable = null,
        bool inventoryPersonal = false,
        bool creditsPersonal = false)
    {
        // 1. Our own character, before anything is touched. Always extracted, whether or not
        //    a sidecar is in play: it is the fallback the moment the sidecar turns out not to
        //    apply, and reading it costs nothing destructive.
        if (!PlayerIfo.TryExtractPlayer(ownSaveDirectory, out GffStruct ownPlayer, out string diagnostic))
        {
            log?.Invoke($"world adoption: {diagnostic}");
            if (sidecar is not null)
            {
                log?.Invoke(
                    "world adoption: refusing to substitute the session-saved character because its metadata "
                    + $"does not bind it to the selected save '{Path.GetFileName(ownSaveDirectory)}'; "
                    + "choose a readable own save, or use join_mode = auto to carry this install's co-op character");
            }

            return null;
        }

        log?.Invoke($"world adoption: {diagnostic}");

        string ownSource = $"'{Path.GetFileName(ownSaveDirectory)}' (own save)";

        return await FetchStageAndVerifyAsync(
            gameDirectory,
            host,
            passwordHash,
            expectedSaveIdentity,
            log,
            // 2b. Decided AFTER the fetch, deliberately, not before: the preference rule's
            //     host-identity check needs the save identity the fetch just learned, and
            //     reading a sidecar (like reading the own save) is non-destructive, so nothing
            //     about the fail-fast order in step 1 is weakened by deciding late.
            expected =>
            {
                if (sidecar is not { } carried)
                {
                    return (ownPlayer, ownSource);
                }

                DateTime ownSaveLastWriteUtc = TryGetOwnSaveLastWriteUtc(ownSaveDirectory);

                bool prefer = ClientCharacterPersistence.ShouldPreferSidecar(
                    sidecarValid: true,
                    carried.Metadata.CapturedAtUtc,
                    ownSaveLastWriteUtc,
                    carried.Metadata.HostSaveIdentity,
                    expected,
                    out string reason);

                if (prefer)
                {
                    return (carried.Player,
                        $"the session-saved character (captured {carried.Metadata.CapturedAtUtc:u}) — {reason}");
                }

                log?.Invoke($"world adoption: session-saved character not used — {reason}");
                return (ownPlayer, ownSource);
            },
            cancellationToken, udpFallback, onNoWorldAvailable, inventoryPersonal, creditsPersonal)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The first-time joiner's adoption: the character is HANDED IN rather than lifted out of a
    /// save, because there is no save to lift it out of.
    ///
    /// WHAT THIS IS FOR. coop.ini <c>join_mode = newcharacter</c>. A player joining a co-op
    /// session on a fresh install has never played, so every other overload here fails at its
    /// very first step — correctly, and without touching anything ("the save's
    /// <c>Mod_PlayerList</c> is empty (no character to carry)"). They go through the game's own
    /// New Game / character-creation screens instead, and the character retail chargen writes to
    /// <c>TEMP:pifo</c> (<c>CSWCCreature::SaveCharGenCreature 0x6123E0</c>) is what arrives
    /// here. It is the same shape <see cref="PlayerIfo.BuildPifo"/> emits — a one-entry
    /// <c>Mod_PlayerList</c> — so from this point on the adoption is bit-for-bit the ordinary
    /// one, with a different provenance.
    ///
    /// ⚠ THERE IS NOTHING TO FAIL FAST ON HERE, and that is a real difference. The other
    /// overloads extract the character before a single byte is fetched or written, because an
    /// unreadable save is the likeliest failure and must not cost the joiner their autosave
    /// slot. Here the character is already in hand and already validated by the caller, so the
    /// fetch is the first thing that can fail — which is also the first thing that would have
    /// failed anyway.
    /// </summary>
    /// <param name="carriedPlayer">The character to carry in. Its <c>ObjectId</c> is the
    /// engine's own <c>0x7FFFFFFF</c> sentinel on a chargen creature exactly as on a saved one,
    /// so the engine mints a fresh id on load and it cannot collide with anything already in
    /// the host's world.</param>
    /// <param name="characterSource">A one-line description for the log and the on-screen line
    /// — where this character came from.</param>
    public static Task<AdoptedWorld?> TryAdoptWithCharacterAsync(
        string gameDirectory,
        GffStruct carriedPlayer,
        string characterSource,
        IPEndPoint host,
        uint passwordHash,
        uint expectedSaveIdentity,
        Action<string>? log,
        CancellationToken cancellationToken,
        bool udpFallback = false,
        Action? onNoWorldAvailable = null,
        bool inventoryPersonal = false,
        bool creditsPersonal = false)
    {
        ArgumentNullException.ThrowIfNull(carriedPlayer);

        return FetchStageAndVerifyAsync(
            gameDirectory, host, passwordHash, expectedSaveIdentity, log,
            _ => (carriedPlayer, characterSource), cancellationToken, udpFallback, onNoWorldAvailable,
            inventoryPersonal, creditsPersonal);
    }

    /// <summary>How long <see cref="ProbeTcpReachableAsync"/> waits for a bare connect before
    /// concluding the port is not reachable at all — far short of a real transfer's own
    /// generous timeout, since this only ever answers one question ("does anything answer a
    /// SYN here"), not "how slow is the link". Chosen from the live 2026-09-10 failure this
    /// exists for: a UDP-only tunnel path left the SYN with no reply at all, which a real host
    /// answers within one RTT — even a slow one.</summary>
    private const int TcpProbeTimeoutMs = 5_000;

    /// <summary>
    /// A bare TCP connect, immediately closed — answers only "does the transfer port take a
    /// connection at all", never reading or writing a single protocol byte. Used to decide
    /// between <see cref="WorldTransfer.FetchAsync"/> and <see cref="WorldTransferUdp.FetchAsync"/>
    /// without risking a slow real transfer racing against this probe's own short timeout.
    /// </summary>
    private static async Task<bool> ProbeTcpReachableAsync(IPEndPoint host, CancellationToken cancellationToken)
    {
        using System.Net.Sockets.TcpClient probe = new();
        using CancellationTokenSource timeoutCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TcpProbeTimeoutMs);

        try
        {
            await probe.ConnectAsync(host, timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException)
        {
            return false;
        }
    }

    private static DateTime TryGetOwnSaveLastWriteUtc(string saveDirectory)
    {
        try
        {
            string savegame = Path.Combine(saveDirectory, "SAVEGAME.sav");
            return File.Exists(savegame) ? File.GetLastWriteTimeUtc(savegame) : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// Everything after the character is decided: fetch the host's world, stage it over the
    /// autosave slot, write the pifo, and prove the staged world fingerprints the same as the
    /// host's.
    ///
    /// <paramref name="chooseCharacter"/> is invoked once, AFTER the fetch, and is handed the
    /// save identity the fetch established — because the one caller that has a choice to make
    /// (own save versus session-saved sidecar) cannot make it before that number exists.
    /// </summary>
    private static async Task<AdoptedWorld?> FetchStageAndVerifyAsync(
        string gameDirectory,
        IPEndPoint host,
        uint passwordHash,
        uint expectedSaveIdentity,
        Action<string>? log,
        Func<uint, (GffStruct Player, string Source)> chooseCharacter,
        CancellationToken cancellationToken,
        bool udpFallback = false,
        Action? onNoWorldAvailable = null,
        bool inventoryPersonal = false,
        bool creditsPersonal = false)
    {
        // 2. The host's world. A bare connect probe first when the UDP fallback is enabled, so
        //    a genuinely unreachable TCP listener (found live 2026-09-10: a UDP-only tunnel/NAT
        //    path forwards the co-op session's own UDP traffic fine but silently drops this
        //    channel) is answered by WorldTransferUdp instead of the ordinary 60s TCP timeout.
        //    Skipped entirely when the fallback is off, so a build/coop.ini that has not opted
        //    in pays no extra round trip and behaves exactly as before.
        FetchedWorld fetched;
        try
        {
            bool useUdp = udpFallback && !await ProbeTcpReachableAsync(host, cancellationToken).ConfigureAwait(false);
            if (useUdp)
            {
                log?.Invoke(
                    "world adoption: the world-transfer port did not answer TCP - "
                    + "falling back to the UDP path (world_transfer_udp_fallback)");
            }

            fetched = useUdp
                ? await WorldTransferUdp.FetchAsync(host, passwordHash, cancellationToken).ConfigureAwait(false)
                : await WorldTransfer.FetchAsync(host, passwordHash, cancellationToken).ConfigureAwait(false);
        }
        catch (WorldTransferRefusedException ex) when (ex.NoWorldAvailable)
        {
            // The single most common first-time-host mistake: Host pressed before ever
            // saving. Distinct from every other refusal/transport failure below — this is
            // not a bug, a version mismatch or a wrong password, and burying it as "could not
            // fetch the host's world — world transfer: the host has no world to send yet"
            // reads exactly like one. See WorldTransferRefusedException's own doc comment.
            log?.Invoke(onNoWorldAvailable is null
                ? "world adoption: host has no save to share yet - ask them to save once "
                    + "(or reach the first autosave) and try again"
                : "world adoption: host has not made a save available yet");
            onNoWorldAvailable?.Invoke();
            return null;
        }
        catch (Exception ex) when (ex is WorldTransferRefusedException or InvalidDataException
            or IOException or System.Net.Sockets.SocketException)
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

        // 2b. NOW that the host's real save identity is known, the caller picks the character.
        (GffStruct player, string characterSource) = chooseCharacter(expected);

        // 3. Stage it, and carry the character in. Both writes, then the verification.
        string slot = Path.Combine(gameDirectory, "saves", PluginMenuLoader.AutosaveSlot);

        try
        {
            WorldTransfer.WriteSaveDirectory(slot, world);
            PersonalPazaak.Restore(player, slot);

            // docs/research/personal-inventory.md's rejoin-duplication fix. Must run BEFORE
            // CoopSaveRedirect.MirrorIntoCoopSlot below: that call overwrites the dedicated
            // Co-op slot with a copy of the world just staged here, so the joiner's own
            // PREVIOUS capture (gold/backpack) has to be read out of that slot before this
            // adoption's own mirror clobbers it. A no-op in both directions when both modes
            // are `shared` — see PersonalInventoryGraft's own doc comment.
            PersonalInventoryGraft.Apply(gameDirectory, slot, inventoryPersonal, creditsPersonal, log);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            log?.Invoke($"world adoption: could not stage the world — {ex.Message}");
            return null;
        }

        // 3a. Say which save is the host's. The slot just written is a byte copy of the host's
        //     world, host PC included, and the engine will later reuse the very same slot for
        //     the joiner's own transition autosave — the character captures (SessionSaveWatch)
        //     tell the two apart only by this marker. Not fatal when it cannot be written; the
        //     line below is the evidence of which case ran.
        if (!StagedSave.TryRecord(Path.GetDirectoryName(slot)!, slot, out string markerDiagnostic))
        {
            log?.Invoke($"world adoption: {markerDiagnostic} - a later character capture may read the host's PC");
        }

        // Mirror the same bytes into the dedicated, player-visible co-op slot (docs/research/
        // session-save.md's 2026-09-25 entry). The AUTOSAVE copy above stays the mechanical
        // load target (the one native path MenuLoadController replays); this copy is what the
        // player sees in the retail Load Game screen as a normal, non-ephemeral "Co-op" save,
        // and what later capture-source lookups prefer once it exists. Best-effort: a failed
        // mirror never blocks an adoption that already succeeded.
        if (!CoopSaveRedirect.MirrorIntoCoopSlot(
                Path.GetDirectoryName(slot)!, slot, out _, out string mirrorDiagnostic))
        {
            log?.Invoke($"world adoption: {mirrorDiagnostic}");
        }

        if (!PlayerIfo.TryWritePifo(gameDirectory, player, out string pifoDiagnostic))
        {
            log?.Invoke($"world adoption: {pifoDiagnostic}");
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
            $"world adoption: adopted '{module}' ({bytes:N0} bytes, identity 0x{identity:X8}), "
                + $"carrying the character from {characterSource}");

        return new AdoptedWorld(module, identity, bytes, player, characterSource, PartyCacheFingerprint.ReadSave(slot));
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

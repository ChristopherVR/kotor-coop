using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// How a JOINER gets into the host's world — the production caller of <see cref="JoinModeRule"/>.
///
/// THE RULE SINCE 2026-09-03 (coop.ini <c>join_mode = auto</c>, the default): the mod owns the
/// joiner's co-op character. The first join on an install ALWAYS goes through the game's own
/// character-creation screens — whatever single-player saves happen to be on disk, none of
/// them is the co-op character — and the character that comes out is written to the
/// <c>saves\kotorcoop-character.*</c> sidecar (<see cref="ClientCharacterPersistence"/>). Every
/// later join carries THAT into the host's world, unconditionally; the sidecar is refreshed on
/// every in-game save the client makes while connected, so it always holds the latest co-op
/// progression. <c>join_mode = newcharacter</c> forces a fresh character even when one exists
/// (a reroll); <c>join_mode = save</c> is the old "carry my own most recent save's PC" path.
///
/// WHY THE FIRST-TIME PATH GOES THROUGH A SAVE. The first design of this flow rested on
/// docs/research/character-login-chain.md §2's claim that retail chargen writes
/// <c>TEMP:pifo</c> — the exact file adoption carries. Measured live 2026-08-29: it does not.
/// <c>CSWCCreature::SaveCharGenCreature 0x6123E0</c> never references the string and its own
/// tail writes <c>Temp\temp.bic</c>. So the character is obtained the only way KOTOR makes one
/// portable, and the way this codebase has always read one: out of a save.
///
/// THE FIRST-TIME STEPS:
/// <list type="number">
/// <item>ARM the plugin's capture (<c>NewCharacterCaptureController</c>) and press New Game for
/// the player (<c>CharGenDriveController</c>'s <c>newgame</c> — the replay of
/// <c>CSWGuiMainMenu::OnNewGamePicked</c>'s body that the console verb already ships; if the
/// press is refused the player is told to click it themselves, exactly as before).</item>
/// <item>The player creates their character and lands in the Endar Spire as any new game does.
/// Nothing of ours runs during it.</item>
/// <item>The plugin notices the world is up, asks the engine to SAVE (retail's own quicksave),
/// and names the save it wrote. The character is read out of it with
/// <c>PlayerIfo.TryExtractPlayer</c> — byte-for-byte the extraction <c>join_mode = save</c>
/// has always used — and written to the sidecar AT ONCE, so it persists even if the adoption
/// that follows fails or the player quits before their first in-game save.</item>
/// <item>Adopt the host's world carrying it (<c>SessionManager.AdoptWithNewCharacterAsync</c>
/// → the existing <c>WorldTransfer</c>/staging/verification chain, with the character handed in
/// rather than extracted).</item>
/// <item>Reconnect, so the session re-handshakes against a world that now fingerprints the same
/// as the host's — which is the whole point: the digests agree BY CONSTRUCTION and every
/// object-id-keyed channel comes back on.</item>
/// </list>
///
/// The every-later-join path is steps 4-5 with the sidecar's character handed in, and runs
/// from the MAIN MENU: <c>MenuLoadController</c> takes its main-menu route whenever no server
/// exists, whatever the request's in-session flag says, so the same adoption call serves both.
/// </summary>
internal static class JoinFlow
{
    /// <summary>How often the capture is polled while the player is in character creation.
    /// Slow on purpose: this is a human at a menu, not a game loop.</summary>
    private const int CapturePollMs = 500;

    /// <summary>How long the adopted-world load gets before it is called a failure. The same
    /// budget <c>AdoptCommand</c> and the rejoin path already allow, and a module load on a
    /// cold disk is genuinely slow.</summary>
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Runs the whole flow. Never throws into its caller: every failure leaves the player
    /// exactly where they were — at a menu, or in the single-player game chargen just made for
    /// them — with a line saying why, which is strictly better than a half-joined session.
    /// </summary>
    /// <param name="loadOwnSave">What <see cref="JoinAction.LoadOwnSave"/> and
    /// <see cref="JoinAction.AdoptWithOwnCharacter"/> run — the <c>join_mode = save</c> path,
    /// which loads this install's own most recent save and lets the post-handshake save-identity
    /// mismatch adopt the host's world if the worlds differ (the adoption is decided there,
    /// against the host's REAL identity, which is unknowable at the menu). Null when the caller
    /// owns that step itself (the CLI <c>--join</c>, whose caller has always loaded the save).
    /// </param>
    public static async Task RunAsync(
        SessionManager manager,
        IGameBridge bridge,
        string gameDirectory,
        JoinMode mode,
        bool autoAdoptEnabled,
        Action? loadOwnSave,
        Action<string> say,
        CancellationToken token)
    {
        try
        {
            await RunCoreAsync(manager, bridge, gameDirectory, mode, autoAdoptEnabled, loadOwnSave, say, token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A shutting-down agent must not leave the player mid-adoption with a message
            // implying something went wrong; it did not, it stopped.
        }
        catch (Exception ex)
        {
            say($"join: {ex.Message}");
        }
    }

    /// <summary>
    /// The decision alone, for callers that need to know whether the flow would do anything
    /// before starting it (the CLI join runs the flow only for the actions it owns).
    /// </summary>
    public static JoinAction Decide(string gameDirectory, JoinMode mode, bool autoAdoptEnabled, out string reason)
    {
        string savesDirectory = Path.Combine(gameDirectory, "saves");

        bool hasCoopCharacter = gameDirectory.Length > 0
            && ClientCharacterPersistence.TryReadSidecar(savesDirectory, out _, out _, out _);

        // Only consulted in join_mode = save, and cheap: resolving the newest save directory
        // and parsing one GFF out of it, nothing written.
        bool hasOwnCharacter = mode == JoinMode.Save
            && gameDirectory.Length > 0
            && PluginMenuLoader.TryResolveSaveDirectory(gameDirectory, slotIndex: 0, out string ownSave, out _)
            && PlayerIfo.TryExtractPlayer(ownSave, out _, out _);

        return JoinModeRule.Decide(
            mode,
            hasCoopCharacter,
            hasOwnCharacter,
            saveIdentityMatchesHost: false,
            autoAdoptEnabled,
            out reason);
    }

    private static async Task RunCoreAsync(
        SessionManager manager,
        IGameBridge bridge,
        string gameDirectory,
        JoinMode mode,
        bool autoAdoptEnabled,
        Action? loadOwnSave,
        Action<string> say,
        CancellationToken token)
    {
        if (string.IsNullOrEmpty(gameDirectory))
        {
            // Nothing sidecar- or chargen-shaped can work without the install directory; the
            // own-save load (which resolves the game its own way) is the one thing left.
            say("join: could not resolve the game directory; "
                + (loadOwnSave is null ? "load a save yourself instead." : "loading your own save instead."));
            loadOwnSave?.Invoke();
            return;
        }

        JoinAction action = Decide(gameDirectory, mode, autoAdoptEnabled, out string reason);
        say($"join: {reason}");

        switch (action)
        {
            case JoinAction.LoadOwnSave:
            case JoinAction.AdoptWithOwnCharacter:
                loadOwnSave?.Invoke();
                return;

            case JoinAction.Refuse:
                say("join: staying at the main menu.");
                return;

            case JoinAction.AdoptWithCoopCharacter:
                await AdoptWithCoopCharacterAsync(manager, bridge, gameDirectory, say, token).ConfigureAwait(false);
                return;

            default:
                await CreateCharacterThenAdoptAsync(manager, bridge, gameDirectory, say, token).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>Every join after the first: the sidecar's character, straight into the host's
    /// world.</summary>
    private static async Task AdoptWithCoopCharacterAsync(
        SessionManager manager,
        IGameBridge bridge,
        string gameDirectory,
        Action<string> say,
        CancellationToken token)
    {
        string savesDirectory = Path.Combine(gameDirectory, "saves");

        if (!ClientCharacterPersistence.TryReadSidecar(
            savesDirectory, out GffStruct player, out SidecarMetadata metadata, out string diagnostic))
        {
            say($"join: {diagnostic}");
            return;
        }

        await AdoptAndReconnectAsync(
            manager, bridge, gameDirectory, player,
            $"your co-op character (captured {metadata.CapturedAtUtc:u})",
            "co-op character", say, token).ConfigureAwait(false);
    }

    /// <summary>The first join (or a forced reroll): chargen, then the sidecar, then the
    /// host's world.</summary>
    private static async Task CreateCharacterThenAdoptAsync(
        SessionManager manager,
        IGameBridge bridge,
        string gameDirectory,
        Action<string> say,
        CancellationToken token)
    {
        if (!bridge.TryArmNewCharacterCapture(out string armed))
        {
            say($"new character: {armed} - load a save yourself instead.");
            return;
        }

        say($"new character: {armed}");

        // 1b. Press New Game for the player. Best-effort: the verb is refused (with the reason
        //     in kotorcoop-plugin.log) when the menu is still behind the logo movies or already
        //     tearing down, and in that case one click by the player costs nothing.
        bool pressed = bridge is KotorGameBridge kotor
            && kotor.TryDriveChargen(ChargenDriveKinds.NewGame, 0, out _);

        say(pressed
            ? "new character: create your character as usual. You will start the Endar Spire as "
                + "normal; a moment later you will be pulled into the host's world with the "
                + "character you just made."
            : "new character: at the main menu choose NEW GAME and create your character as usual. "
                + "You will start the Endar Spire as normal; a moment later you will be pulled into "
                + "the host's world with the character you just made.");

        // 2. Wait for the character. NO DEADLINE, deliberately: the player is choosing a face
        //    and spending skill points, and a timeout here would abandon them silently in the
        //    middle of it. Cancellation (the agent shutting down) is the only way out.
        //
        //    What arrives is a SAVE, not a file lifted out of Temp\. See
        //    KotorCoop.Plugin.NewCharacterCaptureController for why: retail chargen does not
        //    write TEMP:pifo (measured live, then disassembled - SaveCharGenCreature emits a
        //    .bic), so the character is obtained the only way KOTOR makes one portable.
        uint bytes = 0;
        string saveName = string.Empty;
        uint lastState = uint.MaxValue;

        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (bridge.TryGetNewCharacterCapture(out uint state, out uint captured, out string directory))
            {
                if (state != lastState)
                {
                    lastState = state;
                    if (state == CaptureStateSaving)
                    {
                        say("new character: your game is up - saving it so your character can "
                            + "travel; this takes a few seconds.");
                    }
                }

                if (state == CaptureStateCaptured)
                {
                    bytes = captured;
                    saveName = directory;
                    break;
                }

                if (state == CaptureStateFailed)
                {
                    say("new character: the plugin could not obtain the created character "
                        + "(see kotorcoop-plugin.log) - load a save yourself instead.");
                    return;
                }
            }

            await Task.Delay(CapturePollMs, token).ConfigureAwait(false);
        }

        if (saveName.Length == 0)
        {
            say("new character: the plugin reported a capture but named no save directory.");
            return;
        }

        say($"new character: your game was saved into '{saveName}' ({bytes:N0} bytes); "
            + "fetching the host's world.");

        // 3. Read the character out of that save. EXACTLY the extraction join_mode = save uses -
        //    <module>.sav -> module.ifo -> Mod_PlayerList - against a save this session just
        //    watched the engine write. Nothing new parses here.
        string saveDirectory = Path.Combine(gameDirectory, "saves", saveName);

        if (!PlayerIfo.TryExtractPlayer(saveDirectory, out GffStruct player, out string parsed))
        {
            say($"new character: {parsed}");
            return;
        }

        say($"new character: {parsed}");

        // 3b. This IS the co-op character from now on. Written before the adoption, not after,
        //     so a fetch that fails (host gone, wrong password on the re-handshake) or a player
        //     who quits before their first in-game save does not have to make it again.
        //     hostSaveIdentity 0 = not yet known; the post-adoption rewrite below fills it in.
        string savesDirectory = Path.Combine(gameDirectory, "saves");
        if (ClientCharacterPersistence.TryWriteSidecar(
            savesDirectory, player, hostSaveIdentity: 0, DateTime.UtcNow, out string written))
        {
            say($"new character: {written}");
        }
        else
        {
            say($"new character: could not keep the character for later joins - {written}");
        }

        // 4-5. Adopt, wait, reconnect.
        await AdoptAndReconnectAsync(
            manager, bridge, gameDirectory, player, "the character you just created",
            "new character", say, token,
            afterAdopt: adopted => ClientCharacterPersistence.TryWriteSidecar(
                savesDirectory, player, adopted.SaveIdentity, DateTime.UtcNow, out _)).ConfigureAwait(false);
    }

    /// <summary>Steps 4-5, shared by both paths: adopt the host's world carrying
    /// <paramref name="player"/>, wait for the local load, drop the diff shadows, reopen the
    /// socket.</summary>
    private static async Task AdoptAndReconnectAsync(
        SessionManager manager,
        IGameBridge bridge,
        string gameDirectory,
        GffStruct player,
        string characterSource,
        string voice,
        Action<string> say,
        CancellationToken token,
        Action<AdoptedWorld>? afterAdopt = null)
    {
        AdoptedWorld? adopted = await manager.AdoptWithNewCharacterAsync(
            bridge, gameDirectory, player, characterSource, token).ConfigureAwait(false);

        if (adopted is null)
        {
            say($"{voice}: could not adopt the host's world - the session is unchanged.");
            return;
        }

        afterAdopt?.Invoke(adopted.Value);

        say($"{voice}: adopting '{adopted.Value.Module}' "
            + $"({adopted.Value.Bytes:N0} bytes, identity 0x{adopted.Value.SaveIdentity:X8})");

        // Wait for the local load, then reopen the socket. Same split every other adoption
        // uses: the engine load is asynchronous and nothing here may block on it.
        DateTime deadline = DateTime.UtcNow + LoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (bridge.TryGetAdoptedWorldLoadStatus(out uint applied, out int status) && applied != 0)
            {
                if (status != 0)
                {
                    say($"{voice}: the plugin refused the load (failure {status}).");
                    return;
                }

                // Same order, same reason as the rejoin path: the world under the agent's diff
                // shadows has been replaced, so the first scan of the new session must be a
                // fresh silent baseline rather than a diff against whatever we were standing in
                // a moment ago. See IWorldStateBridge.ForgetWorldShadows.
                bridge.ForgetWorldShadows();

                manager.Reconnect();
                say($"{voice}: you are in '{adopted.Value.Module}' with {characterSource}; "
                    + "reconnecting to the host.");
                return;
            }

            await Task.Delay(CapturePollMs, token).ConfigureAwait(false);
        }

        say($"{voice}: the plugin accepted the load but no world appeared in time.");
    }

    /// <summary>Mirrors <c>SharedBlock.JoinChargenStateCaptured</c>. Duplicated rather than
    /// referenced because the plugin is a separate x86 NativeAOT assembly this one cannot see —
    /// the same reason <c>PluginMenuLoader</c> keeps its own copies of the block offsets.
    /// </summary>
    private const uint CaptureStateCaptured = 2;

    /// <summary>Mirrors <c>SharedBlock.JoinChargenStateFailed</c>.</summary>
    private const uint CaptureStateFailed = 3;

    /// <summary>Mirrors <c>SharedBlock.JoinChargenStateSaving</c> — the world is up and the
    /// engine has been asked to save. Announced because it is the one step in this flow that
    /// happens while the player is standing in a game and would otherwise look like nothing
    /// happening.</summary>
    private const uint CaptureStateSaving = 4;
}

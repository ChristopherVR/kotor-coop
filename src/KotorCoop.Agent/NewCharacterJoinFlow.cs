using KotorCoop.Memory;
using KotorCoop.Memory.GameFiles;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// coop.ini <c>join_mode = newcharacter</c>: a player who has never played this game joins a
/// co-op session by making a character in the game's OWN character-creation screens, and is
/// then dropped into the host's world with it.
///
/// WHY IT COULD NOT BE DONE BEFORE. World adoption (docs/research/world-adoption.md) is the
/// only mechanism that lets two installs share one world, and it carries a character lifted out
/// of the JOINER'S OWN SAVE. A first-time joiner has no save, so <c>WorldAdoption</c> refused —
/// correctly, and before touching anything ("the save's <c>Mod_PlayerList</c> is empty"). The
/// only advice that worked was "go play single-player for five minutes first", which is a
/// strange thing to tell someone who just wants to join their friend.
///
/// THE FIVE STEPS, and which of them are new:
/// <list type="number">
/// <item>ARM the plugin's capture (new — <c>NewCharacterCaptureController</c>).</item>
/// <item>The player uses the game's own New Game and character creation. Nothing of ours runs;
/// the engine writes the finished character to <c>Temp\pifo.ifo</c> exactly as it does for
/// every single-player game ever started, and the plugin copies it aside.</item>
/// <item>Read that copy back as a character (<c>PlayerIfo.TryExtractPlayerFromPifoBytes</c> —
/// existing; the sidecar path already round-trips the identical file shape).</item>
/// <item>Adopt the host's world carrying it (<c>SessionManager.AdoptWithNewCharacterAsync</c>
/// → the existing <c>WorldTransfer</c>/staging/verification chain, with the character handed in
/// rather than extracted).</item>
/// <item>Reconnect, so the session re-handshakes against a world that now fingerprints the same
/// as the host's — which is the whole point: the digests agree BY CONSTRUCTION and every
/// object-id-keyed channel comes back on.</item>
/// </list>
///
/// ⚠ THE PLAYER DOES CLICK "NEW GAME" THEMSELVES, and that is deliberate rather than a gap left
/// out of laziness. Pressing it from the plugin means calling
/// <c>CSWGuiMainMenu::OnNewGamePicked 0x0067AFB0</c>, which — unlike
/// <c>CSWGuiPartySelection::ConfirmPartySelection</c>, the precedent for calling a panel's own
/// handler with a null firing control — DEREFERENCES its stack argument
/// (<c>mov eax, [ecx+0x4C]</c> at 0x0067AFF0, disassembly-verified) and the offset of the
/// BTN_NEWGAME control inside <c>CSWGuiMainMenu</c> is not researched. Passing 0 would fault on
/// the main menu. Replaying the handler's body instead (it is only
/// <c>operator new(0x1560)</c> → <c>CSWGuiClassSelection::Constructor 0x006DC3C0</c> →
/// <c>AddPanel</c> → mark the menu for teardown) is entirely feasible and is the natural
/// follow-up; it was not done in this pass because the constructor's second argument is a
/// <c>CExoString</c> built through a resource-directory dance at 0x0067B01B..0x0067B0F2 whose
/// meaning is not yet pinned, and guessing it is exactly the class of mistake this codebase's
/// engine-write rules exist to prevent. One click by the player is a cheap price for not
/// guessing.
/// </summary>
internal static class NewCharacterJoinFlow
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
    public static async Task RunAsync(
        SessionManager manager,
        IGameBridge bridge,
        string gameDirectory,
        Action<string> say,
        CancellationToken token)
    {
        try
        {
            await RunCoreAsync(manager, bridge, gameDirectory, say, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A shutting-down agent must not leave the player mid-adoption with a message
            // implying something went wrong; it did not, it stopped.
        }
        catch (Exception ex)
        {
            say($"new character: {ex.Message}");
        }
    }

    private static async Task RunCoreAsync(
        SessionManager manager,
        IGameBridge bridge,
        string gameDirectory,
        Action<string> say,
        CancellationToken token)
    {
        if (string.IsNullOrEmpty(gameDirectory))
        {
            say("new character: could not resolve the game directory; load a save yourself instead.");
            return;
        }

        if (!bridge.TryArmNewCharacterCapture(out string armed))
        {
            say($"new character: {armed} - load a save yourself instead.");
            return;
        }

        say($"new character: {armed}");
        say("new character: at the main menu choose NEW GAME and create your character as usual. "
            + "When you finish, you will be taken into the host's world instead of the Endar Spire.");

        // 2. Wait for the character. NO DEADLINE, deliberately: the player is choosing a face
        //    and spending skill points, and a timeout here would abandon them silently in the
        //    middle of it. Cancellation (the agent shutting down) is the only way out.
        uint bytes = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();

            if (bridge.TryGetNewCharacterCapture(out uint state, out uint captured))
            {
                if (state == CaptureStateCaptured)
                {
                    bytes = captured;
                    break;
                }

                if (state == CaptureStateFailed)
                {
                    say("new character: the plugin could not capture the created character "
                        + "(see kotorcoop-plugin.log) - load a save yourself instead.");
                    return;
                }
            }

            await Task.Delay(CapturePollMs, token).ConfigureAwait(false);
        }

        say($"new character: captured your character ({bytes:N0} bytes); fetching the host's world.");

        // 3. Read it back. The stash is a pifo — byte-for-byte what PlayerIfo.BuildPifo emits —
        //    so the existing sidecar reader parses it with nothing added.
        string stash = Path.Combine(gameDirectory, "Temp", PlayerIfo.CarriedCharacterFile);
        byte[] pifo;
        try
        {
            pifo = await File.ReadAllBytesAsync(stash, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            say($"new character: could not read the captured character - {ex.Message}");
            return;
        }

        if (!PlayerIfo.TryExtractPlayerFromPifoBytes(pifo, out GffStruct player, out string parsed))
        {
            say($"new character: {parsed}");
            return;
        }

        say($"new character: {parsed}");

        // 4. Adopt.
        AdoptedWorld? adopted = await manager.AdoptWithNewCharacterAsync(
            bridge, gameDirectory, player, "the character you just created", token).ConfigureAwait(false);

        if (adopted is null)
        {
            say("new character: could not adopt the host's world - the session is unchanged and "
                + "your character is still in your own game.");
            return;
        }

        say($"new character: adopting '{adopted.Value.Module}' "
            + $"({adopted.Value.Bytes:N0} bytes, identity 0x{adopted.Value.SaveIdentity:X8})");

        // 5. Wait for the local load, then reopen the socket. Same split every other adoption
        //    uses: the engine load is asynchronous and nothing here may block on it.
        DateTime deadline = DateTime.UtcNow + LoadTimeout;
        while (DateTime.UtcNow < deadline)
        {
            token.ThrowIfCancellationRequested();

            if (bridge.TryGetAdoptedWorldLoadStatus(out uint applied, out int status) && applied != 0)
            {
                if (status != 0)
                {
                    say($"new character: the plugin refused the load (failure {status}).");
                    return;
                }

                manager.Reconnect();
                say($"new character: you are in '{adopted.Value.Module}' with the character you "
                    + "just made; reconnecting to the host.");
                return;
            }

            await Task.Delay(CapturePollMs, token).ConfigureAwait(false);
        }

        say("new character: the plugin accepted the load but no world appeared in time.");
    }

    /// <summary>Mirrors <c>SharedBlock.JoinChargenStateCaptured</c>. Duplicated rather than
    /// referenced because the plugin is a separate x86 NativeAOT assembly this one cannot see —
    /// the same reason <c>PluginMenuLoader</c> keeps its own copies of the block offsets.
    /// </summary>
    private const uint CaptureStateCaptured = 2;

    /// <summary>Mirrors <c>SharedBlock.JoinChargenStateFailed</c>.</summary>
    private const uint CaptureStateFailed = 3;
}

using System.Diagnostics;
using KotorCoop.Memory;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The `agent run` verb: attaches to a game, guards against a second agent attaching to the
/// same process, then wires and runs a live session (<see cref="AgentComposition"/> builds the
/// session; this drives it until Ctrl+C or the game exits).
/// </summary>
internal static class RunCommand
{
    public static async Task<int> RunAsync(AgentOptions options)
    {
        if (options.Mode == CoopMode.None)
        {
            if (!options.MenuConnect)
            {
                Console.Error.WriteLine("Specify --host or --join <host[:port]>, or --menu to connect from the game.");
                return 1;
            }
        }

        using ProcessMemory memory = CommandSupport.OpenTarget(options, writable: !options.ReadOnlyAttach);

        // ⚠ ONE AGENT PER GAME PROCESS, enforced rather than assumed.
        //
        // Two things start an agent — the plugin's AgentLauncher (coop.ini
        // auto_start_agent, on by default) and whatever the human ran — and until now
        // nothing stopped both from attaching to the same swkotor.exe. Both then find the
        // same shared block and write it, both join the session as separate players, and the
        // symptoms are indistinguishable from a protocol bug: a phantom of yourself, peer
        // counts that do not match the number of games open, and two writers racing over
        // OffsetPlayerCreature and the remote slots. This is how a third peer appeared in a
        // two-instance session.
        //
        // Keyed on the target pid, not on the agent, so running the agent by hand against a
        // second game is unaffected. Held for the process lifetime; abandoning it (a crash)
        // releases it, which is the behaviour we want.
        using Mutex attachment = new(initiallyOwned: false, $"Local\\kotorcoop-agent-{memory.ProcessId}");
        bool attached;
        try
        {
            attached = attachment.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            attached = true; // the previous agent died; the game is ours
        }

        if (!attached)
        {
            Console.Error.WriteLine(
                $"another KotorCoop agent is already attached to pid {memory.ProcessId}; "
                + "refusing to be a second one. Set auto_start_agent = 0 in coop.ini if you "
                + "want to drive the agent yourself.");
            return 3;
        }

        try
        {
            return await RunAttachedAsync(options, memory).ConfigureAwait(false);
        }
        finally
        {
            attachment.ReleaseMutex();
        }
    }

    private static async Task<int> RunAttachedAsync(AgentOptions options, ProcessMemory memory)
    {
        using IGameBridge bridge = CreateBridge(options, memory, out string diagnostic, out bool resolved);

        if (!resolved)
        {
            // --menu waits at the MAIN MENU, where by definition no module is loaded and there
            // is no player creature to find — so an unresolved bridge is the expected state, not
            // a failure. The bridge re-resolves lazily once a save is loaded. Any other mode
            // genuinely needs the player now.
            //
            // ⚠ EXCEPT coop.ini join_mode = newcharacter, which is the SAME situation reached a
            // different way: that joiner has no save, so they are at the main menu on purpose and
            // will be for as long as character creation takes. Refusing here is what made
            // `run --join` exit 2 before a first-time joiner could do anything at all — the exact
            // player the mode exists for. See NewCharacterJoinFlow.
            bool newCharacterJoin = options.Mode == CoopMode.Join
                && TryReadJoinMode(memory.ProcessId) == JoinMode.NewCharacter;

            if (!options.MenuConnect && !newCharacterJoin)
            {
                Console.Error.WriteLine($"could not locate player state: {diagnostic}");
                return 2;
            }

            Console.WriteLine($"waiting at the main menu ({diagnostic}); player state resolves when a game loads.");
        }
        else
        {
            Console.WriteLine(diagnostic);
        }

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        // ⚠ THE AGENT DIES WITH ITS GAME. Without this, closing the game leaves a hidden
        // zombie agent still connected to the session: the host keeps a live "peer" whose
        // player no longer exists — its phantom standing frozen in the world, "left the
        // game" never announced, and the slot held until the process is hunted down by
        // hand. Proven live: the joiner's game was killed and the host still reported
        // peers 1 three minutes later, because the auto-started agent was the peer. The
        // cancel tears the session down through the normal path, so the Bye goes out and
        // the host announces the leave within a tick.
        _ = Task.Run(async () =>
        {
            try
            {
                using Process game = Process.GetProcessById(memory.ProcessId);
                await game.WaitForExitAsync(cts.Token).ConfigureAwait(false);
                Console.WriteLine("the game exited; the agent shuts down with it.");
                cts.Cancel();
            }
            catch
            {
                // Already-gone or unwatchable target (mock profile): nothing to watch —
                // the agent then lives until Ctrl+C, exactly as before.
            }
        });

        AgentComposition.Result? composed = AgentComposition.Compose(options, bridge, memory, cts);
        if (composed is not { } session)
        {
            return 1;
        }

        await session.Dashboard.RunAsync(cts.Token).ConfigureAwait(false);
        await session.DispatchTask.ConfigureAwait(false);

        return 0;
    }

    /// <summary>
    /// Chooses between the KOTOR-specific bridge and a JSON offset profile.
    ///
    /// The KOTOR bridge exists because an offset profile cannot express what KOTOR needs:
    /// enumerate every creature by vtable, pick the one flagged is_pc as the local player,
    /// and drive a selection of the others as remote avatars. Those are three different
    /// objects at unrelated heap addresses, not fields at fixed offsets from one anchor.
    /// </summary>
    private static IGameBridge CreateBridge(
        AgentOptions options, ProcessMemory memory, out string diagnostic, out bool resolved)
    {
        if (options.UseKotorBridge)
        {
            KotorGameBridge kotor = new(
                memory, options.Phantoms, ownsMemory: false, options.EnemyCensusCap);
            resolved = kotor.TryResolve(out diagnostic);

            // Co-op means at most one of the two windows is focused, and KOTOR pauses the
            // moment it loses focus, so the unfocused player would freeze for everybody.
            if (resolved && kotor.PluginAttached && kotor.SetKeepAliveUnfocused(true))
            {
                diagnostic += "; unfocused-pause suppression on";
            }

            return kotor;
        }

        string profilePath = ProfileLocator.Resolve(options.Profile);
        GameProfile profile = GameProfile.Load(profilePath);
        Console.WriteLine($"profile: {profile.Name} ({profilePath})");

        ProfileGameBridge bridge = new(memory, profile, ownsMemory: false);
        resolved = bridge.TryResolveAnchor(out diagnostic);
        return bridge;
    }

    /// <summary>
    /// coop.ini's <c>join_mode</c> for the install this process was launched from, read here
    /// rather than taken from <see cref="AgentComposition"/> because the decision it feeds — may
    /// this agent run with no world yet? — happens strictly before composition. Any failure
    /// (no file, unreadable, no game directory) reads as the default, which is the mode that
    /// requires a world, so an unreadable coop.ini can never silently relax the guard.
    /// </summary>
    private static JoinMode TryReadJoinMode(int processId)
    {
        try
        {
            using Process game = Process.GetProcessById(processId);
            string? directory = Path.GetDirectoryName(game.MainModule?.FileName);
            if (directory is null)
            {
                return JoinMode.Save;
            }

            return CoopConfig.TryLoad(CoopConfig.PathFor(directory))?.JoinMode ?? JoinMode.Save;
        }
        catch (Exception)
        {
            return JoinMode.Save;
        }
    }
}

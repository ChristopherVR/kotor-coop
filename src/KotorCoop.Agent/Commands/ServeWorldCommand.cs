using KotorCoop.Memory;
using KotorCoop.Protocol;
using KotorCoop.Session;

namespace KotorCoop.Agent;

/// <summary>
/// The `agent serve-world` verb: serves this install's world to joiners — the save directory
/// itself, over TCP on the session's port number, until cancelled.
///
/// Standalone rather than folded into `run` on purpose, for now. Which world a host should
/// serve while it is STANDING IN ONE is an open question — the save on disk is by definition
/// older than the world being played — and a separate verb keeps that decision out of the
/// session's path until it is answered. See docs/research/world-adoption.md §6.
/// </summary>
internal static class ServeWorldCommand
{
    public static async Task<int> RunAsync(AgentOptions options)
    {
        if (!CommandSupport.TryResolveGameDirectory(options.GamePath, out string gameDirectory))
        {
            Console.Error.WriteLine($"game directory not found: {options.GamePath} (use --game-path <dir or exe>)");
            return 1;
        }

        int slot = options.LoadSave > 0 ? options.LoadSave - 1 : 0;
        if (!PluginMenuLoader.TryResolveSaveDirectory(gameDirectory, slot, out string saveDirectory, out string which))
        {
            Console.Error.WriteLine(which);
            return 1;
        }

        IReadOnlyList<WorldFile> world = WorldTransfer.ReadSaveDirectory(saveDirectory);
        long bytes = 0;
        foreach (WorldFile file in world)
        {
            bytes += file.Data.Length;
        }

        // The identity of what we are serving, so a joiner can prove that what it staged is
        // what we sent. 0 (unreadable save) is a valid answer: the joiner is told the check
        // did not run rather than being blocked by our filesystem's problem.
        uint identity = SaveDigest.TryComputeSaveIdentity(saveDirectory, out uint computed) ? computed : 0;

        Console.WriteLine($"serving '{which}' ({world.Count} file(s), {bytes:N0} bytes) on :{options.Port}");
        Console.WriteLine(identity != 0
            ? $"world identity 0x{identity:X8}"
            : "world identity unknown (the save could not be fingerprinted; joiners cannot self-check)");
        Console.WriteLine("press Ctrl+C to stop.");

        using CancellationTokenSource cts = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await WorldTransfer
            .ServeAsync(
                world, options.Port, SessionPassword.Hash(options.Password), identity, Console.WriteLine, cts.Token)
            .ConfigureAwait(false);

        return 0;
    }
}

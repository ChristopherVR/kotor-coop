using System.Diagnostics;
using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>
/// The `agent load` verb: drives an already-running instance from the main menu into a save.
/// Separate from launch so a caller can start several instances, move their windows apart, and
/// only then load: menu clicks go to whichever window is in front, so overlapping copies steal
/// each other's input.
/// </summary>
internal static class LoadCommand
{
    public static int Run(AgentOptions options)
    {
        if (options.LoadSave <= 0)
        {
            Console.Error.WriteLine("Specify which save with --load-save <n> (1 = top of the list).");
            return 1;
        }

        // Writable: the plugin-driven load route writes a request into the shared block.
        // Menu clicking (the fallback) never needed it, which is why this was read-only.
        using ProcessMemory memory = CommandSupport.OpenTarget(options, writable: true);
        using Process game = Process.GetProcessById(memory.ProcessId);

        bool loaded = SaveLoader.TryLoadSave(
            game, memory, options.LoadSave - 1, TimeSpan.FromSeconds(180), out string diagnostic,
            options.LoadViaMenuClicks);

        Console.WriteLine(diagnostic);
        return loaded ? 0 : 1;
    }
}

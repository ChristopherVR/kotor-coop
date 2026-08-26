using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>The `agent track` verb: the interactive live-value tracking console.</summary>
internal static class TrackCommand
{
    public static int Run(AgentOptions options)
    {
        using ProcessMemory memory = CommandSupport.OpenTarget(options, writable: false);
        return new TrackConsole(memory).Run();
    }
}

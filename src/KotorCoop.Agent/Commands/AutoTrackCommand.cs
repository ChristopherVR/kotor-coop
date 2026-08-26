using System.Diagnostics;
using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>The `agent autotrack` verb: the automatic pointer-chain discovery console.</summary>
internal static class AutoTrackCommand
{
    public static int Run(AgentOptions options)
    {
        using ProcessMemory memory = CommandSupport.OpenTarget(options, writable: false);

        using Process game = Process.GetProcessById(memory.ProcessId);
        return new AutoTrackConsole(memory, game).Run();
    }
}

using KotorCoop.Memory;

namespace KotorCoop.Agent;

/// <summary>The `agent scan` verb: the interactive memory-scan console.</summary>
internal static class ScanCommand
{
    public static int Run(AgentOptions options)
    {
        using ProcessMemory memory = CommandSupport.OpenTarget(options, writable: false);
        new ScanConsole(memory).Run();
        return 0;
    }
}
